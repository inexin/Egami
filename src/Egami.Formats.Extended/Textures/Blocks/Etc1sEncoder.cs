namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>One 2D image an ETC1S encode takes: RGBA pixels in rows, and the mip level it belongs to.</summary>
internal sealed record Etc1sImage(byte[] Rgba, int Width, int Height, int Level);

/// <summary>What an ETC1S encode makes: the BasisLZ global data and each mip level's slices, as KTX 2 stores them.</summary>
internal sealed record Etc1sEncoded(byte[] GlobalData, byte[][] Levels, int EndpointCount, int SelectorCount);

/// <summary>
/// Encodes images to Basis Universal ETC1S in BasisLZ form (KTX 2.0 Specification, Appendices C and D) at a quality of 1 to 255
/// and an effort of 0 to 6. Every block of every image (and of the alpha channel, as gray blocks, when there is alpha) shares
/// one codebook of endpoints (a 5-bit colour and an intensity table) and one of selectors (16 two-bit modifier choices); the
/// quality sets their sizes, from 16 entries each at 1 to 16128 at 255. Endpoints are found by vector quantization of each
/// block's mean colour and spread (splitting the cluster with the largest error, then Lloyd iterations as effort allows), then
/// fitted to their blocks' pixels; selectors are quantized the same way from each block's best modifiers under its endpoint,
/// then refitted texel by texel to their blocks' pixels and reassigned by real error. The slices then code each block's two
/// indices with BasisLZ's predictions, history buffer and run lengths, and the Huffman tables are built from the counts.
/// Every step runs in a fixed order or sums integers, so the output does not depend on the number of threads.
/// </summary>
internal static class Etc1sEncoder
{
    private const int MaxCodebook = 16128;
    private const int HistorySize = 64;
    private const int PredRepeatSymbol = 256;

    /// <summary>Returns the codebook size quality <paramref name="quality"/> (1 to 255) asks for, before the number of blocks caps it.</summary>
    public static int CodebookSize(int quality) => (int)Math.Min(MaxCodebook, Math.Round(Math.Pow(2, 4 + (9.98 * (Math.Clamp(quality, 1, 255) - 1) / 254))));

