namespace Egami.Video;

/// <summary>
/// Sits between a job and its input or output stream: counts the bytes read and written for progress reports, and after
/// <see cref="Abandon"/> refuses every further write, so a cancelled job's muxer stops at once instead of finishing the file.
/// Disposing it leaves the inner stream open.
/// </summary>
internal sealed class JobStream(Stream inner) : Stream
{
    private readonly long _start = inner.CanSeek ? inner.Position : 0;
    private long _written;
    private bool _abandoned;

    public long BytesRead { get; private set; }

    /// <summary>Gets the output's length so far: the furthest byte written, so sizes patched in place count once.</summary>
    public long BytesWritten { get; private set; }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            ThrowIfAbandoned();
            inner.Position = value;
        }
    }

    /// <summary>Refuses every write, seek and flush from now on.</summary>
    public void Abandon() => _abandoned = true;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override int ReadByte()
    {
        int value = inner.ReadByte();
        if (value >= 0)
        {
            BytesRead++;
        }

        return value;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfAbandoned();
        inner.Write(buffer);
        Wrote(buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        ThrowIfAbandoned();
        inner.WriteByte(value);
        Wrote(1);
    }

    public override void Flush()
    {
        ThrowIfAbandoned();
        inner.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfAbandoned();
        return inner.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        ThrowIfAbandoned();
        inner.SetLength(value);
    }

    private void Wrote(int count)
    {
        _written += count;
        BytesWritten = Math.Max(BytesWritten, inner.CanSeek ? inner.Position - _start : _written);
    }

    private void ThrowIfAbandoned()
    {
        if (_abandoned)
        {
            throw new OperationCanceledException("The job was cancelled.");
        }
    }
}
