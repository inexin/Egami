using System.Buffers.Binary;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>Writes BasisLZ's bits, least significant first, as <see cref="BasisBits"/> reads them.</summary>
internal sealed class BasisBitWriter
{
    private readonly List<byte> _bytes = [];
    private ulong _buffer;
    private int _count;

    /// <summary>Writes the low <paramref name="n"/> bits of <paramref name="value"/> (at most 32).</summary>
    public void Write(uint value, int n)
    {
        if (n == 0)
        {
            return;
        }

        _buffer |= (ulong)(value & (uint)((1UL << n) - 1)) << _count;
        _count += n;
        while (_count >= 8)
        {
            _bytes.Add((byte)_buffer);
            _buffer >>= 8;
            _count -= 8;
        }
    }

    /// <summary>Writes a value in chunks of <paramref name="bits"/> + 1 bits, the top bit of each saying another follows.</summary>
    public void Vlc(uint value, int bits)
    {
        uint mask = (1u << bits) - 1;
        while (true)
        {
            uint chunk = value & mask;
            value >>= bits;
            if (value == 0)
            {
                Write(chunk, bits + 1);
                return;
            }

            Write(chunk | (1u << bits), bits + 1);
        }
    }

    /// <summary>Returns the bytes written, the last one padded with zeros.</summary>
    public byte[] ToArray()
    {
        List<byte> bytes = [.. _bytes];
        if (_count > 0)
        {
            bytes.Add((byte)_buffer);
        }

        return [.. bytes];
    }
}

