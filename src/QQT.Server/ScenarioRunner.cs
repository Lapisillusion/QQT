using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;
using QQT.Core;

namespace QQT.Server;

/// <summary>唯一推进世界的任务。快照在锁内序列化，锁外只发送独立缓冲。</summary>
public sealed class ScenarioRunner : IAsyncDisposable
{
    private readonly GameSimulation _world = new();
    private readonly SimulationEvent[] _history;
    private readonly int _historyCapacity;
    private readonly ConcurrentDictionary<int, UiPeer> _peers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ScenarioOptions _options;
    private readonly SimulationLogger _log;
    private readonly ObjectPool<JsonMessageBuffer> _messages;
    private readonly CancellationToken _stopping;
    private Task? _runTask;
    private int _historyCount, _peerId, _totalTicks;
    private string _scenarioName = "M0 脚本验证";
    private bool _running;
    private string _runId = "ready", _status = "ready", _error = "";
    private ValidationResult? _result;

    public ScenarioRunner(ScenarioOptions options, SimulationLogger log, ObjectPool<JsonMessageBuffer> messages, IHostApplicationLifetime lifetime)
    {
        _options = options; _log = log; _messages = messages; _stopping = lifetime.ApplicationStopping;
        // 每轮事件上界由核心统一定义；按允许轮数预留，避免长脚本被短场景容量截断。
        // 本 runner 唯一持有历史数组，跨重跑复用，仅在任务和发送完成后的 DisposeAsync 归还。
        _historyCapacity = checked(options.MaxScriptTicks * GameSimulation.MaxEventsPerTick);
        _history = ArrayPool<SimulationEvent>.Shared.Rent(_historyCapacity);
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
            using var script = ScenarioScript.Load(_options.ScriptPath, _options.MaxScriptTicks);
            await _gate.WaitAsync(_stopping);
            try { _totalTicks = script.TotalTicks; _scenarioName = script.Name; }
            finally { _gate.Release(); }
            _log.Started(_runId, script.Name);
            await PublishAsync(false);
            long previousStart = 0;
            for (int tick = 1; tick <= script.TotalTicks; tick++)
            {
                if (paced && previousStart != 0)
                {
                    var remaining = TimeSpan.FromMilliseconds(_options.TickIntervalMilliseconds) - Stopwatch.GetElapsedTime(previousStart);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _stopping);
                }
                previousStart = Stopwatch.GetTimestamp();
                await _gate.WaitAsync(_stopping);
                try
                {
                    _log.TickStarted(_runId, tick);
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
            if (_historyCount >= _historyCapacity) throw new InvalidOperationException("脚本事件历史超出按允许轮数预留的容量。");
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

    private void WriteSnapshot(Utf8JsonWriter w, bool animate)
    {
        w.WriteStartObject();
        w.WriteString("type", "frame"); w.WriteString("runId", _runId); w.WriteString("status", _status);
        w.WriteString("error", _error); w.WriteString("name", _scenarioName); w.WriteNumber("totalTicks", _totalTicks); w.WriteNumber("tick", _world.Tick); w.WriteNumber("size", GameSimulation.Size);
        w.WriteNumber("durationMs", _options.AnimationMilliseconds); w.WriteBoolean("animate", animate);
        w.WriteBoolean("ended", _world.Ended);
        if (_world.WinnerId is { } winner) w.WriteNumber("winnerId", winner); else w.WriteNull("winnerId");
        if (_world.EndTick is { } endedAt) w.WriteNumber("endTick", endedAt); else w.WriteNull("endTick");
        w.WriteNumber("crateCount", _world.CrateCount); w.WriteNumber("bubbleCount", _world.BubbleCount);
        w.WriteStartArray("terrain");
        foreach (var cell in _world.Terrain) w.WriteNumberValue((int)cell);
        w.WriteEndArray();
        w.WriteStartArray("players");
        foreach (var p in _world.Players)
        {
            w.WriteStartObject(); w.WriteNumber("id", p.Id); WritePosition(w, "position", p.Position); w.WriteBoolean("hit", p.Hit); w.WriteBoolean("alive", p.Alive);
            if (p.DeathTick is { } diedAt) w.WriteNumber("deathTick", diedAt); else w.WriteNull("deathTick");
            w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("bubbles");
        foreach (var b in _world.Bubbles)
        {
            w.WriteStartObject(); w.WriteNumber("id", b.Id); w.WriteNumber("ownerId", b.OwnerId); WritePosition(w, "position", b.Position);
            w.WriteNumber("placedAt", b.PlacedAt); w.WriteNumber("dueAt", b.DueAt); w.WriteNumber("range", GameSimulation.ExplosionRange); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("moves");
        if (animate) foreach (var m in _world.Moves)
        {
            w.WriteStartObject(); w.WriteNumber("playerId", m.PlayerId); WritePosition(w, "from", m.From); WritePosition(w, "to", m.To); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("events");
        for (int i = 0; i < _historyCount; i++)
        {
            var e = _history[i]; w.WriteStartObject(); w.WriteNumber("sequence", i + 1); w.WriteString("kind", EventName(e.Kind));
            w.WriteNumber("tick", e.Tick); w.WriteNumber("playerId", e.PlayerId); w.WriteNumber("bubbleId", e.BubbleId);
            WritePosition(w, "position", e.Position); WritePosition(w, "from", e.From); w.WriteBoolean("success", e.Success);
            w.WriteNumber("value", e.Value); w.WriteString("reason", e.Reason switch { SkipReason.Dead => "dead", SkipReason.NotReached => "notReached", SkipReason.Capacity => "capacity", _ => "none" }); w.WriteEndObject();
        }
        w.WriteEndArray();
        if (_result is { } r)
        {
            w.WriteStartObject("result"); w.WriteBoolean("passed", r.Passed); w.WriteNumber("safePlayers", r.SafePlayers);
            w.WriteNumber("walls", r.Walls); w.WriteNumber("placed", r.Placed); w.WriteNumber("exploded", r.Exploded);
            w.WriteNumber("destroyed", r.Destroyed); w.WriteBoolean("mapMatches", r.MapMatches); w.WriteNumber("deadPlayers", r.DeadPlayers);
            w.WriteNumber("deaths", r.Deaths); w.WriteBoolean("playersMatch", r.PlayersMatch); w.WriteBoolean("bubblesMatch", r.BubblesMatch); w.WriteBoolean("actionsMatch", r.ActionsMatch); w.WriteBoolean("outcomeMatches", r.OutcomeMatches); w.WriteEndObject();
        }
        w.WriteEndObject(); w.Flush();
    }
    private static void WritePosition(Utf8JsonWriter w, string name, Position p)
    {
        w.WriteStartObject(name); w.WriteNumber("x", p.X); w.WriteNumber("y", p.Y); w.WriteEndObject();
    }
    private static string EventName(EventKind kind) => kind switch
    {
        EventKind.Move => "move", EventKind.Blocked => "blocked", EventKind.Placed => "placed",
        EventKind.Exploded => "exploded", EventKind.Covered => "covered", EventKind.CrateDestroyed => "crateDestroyed",
        EventKind.Chained => "chained", EventKind.MatchEnded => "matchEnded",
        EventKind.Safety => "safety", EventKind.Death => "death", EventKind.ActionIgnored => "actionIgnored", _ => "placementSkipped"
    };
    public async ValueTask DisposeAsync()
    {
        if (_runTask is { } task) await task;
        _world.Dispose(); ArrayPool<SimulationEvent>.Shared.Return(_history); _gate.Dispose();
    }
}
