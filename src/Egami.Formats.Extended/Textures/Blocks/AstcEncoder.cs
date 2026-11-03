using System.Numerics;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Encodes LDR ASTC blocks of every 2D footprint at effort 0 to 6. A constant block becomes a void-extent block. Otherwise
/// the encoder picks the colour endpoint mode from the block's content (luminance, luminance-alpha, RGB or RGBA, all direct),
/// estimates for every weight grid and weight range how much error the grid's resolution and the two quantisations cost,
/// and fully tries the most promising block modes. Each try fits the endpoints of every partition along its principal axis,
/// projects the texels for their ideal weights, fits the grid to them, and alternates least-squares endpoint and weight
/// refits; higher efforts try more block modes, two to four partitions (the partitionings nearest a k-means clustering of the
/// texels) and dual weight planes. Every candidate is scored by decoding it, so the error measured is the error a reader
/// sees, and the work for one block depends only on that block, so the output never depends on how many threads encode.
/// </summary>
internal static class AstcEncoder
{
    private const int MaxTexels = 144;

    private static readonly Footprint?[] Footprints = new Footprint?[14];

    /// <summary>The nearest colour value index of each 8-bit value, per colour range.</summary>
    private static readonly byte[][] ColorQuantize = BuildColorQuantize();

    /// <summary>The nearest weight value index of each weight 0 to 64, per weight range.</summary>
    private static readonly byte[][] WeightQuantize = BuildWeightQuantize();

    /// <summary>
    /// Encodes one block: <paramref name="rgba"/> holds the footprint's texels in row order, 4 bytes each, and
    /// <paramref name="block"/> receives 16 bytes. <paramref name="srgb"/> scores candidates as an sRGB texture decodes them.
    /// </summary>
    public static void Encode(Astc astc, ReadOnlySpan<byte> rgba, Span<byte> block, int effort, bool srgb)
    {
        int texels = astc.Texels;
        bool constant = true;
        bool opaque = true;
        bool gray = true;
        for (int t = 0; t < texels; t++)
        {
            int i = t * 4;
            constant &= rgba[i] == rgba[0] && rgba[i + 1] == rgba[1] && rgba[i + 2] == rgba[2] && rgba[i + 3] == rgba[3];
            opaque &= rgba[i + 3] == 255;
            gray &= rgba[i] == rgba[i + 1] && rgba[i] == rgba[i + 2];
        }

        if (constant)
        {
            VoidExtent(rgba, block);
            return;
        }

        Footprint fp = For(astc);
        Settings settings = Settings.For(effort);
        int cem = gray ? (opaque ? 0 : 4) : (opaque ? 8 : 12);
        var state = new Search(astc, fp, rgba, cem, srgb ? AstcProfile.Srgb : AstcProfile.Ldr, settings);

        // Single partition, single plane.
        state.TryConfiguration(1, 0, -1);

        // Dual planes: the channel that varies least with the others gets its own weights.
        if (settings.Dual && cem != 0)
        {
            if (cem == 4)
            {
                state.TryConfiguration(1, 0, 3);
            }
            else
            {
                Span<int> order = stackalloc int[4];
                int channels = state.DualCandidates(order, settings.AllDualChannels);
                for (int i = 0; i < channels; i++)
                {
                    state.TryConfiguration(1, 0, order[i]);
                }
            }
        }

        Span<int> seedList = stackalloc int[16];
        for (int partitions = 2; partitions <= 4; partitions++)
        {
            int seeds = settings.Seeds(partitions);
            if (seeds == 0 || texels < partitions * 2)
            {
                continue;
            }

            Span<int> best = seedList[..seeds];
            int found = state.BestSeeds(partitions, best);
            for (int i = 0; i < found; i++)
            {
                state.TryConfiguration(partitions, best[i], -1);
            }
        }

        state.Best.CopyTo(block);
    }

    private static void VoidExtent(ReadOnlySpan<byte> rgba, Span<byte> block)
    {
        // Mode 0x1FC with the LDR flag clear, the two reserved bits set and every extent coordinate all ones.
        ulong lo = 0x1FCUL | (3UL << 10) | (0x1FFFUL << 12) | (0x1FFFUL << 25) | (0x1FFFUL << 38) | (0x1FFFUL << 51);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(block, lo);
        for (int c = 0; c < 4; c++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(block[(8 + (c * 2))..], (ushort)(rgba[c] * 257));
        }
    }

    private static Footprint For(Astc astc)
    {
        int index = 0;
        while (Astc.Footprints[index * 2] != astc.Width || Astc.Footprints[(index * 2) + 1] != astc.Height)
        {
            index++;
        }

        return Footprints[index] ??= new Footprint(astc);
    }

