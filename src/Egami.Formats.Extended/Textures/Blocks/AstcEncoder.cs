using System.Buffers.Binary;
using System.Numerics;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Encodes LDR ASTC blocks of every 2D footprint at effort 0 to 6. A constant block becomes a void-extent block. Otherwise
/// the encoder picks the colour endpoint mode from the block's content (luminance, luminance-alpha, RGB or RGBA, all direct)
/// and gathers candidate configurations: one partition, dual weight planes, and two to four partitions (the partitionings
/// nearest a k-means clustering of the texels). Each configuration fits every partition's endpoints along its principal
/// axis; an estimate of the error of every block mode (the texels' distance from their lines, the grid's resolution and the
/// two quantisations) ranks the pairs, and the best few are encoded for real: grid weights fitted to the texels' ideal
/// weights by least squares, then alternating least-squares endpoint refits, quantised weight adjustments scored with the
/// decoder's own arithmetic, and a final decode to measure the block. The work for one block depends only on that block, so
/// the output never depends on how many threads encode.
/// </summary>
internal static class AstcEncoder
{
    private const int MaxTexels = 144;
    private const int MaxConfigs = 1 + 4 + 16 + 8 + 4;

    private static readonly Footprint?[] Footprints = new Footprint?[14];

    /// <summary>The nearest colour value index of each 8-bit value, per colour range.</summary>
    internal static readonly byte[][] ColorQuantize = BuildColorQuantize();

    /// <summary>Per weight range: the nearest value index of each weight 0 to 64, and the indices in value order with their ranks.</summary>
    private static readonly WeightTables[] Weights = BuildWeightTables();

    [ThreadStatic]
    private static Search? _search;

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

