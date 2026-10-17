using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace QQT.ScriptClient;

/// <summary>stdin 是有序脚本命令流；只发送给定 JSON 或关闭连接，不生成策略。stdout 是屏障事件。</summary>
public static class Program
{
    private static readonly object OutputGate = new();
    private static StreamWriter? _transcript;
    private static void Emit(string line)
    {
        lock (OutputGate) { _transcript?.WriteLine(line); Console.WriteLine(line); }
    }
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("参数：WebSocket URL、JSONL 记录路径。");
        _transcript = new StreamWriter(args[1], false, new UTF8Encoding(false)) { AutoFlush = true };
        using var socket = new ClientWebSocket();
        using var stopping = new CancellationTokenSource();
        await socket.ConnectAsync(new Uri(args[0]), stopping.Token);
        Emit("{\"event\":\"connected\"}");
        var receiving = ReceiveAsync(socket, stopping.Token);
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var command = JsonDocument.Parse(line); var root = command.RootElement;
                string op = root.GetProperty("op").GetString() ?? "";
                if (op == "stop") break;
                if (op == "close")
                {
                    if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "scripted disconnect", CancellationToken.None);
                    continue;
                }
                if (op != "send") throw new InvalidDataException("未知脚本操作。");
                string json = root.GetProperty("message").GetRawText();
                var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(json));
                try
                {
                    int count = Encoding.UTF8.GetBytes(json, bytes);
                    await socket.SendAsync(bytes.AsMemory(0, count), WebSocketMessageType.Text, true, stopping.Token);
                    Emit("{\"event\":\"sent\",\"message\":" + json + "}");
                }
                finally { ArrayPool<byte>.Shared.Return(bytes); }
            }
            return 0;
        }
        finally
        {
            stopping.Cancel(); socket.Abort(); await receiving;
            _transcript.Dispose();
        }
    }
    private static async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
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
                        var next = ArrayPool<byte>.Shared.Rent(checked(bytes.Length * 2));
                        bytes.AsSpan(0, length).CopyTo(next); ArrayPool<byte>.Shared.Return(bytes); bytes = next;
                    }
                    part = await socket.ReceiveAsync(bytes.AsMemory(length), token);
                    if (part.MessageType == WebSocketMessageType.Close) { Emit("{\"event\":\"closed\"}"); return; }
                    length += part.Count;
                } while (!part.EndOfMessage);
                Emit("{\"event\":\"received\",\"message\":" + Encoding.UTF8.GetString(bytes, 0, length) + "}");
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        { if (!token.IsCancellationRequested) Emit("{\"event\":\"closed\"}"); }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }
}
