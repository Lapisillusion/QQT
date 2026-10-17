using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.ObjectPool;
using QQT.Core;

namespace QQT.Server;

/// <summary>单一读取者拥有整个对局。网络任务只排队命令，不触碰模拟状态。</summary>
public sealed partial class BattleCoordinator : BackgroundService
{
    private readonly record struct Command(string Kind, BattlePeer? Peer, JsonDocument? Document, string GameId = "", int RoundId = 0);
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly GameSimulation _world = new() { TickLimit = 500 };
    private readonly BattlePeer?[] _ai = ArrayPool<BattlePeer?>.Shared.Rent(4);
    private readonly PlayerObservation?[] _observations = ArrayPool<PlayerObservation?>.Shared.Rent(4);
    private readonly Player[] _frozenPlayers = ArrayPool<Player>.Shared.Rent(4);
    private Bubble[] _frozenBubbles = ArrayPool<Bubble>.Shared.Rent(12);
    private readonly PlayerAction[] _plans = ArrayPool<PlayerAction>.Shared.Rent(4);
    private readonly Position[] _placements = ArrayPool<Position>.Shared.Rent(4 * GameSimulation.Size);
    private readonly bool[] _locked = ArrayPool<bool>.Shared.Rent(4);
    private readonly int[] _budgets = ArrayPool<int>.Shared.Rent(4);
    private readonly HashSet<BattlePeer> _uis = new();
    private SimulationEvent[] _history = ArrayPool<SimulationEvent>.Shared.Rent(128);
    private readonly ObjectPool<JsonMessageBuffer> _messages;
    private readonly ScenarioOptions _options;
    private readonly IConfiguration _configuration;
    private readonly SimulationLogger _simulationLog;
    private readonly ILogger _log;
    private int _historyCount, _eventCursor, _frozenBubbleCount, _pendingDisconnects, _seed;
    private string _gameId = "", _phase = "idle", _error = "";
    private bool _manual, _simulationScheduled;
    private int _disposed;
    private long _previousStart;
    private StreamWriter? _record;
    // 仅程序集内的确定性验收使用。生产环境为空，不增加网络命令或游戏等待规则。
    internal Func<ValueTask>? BeforeEvent { get; set; }
    internal Action<int>? DisconnectQueued { get; set; }

    public BattleCoordinator(ObjectPool<JsonMessageBuffer> messages, ScenarioOptions options, IConfiguration configuration, SimulationLogger simulationLog, ILoggerFactory logs)
    {
        _messages = messages; _options = options; _configuration = configuration; _simulationLog = simulationLog;
        _log = logs.CreateLogger("QQT.Server.Battle");
        _ai.AsSpan(0, 4).Clear(); _observations.AsSpan(0, 4).Clear();
        _plans.AsSpan(0, 4).Clear(); _locked.AsSpan(0, 4).Clear(); _budgets.AsSpan(0, 4).Clear();
    }

