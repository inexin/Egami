namespace Egami.Streaming;

/// <summary>
/// The bytes an incremental decode has received and not yet read, as a stream that returns 0 when it runs dry without that meaning
/// the end: <see cref="Ended"/> says whether more can come. Bytes are dropped once read.
/// </summary>
internal sealed class AppendBuffer : Stream
{
    private byte[] _data = new byte[4096];
    private int _start;
    private int _end;

    /// <summary>Gets whether the caller said no more bytes will come.</summary>
    public bool Ended { get; private set; }

    /// <summary>Gets the bytes received and not yet read.</summary>
    public int Available => _end - _start;

    /// <summary>Gets the bytes received in total.</summary>
    public long Received { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Adds received bytes after those not yet read.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (Ended)
        {
            throw new InvalidOperationException("EndOfInput was already called; no more bytes can be appended.");
        }

        if (_data.Length - _end < data.Length)
        {
            int unread = _end - _start;
            if (unread + data.Length > _data.Length)
            {
                byte[] larger = new byte[Math.Max(_data.Length * 2, unread + data.Length)];
                _data.AsSpan(_start, unread).CopyTo(larger);
                _data = larger;
            }
            else
            {
                _data.AsSpan(_start, unread).CopyTo(_data);
            }

            _start = 0;
            _end = unread;
        }

        data.CopyTo(_data.AsSpan(_end));
        _end += data.Length;
        Received += data.Length;
    }

    /// <summary>Says no more bytes will come: running dry is the end of the data from now on.</summary>
    public void End()
    {
        Ended = true;
    }

    /// <summary>Copies bytes not yet read, from <paramref name="offset"/> on, without reading them; false if they have not all arrived.</summary>
    public bool TryPeek(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > Available - destination.Length)
        {
            return false;
        }

        _data.AsSpan(_start + (int)offset, destination.Length).CopyTo(destination);
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _end - _start);
        _data.AsSpan(_start, n).CopyTo(buffer);
        _start += n;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Thrown inside an incremental decode when the bytes received so far run out before the step that needs them is done. The step
/// leaves its state as it was, so it can run again once more bytes arrive. It never reaches callers.
/// </summary>
internal sealed class NeedsInputException : Exception
{
    public NeedsInputException()
        : base("More data is needed.")
    {
    }
}
