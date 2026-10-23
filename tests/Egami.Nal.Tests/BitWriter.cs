namespace Egami.Nal.Tests;

/// <summary>Writes the u(n), ue(v) and se(v) codes of an RBSP, most significant bit first, to build test input.</summary>
internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bits;

    /// <summary>Gets the number of bits written.</summary>
    internal long BitCount => ((long)_bytes.Count * 8) - ((8 - _bits) & 7);

    internal BitWriter U(int n, long value)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            Bit((int)((value >> i) & 1));
        }

        return this;
    }

    internal BitWriter Flag(bool value) => U(1, value ? 1 : 0);

    internal BitWriter Ue(uint value)
    {
        ulong code = (ulong)value + 1;
        int length = 64 - System.Numerics.BitOperations.LeadingZeroCount(code);
        U(length - 1, 0);
        return U(length, (long)code);
    }

    internal BitWriter Se(int value) => Ue(value > 0 ? (uint)((2 * value) - 1) : (uint)(-2 * (long)value));

    internal BitWriter Bit(int bit)
    {
        if (_bits == 0)
        {
            _bytes.Add(0);
        }

        _bytes[^1] |= (byte)(bit << (7 - _bits));
        _bits = (_bits + 1) & 7;
        return this;
    }

    /// <summary>Writes rbsp_trailing_bits: a 1 bit, then zero bits to the byte boundary.</summary>
    internal BitWriter TrailingBits()
    {
        Bit(1);
        while (_bits != 0)
        {
            Bit(0);
        }

        return this;
    }

    internal byte[] ToArray() => [.. _bytes];
}
