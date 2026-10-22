using System.Buffers;

namespace Egami.Formats.Webp;

/// <summary>Choosing and applying the color cache (RFC 9649 section 5.2.3) for a token stream.</summary>
internal static class Vp8lCache
{
    /// <summary>
    /// Returns the cache size (0, or 1 to <paramref name="maxBits"/> bits) whose estimated stream is smallest, simulating every size
    /// at once: a slot of a smaller cache is the top bits of a larger cache's slot, so one hash serves all.
    /// </summary>
    internal static int ChooseBits(ReadOnlySpan<ulong> tokens, ReadOnlySpan<uint> pixels, int maxBits)
    {
        if (maxBits <= 0)
        {
            return 0;
        }

        Vp8lHistogram[] histograms = new Vp8lHistogram[maxBits + 1];
        for (int b = 0; b <= maxBits; b++)
        {
            histograms[b] = new Vp8lHistogram(b);
        }

        // Every cache size side by side: size b's slots are [1 << b, 2 << b). After each pixel every cache holds it, so a pixel
        // equal to the one before changes none of them; runs of one color in a copy cost nothing. When literals are few next to
        // the copied pixels, only the largest cache is kept, with when each slot was written: a smaller cache's slot holds the
        // newest of the larger slots it covers, worked out when a literal asks.
        long literals = 0;
        long copied = 0;
        foreach (ulong token in tokens)
        {
            Vp8lEncoder.Token.Read(token, out int kind, out _, out int length);
            (literals, copied) = kind == Vp8lEncoder.Token.Backward ? (literals, copied + length) : (literals + 1, copied);
        }

        bool newestOnly = (literals << maxBits) < copied * maxBits;
        uint[] slots = new uint[2 << maxBits];
        int[] written = newestOnly ? new int[1 << maxBits] : [];
        written.AsSpan().Fill(-1);
        int shift = 32 - maxBits;
        uint last = 0;
        int pos = 0;
        foreach (ulong token in tokens)
        {
            Vp8lEncoder.Token.Read(token, out int kind, out uint value, out int length);
            if (kind == Vp8lEncoder.Token.Backward)
            {
                int lengthPrefix = 256 + Vp8lEncoder.PrefixOf(length, out _, out _);
                int distancePrefix = Vp8lEncoder.PrefixOf((int)value, out _, out _);
                for (int b = 0; b <= maxBits; b++)
                {
                    histograms[b].Literal[lengthPrefix]++;
                    histograms[b].Distance[distancePrefix]++;
                }

                for (int i = pos; i < pos + length; i++)
                {
                    uint q = pixels[i];
                    if (q == last)
                    {
                        continue;
                    }

                    last = q;
                    uint key = q * 0x1E35A7BDu;
                    if (newestOnly)
                    {
                        int slot = (int)(key >> shift);
                        slots[(1 << maxBits) + slot] = q;
                        written[slot] = i;
                        continue;
                    }

                    for (int b = 1; b <= maxBits; b++)
                    {
                        slots[(1 << b) + (int)(key >> (32 - b))] = q;
                    }
                }

                pos += length;
                continue;
            }

            uint p = pixels[pos];
            uint hash = p * 0x1E35A7BDu;
            histograms[0].Literal[(p >> 8) & 0xFF]++;
            histograms[0].Red[(p >> 16) & 0xFF]++;
            histograms[0].Blue[p & 0xFF]++;
            histograms[0].Alpha[p >> 24]++;
            for (int b = 1; b <= maxBits; b++)
            {
                int slot = (int)(hash >> (32 - b));
                Vp8lHistogram h = histograms[b];
                uint held = newestOnly ? Newest(slots.AsSpan(1 << maxBits), written, slot << (maxBits - b), 1 << (maxBits - b)) : slots[(1 << b) + slot];
                if (held == p)
                {
                    h.Literal[256 + Vp8lEncoder.LengthCodes + slot]++;
                }
                else
                {
                    h.Literal[(p >> 8) & 0xFF]++;
                    h.Red[(p >> 16) & 0xFF]++;
                    h.Blue[p & 0xFF]++;
                    h.Alpha[p >> 24]++;
                    if (!newestOnly)
                    {
                        slots[(1 << b) + slot] = p;
                    }
                }
            }

            if (newestOnly)
            {
                slots[(1 << maxBits) + (int)(hash >> shift)] = p;
                written[(int)(hash >> shift)] = pos;
            }

            last = p;
            pos++;
        }

        int best = 0;
        double bestBits = double.MaxValue;
        for (int b = 0; b <= maxBits; b++)
        {
            double bits = histograms[b].Bits();
            if (bits < bestBits)
            {
                (bestBits, best) = (bits, b);
            }
        }

        return best;
    }

