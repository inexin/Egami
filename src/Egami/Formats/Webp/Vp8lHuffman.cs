using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// Decoding tables for VP8L prefix codes: a root table indexed by the next 8 bits, whose entries for longer codes point at
/// second-level tables. Each entry packs the symbol (bits 16 to 31) and the bits to consume (bits 0 to 7); a root entry of a long
/// code instead holds 8 plus its second-level table's index bits, and that table's offset.
/// </summary>
internal static class Vp8lHuffman
{
    /// <summary>The bits the root table is indexed by.</summary>
    internal const int RootBits = 8;

    /// <summary>
    /// Returns how many entries a table for <paramref name="lengths"/> needs, or -1 if the lengths are not a complete prefix code
    /// (over-subscribed, incomplete, or no symbol at all). A code with one symbol is valid and takes no bits.
    /// </summary>
    internal static int Size(ReadOnlySpan<byte> lengths)
    {
        Span<int> count = stackalloc int[16];
        count.Clear();
        int symbols = 0;
        foreach (byte length in lengths)
        {
            if (length != 0)
            {
                count[length]++;
                symbols++;
            }
        }

        if (symbols == 0)
        {
            return -1;
        }

        if (symbols == 1)
        {
            return 1 << RootBits;
        }

        int left = 1;
        for (int length = 1; length <= 15; length++)
        {
            left = (left << 1) - count[length];
            if (left < 0)
            {
                return -1;
            }
        }

        if (left != 0)
        {
            return -1;
        }

        // The root table, plus a second-level table for each root index that long codes share, as big as its longest code needs.
        Span<byte> longest = stackalloc byte[1 << RootBits];
        longest.Clear();
        Span<int> next = stackalloc int[16];
        Canonical(count, next);
        for (int s = 0; s < lengths.Length; s++)
        {
            int length = lengths[s];
            if (length > RootBits)
            {
                int key = Reverse(next[length]++, length) & ((1 << RootBits) - 1);
                longest[key] = (byte)Math.Max(longest[key], length);
            }
            else if (length != 0)
            {
                next[length]++;
            }
        }

        int size = 1 << RootBits;
        foreach (byte length in longest)
        {
            if (length != 0)
            {
                size += 1 << (length - RootBits);
            }
        }

        return size;
    }

    /// <summary>Fills <paramref name="table"/> (at least <see cref="Size"/> entries) for lengths <see cref="Size"/> accepted.</summary>
    internal static void Build(ReadOnlySpan<byte> lengths, Span<uint> table)
    {
        Span<int> count = stackalloc int[16];
        count.Clear();
        int only = -1;
        int symbols = 0;
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] != 0)
            {
                count[lengths[s]]++;
                symbols++;
                only = s;
            }
        }

        if (symbols == 1)
        {
            table[..(1 << RootBits)].Fill((uint)only << 16);
            return;
        }

        // First the longest code per root index, which sizes each second-level table, then every entry.
        Span<byte> longest = stackalloc byte[1 << RootBits];
        longest.Clear();
        Span<int> next = stackalloc int[16];
        Canonical(count, next);
        for (int s = 0; s < lengths.Length; s++)
        {
            int length = lengths[s];
            if (length > RootBits)
            {
                int key = Reverse(next[length]++, length) & ((1 << RootBits) - 1);
                longest[key] = (byte)Math.Max(longest[key], length);
            }
            else if (length != 0)
            {
                next[length]++;
            }
        }

        Span<int> offset = stackalloc int[1 << RootBits];
        int end = 1 << RootBits;
        for (int key = 0; key < longest.Length; key++)
        {
            if (longest[key] != 0)
            {
                int bits = longest[key] - RootBits;
                offset[key] = end;
                table[key] = ((uint)end << 16) | (uint)(RootBits + bits);
                end += 1 << bits;
            }
        }

        Canonical(count, next);
        for (int s = 0; s < lengths.Length; s++)
        {
            int length = lengths[s];
            if (length == 0)
            {
                continue;
            }

            int code = Reverse(next[length]++, length);
            if (length <= RootBits)
            {
                uint entry = ((uint)s << 16) | (uint)length;
                for (int i = code; i < 1 << RootBits; i += 1 << length)
                {
                    table[i] = entry;
                }
            }
            else
            {
                int key = code & ((1 << RootBits) - 1);
                int bits = longest[key] - RootBits;
                int sub = code >> RootBits;
                int subLength = length - RootBits;
                uint entry = ((uint)s << 16) | (uint)subLength;
                for (int i = sub; i < 1 << bits; i += 1 << subLength)
                {
                    table[offset[key] + i] = entry;
                }
            }
        }
    }

    /// <summary>Decodes one symbol with at least 15 bits buffered.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Decode(ReadOnlySpan<uint> table, ref ulong bits, ref int count)
    {
        uint entry = table[(int)(bits & ((1 << RootBits) - 1))];
        int length = (int)(entry & 0xFF);
        if (length > RootBits)
        {
            bits >>= RootBits;
            count -= RootBits;
            entry = table[(int)(entry >> 16) + (int)(bits & ((1u << (length - RootBits)) - 1))];
            length = (int)(entry & 0xFF);
        }

        bits >>= length;
        count -= length;
        return (int)(entry >> 16);
    }

    /// <summary>Sets <paramref name="next"/>[length] to the first canonical code of each length.</summary>
    private static void Canonical(ReadOnlySpan<int> count, Span<int> next)
    {
        int code = 0;
        next[0] = 0;
        for (int length = 1; length <= 15; length++)
        {
            code = (code + (length > 1 ? count[length - 1] : 0)) << 1;
            next[length] = code;
        }
    }

    private static int Reverse(int code, int length)
    {
        int reversed = 0;
        for (int i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        return reversed;
    }
}