        Search search = _search ??= new Search();
        int cem = gray ? (opaque ? 0 : 4) : (opaque ? 8 : 12);
        search.Run(astc, For(astc), rgba, cem, srgb ? AstcProfile.Srgb : AstcProfile.Ldr, Settings.For(effort), block);
    }

    private static void VoidExtent(ReadOnlySpan<byte> rgba, Span<byte> block)
    {
        // Mode 0x1FC with the LDR flag clear, the two reserved bits set and every extent coordinate all ones.
        ulong lo = 0x1FCUL | (3UL << 10) | (0x1FFFUL << 12) | (0x1FFFUL << 25) | (0x1FFFUL << 38) | (0x1FFFUL << 51);
        BinaryPrimitives.WriteUInt64LittleEndian(block, lo);
        for (int c = 0; c < 4; c++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(block[(8 + (c * 2))..], (ushort)(rgba[c] * 257));
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

    private static WeightTables[] BuildWeightTables()
    {
        var tables = new WeightTables[12];
        for (int r = 0; r < 12; r++)
        {
            byte[] unq = AstcIse.WeightUnquantized[r];
            byte[] nearest = new byte[65];
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

                nearest[v] = (byte)best;
            }

            byte[] sorted = Enumerable.Range(0, unq.Length).OrderBy(i => unq[i]).Select(i => (byte)i).ToArray();
            byte[] rank = new byte[unq.Length];
            for (int i = 0; i < sorted.Length; i++)
            {
                rank[sorted[i]] = (byte)i;
            }

            tables[r] = new WeightTables(unq, nearest, sorted, rank);
        }

        return tables;
    }

    private sealed record WeightTables(byte[] Unquantized, byte[] Nearest, byte[] Sorted, byte[] Rank);

    /// <summary>How hard each effort searches.</summary>
    private readonly record struct Settings(int Candidates, int Trials, int Refits, int Realign, bool Dual, bool AllDualChannels, int Seeds2, int Seeds3, int Seeds4, int Smooth)
    {
        public static Settings For(int effort) => Math.Clamp(effort, 0, 6) switch
        {
            0 => new(1, 1, 0, 0, false, false, 0, 0, 0, 0),
            1 => new(4, 1, 1, 1, false, false, 1, 0, 0, 1),
            2 => new(8, 2, 1, 1, false, false, 2, 0, 0, 2),
            3 => new(16, 2, 1, 2, true, false, 3, 1, 0, 2),
            4 => new(24, 3, 2, 2, true, false, 4, 2, 0, 3),
            5 => new(40, 6, 2, 3, true, true, 8, 4, 2, 4),
            _ => new(64, 12, 3, 4, true, true, 16, 8, 4, 6),
        };

        public int Seeds(int partitions) => partitions switch { 2 => Seeds2, 3 => Seeds3, _ => Seeds4 };
    }

    /// <summary>A weight grid and the block mode of each of its weight ranges, single and dual plane.</summary>
    private sealed class Grid(int width, int height, Astc.Infill infill)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public int Count { get; } = width * height;

        public Astc.Infill Infill { get; } = infill;

        /// <summary>Gets or sets whether every texel has its own grid point.</summary>
        public bool Full { get; set; }

        /// <summary>Gets the block mode of each weight range, or -1, for one plane [0..11] and two [12..23].</summary>
        public short[] Modes { get; } = Enumerable.Repeat((short)-1, 24).ToArray();

        /// <summary>Gets or sets the texels each grid point feeds and with what weight (sixteenths).</summary>
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
                Prepare(grid, astc);
            }

            // Grids that thin one axis only, to estimate any grid's error as the sum of its two axes' errors.
            Columns = new Grid[astc.Width + 1];
            Rows = new Grid[astc.Height + 1];
            for (int w = 2; w < astc.Width; w++)
            {
                Columns[w] = Prepare(new Grid(w, astc.Height, astc.InfillFor(w, astc.Height)), astc);
            }

            for (int h = 2; h < astc.Height; h++)
            {
                Rows[h] = Prepare(new Grid(astc.Width, h, astc.InfillFor(astc.Width, h)), astc);
            }

            Grids = grids.Values.OrderBy(g => (g.Width * 16) + g.Height).ToArray();
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

                    list.Add(new Partitioning(seed, masks));
                }

                Partitionings[count - 2] = list.ToArray();
            }
        }

        public Grid[] Grids { get; }

        /// <summary>Gets the grids of every width with one row per texel row (null for the full width).</summary>
        public Grid?[] Columns { get; }

        /// <summary>Gets the grids of every height with one column per texel column (null for the full height).</summary>
        public Grid?[] Rows { get; }

        public Partitioning[][] Partitionings { get; }

        private static Grid Prepare(Grid grid, Astc astc)
        {
            var feeds = new List<(short, byte)>[grid.Count];
            for (int g = 0; g < feeds.Length; g++)
            {
                feeds[g] = [];
            }

            ReadOnlySpan<short> index = grid.Infill.Indices;
            ReadOnlySpan<byte> weight = grid.Infill.Weights;
            for (int t = 0; t < astc.Texels; t++)
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
            grid.Full = grid.Width == astc.Width && grid.Height == astc.Height;
            return grid;
        }

        private readonly Choice[]?[] _choices = new Choice[]?[19 * 4];

        /// <summary>
        /// Gets every usable (grid, weight range) for <paramref name="colorCount"/> colour values, one or more partitions and
        /// one or two planes, with the colour range left for the endpoints.
        /// </summary>
        public Choice[] Choices(int colorCount, bool partitioned, bool dual)
        {
            int key = (colorCount * 4) + (partitioned ? 2 : 0) + (dual ? 1 : 0);
            return _choices[key] ??= BuildChoices(colorCount, partitioned, dual);
        }

        private Choice[] BuildChoices(int colorCount, bool partitioned, bool dual)
        {
            var list = new List<Choice>();
            int planes = dual ? 2 : 1;
            int configBits = partitioned ? 29 : 17;
            for (int g = 0; g < Grids.Length; g++)
            {
                Grid grid = Grids[g];
                for (int range = 11; range >= 0; range--)
                {
                    if (grid.Modes[range + (dual ? 12 : 0)] < 0)
                    {
                        continue;
                    }

                    int weightBits = AstcIse.BitCount(grid.Count * planes, range);
                    int colorRange = Astc.ColorRange(colorCount, 128 - weightBits - configBits - (dual ? 2 : 0));
                    if (colorRange < AstcIse.Range6)
                    {
                        continue;
                    }

                    double weightStep = 1.0 / (AstcIse.Levels[range] - 1);
                    double colorStep = 255.0 / (AstcIse.Levels[colorRange] - 1);
                    list.Add(new Choice(g, range, weightStep * weightStep / 12, colorStep * colorStep / 12));
                }
            }

            return list.ToArray();
        }
    }

    /// <summary>A grid and weight range, with the mean squared quantisation error of a weight and of a colour value.</summary>
    private readonly record struct Choice(int Grid, int Range, double WeightError, double ColorError);

    /// <summary>A partitioning: its seed, a bit mask of each partition's texels (3 words each) and the size of the first.</summary>
    private sealed record Partitioning(int Seed, ulong[] Masks)
    {
        public int First { get; } = BitOperations.PopCount(Masks[0]) + BitOperations.PopCount(Masks[1]) + BitOperations.PopCount(Masks[2]);
    }

    /// <summary>Every ordering of 3 and of 4 partitions.</summary>
    private static readonly byte[] Permutations3 = [0, 1, 2, 0, 2, 1, 1, 0, 2, 1, 2, 0, 2, 0, 1, 2, 1, 0];

    private static readonly byte[] Permutations4 = BuildPermutations4();

    private static byte[] BuildPermutations4()
    {
        var list = new List<byte>();
        for (int a = 0; a < 4; a++)
        {
            for (int b = 0; b < 4; b++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int d = 6 - a - b - c;
                    if (a != b && a != c && b != c && d != a && d != b && d != c)
                    {
                        list.AddRange([(byte)a, (byte)b, (byte)c, (byte)d]);
                    }
                }
            }
        }

        return list.ToArray();
    }

    /// <summary>One configuration: partition count and seed, the second plane's channel, and its fitted endpoints and weights.</summary>
    private sealed class Config
    {
        public int Partitions { get; set; }

        public int Seed { get; set; }

        public int Plane2 { get; set; }

        public byte[]? Table { get; set; }

        /// <summary>Gets or sets the texels' squared distance from their partitions' lines.</summary>
        public double Residual { get; set; }

        /// <summary>Gets or sets the mean squared endpoint segment length of the first plane.</summary>
        public double Spread0 { get; set; }

        /// <summary>Gets or sets the mean squared endpoint segment length of the second plane.</summary>
        public double Spread1 { get; set; }

        /// <summary>Gets or sets how this configuration's grid errors scale from the configuration that estimated them.</summary>
        public double GridScale0 { get; set; }

        public double GridScale1 { get; set; }

        public double[] Endpoints { get; } = new double[4 * 8];

        public double[] Ideal { get; } = new double[MaxTexels * 2];

        public double[] Lengths { get; } = new double[MaxTexels * 2];
    }

    /// <summary>The search for one block, reused per thread.</summary>
    private sealed class Search
    {
        private readonly int[] _px = new int[MaxTexels * 4];
        private readonly ushort[] _decoded = new ushort[MaxTexels * 4];
        private readonly byte[] _trial = new byte[16];
        private readonly byte[] _best = new byte[16];
        private readonly Config[] _configs = Enumerable.Range(0, MaxConfigs).Select(_ => new Config()).ToArray();
        private readonly double[] _gridError = new double[2 * 256];
        private readonly double[] _gridErrorDual = new double[2 * 256];
        private readonly double[] _endpoints = new double[4 * 8];
        private readonly double[] _texelIdeal = new double[MaxTexels * 2];
        private readonly double[] _lengths = new double[MaxTexels * 2];
        private readonly double[] _gridIdeal = new double[MaxTexels];
        private readonly double[] _residual = new double[MaxTexels];
        private readonly int[] _weights = new int[MaxTexels * 2];
        private readonly int[] _sums = new int[MaxTexels * 2];
        private readonly int[] _colors = new int[18];
        private readonly int[] _ends = new int[4 * 8];
        private readonly (long Estimate, int Config, int Grid, int Range)[] _trials = new (long, int, int, int)[64];
        private Astc _astc = null!;
        private Footprint _fp = null!;
        private int _texels;
        private int _cem;
        private int _channels;
        private int _configCount;
        private int _firstDual;
        private AstcProfile _profile;
        private Settings _settings;
        private long _bestError;

        public void Run(Astc astc, Footprint fp, ReadOnlySpan<byte> rgba, int cem, AstcProfile profile, Settings settings, Span<byte> block)
        {
            _astc = astc;
            _fp = fp;
            _texels = astc.Texels;
            for (int i = 0; i < _texels * 4; i++)
            {
                _px[i] = rgba[i];
            }

            _cem = cem;
            _channels = ((cem >> 2) + 1) * 2;
            _profile = profile;
            _settings = settings;
            _bestError = long.MaxValue;
            _configCount = 0;
            _firstDual = -1;

            // One partition, one plane: its weights also stand in for every configuration's grid estimate.
            AddConfig(1, 0, -1, null);
            if (settings.Dual && cem != 0)
            {
                Span<int> order = stackalloc int[4];
                int channels = cem == 4 ? Single(order, 3) : DualCandidates(order, settings.AllDualChannels);
                for (int i = 0; i < channels; i++)
                {
                    AddConfig(1, 0, order[i], null);
                }
            }

            Span<int> seeds = stackalloc int[16];
            for (int partitions = 2; partitions <= 4; partitions++)
            {
                int count = settings.Seeds(partitions);
                if (count == 0 || _texels < partitions * 2 || _channels * partitions > 18)
                {
                    continue;
                }

                int found = BestSeeds(partitions, seeds[..count]);
                for (int i = 0; i < found; i++)
                {
                    AddConfig(partitions, seeds[i], -1, astc.PartitionTable(partitions, seeds[i]));
                }
            }

            int trials = RankTrials();
            if (trials > settings.Trials)
            {
                // A quick encode of every candidate (no refits or weight adjustments) picks the ones worth refining.
                for (int i = 0; i < trials; i++)
                {
                    (_, int c, int g, int r) = _trials[i];
                    _trials[i].Estimate = Trial(_configs[c], _fp.Grids[g], r, quick: true);
                }

                _trials.AsSpan(0, trials).Sort((a, b) => a.Estimate.CompareTo(b.Estimate));
                trials = settings.Trials;
            }

            for (int i = 0; i < trials; i++)
            {
                (_, int c, int g, int r) = _trials[i];
                Trial(_configs[c], _fp.Grids[g], r, quick: false);
            }

            _best.CopyTo(block);
        }

        private static int Single(Span<int> order, int channel)
        {
            order[0] = channel;
            return 1;
        }

        /// <summary>Orders the colour channels for a second weight plane, least correlated first; returns how many to try.</summary>
        private int DualCandidates(Span<int> order, bool all)
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
        private int BestSeeds(int count, Span<int> output)
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
            int words = (_texels + 63) >> 6;
            int cluster0 = 0;
            for (int w = 0; w < words; w++)
            {
                cluster0 += BitOperations.PopCount(masks[w]);
            }

            foreach (Partitioning p in all)
            {
                int matched;
                if (count == 2)
                {
                    // Texels both sets put in their first part, and (by counting) both in their second.
                    int both = 0;
                    for (int w = 0; w < words; w++)
                    {
                        both += BitOperations.PopCount(p.Masks[w] & masks[w]);
                    }

                    int same = (2 * both) + _texels - p.First - cluster0;
                    matched = Math.Max(same, _texels - same);
                }
                else
                {
                    for (int a = 0; a < count; a++)
                    {
                        for (int b = 0; b < count; b++)
                        {
                            int sum = 0;
                            for (int w = 0; w < words; w++)
                            {
                                sum += BitOperations.PopCount(p.Masks[(a * 3) + w] & masks[(b * 3) + w]);
                            }

                            overlap[(a * count) + b] = sum;
                        }
                    }

                    matched = BestMatch(overlap, count);
                }

                int score = _texels - matched;
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

        /// <summary>Returns the most texels any pairing of the partitions with the clusters puts in matching places.</summary>
        private static int BestMatch(ReadOnlySpan<int> overlap, int count)
        {
            byte[] permutations = count == 3 ? Permutations3 : Permutations4;
            int best = 0;
            for (int i = 0; i < permutations.Length; i += count)
            {
                int sum = 0;
                for (int k = 0; k < count; k++)
                {
                    sum += overlap[(k * count) + permutations[i + k]];
                }

                best = Math.Max(best, sum);
            }

            return best;
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

        private void AddConfig(int partitions, int seed, int plane2, byte[]? table)
        {
            Config config = _configs[_configCount++];
            config.Partitions = partitions;
            config.Seed = seed;
            config.Plane2 = plane2;
            config.Table = table;
            config.Residual = 0;
            for (int p = 0; p < partitions; p++)
            {
                config.Residual += FitPartition(config, p);
            }

            Project(config, config.Endpoints, config.Ideal, config.Lengths);
            config.Spread0 = Mean(config.Lengths.AsSpan(0, _texels));
            config.Spread1 = plane2 >= 0 ? Mean(config.Lengths.AsSpan(_texels, _texels)) : 0;
            config.GridScale1 = 0;
            if (_configCount == 1)
            {
                GridErrors(config, _gridError, 1);
                config.GridScale0 = 1;
            }
            else if (plane2 < 0)
            {
                Config first = _configs[0];
                config.GridScale0 = first.Spread0 > 1e-9 ? config.Spread0 / first.Spread0 : 0;
            }
            else if (_firstDual < 0)
            {
                // Dual planes get their own grid estimate; the first dual configuration provides it.
                _firstDual = _configCount - 1;
                GridErrors(config, _gridErrorDual, 2);
                config.GridScale0 = config.GridScale1 = 1;
            }
            else
            {
                Config dual = _configs[_firstDual];
                config.GridScale0 = dual.Spread0 > 1e-9 ? config.Spread0 / dual.Spread0 : 0;
                config.GridScale1 = dual.Spread1 > 1e-9 ? config.Spread1 / dual.Spread1 : 0;
            }
        }

        private double Mean(ReadOnlySpan<double> values)
        {
            double sum = 0;
            foreach (double v in values)
            {
                sum += v;
            }

            return sum / _texels;
        }

        /// <summary>Fits partition <paramref name="p"/>'s endpoints along its principal axis; returns the texels' squared distance from the line.</summary>
        private double FitPartition(Config config, int p)
        {
            byte[]? table = config.Table;
            int plane2 = config.Plane2;
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
                    for (int b = a; b < 4; b++)
                    {
                        if (b != plane2)
                        {
                            cov[(a * 4) + b] += da * (_px[(t * 4) + b] - mean[b]);
                        }
                    }
                }
            }

            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < a; b++)
                {
                    cov[(a * 4) + b] = cov[(b * 4) + a];
                }
            }

            // Power iteration from the channel with the most variance.
            int start = 0;
            for (int c = 1; c < 4; c++)
            {
                if (cov[(c * 4) + c] > cov[(start * 4) + start])
                {
                    start = c;
                }
            }

            Span<double> axis = stackalloc double[4];
            Span<double> next = stackalloc double[4];
            axis[start] = 1;
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
            double residual = 0;
            for (int t = 0; t < _texels; t++)
            {
                if (table is not null && table[t] != p)
                {
                    continue;
                }

                double s = 0, total = 0;
                for (int c = 0; c < 4; c++)
                {
                    if (c != plane2)
                    {
                        double d = _px[(t * 4) + c] - mean[c];
                        s += d * axis[c];
                        total += d * d;
                    }
                }

                residual += Math.Max(0, total - (s * s));
                low = Math.Min(low, s);
                high = Math.Max(high, s);
                if (plane2 >= 0)
                {
                    lowAlone = Math.Min(lowAlone, _px[(t * 4) + plane2]);
                    highAlone = Math.Max(highAlone, _px[(t * 4) + plane2]);
                }
            }

            Span<double> e = config.Endpoints.AsSpan(p * 8, 8);
            for (int c = 0; c < 4; c++)
            {
                e[c] = Math.Clamp(mean[c] + (low * axis[c]), 0, 255);
                e[4 + c] = Math.Clamp(mean[c] + (high * axis[c]), 0, 255);
            }

            if (plane2 >= 0)
            {
                e[plane2] = lowAlone;
                e[4 + plane2] = highAlone;
            }

            return residual;
        }

        /// <summary>Projects every texel onto its partition's endpoint segment(s) for its ideal weight(s) and the segment's squared length.</summary>
        private void Project(Config config, ReadOnlySpan<double> endpoints, Span<double> ideal, Span<double> lengths)
        {
            int plane2 = config.Plane2;
            for (int t = 0; t < _texels; t++)
            {
                int p = config.Table is null ? 0 : config.Table[t];
                ReadOnlySpan<double> e = endpoints.Slice(p * 8, 8);
                double dd = 0, s = 0;
                for (int c = 0; c < 4; c++)
                {
                    if (c != plane2)
                    {
                        double d = e[4 + c] - e[c];
                        dd += d * d;
                        s += (_px[(t * 4) + c] - e[c]) * d;
                    }
                }

                ideal[t] = dd > 1e-9 ? Math.Clamp(s / dd, 0, 1) : 0.5;
                lengths[t] = dd;
                if (plane2 >= 0)
                {
                    double alone = e[4 + plane2] - e[plane2];
                    ideal[_texels + t] = Math.Abs(alone) > 1e-9 ? Math.Clamp((_px[(t * 4) + plane2] - e[plane2]) / alone, 0, 1) : 0.5;
                    lengths[_texels + t] = alone * alone;
                }
            }
        }

        /// <summary>Estimates for every grid, per plane, the error its resolution adds (in colour units squared).</summary>
        private void GridErrors(Config config, double[] output, int planes)
        {
            Span<double> columns = stackalloc double[13];
            Span<double> rows = stackalloc double[13];
            Grid[] grids = _fp.Grids;
            for (int plane = 0; plane < planes; plane++)
            {
                ReadOnlySpan<double> ideal = config.Ideal.AsSpan(plane * _texels, _texels);
                ReadOnlySpan<double> lengths = config.Lengths.AsSpan(plane * _texels, _texels);
                columns.Clear();
                rows.Clear();
                for (int w = 2; w < _astc.Width; w++)
                {
                    Fit(_fp.Columns[w]!, ideal, lengths, _gridIdeal, 0);
                    columns[w] = Residual(_fp.Columns[w]!, _gridIdeal, ideal, lengths);
                }

                for (int h = 2; h < _astc.Height; h++)
                {
                    Fit(_fp.Rows[h]!, ideal, lengths, _gridIdeal, 0);
                    rows[h] = Residual(_fp.Rows[h]!, _gridIdeal, ideal, lengths);
                }

                for (int g = 0; g < grids.Length; g++)
                {
                    output[(plane * 256) + g] = columns[grids[g].Width] + rows[grids[g].Height];
                }
            }
        }

        private double Residual(Grid grid, ReadOnlySpan<double> gridWeights, ReadOnlySpan<double> ideal, ReadOnlySpan<double> lengths)
        {
            ReadOnlySpan<short> index = grid.Infill.Indices;
            ReadOnlySpan<byte> weight = grid.Infill.Weights;
            double sum = 0;
            for (int t = 0; t < _texels; t++)
            {
                double w = 0;
                for (int k = 0; k < 4; k++)
                {
                    int f = weight[(t * 4) + k];
                    if (f != 0)
                    {
                        w += gridWeights[index[(t * 4) + k]] * f;
                    }
                }

                double d = (w / 16) - ideal[t];
                sum += d * d * lengths[t];
            }

            return sum;
        }

        /// <summary>
        /// Fits grid weights to texel weights: each grid point starts as the weighted mean of the texels it feeds, and
        /// <paramref name="steps"/> damped least-squares steps correct for the infill's blending.
        /// </summary>
        private void Fit(Grid grid, ReadOnlySpan<double> texels, ReadOnlySpan<double> importance, Span<double> output, int steps)
        {
            if (grid.Full)
            {
                // Grid point g feeds texel g alone.
                texels.CopyTo(output);
                return;
            }

            for (int g = 0; g < grid.Count; g++)
            {
                double sum = 0, total = 0;
                foreach ((short t, byte w) in grid.Feeds[g])
                {
                    sum += texels[t] * w;
                    total += w;
                }

                output[g] = total > 0 ? sum / total : 0.5;
            }

            ReadOnlySpan<short> index = grid.Infill.Indices;
            ReadOnlySpan<byte> weight = grid.Infill.Weights;
            Span<double> residual = _residual;
            for (int step = 0; step < steps; step++)
            {
                for (int t = 0; t < _texels; t++)
                {
                    double w = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int f = weight[(t * 4) + k];
                        if (f != 0)
                        {
                            w += output[index[(t * 4) + k]] * f;
                        }
                    }

                    residual[t] = texels[t] - (w / 16);
                }

                for (int g = 0; g < grid.Count; g++)
                {
                    double num = 0, den = 0;
                    foreach ((short t, byte w) in grid.Feeds[g])
                    {
                        double f = w / 16.0;
                        double l = importance[t] + 1;
                        num += f * residual[t] * l;
                        den += f * f * l;
                    }

                    if (den > 0)
                    {
                        output[g] = Math.Clamp(output[g] + (0.75 * num / den), 0, 1);
                    }
                }
            }
        }

        /// <summary>Ranks every (configuration, grid, weight range) by an error estimate and keeps the best few; returns how many.</summary>
        private int RankTrials()
        {
            int limit = _settings.Candidates;
            int count = 0;
            for (int ci = 0; ci < _configCount; ci++)
            {
                Config config = _configs[ci];
                bool dual = config.Plane2 >= 0;
                int planes = dual ? 2 : 1;
                double[] gridError = dual ? _gridErrorDual : _gridError;
                double spread = _texels * (config.Spread0 + config.Spread1);
                double colorWeight = _texels * (_channels / 2) / 2.0;
                foreach (Choice choice in _fp.Choices(_channels * config.Partitions, config.Partitions > 1, dual))
                {
                    {
                        int g = choice.Grid;
                        double resolution = (gridError[g] * config.GridScale0) + (dual ? gridError[256 + g] * config.GridScale1 : 0);
                        double estimate = config.Residual + resolution + (spread * choice.WeightError) + (colorWeight * choice.ColorError);
                        long key = (long)(estimate * 16);
                        if (count == limit && key >= _trials[count - 1].Estimate)
                        {
                            continue;
                        }

                        if (count < limit)
                        {
                            count++;
                        }

                        int i = count - 1;
                        for (; i > 0 && key < _trials[i - 1].Estimate; i--)
                        {
                            _trials[i] = _trials[i - 1];
                        }

                        _trials[i] = (key, ci, g, choice.Range);
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Encodes one configuration with one grid and weight range, refines it, and keeps it if it is the best yet; a quick
        /// trial only quantises the first fit and returns its error.
        /// </summary>
        private long Trial(Config config, Grid grid, int range, bool quick)
        {
            bool dual = config.Plane2 >= 0;
            int planes = dual ? 2 : 1;
            int mode = grid.Modes[range + (dual ? 12 : 0)];
            int configBits = config.Partitions == 1 ? 17 : 29;
            int weightBits = AstcIse.BitCount(grid.Count * planes, range);
            int colorBits = 128 - weightBits - configBits - (dual ? 2 : 0);
            int colorCount = _channels * config.Partitions;
            int colorRange = Astc.ColorRange(colorCount, colorBits);
            WeightTables tables = Weights[range];

            config.Endpoints.CopyTo(_endpoints, 0);
            config.Ideal.AsSpan(0, _texels * planes).CopyTo(_texelIdeal);
            config.Lengths.AsSpan(0, _texels * planes).CopyTo(_lengths);
            Span<int> weights = _weights.AsSpan(0, grid.Count * planes);
            Span<int> colors = _colors.AsSpan(0, colorCount);
            for (int iteration = 0; ; iteration++)
            {
                for (int plane = 0; plane < planes; plane++)
                {
                    Fit(grid, _texelIdeal.AsSpan(plane * _texels, _texels), _lengths.AsSpan(plane * _texels, _texels), _gridIdeal, _settings.Smooth);
                    for (int g = 0; g < grid.Count; g++)
                    {
                        weights[(g * planes) + plane] = tables.Nearest[(int)Math.Round(Math.Clamp(_gridIdeal[g], 0, 1) * 64)];
                    }
                }

                Infill(grid, weights, planes, tables);
                if (quick)
                {
                    QuantizeColors(config.Partitions, colorRange, colors);
                    return BlockError(config);
                }

                if (_settings.Refits > 0)
                {
                    for (int p = 0; p < config.Partitions; p++)
                    {
                        RefitEndpoints(config, p);
                    }
                }

                QuantizeColors(config.Partitions, colorRange, colors);
                if (_settings.Realign > 0 && iteration >= _settings.Refits)
                {
                    Realign(config, grid, weights, planes, tables);
                }

                Pack(mode, config, range, colorRange, weights, colors);
                Score();
                if (iteration >= _settings.Refits)
                {
                    return 0;
                }

                // Reproject the texels onto the quantised endpoints for the next round's weights.
                for (int i = 0; i < 4 * 8; i++)
                {
                    _endpoints[i] = _ends[i] / (_profile == AstcProfile.Srgb ? 1.0 : 257.0);
                }

                Project(config, _endpoints, _texelIdeal, _lengths);
            }
        }

        /// <summary>Returns the error of the whole block for the current endpoints and texel weights.</summary>
        private long BlockError(Config config)
        {
            long error = 0;
            int plane2 = config.Plane2;
            bool srgb = _profile == AstcProfile.Srgb;
            for (int t = 0; t < _texels; t++)
            {
                int w = _sums[t] >> 4;
                int other = plane2 >= 0 ? _sums[_texels + t] >> 4 : w;
                int p = config.Table is null ? 0 : config.Table[t];
                ReadOnlySpan<int> e = _ends.AsSpan(p * 8, 8);
                for (int c = 0; c < 4; c++)
                {
                    int texelWeight = c == plane2 ? other : w;
                    int value = ((e[c] * (64 - texelWeight)) + (e[4 + c] * texelWeight) + 32) >> 6;
                    int d = (srgb ? value : value >> 8) - _px[(t * 4) + c];
                    error += d * d;
                }
            }

            return error;
        }

        /// <summary>Infills the quantised grid weights into each texel's weight, keeping the raw sums for weight adjustments.</summary>
        private void Infill(Grid grid, ReadOnlySpan<int> weights, int planes, WeightTables tables)
        {
            ReadOnlySpan<short> index = grid.Infill.Indices;
            ReadOnlySpan<byte> weight = grid.Infill.Weights;
            for (int plane = 0; plane < planes; plane++)
            {
                for (int t = 0; t < _texels; t++)
                {
                    int sum = 8;
                    for (int k = 0; k < 4; k++)
                    {
                        int f = weight[(t * 4) + k];
                        if (f != 0)
                        {
                            sum += tables.Unquantized[weights[(index[(t * 4) + k] * planes) + plane]] * f;
                        }
                    }

                    _sums[(plane * _texels) + t] = sum;
                }
            }
        }

        private void RefitEndpoints(Config config, int p)
        {
            byte[]? table = config.Table;
            for (int c = 0; c < 4; c++)
            {
                int plane = c == config.Plane2 ? 1 : 0;
                double a = 0, b = 0, d = 0, x0 = 0, x1 = 0;
                for (int t = 0; t < _texels; t++)
                {
                    if (table is not null && table[t] != p)
                    {
                        continue;
                    }

                    double w = (_sums[(plane * _texels) + t] >> 4) / 64.0;
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

                _endpoints[(p * 8) + c] = Math.Clamp(((d * x0) - (b * x1)) / det, 0, 255);
                _endpoints[(p * 8) + 4 + c] = Math.Clamp(((a * x1) - (b * x0)) / det, 0, 255);
            }
        }

        /// <summary>
        /// Quantises every partition's endpoints to <paramref name="range"/> in the colour endpoint mode's order, and keeps
        /// the endpoints a decoder expands them to (scaled as the profile interpolates them) for weight adjustments.
        /// </summary>
        private void QuantizeColors(int partitions, int range, Span<int> colors)
        {
            byte[] q = ColorQuantize[range];
            byte[] unq = AstcIse.ColorUnquantized[range];
            for (int p = 0; p < partitions; p++)
            {
                ReadOnlySpan<double> e = _endpoints.AsSpan(p * 8, 8);
                Span<int> v = colors.Slice(p * _channels, _channels);
                Span<int> ends = _ends.AsSpan(p * 8, 8);
                switch (_cem)
                {
                    case 0:
                    case 4:
                        v[0] = q[Round((e[0] + e[1] + e[2]) / 3)];
                        v[1] = q[Round((e[4] + e[5] + e[6]) / 3)];
                        ends[0] = ends[1] = ends[2] = unq[v[0]];
                        ends[4] = ends[5] = ends[6] = unq[v[1]];
                        if (_cem == 4)
                        {
                            v[2] = q[Round(e[3])];
                            v[3] = q[Round(e[7])];
                            ends[3] = unq[v[2]];
                            ends[7] = unq[v[3]];
                        }
                        else
                        {
                            ends[3] = ends[7] = 255;
                        }

                        break;
                    default:
                        for (int c = 0; c < _channels / 2; c++)
                        {
                            v[c * 2] = q[Round(e[c])];
                            v[(c * 2) + 1] = q[Round(e[4 + c])];
                        }

                        // The direct modes swap the endpoints and contract blue when endpoint 1 is the darker: step it up.
                        while (unq[v[1]] + unq[v[3]] + unq[v[5]] < unq[v[0]] + unq[v[2]] + unq[v[4]])
                        {
                            int channel = -1;
                            int gap = int.MinValue;
                            for (int k = 0; k < 3; k++)
                            {
                                int g = unq[v[k * 2]] - unq[v[(k * 2) + 1]];
                                if (g > gap && Next(unq, v[(k * 2) + 1]) >= 0)
                                {
                                    gap = g;
                                    channel = k;
                                }
                            }

                            if (channel < 0)
                            {
                                break;
                            }

                            v[(channel * 2) + 1] = Next(unq, v[(channel * 2) + 1]);
                        }

                        for (int c = 0; c < 4; c++)
                        {
                            bool present = c < _channels / 2;
                            ends[c] = present ? unq[v[c * 2]] : 255;
                            ends[4 + c] = present ? unq[v[(c * 2) + 1]] : 255;
                        }

                        break;
                }

                if (_profile == AstcProfile.Ldr)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        ends[c] *= 257;
                    }
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

        /// <summary>
        /// Moves each quantised grid weight one level up or down when that lowers the error of the texels it feeds, measured
        /// with the decoder's interpolation of the quantised endpoints.
        /// </summary>
        private void Realign(Config config, Grid grid, Span<int> weights, int planes, WeightTables tables)
        {
            int levels = tables.Sorted.Length;
            for (int pass = 0; pass < _settings.Realign; pass++)
            {
                bool changed = false;
                for (int plane = 0; plane < planes; plane++)
                {
                    for (int g = 0; g < grid.Count; g++)
                    {
                        int at = (g * planes) + plane;
                        int rank = tables.Rank[weights[at]];
                        int current = tables.Unquantized[weights[at]];
                        (short Texel, byte Weight)[] feeds = grid.Feeds[g];
                        long bestError = FeedError(config, feeds, plane, 0);
                        int bestRank = rank;
                        for (int step = -1; step <= 1; step += 2)
                        {
                            int r = rank + step;
                            if ((uint)r >= (uint)levels)
                            {
                                continue;
                            }

                            long error = FeedError(config, feeds, plane, tables.Unquantized[tables.Sorted[r]] - current);
                            if (error < bestError)
                            {
                                bestError = error;
                                bestRank = r;
                            }
                        }

                        if (bestRank != rank)
                        {
                            int delta = tables.Unquantized[tables.Sorted[bestRank]] - current;
                            foreach ((short t, byte f) in feeds)
                            {
                                _sums[(plane * _texels) + t] += delta * f;
                            }

                            weights[at] = tables.Sorted[bestRank];
                            changed = true;
                        }
                    }
                }

                if (!changed)
                {
                    break;
                }
            }
        }

        /// <summary>Returns the error of the texels a grid point feeds if its weight changed by <paramref name="delta"/>.</summary>
        private long FeedError(Config config, (short Texel, byte Weight)[] feeds, int plane, int delta)
        {
            long error = 0;
            int plane2 = config.Plane2;
            bool srgb = _profile == AstcProfile.Srgb;
            foreach ((short t, byte f) in feeds)
            {
                int w = (_sums[(plane * _texels) + t] + (delta * f)) >> 4;
                int other = plane2 >= 0 ? _sums[((1 - plane) * _texels) + t] >> 4 : w;
                int p = config.Table is null ? 0 : config.Table[t];
                ReadOnlySpan<int> e = _ends.AsSpan(p * 8, 8);
                for (int c = 0; c < 4; c++)
                {
                    // Channels on the other plane keep their weight; they still count, so errors compare like for like.
                    int texelWeight = (c == plane2) == (plane == 1) ? w : other;
                    int value = ((e[c] * (64 - texelWeight)) + (e[4 + c] * texelWeight) + 32) >> 6;
                    int d = (srgb ? value : value >> 8) - _px[(t * 4) + c];
                    error += d * d;
                }
            }

            return error;
        }

        private void Pack(int mode, Config config, int weightRange, int colorRange, ReadOnlySpan<int> weights, ReadOnlySpan<int> colors)
        {
            Span<byte> block = _trial;
            block.Clear();
            int offset = 0;
            AstcIse.Write(block, ref offset, mode, 11);
            AstcIse.Write(block, ref offset, config.Partitions - 1, 2);
            if (config.Partitions == 1)
            {
                AstcIse.Write(block, ref offset, _cem, 4);
            }
            else
            {
                AstcIse.Write(block, ref offset, config.Seed, 10);
                AstcIse.Write(block, ref offset, _cem << 2, 6);
            }

            AstcIse.Encode(colorRange, colors, block, offset);
            if (config.Plane2 >= 0)
            {
                int ccs = 128 - AstcIse.BitCount(weights.Length, weightRange) - 2;
                AstcIse.Write(block, ref ccs, config.Plane2, 2);
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
            Span<ushort> decoded = _decoded.AsSpan(0, _texels * 4);
            _astc.Decode(_trial, _profile, decoded);
            long error = 0;
            for (int i = 0; i < decoded.Length; i++)
            {
                int d = (decoded[i] >> 8) - _px[i];
                error += d * d;
            }

            if (error < _bestError)
            {
                _bestError = error;
                _trial.CopyTo(_best, 0);
            }
        }
    }
}
