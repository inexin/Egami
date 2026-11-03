namespace Egami.Formats.Jxl;

/// <summary>
/// Writes the JPEG XL codestream LSB first, the inverse of <see cref="JxlBitReader"/>: fixed bits, U32 (choosing the first
/// distribution that holds the value), U64, F16, Enum, byte alignment and an empty extensions bundle.
/// </summary>
internal sealed class JxlBitWriter : IJxlBitSink
{
    // Whole bytes so far, then up to 31 pending bits; four bytes go out at a time.
    private byte[] _bytes = new byte[64];
    private int _length;
    private ulong _buffer;
    private int _count;

    /// <summary>Gets the bits written so far.</summary>
    internal long BitLength => ((long)_length * 8) + _count;

    public void Write(ulong value, int bits)
    {
        if (bits == 0)
        {
            return;
        }

        if (bits > 32)
        {
            Write(value & 0xFFFF_FFFF, 32);
            Write(value >> 32, bits - 32);
            return;
        }

        _buffer |= (value & ((1UL << bits) - 1)) << _count;
        _count += bits;
        if (_count >= 32)
        {
            Reserve(4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(_length), (uint)_buffer);
            _length += 4;
            _buffer >>= 32;
            _count -= 32;
        }
    }

    private void Reserve(int more)
    {
        if (_length + more > _bytes.Length)
        {
            Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _length + more));
        }
    }

    /// <summary>Moves the pending whole bytes out of the bit buffer.</summary>
    private void FlushBytes()
    {
        while (_count >= 8)
        {
            Reserve(1);
            _bytes[_length++] = (byte)_buffer;
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
    internal void WriteF16(float value) => Write(F16Bits(value), 16);

    /// <summary>Gets the binary16 bits <see cref="WriteF16"/> writes for <paramref name="value"/>.</summary>
    internal static ushort F16Bits(float value)
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
            return 0;
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

        return (ushort)((sign << 15) | (biased << 10) | mantissa);
    }

    internal void WriteEnum(uint value) => WriteU32(new JxlU32(0, 0, 1, 0, 2, 4, 18, 6), value);

    /// <summary>Writes an extensions bundle with no extension.</summary>
    internal void WriteNoExtensions() => WriteU64(0);

    internal void ZeroPadToByte()
    {
        if (_count % 8 != 0)
        {
            Write(0, 8 - (_count % 8));
        }
    }

    /// <summary>Appends another writer's whole bytes (it must be byte-aligned, as sections are).</summary>
    internal void Append(JxlBitWriter other)
    {
        FlushBytes();
        other.FlushBytes();
        if (_count != 0 || other._count != 0)
        {
            throw new InvalidOperationException("Only byte-aligned writers can be joined.");
        }

        Append(other._bytes.AsSpan(0, other._length));
    }

    /// <summary>Appends whole bytes; the writer must be byte-aligned.</summary>
    internal void Append(byte[] bytes) => Append(bytes.AsSpan());

    private void Append(ReadOnlySpan<byte> bytes)
    {
        FlushBytes();
        if (_count != 0)
        {
            throw new InvalidOperationException("Bytes can only be appended at a byte boundary.");
        }

        Reserve(bytes.Length);
        bytes.CopyTo(_bytes.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Appends another writer's bits, unaligned, as if they had been written here.</summary>
    internal void AppendBits(JxlBitWriter other)
    {
        if (_count % 8 == 0)
        {
            // Aligned here: the whole bytes go across as they are.
            FlushBytes();
            Append(other._bytes.AsSpan(0, other._length));
        }
        else
        {
            ReadOnlySpan<byte> bytes = other._bytes.AsSpan(0, other._length);
            int i = 0;
            for (; i + 4 <= bytes.Length; i += 4)
            {
                Write(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]), 32);
            }

            for (; i < bytes.Length; i++)
            {
                Write(bytes[i], 8);
            }
        }

        Write(other._buffer, other._count);
    }

    internal byte[] ToArray()
    {
        ZeroPadToByte();
        FlushBytes();
        return _bytes.AsSpan(0, _length).ToArray();
    }

    /// <summary>Gets the byte count once padded.</summary>
    internal int ByteLength => _length + ((_count + 7) / 8);
}
