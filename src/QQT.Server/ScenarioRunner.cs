using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;
using QQT.Core;

namespace QQT.Server;

/// <summary>唯一推进世界的任务。快照在锁内序列化，锁外只发送独立缓冲。</summary>
public sealed partial class ScenarioRunner : IAsyncDisposable
{
    private readonly GameSimulation _world;
    private SimulationEvent[] _history;
    private readonly ConcurrentDictionary<int, UiPeer> _peers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ServerConfiguration _configuration;
    private ServerSettings _settings;
    private ScenarioOptions Options => _settings.Scenario;
    private readonly SimulationLogger _log;
    private readonly ObjectPool<JsonMessageBuffer> _messages;
    private readonly CancellationToken _stopping;
    private Task? _runTask;
    private int _historyCount, _peerId, _totalTicks;
    private string _scenarioName = "M0 脚本验证";
    private bool _running;
    private string _runId = "ready", _status = "ready", _error = "";
    private ValidationResult? _result;

    public ScenarioRunner(SimulationLogger log, ObjectPool<JsonMessageBuffer> messages, IHostApplicationLifetime lifetime, ServerConfiguration configuration)
    {
        _configuration = configuration; _settings = configuration.Read(); _log = log; _messages = messages; _stopping = lifetime.ApplicationStopping;
        _world = new(_settings.Player, Options.InitialEventCapacity, _settings.PlayerCount, _settings.MaxTicks);
        // 事件量取决于速度与连锁，按实际需要扩容；初始容量不作为事件上限。
        // 本 runner 唯一持有历史数组，跨重跑复用，仅在任务和发送完成后的 DisposeAsync 归还。
        _history = ArrayPool<SimulationEvent>.Shared.Rent(Options.InitialEventCapacity);
    }

    public async Task<bool> StartAsync(bool paced = true)
    {
        await _gate.WaitAsync(_stopping);
        try
        {
            if (_running) return false;
            _world.Reset(); _historyCount = 0; _result = null; _error = ""; _totalTicks = 0;
            _runId = Guid.NewGuid().ToString("N"); _status = "running"; _running = true;
            // RunAsync 的首次 gate 等待保证初始化和任务交接完成后再推进。
            _runTask = RunAsync(paced);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> VerifyAsync()
    {
        await StartAsync(false);
        if (_runTask is { } task) await task;
        return _result?.Passed == true;
    }

    public async Task<int> AddPeerAsync(UiPeer peer, CancellationToken token)
    {
        int id = Interlocked.Increment(ref _peerId);
        // 订阅与初始快照在同一锁内，避免快照晚于第一条增量发送而倒退。
        await _gate.WaitAsync(token);
        var message = _messages.Get();
        try
        {
            WriteSnapshot(message.Writer, false);
            await peer.SendAsync(message.Written, token);
            _peers[id] = peer;
        }
        finally { _messages.Return(message); _gate.Release(); }
        return id;
    }
    public void RemovePeer(int id) => _peers.TryRemove(id, out _);

    private async Task RunAsync(bool paced)
    {
        try
        {
            var settings = _configuration.Read();
            _settings.Dispose(); _settings = settings;
            using var script = ScenarioScript.Load(Options.ScriptPath, Options.MaxScriptTicks, settings.PlayerCount);
            await _gate.WaitAsync(_stopping);
            try { settings.ConfigureWorld(_world, script.PlayerCount, settings.Seed, script.Initial ?? settings.Initial, script.Items ?? settings.Items); _totalTicks = script.TotalTicks; _scenarioName = script.Name; StoreAndLogEvents(); }
            finally { _gate.Release(); }
            _log.Started(_runId, script.Name);
            _log.ItemsConfigured(_runId, _world);
            await PublishAsync(false);
            long previousStart = 0;
            for (int tick = 1; tick <= script.TotalTicks; tick++)
            {
                if (paced && previousStart != 0)
                {
                    var remaining = TimeSpan.FromMilliseconds(Options.TickIntervalMilliseconds) - Stopwatch.GetElapsedTime(previousStart);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _stopping);
                }
                previousStart = Stopwatch.GetTimestamp();
                await _gate.WaitAsync(_stopping);
                try
                {
                    _log.TickStarted(_runId, tick);
                    _world.PrepareRound();
                    _world.ValidateObservations(script.Expected);
                    _world.Advance(script.Round(tick));
                    StoreAndLogEvents();
                    _log.TickEnded(_runId, _world);
                    if (_world.Ended || tick == script.TotalTicks)
                    {
                        _result = _world.Validate(script.Expected, _history.AsSpan(0, _historyCount));
                        _status = _result.Value.Passed ? "passed" : "failed";
                        _log.Validated(_runId, _world, _result.Value);
                    }
                }
                finally { _gate.Release(); }
                await PublishAsync(true);
                if (_world.Ended) break;
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await _gate.WaitAsync(CancellationToken.None);
            try { _status = "error"; _error = exception.Message; _log.Error(_runId, exception); }
            finally { _gate.Release(); }
            if (!_stopping.IsCancellationRequested) await PublishAsync(false);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try { _running = false; }
            finally { _gate.Release(); }
        }
    }

    private void StoreAndLogEvents()
    {
        foreach (var item in _world.Events)
        {
            if (_historyCount == _history.Length)
            {
                var next = ArrayPool<SimulationEvent>.Shared.Rent(checked(_history.Length * 2));
                _history.AsSpan(0, _historyCount).CopyTo(next); ArrayPool<SimulationEvent>.Shared.Return(_history); _history = next;
            }
            _history[_historyCount++] = item;
            _log.Write(_runId, item);
        }
    }

    private async Task PublishAsync(bool animate)
    {
        var message = _messages.Get();
        try
        {
            await _gate.WaitAsync(_stopping);
            try { WriteSnapshot(message.Writer, animate); }
            finally { _gate.Release(); }
            foreach (var entry in _peers)
            {
                try { await entry.Value.SendAsync(message.Written, _stopping); }
                catch (WebSocketException) { _peers.TryRemove(entry.Key, out _); }
                catch (ObjectDisposedException) { _peers.TryRemove(entry.Key, out _); }
            }
        }
        // 此处所有发送均已完成，复用 writer 和数组不会污染在途消息。
        finally { _messages.Return(message); }
    }

    private void WriteSnapshot(Utf8JsonWriter w, bool animate) => WorldFrame.Write(w, _world, _history.AsSpan(0, _historyCount), _historyCount,
        _runId, _status, _error, _scenarioName, _totalTicks, _result, Options.AnimationMilliseconds, animate, _settings.CatchUpThreshold, _settings.PlaybackSpeed);
    public async ValueTask DisposeAsync()
    {
        if (_runTask is { } task) await task;
        _world.Dispose(); _settings.Dispose(); ArrayPool<SimulationEvent>.Shared.Return(_history); _gate.Dispose();
    }
}
