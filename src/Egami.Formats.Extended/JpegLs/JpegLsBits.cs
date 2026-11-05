using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>Thrown inside the scan decoder when the coded data cannot be valid; the reader turns it into a warning or an error.</summary>
internal sealed class JpegLsDataException(string message) : Exception(message);

/// <summary>
/// Reads the bits of one coded segment (a scan, or a restart interval of it), most significant first. A byte after 0xFF holds
/// only 7 bits, its top bit being the 0 that T.87 stuffs there (A.1); past the end of the segment the reader gives zeros.
/// </summary>
internal struct JpegLsBitReader
{
    private readonly byte[] _data;
    private int _position;
    private readonly int _end;
    private ulong _cache;
    private int _bits;
    private bool _afterFF;
    private int _padding;

    public JpegLsBitReader(byte[] data, int start, int end)
    {
        _data = data;
        _position = start;
        _end = end;
        _cache = 0;
        _bits = 0;
        _afterFF = false;
        _padding = 0;
    }

    /// <summary>Gets how many bytes of zeros the reader has made up past the end of the segment.</summary>
    public readonly int Padding => _padding;

    /// <summary>Gets the position of the next byte the reader would take.</summary>
    public readonly int Position => _position;

    /// <summary>Tops the cache up to at least 57 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill()
    {
        if (_bits > 56)
        {
            return;
        }

        int position = _position;
        if (!_afterFF && position + 8 <= _end)
        {
            ulong word = BinaryPrimitives.ReadUInt64BigEndian(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), position), 8));

            // No 0xFF byte in the eight: take as many whole bytes as fit.
            ulong inverted = ~word;
            if (((inverted - 0x0101010101010101UL) & ~inverted & 0x8080808080808080UL) == 0)
            {
                int take = (64 - _bits) >> 3;
                _cache |= (word & (ulong.MaxValue << (64 - (take * 8)))) >> _bits;
                _bits += take * 8;
                _position = position + take;
                return;
            }
        }

        FillSlow();
    }

    /// <summary>Returns the next <paramref name="count"/> bits, 0 to 32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(int count)
    {
        if (_bits < count)
        {
            Fill();
        }

        int value = (int)((_cache >> 1) >> (63 - count));
        _cache <<= count;
        _bits -= count;
        return value;
    }

    /// <summary>Returns the next bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit()
    {
        if (_bits < 1)
        {
            Fill();
        }

        int value = (int)(_cache >> 63);
        _cache <<= 1;
        _bits--;
        return value;
    }

    /// <summary>
    /// Reads a limited-length Golomb code word (T.87 A.5.3) with parameter <paramref name="k"/>, where <paramref name="limit"/>
    /// is the word's LIMIT and <paramref name="qbpp"/> the bits of an escaped value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Golomb(int k, int limit, int qbpp)
    {
        Fill();
        int zeros = BitOperations.LeadingZeroCount(_cache);
        int escape = limit - qbpp - 1;
        if (zeros < escape && zeros + 1 + k <= _bits)
        {
            ulong cache = _cache << (zeros + 1);
            int value = (zeros << k) | (int)((cache >> 1) >> (63 - k));
            _cache = cache << k;
            _bits -= zeros + 1 + k;
            return value;
        }

        return GolombSlow(k, escape, qbpp);
    }

    private int GolombSlow(int k, int escape, int qbpp)
    {
        int zeros = 0;
        while (true)
        {
            Fill();
            if (_cache != 0)
            {
                int run = BitOperations.LeadingZeroCount(_cache);
                zeros += run;
                _cache <<= run + 1;
                _bits -= run + 1;
                break;
            }

            zeros += _bits;
            _bits = 0;
            if (zeros > escape || _padding > 8)
            {
                throw new JpegLsDataException("A Golomb code word is longer than the limit.");
            }
        }

        if (zeros < escape)
        {
            return (zeros << k) | Read(k);
        }

        if (zeros > escape)
        {
            throw new JpegLsDataException("A Golomb code word is longer than the limit.");
        }

        return Read(qbpp) + 1;
    }

    private void FillSlow()
    {
        while (_bits <= 56)
        {
            if (_position >= _end)
            {
                // Zeros past the end; count them so a decoder can tell the data ran out.
                _padding += (64 - _bits) >> 3;
                _bits = 64;
                return;
            }

            ulong b = _data[_position++];
            if (_afterFF)
            {
                _cache |= (b & 0x7F) << (57 - _bits);
                _bits += 7;
            }
            else
            {
                _cache |= b << (56 - _bits);
                _bits += 8;
            }

            _afterFF = b == 0xFF;
        }
    }
}

