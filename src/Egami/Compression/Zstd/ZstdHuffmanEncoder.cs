using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Zstd;

/// <summary>
/// Huffman coding of literals for the encoder (RFC 8878 sections 3.1.1.3.1 and 4.2): code lengths limited to 11 bits, the tree
/// description (FSE-compressed weights or four bits a weight), and one or four streams written so <see cref="ZstdHuffman"/>
/// reads them. Holds one table; <see cref="CopyFrom"/> keeps the last one a frame used, for treeless literals.
/// </summary>
internal sealed class ZstdHuffmanEncoder
{
    private readonly byte[] _lengths = new byte[256];
    private readonly ushort[] _codes = new ushort[256];

    // Each symbol's code shifted up 8 with its length in the low byte, for the coding loop.
    private readonly uint[] _entries = new uint[256];
    private readonly ZstdFseEncoder _weights = new();

    /// <summary>Gets the longest code, 0 when there is no table.</summary>
    public int MaxBits { get; private set; }

    /// <summary>Gets the highest symbol with a code.</summary>
    public int MaxSymbol { get; private set; }

    /// <summary>Forgets the table.</summary>
    public void Clear() => MaxBits = 0;

    /// <summary>Takes <paramref name="other"/>'s table.</summary>
    public void CopyFrom(ZstdHuffmanEncoder other)
    {
        other._lengths.AsSpan().CopyTo(_lengths);
        other._codes.AsSpan().CopyTo(_codes);
        other._entries.AsSpan().CopyTo(_entries);
        MaxBits = other.MaxBits;
        MaxSymbol = other.MaxSymbol;
    }

    /// <summary>The coded size in bits of literals with these counts, or -1 when the table lacks one of them.</summary>
    public long Cost(ReadOnlySpan<int> counts, int maxSymbol)
    {
        if (MaxBits == 0 || maxSymbol > MaxSymbol)
        {
            return -1;
        }

        long bits = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            if (counts[s] != 0)
            {
                if (_lengths[s] == 0)
                {
                    return -1;
                }

                bits += (long)counts[s] * _lengths[s];
            }
        }

