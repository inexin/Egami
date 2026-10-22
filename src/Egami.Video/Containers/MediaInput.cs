using System.Buffers.Binary;

namespace Egami.Video.Containers;

/// <summary>
/// Reads a container from a stream, seekable or not: tracks the position itself, skips by seeking or by reading, and turns
/// short reads into a clean end of data. Small reads go through a buffer, so parsers can read field by field cheaply.
/// </summary>
internal sealed class MediaInput : IDisposable
{
    private const int BufferSize = 1 << 16;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _bufferStart;
    private int _bufferEnd;
    private long _bufferFilePosition;

    public MediaInput(Stream stream, bool leaveOpen, string formatName)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        FormatName = formatName;
        CanSeek = stream.CanSeek;
        _bufferFilePosition = CanSeek ? stream.Position : 0;
        Length = CanSeek ? stream.Length : long.MaxValue;
    }

    public string FormatName { get; }

    public bool CanSeek { get; }

    /// <summary>Gets the stream length, or <see cref="long.MaxValue"/> when the stream cannot seek.</summary>
    public long Length { get; }

    public long Position => _bufferFilePosition + _bufferStart;

    /// <summary>Gets how many bytes remain before the end of a seekable stream, or <see cref="long.MaxValue"/>.</summary>
    public long Remaining => CanSeek ? Math.Max(0, Length - Position) : long.MaxValue;

    public CorruptDataException Corrupt(string problem) => new(FormatName, Position, problem);

    public CorruptDataException Corrupt(long offset, string problem) => new(FormatName, offset, problem);

    /// <summary>Reads up to <paramref name="destination"/>'s length; fewer bytes only at the end of the stream.</summary>
    public int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (_bufferStart < _bufferEnd)
            {
                int n = Math.Min(_bufferEnd - _bufferStart, destination.Length - total);
                _buffer.AsSpan(_bufferStart, n).CopyTo(destination[total..]);
                _bufferStart += n;
                total += n;
                continue;
            }

            int wanted = destination.Length - total;
            if (wanted >= BufferSize)
            {
                // Large reads bypass the buffer.
                _bufferFilePosition += _bufferEnd;
                _bufferStart = _bufferEnd = 0;
                int direct = _stream.Read(destination[total..]);
                if (direct <= 0)
                {
                    break;
                }

                _bufferFilePosition += direct;
                total += direct;
                continue;
            }

            if (!Fill())
            {
                break;
            }
        }

        return total;
    }

    public void ReadExactly(Span<byte> destination)
    {
        long start = Position;
        if (Read(destination) != destination.Length)
        {
            throw Corrupt(start, $"The data ends inside a {destination.Length}-byte structure.");
        }
    }

    /// <summary>Tries to read exactly; returns false (having consumed what was there) at the end of the stream.</summary>
    public bool TryReadExactly(Span<byte> destination) => Read(destination) == destination.Length;

    public byte ReadByte()
    {
        if (_bufferStart == _bufferEnd && !Fill())
        {
            throw Corrupt("The data ends early.");
        }

        return _buffer[_bufferStart++];
    }

    /// <summary>Returns the next byte without consuming it, or -1 at the end.</summary>
    public int PeekByte() => _bufferStart < _bufferEnd || Fill() ? _buffer[_bufferStart] : -1;

    public uint ReadUInt32LittleEndian()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    /// <summary>Moves forward by <paramref name="count"/> bytes; returns false when the stream ends first.</summary>
    public bool Skip(long count)
    {
        if (count <= 0)
        {
            return count == 0;
        }

        long buffered = _bufferEnd - _bufferStart;
        if (count <= buffered)
        {
            _bufferStart += (int)count;
            return true;
        }

        if (CanSeek)
        {
            long target = Position + count;
            if (target > Length)
            {
                Seek(Length);
                return false;
            }

            Seek(target);
            return true;
        }

        count -= buffered;
        _bufferStart = _bufferEnd;
        while (count > 0)
        {
            if (!Fill())
            {
                return false;
            }

            int n = (int)Math.Min(count, _bufferEnd - _bufferStart);
            _bufferStart += n;
            count -= n;
        }

        return true;
    }

    public void Seek(long position)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        if (position < 0 || position > Length)
        {
            // Positions come from the file's own indexes and sizes.
            throw Corrupt($"A position in the file ({position}) is outside it.");
        }

        if (position >= _bufferFilePosition && position <= _bufferFilePosition + _bufferEnd)
        {
            _bufferStart = (int)(position - _bufferFilePosition);
            return;
        }

        _stream.Position = position;
        _bufferFilePosition = position;
        _bufferStart = _bufferEnd = 0;
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private bool Fill()
    {
        _bufferFilePosition += _bufferEnd;
        _bufferStart = _bufferEnd = 0;
        int n = _stream.Read(_buffer, 0, BufferSize);
        if (n <= 0)
        {
            return false;
        }

        _bufferEnd = n;
        return true;
    }
}