    /// <summary>
    /// Encodes <paramref name="images"/>, in KTX 2's order (mip level, then layer, face and depth slice), to ETC1S; with
    /// <paramref name="alpha"/> each image also gets an alpha slice. <paramref name="threads"/> caps the threads (−1: no cap).
    /// </summary>
    public static Etc1sEncoded Encode(IReadOnlyList<Etc1sImage> images, bool alpha, int quality, int effort, int threads = -1)
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = threads };
        effort = Math.Clamp(effort, 0, 6);
        List<(int BlocksX, int BlocksY, int Rgb, int Alpha)> layout = [];
        int total = 0;
        foreach (Etc1sImage image in images)
        {
            int bx = (image.Width + 3) / 4;
            int by = (image.Height + 3) / 4;
            int rgb = total;
            total += bx * by;
            int a = -1;
            if (alpha)
            {
                a = total;
                total += bx * by;
            }

            layout.Add((bx, by, rgb, a));
        }

        // Every block's pixels, 48 bytes of RGB (an alpha block as gray), edges repeated into partial blocks.
        byte[] pixels = new byte[total * 48];
        for (int i = 0; i < images.Count; i++)
        {
            Gather(images[i], layout[i].BlocksX, layout[i].BlocksY, layout[i].Rgb, false, pixels);
            if (alpha)
            {
                Gather(images[i], layout[i].BlocksX, layout[i].BlocksY, layout[i].Alpha, true, pixels);
            }
        }

        Codebooks books = Quantize(pixels, total, CodebookSize(quality), effort, options);
        return Write(books, layout, images);
    }

    /// <summary>The codebooks and each block's two indices.</summary>
    private sealed record Codebooks(Endpoint[] Endpoints, uint[] Selectors, int[] BlockEndpoint, int[] BlockSelector);

    /// <summary>An ETC1S endpoint: a colour of 5 bits a channel and an intensity table.</summary>
    private readonly record struct Endpoint(byte R, byte G, byte B, byte Inten);

    private static void Gather(Etc1sImage image, int blocksX, int blocksY, int first, bool alpha, byte[] pixels)
    {
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int at = (first + (by * blocksX) + bx) * 48;
                for (int t = 0; t < 16; t++)
                {
                    int x = Math.Min((bx * 4) + (t & 3), image.Width - 1);
                    int y = Math.Min((by * 4) + (t >> 2), image.Height - 1);
                    int p = ((y * image.Width) + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        pixels[at + (t * 3) + c] = alpha ? image.Rgba[p + 3] : image.Rgba[p + c];
                    }
                }
            }
        }
    }

    /// <summary>Fills the four colours of an endpoint, 3 values each: selectors 0 to 3 are −b, −a, +a and +b.</summary>
    private static void Palette(Endpoint e, Span<int> palette)
    {
        int a = Etc.Modifiers[e.Inten * 2];
        int b = Etc.Modifiers[(e.Inten * 2) + 1];
        int r = (e.R << 3) | (e.R >> 2);
        int g = (e.G << 3) | (e.G >> 2);
        int bl = (e.B << 3) | (e.B >> 2);
        Span<int> mods = [-b, -a, a, b];
        for (int s = 0; s < 4; s++)
        {
            palette[s * 3] = Math.Clamp(r + mods[s], 0, 255);
            palette[(s * 3) + 1] = Math.Clamp(g + mods[s], 0, 255);
            palette[(s * 3) + 2] = Math.Clamp(bl + mods[s], 0, 255);
        }
    }

    private static int TexelError(ReadOnlySpan<byte> px, int t, ReadOnlySpan<int> palette, int s)
    {
        int dr = px[t * 3] - palette[s * 3];
        int dg = px[(t * 3) + 1] - palette[(s * 3) + 1];
        int db = px[(t * 3) + 2] - palette[(s * 3) + 2];
        return (dr * dr) + (dg * dg) + (db * db);
    }

    /// <summary>Returns a block's error under an endpoint with each texel's best selector, and those selectors (2 bits each, texel 0 lowest).</summary>
    private static long BestSelectors(ReadOnlySpan<byte> px, ReadOnlySpan<int> palette, out uint selectors)
    {
        long error = 0;
        selectors = 0;
        for (int t = 0; t < 16; t++)
        {
            int best = int.MaxValue;
            int pick = 0;
            for (int s = 0; s < 4; s++)
            {
                int e = TexelError(px, t, palette, s);
                if (e < best)
                {
                    best = e;
                    pick = s;
                }
            }

            error += best;
            selectors |= (uint)pick << (t * 2);
        }

        return error;
    }

    /// <summary>Returns a block's error under an endpoint with fixed selectors.</summary>
    private static long FixedError(ReadOnlySpan<byte> px, ReadOnlySpan<int> palette, uint selectors)
    {
        long error = 0;
        for (int t = 0; t < 16; t++)
        {
            error += TexelError(px, t, palette, (int)((selectors >> (t * 2)) & 3));
        }

        return error;
    }

    private static Codebooks Quantize(byte[] pixels, int blocks, int size, int effort, ParallelOptions options)
    {
        // Endpoint features: the mean colour (as sums of 16 texels) and the spread, as the small modifier of the block's best table.
        int[] features = new int[blocks * 4];
        Parallel.For(0, blocks, options, i =>
        {
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            Span<int> sum = stackalloc int[3];
            for (int t = 0; t < 16; t++)
            {
                sum[0] += px[t * 3];
                sum[1] += px[(t * 3) + 1];
                sum[2] += px[(t * 3) + 2];
            }

            Span<int> palette = stackalloc int[12];
            long best = long.MaxValue;
            int inten = 0;
            for (int t = 0; t < 8; t++)
            {
                Palette(new Endpoint(Q5(sum[0]), Q5(sum[1]), Q5(sum[2]), (byte)t), palette);
                long e = BestSelectors(px, palette, out _);
                if (e < best)
                {
                    best = e;
                    inten = t;
                }
            }

            features[i * 4] = sum[0];
            features[(i * 4) + 1] = sum[1];
            features[(i * 4) + 2] = sum[2];
            features[(i * 4) + 3] = Etc.Modifiers[inten * 2] * 32;
        });

        int endpointCount = Math.Min(size, blocks);
        int[] blockEndpoint = Vq(features, blocks, 4, endpointCount, effort switch { 0 => 0, <= 2 => 1, <= 4 => 2, 5 => 4, _ => 8 }, options, out int clusters);

        // Fit each endpoint to its blocks' pixels.
        List<int>[] members = Members(blockEndpoint, clusters);
        Endpoint[] endpoints = new Endpoint[clusters];
        Parallel.For(0, clusters, options, k => endpoints[k] = FitEndpoint(pixels, members[k], effort));

        // Each block's best selectors under its endpoint, quantized to the selector codebook.
        uint[] ideal = new uint[blocks];
        Parallel.For(0, blocks, options, i =>
        {
            Span<int> palette = stackalloc int[12];
            Palette(endpoints[blockEndpoint[i]], palette);
            BestSelectors(pixels.AsSpan(i * 48, 48), palette, out ideal[i]);
        });

        int[] selectorFeatures = new int[blocks * 16];
        for (int i = 0; i < blocks; i++)
        {
            for (int t = 0; t < 16; t++)
            {
                selectorFeatures[(i * 16) + t] = (int)((ideal[i] >> (t * 2)) & 3) * 16;
            }
        }

        int selectorCount = Math.Min(size, blocks);
        int[] blockSelector = Vq(selectorFeatures, blocks, 16, selectorCount, effort switch { <= 1 => 0, <= 4 => 1, 5 => 2, _ => 4 }, options, out int selectorClusters);
        uint[] selectors = new uint[selectorClusters];

        // Refit selectors texel by texel to their blocks, then reassign blocks by real error where the budget allows, and repeat.
        long budget = effort switch { <= 1 => 0, <= 3 => 1L << 26, 4 => 1L << 28, 5 => 1L << 30, _ => 1L << 32 };
        int rounds = effort switch { <= 1 => 1, <= 4 => 2, 5 => 3, _ => 4 };
        for (int round = 0; round < rounds; round++)
        {
            members = Members(blockSelector, selectorClusters);
            Parallel.For(0, selectorClusters, options, k => selectors[k] = FitSelector(pixels, members[k], blockEndpoint, endpoints, ideal));
            if ((long)blocks * selectorClusters > budget)
            {
                break;
            }

            Parallel.For(0, blocks, options, i =>
            {
                Span<int> palette = stackalloc int[12];
                Palette(endpoints[blockEndpoint[i]], palette);
                ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
                long best = FixedError(px, palette, selectors[blockSelector[i]]);
                for (int k = 0; k < selectors.Length; k++)
                {
                    long e = FixedError(px, palette, selectors[k]);
                    if (e < best)
                    {
                        best = e;
                        blockSelector[i] = k;
                    }
                }
            });
        }

        // With the selectors settled, refit each endpoint to them.
        if (effort >= 2)
        {
            members = Members(blockEndpoint, clusters);
            Parallel.For(0, clusters, options, k => endpoints[k] = RefitEndpoint(pixels, members[k], endpoints[k], blockSelector, selectors));
        }

        return Compact(endpoints, selectors, blockEndpoint, blockSelector);
    }

    private static byte Q5(int sum16) => (byte)Math.Clamp(((sum16 * 31) + (255 * 8)) / (255 * 16), 0, 31);

    private static List<int>[] Members(int[] assignment, int clusters)
    {
        List<int>[] members = new List<int>[clusters];
        for (int k = 0; k < clusters; k++)
        {
            members[k] = [];
        }

        for (int i = 0; i < assignment.Length; i++)
        {
            members[assignment[i]].Add(i);
        }

        return members;
    }

    /// <summary>
    /// Vector quantization of <paramref name="n"/> integer vectors of <paramref name="dims"/> values into at most
    /// <paramref name="k"/> clusters: the cluster with the largest error splits at its mean along its widest dimension until
    /// there are <paramref name="k"/>, then <paramref name="iterations"/> Lloyd iterations move vectors to their nearest centroid.
    /// Returns each vector's cluster.
    /// </summary>
    internal static int[] Vq(int[] features, int n, int dims, int k, int iterations, ParallelOptions options, out int clusters)
    {
        int[] assignment = new int[n];
        List<int[]> groups = [Enumerable.Range(0, n).ToArray()];
        PriorityQueue<int, (long, int)> queue = new();
        queue.Enqueue(0, (-Spread(features, groups[0], dims, out _, out _), 0));
        while (groups.Count < k && queue.Count > 0)
        {
            queue.TryDequeue(out int g, out (long Error, int Order) key);
            if (key.Error == 0)
            {
                break;
            }

            int[] group = groups[g];
            Spread(features, group, dims, out int dim, out long mean);
            int[] left = [.. group.Where(i => (long)features[(i * dims) + dim] * group.Length < mean)];
            int[] right = [.. group.Where(i => (long)features[(i * dims) + dim] * group.Length >= mean)];
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            groups[g] = left;
            groups.Add(right);
            queue.Enqueue(g, (-Spread(features, left, dims, out _, out _), g));
            queue.Enqueue(groups.Count - 1, (-Spread(features, right, dims, out _, out _), groups.Count - 1));
        }

        for (int g = 0; g < groups.Count; g++)
        {
            foreach (int i in groups[g])
            {
                assignment[i] = g;
            }
        }

        clusters = groups.Count;
        for (int iteration = 0; iteration < iterations && clusters > 1; iteration++)
        {
            long[] sums = new long[clusters * dims];
            int[] counts = new int[clusters];
            for (int i = 0; i < n; i++)
            {
                int c = assignment[i];
                counts[c]++;
                for (int d = 0; d < dims; d++)
                {
                    sums[(c * dims) + d] += features[(i * dims) + d];
                }
            }

            int[] centroids = new int[clusters * dims];
            int live = 0;
            for (int c = 0; c < clusters; c++)
            {
                if (counts[c] == 0)
                {
                    continue;
                }

                for (int d = 0; d < dims; d++)
                {
                    centroids[(live * dims) + d] = (int)((sums[(c * dims) + d] + (counts[c] / 2)) / counts[c]);
                }

                live++;
            }

            int changed = 0;
            int[] next = new int[n];
            Parallel.For(0, n, options, () => 0, (i, _, local) =>
            {
                long best = long.MaxValue;
                int pick = 0;
                for (int c = 0; c < live; c++)
                {
                    long distance = 0;
                    for (int d = 0; d < dims && distance < best; d++)
                    {
                        long v = features[(i * dims) + d] - centroids[(c * dims) + d];
                        distance += v * v;
                    }

                    if (distance < best)
                    {
                        best = distance;
                        pick = c;
                    }
                }

                next[i] = pick;
                return local + (pick != assignment[i] ? 1 : 0);
            }, local => Interlocked.Add(ref changed, local));

            assignment = next;
            clusters = Renumber(assignment, live);
            if (changed == 0)
            {
                break;
            }
        }

        return assignment;
    }

    /// <summary>Renumbers clusters so the used ones are 0 to count − 1 in order; returns the count.</summary>
    private static int Renumber(int[] assignment, int clusters)
    {
        int[] map = new int[clusters];
        Array.Fill(map, -1);
        int count = 0;
        for (int c = 0; c < clusters; c++)
        {
            map[c] = -1;
        }

        bool[] used = new bool[clusters];
        foreach (int c in assignment)
        {
            used[c] = true;
        }

        for (int c = 0; c < clusters; c++)
        {
            if (used[c])
            {
                map[c] = count++;
            }
        }

        for (int i = 0; i < assignment.Length; i++)
        {
            assignment[i] = map[assignment[i]];
        }

        return count;
    }

    /// <summary>Returns a group's squared error about its mean, its widest dimension, and that dimension's sum.</summary>
    private static long Spread(int[] features, int[] group, int dims, out int widest, out long mean)
    {
        widest = 0;
        mean = 0;
        long total = 0;
        double widestVariance = -1;
        for (int d = 0; d < dims; d++)
        {
            long sum = 0;
            long squares = 0;
            foreach (int i in group)
            {
                long v = features[(i * dims) + d];
                sum += v;
                squares += v * v;
            }

            long variance = squares - (sum * sum / Math.Max(group.Length, 1));
            total += variance;
            if (variance > widestVariance)
            {
                widestVariance = variance;
                widest = d;
                mean = sum;
            }
        }

        return total;
    }

    /// <summary>Finds the endpoint with the least error over a cluster's blocks, each texel taking its best selector.</summary>
    private static Endpoint FitEndpoint(byte[] pixels, List<int> blocks, int effort)
    {
        Span<long> sum = stackalloc long[3];
        foreach (int i in blocks)
        {
            for (int t = 0; t < 16; t++)
            {
                for (int c = 0; c < 3; c++)
                {
                    sum[c] += pixels[(i * 48) + (t * 3) + c];
                }
            }
        }

        long count = blocks.Count * 16L;
        Span<int> palette = stackalloc int[12];
        Endpoint best = default;
        long bestError = long.MaxValue;
        for (int inten = 0; inten < 8; inten++)
        {
            Endpoint e = new(Q5Mean(sum[0], count), Q5Mean(sum[1], count), Q5Mean(sum[2], count), (byte)inten);
            long error = Error(pixels, blocks, e, palette);
            if (error < bestError)
            {
                bestError = error;
                best = e;
            }
        }

        if (effort >= 3)
        {
            // Try the neighbouring colours of the best one.
            Endpoint centre = best;
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dg = -1; dg <= 1; dg++)
                {
                    for (int db = -1; db <= 1; db++)
                    {
                        int r = centre.R + dr, g = centre.G + dg, b = centre.B + db;
                        if ((dr | dg | db) == 0 || (uint)r > 31 || (uint)g > 31 || (uint)b > 31)
                        {
                            continue;
                        }

                        Endpoint e = new((byte)r, (byte)g, (byte)b, centre.Inten);
                        long error = Error(pixels, blocks, e, palette);
                        if (error < bestError)
                        {
                            bestError = error;
                            best = e;
                        }
                    }
                }
            }
        }

        return best;

        static long Error(byte[] pixels, List<int> blocks, Endpoint e, Span<int> palette)
        {
            Palette(e, palette);
            long error = 0;
            foreach (int i in blocks)
            {
                error += BestSelectors(pixels.AsSpan(i * 48, 48), palette, out _);
            }

            return error;
        }
    }

    private static byte Q5Mean(long sum, long count) => (byte)Math.Clamp(((sum * 31) + (count * 255 / 2)) / (count * 255), 0, 31);

    /// <summary>Refits an endpoint to its blocks with their selectors fixed: for each table the mean of texel minus modifier, and its neighbours.</summary>
    private static Endpoint RefitEndpoint(byte[] pixels, List<int> blocks, Endpoint current, int[] blockSelector, uint[] selectors)
    {
        Span<int> palette = stackalloc int[12];
        Endpoint best = current;
        long bestError = Error(current, palette);
        Span<long> sum = stackalloc long[3];
        Span<int> mods = stackalloc int[4];
        for (int inten = 0; inten < 8; inten++)
        {
            int a = Etc.Modifiers[inten * 2];
            int b = Etc.Modifiers[(inten * 2) + 1];
            mods[0] = -b;
            mods[1] = -a;
            mods[2] = a;
            mods[3] = b;
            sum.Clear();
            foreach (int i in blocks)
            {
                uint s = selectors[blockSelector[i]];
                for (int t = 0; t < 16; t++)
                {
                    int m = mods[(int)((s >> (t * 2)) & 3)];
                    for (int c = 0; c < 3; c++)
                    {
                        sum[c] += pixels[(i * 48) + (t * 3) + c] - m;
                    }
                }
            }

            long count = blocks.Count * 16L;
            int r0 = Q5Mean(Math.Max(sum[0], 0), count), g0 = Q5Mean(Math.Max(sum[1], 0), count), b0 = Q5Mean(Math.Max(sum[2], 0), count);
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dg = -1; dg <= 1; dg++)
                {
                    for (int db = -1; db <= 1; db++)
                    {
                        int r = r0 + dr, g = g0 + dg, bl = b0 + db;
                        if ((uint)r > 31 || (uint)g > 31 || (uint)bl > 31)
                        {
                            continue;
                        }

                        Endpoint e = new((byte)r, (byte)g, (byte)bl, (byte)inten);
                        long error = Error(e, palette);
                        if (error < bestError)
                        {
                            bestError = error;
                            best = e;
                        }
                    }
                }
            }
        }

        return best;

        long Error(Endpoint e, Span<int> palette)
        {
            Palette(e, palette);
            long error = 0;
            foreach (int i in blocks)
            {
                error += FixedError(pixels.AsSpan(i * 48, 48), palette, selectors[blockSelector[i]]);
            }

            return error;
        }
    }

    /// <summary>Fits a selector texel by texel: each texel takes the modifier with the least error summed over the cluster's blocks.</summary>
    private static uint FitSelector(byte[] pixels, List<int> blocks, int[] blockEndpoint, Endpoint[] endpoints, uint[] ideal)
    {
        if (blocks.Count == 0)
        {
            return 0;
        }

        if (blocks.Count == 1)
        {
            return ideal[blocks[0]];
        }

        Span<long> errors = stackalloc long[64];
        errors.Clear();
        Span<int> palette = stackalloc int[12];
        foreach (int i in blocks)
        {
            Palette(endpoints[blockEndpoint[i]], palette);
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            for (int t = 0; t < 16; t++)
            {
                for (int s = 0; s < 4; s++)
                {
                    errors[(t * 4) + s] += TexelError(px, t, palette, s);
                }
            }
        }

        uint selector = 0;
        for (int t = 0; t < 16; t++)
        {
            int pick = 0;
            for (int s = 1; s < 4; s++)
            {
                if (errors[(t * 4) + s] < errors[(t * 4) + pick])
                {
                    pick = s;
                }
            }

            selector |= (uint)pick << (t * 2);
        }

        return selector;
    }

    /// <summary>Merges identical codebook entries, drops unused ones and sorts both codebooks so their deltas code small.</summary>
    private static Codebooks Compact(Endpoint[] endpoints, uint[] selectors, int[] blockEndpoint, int[] blockSelector)
    {
        int[] endpointMap = Sort(endpoints, blockEndpoint, e => (e.G << 24) | (e.R << 16) | (e.B << 8) | e.Inten, out Endpoint[] sortedEndpoints);
        int[] selectorMap = Sort(selectors, blockSelector, s => s, out uint[] sortedSelectors);
        int[] be = [.. blockEndpoint.Select(e => endpointMap[e])];
        int[] bs = [.. blockSelector.Select(s => selectorMap[s])];
        return new Codebooks(sortedEndpoints, sortedSelectors, be, bs);

        static int[] Sort<T, TKey>(T[] entries, int[] used, Func<T, TKey> key, out T[] sorted)
            where T : notnull
            where TKey : IComparable<TKey>
        {
            bool[] live = new bool[entries.Length];
            foreach (int u in used)
            {
                live[u] = true;
            }

            List<T> distinct = [.. entries.Where((_, i) => live[i]).Distinct().OrderBy(key)];
            Dictionary<T, int> index = [];
            for (int i = 0; i < distinct.Count; i++)
            {
                index[distinct[i]] = i;
            }

            sorted = [.. distinct];
            int[] map = new int[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                map[i] = live[i] ? index[entries[i]] : 0;
            }

            return map;
        }
    }

    /// <summary>Codes the codebooks and slices into BasisLZ global data and level data.</summary>
    private static Etc1sEncoded Write(Codebooks books, List<(int BlocksX, int BlocksY, int Rgb, int Alpha)> layout, IReadOnlyList<Etc1sImage> images)
    {
        byte[] endpoints = WriteEndpoints(books.Endpoints);
        byte[] selectors = WriteSelectors(books.Selectors);

        // Slices: first the symbols, with the decoder's state followed exactly, then the codes built from their counts.
        List<List<Symbol>> slices = [];
        long[] predCounts = new long[PredRepeatSymbol + 1];
        long[] deltaCounts = new long[books.Endpoints.Length];
        long[] selectorCounts = new long[books.Selectors.Length + HistorySize + 1];
        long[] rleCounts = new long[64];
        foreach ((int bx, int by, int rgb, int alpha) in layout)
        {
            slices.Add(Slice(books, rgb, bx, by));
            if (alpha >= 0)
            {
                slices.Add(Slice(books, alpha, bx, by));
            }
        }

        foreach (List<Symbol> slice in slices)
        {
            foreach (Symbol s in slice)
            {
                long[]? counts = s.Table switch { 0 => predCounts, 1 => deltaCounts, 2 => selectorCounts, 3 => rleCounts, _ => null };
                if (counts is not null)
                {
                    counts[s.Value]++;
                }
            }
        }

        BasisHuffmanCode[] codes = [new(predCounts), new(deltaCounts), new(selectorCounts), new(rleCounts)];
        BasisBitWriter tableBits = new();
        foreach (BasisHuffmanCode code in codes)
        {
            code.WriteTable(tableBits);
        }

        tableBits.Write(HistorySize, 13);
        byte[] tables = tableBits.ToArray();

        int levelCount = images.Count == 0 ? 0 : images.Max(i => i.Level) + 1;
        List<byte>[] levels = new List<byte>[levelCount];
        for (int l = 0; l < levelCount; l++)
        {
            levels[l] = [];
        }

        List<BasisImage> descriptors = [];
        int next = 0;
        for (int i = 0; i < images.Count; i++)
        {
            List<byte> level = levels[images[i].Level];
            byte[] rgb = Code(slices[next++], codes);
            uint rgbOffset = (uint)level.Count;
            level.AddRange(rgb);
            uint alphaOffset = 0;
            uint alphaLength = 0;
            if (layout[i].Alpha >= 0)
            {
                byte[] a = Code(slices[next++], codes);
                alphaOffset = (uint)level.Count;
                alphaLength = (uint)a.Length;
                level.AddRange(a);
            }

            descriptors.Add(new BasisImage(false, rgbOffset, (uint)rgb.Length, alphaOffset, alphaLength));
        }

        byte[] global = BasisLzLayout.GlobalData(books.Endpoints.Length, books.Selectors.Length, descriptors, endpoints, selectors, tables);
        return new Etc1sEncoded(global, [.. levels.Select(l => l.ToArray())], books.Endpoints.Length, books.Selectors.Length);
    }

    /// <summary>One coded item of a slice: a Huffman symbol of table 0 to 3, or (table −1) raw VLC bits.</summary>
    private readonly record struct Symbol(int Table, int Value, int VlcBits = 0);

    private static byte[] Code(List<Symbol> slice, BasisHuffmanCode[] codes)
    {
        BasisBitWriter bits = new();
        foreach (Symbol s in slice)
        {
            if (s.Table < 0)
            {
                bits.Vlc((uint)s.Value, s.VlcBits);
            }
            else
            {
                codes[s.Table].Write(bits, s.Value);
            }
        }

        return bits.ToArray();
    }

    /// <summary>Chooses each block's predictions and selector symbols, as the decoder (Appendix D.5) will read them.</summary>
    private static List<Symbol> Slice(Codebooks books, int first, int blocksX, int blocksY)
    {
        List<Symbol> symbols = [];
        int selectorCount = books.Selectors.Length;
        int[] history = new int[HistorySize];
        int rover = HistorySize / 2;
        int prevEndpoint = 0;
        int prevPredSymbol = 0;
        int[] preds = new int[blocksX * blocksY];

        // Each block's prediction: 0 left, 1 above, 2 upper left, 3 a coded delta.
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int e = books.BlockEndpoint[first + (by * blocksX) + bx];
                int pred = 3;
                if (bx > 0 && books.BlockEndpoint[first + (by * blocksX) + bx - 1] == e)
                {
                    pred = 0;
                }
                else if (by > 0 && books.BlockEndpoint[first + ((by - 1) * blocksX) + bx] == e)
                {
                    pred = 1;
                }
                else if (bx > 0 && by > 0 && books.BlockEndpoint[first + ((by - 1) * blocksX) + bx - 1] == e)
                {
                    pred = 2;
                }

                preds[(by * blocksX) + bx] = pred;
            }
        }

        // The 2 × 2 groups' symbols, in the order the decoder reads them (on even block rows), with runs of repeats.
        List<int> groups = [];
        for (int by = 0; by < blocksY; by += 2)
        {
            for (int bx = 0; bx < blocksX; bx += 2)
            {
                int symbol = 0;
                for (int k = 0; k < 4; k++)
                {
                    int x = bx + (k & 1);
                    int y = by + (k >> 1);
                    int pred = x < blocksX && y < blocksY ? preds[(y * blocksX) + x] : 0;
                    symbol |= pred << (k * 2);
                }

                groups.Add(symbol);
            }
        }

        // The block loop: group symbols where the decoder reads them, then each block's delta and selector.
        int groupIndex = 0;
        int repeat = 0;
        int rle = 0;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                if ((bx & 1) == 0 && (by & 1) == 0)
                {
                    int symbol = groups[groupIndex];
                    if (repeat > 0)
                    {
                        repeat--;
                    }
                    else
                    {
                        int run = 0;
                        while (groupIndex + run < groups.Count && groups[groupIndex + run] == prevPredSymbol && run < 1 << 20)
                        {
                            run++;
                        }

                        if (run >= 3 && groupIndex > 0)
                        {
                            symbols.Add(new Symbol(0, PredRepeatSymbol));
                            symbols.Add(new Symbol(-1, run - 3, 4));
                            repeat = run - 1;
                        }
                        else
                        {
                            symbols.Add(new Symbol(0, symbol));
                            prevPredSymbol = symbol;
                        }
                    }

                    groupIndex++;
                }

                int block = first + (by * blocksX) + bx;
                int endpoint = books.BlockEndpoint[block];
                if (preds[(by * blocksX) + bx] == 3)
                {
                    int delta = endpoint - prevEndpoint;
                    if (delta < 0)
                    {
                        delta += books.Endpoints.Length;
                    }

                    symbols.Add(new Symbol(1, delta));
                }

                prevEndpoint = endpoint;

                // The selector: inside a run, history entry 0; else a run of history entry 0, a history entry or a codebook index.
                int selector = books.BlockSelector[block];
                if (rle > 0)
                {
                    rle--;
                    continue;
                }

                int found = Array.IndexOf(history, selector);
                if (found == 0)
                {
                    int run = 1;
                    int at = block + 1;
                    int lastBlock = first + (blocksX * blocksY);
                    while (at < lastBlock && books.BlockSelector[at] == selector)
                    {
                        run++;
                        at++;
                    }

                    if (run >= 3)
                    {
                        symbols.Add(new Symbol(2, selectorCount + HistorySize));
                        if (run - 3 >= 63)
                        {
                            symbols.Add(new Symbol(3, 63));
                            symbols.Add(new Symbol(-1, run - 3, 7));
                        }
                        else
                        {
                            symbols.Add(new Symbol(3, run - 3));
                        }

                        rle = run - 1;
                        continue;
                    }
                }

                if (found >= 0)
                {
                    symbols.Add(new Symbol(2, selectorCount + found));
                    if (found != 0)
                    {
                        (history[found / 2], history[found]) = (history[found], history[found / 2]);
                    }
                }
                else
                {
                    symbols.Add(new Symbol(2, selector));
                    history[rover++] = selector;
                    if (rover == HistorySize)
                    {
                        rover = HistorySize / 2;
                    }
                }
            }
        }

        return symbols;
    }

    /// <summary>Codes the endpoint codebook (Appendix D.2): four Huffman tables, the grayscale flag, then DPCM deltas.</summary>
    private static byte[] WriteEndpoints(Endpoint[] endpoints)
    {
        bool gray = endpoints.All(e => e.R == e.G && e.G == e.B);
        long[][] colorCounts = [new long[32], new long[32], new long[32]];
        long[] intenCounts = new long[8];
        List<(int Model, int Delta, bool Inten)> deltas = [];
        int prevInten = 0;
        Span<int> prev = [16, 16, 16];
        foreach (Endpoint e in endpoints)
        {
            int di = (e.Inten - prevInten) & 7;
            intenCounts[di]++;
            deltas.Add((-1, di, true));
            prevInten = e.Inten;
            Span<int> value = [e.R, e.G, e.B];
            for (int c = 0; c < (gray ? 1 : 3); c++)
            {
                int model = prev[c] <= 9 ? 0 : prev[c] <= 21 ? 1 : 2;
                int d = (value[c] - prev[c]) & 31;
                colorCounts[model][d]++;
                deltas.Add((model, d, false));
                prev[c] = value[c];
            }
        }

        BasisHuffmanCode[] color = [new(colorCounts[0]), new(colorCounts[1]), new(colorCounts[2])];
        BasisHuffmanCode inten = new(intenCounts);
        BasisBitWriter bits = new();
        foreach (BasisHuffmanCode code in color)
        {
            code.WriteTable(bits);
        }

        inten.WriteTable(bits);
        bits.Write(gray ? 1u : 0, 1);
        foreach ((int model, int delta, bool isInten) in deltas)
        {
            if (isInten)
            {
                inten.Write(bits, delta);
            }
            else
            {
                color[model].Write(bits, delta);
            }
        }

        return bits.ToArray();
    }

    /// <summary>Codes the selector codebook (Appendix D.3), XOR deltas with Huffman coding or raw, whichever is smaller.</summary>
    private static byte[] WriteSelectors(uint[] selectors)
    {
        // A selector's 2-bit texels, texel t at bit 2t, are BasisLZ's four rows of bytes with the left texel lowest.
        uint[] rows = selectors;
        BasisBitWriter raw = new();
        raw.Write(0, 2);
        raw.Write(1, 1);
        foreach (uint r in rows)
        {
            raw.Write(r, 32);
        }

        long[] counts = new long[256];
        for (int i = 1; i < rows.Length; i++)
        {
            uint x = rows[i] ^ rows[i - 1];
            for (int j = 0; j < 4; j++)
            {
                counts[(x >> (j * 8)) & 0xFF]++;
            }
        }

        BasisHuffmanCode delta = new(counts);
        BasisBitWriter coded = new();
        coded.Write(0, 3);
        delta.WriteTable(coded);
        coded.Write(rows[0], 32);
        for (int i = 1; i < rows.Length; i++)
        {
            uint x = rows[i] ^ rows[i - 1];
            for (int j = 0; j < 4; j++)
            {
                delta.Write(coded, (int)((x >> (j * 8)) & 0xFF));
            }
        }

        byte[] a = raw.ToArray();
        byte[] b = coded.ToArray();
        return b.Length < a.Length ? b : a;
    }
}