        return bits;
    }

    /// <summary>Builds a code for <paramref name="counts"/> (at least two symbols occur); false when no code of 11 bits fits.</summary>
    public bool Build(ReadOnlySpan<int> counts, int maxSymbol)
    {
        // Leaves sorted by count, then a two-queue Huffman merge; the depth of each leaf is its length.
        Span<int> order = stackalloc int[256];
        int n = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            _lengths[s] = 0;
            if (counts[s] != 0)
            {
                order[n++] = s;
            }
        }

        for (int s = maxSymbol + 1; s < 256; s++)
        {
            _lengths[s] = 0;
        }

        Span<int> leaves = order[..n];
        SortByCount(leaves, counts);
        Span<long> weight = stackalloc long[512];
        Span<int> parent = stackalloc int[512];
        for (int i = 0; i < n; i++)
        {
            weight[i] = counts[leaves[i]];
        }

        int leaf = 0;
        int inner = n;
        int next = n;
        for (int k = 0; k < n - 1; k++)
        {
            int a = leaf < n && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
            int b = leaf < n && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
            weight[next] = weight[a] + weight[b];
            parent[a] = next;
            parent[b] = next;
            next++;
        }

        Span<byte> depth = stackalloc byte[512];
        int root = next - 1;
        depth[root] = 0;
        for (int node = root - 1; node >= 0; node--)
        {
            depth[node] = (byte)Math.Min(255, depth[parent[node]] + 1);
        }

        int maxBits = 0;
        for (int i = 0; i < n; i++)
        {
            _lengths[leaves[i]] = depth[i];
            maxBits = Math.Max(maxBits, depth[i]);
        }

        if (maxBits > ZstdHuffman.MaxBits && !Limit(leaves, counts))
        {
            return false;
        }

        maxBits = 0;
        for (int i = 0; i < n; i++)
        {
            maxBits = Math.Max(maxBits, _lengths[leaves[i]]);
        }

        // Codes as the decoder lays its table out: by weight from the lightest, then by symbol.
        Span<int> start = stackalloc int[ZstdHuffman.MaxBits + 2];
        for (int s = 0; s <= maxSymbol; s++)
        {
            if (_lengths[s] != 0)
            {
                start[maxBits + 1 - _lengths[s]]++;
            }
        }

        int position = 0;
        for (int w = 1; w <= maxBits; w++)
        {
            int count = start[w];
            start[w] = position;
            position += count << (w - 1);
        }

        for (int s = 0; s <= maxSymbol; s++)
        {
            if (_lengths[s] != 0)
            {
                int w = maxBits + 1 - _lengths[s];
                _codes[s] = (ushort)(start[w] >> (w - 1));
                _entries[s] = ((uint)_codes[s] << 8) | _lengths[s];
                start[w] += 1 << (w - 1);
            }
        }

        MaxBits = maxBits;
        MaxSymbol = maxSymbol;
        while (_lengths[MaxSymbol] == 0)
        {
            MaxSymbol--;
        }

        return true;
    }

    /// <summary>Cuts lengths over 11 down and rebalances the rest so the code stays complete.</summary>
    private bool Limit(ReadOnlySpan<int> leaves, ReadOnlySpan<int> counts)
    {
        const int Limit = ZstdHuffman.MaxBits;
        long kraft = 0;
        foreach (int s in leaves)
        {
            _lengths[s] = (byte)Math.Min((int)_lengths[s], Limit);
            kraft += 1L << (Limit - _lengths[s]);
        }

        // Over-full: lengthen the rarest symbols among the longest codes under the limit.
        long full = 1L << Limit;
        while (kraft > full)
        {
            int best = -1;
            for (int length = Limit - 1; length >= 1 && best < 0; length--)
            {
                foreach (int s in leaves)
                {
                    if (_lengths[s] == length && (best < 0 || counts[s] < counts[best]))
                    {
                        best = s;
                    }
                }
            }

            if (best < 0)
            {
                return false;
            }

            kraft -= 1L << (Limit - _lengths[best] - 1);
            _lengths[best]++;
        }

        // Under-full: shorten the commonest symbols whose gain still fits.
        while (kraft < full)
        {
            int best = -1;
            foreach (int s in leaves)
            {
                if (_lengths[s] > 1 && (1L << (Limit - _lengths[s])) <= full - kraft && (best < 0 || counts[s] > counts[best]))
                {
                    best = s;
                }
            }

            if (best < 0)
            {
                return false;
            }

            kraft += 1L << (Limit - _lengths[best]);
            _lengths[best]--;
        }

        return true;
    }

    private static void SortByCount(Span<int> symbols, ReadOnlySpan<int> counts)
    {
        // By count, then by symbol, as one sort of keys holding both.
        Span<long> keys = stackalloc long[symbols.Length];
        for (int i = 0; i < symbols.Length; i++)
        {
            keys[i] = ((long)counts[symbols[i]] << 8) | (uint)symbols[i];
        }

        keys.Sort();
        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = (int)(keys[i] & 0xFF);
        }
    }

    /// <summary>Writes the tree description; returns its length, or -1 when neither form fits.</summary>
    public int WriteTree(Span<byte> target)
    {
        int count = MaxSymbol;
        Span<byte> weights = stackalloc byte[256];
        for (int s = 0; s < count; s++)
        {
            weights[s] = (byte)(_lengths[s] == 0 ? 0 : MaxBits + 1 - _lengths[s]);
        }

        int best = -1;
        if (count <= 128)
        {
            best = 1 + ((count + 1) >> 1);
        }

        int compressed = WriteCompressedWeights(weights[..count], target);
        if (compressed > 0 && (best < 0 || compressed < best))
        {
            return compressed;
        }

        if (best < 0 || best > target.Length)
        {
            return -1;
        }

        target[0] = (byte)(127 + count);
        for (int i = 0; i < count; i += 2)
        {
            target[1 + (i >> 1)] = (byte)((weights[i] << 4) | (i + 1 < count ? weights[i + 1] : 0));
        }

        return best;
    }

    /// <summary>Writes FSE-compressed weights after a size byte; returns the total, or -1 when they do not compress or decode back.</summary>
    private int WriteCompressedWeights(ReadOnlySpan<byte> weights, Span<byte> target)
    {
        int count = weights.Length;
        if (count < 2 || target.Length < 2)
        {
            return -1;
        }

        Span<int> histogram = stackalloc int[16];
        int maxWeight = 0;
        foreach (byte w in weights)
        {
            histogram[w]++;
            maxWeight = Math.Max(maxWeight, w);
        }

        for (int w = 0; w <= maxWeight; w++)
        {
            if (histogram[w] == count)
            {
                return -1;
            }
        }

        int log = ZstdFseEncoder.OptimalLog(6, count, maxWeight);
        Span<short> normalized = stackalloc short[16];
        ZstdFseEncoder.Normalize(histogram, maxWeight, count, log, normalized);
        Span<byte> body = stackalloc byte[160];
        int header = ZstdFseEncoder.WriteCounts(body, normalized, maxWeight, log);
        if (header < 0)
        {
            return -1;
        }

        _weights.Build(normalized, maxWeight, log);

        // Two states taking turns, the first weight from the first state; coded from the last weight back.
        ZstdBitWriter writer = new(body[header..]);
        int state1;
        int state2;
        int i = count;
        if ((count & 1) != 0)
        {
            state1 = _weights.Start(weights[--i]);
            state2 = _weights.Start(weights[--i]);
            if (i > 0)
            {
                _weights.Encode(ref writer, ref state1, weights[--i]);
            }
        }
        else
        {
            state2 = _weights.Start(weights[--i]);
            state1 = _weights.Start(weights[--i]);
        }

        while (i > 0)
        {
            _weights.Encode(ref writer, ref state2, weights[--i]);
            _weights.Encode(ref writer, ref state1, weights[--i]);
            writer.Flush();
        }

        _weights.Finish(ref writer, state2);
        _weights.Finish(ref writer, state1);
        int stream = writer.Close();
        int size = header + stream;
        if (stream < 0 || size >= 128 || 1 + size > target.Length)
        {
            return -1;
        }

        // The decoder ends on the stream running out, which a state that reads no bits can hide; check the weights come back.
        Span<byte> back = stackalloc byte[256];
        int decoded;
        try
        {
            decoded = ZstdHuffman.DecodeWeights(body[..size], back);
        }
        catch (CorruptDataException)
        {
            return -1;
        }

        if (decoded != count || !back[..count].SequenceEqual(weights))
        {
            return -1;
        }

        target[0] = (byte)size;
        body[..size].CopyTo(target[1..]);
        return 1 + size;
    }

    /// <summary>Codes <paramref name="literals"/> as one stream; returns its length, or -1 when it does not fit.</summary>
    public int EncodeOne(ReadOnlySpan<byte> literals, Span<byte> target)
    {
        ZstdBitWriter writer = new(target);
        ref uint entries = ref MemoryMarshal.GetArrayDataReference(_entries);
        ref byte data = ref MemoryMarshal.GetReference(literals);
        int i = literals.Length;
        while ((i & 3) != 0)
        {
            uint e = Unsafe.Add(ref entries, Unsafe.Add(ref data, --i));
            writer.AddClean(e >> 8, (int)(e & 0xFF));
        }

        writer.Flush();
        while (i > 0)
        {
            uint e3 = Unsafe.Add(ref entries, Unsafe.Add(ref data, i - 1));
            uint e2 = Unsafe.Add(ref entries, Unsafe.Add(ref data, i - 2));
            uint e1 = Unsafe.Add(ref entries, Unsafe.Add(ref data, i - 3));
            uint e0 = Unsafe.Add(ref entries, Unsafe.Add(ref data, i - 4));
            writer.AddClean(e3 >> 8, (int)(e3 & 0xFF));
            writer.AddClean(e2 >> 8, (int)(e2 & 0xFF));
            writer.AddClean(e1 >> 8, (int)(e1 & 0xFF));
            writer.AddClean(e0 >> 8, (int)(e0 & 0xFF));
            writer.Flush();
            i -= 4;
        }

        return writer.Close();
    }

    /// <summary>Codes <paramref name="literals"/> as four streams behind their jump table; returns the length, or -1.</summary>
    public int EncodeFour(ReadOnlySpan<byte> literals, Span<byte> target)
    {
        if (target.Length < 6 + 32)
        {
            return -1;
        }

        int segment = (literals.Length + 3) >> 2;
        int at = 6;
        for (int k = 0; k < 4; k++)
        {
            int from = Math.Min(k * segment, literals.Length);
            int to = k == 3 ? literals.Length : Math.Min(from + segment, literals.Length);
            int size = EncodeOne(literals[from..to], target[at..]);
            if (size < 0 || (k < 3 && size > ushort.MaxValue))
            {
                return -1;
            }

            if (k < 3)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(target[(2 * k)..], (ushort)size);
            }

            at += size;
        }

        return at;
    }
}
