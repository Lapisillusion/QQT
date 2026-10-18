using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using QQT.Core;

namespace QQT.ClientAI;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string server = "ws://127.0.0.1:5080/ws/ai", game = "", path = "artifacts/client-ai/client.jsonl";
        int seed = 1, nodes = 60000; AiLevel level = AiLevel.Information; AiLevel? transport = null, search = null;
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i]; if (i + 1 >= args.Length) throw new ArgumentException("参数缺少值：" + option);
            string value = args[++i];
            switch (option)
            {
                case "--server": server = value; break; case "--game-id": game = value; break; case "--log": path = value; break;
                case "--seed": seed = int.Parse(value); break; case "--nodes": nodes = int.Parse(value); break;
                case "--log-level": level = Enum.Parse<AiLevel>(value, true); break;
                case "--transport-log-level": transport = Enum.Parse<AiLevel>(value, true); break;
                case "--search-log-level": search = Enum.Parse<AiLevel>(value, true); break;
                default: throw new ArgumentException("不支持的参数：" + option);
            }
        }
        if (string.IsNullOrWhiteSpace(game) || nodes < 1000) throw new ArgumentException("必须提供 --game-id，--nodes 至少为 1000。");
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        using var log = new AiLog(path, game, level, transport, search);
        using var memory = new WorldMemory(); using var planner = new Planner(seed, nodes);
        await using var client = new ClientSession(new Uri(server), game, memory, planner, log);
        try { return await client.RunAsync(stopping.Token); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { log.Status("cancelled", memory.Round); return 130; }
        catch (Exception exception) { log.Status("failed", memory.Round, detail: exception.ToString(), level: AiLevel.Error); return 1; }
    }
}

/// <summary>接收任务只转交文档所有权；决策循环串行更新记忆，读取／发送完成后分别释放缓冲。</summary>
public sealed class ClientSession : IAsyncDisposable
{
    private readonly Uri _server;
    private readonly string _game;
    private readonly WorldMemory _memory;
    private readonly Planner _planner;
    private readonly AiLog _log;
    private readonly ClientWebSocket _socket = new();
    private readonly MessageBuffer _send = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<JsonDocument> _inbox = Channel.CreateBounded<JsonDocument>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private Task? _receiving;
    private CancellationTokenSource? _lifetime;
    private int _request;
    private bool _finished, _eliminated;
    public ClientSession(Uri server, string game, WorldMemory memory, Planner planner, AiLog log)
    { _server = server; _game = game; _memory = memory; _planner = planner; _log = log; }

    public async Task<int> RunAsync(CancellationToken token)
    {
        // 连接接收任务结束前保留关联取消源；不能在 RunAsync 返回时先解除取消关联。
        var linked = _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        await _socket.ConnectAsync(_server, linked.Token); _log.Status("connected", detail: _server.ToString());
        _receiving = ReceiveAsync(linked.Token);
        string registration = Id(); await SendAsync("register.request", 0, registration, default, null, 0, linked.Token);
        using (var reply = await ResultAsync("register.result", registration, linked.Token))
        {
            if (reply is null) return _finished ? 0 : 2;
            var payload = reply.RootElement.GetProperty("payload");
            if (!payload.GetProperty("success").GetBoolean()) throw new InvalidDataException("登记失败：" + payload.GetRawText());
            _log.PlayerId = payload.GetProperty("playerId").GetInt32(); _log.Status("registered", data: payload);
        }
        while (!_finished)
        {
            using var doc = await NextAsync(linked.Token); if (doc is null) break;
            var root = doc.RootElement; if (Notice(root)) continue;
            if (root.GetProperty("type").GetString() == "round.started" && !_eliminated) await RoundAsync(root, linked.Token);
            else throw new InvalidDataException("非预期消息：" + root.GetRawText());
        }
        if (!_finished) _log.Status("closed-without-result", _memory.Round, level: AiLevel.Error);
        return _finished ? 0 : 2;
    }

