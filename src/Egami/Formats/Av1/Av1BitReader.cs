namespace Egami.Formats.Av1;

/// <summary>
/// Reads the AV1 header syntax MSB first, as section 4.10 of the AV1 Bitstream and Decoding Process specification defines its
/// descriptors: f(n), uvlc(), le(n), leb128(), su(n) and ns(n). Running out of bits throws <see cref="CorruptDataException"/>.
/// </summary>
internal ref struct Av1BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string _formatName;
    private int _bit;

    internal Av1BitReader(ReadOnlySpan<byte> data, string formatName)
    {
        _data = data;
        _formatName = formatName;
    }

    /// <summary>Gets how many bits have been read.</summary>
    internal readonly int Position => _bit;

    /// <summary>Gets how many bits are left.</summary>
    internal readonly int Remaining => (_data.Length * 8) - _bit;

    /// <summary>f(1).</summary>
    internal bool Flag() => Bits(1) != 0;

    /// <summary>f(n) for n up to 32.</summary>
    internal uint Bits(int n)
    {
        if (n > Remaining)
        {
            throw new CorruptDataException(_formatName, _bit / 8, "An AV1 header is cut off.") { IsTruncation = true };
        }

        uint value = 0;
        for (int i = 0; i < n; i++)
        {
            value = (value << 1) | (uint)((_data[_bit >> 3] >> (7 - (_bit & 7))) & 1);
            _bit++;
        }

        return value;
    }

    /// <summary>uvlc(): a variable-length unsigned number, up to 32 bits.</summary>
    internal uint Uvlc()
    {
        int leadingZeros = 0;
        while (!Flag())
        {
            leadingZeros++;
            if (leadingZeros >= 32)
            {
                return uint.MaxValue;
            }
        }

        return leadingZeros == 0 ? 0 : (uint)((1UL << leadingZeros) - 1 + Bits(leadingZeros));
    }

    /// <summary>su(n): a signed number of n bits.</summary>
    internal int Signed(int n)
    {
        int value = (int)Bits(n);
        int sign = 1 << (n - 1);
        return (value & sign) != 0 ? value - (2 * sign) : value;
    }

    /// <summary>ns(n): an unsigned number below n, in as few bits as needed.</summary>
    internal uint NonSymmetric(uint n)
    {
        int w = 0;
        for (uint x = n; x != 0; x >>= 1)
        {
            w++;
        }

        uint m = (1u << w) - n;
        uint v = Bits(w - 1);
        return v < m ? v : (v << 1) - m + Bits(1);
    }

    /// <summary>Skips to the next byte boundary.</summary>
    internal void ByteAlign() => _bit = (_bit + 7) & ~7;

    /// <summary>leb128() from a byte span; returns the value and how many bytes it took, or -1 bytes when it is broken.</summary>
    internal static (ulong Value, int Length) Leb128(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= data.Length)
            {
                return (0, -1);
            }

            value |= (ulong)(data[i] & 0x7F) << (i * 7);
            if ((data[i] & 0x80) == 0)
            {
                return value > uint.MaxValue ? (0, -1) : (value, i + 1);
            }
        }

        return (0, -1);
    }
}
