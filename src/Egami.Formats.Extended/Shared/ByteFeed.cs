namespace Egami.Formats.Extended;

/// <summary>
/// The bytes after a header, one at a time or in blocks: read from the input through a window as they are needed, or from data
/// already read whole. Run-length decoders read through it so they need not care which.
/// </summary>
internal sealed class ByteFeed
{
    private const int Window = 16 * 1024;

    private readonly EgamiInput? _input;
    private readonly byte[] _buffer;
    private int _start;
    private int _end;
    private long _consumed;

    /// <summary>Reads from <paramref name="input"/> as bytes are needed, through a small window.</summary>
    public ByteFeed(EgamiInput input)
    {
        input.CheckMemory(Window);
        _input = input;
        _buffer = new byte[Window];
        _consumed = input.Position;
    }

    /// <summary>Reads the first <paramref name="length"/> bytes of <paramref name="data"/>, which start at <paramref name="offset"/> in the file.</summary>
    public ByteFeed(byte[] data, int length, long offset)
    {
        _buffer = data;
        _end = length;
        _consumed = offset;
    }

    /// <summary>Gets the file offset of the next byte.</summary>
    public long Position => _consumed;

    /// <summary>Returns the next byte, or −1 at the end of the data.</summary>
    public int Next()
    {
        if (_start == _end && !Fill())
        {
            return -1;
        }

        _consumed++;
        return _buffer[_start++];
    }

    /// <summary>Copies up to <paramref name="destination"/>'s length of bytes; returns how many there were.</summary>
    public int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (_start == _end && !Fill())
            {
                break;
            }

            int n = Math.Min(destination.Length - total, _end - _start);
            _buffer.AsSpan(_start, n).CopyTo(destination[total..]);
            _start += n;
            _consumed += n;
            total += n;
        }

        return total;
    }

    /// <summary>Skips up to <paramref name="count"/> bytes; returns how many there were.</summary>
    public long Skip(long count)
    {
        long total = 0;
        while (total < count)
        {
            if (_start == _end && !Fill())
            {
                break;
            }

            int n = (int)Math.Min(count - total, _end - _start);
            _start += n;
            _consumed += n;
            total += n;
        }

        return total;
    }

    private bool Fill()
    {
        if (_input is null)
        {
            return false;
        }

        _start = 0;
        _end = _input.Read(_buffer);
        return _end > 0;
    }
}
