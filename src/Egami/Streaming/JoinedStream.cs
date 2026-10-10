namespace Egami.Streaming;

/// <summary>A read-only, seekable stream over two blocks of bytes one after the other, read in place without copying them.</summary>
internal sealed class JoinedStream(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => first.Length + second.Length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            ReadOnlySpan<byte> from = _position < first.Length ? first.Span[(int)_position..] : second.Span[(int)(_position - first.Length)..];
            int n = Math.Min(from.Length, buffer.Length);
            from[..n].CopyTo(buffer);
            buffer = buffer[n..];
            _position += n;
            total += n;
        }

        return total;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
