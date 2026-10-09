using System.Buffers;

namespace Egami.Formats;

/// <summary>An <see cref="IBufferWriter{T}"/> that writes to a stream through one pooled buffer, flushing whenever it fills.</summary>
internal sealed class StreamBufferWriter(Stream output, int size = 64 * 1024) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(size);
    private int _count;

    public void Advance(int count)
    {
        _count += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsMemory(_count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsSpan(_count);
    }

    /// <summary>Writes out what is buffered.</summary>
    public void Flush()
    {
        if (_count > 0)
        {
            output.Write(_buffer, 0, _count);
            _count = 0;
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    private void Reserve(int sizeHint)
    {
        int needed = Math.Max(sizeHint, 1);
        if (_buffer.Length - _count >= needed)
        {
            return;
        }

        Flush();
        if (_buffer.Length < needed)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(needed);
        }
    }
}