    private static byte[][] BuildColorQuantize()
    {
        byte[][] tables = new byte[21][];
        for (int r = 0; r < 21; r++)
        {
            byte[] unq = AstcIse.ColorUnquantized[r];
            tables[r] = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                int best = 0;
                for (int i = 1; i < unq.Length; i++)
                {
                    if (Math.Abs(unq[i] - v) < Math.Abs(unq[best] - v))
                    {
                        best = i;
                    }
                }

                tables[r][v] = (byte)best;
            }
        }

        return tables;
    }

    private static byte[][] BuildWeightQuantize()
    {
        byte[][] tables = new byte[12][];
        for (int r = 0; r < 12; r++)
        {
            byte[] unq = AstcIse.WeightUnquantized[r];
            tables[r] = new byte[65];
            for (int v = 0; v <= 64; v++)
            {
                int best = 0;
                for (int i = 1; i < unq.Length; i++)
                {
                    if (Math.Abs(unq[i] - v) < Math.Abs(unq[best] - v))
                    {
                        best = i;
                    }
                }

                tables[r][v] = (byte)best;
            }
        }

        return tables;
    }

    /// <summary>How hard each effort searches.</summary>
    private readonly record struct Settings(int Modes, int Refits, bool Dual, bool AllDualChannels, int Seeds2, int Seeds3, int Seeds4)
    {
        public static Settings For(int effort) => Math.Clamp(effort, 0, 6) switch
        {
            0 => new(1, 0, false, false, 0, 0, 0),
            1 => new(2, 1, false, false, 1, 0, 0),
            2 => new(3, 1, false, false, 2, 0, 0),
            3 => new(3, 2, true, false, 2, 1, 0),
            4 => new(4, 2, true, false, 4, 2, 0),
            5 => new(6, 3, true, true, 8, 4, 2),
            _ => new(10, 4, true, true, 16, 8, 4),
        };

        public int Seeds(int partitions) => partitions switch { 2 => Seeds2, 3 => Seeds3, _ => Seeds4 };
    }

    /// <summary>A weight grid and the block mode of each of its weight ranges, single and dual plane.</summary>
    private sealed class Grid(int width, int height, Astc.Infill infill)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public Astc.Infill Infill { get; } = infill;

        /// <summary>The block mode of each weight range, or -1, for one plane [0..11] and two [12..23].</summary>
        public short[] Modes { get; } = Enumerable.Repeat((short)-1, 24).ToArray();

        /// <summary>The texels each grid point feeds and with what weight (sixteenths), for fitting the grid to texels.</summary>
        public (short Texel, byte Weight)[][] Feeds { get; set; } = [];
    }

    /// <summary>Per-footprint data: the weight grids and the partitionings of each partition count.</summary>
    private sealed class Footprint
    {
        public Footprint(Astc astc)
        {
            int texels = astc.Texels;
            var grids = new Dictionary<int, Grid>();
            for (int m = 0; m < 2048; m++)
            {
                if ((m & 0x1FF) == 0x1FC)
                {
                    continue;
                }

                Astc.BlockMode mode = astc.Mode(m);
                if (!mode.Valid)
                {
                    continue;
                }

                int key = (mode.GridWidth * 16) + mode.GridHeight;
                if (!grids.TryGetValue(key, out Grid? grid))
                {
                    grid = new Grid(mode.GridWidth, mode.GridHeight, astc.InfillFor(mode.GridWidth, mode.GridHeight));
                    grids.Add(key, grid);
                }

                int slot = mode.WeightRange + (mode.DualPlane ? 12 : 0);
                if (grid.Modes[slot] < 0)
                {
                    grid.Modes[slot] = (short)m;
                }
            }

            foreach (Grid grid in grids.Values)
            {
                var feeds = new List<(short, byte)>[grid.Width * grid.Height];
                for (int g = 0; g < feeds.Length; g++)
                {
                    feeds[g] = [];
                }

                ReadOnlySpan<short> index = grid.Infill.Indices;
                ReadOnlySpan<byte> weight = grid.Infill.Weights;
                for (int t = 0; t < texels; t++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        if (weight[(t * 4) + k] != 0)
                        {
                            feeds[index[(t * 4) + k]].Add(((short)t, weight[(t * 4) + k]));
                        }
                    }
                }

                grid.Feeds = feeds.Select(f => f.ToArray()).ToArray();
            }

            Grids = grids.Values.OrderBy(g => g.Width * 16 + g.Height).ToArray();
            Partitionings = new Partitioning[3][];
            for (int count = 2; count <= 4; count++)
            {
                var list = new List<Partitioning>();
                var seen = new HashSet<(ulong, ulong, ulong, ulong, ulong, ulong)>();
                for (int seed = 0; seed < 1024; seed++)
                {
                    byte[] table = astc.PartitionTable(count, seed);
                    var masks = new ulong[count * 3];
                    for (int t = 0; t < texels; t++)
                    {
                        masks[(table[t] * 3) + (t >> 6)] |= 1UL << (t & 63);
                    }

                    bool empty = false;
                    for (int p = 0; p < count; p++)
                    {
                        empty |= (masks[p * 3] | masks[(p * 3) + 1] | masks[(p * 3) + 2]) == 0;
                    }

                    if (empty || !seen.Add((masks[0], masks[1], masks[2], masks[3], masks[4], masks[5])))
                    {
                        continue;
                    }

                    list.Add(new Partitioning(seed, table, masks));
                }

                Partitionings[count - 2] = list.ToArray();
            }
        }

        public Grid[] Grids { get; }

        public Partitioning[][] Partitionings { get; }
    }

    private sealed record Partitioning(int Seed, byte[] Table, ulong[] Masks);

    /// <summary>The search for one block: its texels, the best block so far and its error.</summary>
    private sealed class Search
    {
        private readonly Astc _astc;
        private readonly Footprint _fp;
        private readonly int[] _px;
        private readonly int _texels;
        private readonly int _cem;
        private readonly int _channels;
        private readonly AstcProfile _profile;
        private readonly Settings _settings;
        private readonly byte[] _best = new byte[16];
        private readonly byte[] _trial = new byte[16];
        private readonly ushort[] _decoded;
        private long _bestError = long.MaxValue;

        public Search(Astc astc, Footprint fp, ReadOnlySpan<byte> rgba, int cem, AstcProfile profile, Settings settings)
        {
            _astc = astc;
            _fp = fp;
            _texels = astc.Texels;
            _px = new int[_texels * 4];
            for (int i = 0; i < _px.Length; i++)
            {
                _px[i] = rgba[i];
            }

            _cem = cem;
            _channels = ((cem >> 2) + 1) * 2;
            _profile = profile;
            _settings = settings;
            _decoded = new ushort[_texels * 4];
        }

        public ReadOnlySpan<byte> Best => _best;

        /// <summary>Orders the colour channels for a second weight plane, least correlated first; returns how many to try.</summary>
        public int DualCandidates(Span<int> order, bool all)
        {
            int channels = _cem == 12 ? 4 : 3;
            Span<double> mean = stackalloc double[4];
            for (int t = 0; t < _texels; t++)
            {
                for (int c = 0; c < 4; c++)
                {
                    mean[c] += _px[(t * 4) + c];
                }
            }

            for (int c = 0; c < 4; c++)
            {
                mean[c] /= _texels;
            }

            Span<double> score = stackalloc double[4];
            for (int c = 0; c < channels; c++)
            {
                // How much of the channel's variance the other channels' sum does not explain.
                double sxx = 0, syy = 0, sxy = 0;
                for (int t = 0; t < _texels; t++)
                {
                    double x = _px[(t * 4) + c] - mean[c];
                    double y = 0;
                    for (int o = 0; o < channels; o++)
                    {
                        if (o != c)
                        {
                            y += _px[(t * 4) + o] - mean[o];
                        }
                    }

                    sxx += x * x;
                    syy += y * y;
                    sxy += x * y;
                }

                score[c] = syy < 1e-9 || sxx < 1e-9 ? sxx : sxx - (sxy * sxy / syy);
                order[c] = c;
            }

            for (int i = 0; i < channels; i++)
            {
                for (int j = i + 1; j < channels; j++)
                {
                    if (score[order[j]] > score[order[i]])
                    {
                        (order[i], order[j]) = (order[j], order[i]);
                    }
                }
            }

            return all ? channels : (score[order[0]] > 0 ? 1 : 0);
        }

        /// <summary>Clusters the texels with k-means and returns the partitionings that match the clusters best.</summary>
        public int BestSeeds(int count, Span<int> output)
        {
            Span<int> labels = stackalloc int[_texels];
            Cluster(count, labels);
            Span<ulong> masks = stackalloc ulong[count * 3];
            for (int t = 0; t < _texels; t++)
            {
                masks[(labels[t] * 3) + (t >> 6)] |= 1UL << (t & 63);
            }

            Partitioning[] all = _fp.Partitionings[count - 2];
            Span<int> scores = stackalloc int[output.Length];
            int found = 0;
            Span<int> overlap = stackalloc int[16];
            foreach (Partitioning p in all)
            {
                for (int a = 0; a < count; a++)
                {
                    for (int b = 0; b < count; b++)
                    {
                        overlap[(a * count) + b] = BitOperations.PopCount(p.Masks[a * 3] & masks[b * 3]) + BitOperations.PopCount(p.Masks[(a * 3) + 1] & masks[(b * 3) + 1])
                            + BitOperations.PopCount(p.Masks[(a * 3) + 2] & masks[(b * 3) + 2]);
                    }
                }

                int score = _texels - BestMatch(overlap, count);
                if (found < output.Length)
                {
                    output[found] = p.Seed;
                    scores[found++] = score;
                }
                else if (score < scores[found - 1])
                {
                    output[found - 1] = p.Seed;
                    scores[found - 1] = score;
                }
                else
                {
                    continue;
                }

                // Keep the list sorted by score, earlier seeds first among equals.
                for (int i = found - 1; i > 0 && scores[i] < scores[i - 1]; i--)
                {
                    (scores[i], scores[i - 1]) = (scores[i - 1], scores[i]);
                    (output[i], output[i - 1]) = (output[i - 1], output[i]);
                }
            }

            return found;
        }

        private static int BestMatch(ReadOnlySpan<int> overlap, int count)
        {
            Span<int> perm = stackalloc int[4];
            for (int i = 0; i < count; i++)
            {
                perm[i] = i;
            }

            int best = 0;
            while (true)
            {
                int sum = 0;
                for (int i = 0; i < count; i++)
                {
                    sum += overlap[(i * count) + perm[i]];
                }

                best = Math.Max(best, sum);
                if (!NextPermutation(perm[..count]))
                {
                    return best;
                }
            }
        }

        private static bool NextPermutation(Span<int> p)
        {
            int i = p.Length - 2;
            while (i >= 0 && p[i] >= p[i + 1])
            {
                i--;
            }

            if (i < 0)
            {
                return false;
            }

            int j = p.Length - 1;
            while (p[j] <= p[i])
            {
                j--;
            }

            (p[i], p[j]) = (p[j], p[i]);
            p[(i + 1)..].Reverse();
            return true;
        }

        private void Cluster(int count, Span<int> labels)
        {
            Span<double> centers = stackalloc double[count * 4];
            Span<double> mean = stackalloc double[4];
            for (int t = 0; t < _texels; t++)
            {
                for (int c = 0; c < 4; c++)
                {
                    mean[c] += _px[(t * 4) + c] / (double)_texels;
                }
            }

            // Farthest-point seeding from the mean: deterministic and spread out.
            Span<double> nearest = stackalloc double[_texels];
            nearest.Fill(double.MaxValue);
            for (int k = 0; k < count; k++)
            {
                int pick = 0;
                double far = -1;
                for (int t = 0; t < _texels; t++)
                {
                    double d = k == 0 ? Distance(t, mean) : nearest[t];
                    if (d > far)
                    {
                        far = d;
                        pick = t;
                    }
                }

                for (int c = 0; c < 4; c++)
                {
                    centers[(k * 4) + c] = _px[(pick * 4) + c];
                }

                for (int t = 0; t < _texels; t++)
                {
                    nearest[t] = Math.Min(nearest[t], Distance(t, centers.Slice(k * 4, 4)));
                }
            }

            Span<double> sums = stackalloc double[count * 5];
            for (int step = 0; step < 4; step++)
            {
                for (int t = 0; t < _texels; t++)
                {
                    int best = 0;
                    double bestDistance = double.MaxValue;
                    for (int k = 0; k < count; k++)
                    {
                        double d = Distance(t, centers.Slice(k * 4, 4));
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = k;
                        }
                    }

                    labels[t] = best;
                }

                sums.Clear();
                for (int t = 0; t < _texels; t++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        sums[(labels[t] * 5) + c] += _px[(t * 4) + c];
                    }

                    sums[(labels[t] * 5) + 4]++;
                }

                for (int k = 0; k < count; k++)
                {
                    if (sums[(k * 5) + 4] > 0)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            centers[(k * 4) + c] = sums[(k * 5) + c] / sums[(k * 5) + 4];
                        }
                    }
                }
            }
        }

        private double Distance(int t, ReadOnlySpan<double> center)
        {
            double sum = 0;
            for (int c = 0; c < 4; c++)
            {
                double d = _px[(t * 4) + c] - center[c];
                sum += d * d;
            }

            return sum;
        }

        /// <summary>Tries one partitioning (count and seed) with one or two weight planes over the most promising block modes.</summary>
        public void TryConfiguration(int partitions, int seed, int plane2)
        {
            byte[]? table = partitions > 1 ? _astc.PartitionTable(partitions, seed) : null;
            bool dual = plane2 >= 0;
            int planes = dual ? 2 : 1;

            // Ideal (unquantised) endpoints and texel weights for this partitioning.
            var fit = new Fit(partitions, _texels);
            for (int p = 0; p < partitions; p++)
            {
                FitPartition(table, p, plane2, fit);
            }

            int colorCount = _channels * partitions;
            if (colorCount > 18)
            {
                return;
            }

            int configBits = partitions == 1 ? 17 : 29;

            // Rank the block modes by an error estimate and try the best few.
            int limit = _settings.Modes;
            Span<long> estimates = stackalloc long[limit];
            Span<int> chosen = stackalloc int[limit];
            Span<int> chosenGrid = stackalloc int[limit];
            int count = 0;
            Span<double> spread = stackalloc double[2];
            Span<double> gridError = stackalloc double[2];
            for (int g = 0; g < _fp.Grids.Length; g++)
            {
                Grid grid = _fp.Grids[g];
                bool computed = false;
                for (int range = 11; range >= 0; range--)
                {
                    int mode = grid.Modes[range + (dual ? 12 : 0)];
                    if (mode < 0)
                    {
                        continue;
                    }

                    int weightBits = AstcIse.BitCount(grid.Width * grid.Height * planes, range);
                    int colorBits = 128 - weightBits - configBits - (dual ? 2 : 0);
                    int colorRange = Astc.ColorRange(colorCount, colorBits);
                    if (colorRange < AstcIse.Range6)
                    {
                        continue;
                    }

                    if (!computed)
                    {
                        GridError(grid, fit, planes, gridError, spread);
                        computed = true;
                    }

                    double weightStep = 1.0 / (AstcIse.Levels[range] - 1);
                    double colorStep = 255.0 / (AstcIse.Levels[colorRange] - 1);
                    double estimate = 0;
                    for (int plane = 0; plane < planes; plane++)
                    {
                        estimate += gridError[plane] + (_texels * spread[plane] * weightStep * weightStep / 12);
                    }

                    estimate += _texels * 4 * colorStep * colorStep / 12 / 2;
                    long key = (long)estimate;
                    if (count < limit)
                    {
                        estimates[count] = key;
                        chosen[count] = mode;
                        chosenGrid[count++] = g;
                    }
                    else if (key < estimates[count - 1])
                    {
                        estimates[count - 1] = key;
                        chosen[count - 1] = mode;
                        chosenGrid[count - 1] = g;
                    }
                    else
                    {
                        continue;
                    }

                    for (int i = count - 1; i > 0 && estimates[i] < estimates[i - 1]; i--)
                    {
                        (estimates[i], estimates[i - 1]) = (estimates[i - 1], estimates[i]);
                        (chosen[i], chosen[i - 1]) = (chosen[i - 1], chosen[i]);
                        (chosenGrid[i], chosenGrid[i - 1]) = (chosenGrid[i - 1], chosenGrid[i]);
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                TryMode(chosen[i], _fp.Grids[chosenGrid[i]], partitions, seed, table, plane2, fit);
            }
        }

        /// <summary>Estimates, per plane, the error a grid's resolution adds and the spread its weights cover.</summary>
        private void GridError(Grid grid, Fit fit, int planes, Span<double> error, Span<double> spread)
        {
            Span<double> gridWeights = stackalloc double[grid.Width * grid.Height];
            for (int plane = 0; plane < planes; plane++)
            {
                ReadOnlySpan<double> ideal = fit.Weights(plane);
                Downsample(grid, ideal, gridWeights);
                double sum = 0;
                double length = 0;
                ReadOnlySpan<short> index = grid.Infill.Indices;
                ReadOnlySpan<byte> weight = grid.Infill.Weights;
                for (int t = 0; t < _texels; t++)
                {
                    double w = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        w += gridWeights[index[(t * 4) + k]] * weight[(t * 4) + k];
                    }

                    double d = (w / 16) - ideal[t];
                    double l = fit.Length(plane, t);
                    sum += d * d * l;
                    length += l;
                }

                error[plane] = sum;
                spread[plane] = length / _texels;
            }
        }

        /// <summary>Fits grid weights to texel weights: each grid point takes the mean of the texels it feeds, weighted.</summary>
        private static void Downsample(Grid grid, ReadOnlySpan<double> texels, Span<double> output)
        {
            for (int g = 0; g < output.Length; g++)
            {
                double sum = 0, total = 0;
                foreach ((short t, byte w) in grid.Feeds[g])
                {
                    sum += texels[t] * w;
                    total += w;
                }

                output[g] = total > 0 ? sum / total : 0.5;
            }
        }

        private void FitPartition(byte[]? table, int p, int plane2, Fit fit)
        {
            Span<double> mean = stackalloc double[4];
            int n = 0;
            for (int t = 0; t < _texels; t++)
            {
                if (table is null || table[t] == p)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        mean[c] += _px[(t * 4) + c];
                    }

                    n++;
                }
            }

            for (int c = 0; c < 4; c++)
            {
                mean[c] /= Math.Max(n, 1);
            }

            // The principal axis of the channels on the first plane.
            Span<double> axis = stackalloc double[4];
            Span<double> cov = stackalloc double[16];
            for (int t = 0; t < _texels; t++)
            {
                if (table is not null && table[t] != p)
                {
                    continue;
                }

                for (int a = 0; a < 4; a++)
                {
                    if (a == plane2)
                    {
                        continue;
                    }

                    double da = _px[(t * 4) + a] - mean[a];
                    for (int b = 0; b < 4; b++)
                    {
                        if (b != plane2)
                        {
                            cov[(a * 4) + b] += da * (_px[(t * 4) + b] - mean[b]);
                        }
                    }
                }
            }

            // Start from the channel with the most variance; power iteration converges quickly for 4 × 4.
            int start = 0;
            for (int c = 1; c < 4; c++)
            {
                if (cov[(c * 4) + c] > cov[(start * 4) + start])
                {
                    start = c;
                }
            }

            axis[start] = 1;
            Span<double> next = stackalloc double[4];
            for (int i = 0; i < 8; i++)
            {
                next.Clear();
                for (int a = 0; a < 4; a++)
                {
                    for (int b = 0; b < 4; b++)
                    {
                        next[a] += cov[(a * 4) + b] * axis[b];
                    }
                }

                double length = Math.Sqrt((next[0] * next[0]) + (next[1] * next[1]) + (next[2] * next[2]) + (next[3] * next[3]));
                if (length < 1e-9)
                {
                    break;
                }

                for (int a = 0; a < 4; a++)
                {
                    axis[a] = next[a] / length;
                }
            }

            // Point the axis towards rising RGB so endpoint 1 is the brighter one, as the direct modes need.
            double rise = axis[0] + axis[1] + axis[2];
            if (rise < 0 || (rise == 0 && axis[3] < 0))
            {
                for (int a = 0; a < 4; a++)
                {
                    axis[a] = -axis[a];
                }
            }

            double low = double.MaxValue, high = double.MinValue;
            double lowAlone = double.MaxValue, highAlone = double.MinValue;
            for (int t = 0; t < _texels; t++)
            {
                if (table is not null && table[t] != p)
                {
                    continue;
                }

                double s = 0;
                for (int c = 0; c < 4; c++)
                {
                    s += (_px[(t * 4) + c] - mean[c]) * axis[c];
                }

                low = Math.Min(low, s);
                high = Math.Max(high, s);
                if (plane2 >= 0)
                {
                    lowAlone = Math.Min(lowAlone, _px[(t * 4) + plane2]);
                    highAlone = Math.Max(highAlone, _px[(t * 4) + plane2]);
                }
            }

            Span<double> e0 = fit.Endpoint(p, 0);
            Span<double> e1 = fit.Endpoint(p, 1);
            for (int c = 0; c < 4; c++)
            {
                e0[c] = Math.Clamp(mean[c] + (low * axis[c]), 0, 255);
                e1[c] = Math.Clamp(mean[c] + (high * axis[c]), 0, 255);
            }

            if (plane2 >= 0)
            {
                e0[plane2] = lowAlone;
                e1[plane2] = highAlone;
            }

            fit.Project(_px, table, p, plane2);
        }

        /// <summary>Encodes the block with block mode <paramref name="mode"/>, refines it, and keeps it if it is the best yet.</summary>
        private void TryMode(int mode, Grid grid, int partitions, int seed, byte[]? table, int plane2, Fit start)
        {
            Astc.BlockMode m = _astc.Mode(mode);
            bool dual = plane2 >= 0;
            int planes = dual ? 2 : 1;
            int gridCount = grid.Width * grid.Height;
            int configBits = partitions == 1 ? 17 : 29;
            int weightBits = AstcIse.BitCount(gridCount * planes, m.WeightRange);
            int colorBits = 128 - weightBits - configBits - (dual ? 2 : 0);
            int colorCount = _channels * partitions;
            int colorRange = Astc.ColorRange(colorCount, colorBits);

            Fit fit = start.Clone();
            Span<int> weights = stackalloc int[gridCount * planes];
            Span<int> colors = stackalloc int[colorCount];
            Span<int> texelWeights = stackalloc int[_texels * planes];
            Span<double> gridWeights = stackalloc double[gridCount];
            byte[] weightUnq = AstcIse.WeightUnquantized[m.WeightRange];
            byte[] weightQ = WeightQuantize[m.WeightRange];
            Span<int> unq = stackalloc int[gridCount * planes];
            for (int iteration = 0; ; iteration++)
            {
                // Quantise the grid weights of each plane.
                for (int plane = 0; plane < planes; plane++)
                {
                    Downsample(grid, fit.Weights(plane), gridWeights);
                    for (int g = 0; g < gridCount; g++)
                    {
                        int w = (int)Math.Round(Math.Clamp(gridWeights[g], 0, 1) * 64);
                        weights[(g * planes) + plane] = weightQ[w];
                    }
                }

                // The texel weights a decoder infills from them.
                for (int i = 0; i < unq.Length; i++)
                {
                    unq[i] = weightUnq[weights[i]];
                }

                for (int plane = 0; plane < planes; plane++)
                {
                    grid.Infill.Apply(unq, planes, plane, texelWeights.Slice(plane * _texels, _texels));
                }

                // Least-squares endpoints for those weights (the first pass keeps the projected ones when refits are off).
                if (iteration > 0 || _settings.Refits > 0)
                {
                    for (int p = 0; p < partitions; p++)
                    {
                        RefitEndpoints(table, p, plane2, texelWeights, fit);
                    }
                }

                QuantizeColors(fit, partitions, colorRange, colors);
                Pack(mode, partitions, seed, plane2, m.WeightRange, colorRange, weights, colors);
                Score();
                if (iteration >= _settings.Refits)
                {
                    break;
                }

                // Reproject the texels onto the quantised endpoints for the next round's weights.
                fit.SetEndpoints(colors, _cem, AstcIse.ColorUnquantized[colorRange]);
                for (int p = 0; p < partitions; p++)
                {
                    fit.Project(_px, table, p, plane2);
                }
            }
        }

        private void RefitEndpoints(byte[]? table, int p, int plane2, ReadOnlySpan<int> texelWeights, Fit fit)
        {
            for (int c = 0; c < 4; c++)
            {
                int plane = c == plane2 ? 1 : 0;
                double a = 0, b = 0, d = 0, x0 = 0, x1 = 0;
                for (int t = 0; t < _texels; t++)
                {
                    if (table is not null && table[t] != p)
                    {
                        continue;
                    }

                    double w = texelWeights[(plane * _texels) + t] / 64.0;
                    double v = _px[(t * 4) + c];
                    a += (1 - w) * (1 - w);
                    b += (1 - w) * w;
                    d += w * w;
                    x0 += (1 - w) * v;
                    x1 += w * v;
                }

                double det = (a * d) - (b * b);
                if (Math.Abs(det) < 1e-9)
                {
                    continue;
                }

                fit.Endpoint(p, 0)[c] = Math.Clamp(((d * x0) - (b * x1)) / det, 0, 255);
                fit.Endpoint(p, 1)[c] = Math.Clamp(((a * x1) - (b * x0)) / det, 0, 255);
            }
        }

        /// <summary>Quantises every partition's endpoints to <paramref name="range"/> in the colour endpoint mode's order.</summary>
        private void QuantizeColors(Fit fit, int partitions, int range, Span<int> colors)
        {
            byte[] q = ColorQuantize[range];
            byte[] unq = AstcIse.ColorUnquantized[range];
            for (int p = 0; p < partitions; p++)
            {
                Span<double> e0 = fit.Endpoint(p, 0);
                Span<double> e1 = fit.Endpoint(p, 1);
                Span<int> v = colors.Slice(p * _channels, _channels);
                switch (_cem)
                {
                    case 0:
                        v[0] = q[Round((e0[0] + e0[1] + e0[2]) / 3)];
                        v[1] = q[Round((e1[0] + e1[1] + e1[2]) / 3)];
                        break;
                    case 4:
                        v[0] = q[Round((e0[0] + e0[1] + e0[2]) / 3)];
                        v[1] = q[Round((e1[0] + e1[1] + e1[2]) / 3)];
                        v[2] = q[Round(e0[3])];
                        v[3] = q[Round(e1[3])];
                        break;
                    default:
                        for (int c = 0; c < _channels / 2; c++)
                        {
                            v[c * 2] = q[Round(e0[c])];
                            v[(c * 2) + 1] = q[Round(e1[c])];
                        }

                        // The direct modes swap the endpoints and contract blue when endpoint 1 is the darker: step it up.
                        while (unq[v[1]] + unq[v[3]] + unq[v[5]] < unq[v[0]] + unq[v[2]] + unq[v[4]])
                        {
                            int c = 0;
                            int gap = int.MinValue;
                            for (int k = 0; k < 3; k++)
                            {
                                int g = unq[v[k * 2]] - unq[v[(k * 2) + 1]];
                                if (g > gap && Next(unq, v[(k * 2) + 1]) >= 0)
                                {
                                    gap = g;
                                    c = k;
                                }
                            }

                            int up = Next(unq, v[(c * 2) + 1]);
                            if (up < 0)
                            {
                                break;
                            }

                            v[(c * 2) + 1] = up;
                        }

                        break;
                }
            }
        }

        /// <summary>Returns the value index whose unquantised value is the next above index <paramref name="i"/>'s, or -1.</summary>
        private static int Next(byte[] unq, int i)
        {
            int best = -1;
            for (int k = 0; k < unq.Length; k++)
            {
                if (unq[k] > unq[i] && (best < 0 || unq[k] < unq[best]))
                {
                    best = k;
                }
            }

            return best;
        }

        private static int Round(double v) => Math.Clamp((int)Math.Round(v), 0, 255);

        private void Pack(int mode, int partitions, int seed, int plane2, int weightRange, int colorRange, ReadOnlySpan<int> weights, ReadOnlySpan<int> colors)
        {
            Span<byte> block = _trial;
            block.Clear();
            int offset = 0;
            AstcIse.Write(block, ref offset, mode, 11);
            AstcIse.Write(block, ref offset, partitions - 1, 2);
            if (partitions == 1)
            {
                AstcIse.Write(block, ref offset, _cem, 4);
            }
            else
            {
                AstcIse.Write(block, ref offset, seed, 10);
                AstcIse.Write(block, ref offset, _cem << 2, 6);
            }

            AstcIse.Encode(colorRange, colors, block, offset);
            int weightBits = AstcIse.BitCount(weights.Length, weightRange);
            if (plane2 >= 0)
            {
                int ccs = 128 - weightBits - 2;
                AstcIse.Write(block, ref ccs, plane2, 2);
            }

            Span<byte> reversed = stackalloc byte[16];
            AstcIse.Encode(weightRange, weights, reversed, 0);
            for (int i = 0; i < 16; i++)
            {
                block[15 - i] |= Reverse(reversed[i]);
            }
        }

        private static byte Reverse(byte b)
        {
            b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
            b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
            return (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        }

        private void Score()
        {
            _astc.Decode(_trial, _profile, _decoded);
            long error = 0;
            for (int i = 0; i < _decoded.Length; i++)
            {
                int d = (_decoded[i] >> 8) - _px[i];
                error += d * d;
            }

            if (error < _bestError)
            {
                _bestError = error;
                _trial.CopyTo(_best, 0);
            }
        }
    }

    /// <summary>Endpoints (as doubles, RGBA) of each partition and the ideal weight of each texel, per plane.</summary>
    private sealed class Fit
    {
        private readonly double[] _endpoints;
        private readonly double[] _weights;
        private readonly double[] _lengths;
        private readonly int _texels;

        public Fit(int partitions, int texels)
        {
            _endpoints = new double[partitions * 8];
            _weights = new double[texels * 2];
            _lengths = new double[texels * 2];
            _texels = texels;
        }

        private Fit(Fit other)
        {
            _endpoints = (double[])other._endpoints.Clone();
            _weights = (double[])other._weights.Clone();
            _lengths = (double[])other._lengths.Clone();
            _texels = other._texels;
        }

        public Fit Clone() => new(this);

        public Span<double> Endpoint(int partition, int which) => _endpoints.AsSpan((partition * 8) + (which * 4), 4);

        public ReadOnlySpan<double> Weights(int plane) => _weights.AsSpan(plane * _texels, _texels);

        /// <summary>Gets the squared length of texel <paramref name="t"/>'s endpoint segment on a plane, to weigh weight errors.</summary>
        public double Length(int plane, int t) => _lengths[(plane * _texels) + t];

        /// <summary>Sets the endpoints from quantised colour values of a direct colour endpoint mode.</summary>
        public void SetEndpoints(ReadOnlySpan<int> colors, int cem, byte[] unq)
        {
            int channels = ((cem >> 2) + 1) * 2;
            for (int p = 0; p < _endpoints.Length / 8; p++)
            {
                ReadOnlySpan<int> v = colors.Slice(p * channels, channels);
                Span<double> e0 = Endpoint(p, 0);
                Span<double> e1 = Endpoint(p, 1);
                switch (cem)
                {
                    case 0:
                        e0[0] = e0[1] = e0[2] = unq[v[0]];
                        e1[0] = e1[1] = e1[2] = unq[v[1]];
                        e0[3] = e1[3] = 255;
                        break;
                    case 4:
                        e0[0] = e0[1] = e0[2] = unq[v[0]];
                        e1[0] = e1[1] = e1[2] = unq[v[1]];
                        e0[3] = unq[v[2]];
                        e1[3] = unq[v[3]];
                        break;
                    default:
                        for (int c = 0; c < channels / 2; c++)
                        {
                            e0[c] = unq[v[c * 2]];
                            e1[c] = unq[v[(c * 2) + 1]];
                        }

                        if (channels == 6)
                        {
                            e0[3] = e1[3] = 255;
                        }

                        break;
                }
            }
        }

        /// <summary>Projects the texels of partition <paramref name="p"/> onto its endpoint segment(s) for their ideal weights.</summary>
        public void Project(int[] px, byte[]? table, int p, int plane2)
        {
            Span<double> e0 = Endpoint(p, 0);
            Span<double> e1 = Endpoint(p, 1);
            double dd = 0;
            for (int c = 0; c < 4; c++)
            {
                if (c != plane2)
                {
                    dd += (e1[c] - e0[c]) * (e1[c] - e0[c]);
                }
            }

            double alone = plane2 >= 0 ? e1[plane2] - e0[plane2] : 0;
            for (int t = 0; t < _texels; t++)
            {
                if (table is not null && table[t] != p)
                {
                    continue;
                }

                double s = 0;
                for (int c = 0; c < 4; c++)
                {
                    if (c != plane2)
                    {
                        s += (px[(t * 4) + c] - e0[c]) * (e1[c] - e0[c]);
                    }
                }

                _weights[t] = dd > 1e-9 ? Math.Clamp(s / dd, 0, 1) : 0.5;
                _lengths[t] = dd;
                if (plane2 >= 0)
                {
                    _weights[_texels + t] = Math.Abs(alone) > 1e-9 ? Math.Clamp((px[(t * 4) + plane2] - e0[plane2]) / alone, 0, 1) : 0.5;
                    _lengths[_texels + t] = alone * alone;
                }
            }
        }
    }
}
