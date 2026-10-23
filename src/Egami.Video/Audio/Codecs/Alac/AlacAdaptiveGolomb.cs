using System.Numerics;

namespace Egami.Video.Audio.Codecs.Alac;

/// <summary>
/// ALAC's adaptive Golomb entropy coder, translated from Apple's ag_dec.c and ag_enc.c (Apache-2.0): each residual is
/// coded with a Rice-like parameter that tracks a running mean, and long runs of zeros switch to a run-length code.
/// </summary>
internal static class AlacAdaptiveGolomb
{
    /// <summary>The default mean tuning (mb), as Apple's encoder writes it in the config.</summary>
    public const int Mb0 = 10;

    /// <summary>The default mean update rate (pb).</summary>
    public const int Pb0 = 40;

    /// <summary>The default largest Rice parameter (kb).</summary>
    public const int Kb0 = 14;

    /// <summary>The default maximum run, unused by the coder.</summary>
    public const int MaxRunDefault = 255;

    private const int QbShift = 9;
    private const uint Qb = 1u << QbShift;
    private const int MMulShift = 2;
    private const int MDenShift = QbShift - MMulShift - 1;
    private const uint MOff = 1u << (MDenShift - 2);
    private const int BitOff = 24;
    private const int MaxPrefix16 = 9;
    private const int MaxPrefix32 = 9;
    private const int MaxDatatypeBits16 = 16;
    private const uint MaxMeanClamp = 0xffff;
    private const uint MeanClampValue = 0xffff;

    /// <summary>
    /// Decodes <paramref name="count"/> residuals (dyn_decomp) from <paramref name="data"/>, which must have at least 16 bytes
    /// of zero padding past <paramref name="endBits"/>. Advances <paramref name="bitPos"/>.
    /// </summary>
    /// <exception cref="CorruptDataException">The residuals run past the end, or a zero run past the frame.</exception>
    public static void Decompress(byte[] data, ref int bitPos, int endBits, Span<int> output, int count, int maxSize, uint mb0, uint pb, uint kb)
    {
        uint wb = (1u << (int)kb) - 1;
        uint mb = mb0;
        uint zmode = 0;
        int c = 0;
        int pos = bitPos;
        while (c < count)
        {
            if (pos >= endBits)
            {
                throw new CorruptDataException("ALAC", pos >> 3, "The residuals run past the end of the packet.");
            }

            uint m = mb >> QbShift;
            uint k = Math.Min(Lg3a(m), kb);
            m = (1u << (int)k) - 1;
            uint n = Get32(data, ref pos, m, (int)k, maxSize);

            // The lowest bit is the sign.
            uint decoded = n + zmode;
            int multiplier = -(int)(decoded & 1) | 1;
            output[c++] = (int)((decoded + 1) >> 1) * multiplier;
            mb = (pb * (n + zmode)) + mb - ((pb * mb) >> QbShift);
            if (n > MaxMeanClamp)
            {
                mb = MeanClampValue;
            }

            zmode = 0;
            if ((mb << MMulShift) < Qb && c < count)
            {
                zmode = 1;
                int zk = Lead(mb) - BitOff + (int)((mb + MOff) >> MDenShift);
                uint mz = ((1u << zk) - 1) & wb;
                uint run = Get16(data, ref pos, mz, zk);
                if ((ulong)c + run > (ulong)count)
                {
                    throw new CorruptDataException("ALAC", pos >> 3, "A run of zero residuals runs past the frame.");
                }

                output.Slice(c, (int)run).Clear();
                c += (int)run;
                if (run >= 65535)
                {
                    zmode = 0;
                }

                mb = 0;
            }
        }

        bitPos = pos;
    }

