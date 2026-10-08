using System.Buffers;

namespace Egami.Formats;

/// <summary>A write-only stream that collects bytes in arrays rented from the shared pool, for encoding before an async write.</summary>
internal sealed class PooledBufferStream : Stream
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _length;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position { get => _length; set => throw new NotSupportedException(); }

    /// <summary>Gets the bytes written so far. Valid until the next write or dispose.</summary>
    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _length);

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_length + buffer.Length > _buffer.Length)
        {
            long needed = (long)_length + buffer.Length;
            if (needed > Array.MaxLength)
            {
                throw new InvalidOperationException("The encoded image is larger than 2 GB.");
            }

            byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, Math.Max(needed, (long)_buffer.Length * 2)));
            _buffer.AsSpan(0, _length).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }

        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        byte[] buffer = Interlocked.Exchange(ref _buffer, []);
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        base.Dispose(disposing);
    }
}
