using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.ObjectPool;

namespace QQT.Server;

/// <summary>发送队列持有消息缓冲直到实际发送结束；慢连接不能阻塞对局状态机。</summary>
public sealed class BattlePeer : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ObjectPool<JsonMessageBuffer> _pool;
    private readonly Channel<JsonMessageBuffer> _outgoing;
    private readonly Action<BattlePeer> _closed;
    private readonly Task _sending;
    private int _stopped;
    public int PlayerId { get; set; }
    public string GameId { get; set; } = "";
    public bool IsUi { get; }

    public BattlePeer(WebSocket socket, ObjectPool<JsonMessageBuffer> pool, bool isUi, Action<BattlePeer> closed, int queueCapacity)
    {
        _socket = socket; _pool = pool; IsUi = isUi; _closed = closed;
        _outgoing = Channel.CreateBounded<JsonMessageBuffer>(queueCapacity);
        _sending = SendLoopAsync();
    }
    // 所有权无条件转移；入队失败也由本方法归还。
    public void Send(JsonMessageBuffer message)
    {
        if (!_outgoing.Writer.TryWrite(message)) { _pool.Return(message); Stop(); }
    }
    public void Complete() => _outgoing.Writer.TryComplete();
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _outgoing.Writer.TryComplete(); _socket.Abort(); _closed(this);
    }
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync())
            {
                try { await _socket.SendAsync(message.Written, WebSocketMessageType.Text, true, CancellationToken.None); }
                finally { _pool.Return(message); }
            }
            if (_socket.State == WebSocketState.Open) await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "match ended", CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException) { Stop(); }
        finally { while (_outgoing.Reader.TryRead(out var message)) _pool.Return(message); }
    }
    public async ValueTask DisposeAsync() { Stop(); await _sending; }
}
