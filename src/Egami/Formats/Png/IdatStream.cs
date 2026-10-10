using System.Buffers.Binary;

namespace Egami.Formats.Png;

/// <summary>
/// Presents the data of consecutive IDAT chunks (or an APNG frame's fdAT chunks, without their sequence numbers) as one read-only
/// stream, checking each chunk's CRC as it reaches the end of the chunk. When the run ends, the header of the next chunk is left
/// in <see cref="NextLength"/> and <see cref="NextType"/>.
/// </summary>
internal sealed class IdatStream : Stream
{
    private readonly PngChunkReader _chunks;
    private readonly uint _type;
    private readonly Func<uint, bool>? _sequence;
    private long _remaining;
    private uint _crc;
    private bool _ended;

    /// <summary>Starts at the data of an IDAT chunk whose header has just been read.</summary>
    internal IdatStream(PngChunkReader chunks, uint length)
        : this(chunks, length, PngChunkType.Idat, null)
    {
    }

    /// <summary>
    /// Starts at the data of a chunk of <paramref name="type"/> whose header has just been read. For fdAT, each chunk's sequence
    /// number goes to <paramref name="sequence"/>; when it returns false the run ends there, as if the file ended.
    /// </summary>
    internal IdatStream(PngChunkReader chunks, uint length, uint type, Func<uint, bool>? sequence)
    {
        _chunks = chunks;
        _type = type;
        _sequence = sequence;
        Begin(length);
    }

    /// <summary>Gets a value indicating whether the run ended at a sequence number that was out of order.</summary>
    internal bool OutOfSequence { get; private set; }

    /// <summary>Gets the length of the chunk after the IDAT run, once it has ended.</summary>
    internal uint NextLength { get; private set; }

    /// <summary>Gets the type of the chunk after the IDAT run, once it has ended, or 0 if the data ended first.</summary>
    internal uint NextType { get; private set; }

    /// <summary>Gets a value indicating whether the IDAT run has ended.</summary>
    internal bool Ended => _ended;

    /// <summary>Gets a value indicating whether the file ended inside the IDAT run.</summary>
    internal bool Truncated { get; private set; }

    /// <summary>Gets the last four data bytes read, big-endian: after a full drain, the zlib stream's Adler-32 checksum.</summary>
    internal uint Tail { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        while (!_ended && _remaining == 0)
        {
            Advance();
        }

        if (_ended || buffer.IsEmpty)
        {
            return 0;
        }

        int n = _chunks.Input.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        if (n == 0)
        {
            Truncated = true;
            _ended = true;
            return 0;
        }

        _crc = Crc32.Update(_crc, buffer[..n]);
        foreach (byte value in n > 4 ? buffer.Slice(n - 4, 4) : buffer[..n])
        {
            Tail = (Tail << 8) | value;
        }

        _remaining -= n;
        return n;
    }

    /// <summary>Reads and checks the rest of the IDAT run, discarding it.</summary>
    internal void Drain()
    {
        Span<byte> scratch = stackalloc byte[4096];
        while (Read(scratch) > 0)
        {
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    private void Begin(uint length)
    {
        _remaining = length;
        Span<byte> name = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(name, _type);
        _crc = Crc32.Update(0, name);
        if (_sequence is null)
        {
            return;
        }

        Span<byte> number = stackalloc byte[4];
        if (length < 4 || _chunks.Input.Read(number) < 4)
        {
            Truncated = length >= 4;
            _ended = true;
            return;
        }

        _crc = Crc32.Update(_crc, number);
        _remaining -= 4;
        if (!_sequence(BinaryPrimitives.ReadUInt32BigEndian(number)))
        {
            OutOfSequence = true;
            _ended = true;
        }
    }

    /// <summary>
    /// Moves past the rest of the image data without decompressing or checksumming it, for a decode that needs no more rows. The
    /// chunk headers are still read, so the chunk after the data is known.
    /// </summary>
    internal void SkipRest()
    {
        while (!_ended)
        {
            try
            {
                _chunks.Input.Skip(_remaining);
                _remaining = 0;
            }
            catch (CorruptDataException e) when (e.IsTruncation)
            {
                Truncated = true;
                _ended = true;
                return;
            }

            Advance(checkCrc: false);
        }
    }

    /// <summary>Checks the CRC of the finished chunk (or skips it) and reads the next chunk header.</summary>
    private void Advance(bool checkCrc = true)
    {
        if (_chunks.Input.Peek(4).Length < 4)
        {
            Truncated = true;
            _ended = true;
            return;
        }

        if (checkCrc)
        {
            _chunks.CheckCrc(_type, _crc, critical: true);
        }
        else
        {
            _chunks.Input.Skip(4);
        }

        int available = _chunks.Input.Peek(8).Length;
        if (available is > 0 and < 8)
        {
            Truncated = true;
        }

        if (available < 8 || !_chunks.TryReadHeader(out uint length, out uint type))
        {
            _ended = true;
            return;
        }

        if (type == _type)
        {
            Begin(length);
            return;
        }

        NextLength = length;
        NextType = type;
        _ended = true;
    }
}