    /// <summary>Codes <paramref name="count"/> residuals (dyn_comp) into the writer at its position, and moves it on.</summary>
    public static void Compress(ReadOnlySpan<int> input, AlacBitWriter writer, int count, int bitSize)
    {
        byte[] output = writer.Buffer;
        int pos = writer.Position;
        uint mb = Mb0;
        const uint pb = Pb0;
        const uint kb = Kb0;
        const uint wb = (1u << Kb0) - 1;
        uint zmode = 0;
        int c = 0;
        while (c < count)
        {
            uint m = mb >> QbShift;
            uint k = Math.Min(Lg3a(m), kb);
            m = (1u << (int)k) - 1;
            int del = input[c++];
            uint n = (uint)(Abs(del) << 1) - (uint)((del >> 31) & 1) - zmode;
            if (Code32(m, k, n, out int numBits, out uint value))
            {
                Jam(output, pos, numBits, value);
                pos += numBits;
                JamLarge(output, pos, bitSize, n);
                pos += bitSize;
            }
            else
            {
                Jam(output, pos, numBits, value);
                pos += numBits;
            }

            mb = (pb * (n + zmode)) + mb - ((pb * mb) >> QbShift);
            if (n > MaxMeanClamp)
            {
                mb = MeanClampValue;
            }

            zmode = 0;
            if ((mb << MMulShift) < Qb && c < count)
            {
                zmode = 1;
                uint nz = 0;
                while (c < count && input[c] == 0)
                {
                    c++;
                    if (++nz >= 65535)
                    {
                        zmode = 0;
                        break;
                    }
                }

                int zk = Lead(mb) - BitOff + (int)((mb + MOff) >> MDenShift);
                uint mz = ((1u << zk) - 1) & wb;
                value = Code16(mz, zk, nz, out numBits);
                Jam(output, pos, numBits, value);
                pos += numBits;
                mb = 0;
            }
        }

        writer.Position = pos;
    }

    /// <summary>Returns how many bits <see cref="Compress"/> would write, without writing them (the encoder's search passes).</summary>
    public static int Count(ReadOnlySpan<int> input, int count, int bitSize)
    {
        int bits = 0;
        uint mb = Mb0;
        const uint pb = Pb0;
        const uint kb = Kb0;
        const uint wb = (1u << Kb0) - 1;
        uint zmode = 0;
        int c = 0;
        while (c < count)
        {
            uint m = mb >> QbShift;
            uint k = Math.Min(Lg3a(m), kb);
            m = (1u << (int)k) - 1;
            int del = input[c++];
            uint n = (uint)(Abs(del) << 1) - (uint)((del >> 31) & 1) - zmode;
            bits += Code32(m, k, n, out int numBits, out _) ? numBits + bitSize : numBits;
            mb = (pb * (n + zmode)) + mb - ((pb * mb) >> QbShift);
            if (n > MaxMeanClamp)
            {
                mb = MeanClampValue;
            }

            zmode = 0;
            if ((mb << MMulShift) < Qb && c < count)
            {
                zmode = 1;
                uint nz = 0;
                while (c < count && input[c] == 0)
                {
                    c++;
                    if (++nz >= 65535)
                    {
                        zmode = 0;
                        break;
                    }
                }

                int zk = Lead(mb) - BitOff + (int)((mb + MOff) >> MDenShift);
                uint mz = ((1u << zk) - 1) & wb;
                Code16(mz, zk, nz, out int runBits);
                bits += runBits;
                mb = 0;
            }
        }

        return bits;
    }

    /// <summary>Counts the leading zeros of a 32-bit word (Apple's lead, 32 for zero).</summary>
    private static int Lead(uint m) => BitOperations.LeadingZeroCount(m);

    private static uint Lg3a(uint x) => (uint)(31 - Lead(x + 3));

    private static int Abs(int a)
    {
        int negative = a >> 31;
        return (a ^ negative) - negative;
    }

