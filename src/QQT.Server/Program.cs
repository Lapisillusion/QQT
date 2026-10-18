using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;

namespace QQT.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool verify = false, battle = false;
        string? scriptOverride = null, configOverride = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--verify") verify = true;
            else if (args[i] == "--battle") battle = true;
            else if (args[i] == "--script" && i + 1 < args.Length) scriptOverride = Path.GetFullPath(args[++i]);
            else if (args[i] == "--config" && i + 1 < args.Length) configOverride = Path.GetFullPath(args[++i]);
            else throw new ArgumentException($"不支持的参数：{args[i]}");
        }
        if (verify && battle) throw new ArgumentException("--verify 与 --battle 不能同时使用。");
        var source = new ServerConfiguration(configOverride ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json"), scriptOverride);
        using var startup = source.Read();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        { ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        // 指定文件替代默认 JSON，不再叠加环境文件或环境变量形成隐含覆盖。
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonFile(source.Path, optional: false, reloadOnChange: false);
        builder.WebHost.UseKestrel().UseUrls(startup.Urls);
        builder.Services.AddRouting();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole();
        var options = startup.Scenario;
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SimulationLogger>();
        builder.Services.AddSingleton<ObjectPoolProvider>(_ => new DefaultObjectPoolProvider { MaximumRetained = options.MessagePoolRetained });
        builder.Services.AddSingleton<ObjectPool<JsonMessageBuffer>>(static services => services.GetRequiredService<ObjectPoolProvider>()
            .Create(new MessageBufferPolicy(services.GetRequiredService<ScenarioOptions>().MessageBufferBytes)));
        builder.Services.AddSingleton<ScenarioRunner>();
        builder.Services.AddSingleton<BattleCoordinator>();
        if (battle) builder.Services.AddHostedService(static services => services.GetRequiredService<BattleCoordinator>());
        await using var app = builder.Build();
        if (verify) return await app.Services.GetRequiredService<ScenarioRunner>().VerifyAsync() ? 0 : 1;
        app.UseDefaultFiles(); app.UseStaticFiles(); app.UseWebSockets();
        if (battle)
        {
            app.Map("/ws/ui", context => HandleBattleAsync(context, true));
            app.Map("/ws/ai", context => HandleBattleAsync(context, false));
        }
        else app.Map("/ws/ui", HandleUiAsync);
        app.Logger.LogInformation("SERVER_READY ConfigPath={ConfigPath} Address={Address} ScriptPath={ScriptPath}", source.Path, startup.Urls, options.ScriptPath);
        await app.RunAsync();
        return 0;
    }

    internal static async Task HandleBattleAsync(HttpContext context, bool ui)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var coordinator = context.RequestServices.GetRequiredService<BattleCoordinator>();
        var pool = context.RequestServices.GetRequiredService<ObjectPool<JsonMessageBuffer>>();
        var options = context.RequestServices.GetRequiredService<ScenarioOptions>();
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(ui ? "QQT.Transport.Ui" : "QQT.Transport.Ai");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var peer = new BattlePeer(socket, pool, ui, coordinator.Closed, options.OutgoingQueueCapacity);
        var receive = ArrayPool<byte>.Shared.Rent(options.ReceiveBufferBytes);
        coordinator.Attach(peer);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                int length = 0; ValueWebSocketReceiveResult part;
                do
                {
                    if (length == options.ReceiveBufferBytes) throw new InvalidDataException("消息过长。");
                    part = await socket.ReceiveAsync(receive.AsMemory(length, options.ReceiveBufferBytes - length), context.RequestAborted);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("仅接受 JSON 文本。");
                    length += part.Count;
                } while (!part.EndOfMessage);
                try { coordinator.Receive(peer, JsonDocument.Parse(receive.AsMemory(0, length))); }
                catch (JsonException exception) { log.LogWarning(exception, "INVALID_JSON PlayerId={PlayerId}", peer.PlayerId); }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception) when (exception is WebSocketException or InvalidDataException or ObjectDisposedException)
        { log.LogDebug(exception, "CONNECTION_CLOSED PlayerId={PlayerId}", peer.PlayerId); }
        finally { ArrayPool<byte>.Shared.Return(receive); peer.Stop(); }
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
