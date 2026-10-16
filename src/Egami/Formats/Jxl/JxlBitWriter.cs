namespace Egami.Formats.Jxl;

/// <summary>
/// Writes the JPEG XL codestream LSB first, the inverse of <see cref="JxlBitReader"/>: fixed bits, U32 (choosing the first
/// distribution that holds the value), U64, F16, Enum, byte alignment and an empty extensions bundle.
/// </summary>
internal sealed class JxlBitWriter : IJxlBitSink
{
    private readonly List<byte> _bytes = [];
    private ulong _buffer;
    private int _count;

    /// <summary>Gets the bits written so far.</summary>
    internal long BitLength => ((long)_bytes.Count * 8) + _count;

    public void Write(ulong value, int bits)
    {
        if (bits == 0)
        {
            return;
        }

        if (bits > 56)
        {
            Write(value & 0xFFFF_FFFF, 32);
            Write(value >> 32, bits - 32);
            return;
        }

        _buffer |= (value & ((1UL << bits) - 1)) << _count;
        _count += bits;
        while (_count >= 8)
        {
            _bytes.Add((byte)_buffer);
            _buffer >>= 8;
            _count -= 8;
        }
    }

    internal void WriteBool(bool value) => Write(value ? 1UL : 0, 1);

    /// <summary>Writes a U32 with the first of the four distributions that can hold <paramref name="value"/>.</summary>
    internal void WriteU32(in JxlU32 d, uint value)
    {
        ReadOnlySpan<(uint Offset, int Bits)> choices = [(d.Offset0, d.Bits0), (d.Offset1, d.Bits1), (d.Offset2, d.Bits2), (d.Offset3, d.Bits3)];
        for (int i = 0; i < 4; i++)
        {
            (uint offset, int bits) = choices[i];
            if (value >= offset && (ulong)(value - offset) < (1UL << bits))
            {
                Write((ulong)i, 2);
                Write(value - offset, bits);
                return;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "No distribution of the U32 field holds the value.");
    }

    internal void WriteU64(ulong value)
    {
        if (value == 0)
        {
            Write(0, 2);
        }
        else if (value <= 16)
        {
            Write(1, 2);
            Write(value - 1, 4);
        }
        else if (value <= 272)
        {
            Write(2, 2);
            Write(value - 17, 8);
        }
        else
        {
            Write(3, 2);
            Write(value & 0xFFF, 12);
            value >>= 12;
            int shift = 12;
            while (value != 0)
            {
                WriteBool(true);
                int bits = shift == 60 ? 4 : 8;
                Write(value & ((1UL << bits) - 1), bits);
                value >>= bits;
                shift += bits;
            }

            if (shift < 64)
            {
                WriteBool(false);
            }
        }
    }

    /// <summary>Writes a binary16 float, the mantissa truncated as libjxl's F16Coder does (the value must be finite and below 65536).</summary>
    internal void WriteF16(float value)
    {
        uint bits32 = BitConverter.SingleToUInt32Bits(value);
        uint sign = bits32 >> 31;
        int exp = (int)((bits32 >> 23) & 0xFF) - 127;
        uint mantissa32 = bits32 & 0x7FFFFF;
        if (exp > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Too large for a binary16 float.");
        }

        if (exp < -24)
        {
            Write(0, 16);
            return;
        }

        uint biased;
        uint mantissa;
        if (exp < -14)
        {
            int sub = -14 - exp;
            biased = 0;
            mantissa = (1u << (10 - sub)) + (mantissa32 >> (13 + sub));
        }
        else
        {
            biased = (uint)(exp + 15);
            mantissa = mantissa32 >> 13;
        }

        Write((sign << 15) | (biased << 10) | mantissa, 16);
    }

    internal void WriteEnum(uint value) => WriteU32(new JxlU32(0, 0, 1, 0, 2, 4, 18, 6), value);

    /// <summary>Writes an extensions bundle with no extension.</summary>
    internal void WriteNoExtensions() => WriteU64(0);

    internal void ZeroPadToByte()
    {
        if (_count > 0)
        {
            Write(0, 8 - _count);
        }
    }

    /// <summary>Appends another writer's whole bytes (it must be byte-aligned, as sections are).</summary>
    internal void Append(JxlBitWriter other)
    {
        if (_count != 0 || other._count != 0)
        {
            throw new InvalidOperationException("Only byte-aligned writers can be joined.");
        }

        _bytes.AddRange(other._bytes);
    }

    /// <summary>Appends whole bytes; the writer must be byte-aligned.</summary>
    internal void Append(byte[] bytes)
    {
        if (_count != 0)
        {
            throw new InvalidOperationException("Bytes can only be appended at a byte boundary.");
        }

        _bytes.AddRange(bytes);
    }

    /// <summary>Appends another writer's bits, unaligned, as if they had been written here.</summary>
    internal void AppendBits(JxlBitWriter other)
    {
        foreach (byte b in other._bytes)
        {
            Write(b, 8);
        }

        Write(other._buffer, other._count);
    }

    internal byte[] ToArray()
    {
        ZeroPadToByte();
        return [.. _bytes];
    }

    /// <summary>Gets the byte count once padded.</summary>
    internal int ByteLength => _bytes.Count + (_count > 0 ? 1 : 0);
}