    private static uint Read32(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    /// <summary>Reads <paramref name="count"/> bits, 1 to 32, at a bit offset (getstreambits).</summary>
    private static uint StreamBits(byte[] input, int bitOffset, int count)
    {
        int byteOffset = bitOffset >> 3;
        int sub = bitOffset & 7;
        uint load1 = Read32(input, byteOffset);
        uint result;
        if (count + sub > 32)
        {
            result = load1 << sub;
            uint load2 = (uint)input[byteOffset + 4] >> (8 - (count + sub - 32));
            result >>= 32 - count;
            result |= load2;
        }
        else
        {
            result = load1 >> (32 - count - sub);
        }

        if (count != 32)
        {
            result &= ~(0xffffffffu << count);
        }

        return result;
    }

    /// <summary>Reads one zero-run length (dyn_get).</summary>
    private static uint Get16(byte[] input, ref int bitPos, uint m, int k)
    {
        int pos = bitPos;
        uint stream = Read32(input, pos >> 3) << (pos & 7);
        uint pre = (uint)Lead(~stream);
        uint result;
        if (pre >= MaxPrefix16)
        {
            pre = MaxPrefix16;
            pos += (int)pre;
            stream <<= (int)pre;
            result = stream >> (32 - MaxDatatypeBits16);
            pos += MaxDatatypeBits16;
        }
        else
        {
            pos += (int)pre + 1;
            stream <<= (int)pre + 1;
            uint v = stream >> (32 - k);
            pos += k;
            result = (pre * m) + v - 1;
            if (v < 2)
            {
                result -= v - 1;
                pos -= 1;
            }
        }

        bitPos = pos;
        return result;
    }

    /// <summary>Reads one residual code (dyn_get_32bit), escaping to <paramref name="maxBits"/> raw bits.</summary>
    private static uint Get32(byte[] input, ref int bitPos, uint m, int k, int maxBits)
    {
        int pos = bitPos;
        uint stream = Read32(input, pos >> 3) << (pos & 7);
        uint result = (uint)Lead(~stream);
        if (result >= MaxPrefix32)
        {
            result = StreamBits(input, pos + MaxPrefix32, maxBits);
            pos += MaxPrefix32 + maxBits;
        }
        else
        {
            pos += (int)result + 1;
            if (k != 1)
            {
                stream <<= (int)result + 1;
                uint v = stream >> (32 - k);
                pos += k - 1;
                result *= m;
                if (v >= 2)
                {
                    result += v - 1;
                    pos += 1;
                }
            }
        }

        bitPos = pos;
        return result;
    }

    /// <summary>Codes a zero-run length (dyn_code).</summary>
    private static uint Code16(uint m, int k, uint n, out int numBits)
    {
        uint div = n / m;
        uint value;
        if (div >= MaxPrefix16)
        {
            numBits = MaxPrefix16 + MaxDatatypeBits16;
            value = (((1u << MaxPrefix16) - 1) << MaxDatatypeBits16) + n;
        }
        else
        {
            uint mod = n % m;
            uint de = mod == 0 ? 1u : 0u;
            numBits = (int)(div + (uint)k + 1 - de);
            value = (((1u << (int)div) - 1) << (numBits - (int)div)) + mod + 1 - de;

            // If this is longer than the escape, escape.
            if (numBits > MaxPrefix16 + MaxDatatypeBits16)
            {
                numBits = MaxPrefix16 + MaxDatatypeBits16;
                value = (((1u << MaxPrefix16) - 1) << MaxDatatypeBits16) + n;
            }
        }

        return value;
    }

    /// <summary>Codes a residual (dyn_code_32bit); returns whether it escaped, its raw bits then following the prefix.</summary>
    private static bool Code32(uint m, uint k, uint n, out int numBits, out uint value)
    {
        uint div = n / m;
        if (div < MaxPrefix32)
        {
            uint mod = n - (m * div);
            uint de = mod == 0 ? 1u : 0u;
            numBits = (int)(div + k + 1 - de);
            value = (((1u << (int)div) - 1) << (numBits - (int)div)) + mod + 1 - de;
            if (numBits <= 25)
            {
                return false;
            }
        }

        numBits = MaxPrefix32;
        value = (1u << MaxPrefix32) - 1;
        return true;
    }

    /// <summary>Puts up to 25 bits at a bit position, keeping the bits around them (dyn_jam_noDeref).</summary>
    private static void Jam(byte[] output, int bitPos, int numBits, uint value)
    {
        int i = bitPos >> 3;
        uint current = Read32(output, i);
        int shift = 32 - (bitPos & 7) - numBits;
        uint mask = (~0u >> (32 - numBits)) << shift;
        value = ((value << shift) & mask) | (current & ~mask);
        Write32(output, i, value);
    }

    /// <summary>Puts up to 32 bits at a bit position, spilling into a fifth byte (dyn_jam_noDeref_large).</summary>
    private static void JamLarge(byte[] output, int bitPos, int numBits, uint value)
    {
        int i = bitPos >> 3;
        uint current = Read32(output, i);
        int shift = 32 - (bitPos & 7) - numBits;
        uint w;
        if (shift < 0)
        {
            w = value >> -shift;
            uint mask = ~0u >> -shift;
            w |= current & ~mask;
            output[i + 4] = (byte)(value << (8 + shift));
        }
        else
        {
            uint mask = (~0u >> (32 - numBits)) << shift;
            w = ((value << shift) & mask) | (current & ~mask);
        }

        Write32(output, i, w);
    }

    private static void Write32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
