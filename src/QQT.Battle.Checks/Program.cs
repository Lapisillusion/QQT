using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using QQT.Server;

namespace QQT.Battle.Checks;

/// <summary>真实网络断线在确定屏障内注入，避免用 sleep 猜测同刻结算先后。</summary>
public static class Program
{
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    public static async Task Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("参数：仓库目录、证据输出目录。");
        string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.Combine(root, "src/QQT.Server/bin/Debug/net10.0") });
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "src/QQT.Server/appsettings.json")))!;
        json["Urls"] = "http://127.0.0.1:5082"; json["Battle"]!["RecordDirectory"] = Path.Combine(output, "server-jsonl");
        json["Scenario"]!["ScriptPath"] = Path.Combine(root, "scripts/m0.json");
        string configPath = Path.Combine(output, "server.config.json"); File.WriteAllText(configPath, json.ToJsonString());
        builder.Configuration.Sources.Clear(); builder.Configuration.AddJsonFile(configPath, false, false);
        var source = new ServerConfiguration(configPath); using var settings = source.Read(); var options = settings.Scenario;
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(options); builder.Services.AddSingleton<SimulationLogger>();
        builder.Services.AddSingleton<ObjectPool<JsonMessageBuffer>>(new DefaultObjectPool<JsonMessageBuffer>(new MessageBufferPolicy(options.MessageBufferBytes), options.MessagePoolRetained));
        builder.Services.AddSingleton<BattleCoordinator>(); builder.Services.AddHostedService(static p => p.GetRequiredService<BattleCoordinator>());
        await using var app = builder.Build(); app.UseWebSockets();
        app.Map("/ws/ui", c => QQT.Server.Program.HandleBattleAsync(c, true));
        app.Map("/ws/ai", c => QQT.Server.Program.HandleBattleAsync(c, false));
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var ui = new ClientWebSocket(); using var p1 = new ClientWebSocket(); using var p2 = new ClientWebSocket();
        var coordinator = app.Services.GetRequiredService<BattleCoordinator>();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int batches = 0; string game = "";
        coordinator.BeforeEvent = async () => { if (Interlocked.Increment(ref batches) == 1) { reached.SetResult(); await release.Task.WaitAsync(deadline.Token); } };
        coordinator.DisconnectQueued = id => { if (id == 2) disconnected.TrySetResult(); };
        try
        {
            await ui.ConnectAsync(new("ws://127.0.0.1:5082/ws/ui"), deadline.Token);
            string config = await File.ReadAllTextAsync(Path.Combine(root, "scripts/battle/simulation-disconnect.json"), deadline.Token);
            await Send(ui, "{\"type\":\"match.create\",\"payload\":" + config + "}", deadline.Token);
            using var ready = await Until(ui, "session.state", d => d.GetProperty("payload").GetProperty("phase").GetString() == "preparing", deadline.Token);
            game = ready.RootElement.GetProperty("gameId").GetString()!;
            await p1.ConnectAsync(new("ws://127.0.0.1:5082/ws/ai"), deadline.Token);
            await Send(p1, "{\"type\":\"register.request\",\"gameId\":\"" + game + "\",\"requestId\":\"p1\",\"payload\":{}}", deadline.Token);
            using var registered1 = await Until(p1, "register.result", null, deadline.Token);
            await p2.ConnectAsync(new("ws://127.0.0.1:5082/ws/ai"), deadline.Token);
            await Send(p2, "{\"type\":\"register.request\",\"gameId\":\"" + game + "\",\"requestId\":\"p2\",\"payload\":{}}", deadline.Token);
            using var registered2 = await Until(p2, "register.result", null, deadline.Token);
            await Send(ui, "{\"type\":\"start\",\"gameId\":\"" + game + "\"}", deadline.Token);
            using var observed1 = await Until(p1, "round.started", null, deadline.Token);
            using var observed2 = await Until(p2, "round.started", null, deadline.Token);
            await Send(p1, "{\"type\":\"action.submit\",\"gameId\":\"" + game + "\",\"roundId\":1,\"requestId\":\"a1\",\"payload\":{\"direction\":\"wait\",\"placements\":[]}}", deadline.Token);
            await Send(p2, "{\"type\":\"action.submit\",\"gameId\":\"" + game + "\",\"roundId\":1,\"requestId\":\"a2\",\"payload\":{\"direction\":\"right\",\"placements\":[{\"x\":11,\"y\":10}]}}", deadline.Token);
            await reached.Task.WaitAsync(deadline.Token);
            using var simulating = await Until(ui, "session.state", d => d.GetProperty("payload").GetProperty("phase").GetString() == "simulating", deadline.Token);
            await p2.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "scripted simulation disconnect", deadline.Token);
            await disconnected.Task.WaitAsync(deadline.Token); release.SetResult();
            using var ended = await Until(p1, "match.ended", null, deadline.Token);
            var result = ended.RootElement.GetProperty("payload");
            Require(result.GetProperty("winnerId").ValueKind == JsonValueKind.Null && Fraction(result.GetProperty("time"), 1, 2), "同刻爆炸未合并为 t=1/2 平局。");
            using var frame = await Until(ui, "frame", d => d.GetProperty("ended").GetBoolean(), deadline.Token);
            var state = frame.RootElement;
            Require(state.GetProperty("bubbleCount").GetInt32() == 1 && state.GetProperty("bubbles")[0].GetProperty("id").GetInt32() == 2, "断线者遗留泡泡未保留。");
            bool disconnectedSeen = false, explosionSeen = false; int deaths = 0;
            foreach (var e in state.GetProperty("events").EnumerateArray())
            {
                string kind = e.GetProperty("kind").GetString()!;
                Require(kind != "placed", "断线者尚未到达中心的放置不应执行。");
                if (kind == "disconnected") { disconnectedSeen = true; Require(Fraction(e.GetProperty("tick"), 1, 2) && e.GetProperty("position").GetProperty("x").GetInt32() == 11, "断线必须在跨线归属后处理。"); }
                if (kind == "exploded") explosionSeen = true;
                if (kind == "death") { deaths++; Require(Fraction(e.GetProperty("tick"), 1, 2), "同刻死亡时间。"); }
                if (kind == "matchEnded") Require(disconnectedSeen && explosionSeen && deaths == 2, "整批爆炸前提前判胜负。");
            }
            Require(deaths == 2 && disconnectedSeen && explosionSeen, "缺少断线或爆炸死亡事件。");
            await File.WriteAllTextAsync(Path.Combine(output, "final-frame.json"), state.GetRawText(), deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), "{\"status\":\"passed\",\"scenario\":\"simulation-disconnect-same-batch\",\"time\":{\"numerator\":1,\"denominator\":2},\"winnerId\":null,\"retainedBubbleId\":2}", deadline.Token);
            Console.WriteLine("PASS simulation-disconnect-same-batch: real WebSocket close, crossing first, cancel future placement, retain bubble #2, explosion then draw at 1/2.");
        }
        catch
        {
            release.TrySetResult();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), "{\"status\":\"failed\"}");
            if (ui.State == WebSocketState.Open && game.Length != 0)
                await Send(ui, "{\"type\":\"match.abort\",\"gameId\":\"" + game + "\"}", CancellationToken.None);
            throw;
        }
        finally { coordinator.BeforeEvent = null; coordinator.DisconnectQueued = null; ui.Abort(); p1.Abort(); p2.Abort(); await app.StopAsync(); }
    }
    private static bool Fraction(JsonElement value, int numerator, int denominator) => value.GetProperty("numerator").GetInt64() == numerator && value.GetProperty("denominator").GetInt64() == denominator;
    private static async Task Send(ClientWebSocket socket, string json, CancellationToken token)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(json));
        try { int count = Encoding.UTF8.GetBytes(json, bytes); await socket.SendAsync(bytes.AsMemory(0, count), WebSocketMessageType.Text, true, token); }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }
    private static async Task<JsonDocument> Until(ClientWebSocket socket, string type, Func<JsonElement, bool>? predicate, CancellationToken token)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                int length = 0; ValueWebSocketReceiveResult part;
                do
                {
                    if (length == bytes.Length) throw new InvalidDataException("验收帧过长。");
                    part = await socket.ReceiveAsync(bytes.AsMemory(length), token);
                    if (part.MessageType == WebSocketMessageType.Close) throw new InvalidDataException("预期消息前连接关闭。");
                    length += part.Count;
                } while (!part.EndOfMessage);
                var document = JsonDocument.Parse(bytes.AsMemory(0, length)); var root = document.RootElement;
                if (root.GetProperty("type").GetString() == type && (predicate is null || predicate(root))) return document;
                document.Dispose();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }
}
