using System.Buffers;
using System.Text.Json;
using QQT.Core;

namespace QQT.ClientAI;

public enum AiLevel { Trace, Debug, Information, Warning, Error, Critical }

/// <summary>实例唯一持有池数组；发送完成后才能 Reset，Dispose 后归还。</summary>
public sealed class MessageBuffer : IBufferWriter<byte>, IDisposable
{
    private byte[]? _bytes = ArrayPool<byte>.Shared.Rent(4096);
    private int _written;
    public Utf8JsonWriter Writer { get; }
    public MessageBuffer() => Writer = new(this);
    public ReadOnlyMemory<byte> Memory => _bytes.AsMemory(0, _written);
    public void Reset() { _written = 0; Writer.Reset(this); }
    public void Advance(int count)
    {
        if (count < 0 || _bytes is null || count > _bytes.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsMemory(_written); }
    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsSpan(_written); }
    private void Ensure(int hint)
    {
        ObjectDisposedException.ThrowIf(_bytes is null, this);
        if (hint < 0) throw new ArgumentOutOfRangeException(nameof(hint));
        int needed = Math.Max(hint, 256); if (needed <= _bytes.Length - _written) return;
        var next = ArrayPool<byte>.Shared.Rent(Math.Max(_written + needed, checked(_bytes.Length * 2)));
        _bytes.AsSpan(0, _written).CopyTo(next); ArrayPool<byte>.Shared.Return(_bytes); _bytes = next;
    }
    public void Dispose()
    {
        if (_bytes is null) return;
        try { Writer.Dispose(); }
        finally { var bytes = Interlocked.Exchange(ref _bytes, null); if (bytes is not null) ArrayPool<byte>.Shared.Return(bytes); }
    }
}

/// <summary>分类 JSONL 诊断。锁只保护同步写入；不把消息 JsonElement 或租用内存留给异步日志任务。</summary>
public sealed class AiLog : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly MessageBuffer _buffer = new();
    private readonly string _gameId;
    private readonly AiLevel _level, _transport, _search;
    public int PlayerId { get; set; }
    public AiLog(string path, string gameId, AiLevel level, AiLevel? transport = null, AiLevel? search = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _gameId = gameId; _level = level; _transport = transport ?? level; _search = search ?? level;
    }
    private bool Begin(string category, AiLevel level, string name, int round)
    {
        var minimum = category == "Transport" ? _transport : category == "Search" ? _search : _level;
        if (level < minimum) return false;
        _buffer.Reset(); var w = _buffer.Writer;
        w.WriteStartObject(); w.WriteString("timestamp", DateTimeOffset.UtcNow); w.WriteString("level", level.ToString());
        w.WriteString("category", category switch { "Transport" => "QQT.ClientAI.Transport", "Search" => "QQT.ClientAI.Search", "Query" => "QQT.ClientAI.Query", "Decision" => "QQT.ClientAI.Decision", _ => "QQT.ClientAI.Lifecycle" });
        w.WriteString("event", name); w.WriteString("gameId", _gameId);
        w.WriteNumber("playerId", PlayerId); w.WriteNumber("roundId", round); return true;
    }
    private void End()
    {
        _buffer.Writer.WriteEndObject(); _buffer.Writer.Flush(); _stream.Write(_buffer.Memory.Span); _stream.WriteByte(10); _stream.Flush();
    }
    public void Status(string name, int round = 0, JsonElement? data = null, string? detail = null, AiLevel level = AiLevel.Information)
    {
        lock (_gate)
        {
            if (!Begin("Lifecycle", level, name, round)) return;
            if (data is { } value) { _buffer.Writer.WritePropertyName("data"); value.WriteTo(_buffer.Writer); }
            if (detail is not null) _buffer.Writer.WriteString("detail", detail);
            End();
        }
    }
    public void Received(JsonElement message)
    {
        lock (_gate)
        {
            int round = message.TryGetProperty("roundId", out var r) && r.TryGetInt32(out int n) ? n : 0;
            if (!Begin("Transport", AiLevel.Debug, "received", round)) return;
            _buffer.Writer.WritePropertyName("message"); message.WriteTo(_buffer.Writer); End();
        }
    }
    public void Sent(ReadOnlySpan<byte> message, int round)
    {
        lock (_gate)
        {
            if (!Begin("Transport", AiLevel.Debug, "sent", round)) return;
            _buffer.Writer.WritePropertyName("message"); _buffer.Writer.WriteRawValue(message); End();
        }
    }
    public void Query(int round, string request, string purpose, JsonElement payload)
    {
        lock (_gate)
        {
            if (!Begin("Query", AiLevel.Information, "query", round)) return;
            var w = _buffer.Writer; w.WriteString("requestId", request); w.WriteString("purpose", purpose);
            w.WritePropertyName("result"); payload.WriteTo(w); End();
        }
    }
    public void Candidate(int round, Plan plan, double score, int depth, int crates, double attack, double uncertainty, bool counter)
    {
        lock (_gate)
        {
            if (!Begin("Search", AiLevel.Debug, "candidate", round)) return;
            var w = _buffer.Writer; w.WriteString("direction", DirectionName(plan.Direction)); w.WriteNumber("placementMask", plan.Placements);
            w.WriteNumber("score", score); w.WriteNumber("escapeDepth", depth); w.WriteNumber("additionalCrates", crates);
            w.WriteNumber("attackValue", attack); w.WriteNumber("uncertaintyPenalty", uncertainty); w.WriteBoolean("counterRisk", counter); End();
        }
    }
    public void Chosen(WorldMemory memory, in Decision decision, double elapsed)
    {
        lock (_gate)
        {
            if (!Begin("Decision", decision.EscapeDepth < decision.Horizon || decision.CounterRisk ? AiLevel.Warning : AiLevel.Information, "decision", memory.Round)) return;
            var w = _buffer.Writer; w.WritePropertyName("time"); memory.Time.Write(w); w.WriteNumber("actionPoints", memory.Budget);
            w.WriteString("direction", DirectionName(decision.Plan.Direction)); w.WriteNumber("placementMask", decision.Plan.Placements);
            w.WriteNumber("score", decision.Score); w.WriteNumber("escapeDepth", decision.EscapeDepth); w.WriteNumber("horizon", decision.Horizon);
            w.WriteNumber("candidates", decision.Candidates); w.WriteNumber("nodes", decision.Nodes); w.WriteNumber("predictedPlacements", decision.Placed);
            w.WriteNumber("additionalCrates", decision.Crates); w.WriteBoolean("counterRisk", decision.CounterRisk);
            w.WriteNumber("endpointX", decision.Endpoint.X); w.WriteNumber("endpointY", decision.Endpoint.Y); w.WriteNumber("elapsedMilliseconds", elapsed); End();
        }
    }
    public static string DirectionName(Direction direction) => direction switch { Direction.Up => "up", Direction.Down => "down", Direction.Left => "left", Direction.Right => "right", _ => "wait" };
    public void Dispose() { lock (_gate) { _buffer.Dispose(); _stream.Dispose(); } }
}
