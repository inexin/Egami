namespace Egami.Formats.Av1;

/// <summary>
/// Writes the AV1 header syntax MSB first, the inverse of <see cref="Av1BitReader"/>: f(n), su(n), ns(n), uvlc() and leb128(),
/// with the trailing bits and byte alignment of section 5.3.
/// </summary>
internal sealed class Av1BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bit;

    /// <summary>Gets how many bits have been written.</summary>
    internal int Position => (_bytes.Count * 8) - ((8 - _bit) & 7);

    /// <summary>f(1).</summary>
    internal void Flag(bool value) => Bits(value ? 1u : 0u, 1);

    /// <summary>f(n) for n up to 32.</summary>
    internal void Bits(uint value, int n)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            if (_bit == 0)
            {
                _bytes.Add(0);
            }

            if (((value >> i) & 1) != 0)
            {
                _bytes[^1] |= (byte)(0x80 >> _bit);
            }

            _bit = (_bit + 1) & 7;
        }
    }

    /// <summary>f(n) of a value known to be non-negative.</summary>
    internal void Bits(int value, int n) => Bits((uint)value, n);

    /// <summary>su(n): a signed number in n bits, two's complement.</summary>
    internal void Signed(int value, int n) => Bits((uint)value & ((1u << n) - 1), n);

    /// <summary>ns(n): a number below n in as few bits as needed.</summary>
    internal void NonSymmetric(uint value, uint n)
    {
        int w = 0;
        for (uint x = n; x != 0; x >>= 1)
        {
            w++;
        }

        uint m = (1u << w) - n;
        if (value < m)
        {
            Bits(value, w - 1);
        }
        else
        {
            uint extra = value - m;
            Bits(m + (extra >> 1), w - 1);
            Bits(extra & 1, 1);
        }
    }

    /// <summary>uvlc(): a variable-length unsigned number.</summary>
    internal void Uvlc(uint value)
    {
        ulong v = (ulong)value + 1;
        int leadingZeros = 63 - (int)ulong.LeadingZeroCount(v);
        Bits(0, leadingZeros);
        Bits(1, 1);
        Bits((uint)(v - (1UL << leadingZeros)), leadingZeros);
    }

    /// <summary>trailing_bits(): a one bit, then zeros to the byte boundary.</summary>
    internal void TrailingBits()
    {
        Bits(1, 1);
        ByteAlign();
    }

    /// <summary>byte_alignment(): zeros to the byte boundary.</summary>
    internal void ByteAlign() => _bit = 0;

    /// <summary>Gets the bytes written, the last one padded with zeros.</summary>
    internal byte[] ToArray() => [.. _bytes];

    /// <summary>Writes leb128() in as few bytes as the value needs, or in <paramref name="minimumBytes"/>.</summary>
    internal static void Leb128(List<byte> output, ulong value, int minimumBytes = 1)
    {
        int count = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            count++;
            if (value != 0 || count < minimumBytes)
            {
                b |= 0x80;
            }

            output.Add(b);
        }
        while (value != 0 || count < minimumBytes);
    }
}