    /// <summary>Returns what the slots from <paramref name="first"/> on, <paramref name="count"/> of them, last held: the newest written, or zero.</summary>
    private static uint Newest(ReadOnlySpan<uint> slots, ReadOnlySpan<int> written, int first, int count)
    {
        int newest = first;
        for (int i = first + 1; i < first + count; i++)
        {
            if (written[i] > written[newest])
            {
                newest = i;
            }
        }

        return written[newest] < 0 ? 0 : slots[newest];
    }

    /// <summary>Turns literals that the decoder's cache will hold into cache hits, updating the cache as the decoder does.</summary>
    internal static void Apply(Span<ulong> tokens, ReadOnlySpan<uint> pixels, int bits)
    {
        uint[] cache = new uint[1 << bits];
        uint last = 0;
        int pos = 0;
        for (int t = 0; t < tokens.Length; t++)
        {
            ulong token = tokens[t];
            Vp8lEncoder.Token.Read(token, out int kind, out _, out int length);
            if (kind == Vp8lEncoder.Token.Backward)
            {
                // The cache already holds the pixel before, so a repeat of it changes nothing.
                foreach (uint q in pixels.Slice(pos, length))
                {
                    if (q != last)
                    {
                        cache[Vp8lEncoder.CacheKey(q, bits)] = q;
                        last = q;
                    }
                }

                pos += length;
                continue;
            }

            uint p = pixels[pos++];
            int slot = Vp8lEncoder.CacheKey(p, bits);
            if (cache[slot] == p)
            {
                tokens[t] = Vp8lEncoder.Token.CacheHit(slot);
            }
            else
            {
                cache[slot] = p;
            }

            last = p;
        }
    }
}

/// <summary>
/// The prefix code groups of an entropy-coded image and which tile uses which (meta prefix codes, section 3.7.2.2). Tiles are
/// first sorted into bins by how their green, red and blue symbols spread, merged within a bin when that saves bits, then the
/// clusters are merged greedily while any pair saves bits; at efforts 5 and 6 every tile then moves to the cluster that suits it
/// best.
/// </summary>
internal sealed class Vp8lGroups
{
    private readonly Vp8lHistogram[] _histograms;
    private readonly int[] _tileGroup;
    private readonly int _tilesX;

    private Vp8lGroups(Vp8lHistogram[] histograms, int[] tileGroup, int bits, int tilesX, int tilesY)
    {
        _histograms = histograms;
        _tileGroup = tileGroup;
        Bits = bits;
        _tilesX = tilesX;
        MapWidth = tilesX;
        MapHeight = tilesY;
    }

    /// <summary>Gets the number of groups.</summary>
    internal int Count => _histograms.Length;

    /// <summary>Gets the tile size bits of the entropy image.</summary>
    internal int Bits { get; }

    internal int MapWidth { get; }

    internal int MapHeight { get; }

    /// <summary>Gets the entropy image: each tile's group number in red and green.</summary>
    internal uint[] Map
    {
        get
        {
            uint[] map = new uint[_tileGroup.Length];
            for (int i = 0; i < map.Length; i++)
            {
                map[i] = 0xFF000000u | ((uint)_tileGroup[i] << 8);
            }

            return map;
        }
    }

    /// <summary>Returns one group for the whole image.</summary>
    internal static Vp8lGroups Single(ReadOnlySpan<ulong> tokens, int cacheBits)
    {
        Vp8lHistogram histogram = new(cacheBits);
        foreach (ulong token in tokens)
        {
            histogram.Add(token);
        }

        return new Vp8lGroups([histogram], [0], 0, 1, 1);
    }

