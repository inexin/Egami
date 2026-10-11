using System.Buffers;

namespace Egami.Formats.Webp;

/// <summary>
/// Builds and writes VP8L prefix codes (RFC 9649 section 3.7.2): canonical Huffman code lengths from symbol counts, at most 15
/// bits, written as a simple code for one or two symbols under 256, or as code lengths coded by a code-length code. Codes are
/// stored reversed, since the bit stream is read from its least significant bit. A code with one symbol takes no bits.
/// </summary>
/// <remarks>Ported from ClassicMac's lossless WebP encoder (Lars Dytterud, MIT), reshaped to work on spans without LINQ.</remarks>
internal static class Vp8lPrefixCode
{
    /// <summary>The longest code VP8L allows for its five alphabets.</summary>
    internal const int MaxLength = 15;

    /// <summary>The longest code allowed in the code-length code.</summary>
    internal const int MaxCodeLengthLength = 7;

    /// <summary>The number of code-length symbols: lengths 0 to 15 and the repeat codes 16, 17 and 18.</summary>
    internal const int CodeLengthSymbols = 19;

    /// <summary>The order the code-length code's own lengths are written in.</summary>
    internal static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>
    /// Sets <paramref name="lengths"/> to Huffman code lengths for <paramref name="counts"/>, none over <paramref name="maxLength"/>:
    /// built, and while too long, built again with every count raised to a minimum that doubles each time, as libwebp limits
    /// its trees. A single used symbol gets length 1; unused symbols get 0.
    /// </summary>
    internal static void BuildLengths(ReadOnlySpan<uint> counts, Span<byte> lengths, int maxLength)
    {
        lengths.Clear();
        int used = 0;
        int only = 0;
        for (int s = 0; s < counts.Length; s++)
        {
            if (counts[s] != 0)
            {
                used++;
                only = s;
            }
        }

        if (used == 0)
        {
            return;
        }

        if (used == 1)
        {
            lengths[only] = 1;
            return;
        }

        // Leaves sorted by count then symbol, merged with the two-queue method; node i < used is leaf i.
        int nodes = (2 * used) - 1;
        ulong[] weightArray = ArrayPool<ulong>.Shared.Rent(nodes);
        int[] arrays = ArrayPool<int>.Shared.Rent(nodes * 3);
        try
        {
            Span<ulong> weight = weightArray.AsSpan(0, nodes);
            Span<int> symbol = arrays.AsSpan(0, used);
            Span<int> parent = arrays.AsSpan(nodes, nodes);
            Span<int> depth = arrays.AsSpan(2 * nodes, nodes);
            for (uint minimum = 1; ; minimum *= 2)
            {
                int n = 0;
                for (int s = 0; s < counts.Length; s++)
                {
                    if (counts[s] != 0)
                    {
                        symbol[n] = s;
                        weight[n] = Math.Max(counts[s], minimum);
                        n++;
                    }
                }

                SortLeaves(weight[..used], symbol);
                int leaf = 0;
                int inner = used;
                int next = used;
                while (next < nodes)
                {
                    int a = Smallest(weight, ref leaf, used, ref inner, next);
                    int b = Smallest(weight, ref leaf, used, ref inner, next);
                    weight[next] = weight[a] + weight[b];
                    parent[a] = next;
                    parent[b] = next;
                    next++;
                }

                depth[nodes - 1] = 0;
                int longest = 0;
                for (int i = nodes - 2; i >= 0; i--)
                {
                    depth[i] = depth[parent[i]] + 1;
                    if (i < used)
                    {
                        longest = Math.Max(longest, depth[i]);
                    }
                }

                if (longest <= maxLength)
                {
                    for (int i = 0; i < used; i++)
                    {
                        lengths[symbol[i]] = (byte)depth[i];
                    }

                    return;
                }
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(weightArray);
            ArrayPool<int>.Shared.Return(arrays);
        }
    }

    /// <summary>Sets <paramref name="codes"/> to the canonical codes for <paramref name="lengths"/>, bit-reversed.</summary>
    internal static void BuildCodes(ReadOnlySpan<byte> lengths, Span<ushort> codes)
    {
        Span<int> perLength = stackalloc int[MaxLength + 2];
        perLength.Clear();
        for (int s = 0; s < lengths.Length; s++)
        {
            perLength[lengths[s]]++;
        }

        perLength[0] = 0;
        Span<int> nextCode = stackalloc int[MaxLength + 2];
        int code = 0;
        for (int length = 1; length <= MaxLength; length++)
        {
            code = (code + perLength[length - 1]) << 1;
            nextCode[length] = code;
        }

        for (int s = 0; s < lengths.Length; s++)
        {
            int length = lengths[s];
            codes[s] = length == 0 ? (ushort)0 : Reverse(nextCode[length]++, length);
        }
    }

    /// <summary>Writes a code as a decoder reads it before its symbols. An alphabet nothing uses is written as the one symbol 0.</summary>
    internal static void Write(Vp8lBitWriter writer, ReadOnlySpan<byte> lengths)
    {
        int count = 0;
        int first = -1;
        int second = -1;
        for (int s = 0; s < lengths.Length && count <= 2; s++)
        {
            if (lengths[s] != 0)
            {
                if (count == 0)
                {
                    first = s;
                }
                else if (count == 1)
                {
                    second = s;
                }

                count++;
            }
        }

        if (count == 0)
        {
            first = 0;
            count = 1;
        }

        if (count <= 2 && first < 256 && second < 256)
        {
            writer.Write(1, 1);
            writer.Write((uint)(count - 1), 1);
            if (first < 2)
            {
                writer.Write(0, 1);
                writer.Write((uint)first, 1);
            }
            else
            {
                writer.Write(1, 1);
                writer.Write((uint)first, 8);
            }

            if (count == 2)
            {
                writer.Write((uint)second, 8);
            }

            return;
        }

        writer.Write(0, 1);
        byte[] tokenArray = ArrayPool<byte>.Shared.Rent(lengths.Length * 2);
        try
        {
            // Each token is a code-length symbol and its extra bits value.
            Span<byte> tokens = tokenArray.AsSpan(0, Tokens(lengths, tokenArray) * 2);
            Span<uint> counts = stackalloc uint[CodeLengthSymbols];
            counts.Clear();
            for (int i = 0; i < tokens.Length; i += 2)
            {
                counts[tokens[i]]++;
            }

            Span<byte> codeLengths = stackalloc byte[CodeLengthSymbols];
            Span<ushort> codeCodes = stackalloc ushort[CodeLengthSymbols];
            BuildLengths(counts, codeLengths, MaxCodeLengthLength);
            BuildCodes(codeLengths, codeCodes);
            int written = CodeLengthSymbols;
            while (written > 4 && codeLengths[CodeLengthOrder[written - 1]] == 0)
            {
                written--;
            }

            writer.Write((uint)(written - 4), 4);
            for (int i = 0; i < written; i++)
            {
                writer.Write(codeLengths[CodeLengthOrder[i]], 3);
            }

            // Every symbol's length follows (no max_symbol).
            writer.Write(0, 1);
            bool single = IsSingle(codeLengths);
            for (int i = 0; i < tokens.Length; i += 2)
            {
                int code = tokens[i];
                if (!single)
                {
                    writer.Write(codeCodes[code], codeLengths[code]);
                }

                switch (code)
                {
                    case 16:
                        writer.Write(tokens[i + 1], 2);
                        break;
                    case 17:
                        writer.Write(tokens[i + 1], 3);
                        break;
                    case 18:
                        writer.Write(tokens[i + 1], 7);
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tokenArray);
        }
    }

    /// <summary>Returns whether a code has exactly one symbol, which takes no bits.</summary>
    internal static bool IsSingle(ReadOnlySpan<byte> lengths)
    {
        int count = 0;
        foreach (byte length in lengths)
        {
            if (length != 0 && ++count > 1)
            {
                return false;
            }
        }

        return count == 1;
    }

    /// <summary>
    /// Writes the code lengths as code-length tokens into <paramref name="tokens"/> (symbol, extra) and returns how many: 0 to 15
    /// a length; 16 the previous non-zero length 3 to 6 times (before any, 8); 17 3 to 10 zeros; 18 11 to 138 zeros.
    /// </summary>
    private static int Tokens(ReadOnlySpan<byte> lengths, Span<byte> tokens)
    {
        int n = 0;
        int previous = 8;
        for (int i = 0; i < lengths.Length;)
        {
            int value = lengths[i];
            int run = 1;
            while (i + run < lengths.Length && lengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run >= 11)
                {
                    int take = Math.Min(run, 138);
                    tokens[n * 2] = 18;
                    tokens[(n * 2) + 1] = (byte)(take - 11);
                    n++;
                    run -= take;
                }

                if (run >= 3)
                {
                    tokens[n * 2] = 17;
                    tokens[(n * 2) + 1] = (byte)(run - 3);
                    n++;
                    run = 0;
                }

                for (; run > 0; run--)
                {
                    tokens[n * 2] = 0;
                    tokens[(n * 2) + 1] = 0;
                    n++;
                }

                continue;
            }

            if (value != previous)
            {
                tokens[n * 2] = (byte)value;
                tokens[(n * 2) + 1] = 0;
                n++;
                previous = value;
                run--;
            }

            while (run >= 3)
            {
                int take = Math.Min(run, 6);
                tokens[n * 2] = 16;
                tokens[(n * 2) + 1] = (byte)(take - 3);
                n++;
                run -= take;
            }

            for (; run > 0; run--)
            {
                tokens[n * 2] = (byte)value;
                tokens[(n * 2) + 1] = 0;
                n++;
            }
        }

        return n;
    }

    /// <summary>Sorts leaves by weight, then by symbol, with an insertion sort on short alphabets and an index sort otherwise.</summary>
    private static void SortLeaves(Span<ulong> weight, Span<int> symbol)
    {
        // Weights fit in 40 bits for any image VP8L allows; the key keeps symbol order for equal weights.
        int n = weight.Length;
        ulong[] keyArray = ArrayPool<ulong>.Shared.Rent(n);
        try
        {
            Span<ulong> keys = keyArray.AsSpan(0, n);
            for (int i = 0; i < n; i++)
            {
                keys[i] = (weight[i] << 12) | (uint)symbol[i];
            }

            keys.Sort();
            for (int i = 0; i < n; i++)
            {
                weight[i] = keys[i] >> 12;
                symbol[i] = (int)(keys[i] & 0xFFF);
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(keyArray);
        }
    }

    /// <summary>Takes the lighter of the next leaf and the next inner node (leaves first on ties).</summary>
    private static int Smallest(Span<ulong> weight, ref int leaf, int leaves, ref int inner, int innerEnd)
    {
        if (leaf < leaves && (inner >= innerEnd || weight[leaf] <= weight[inner]))
        {
            return leaf++;
        }

        return inner++;
    }

    private static ushort Reverse(int code, int length)
    {
        int reversed = 0;
        for (int i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        return (ushort)reversed;
    }
}
