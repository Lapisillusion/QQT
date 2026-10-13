using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;

namespace QQT.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool verify = false;
        string? scriptOverride = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--verify") verify = true;
            else if (args[i] == "--script" && i + 1 < args.Length) scriptOverride = Path.GetFullPath(args[++i]);
            else throw new ArgumentException($"不支持的参数：{args[i]}");
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        var options = ScenarioOptions.Load(builder.Configuration, scriptOverride);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SimulationLogger>();
        builder.Services.AddSingleton<ObjectPoolProvider>(_ => new DefaultObjectPoolProvider { MaximumRetained = options.MessagePoolRetained });
        builder.Services.AddSingleton<ObjectPool<JsonMessageBuffer>>(static services => services.GetRequiredService<ObjectPoolProvider>()
            .Create(new MessageBufferPolicy(services.GetRequiredService<ScenarioOptions>().MessageBufferBytes)));
        builder.Services.AddSingleton<ScenarioRunner>();
        await using var app = builder.Build();
        if (verify) return await app.Services.GetRequiredService<ScenarioRunner>().VerifyAsync() ? 0 : 1;
        app.UseDefaultFiles(); app.UseStaticFiles(); app.UseWebSockets();
        app.Map("/ws/ui", HandleUiAsync);
        app.Logger.LogInformation("M0 ready: open {Address}; script={ScriptPath}", builder.Configuration["Urls"], options.ScriptPath);
        await app.RunAsync();
        return 0;
    }

    private static async Task HandleUiAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var runner = context.RequestServices.GetRequiredService<ScenarioRunner>();
        var options = context.RequestServices.GetRequiredService<ScenarioOptions>();
        var pool = context.RequestServices.GetRequiredService<ObjectPool<JsonMessageBuffer>>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("QQT.Transport.Ui");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var peer = new UiPeer(socket);
        var receive = ArrayPool<byte>.Shared.Rent(options.ReceiveBufferBytes);
        int peerId = 0;
        try
        {
            peerId = await runner.AddPeerAsync(peer, context.RequestAborted);
            logger.LogInformation("UI_CONNECTED PeerId={PeerId}", peerId);
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                int length = 0;
                ValueWebSocketReceiveResult part;
                do
                {
                    if (length == options.ReceiveBufferBytes) throw new InvalidDataException("UI 消息过长。");
                    part = await socket.ReceiveAsync(receive.AsMemory(length, options.ReceiveBufferBytes - length), context.RequestAborted);
                    if (part.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "UI closed", CancellationToken.None);
                        return;
                    }
                    if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("UI 仅接受 JSON 文本消息。");
                    length += part.Count;
                } while (!part.EndOfMessage);
                try
                {
                    using var document = JsonDocument.Parse(receive.AsMemory(0, length));
                    if (!document.RootElement.TryGetProperty("type", out var type) || type.GetString() != "start")
                        await SendErrorAsync(peer, pool, "仅支持开始／重新运行。", context.RequestAborted);
                    else if (!await runner.StartAsync())
                        await SendErrorAsync(peer, pool, "验证正在运行，请等待完成。", context.RequestAborted);
                }
                catch (JsonException) { await SendErrorAsync(peer, pool, "JSON 格式错误。", context.RequestAborted); }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (WebSocketException exception) { logger.LogDebug(exception, "UI connection closed PeerId={PeerId}", peerId); }
        catch (InvalidDataException exception) { logger.LogWarning(exception, "Invalid UI message PeerId={PeerId}", peerId); }
        finally
        {
            runner.RemovePeer(peerId);
            ArrayPool<byte>.Shared.Return(receive);
            logger.LogInformation("UI_DISCONNECTED PeerId={PeerId}", peerId);
        }
    }
    private static async Task SendErrorAsync(UiPeer peer, ObjectPool<JsonMessageBuffer> pool, string text, CancellationToken token)
    {
        var message = pool.Get();
        try
        {
            message.Writer.WriteStartObject(); message.Writer.WriteString("type", "error");
            message.Writer.WriteString("message", text); message.Writer.WriteEndObject(); message.Writer.Flush();
            await peer.SendAsync(message.Written, token);
        }
        finally { pool.Return(message); }
    }
}