    private async Task RoundAsync(JsonElement root, CancellationToken token)
    {
        int round = root.GetProperty("roundId").GetInt32();
        if (round <= _memory.Round) throw new InvalidDataException("观察轮次没有前进。");
        _memory.Observe(round, root.GetProperty("payload")); long start = Stopwatch.GetTimestamp();
        while (_memory.NextQuery(out string targetType, out int target, out string purpose))
        {
            string id = Id(); await SendAsync("query.request", round, id, default, targetType, target, token);
            using var response = await ResultAsync("query.result", id, token); if (response is null || _eliminated) return;
            var payload = response.RootElement.GetProperty("payload"); _memory.QueryResult(payload); _log.Query(round, id, purpose, payload);
        }
        if (_finished || _eliminated) return;
        var decision = await Task.Run(() => _planner.Choose(_memory, _log, token), token);
        // 搜索期间接收任务仍工作；先处理已到的淘汰／终局，避免向结束的轮次提交。
        while (_inbox.Reader.TryRead(out var notification))
        {
            using (notification) if (!Notice(notification.RootElement)) throw new InvalidDataException("搜索期间收到非预期消息。");
        }
        if (_finished || _eliminated) return;
        string request = Id(); await SendAsync("action.submit", round, request, decision.Plan, null, 0, token);
        using var result = await ResultAsync("action.result", request, token); if (result is null) return;
        var accepted = result.RootElement.GetProperty("payload");
        if (!accepted.GetProperty("success").GetBoolean() || !accepted.GetProperty("locked").GetBoolean())
        {
            string code = accepted.GetProperty("error").GetProperty("code").GetString() ?? "";
            _log.Status("action-rejected", round, accepted, level: AiLevel.Warning);
            if (code != "invalidPlan") throw new InvalidDataException("动作拒绝：" + code);
            // 非法计划未锁定，只修正一次合法空计划；不无限重试查询或提交。
            decision = new(new(Direction.Wait, 0), 0, 0, decision.Horizon, decision.Candidates, decision.Nodes, 0, 0, true, _memory.Self.Position);
            request = Id(); await SendAsync("action.submit", round, request, decision.Plan, null, 0, token);
            using var fallback = await ResultAsync("action.result", request, token);
            if (fallback is null) return;
            if (!fallback.RootElement.GetProperty("payload").GetProperty("locked").GetBoolean()) throw new InvalidDataException("修正计划仍未锁定。");
            CheckAcknowledged(decision.Plan, fallback.RootElement.GetProperty("payload").GetProperty("plan"));
        }
        else CheckAcknowledged(decision.Plan, accepted.GetProperty("plan"));
        _planner.Commit(_memory, decision); _log.Chosen(_memory, decision, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    private void CheckAcknowledged(Plan expected, JsonElement actual)
    {
        if (actual.GetProperty("direction").GetString() != AiLog.DirectionName(expected.Direction)) throw new InvalidDataException("锁定方向与提交不一致。");
        uint seen = 0;
        foreach (var point in actual.GetProperty("placements").EnumerateArray())
        {
            var position = WorldMemory.ReadPosition(point); int distance = WorldMemory.Distance(position, _memory.Self.Position);
            if (distance > 20 || position != expected.At(_memory.Self.Position, distance) || (seen & (1U << distance)) != 0) throw new InvalidDataException("锁定放置列表与提交不一致。");
            seen |= 1U << distance;
        }
        if (seen != expected.Placements) throw new InvalidDataException("锁定放置列表与提交不一致。");
    }
    private string Id() => $"ai-{_log.PlayerId}-{++_request}";
    private async Task SendAsync(string type, int round, string id, Plan plan, string? targetType, int target, CancellationToken token)
    {
        _send.Reset(); var w = _send.Writer; w.WriteStartObject(); w.WriteString("type", type); w.WriteString("gameId", _game);
        if (type != "register.request") w.WriteNumber("roundId", round);
        w.WriteString("requestId", id); w.WriteStartObject("payload");
        if (type == "query.request") { w.WriteString("targetType", targetType); w.WriteNumber("targetId", target); }
        if (type == "action.submit")
        {
            w.WriteString("direction", AiLog.DirectionName(plan.Direction)); w.WriteStartArray("placements");
            for (int i = 0; i <= 20; i++) if ((plan.Placements & (1U << i)) != 0)
            {
                var p = plan.At(_memory.Self.Position, i); w.WriteStartObject(); w.WriteNumber("x", p.X); w.WriteNumber("y", p.Y); w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject(); w.WriteEndObject(); w.Flush();
        await _socket.SendAsync(_send.Memory, WebSocketMessageType.Text, true, token); _log.Sent(_send.Memory.Span, round);
    }
    private bool Notice(JsonElement root)
    {
        if (root.TryGetProperty("gameId", out var game) && game.GetString() != _game) throw new InvalidDataException("收到其他对局消息。");
        string type = root.GetProperty("type").GetString() ?? "";
        if (type == "participant.eliminated")
        {
            var data = root.GetProperty("payload"); int player = data.GetProperty("playerId").GetInt32();
            if (player == _log.PlayerId) _eliminated = true;
            _memory.Enemies[player].Alive = false; _log.Status("participant.eliminated", _memory.Round, data); return true;
        }
        if (type == "match.ended") { _finished = true; _log.Status("match.ended", _memory.Round, root.GetProperty("payload")); return true; }
        if (type == "error") throw new InvalidDataException("协议错误：" + root.GetRawText());
        return false;
    }
    private async Task<JsonDocument?> ResultAsync(string type, string request, CancellationToken token)
    {
        while (!_finished)
        {
            var doc = await NextAsync(token); if (doc is null) return null;
            try
            {
                if (!Notice(doc.RootElement) && doc.RootElement.GetProperty("type").GetString() == type && doc.RootElement.GetProperty("requestId").GetString() == request) return doc;
                if (!_finished && doc.RootElement.GetProperty("type").GetString() != "participant.eliminated") throw new InvalidDataException("回复关联不匹配。");
            }
            catch { doc.Dispose(); throw; }
            doc.Dispose();
        }
        return null;
    }
    private async Task<JsonDocument?> NextAsync(CancellationToken token)
    {
        try { return await _inbox.Reader.ReadAsync(token); }
        catch (ChannelClosedException) { return null; }
    }
    private async Task ReceiveAsync(CancellationToken token)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!token.IsCancellationRequested)
            {
                int length = 0; ValueWebSocketReceiveResult part;
                do
                {
                    if (length == bytes.Length)
                    {
                        if (length >= 4 * 1024 * 1024) throw new InvalidDataException("服务端消息超过 4 MiB。");
                        var next = ArrayPool<byte>.Shared.Rent(checked(bytes.Length * 2)); bytes.AsSpan(0, length).CopyTo(next);
                        ArrayPool<byte>.Shared.Return(bytes); bytes = next;
                    }
                    part = await _socket.ReceiveAsync(bytes.AsMemory(length), token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("服务端消息必须是 JSON 文本。");
                    length += part.Count;
                } while (!part.EndOfMessage);
                // Parse(ReadOnlyMemory) 会借用输入，接收数组下一条消息就会被改写。
                // ParseValue 让 JsonDocument 持有自己的解析存储，所有权直到决策循环 Dispose。
                var document = ParseOwned(bytes.AsSpan(0, length));
                try { _log.Received(document.RootElement); await _inbox.Writer.WriteAsync(document, token); }
                catch { document.Dispose(); throw; }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        { if (!token.IsCancellationRequested) _log.Status("receive-closed", _memory.Round, detail: exception.ToString(), level: AiLevel.Warning); }
        catch (Exception exception) { _log.Status("receive-failed", _memory.Round, detail: exception.ToString(), level: AiLevel.Error); }
        finally { ArrayPool<byte>.Shared.Return(bytes); _inbox.Writer.TryComplete(); }
    }
    internal static JsonDocument ParseOwned(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes); return JsonDocument.ParseValue(ref reader);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _socket.Abort(); if (_receiving is not null) await _receiving;
        while (_inbox.Reader.TryRead(out var doc)) doc.Dispose();
        _socket.Dispose(); _send.Dispose(); _lifetime?.Dispose(); _stop.Dispose();
    }
}
