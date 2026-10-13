using System.Net.WebSockets;

namespace QQT.Server;

public sealed class UiPeer(WebSocket socket)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken token)
    {
        await _sendGate.WaitAsync(token);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(message, WebSocketMessageType.Text, true, token);
        }
        finally { _sendGate.Release(); }
    }
}