/// <summary>
/// Writes coded bits, most significant first, stuffing a 0 bit at the top of every byte after 0xFF (T.87 A.1), into a growing
/// buffer.
/// </summary>
internal sealed class JpegLsBitWriter
{
    private byte[] _buffer;
    private int _length;
    private ulong _cache;
    private int _bits;
    private bool _afterFF;

    public JpegLsBitWriter(int capacity)
    {
        _buffer = new byte[Math.Max(capacity, 256)];
    }

    /// <summary>Gets the bytes written so far.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    /// <summary>Writes the whole bytes so far to <paramref name="output"/> and forgets them; the bits still in the cache stay.</summary>
    public void MoveTo(Stream output)
    {
        output.Write(_buffer, 0, _length);
        _length = 0;
    }

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, 0 to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(uint value, int count)
    {
        if (_bits + count > 64)
        {
            Drain();
        }

        _cache |= (ulong)value << (64 - _bits - count);
        _bits += count;
    }

    /// <summary>Writes <paramref name="count"/> zero bits, any number.</summary>
    public void Zeros(int count)
    {
        while (count > 32)
        {
            Write(0, 32);
            count -= 32;
        }

        Write(0, count);
    }

    /// <summary>
    /// Writes a limited-length Golomb code word (T.87 A.5.3) for <paramref name="value"/> with parameter <paramref name="k"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Golomb(int value, int k, int limit, int qbpp)
    {
        int high = value >> k;
        int escape = limit - qbpp - 1;
        if (high < escape)
        {
            if (high + 1 + k <= 32)
            {
                Write((1u << k) | (uint)(value & ((1 << k) - 1)), high + 1 + k);
            }
            else
            {
                Zeros(high);
                Write((1u << k) | (uint)(value & ((1 << k) - 1)), 1 + k);
            }
        }
        else
        {
            Zeros(escape);
            Write((1u << qbpp) | (uint)(value - 1), 1 + qbpp);
        }
    }

    /// <summary>Pads the last byte with zeros (T.87 A.1) and empties the cache into the buffer.</summary>
    public void Flush()
    {
        Drain();
        if (_bits > 0)
        {
            int room = _afterFF ? 7 : 8;
            Write(0, room - _bits);
            Drain();
        }

        // A segment that ends in 0xFF gets a 0 byte, so the 0xFF cannot read as the start of the marker after it.
        if (_afterFF)
        {
            Put(0);
        }

        _bits = 0;
        _cache = 0;
        _afterFF = false;
    }

    /// <summary>Appends whole bytes, as markers and segments, after a <see cref="Flush"/>.</summary>
    public void Raw(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Moves the whole bytes of the cache into the buffer.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Drain()
    {
        Ensure(16);
        while (true)
        {
            int room = _afterFF ? 7 : 8;
            if (_bits < room)
            {
                return;
            }

            byte b = (byte)(_cache >> (64 - room));
            _cache <<= room;
            _bits -= room;
            Put(b);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(byte b)
    {
        Ensure(1);
        _buffer[_length++] = b;
        _afterFF = b == 0xFF;
    }

    private void Ensure(int count)
    {
        if (_length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        }
    }
}
