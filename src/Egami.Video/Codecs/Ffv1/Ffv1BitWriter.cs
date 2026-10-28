using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>Writes FFV1's Golomb-Rice coded slices most significant bit first, after a range-coded header in the same buffer.</summary>
internal struct Ffv1BitWriter(Ffv1RangeEncoder target)
{
    private readonly Ffv1RangeEncoder _target = target;
    private ulong _window;
    private int _bits;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, 0 to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBits(int count, uint value)
    {
        if (count == 0)
        {
            return;
        }

        ulong bits = value & ((1UL << count) - 1);
        int free = 64 - _bits;
        if (count <= free)
        {
            _window |= bits << (free - count);
            _bits += count;
            if (_bits == 64)
            {
                _target.AppendBigEndian(_window);
                _window = 0;
                _bits = 0;
            }

            return;
        }

        // Fill the window, hand it over whole, and start the next with what is left.
        int rest = count - free;
        _target.AppendBigEndian(_window | (bits >> rest));
        _window = bits << (64 - rest);
        _bits = rest;
    }

    /// <summary>Writes an unsigned Golomb-Rice code with parameter <paramref name="k"/>; values too large escape with <paramref name="bits"/> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnsignedGolomb(int value, int k, int bits)
    {
        int prefix = value >> k;
        if (prefix < 12)
        {
            // The prefix's zeros, its closing 1 and the k low bits in one go when they fit in 32 bits.
            if (prefix + 1 + k <= 32)
            {
                WriteBits(prefix + 1 + k, (uint)((1L << k) | (uint)(value & ((1 << k) - 1))));
                return;
            }

            WriteBits(prefix + 1, 1);
            WriteBits(k, (uint)value);
        }
        else
        {
            WriteBits(12, 0);
            WriteBits(bits, (uint)(value - 11));
        }
    }

    /// <summary>Pads to a whole byte with zeros and hands the bytes to the range encoder's buffer.</summary>
    public void Finish()
    {
        Span<byte> bytes = stackalloc byte[8];
        int n = (_bits + 7) >> 3;
        for (int i = 0; i < n; i++)
        {
            bytes[i] = (byte)(_window >> (56 - (8 * i)));
        }

        _target.Append(bytes[..n]);
        _window = 0;
        _bits = 0;
    }
}