    /// <summary>Clusters the tiles of 2^<paramref name="bits"/> pixels into groups; returns one group when that is cheaper.</summary>
    internal static Vp8lGroups Cluster(ReadOnlySpan<ulong> tokens, int width, int height, int bits, int cacheBits, int effort)
    {
        int tilesX = Vp8lDecoder.SubSampleSize(width, bits);
        int tilesY = Vp8lDecoder.SubSampleSize(height, bits);
        int tiles = tilesX * tilesY;
        Vp8lGroups single = Single(tokens, cacheBits);
        if (tiles <= 1)
        {
            return single;
        }

        Vp8lHistogram[] row = new Vp8lHistogram[tilesX];
        for (int i = 0; i < tilesX; i++)
        {
            row[i] = new Vp8lHistogram(cacheBits);
        }

        // Pass 1: each tile's spread of green, red and blue, for the bins.
        float[] features = new float[tiles * 3];
        bool[] used = new bool[tiles];
        TileWalker walker = new(width, bits, tilesX);
        while (walker.NextRow(tokens, row, out int ty))
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                Vp8lHistogram h = row[tx];
                int t = (ty * tilesX) + tx;
                used[t] = !h.IsEmpty();
                features[t * 3] = (float)Vp8lCost.Bits(h.Literal);
                features[(t * 3) + 1] = (float)Vp8lCost.Bits(h.Red);
                features[(t * 3) + 2] = (float)Vp8lCost.Bits(h.Blue);
            }
        }

        int levels = effort >= 5 ? 6 : 4;
        Span<float> min = stackalloc float[3];
        Span<float> max = stackalloc float[3];
        min.Fill(float.MaxValue);
        max.Fill(float.MinValue);
        for (int t = 0; t < tiles; t++)
        {
            if (used[t])
            {
                for (int f = 0; f < 3; f++)
                {
                    min[f] = Math.Min(min[f], features[(t * 3) + f]);
                    max[f] = Math.Max(max[f], features[(t * 3) + f]);
                }
            }
        }

        // Pass 2: tiles into clusters within their bin, merged when that saves bits.
        const int PerBin = 4;
        int bins = levels * levels * levels;
        List<Vp8lHistogram> clusters = [];
        List<int>[] binClusters = new List<int>[bins];
        int[] tileCluster = new int[tiles];
        Array.Fill(tileCluster, -1);
        walker = new TileWalker(width, bits, tilesX);
        while (walker.NextRow(tokens, row, out int ty))
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int t = (ty * tilesX) + tx;
                if (!used[t])
                {
                    tileCluster[t] = -1;
                    continue;
                }

                int bin = 0;
                for (int f = 0; f < 3; f++)
                {
                    float range = max[f] - min[f];
                    int level = range <= 0 ? 0 : Math.Min(levels - 1, (int)((features[(t * 3) + f] - min[f]) / range * levels));
                    bin = (bin * levels) + level;
                }

                List<int> candidates = binClusters[bin] ??= [];
                Vp8lHistogram h = row[tx];
                int chosen = -1;
                double bestSaving = 0;
                foreach (int c in candidates)
                {
                    double saving = clusters[c].Bits() + h.Bits() - clusters[c].MergedBits(h);
                    if (saving > bestSaving || (candidates.Count >= PerBin && chosen < 0))
                    {
                        (bestSaving, chosen) = (saving, c);
                    }
                }

                if (chosen < 0)
                {
                    Vp8lHistogram cluster = new(cacheBits);
                    cluster.CopyFrom(h);
                    clusters.Add(cluster);
                    chosen = clusters.Count - 1;
                    candidates.Add(chosen);
                }
                else
                {
                    clusters[chosen].Merge(h);
                }

                tileCluster[t] = chosen;
            }
        }

        // Greedy merging of any two clusters while it saves bits.
        int[] merged = MergeGreedily(clusters);
        for (int t = 0; t < tiles; t++)
        {
            tileCluster[t] = tileCluster[t] < 0 ? -1 : merged[tileCluster[t]];
        }

        List<Vp8lHistogram> groups = [];
        int[] renumber = new int[clusters.Count];
        Array.Fill(renumber, -1);
        if (effort >= 5)
        {
            Remap(tokens, width, bits, tilesX, row, clusters, merged, tileCluster);
        }

        // Number the groups in order of first use, and rebuild their counts from the final assignment.
        for (int t = 0; t < tiles; t++)
        {
            int c = tileCluster[t];
            if (c < 0)
            {
                tileCluster[t] = 0;
                continue;
            }

            if (renumber[c] < 0)
            {
                renumber[c] = groups.Count;
                groups.Add(new Vp8lHistogram(cacheBits));
            }

            tileCluster[t] = renumber[c];
        }

        if (groups.Count <= 1)
        {
            return single;
        }

        walker = new TileWalker(width, bits, tilesX);
        while (walker.NextRow(tokens, row, out int ty))
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                if (used[(ty * tilesX) + tx])
                {
                    groups[tileCluster[(ty * tilesX) + tx]].Merge(row[tx]);
                }
            }
        }

        double groupBits = 0;
        foreach (Vp8lHistogram g in groups)
        {
            groupBits += g.Bits();
        }

        // The entropy image costs about a few bits a tile plus its own codes.
        double mapBits = 200 + (tiles * Math.Max(1, Math.Log2(groups.Count)) * 0.6);
        return groupBits + mapBits < single._histograms[0].Bits() ? new Vp8lGroups([.. groups], tileCluster, bits, tilesX, tilesY) : single;
    }

    /// <summary>Merges clusters pairwise, the pair saving most first, until no pair saves bits; returns each cluster's final index.</summary>
    private static int[] MergeGreedily(List<Vp8lHistogram> clusters)
    {
        int n = clusters.Count;
        int[] target = new int[n];
        for (int i = 0; i < n; i++)
        {
            target[i] = i;
        }

        if (n <= 1)
        {
            return target;
        }

        bool[] alive = new bool[n];
        Array.Fill(alive, true);
        double[] saving = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                saving[(i * n) + j] = clusters[i].Bits() + clusters[j].Bits() - clusters[i].MergedBits(clusters[j]);
            }
        }

        while (true)
        {
            int bi = -1, bj = -1;
            double best = 0;
            for (int i = 0; i < n; i++)
            {
                if (!alive[i])
                {
                    continue;
                }

                for (int j = i + 1; j < n; j++)
                {
                    if (alive[j] && saving[(i * n) + j] > best)
                    {
                        (best, bi, bj) = (saving[(i * n) + j], i, j);
                    }
                }
            }

            if (bi < 0)
            {
                break;
            }

            clusters[bi].Merge(clusters[bj]);
            alive[bj] = false;
            for (int k = 0; k < n; k++)
            {
                if (target[k] == bj)
                {
                    target[k] = bi;
                }
            }

            for (int k = 0; k < n; k++)
            {
                if (alive[k] && k != bi)
                {
                    (int a, int b) = k < bi ? (k, bi) : (bi, k);
                    saving[(a * n) + b] = clusters[bi].Bits() + clusters[k].Bits() - clusters[bi].MergedBits(clusters[k]);
                }
            }
        }

        return target;
    }

    /// <summary>Moves each tile to the surviving cluster that its symbols cost least in.</summary>
    private static void Remap(ReadOnlySpan<ulong> tokens, int width, int bits, int tilesX, Vp8lHistogram[] row, List<Vp8lHistogram> clusters, int[] merged, int[] tileCluster)
    {
        List<int> alive = [];
        for (int i = 0; i < merged.Length; i++)
        {
            if (merged[i] == i)
            {
                alive.Add(i);
            }
        }

        if (alive.Count <= 1)
        {
            return;
        }

        TileWalker walker = new(width, bits, tilesX);
        while (walker.NextRow(tokens, row, out int ty))
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int t = (ty * tilesX) + tx;
                if (tileCluster[t] < 0)
                {
                    continue;
                }

                Vp8lHistogram h = row[tx];
                int best = tileCluster[t];
                double bestCost = double.MaxValue;
                foreach (int c in alive)
                {
                    double cost = clusters[c].MergedBits(h) - clusters[c].Bits();
                    if (cost < bestCost)
                    {
                        (bestCost, best) = (cost, c);
                    }
                }

                tileCluster[t] = best;
            }
        }
    }

    /// <summary>Writes every group's five codes, then the tokens, each with the codes of the tile it starts in.</summary>
    internal void Write(Vp8lBitWriter w, ReadOnlySpan<ulong> tokens, int width)
    {
        int groups = _histograms.Length;
        int green = _histograms[0].Literal.Length;
        int stride = green + (256 * 3) + Vp8lEncoder.DistanceCodes;
        byte[] lengthArray = ArrayPool<byte>.Shared.Rent(groups * stride);
        ushort[] codeArray = ArrayPool<ushort>.Shared.Rent(groups * stride);
        try
        {
            Span<byte> lengths = lengthArray.AsSpan(0, groups * stride);
            Span<ushort> codes = codeArray.AsSpan(0, groups * stride);
            for (int g = 0; g < groups; g++)
            {
                Vp8lHistogram h = _histograms[g];
                int at = g * stride;
                foreach (uint[] counts in (ReadOnlySpan<uint[]>)[h.Literal, h.Red, h.Blue, h.Alpha, h.Distance])
                {
                    Span<byte> l = lengths.Slice(at, counts.Length);
                    Span<ushort> c = codes.Slice(at, counts.Length);
                    Vp8lPrefixCode.BuildLengths(counts, l, Vp8lPrefixCode.MaxLength);
                    Vp8lPrefixCode.Write(w, l);
                    if (Vp8lPrefixCode.IsSingle(l))
                    {
                        // A code of one symbol takes no bits per symbol.
                        l.Clear();
                    }

                    Vp8lPrefixCode.BuildCodes(l, c);
                    at += counts.Length;
                }
            }

            int redAt = green;
            int blueAt = green + 256;
            int alphaAt = green + 512;
            int distanceAt = green + 768;
            int mask = Bits == 0 ? 0 : (1 << Bits) - 1;
            int x = 0;
            int y = 0;
            foreach (ulong token in tokens)
            {
                int group = Bits == 0 ? 0 : _tileGroup[((y >> Bits) * _tilesX) + (x >> Bits)];
                int g = group * stride;
                Vp8lEncoder.Token.Read(token, out int kind, out uint value, out int length);
                switch (kind)
                {
                    case Vp8lEncoder.Token.Literal:
                        int gs = g + (int)((value >> 8) & 0xFF);
                        int rs = g + redAt + (int)((value >> 16) & 0xFF);
                        int bs = g + blueAt + (int)(value & 0xFF);
                        int as_ = g + alphaAt + (int)(value >> 24);
                        w.Write(codes[gs], lengths[gs]);
                        w.Write(codes[rs], lengths[rs]);
                        w.Write(codes[bs], lengths[bs]);
                        w.Write(codes[as_], lengths[as_]);
                        length = 1;
                        break;
                    case Vp8lEncoder.Token.Cached:
                        int cs = g + 256 + Vp8lEncoder.LengthCodes + (int)value;
                        w.Write(codes[cs], lengths[cs]);
                        length = 1;
                        break;
                    default:
                        int ls = g + 256 + Vp8lEncoder.PrefixOf(length, out int extra, out int extraBits);
                        w.Write(codes[ls], lengths[ls]);
                        w.Write((uint)extra, extraBits);
                        int ds = g + distanceAt + Vp8lEncoder.PrefixOf((int)value, out extra, out extraBits);
                        w.Write(codes[ds], lengths[ds]);
                        w.Write((uint)extra, extraBits);
                        break;
                }

                x += length;
                while (x >= width)
                {
                    x -= width;
                    y++;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthArray);
            ArrayPool<ushort>.Shared.Return(codeArray);
        }
    }

    /// <summary>Walks a token stream one row of tiles at a time, counting each token in the histogram of the tile it starts in.</summary>
    private struct TileWalker(int width, int bits, int tilesX)
    {
        private int _token;
        private long _pos;

        public bool NextRow(ReadOnlySpan<ulong> tokens, Vp8lHistogram[] row, out int tileRow)
        {
            tileRow = (int)((_pos / width) >> bits);
            if (_token >= tokens.Length)
            {
                return false;
            }

            for (int i = 0; i < tilesX; i++)
            {
                row[i].Clear();
            }

            long end = (long)((tileRow + 1) << bits) * width;
            while (_token < tokens.Length && _pos < end)
            {
                ulong token = tokens[_token++];
                int x = (int)(_pos % width);
                row[x >> bits].Add(token);
                _pos += Vp8lEncoder.Token.Pixels(token);
            }

            return true;
        }
    }
}
