namespace Egami.Formats.Jxl;

/// <summary>Supplies codestream bytes to <see cref="JxlBitReader"/>: the bare file, or the payload of its jxlc or jxlp boxes.</summary>
internal interface IJxlByteSource
{
    /// <summary>Reads up to <paramref name="destination"/>'s length of codestream bytes; returns 0 at the end of the codestream.</summary>
    int Read(Span<byte> destination);

    /// <summary>Skips <paramref name="count"/> codestream bytes; returns how many there were to skip.</summary>
    long Skip(long count);
}

/// <summary>A codestream held in memory.</summary>
internal sealed class JxlMemorySource(ReadOnlyMemory<byte> data) : IJxlByteSource
{
    private int _position;

    public int Read(Span<byte> destination)
    {
        int n = Math.Min(destination.Length, data.Length - _position);
        data.Span.Slice(_position, n).CopyTo(destination);
        _position += n;
        return n;
    }

    public long Skip(long count)
    {
        int n = (int)Math.Min(count, data.Length - _position);
        _position += n;
        return n;
    }
}

/// <summary>A U32 field's four distributions (ISO/IEC 18181-1 A.3.1.3): each a constant offset plus a number of extra bits.</summary>
internal readonly record struct JxlU32(uint Offset0, int Bits0, uint Offset1, int Bits1, uint Offset2, int Bits2, uint Offset3, int Bits3);

/// <summary>
/// Reads the JPEG XL codestream LSB first, as ISO/IEC 18181-1 Annex A defines it: fixed bits, U32, U64, F16, Enum, byte
/// alignment and the extensions bundle. Bytes come from an <see cref="IJxlByteSource"/> a buffer at a time, so a probe reads
/// only the headers and skips the group data.
/// </summary>
internal sealed class JxlBitReader
{
    private readonly IJxlByteSource _source;
    private readonly byte[] _buffer = new byte[4096];
    private int _pos;
    private int _len;
    private ulong _bits;
    private int _count;
    private long _bytesTaken;

    internal JxlBitReader(IJxlByteSource source)
    {
        _source = source;
    }

    /// <summary>Gets the number of bits read from the start of the codestream.</summary>
    internal long BitPosition => (_bytesTaken * 8) - _count;

    /// <summary>Reads <paramref name="n"/> bits, 0 to 32.</summary>
    internal uint ReadBits(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        if (_count < n)
        {
            Refill();
            if (_count < n)
            {
                throw Truncated();
            }
        }

        uint value = (uint)(_bits & ((1UL << n) - 1));
        _bits >>= n;
        _count -= n;
        return value;
    }

    internal bool ReadBool() => ReadBits(1) != 0;

    /// <summary>Reads a U32 field.</summary>
    internal uint ReadU32(in JxlU32 d)
    {
        return ReadBits(2) switch
        {
            0 => d.Offset0 + ReadBits(d.Bits0),
            1 => d.Offset1 + ReadBits(d.Bits1),
            2 => d.Offset2 + ReadBits(d.Bits2),
            _ => d.Offset3 + ReadBits(d.Bits3),
        };
    }

    /// <summary>Reads a U64 field (A.3.1.4).</summary>
    internal ulong ReadU64()
    {
        switch (ReadBits(2))
        {
            case 0:
                return 0;
            case 1:
                return 1 + ReadBits(4);
            case 2:
                return 17 + ReadBits(8);
            default:
                ulong value = ReadBits(12);
                int shift = 12;
                while (ReadBool())
                {
                    if (shift == 60)
                    {
                        value |= (ulong)ReadBits(4) << 60;
                        break;
                    }

                    value |= (ulong)ReadBits(8) << shift;
                    shift += 8;
                }

                return value;
        }
    }

    /// <summary>Reads an F16 field (A.3.1.5); infinities and NaNs are invalid.</summary>
    internal float ReadF16()
    {
        ushort bits = (ushort)ReadBits(16);
        if ((bits & 0x7C00) == 0x7C00)
        {
            throw Corrupt("An F16 field holds an infinity or NaN.");
        }

        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    /// <summary>Reads an Enum field (A.3.1.6), whose values are below 64.</summary>
    internal uint ReadEnum()
    {
        uint value = ReadU32(new JxlU32(0, 0, 1, 0, 2, 4, 18, 6));
        return value < 64 ? value : throw Corrupt($"An Enum field holds {value}; it must be below 64.");
    }

    /// <summary>Skips to the next byte boundary; the skipped bits must be zero.</summary>
    internal void ZeroPadToByte()
    {
        int n = (int)((8 - (BitPosition & 7)) & 7);
        if (ReadBits(n) != 0)
        {
            throw Corrupt("Padding bits before a byte boundary are not zero.");
        }
    }

    /// <summary>Reads the Extensions field (A.3.2): the extension bits, then skips each extension's payload.</summary>
    internal ulong ReadExtensions()
    {
        ulong extensions = ReadU64();
        ulong total = 0;
        for (int i = 0; i < 64; i++)
        {
            if ((extensions >> i & 1) != 0)
            {
                ulong length = ReadU64();
                total = length > ulong.MaxValue - total ? ulong.MaxValue : total + length;
            }
        }

        SkipBits(total);
        return extensions;
    }

    /// <summary>Skips bits; long runs at a byte boundary skip whole bytes in the source.</summary>
    internal void SkipBits(ulong n)
    {
        while (n > 0 && ((BitPosition & 7) != 0 || n < 64))
        {
            int step = (int)Math.Min(n, 32);
            ReadBits(step);
            n -= (ulong)step;
        }

        if (n > 0)
        {
            SkipBytes(checked((long)(n / 8)));
            ReadBits((int)(n % 8));
        }
    }

    /// <summary>Skips whole bytes; the reader must be at a byte boundary.</summary>
    internal void SkipBytes(long count)
    {
        while (count > 0 && _count >= 8)
        {
            _bits >>= 8;
            _count -= 8;
            count--;
        }

        int buffered = (int)Math.Min(count, _len - _pos);
        _pos += buffered;
        _bytesTaken += buffered;
        count -= buffered;
        if (count > 0)
        {
            long skipped = _source.Skip(count);
            _bytesTaken += skipped;
            if (skipped < count)
            {
                throw Truncated();
            }
        }
    }

    /// <summary>Returns whether the codestream has no bits left after this point.</summary>
    internal bool AtEnd()
    {
        Refill();
        return _count == 0;
    }

    /// <summary>Makes the exception for a broken codestream; its offset is the codestream byte being read.</summary>
    internal CorruptDataException Corrupt(string problem) => new(JxlFormat.Info.Name, BitPosition / 8, problem);

    private CorruptDataException Truncated() => Corrupt("The codestream ends early.");

    private void Refill()
    {
        while (_count <= 56)
        {
            if (_pos == _len)
            {
                _len = _source.Read(_buffer);
                _pos = 0;
                if (_len == 0)
                {
                    return;
                }
            }

            _bits |= (ulong)_buffer[_pos++] << _count;
            _count += 8;
            _bytesTaken++;
        }
    }
}
