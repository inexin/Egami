namespace Egami.Video;

/// <summary>A read-only, non-seekable stream that returns already-read bytes before the rest of another stream.</summary>
internal sealed class ReplayStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
{
    private ReadOnlyMemory<byte> _prefix = prefix;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read;
        if (!_prefix.IsEmpty)
        {
            read = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..read].CopyTo(buffer);
            _prefix = _prefix[read..];
        }
        else
        {
            read = inner.Read(buffer);
        }

        _position += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
