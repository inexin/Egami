using System.Buffers.Binary;

namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>Writes FLAC's most-significant-bit-first fields into a growing buffer through a 64-bit accumulator.</summary>
internal sealed class FlacBitWriter
{
    private byte[] _buffer = new byte[1 << 16];
    private int _length;
    private ulong _accumulator;
    private int _bits;

    /// <summary>Gets the bytes written so far, after <see cref="AlignToByte"/>.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    /// <summary>Gets the number of bits written.</summary>
    public long BitCount => ((long)_length * 8) + _bits;

    public void Reset()
    {
        _length = 0;
        _accumulator = 0;
        _bits = 0;
    }

    /// <summary>Writes the low <paramref name="count"/> bits of a value, up to 32.</summary>
    public void Write(uint value, int count)
    {
        if (count == 0)
        {
            return;
        }

        value &= count == 32 ? uint.MaxValue : (1u << count) - 1;
        if (_bits + count > 64)
        {
            Spill();
        }

        _accumulator |= (ulong)value << (64 - _bits - count);
        _bits += count;
    }

    public void Write64(ulong value, int count)
    {
        if (count > 32)
        {
            Write((uint)(value >> 32), count - 32);
            Write((uint)value, 32);
        }
        else
        {
            Write((uint)value, count);
        }
    }

    public void WriteSigned(int value, int count) => Write((uint)value, count);

    public void WriteSigned64(long value, int count) => Write64((ulong)value, count);

    /// <summary>Writes <paramref name="zeros"/> zero bits and a one: a unary number.</summary>
    public void WriteUnary(uint zeros)
    {
        while (zeros >= 32)
        {
            Write(0, 32);
            zeros -= 32;
        }

        Write(1, (int)zeros + 1);
    }

    /// <summary>Writes Rice codes of zigzagged residuals with parameter <paramref name="k"/>.</summary>
    public void WriteRice(ReadOnlySpan<int> residual, int k)
    {
        foreach (int r in residual)
        {
            uint u = (uint)((r << 1) ^ (r >> 31));
            uint q = u >> k;
            if (q + 1 + k <= 32)
            {
                // Quotient zeros, the stop bit and the low bits in one write.
                Write((1u << k) | (u & ((1u << k) - 1)), (int)q + 1 + k);
            }
            else
            {
                WriteUnary(q);
                Write(u, k);
            }
        }
    }

    /// <summary>Pads with zero bits to a byte boundary and moves whole bytes into the buffer.</summary>
    public void AlignToByte()
    {
        if ((_bits & 7) != 0)
        {
            _bits += 8 - (_bits & 7);
        }

        Spill();
    }

    /// <summary>Appends whole bytes; the writer must be at a byte boundary.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        AlignToByte();
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    private void Spill()
    {
        int whole = _bits >> 3;
        if (whole == 0)
        {
            return;
        }

        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), _accumulator);
        _length += whole;
        _accumulator = whole == 8 ? 0 : _accumulator << (whole * 8);
        _bits -= whole * 8;
    }

    private void Ensure(int more)
    {
        if (_length + more > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + more + 8));
        }
    }
}