/// <summary>
/// A canonical Huffman code for BasisLZ built from symbol counts, limited to a code length, written in BasisLZ's compressed
/// table form (Appendix D.1): its code sizes run-length coded with symbols 17 to 20 and those coded with a small Huffman code.
/// </summary>
internal sealed class BasisHuffmanCode
{
    private static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16];

    private readonly byte[] _sizes;
    private readonly ushort[] _codes;

    /// <summary>Builds a code for symbols with <paramref name="counts"/>, no code longer than <paramref name="limit"/> bits.</summary>
    public BasisHuffmanCode(ReadOnlySpan<long> counts, int limit = 16)
    {
        _sizes = Lengths(counts, limit);
        _codes = Codes(_sizes);
    }

    /// <summary>Gets the code size of each symbol.</summary>
    public ReadOnlySpan<byte> Sizes => _sizes;

    /// <summary>Writes symbol <paramref name="symbol"/>.</summary>
    public void Write(BasisBitWriter bits, int symbol)
    {
        int size = _sizes[symbol];
        if (size == 0)
        {
            throw new InvalidOperationException($"Symbol {symbol} has no code.");
        }

        bits.Write(_codes[symbol], size);
    }

    /// <summary>Writes the table: the number of symbols, the code length code and the run-length coded code sizes.</summary>
    public void WriteTable(BasisBitWriter bits)
    {
        int total = _sizes.Length;
        while (total > 1 && _sizes[total - 1] == 0)
        {
            total--;
        }

        // Run-length code the sizes: runs of zeros as 17 and 18, repeats of a size as 19 and 20.
        List<(int Symbol, int Extra, int Bits)> runs = [];
        int i = 0;
        while (i < total)
        {
            int size = _sizes[i];
            int run = 1;
            while (i + run < total && _sizes[i + run] == size)
            {
                run++;
            }

            if (size == 0)
            {
                int left = run;
                while (left >= 3)
                {
                    if (left >= 11)
                    {
                        int n = Math.Min(left, 138);
                        runs.Add((18, n - 11, 7));
                        left -= n;
                    }
                    else
                    {
                        int n = Math.Min(left, 10);
                        runs.Add((17, n - 3, 3));
                        left -= n;
                    }
                }

                for (; left > 0; left--)
                {
                    runs.Add((0, 0, 0));
                }
            }
            else
            {
                runs.Add((size, 0, 0));
                int left = run - 1;
                while (left >= 3)
                {
                    if (left >= 7)
                    {
                        int n = Math.Min(left, 134);
                        runs.Add((20, n - 7, 7));
                        left -= n;
                    }
                    else
                    {
                        int n = Math.Min(left, 6);
                        runs.Add((19, n - 3, 2));
                        left -= n;
                    }
                }

                for (; left > 0; left--)
                {
                    runs.Add((size, 0, 0));
                }
            }

            i += run;
        }

        Span<long> counts = stackalloc long[21];
        counts.Clear();
        foreach ((int symbol, _, _) in runs)
        {
            counts[symbol]++;
        }

        BasisHuffmanCode lengths = new(counts, 7);
        int codes = 21;
        while (codes > 1 && lengths._sizes[CodeLengthOrder[codes - 1]] == 0)
        {
            codes--;
        }

        bits.Write((uint)total, 14);
        bits.Write((uint)codes, 5);
        for (int c = 0; c < codes; c++)
        {
            bits.Write(lengths._sizes[CodeLengthOrder[c]], 3);
        }

        foreach ((int symbol, int extra, int extraBits) in runs)
        {
            lengths.Write(bits, symbol);
            bits.Write((uint)extra, extraBits);
        }
    }

    /// <summary>
    /// Returns Huffman code sizes for <paramref name="counts"/>, limited to <paramref name="limit"/> bits by moving codes down
    /// the tree as JPEG's Annex K does. A single used symbol gets a 1-bit code; with none used, symbol 0 gets one.
    /// </summary>
    internal static byte[] Lengths(ReadOnlySpan<long> counts, int limit)
    {
        int n = counts.Length;
        byte[] sizes = new byte[n];
        List<int> used = [];
        for (int s = 0; s < n; s++)
        {
            if (counts[s] > 0)
            {
                used.Add(s);
            }
        }

        if (used.Count <= 1)
        {
            sizes[used.Count == 1 ? used[0] : 0] = 1;
            return sizes;
        }

        // Huffman's merge with a deterministic order: by weight, then by the order nodes were made.
        int nodes = used.Count;
        long[] weight = new long[nodes * 2];
        int[] parent = new int[nodes * 2];
        PriorityQueue<int, (long, int)> queue = new();
        for (int k = 0; k < nodes; k++)
        {
            weight[k] = counts[used[k]];
            queue.Enqueue(k, (weight[k], k));
        }

        int next = nodes;
        while (queue.Count > 1)
        {
            int a = queue.Dequeue();
            int b = queue.Dequeue();
            weight[next] = weight[a] + weight[b];
            parent[a] = parent[b] = next;
            queue.Enqueue(next, (weight[next], next));
            next++;
        }

        int root = next - 1;
        int[] depth = new int[nodes];
        int maxDepth = 0;
        for (int k = 0; k < nodes; k++)
        {
            int d = 0;
            for (int at = k; at != root; at = parent[at])
            {
                d++;
            }

            depth[k] = d;
            maxDepth = Math.Max(maxDepth, d);
        }

        int[] perLength = new int[Math.Max(maxDepth, limit) + 1];
        foreach (int d in depth)
        {
            perLength[d]++;
        }

        for (int len = maxDepth; len > limit; len--)
        {
            while (perLength[len] > 0)
            {
                int j = len - 2;
                while (perLength[j] == 0)
                {
                    j--;
                }

                perLength[len] -= 2;
                perLength[len - 1]++;
                perLength[j + 1] += 2;
                perLength[j]--;
            }
        }

        // The most frequent symbols get the shortest codes; ties go to the lower symbol.
        int[] order = [.. Enumerable.Range(0, nodes).OrderByDescending(k => weight[k]).ThenBy(k => used[k])];
        int position = 0;
        for (int len = 1; len <= limit; len++)
        {
            for (int c = 0; c < perLength[len]; c++)
            {
                sizes[used[order[position++]]] = (byte)len;
            }
        }

        return sizes;
    }

    /// <summary>Returns each symbol's code as Deflate assigns them, bit-reversed so the first bit written is the code's top bit.</summary>
    private static ushort[] Codes(byte[] sizes)
    {
        Span<int> perLength = stackalloc int[17];
        perLength.Clear();
        foreach (byte size in sizes)
        {
            perLength[size]++;
        }

        perLength[0] = 0;
        Span<int> next = stackalloc int[17];
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            next[len] = code;
            code = (code + perLength[len]) << 1;
        }

        ushort[] codes = new ushort[sizes.Length];
        for (int s = 0; s < sizes.Length; s++)
        {
            int len = sizes[s];
            if (len == 0)
            {
                continue;
            }

            int c = next[len]++;
            int reversed = 0;
            for (int i = 0; i < len; i++)
            {
                reversed = (reversed << 1) | ((c >> i) & 1);
            }

            codes[s] = (ushort)reversed;
        }

        return codes;
    }
}

/// <summary>Writes the parts of BasisLZ global data: its header and image descriptors around the coded codebooks and tables.</summary>
internal static class BasisLzLayout
{
    /// <summary>Returns the global data: header, image descriptors, then the four sections.</summary>
    public static byte[] GlobalData(int endpointCount, int selectorCount, IReadOnlyList<BasisImage> images, byte[] endpoints, byte[] selectors, byte[] tables)
    {
        byte[] data = new byte[20 + (images.Count * 20) + endpoints.Length + selectors.Length + tables.Length];
        Span<byte> d = data;
        BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)endpointCount);
        BinaryPrimitives.WriteUInt16LittleEndian(d[2..], (ushort)selectorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], (uint)endpoints.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], (uint)selectors.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], (uint)tables.Length);
        int at = 20;
        foreach (BasisImage image in images)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d[at..], image.IsPFrame ? 2u : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(d[(at + 4)..], image.RgbOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(d[(at + 8)..], image.RgbLength);
            BinaryPrimitives.WriteUInt32LittleEndian(d[(at + 12)..], image.AlphaOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(d[(at + 16)..], image.AlphaLength);
            at += 20;
        }

        endpoints.CopyTo(d[at..]);
        at += endpoints.Length;
        selectors.CopyTo(d[at..]);
        at += selectors.Length;
        tables.CopyTo(d[at..]);
        return data;
    }
}
