namespace Egami.Nal;

/// <summary>
/// Writes the fixed- and variable-length codes of an H.264 or HEVC RBSP (H.264 7.2 and 9.1, H.265 7.2 and 9.2): u(n), ue(v), se(v),
/// most significant bit first, the inverse of <see cref="NalBitReader"/>. The bytes are an RBSP; <see cref="NalFraming.Escape"/>
/// adds the emulation prevention bytes of a NAL unit.
/// </summary>
internal sealed class NalBitWriter
{
    private byte[] _data;
    private int _length;
    private ulong _accumulator;
    private int _bits;

    /// <summary>Initializes a writer with room for <paramref name="capacity"/> bytes before it grows.</summary>
    internal NalBitWriter(int capacity = 64) => _data = new byte[Math.Max(capacity, 8)];

    /// <summary>Gets the number of bits written.</summary>
    internal long BitCount => ((long)_length * 8) + _bits;

    /// <summary>Gets whether the position is at a byte boundary.</summary>
    internal bool ByteAligned => _bits == 0;

    /// <summary>Writes the low n bits (0 to 32) of a value: u(n).</summary>
    internal void U(int n, uint value)
    {
        if (n == 0)
        {
            return;
        }

        _accumulator = (_accumulator << n) | (value & (uint.MaxValue >> (32 - n)));
        _bits += n;
        while (_bits >= 8)
        {
            _bits -= 8;
            Put((byte)(_accumulator >> _bits));
        }
    }

    /// <summary>Writes a flag: u(1).</summary>
    internal void Flag(bool value) => U(1, value ? 1u : 0u);

    /// <summary>Writes an unsigned Exp-Golomb code: ue(v).</summary>
    internal void Ue(uint value)
    {
        ulong code = (ulong)value + 1;
        int length = 64 - System.Numerics.BitOperations.LeadingZeroCount(code);
        if (length <= 32)
        {
            U(length - 1, 0);
            U(length, (uint)code);
        }
        else
        {
            U(32, 0);
            U(1, 1);
            U(32, (uint)code);
        }
    }

    /// <summary>Writes a signed Exp-Golomb code: se(v).</summary>
    internal void Se(int value) => Ue(value > 0 ? (uint)((2L * value) - 1) : (uint)(-2L * value));

    /// <summary>Writes rbsp_trailing_bits: the stop bit and zero bits to the byte boundary.</summary>
    internal void TrailingBits()
    {
        U(1, 1);
        AlignZero();
    }

    /// <summary>Writes byte_alignment() (HEVC): a 1 bit, then zero bits to the byte boundary.</summary>
    internal void ByteAlignment() => TrailingBits();

    /// <summary>Writes zero bits to the byte boundary.</summary>
    internal void AlignZero()
    {
        if (_bits != 0)
        {
            U(8 - _bits, 0);
        }
    }

    /// <summary>Appends whole bytes; the position must be at a byte boundary.</summary>
    internal void Bytes(ReadOnlySpan<byte> bytes)
    {
        if (_bits != 0)
        {
            throw new InvalidOperationException("The writer is not at a byte boundary.");
        }

        Reserve(bytes.Length);
        bytes.CopyTo(_data.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Gets the bytes written; the position must be at a byte boundary.</summary>
    internal byte[] ToArray()
    {
        if (_bits != 0)
        {
            throw new InvalidOperationException("The writer is not at a byte boundary.");
        }

        return _data.AsSpan(0, _length).ToArray();
    }

    private void Put(byte value)
    {
        if (_length == _data.Length)
        {
            Reserve(1);
        }

        _data[_length++] = value;
    }

    private void Reserve(int count)
    {
        if (_length + count > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, _length + count));
        }
    }
}
