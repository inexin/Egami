using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Compression.Zstd;

/// <summary>
/// One state of an FSE decoding table (RFC 8878 section 4.1.1), with the value its symbol stands for: the symbol's base value
/// and how many extra bits follow it, so decoding a literal length, match length or offset is one lookup.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ZstdFseEntry
{
    /// <summary>The symbol's base value (the symbol itself for Huffman weights).</summary>
    public uint BaseValue;

    /// <summary>The next state, before the bits read are added.</summary>
    public ushort NextState;

    /// <summary>How many bits the next state reads.</summary>
    public byte StateBits;

    /// <summary>How many extra bits the value reads.</summary>
    public byte ExtraBits;
}

/// <summary>FSE table descriptions and decoding tables (RFC 8878 section 4.1).</summary>
internal static class ZstdFse
{
    /// <summary>
    /// Reads a table description (normalized counts) from the start of <paramref name="source"/> into <paramref name="counts"/>,
    /// where -1 stands for "less than 1"; returns the bytes it takes. Throws when it breaks the rules.
    /// </summary>
    public static int ReadCounts(ReadOnlySpan<byte> source, Span<short> counts, int maxSymbol, int maxLog, out int log, out int symbols)
    {
        if (source.IsEmpty)
        {
            throw ZstdDecoder.Corrupt("An FSE table description is missing.");
        }

        log = (source[0] & 15) + 5;
        if (log > maxLog)
        {
            throw ZstdDecoder.Corrupt($"An FSE table's accuracy log of {log} is over {maxLog}.");
        }

        int bit = 4;
        int remaining = (1 << log) + 1;
        int threshold = 1 << log;
        int bits = log + 1;
        int symbol = 0;
        bool previousZero = false;
        while (remaining > 1)
        {
            if (previousZero)
            {
                // Two-bit repeat flags: that many more zero symbols, and another flag after a 3.
                int repeat;
                do
                {
                    repeat = (int)(Peek(source, bit) & 3);
                    bit += 2;
                    symbol += repeat;
                }
                while (repeat == 3 && symbol <= maxSymbol);
            }

            if (symbol > maxSymbol)
            {
                throw ZstdDecoder.Corrupt("An FSE table description has more symbols than its alphabet.");
            }

            uint value = Peek(source, bit);
            int max = (2 * threshold) - 1 - remaining;
            int count;
            if ((int)(value & (uint)(threshold - 1)) < max)
            {
                count = (int)(value & (uint)(threshold - 1));
                bit += bits - 1;
            }
            else
            {
                count = (int)(value & (uint)((2 * threshold) - 1));
                if (count >= threshold)
                {
                    count -= max;
                }

                bit += bits;
            }

            count--;
            remaining -= Math.Abs(count);
            counts[symbol++] = (short)count;
            previousZero = count == 0;
            while (remaining < threshold)
            {
                bits--;
                threshold >>= 1;
            }
        }

        int used = (bit + 7) >> 3;
        if (remaining != 1 || used > source.Length)
        {
            throw ZstdDecoder.Corrupt("An FSE table description does not add up.");
        }

        counts[symbol..(maxSymbol + 1)].Clear();
        symbols = symbol;
        return used;
    }

    /// <summary>
    /// Builds the decoding table of <paramref name="counts"/> (the first <paramref name="symbols"/>) at accuracy
    /// <paramref name="log"/>; <paramref name="baseValues"/> and <paramref name="extraBits"/> give each symbol's value, or the
    /// symbol itself when empty.
    /// </summary>
    public static void Build(ReadOnlySpan<short> counts, int symbols, int log, ReadOnlySpan<uint> baseValues, ReadOnlySpan<byte> extraBits,
        Span<ZstdFseEntry> table)
    {
        int size = 1 << log;
        int high = size - 1;
        Span<ushort> next = stackalloc ushort[64];
        Span<byte> spread = stackalloc byte[512];
        for (int s = 0; s < symbols; s++)
        {
            if (counts[s] == -1)
            {
                spread[high--] = (byte)s;
                next[s] = 1;
            }
            else
            {
                next[s] = (ushort)counts[s];
            }
        }

        int mask = size - 1;
        int step = (size >> 1) + (size >> 3) + 3;
        int position = 0;
        for (int s = 0; s < symbols; s++)
        {
            for (int i = 0; i < counts[s]; i++)
            {
                spread[position] = (byte)s;
                do
                {
                    position = (position + step) & mask;
                }
                while (position > high);
            }
        }

        if (position != 0)
        {
            throw ZstdDecoder.Corrupt("An FSE table's counts do not fill it.");
        }

        for (int u = 0; u < size; u++)
        {
            int s = spread[u];
            int state = next[s]++;
            int stateBits = log - BitOperations.Log2((uint)state);
            table[u] = new ZstdFseEntry
            {
                BaseValue = baseValues.IsEmpty ? (uint)s : baseValues[s],
                ExtraBits = extraBits.IsEmpty ? (byte)0 : extraBits[s],
                StateBits = (byte)stateBits,
                NextState = (ushort)((state << stateBits) - size),
            };
        }
    }

    /// <summary>Gets at least 25 bits from bit <paramref name="bit"/> on, least significant first; zeros past the end.</summary>
    private static uint Peek(ReadOnlySpan<byte> source, int bit)
    {
        int at = bit >> 3;
        uint value = 0;
        for (int i = 0; i < 4 && at + i < source.Length; i++)
        {
            value |= (uint)source[at + i] << (8 * i);
        }

        return value >> (bit & 7);
    }
}
