using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;

namespace QQT.Server;

/// <summary>一次消息的唯一所有者；直到所有发送完成后才能归还对象池。</summary>
public sealed class JsonMessageBuffer : IBufferWriter<byte>, IResettable, IDisposable
{
    private byte[]? _buffer;
    private int _written;
    public Utf8JsonWriter Writer { get; }
    public JsonMessageBuffer(int initialBytes)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialBytes);
        Writer = new Utf8JsonWriter(this);
    }
    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _written);
    public void Advance(int count)
    {
        if (count < 0 || _buffer is null || count > _buffer.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(_written); }
    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(_written); }
    private void Ensure(int hint)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (hint < 0) throw new ArgumentOutOfRangeException(nameof(hint));
        int size = Math.Max(hint, 256);
        if (size <= _buffer.Length - _written) return;
        var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(_written + size, _buffer.Length * 2));
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }
    public bool TryReset()
    {
        if (_buffer is null) return false;
        _written = 0;
        Writer.Reset(this);
        return true;
    }
    public void Dispose()
    {
        Writer.Dispose();
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

public sealed class MessageBufferPolicy(int initialBytes) : PooledObjectPolicy<JsonMessageBuffer>
{
    public override JsonMessageBuffer Create() => new(initialBytes);
    public override bool Return(JsonMessageBuffer obj) => obj.TryReset();
}
