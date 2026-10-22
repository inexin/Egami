namespace Egami.Video.Containers;

/// <summary>
/// A buffered, write-only view of a muxer's output that reports a position on non-seekable streams too, by counting the
/// bytes written.
/// </summary>
internal sealed class OutputStream(Stream inner) : Stream
{
    private readonly BufferedStream _buffer = new(inner, 1 << 16);
    private long _written;

    public override bool CanRead => false;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => true;

    public override long Length => inner.CanSeek ? _buffer.Length : _written;

    public override long Position
    {
        get => inner.CanSeek ? _buffer.Position : _written;
        set => _buffer.Position = value;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _buffer.Write(buffer);
        _written += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        _buffer.WriteByte(value);
        _written++;
    }

    public override void Flush() => _buffer.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Flush();
        }

        base.Dispose(disposing);
    }
}