    public void Attach(BattlePeer peer) => _commands.Writer.TryWrite(new("attach", peer, null));
    public void Closed(BattlePeer peer)
    {
        _commands.Writer.TryWrite(new("closed", peer, null));
        if (!peer.IsUi) DisconnectQueued?.Invoke(peer.PlayerId);
    }
    // Document 所有权从接收任务转移至状态机，处理后统一 Dispose。
    public void Receive(BattlePeer peer, JsonDocument document)
    {
        if (!_commands.Writer.TryWrite(new("message", peer, document))) document.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StoppingToken = stoppingToken;
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    switch (command.Kind)
                    {
                        case "attach": if (command.Peer!.IsUi) { _uis.Add(command.Peer); SendFrame(command.Peer, false); SendState(command.Peer); } break;
                        case "closed": OnClosed(command.Peer!); break;
                        case "message": HandleMessage(command.Peer!, command.Document!.RootElement); break;
                        case "simulate":
                            if (command.GameId == _gameId && command.RoundId == _world.Tick + 1 && _phase == "collecting" && AllLocked()) StartSimulation();
                            break;
                        case "event":
                            if (command.GameId == _gameId && _phase == "simulating")
                            {
                                if (BeforeEvent is { } before) await before();
                                AdvanceEvent();
                            }
                            break;
                    }
                    if (command.Kind == "event") await Task.Yield();
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidDataException or FormatException or OverflowException or ArgumentException || command.Kind == "message" && exception is InvalidOperationException)
                {
                    if (command.Peer is { } peer) SendFailure(peer, command.Document?.RootElement, "invalidRequest", exception.Message);
                    else Abort(exception);
                }
                catch (Exception exception) { Abort(exception); }
                finally { command.Document?.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _commands.Writer.TryComplete();
            while (_commands.Reader.TryRead(out var command)) command.Document?.Dispose();
            _record?.Dispose(); ReleaseRound();
            for (int i = 0; i < 4; i++) _ai[i]?.Stop();
            foreach (var peer in _uis) peer.Stop();
        }
    }

    private void HandleMessage(BattlePeer peer, JsonElement root)
    {
        string type = root.GetProperty("type").GetString() ?? "";
        if (peer.IsUi)
        {
            if (type != "match.create") Record(root, "uiCommand");
            if (type == "match.create") { Create(root.GetProperty("payload")); PublishFrame(false); PublishState(); return; }
            if (root.GetProperty("gameId").GetString() != _gameId) { SendFailure(peer, root, "wrongGame", "对局编号不匹配。"); return; }
            if (type == "match.abort") { _phase = "aborted"; _error = "验证未完成，已停止推进。"; PublishFrame(false); PublishState(); return; }
            if (type == "start")
            {
                if (_phase != "preparing" || RegisteredCount() != _world.PlayerCount) { SendFailure(peer, root, "notReady", "尚未满员或已经开局。"); return; }
                _phase = _manual ? "awaitingTick" : "collecting";
                if (!_manual) OpenRound();
                PublishFrame(false); PublishState(); return;
            }
            if (type == "tick.demonstrate")
            {
                if (!_manual || _phase != "awaitingTick" || root.GetProperty("roundId").GetInt32() != _world.Tick + 1)
                { SendFailure(peer, root, "wrongStage", "本轮不能演示或命令已过期。"); return; }
                OpenRound(); PublishState(); return;
            }
            throw new InvalidDataException("未知 UI 命令。");
        }
        if (!root.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(requestId.GetString())) throw new InvalidDataException("缺少字符串 requestId。");
        if (type == "register.request") { Register(peer, root); return; }
        Record(root, "request", peer.PlayerId);
        if (type == "query.request") { Query(peer, root); return; }
        if (type == "action.submit") { Submit(peer, root); return; }
        SendFailure(peer, root, "unknownType", "未知 AI 命令。");
    }

    private void Create(JsonElement payload)
    {
        if (_phase is not ("idle" or "ended" or "aborted")) throw new InvalidDataException("请结束当前对局后再创建。");
        int count = payload.TryGetProperty("playerCount", out var n) ? n.GetInt32() : _configuration.GetValue("Match:PlayerCount", 4);
        if (count is < 2 or > 4) throw new InvalidDataException("playerCount 必须是 2、3 或 4。");
        bool manual = payload.TryGetProperty("manualTickAdvance", out var m) ? m.GetBoolean() : _configuration.GetValue("Match:ManualTickAdvance", false);
        int seed = payload.TryGetProperty("seed", out var s) ? s.GetInt32() : Random.Shared.Next();
        // 在临时世界验证配置，失败时不破坏上一局可审阅的结果。
        using (var candidate = new GameSimulation())
        {
            candidate.Reset(count, seed);
            if (payload.TryGetProperty("initial", out var initial)) candidate.Configure(initial);
            candidate.ConfigureItems(payload.TryGetProperty("items", out var items) ? items : null);
        }
        ReleaseRound();
        for (int i = 0; i < 4; i++) { _ai[i]?.Stop(); _ai[i] = null; }
        _record?.Dispose(); _record = null;
        _world.Reset(count, seed); _world.TickLimit = 500;
        if (payload.TryGetProperty("initial", out var environment)) _world.Configure(environment);
        _world.ConfigureItems(payload.TryGetProperty("items", out var itemConfig) ? itemConfig : null);
        _manual = manual; _seed = seed; _gameId = Guid.NewGuid().ToString("N"); _phase = "preparing"; _error = "";
        _previousStart = 0; _pendingDisconnects = _historyCount = _eventCursor = 0; _simulationScheduled = false;
        string directory = _configuration["Battle:RecordDirectory"] ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "battle");
        Directory.CreateDirectory(directory);
        _record = new StreamWriter(Path.Combine(directory, _gameId + ".jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
        Record(payload, "initialConfiguration"); RecordConfiguration(); StoreEvents();
        LogCreated(_log, _gameId, count, manual, seed);
    }

    private void Register(BattlePeer peer, JsonElement root)
    {
        string? error = null;
        if (_phase != "preparing") error = "notPreparing";
        else if (root.GetProperty("gameId").GetString() != _gameId) error = "wrongGame";
        else if (peer.PlayerId != 0) error = "alreadyRegistered";
        else
        {
            int index = 0; while (index < _world.PlayerCount && _ai[index] is not null) index++;
            if (index == _world.PlayerCount) error = "roomFull";
            else { _ai[index] = peer; peer.PlayerId = index + 1; peer.GameId = _gameId; }
        }
        Record(root, "request", peer.PlayerId);
        var message = Begin("register.result", root);
        var w = message.Writer; w.WriteBoolean("success", error is null);
        if (error is null) { w.WriteNumber("playerId", peer.PlayerId); w.WriteNumber("playerCount", _world.PlayerCount); }
        WriteError(w, error, error is null ? null : "登记失败。"); End(peer, message);
        if (error is null) { LogRegistered(_log, _gameId, peer.PlayerId); PublishState(); }
    }

    private int RegisteredCount() { int count = 0; for (int i = 0; i < _world.PlayerCount; i++) if (_ai[i] is not null) count++; return count; }

    private void OpenRound()
    {
        ReleaseRound(); _world.PrepareRound(); _eventCursor = 0; StoreEvents();
        _world.Players.CopyTo(_frozenPlayers); _frozenBubbleCount = _world.BubbleCount;
        if (_frozenBubbles.Length < _frozenBubbleCount)
        {
            ArrayPool<Bubble>.Shared.Return(_frozenBubbles); _frozenBubbles = ArrayPool<Bubble>.Shared.Rent(_frozenBubbleCount);
        }
        _world.Bubbles.CopyTo(_frozenBubbles);
        _phase = "collecting"; _simulationScheduled = false;
        for (int i = 0; i < _world.PlayerCount; i++)
        {
            _budgets[i] = 10; _locked[i] = false; _plans[i] = new(i + 1, Direction.Wait, null);
            if (!_world.Players[i].Alive) continue;
            _observations[i] = _world.Observe(i + 1);
            if (_ai[i] is { } peer) SendObservation(peer, _observations[i]!);
        }
        LogRound(_log, _gameId, _world.Tick + 1, _world.Time);
    }

    private bool AllLocked()
    {
        for (int i = 0; i < _world.PlayerCount; i++) if (_world.Players[i].Alive && !_locked[i]) return false;
        return true;
    }

    private void ScheduleSimulation()
    {
        if (_phase != "collecting" || _simulationScheduled || !AllLocked()) return;
        _simulationScheduled = true;
        var remaining = _previousStart == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_options.TickIntervalMilliseconds) - Stopwatch.GetElapsedTime(_previousStart);
        if (remaining <= TimeSpan.Zero) _commands.Writer.TryWrite(new("simulate", null, null, _gameId, _world.Tick + 1));
        else _ = WakeAsync(_gameId, _world.Tick + 1, remaining, StoppingToken);
    }
    private CancellationToken StoppingToken { get; set; }
    private async Task WakeAsync(string gameId, int round, TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token); _commands.Writer.TryWrite(new("simulate", null, null, gameId, round)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void StartSimulation()
    {
        _previousStart = Stopwatch.GetTimestamp(); _phase = "simulating";
        _simulationLog.TickStarted(_gameId, _world.Tick + 1);
        _world.BeginTick(_plans.AsSpan(0, _world.PlayerCount)); PublishState();
        _commands.Writer.TryWrite(new("event", null, null, _gameId));
    }
    private void AdvanceEvent()
    {
        _world.AdvanceEvent(_pendingDisconnects); _pendingDisconnects = 0; StoreEvents();
        if (_world.IsExecuting) { _commands.Writer.TryWrite(new("event", null, null, _gameId)); return; }
        _simulationLog.TickEnded(_gameId, _world);
        if (_world.Ended) { EndMatch(); PublishFrame(true); PublishState(); return; }
        _phase = _manual ? "awaitingTick" : "collecting";
        PublishFrame(true);
        if (!_manual) OpenRound(); else ReleaseRound();
        PublishState();
    }

    private void OnClosed(BattlePeer peer)
    {
        if (peer.IsUi) { _uis.Remove(peer); return; }
        int index = peer.PlayerId - 1;
        if (index < 0 || peer.GameId != _gameId || !ReferenceEquals(_ai[index], peer)) return;
        _ai[index] = null;
        if (_phase == "preparing") { PublishState(); return; }
        if (_phase is "ended" or "aborted" or "idle" || !_world.Players[index].Alive) return;
        LogDisconnected(_log, _gameId, peer.PlayerId, _phase, _world.Time);
        if (_phase == "simulating") { _pendingDisconnects |= 1 << index; return; }
        _world.Disconnect(1 << index); StoreEvents();
        if (_world.Ended) EndMatch(); else ScheduleSimulation();
        PublishFrame(false); PublishState();
    }

    private void StoreEvents()
    {
        var events = _world.Events;
        for (; _eventCursor < events.Length; _eventCursor++)
        {
            var e = events[_eventCursor];
            if (_historyCount == _history.Length)
            {
                var next = ArrayPool<SimulationEvent>.Shared.Rent(_history.Length * 2);
                _history.AsSpan(0, _historyCount).CopyTo(next); ArrayPool<SimulationEvent>.Shared.Return(_history); _history = next;
            }
            _history[_historyCount++] = e; _simulationLog.Write(_gameId, e);
            var eventMessage = _messages.Get();
            try
            {
                WorldFrame.WriteEvent(eventMessage.Writer, e, _historyCount); eventMessage.Writer.Flush();
                Record(eventMessage.Written, "event", e.PlayerId);
            }
            finally { _messages.Return(eventMessage); }
            if (e.Kind == EventKind.Death)
            {
                for (int i = 0; i < _world.PlayerCount; i++) if (_ai[i] is { } peer)
                {
                    var message = Begin("participant.eliminated"); var w = message.Writer;
                    w.WriteNumber("playerId", e.PlayerId); WorldFrame.WriteTime(w, "time", e.Tick);
                    w.WriteString("reason", HasDisconnectEvent(e.PlayerId, e.Tick) ? "disconnected" : "explosion"); End(peer, message);
                }
            }
        }
    }
    private bool HasDisconnectEvent(int id, RationalTime time)
    {
        foreach (var e in _world.Events) if (e.Kind == EventKind.Disconnected && e.PlayerId == id && e.Tick == time) return true;
        return false;
    }
    private void EndMatch()
    {
        _phase = "ended";
        int survivors = 0; foreach (var p in _world.Players) if (p.Alive) survivors++;
        for (int i = 0; i < _world.PlayerCount; i++) if (_ai[i] is { } peer)
        {
            var message = Begin("match.ended"); var w = message.Writer;
            if (_world.WinnerId is { } winner) w.WriteNumber("winnerId", winner); else w.WriteNull("winnerId");
            WorldFrame.WriteTime(w, "time", _world.Time); w.WriteString("reason", _world.Tick >= 500 && survivors > 1 ? "tickLimit" : "elimination"); End(peer, message);
        }
        for (int i = 0; i < _world.PlayerCount; i++) _ai[i]?.Complete();
        ReleaseRound();
        LogEnded(_log, _gameId, _world.Time, _world.WinnerId);
    }
    private void Abort(Exception exception)
    {
        _phase = "aborted"; _error = exception.Message;
        LogAborted(_log, exception, _gameId, _world.Tick);
        _record?.Dispose(); _record = null;
        PublishFrame(false); PublishState();
    }
    private void ReleaseRound()
    {
        for (int i = 0; i < 4; i++) { _observations[i]?.Dispose(); _observations[i] = null; }
        _plans.AsSpan(0, 4).Clear();
    }

    private void PublishFrame(bool animate) { foreach (var peer in _uis) SendFrame(peer, animate); }
    private void SendFrame(BattlePeer peer, bool animate)
    {
        var message = _messages.Get();
        try
        {
            WorldFrame.Write(message.Writer, _world, _history.AsSpan(0, _historyCount), _historyCount, _gameId, _phase is "ended" ? "ended" : _phase is "aborted" ? "error" : "running", _error, "联机对战", 500, null, _options.AnimationMilliseconds, animate);
            Record(message.Written, "uiFrame");
        }
        catch { _messages.Return(message); throw; }
        peer.Send(message);
    }
    private void PublishState() { foreach (var peer in _uis) SendState(peer); }
    private void SendState(BattlePeer peer)
    {
        var message = Begin("session.state"); var w = message.Writer;
        w.WriteString("phase", _phase); w.WriteNumber("playerCount", _world.PlayerCount); w.WriteNumber("registeredCount", RegisteredCount());
        w.WriteBoolean("manualTickAdvance", _manual); w.WriteNumber("seed", _seed); w.WriteNumber("nextRoundId", _world.Tick + 1);
        w.WriteBoolean("canStart", _phase == "preparing" && RegisteredCount() == _world.PlayerCount);
        w.WriteBoolean("canDemonstrate", _manual && _phase == "awaitingTick");
        w.WriteStartArray("waitingPlayerIds");
        for (int i = 0; i < _world.PlayerCount; i++) if (_phase == "collecting" && _world.Players[i].Alive && !_locked[i]) w.WriteNumberValue(i + 1);
        w.WriteEndArray(); End(peer, message);
    }
    private void RecordConfiguration()
    {
        var message = _messages.Get();
        try
        {
            var w = message.Writer; w.WriteStartObject(); w.WriteNumber("playerCount", _world.PlayerCount);
            w.WriteBoolean("manualTickAdvance", _manual); w.WriteNumber("seed", _seed); w.WriteStartArray("spawns");
            foreach (var p in _world.Players) { w.WriteStartObject(); w.WriteNumber("playerId", p.Id); WorldFrame.WritePosition(w, "position", p.Position); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteEndObject(); w.Flush(); Record(message.Written, "configuration");
        }
        finally { _messages.Return(message); }
    }
    private void Record(JsonElement root, string kind = "configuration", int playerId = 0)
    {
        if (_record is null) return;
        var message = _messages.Get();
        try
        {
            var w = message.Writer; WriteRecordHeader(w, kind, playerId); w.WritePropertyName("message"); root.WriteTo(w);
            w.WriteEndObject(); w.Flush(); _record.WriteLine(Encoding.UTF8.GetString(message.Written.Span));
        }
        finally { _messages.Return(message); }
    }
    private void Record(ReadOnlyMemory<byte> bytes, string kind = "outgoing", int playerId = 0)
    {
        if (_record is null) return;
        var message = _messages.Get();
        try
        {
            var w = message.Writer; WriteRecordHeader(w, kind, playerId); w.WritePropertyName("message"); w.WriteRawValue(bytes.Span, true);
            w.WriteEndObject(); w.Flush(); _record.WriteLine(Encoding.UTF8.GetString(message.Written.Span));
        }
        finally { _messages.Return(message); }
    }
    private void WriteRecordHeader(Utf8JsonWriter w, string kind, int playerId)
    {
        w.WriteStartObject(); w.WriteString("recordType", kind); w.WriteString("gameId", _gameId); w.WriteNumber("playerId", playerId);
        WorldFrame.WriteTime(w, "time", _world.Time);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        base.Dispose(); _world.Dispose();
        ArrayPool<BattlePeer?>.Shared.Return(_ai, true); ArrayPool<PlayerObservation?>.Shared.Return(_observations, true);
        ArrayPool<Player>.Shared.Return(_frozenPlayers); ArrayPool<Bubble>.Shared.Return(_frozenBubbles);
        ArrayPool<PlayerAction>.Shared.Return(_plans, true); ArrayPool<Position>.Shared.Return(_placements);
        ArrayPool<bool>.Shared.Return(_locked); ArrayPool<int>.Shared.Return(_budgets); ArrayPool<SimulationEvent>.Shared.Return(_history);
    }
}
