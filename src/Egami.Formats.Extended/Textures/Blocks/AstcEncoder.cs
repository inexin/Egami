using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Encodes LDR ASTC blocks of every 2D footprint at effort 0 to 6. A constant block becomes a void-extent block. Otherwise
/// the encoder picks the colour endpoint class from the block's content (luminance, luminance-alpha, RGB or RGBA) and
/// searches in two stages. The first tries one partition: direct endpoints, RGB base+scale endpoints and dual weight planes.
/// Only a block that stage leaves above an error limit (which depends on the footprint and the effort) goes on to two to
/// four partitions: the partitionings nearest a k-means clustering of the texels, ordered by how well their partitions lie
/// on lines, each with direct, base+scale and mixed per-partition endpoint modes. Each configuration fits every partition's
/// endpoints; an estimate of the error of every block mode (the texels' distance from their lines, the grid's resolution and
/// the two quantisations) ranks them, a quick encode of the best candidates picks a few, and those are encoded for real:
/// grid weights fitted to the texels' ideal weights by exact least squares, then least-squares endpoint refits, direct or
/// base+offset quantisation, and weight and colour-value adjustments scored with the decoder's own arithmetic. The work
/// for one block depends only on that block, so the output never depends on how many threads encode.
/// </summary>
internal static class AstcEncoder
{
    private const int MaxTexels = 144;
    private const int MaxConfigs = 2 + 8 + (3 * (16 + 8 + 4)) + 1;

    private static readonly Footprint?[] Footprints = new Footprint?[14];

    /// <summary>The nearest colour value index of each 8-bit value, per colour range.</summary>
    internal static readonly byte[][] ColorQuantize = BuildColorQuantize();

    /// <summary>Per colour range: the value indices in value order, and the rank of each index.</summary>
    private static readonly (byte[] Sorted, byte[] Rank)[] ColorOrder = BuildColorOrder();

    /// <summary>Per weight range: the nearest value index of each weight 0 to 64, and the indices in value order with their ranks.</summary>
    private static readonly WeightTables[] Weights = BuildWeightTables();

    [ThreadStatic]
    private static Search? _search;

    /// <summary>The partition of every texel of a one-partition block.</summary>
    private static readonly byte[] Zeros = new byte[MaxTexels];

    /// <summary>A lane mask per channel, to give the second weight plane's channel its own weight.</summary>
    private static readonly Vector128<int>[] PlaneMasks = [.. Enumerable.Range(0, 4).Select(c => Vector128.Create(c == 0 ? -1 : 0, c == 1 ? -1 : 0, c == 2 ? -1 : 0, c == 3 ? -1 : 0))];

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

    private static (byte[], byte[])[] BuildColorOrder()
    {
        var tables = new (byte[], byte[])[21];
        for (int r = 0; r < 21; r++)
        {
            byte[] unq = AstcIse.ColorUnquantized[r];
            byte[] sorted = Enumerable.Range(0, unq.Length).OrderBy(i => unq[i]).Select(i => (byte)i).ToArray();
            byte[] rank = new byte[unq.Length];
            for (int i = 0; i < sorted.Length; i++)
            {
                rank[sorted[i]] = (byte)i;
            }

            tables[r] = (sorted, rank);
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

    /// <summary>
    /// How hard each effort searches: candidate and fully encoded block modes per stage, endpoint refits, weight
    /// adjustment passes, dual planes, partitionings tried per partition count (and how many more the clustering ranks),
    /// base+scale endpoints, exact least-squares grid fits, and the shift in dB of the error limits that end the search.
    /// </summary>
    private readonly record struct Settings(int Candidates, int Trials, int Refits, int Realign, bool Dual, bool AllDualChannels, int Seeds2, int Seeds3, int Seeds4, int Over, bool Exact, double LimitShift)
    {
        public static Settings For(int effort) => Math.Clamp(effort, 0, 6) switch
        {
            0 => new(1, 1, 0, 0, false, false, 0, 0, 0, 1, false, 0),
            1 => new(4, 1, 1, 1, false, false, 1, 0, 0, 2, true, -6),
            2 => new(8, 2, 1, 1, false, false, 2, 0, 0, 4, true, -4),
            3 => new(16, 2, 1, 1, true, false, 3, 1, 0, 6, true, -2),
            4 => new(24, 3, 1, 1, true, false, 4, 1, 0, 6, true, 0),
            5 => new(40, 4, 2, 2, true, true, 8, 4, 2, 8, true, 6),
            _ => new(64, 8, 3, 3, true, true, 16, 8, 4, 4, true, double.PositiveInfinity),
        };

        public int Seeds(int partitions) => partitions switch { 2 => Seeds2, 3 => Seeds3, _ => Seeds4 };

        /// <summary>
        /// Returns the PSNR above which one partition is good enough for a block of <paramref name="texels"/>: small
        /// footprints aim higher, since they have the bits to reach it (40 dB at 4x4, 30 dB at 8x8, 26 dB at 12x12).
        /// </summary>
        public double LimitDb(int texels) => LimitShift + (texels <= 64 ? 30 + (10 * Math.Min((64 - texels) / 48.0, 1)) : 30 - (4 * Math.Min((texels - 64) / 80.0, 1)));

        /// <summary>Returns the squared error per texel (four channels) of a block at <paramref name="db"/> dB PSNR.</summary>
        public static double Mse(double db) => 4 * 255 * 255 * Math.Pow(10, -db / 10);
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

        /// <summary>Gets or sets the least-squares matrix from texel weights to grid weights (see <see cref="Footprint.Stride"/>).</summary>
        public float[] Solve { get; set; } = [];

        /// <summary>Gets or sets each texel's four grid points, with those of no weight pointing at point 0.</summary>
        public int[] TapIndex { get; set; } = [];

        /// <summary>Gets or sets each texel's four bilinear weights (sixteenths).</summary>
        public int[] TapWeight { get; set; } = [];

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

        /// <summary>Gets the row length of every grid's least-squares matrix: the texel count rounded up to 8.</summary>
        public static int Stride(int texels) => (texels + 7) & ~7;

        /// <summary>
        /// Returns the matrix that maps texel weights to the grid weights whose infill fits them best by least squares,
        /// (AᵀA)⁻¹Aᵀ for the infill matrix A, one row of <see cref="Stride"/> per grid point.
        /// </summary>
        private static float[] LeastSquares(Grid grid, int texels, ReadOnlySpan<short> index, ReadOnlySpan<byte> weight)
        {
            int n = grid.Count;
            int stride = Stride(texels);
            var a = new double[texels, n];
            for (int t = 0; t < texels; t++)
            {
                for (int k = 0; k < 4; k++)
                {
                    if (weight[(t * 4) + k] != 0)
                    {
                        a[t, index[(t * 4) + k]] += weight[(t * 4) + k] / 16.0;
                    }
                }
            }

            // Gauss-Jordan elimination of [AᵀA + εI | Aᵀ].
            int columns = n + texels;
            var m = new double[n, columns];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double sum = i == j ? 1e-6 : 0;
                    for (int t = 0; t < texels; t++)
                    {
                        sum += a[t, i] * a[t, j];
                    }

                    m[i, j] = sum;
                }

                for (int t = 0; t < texels; t++)
                {
                    m[i, n + t] = a[t, i];
                }
            }

            for (int i = 0; i < n; i++)
            {
                int pivot = i;
                for (int r = i + 1; r < n; r++)
                {
                    if (Math.Abs(m[r, i]) > Math.Abs(m[pivot, i]))
                    {
                        pivot = r;
                    }
                }

                for (int j = 0; j < columns; j++)
                {
                    (m[i, j], m[pivot, j]) = (m[pivot, j], m[i, j]);
                }

                double scale = 1 / m[i, i];
                for (int j = 0; j < columns; j++)
                {
                    m[i, j] *= scale;
                }

                for (int r = 0; r < n; r++)
                {
                    double f = m[r, i];
                    if (r != i && f != 0)
                    {
                        for (int j = 0; j < columns; j++)
                        {
                            m[r, j] -= f * m[i, j];
                        }
                    }
                }
            }

            float[] solve = new float[n * stride];
            for (int g = 0; g < n; g++)
            {
                for (int t = 0; t < texels; t++)
                {
                    solve[(g * stride) + t] = (float)m[g, n + t];
                }
            }

            return solve;
        }

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
            grid.Solve = LeastSquares(grid, astc.Texels, index, weight);
            grid.TapIndex = new int[astc.Texels * 4];
            grid.TapWeight = new int[astc.Texels * 4];
            for (int i = 0; i < astc.Texels * 4; i++)
            {
                grid.TapWeight[i] = weight[i];
                grid.TapIndex[i] = weight[i] != 0 ? index[i] : 0;
            }

            grid.Full = grid.Width == astc.Width && grid.Height == astc.Height;
            return grid;
        }

        private readonly Choice[]?[] _choices = new Choice[]?[19 * 5 * 2];

        /// <summary>
        /// Gets every usable (grid, weight range) for <paramref name="colorCount"/> colour values, one or more partitions and
        /// one or two planes, with the colour range left for the endpoints.
        /// </summary>
        public Choice[] Choices(int colorCount, int configBits, bool dual)
        {
            int slot = configBits switch { 17 => 0, 29 => 1, 31 => 2, 34 => 3, _ => 4 };
            int key = (((colorCount * 5) + slot) * 2) + (dual ? 1 : 0);
            return _choices[key] ??= BuildChoices(colorCount, configBits, dual);
        }

        private Choice[] BuildChoices(int colorCount, int configBits, bool dual)
        {
            var list = new List<Choice>();
            int planes = dual ? 2 : 1;
            for (int g = 0; g < Grids.Length; g++)
            {
                Grid grid = Grids[g];
                int lastColorRange = -1;
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

                    // A lower weight range with the same colour range only loses precision.
                    if (colorRange == lastColorRange)
                    {
                        continue;
                    }

                    lastColorRange = colorRange;
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

        /// <summary>Gets or sets which partitions use the RGB base+scale mode (6, or 10 with alpha), one bit each.</summary>
        public int ScaleMask { get; set; }

        /// <summary>Gets whether some partitions use the base+scale mode and some not, which costs extra mode bits.</summary>
        public bool Mixed => ScaleMask != 0 && ScaleMask != (1 << Partitions) - 1;

        /// <summary>Gets or sets the colour values of all the partitions.</summary>
        public int Values { get; set; }

        /// <summary>Gets where each partition's colour values start, with the total last.</summary>
        public int[] Offsets { get; } = new int[5];

        /// <summary>Gets each partition's squared distance from its fitted endpoints.</summary>
        public double[] Residuals { get; } = new double[4];

        /// <summary>Gets the bits the block mode, partitioning and colour endpoint modes take.</summary>
        public int ConfigBits => Partitions == 1 ? 17 : 29 + (Mixed ? (3 * Partitions) - 4 : 0);

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
        private readonly float[] _fitInput = new float[MaxTexels];
        private readonly int[] _fitKeys = new int[48];
        private readonly double[] _fitValues = new double[48 * 128];
        private int _fitCount;
        private readonly byte[] _trial = new byte[16];
        private readonly byte[] _best = new byte[16];
        private readonly Config[] _configs = Enumerable.Range(0, MaxConfigs).Select(_ => new Config()).ToArray();
        private readonly double[] _gridError = new double[2 * 256];
        private readonly double[] _gridErrorDual = new double[2 * 256];
        private readonly double[] _endpoints = new double[4 * 8];
        private readonly double[] _gridIdeal = new double[MaxTexels];
        private readonly int[] _weights = new int[MaxTexels * 2];
        private readonly int[] _sums = new int[MaxTexels * 2];
        private readonly int[] _colors = new int[18];
        private readonly int[] _ends = new int[4 * 8];
        private readonly (long Estimate, int Config, int Grid, int Range)[] _trials = new (long, int, int, int)[64];
        private Astc _astc = null!;
        private Footprint _fp = null!;
        private int _texels;
        private int _cem;
        private readonly int[] _cems = new int[4];
        private int _channels;
        private int _configCount;
        private int _firstDual;
        private AstcProfile _profile;
        private Settings _settings;
        private long _bestError;
        private double _stopError;

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
            _fitCount = 0;
            double limitDb = settings.LimitDb(_texels);
            double limit = _texels * Settings.Mse(limitDb);
            _stopError = _texels * Settings.Mse(limitDb + 6);

            // One partition, one plane: its weights also stand in for every configuration's grid estimate.
            AddConfig(1, 0, -1, null, false);
            bool scale = cem >= 8 && settings.Exact;
            if (scale)
            {
                AddConfig(1, 0, -1, null, true);
            }

            if (settings.Dual && cem != 0)
            {
                Span<int> order = stackalloc int[4];
                int channels = cem == 4 ? Single(order, 3) : DualCandidates(order, settings.AllDualChannels);
                for (int i = 0; i < channels; i++)
                {
                    AddConfig(1, 0, order[i], null, false);
                    if (scale)
                    {
                        AddConfig(1, 0, order[i], null, true);
                    }
                }
            }

            RunStage(0, settings.Candidates, settings.Trials);

            // Blocks one partition already encodes well enough skip the partitioned search.
            if (_bestError > limit)
            {
                int from = _configCount;
                Span<int> seeds = stackalloc int[64];
                int scaleInts = cem == 12 ? 6 : 4;
                for (int partitions = 2; partitions <= 4; partitions++)
                {
                    int count = settings.Seeds(partitions);
                    bool direct = _channels * partitions <= 18;
                    bool scaled = scale && scaleInts * partitions <= 18;
                    if (count == 0 || _texels < partitions * 2 || !(direct || scaled))
                    {
                        continue;
                    }

                    int found = BestSeeds(partitions, seeds[..Math.Min(count * settings.Over, seeds.Length)]);
                    if (found > count)
                    {
                        found = RefineSeeds(partitions, seeds[..found], count);
                    }

                    for (int i = 0; i < found; i++)
                    {
                        byte[] table = astc.PartitionTable(partitions, seeds[i]);
                        if (direct)
                        {
                            AddConfig(partitions, seeds[i], -1, table, false);
                        }

                        if (scaled)
                        {
                            AddConfig(partitions, seeds[i], -1, table, true);
                        }

                        if (direct && scaled)
                        {
                            // Partitions that lie on a line through black take the cheaper base+scale mode.
                            Config d = _configs[_configCount - 2], sc = _configs[_configCount - 1];
                            int mask = 0, values = 0;
                            for (int p = 0; p < partitions; p++)
                            {
                                bool useScale = sc.Residuals[p] <= (d.Residuals[p] * 1.5) + 1;
                                mask |= useScale ? 1 << p : 0;
                                values += useScale ? scaleInts : _channels;
                            }

                            if (mask != 0 && mask != (1 << partitions) - 1 && values <= 18)
                            {
                                AddMixed(_configCount - 2, _configCount - 1, mask);
                            }
                        }
                    }
                }

                RunStage(from, settings.Candidates, settings.Trials);
            }

            _best.CopyTo(block);
        }

        /// <summary>
        /// Keeps the <paramref name="count"/> partitionings of <paramref name="seeds"/> whose partitions lie nearest their
        /// fitted lines, in that order; returns the count.
        /// </summary>
        private int RefineSeeds(int partitions, Span<int> seeds, int count)
        {
            Config scratch = _configs[_configCount];
            scratch.Partitions = partitions;
            scratch.Plane2 = -1;
            Span<double> residuals = stackalloc double[seeds.Length];
            for (int i = 0; i < seeds.Length; i++)
            {
                scratch.Table = _astc.PartitionTable(partitions, seeds[i]);
                double residual = 0;
                for (int p = 0; p < partitions; p++)
                {
                    residual += FitPartition(scratch, p);
                }

                residuals[i] = residual;
            }

            for (int i = 0; i < count; i++)
            {
                int best = i;
                for (int j = i + 1; j < seeds.Length; j++)
                {
                    if (residuals[j] < residuals[best])
                    {
                        best = j;
                    }
                }

                (seeds[i], seeds[best]) = (seeds[best], seeds[i]);
                (residuals[i], residuals[best]) = (residuals[best], residuals[i]);
            }

            return count;
        }

        /// <summary>Ranks the configurations from <paramref name="from"/> on, quick-encodes the best candidates and fully encodes the best few.</summary>
        private void RunStage(int from, int candidates, int full)
        {
            int trials = RankTrials(from, candidates);
            if (trials > full)
            {
                // A quick encode of every candidate (no refits or weight adjustments) picks the ones worth refining.
                for (int i = 0; i < trials; i++)
                {
                    (_, int c, int g, int r) = _trials[i];
                    _trials[i].Estimate = Trial(c, g, r, quick: true);
                }

                _trials.AsSpan(0, trials).Sort((a, b) => a.Estimate.CompareTo(b.Estimate));
                trials = full;
            }

            for (int i = 0; i < trials; i++)
            {
                (_, int c, int g, int r) = _trials[i];
                Trial(c, g, r, quick: false);
                if (_bestError <= _stopError)
                {
                    break;
                }
            }
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

        private void AddConfig(int partitions, int seed, int plane2, byte[]? table, bool scale)
        {
            Config config = _configs[_configCount++];
            config.Partitions = partitions;
            config.Seed = seed;
            config.Plane2 = plane2;
            config.Table = table;
            config.ScaleMask = scale ? (1 << partitions) - 1 : 0;
            Layout(config);
            config.Residual = 0;
            for (int p = 0; p < partitions; p++)
            {
                config.Residuals[p] = scale ? FitScale(config, p) : FitPartition(config, p);
                config.Residual += config.Residuals[p];
            }

            Finish(config);
        }

        /// <summary>Sets where each partition's colour values go.</summary>
        private void Layout(Config config)
        {
            int scaleValues = _cem == 12 ? 6 : 4;
            for (int p = 0; p < config.Partitions; p++)
            {
                config.Offsets[p + 1] = config.Offsets[p] + ((config.ScaleMask & (1 << p)) != 0 ? scaleValues : _channels);
            }

            config.Values = config.Offsets[config.Partitions];
        }

        /// <summary>
        /// Adds the configuration that takes each partition from the direct configuration <paramref name="direct"/> or the
        /// base+scale one <paramref name="scaled"/> of the same partitioning, as <paramref name="mask"/> picks.
        /// </summary>
        private void AddMixed(int direct, int scaled, int mask)
        {
            Config a = _configs[direct], b = _configs[scaled];
            Config config = _configs[_configCount++];
            config.Partitions = a.Partitions;
            config.Seed = a.Seed;
            config.Plane2 = -1;
            config.Table = a.Table;
            config.ScaleMask = mask;
            Layout(config);
            config.Residual = 0;
            for (int p = 0; p < config.Partitions; p++)
            {
                Config from = (mask & (1 << p)) != 0 ? b : a;
                from.Endpoints.AsSpan(p * 8, 8).CopyTo(config.Endpoints.AsSpan(p * 8, 8));
                config.Residuals[p] = from.Residuals[p];
                config.Residual += from.Residuals[p];
            }

            Finish(config);
        }

        /// <summary>Projects the texels on a fitted configuration and sets how its grid errors scale.</summary>
        private void Finish(Config config)
        {
            int plane2 = config.Plane2;
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
                // The single plane's estimate, scaled to each plane's spread, stands in for the dual planes'.
                Config first = _configs[0];
                double inverse = first.Spread0 > 1e-9 ? 1 / first.Spread0 : 0;
                for (int g = 0; g < _fp.Grids.Length; g++)
                {
                    _gridErrorDual[g] = _gridError[g] * config.Spread0 * inverse;
                    _gridErrorDual[256 + g] = _gridError[g] * config.Spread1 * inverse;
                }

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

        /// <summary>
        /// Fits partition <paramref name="p"/>'s endpoints for the base+scale modes: RGB along the line through black that best
        /// fits the texels, alpha (on the same plane) by least squares on the texels' positions along it. Returns the squared
        /// error of the texels from the fitted segment.
        /// </summary>
        private double FitScale(Config config, int p)
        {
            byte[]? table = config.Table;
            int alone = config.Plane2;
            bool alpha = _cem == 12;

            // The RGB channels that follow the line through black; a second plane's channel ranges on its own.
            double kr = alone == 0 ? 0 : 1, kg = alone == 1 ? 0 : 1, kb = alone == 2 ? 0 : 1;
            double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
            for (int t = 0; t < _texels; t++)
            {
                if (table is not null && table[t] != p)
                {
                    continue;
                }

                double r = _px[t * 4] * kr, g = _px[(t * 4) + 1] * kg, b = _px[(t * 4) + 2] * kb;
                m00 += r * r;
                m01 += r * g;
                m02 += r * b;
                m11 += g * g;
                m12 += g * b;
                m22 += b * b;
            }

            double ux = 0.577 * kr, uy = 0.577 * kg, uz = 0.577 * kb;
            for (int i = 0; i < 6; i++)
            {
                double nx = (m00 * ux) + (m01 * uy) + (m02 * uz);
                double ny = (m01 * ux) + (m11 * uy) + (m12 * uz);
                double nz = (m02 * ux) + (m12 * uy) + (m22 * uz);
                double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                if (length < 1e-9)
                {
                    break;
                }

                (ux, uy, uz) = (nx / length, ny / length, nz / length);
            }

            double low = double.MaxValue, high = 0;
            double residual = 0;
            double lowAlone = 255, highAlone = 0;
            int aloneChannel = alone >= 0 ? alone : 3;
            for (int t = 0; t < _texels; t++)
            {
                if (table is not null && table[t] != p)
                {
                    continue;
                }

                double r = _px[t * 4] * kr, g = _px[(t * 4) + 1] * kg, b = _px[(t * 4) + 2] * kb;
                double s = (r * ux) + (g * uy) + (b * uz);
                residual += Math.Max(0, (r * r) + (g * g) + (b * b) - (s * s));
                low = Math.Min(low, s);
                high = Math.Max(high, s);
                lowAlone = Math.Min(lowAlone, _px[(t * 4) + aloneChannel]);
                highAlone = Math.Max(highAlone, _px[(t * 4) + aloneChannel]);
            }

            Span<double> e = config.Endpoints.AsSpan(p * 8, 8);
            e[0] = Math.Clamp(low * ux, 0, 255);
            e[1] = Math.Clamp(low * uy, 0, 255);
            e[2] = Math.Clamp(low * uz, 0, 255);
            e[4] = Math.Clamp(high * ux, 0, 255);
            e[5] = Math.Clamp(high * uy, 0, 255);
            e[6] = Math.Clamp(high * uz, 0, 255);
            e[3] = e[7] = 255;
            if (alone >= 0)
            {
                e[alone] = lowAlone;
                e[4 + alone] = highAlone;
            }

            if (alpha && alone != 3)
            {
                // Alpha follows the texels' positions along the RGB segment: a least-squares line.
                double span = high - low;
                double a = 0, bb = 0, d = 0, x0 = 0, x1 = 0, sumA2 = 0;
                for (int t = 0; t < _texels; t++)
                {
                    if (table is not null && table[t] != p)
                    {
                        continue;
                    }

                    double s = (_px[t * 4] * kr * ux) + (_px[(t * 4) + 1] * kg * uy) + (_px[(t * 4) + 2] * kb * uz);
                    double w = span > 1e-9 ? (s - low) / span : 0.5;
                    double v = _px[(t * 4) + 3];
                    a += (1 - w) * (1 - w);
                    bb += (1 - w) * w;
                    d += w * w;
                    x0 += (1 - w) * v;
                    x1 += w * v;
                    sumA2 += v * v;
                }

                double det = (a * d) - (bb * bb);
                double a0, a1;
                if (Math.Abs(det) < 1e-9)
                {
                    a0 = a1 = (x0 + x1) / Math.Max(a + (2 * bb) + d, 1e-9);
                }
                else
                {
                    a0 = ((d * x0) - (bb * x1)) / det;
                    a1 = ((a * x1) - (bb * x0)) / det;
                }

                // The fitted line's squared error: sum (v - fit)^2 = sum v^2 - 2 sum v fit + sum fit^2.
                double fit2 = (a0 * a0 * a) + (2 * a0 * a1 * bb) + (a1 * a1 * d);
                residual += Math.Max(0, sumA2 - (2 * ((a0 * x0) + (a1 * x1))) + fit2);
                e[3] = Math.Clamp(a0, 0, 255);
                e[7] = Math.Clamp(a1, 0, 255);
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
                    Fit(_fp.Columns[w]!, ideal, _gridIdeal);
                    columns[w] = Residual(_fp.Columns[w]!, _gridIdeal, ideal, lengths);
                }

                for (int h = 2; h < _astc.Height; h++)
                {
                    Fit(_fp.Rows[h]!, ideal, _gridIdeal);
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
            int[] index = grid.TapIndex;
            int[] tap = grid.TapWeight;
            double sum = 0;
            for (int t = 0; t < _texels; t++)
            {
                int i = t * 4;
                double w = (gridWeights[index[i]] * tap[i]) + (gridWeights[index[i + 1]] * tap[i + 1]) + (gridWeights[index[i + 2]] * tap[i + 2]) + (gridWeights[index[i + 3]] * tap[i + 3]);
                double d = (w / 16) - ideal[t];
                sum += d * d * lengths[t];
            }

            return sum;
        }

        /// <summary>Fits grid weights to texel weights by least squares, with the grid's precomputed matrix.</summary>
        private void FitExact(Grid grid, ReadOnlySpan<double> texels, Span<double> output)
        {
            if (grid.Full)
            {
                texels.CopyTo(output);
                return;
            }

            int stride = Footprint.Stride(_texels);
            Span<float> x = _fitInput.AsSpan(0, stride);
            for (int t = 0; t < texels.Length; t++)
            {
                x[t] = (float)texels[t];
            }

            ref float solve = ref MemoryMarshal.GetArrayDataReference(grid.Solve);
            ref float input = ref MemoryMarshal.GetArrayDataReference(_fitInput);
            for (int g = 0; g < grid.Count; g++)
            {
                Vector256<float> sum = Vector256<float>.Zero;
                nuint row = (nuint)(g * stride);
                for (int t = 0; t < stride; t += 8)
                {
                    sum += Vector256.LoadUnsafe(ref solve, row + (nuint)t) * Vector256.LoadUnsafe(ref input, (nuint)t);
                }

                Vector128<float> half = sum.GetLower() + sum.GetUpper();
                output[g] = (half.GetElement(0) + half.GetElement(2)) + (half.GetElement(1) + half.GetElement(3));
            }
        }

        /// <summary>Fits grid weights to texel weights roughly: each grid point takes the weighted mean of the texels it feeds.</summary>
        private static void Fit(Grid grid, ReadOnlySpan<double> texels, Span<double> output)
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
        }

        /// <summary>Ranks every (configuration, grid, weight range) by an error estimate and keeps the best few; returns how many.</summary>
        private int RankTrials(int from, int limit)
        {
            int count = 0;
            for (int ci = from; ci < _configCount; ci++)
            {
                Config config = _configs[ci];
                bool dual = config.Plane2 >= 0;
                int planes = dual ? 2 : 1;
                double[] gridError = dual ? _gridErrorDual : _gridError;
                double spread = _texels * (config.Spread0 + config.Spread1);
                double colorWeight = _texels * (_channels / 2) / 2.0;
                double weightShare = _settings.Realign > 0 ? 0.25 : 1;
                foreach (Choice choice in _fp.Choices(config.Values, config.ConfigBits, dual))
                {
                    // The weight quantisation counts a quarter when the weight and colour-value adjustments win most of it back.
                    int g = choice.Grid;
                    double resolution = (gridError[g] * config.GridScale0) + (dual ? gridError[256 + g] * config.GridScale1 : 0);
                    double estimate = config.Residual + resolution + (spread * choice.WeightError * weightShare) + (colorWeight * choice.ColorError);
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

            return count;
        }

        /// <summary>
        /// Returns the grid weights fitted to configuration <paramref name="configIndex"/>'s ideal texel weights for grid
        /// <paramref name="gridIndex"/>, interleaved by plane and clamped to 0..1; every weight range of the pair shares them.
        /// </summary>
        private ReadOnlySpan<double> Fitted(int configIndex, int gridIndex, int planes)
        {
            int key = (configIndex << 8) | gridIndex;
            int count = _fp.Grids[gridIndex].Count;
            for (int i = 0; i < _fitCount; i++)
            {
                if (_fitKeys[i] == key)
                {
                    return _fitValues.AsSpan(i * 128, count * planes);
                }
            }

            int slot = _fitCount < _fitKeys.Length ? _fitCount++ : _fitKeys.Length - 1;
            _fitKeys[slot] = key;
            Span<double> output = _fitValues.AsSpan(slot * 128, count * planes);
            Config config = _configs[configIndex];
            Grid grid = _fp.Grids[gridIndex];
            for (int plane = 0; plane < planes; plane++)
            {
                if (_settings.Exact)
                {
                    FitExact(grid, config.Ideal.AsSpan(plane * _texels, _texels), _gridIdeal);
                }
                else
                {
                    Fit(grid, config.Ideal.AsSpan(plane * _texels, _texels), _gridIdeal);
                }

                for (int g = 0; g < count; g++)
                {
                    output[(g * planes) + plane] = Math.Clamp(_gridIdeal[g], 0, 1);
                }
            }

            return output;
        }

        /// <summary>
        /// Encodes one configuration with one grid and weight range, refines it, and keeps it if it is the best yet; a quick
        /// trial only quantises the first fit and returns its error.
        /// </summary>
        private long Trial(int configIndex, int gridIndex, int range, bool quick)
        {
            Config config = _configs[configIndex];
            Grid grid = _fp.Grids[gridIndex];
            bool dual = config.Plane2 >= 0;
            int planes = dual ? 2 : 1;
            int mode = grid.Modes[range + (dual ? 12 : 0)];
            int configBits = config.ConfigBits;
            int weightBits = AstcIse.BitCount(grid.Count * planes, range);
            int colorBits = 128 - weightBits - configBits - (dual ? 2 : 0);
            int colorCount = config.Values;
            int colorRange = Astc.ColorRange(colorCount, colorBits);
            WeightTables tables = Weights[range];

            config.Endpoints.CopyTo(_endpoints, 0);
            Span<int> weights = _weights.AsSpan(0, grid.Count * planes);
            Span<int> colors = _colors.AsSpan(0, colorCount);
            ReadOnlySpan<double> fitted = Fitted(configIndex, gridIndex, planes);
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = tables.Nearest[(int)((fitted[i] * 64) + 0.5)];
            }

            Infill(grid, weights, planes, tables);
            if (quick)
            {
                QuantizeColors(config, colorRange, colors, tryDelta: false);
                return BlockError(config);
            }

            // Alternate: endpoints by least squares for the weights, quantised; then the weights and the colour values
            // nudged level by level against the decoder's arithmetic.
            long previous = long.MaxValue;
            for (int iteration = 0; iteration <= _settings.Refits; iteration++)
            {
                if (iteration > 0 || _settings.Refits > 0)
                {
                    RefitEndpoints(config);
                }

                QuantizeColors(config, colorRange, colors);
                if (_settings.Realign > 0)
                {
                    Realign(config, grid, weights, planes, tables);
                    if (RealignColors(config, colorRange, colors))
                    {
                        Realign(config, grid, weights, planes, tables);
                    }
                }

                long error = BlockError(config);
                if (error < _bestError)
                {
                    _bestError = error;
                    Pack(mode, config, range, colorRange, weights, colors);
                    _trial.CopyTo(_best, 0);
                }

                if (error >= previous)
                {
                    break;
                }

                previous = error;
            }

            return 0;
        }

        /// <summary>
        /// Moves each quantised colour value one level up or down (for direct and base+offset values, only the way the error's
        /// slope points) when that lowers the block's error with the current texel weights; returns whether any value moved.
        /// </summary>
        private bool RealignColors(Config config, int range, Span<int> colors)
        {
            byte[] unq = AstcIse.ColorUnquantized[range];
            (byte[] sorted, byte[] rank) = ColorOrder[range];
            long best = BlockError(config);
            bool any = false;
            Span<int> values = stackalloc int[8];
            Span<int> kept = stackalloc int[8];
            Span<int> slopes = stackalloc int[4 * 8];
            Slopes(config, slopes);
            for (int p = 0; p < config.Partitions; p++)
            {
                int cem = _cems[p];
                bool directed = cem is 8 or 9 or 12 or 13;
                Span<int> v = colors[config.Offsets[p]..config.Offsets[p + 1]];
                int ints = v.Length;
                Span<int> ends = _ends.AsSpan(p * 8, 8);
                ends.CopyTo(kept);
                for (int k = 0; k < ints; k++)
                {
                    int keptValue = v[k];
                    int r = rank[keptValue];
                    int first = -1, last = 1;
                    if (directed)
                    {
                        // The error's slope along the endpoint channel this value drives picks the one direction to try.
                        int c = k >> 1;
                        long slope = (k & 1) == 1 || cem is 8 or 12 ? slopes[(p * 8) + ((k & 1) * 4) + c] : (long)slopes[(p * 8) + c] + slopes[(p * 8) + 4 + c];
                        first = last = slope > 0 ? -1 : slope < 0 ? 1 : 0;
                        if (first == 0)
                        {
                            continue;
                        }
                    }

                    for (int step = first; step <= last; step += 2)
                    {
                        int nr = r + step;
                        if ((uint)nr >= (uint)sorted.Length)
                        {
                            continue;
                        }

                        v[k] = sorted[nr];
                        DecodeEnds(p, v, unq, values);
                        long error = BlockError(config);
                        if (error < best)
                        {
                            best = error;
                            keptValue = v[k];
                            ends.CopyTo(kept);
                            any = true;
                        }
                    }

                    v[k] = keptValue;
                    kept.CopyTo(ends);
                }
            }

            return any;
        }

        /// <summary>Gets, per partition, the slope of the block's error along each channel of endpoint 0 and of endpoint 1.</summary>
        private void Slopes(Config config, Span<int> slopes)
        {
            byte[] part = config.Table ?? Zeros;
            int plane2 = config.Plane2;
            int shift = _profile == AstcProfile.Srgb ? 6 : 14;
            ref int ends = ref MemoryMarshal.GetArrayDataReference(_ends);
            ref int px = ref MemoryMarshal.GetArrayDataReference(_px);
            int[] sums = _sums;
            int texels = _texels;
            Vector128<int> round = Vector128.Create(32);
            Vector128<int> full = Vector128.Create(64);
            Vector128<int> mask = plane2 >= 0 ? PlaneMasks[plane2] : Vector128<int>.Zero;
            Span<Vector128<int>> low = stackalloc Vector128<int>[4];
            Span<Vector128<int>> high = stackalloc Vector128<int>[4];
            low.Clear();
            high.Clear();
            for (int t = 0; t < texels; t++)
            {
                int pt = part[t];
                int p = pt * 8;
                Vector128<int> w = Vector128.ConditionalSelect(mask, Vector128.Create(plane2 >= 0 ? sums[texels + t] >> 4 : 0), Vector128.Create(sums[t] >> 4));
                Vector128<int> e0 = Vector128.LoadUnsafe(ref ends, (nuint)p);
                Vector128<int> e1 = Vector128.LoadUnsafe(ref ends, (nuint)(p + 4));
                Vector128<int> d = (((e0 * (full - w)) + (e1 * w) + round) >> shift) - Vector128.LoadUnsafe(ref px, (nuint)(t * 4));
                low[pt] += d * (full - w);
                high[pt] += d * w;
            }

            for (int p = 0; p < config.Partitions; p++)
            {
                low[p].CopyTo(slopes[(p * 8)..]);
                high[p].CopyTo(slopes[((p * 8) + 4)..]);
            }
        }

        /// <summary>Decodes partition <paramref name="p"/>'s colour values into its endpoints as the decoder expands them.</summary>
        private void DecodeEnds(int p, ReadOnlySpan<int> v, byte[] unq, Span<int> values)
        {
            for (int i = 0; i < v.Length; i++)
            {
                values[i] = unq[v[i]];
            }

            Astc.Endpoints(_cems[p], values[..v.Length], _profile, _ends.AsSpan(p * 8, 8), out _, out _);
        }

        /// <summary>Returns the error of the whole block for the current endpoints and texel weights.</summary>
        private long BlockError(Config config)
        {
            byte[] part = config.Table ?? Zeros;
            int plane2 = config.Plane2;
            int shift = _profile == AstcProfile.Srgb ? 6 : 14;
            ref int ends = ref MemoryMarshal.GetArrayDataReference(_ends);
            ref int px = ref MemoryMarshal.GetArrayDataReference(_px);
            int[] sums = _sums;
            int texels = _texels;
            Vector128<int> sum = Vector128<int>.Zero;
            Vector128<int> round = Vector128.Create(32);
            Vector128<int> full = Vector128.Create(64);
            if (plane2 < 0)
            {
                for (int t = 0; t < texels; t++)
                {
                    int p = part[t] * 8;
                    Vector128<int> w = Vector128.Create(sums[t] >> 4);
                    Vector128<int> e0 = Vector128.LoadUnsafe(ref ends, (nuint)p);
                    Vector128<int> e1 = Vector128.LoadUnsafe(ref ends, (nuint)(p + 4));
                    Vector128<int> d = (((e0 * (full - w)) + (e1 * w) + round) >> shift) - Vector128.LoadUnsafe(ref px, (nuint)(t * 4));
                    sum += d * d;
                }
            }
            else
            {
                Vector128<int> mask = PlaneMasks[plane2];
                for (int t = 0; t < texels; t++)
                {
                    int p = part[t] * 8;
                    Vector128<int> w = Vector128.ConditionalSelect(mask, Vector128.Create(sums[texels + t] >> 4), Vector128.Create(sums[t] >> 4));
                    Vector128<int> e0 = Vector128.LoadUnsafe(ref ends, (nuint)p);
                    Vector128<int> e1 = Vector128.LoadUnsafe(ref ends, (nuint)(p + 4));
                    Vector128<int> d = (((e0 * (full - w)) + (e1 * w) + round) >> shift) - Vector128.LoadUnsafe(ref px, (nuint)(t * 4));
                    sum += d * d;
                }
            }

            return Vector128.Sum(sum);
        }

        /// <summary>Infills the quantised grid weights into each texel's weight, keeping the raw sums for weight adjustments.</summary>
        private void Infill(Grid grid, ReadOnlySpan<int> weights, int planes, WeightTables tables)
        {
            Span<int> values = stackalloc int[grid.Count];
            byte[] unq = tables.Unquantized;
            int[] index = grid.TapIndex;
            int[] tap = grid.TapWeight;
            for (int plane = 0; plane < planes; plane++)
            {
                for (int g = 0; g < values.Length; g++)
                {
                    values[g] = unq[weights[(g * planes) + plane]];
                }

                Span<int> sums = _sums.AsSpan(plane * _texels, _texels);
                if (grid.Full)
                {
                    for (int t = 0; t < sums.Length; t++)
                    {
                        sums[t] = (values[t] << 4) + 8;
                    }

                    continue;
                }

                for (int t = 0; t < sums.Length; t++)
                {
                    int i = t * 4;
                    sums[t] = 8 + (values[index[i]] * tap[i]) + (values[index[i + 1]] * tap[i + 1]) + (values[index[i + 2]] * tap[i + 2]) + (values[index[i + 3]] * tap[i + 3]);
                }
            }
        }

        /// <summary>Refits every partition's endpoints by least squares for the texels' current weights.</summary>
        private void RefitEndpoints(Config config)
        {
            byte[] part = config.Table ?? Zeros;
            int plane2 = config.Plane2;
            int partitions = config.Partitions;
            Span<double> moments = stackalloc double[4 * 3 * 2];
            Span<double> x = stackalloc double[4 * 8];
            moments.Clear();
            x.Clear();
            int[] sums = _sums;
            for (int t = 0; t < _texels; t++)
            {
                int p = part[t];
                double w = (sums[t] >> 4) / 64.0;
                double other = plane2 >= 0 ? (sums[_texels + t] >> 4) / 64.0 : w;
                Span<double> m = moments.Slice(p * 6, 6);
                m[0] += (1 - w) * (1 - w);
                m[1] += (1 - w) * w;
                m[2] += w * w;
                m[3] += (1 - other) * (1 - other);
                m[4] += (1 - other) * other;
                m[5] += other * other;
                for (int c = 0; c < 4; c++)
                {
                    double cw = c == plane2 ? other : w;
                    double v = _px[(t * 4) + c];
                    x[(p * 8) + c] += (1 - cw) * v;
                    x[(p * 8) + 4 + c] += cw * v;
                }
            }

            for (int p = 0; p < partitions; p++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int k = (p * 6) + (c == plane2 ? 3 : 0);
                    double a = moments[k], b = moments[k + 1], d = moments[k + 2];
                    double x0 = x[(p * 8) + c], x1 = x[(p * 8) + 4 + c];
                    double det = (a * d) - (b * b);
                    if (Math.Abs(det) < 1e-9)
                    {
                        continue;
                    }

                    _endpoints[(p * 8) + c] = Math.Clamp(((d * x0) - (b * x1)) / det, 0, 255);
                    _endpoints[(p * 8) + 4 + c] = Math.Clamp(((a * x1) - (b * x0)) / det, 0, 255);
                }
            }
        }

        /// <summary>
        /// Quantises every partition's endpoints to <paramref name="range"/> for its colour endpoint mode (the base+scale mode,
        /// or the direct or base+offset mode, whichever lands nearer), and keeps the endpoints a decoder expands them to
        /// (scaled as the profile interpolates them) for weight adjustments. A configuration whose partitions share one mode
        /// gives them all the same one; a mixed configuration picks per partition.
        /// </summary>
        private void QuantizeColors(Config config, int range, Span<int> colors, bool tryDelta = true)
        {
            int partitions = config.Partitions;
            double direct = 0, delta = 0;
            Span<int> deltaColors = stackalloc int[8];
            Span<int> deltaEnds = stackalloc int[8];
            Span<int> saved = stackalloc int[18 + 32];
            bool anyDirect = false;
            for (int p = 0; p < partitions; p++)
            {
                Span<int> v = colors[config.Offsets[p]..config.Offsets[p + 1]];
                if ((config.ScaleMask & (1 << p)) != 0)
                {
                    QuantizeScale(p, range, v);
                    _cems[p] = _cem == 12 ? 10 : 6;
                    continue;
                }

                anyDirect = true;
                QuantizeDirect(p, range, v);
                _cems[p] = _cem;
                if (_cem == 0 || !tryDelta)
                {
                    continue;
                }

                double directError = EndpointError(p);
                double deltaErrorP = QuantizeDelta(p, range, deltaColors, deltaEnds);
                if (config.Mixed)
                {
                    // Each partition of a mixed block names its own mode.
                    if (deltaErrorP < directError)
                    {
                        deltaColors[..v.Length].CopyTo(v);
                        deltaEnds.CopyTo(_ends.AsSpan(p * 8, 8));
                        _cems[p] = _cem + 1;
                    }

                    continue;
                }

                direct += directError;
                delta += deltaErrorP;
                deltaColors[..v.Length].CopyTo(saved[config.Offsets[p]..]);
                deltaEnds.CopyTo(saved[(18 + (p * 8))..]);
            }

            // One mode for every partition: base+offset only if it beats the direct mode over the whole block.
            if (anyDirect && !config.Mixed && _cem != 0 && tryDelta && delta < direct)
            {
                for (int p = 0; p < partitions; p++)
                {
                    saved[config.Offsets[p]..config.Offsets[p + 1]].CopyTo(colors[config.Offsets[p]..]);
                    saved.Slice(18 + (p * 8), 8).CopyTo(_ends.AsSpan(p * 8, 8));
                    _cems[p] = _cem + 1;
                }
            }

            if (_profile == AstcProfile.Ldr)
            {
                for (int c = 0; c < partitions * 8; c++)
                {
                    _ends[c] *= 257;
                }
            }
        }

        /// <summary>Returns the squared distance of partition <paramref name="p"/>'s quantised endpoints (8-bit) from the fitted ones.</summary>
        private double EndpointError(int p)
        {
            double error = 0;
            for (int i = p * 8; i < (p * 8) + 8; i++)
            {
                double d = _ends[i] - _endpoints[i];
                error += d * d;
            }

            return error;
        }

        /// <summary>
        /// Quantises partition <paramref name="p"/>'s endpoints for the base+offset modes (5, 9 or 13); returns their squared
        /// error, or infinity when the offsets would make the decoder swap the endpoints.
        /// </summary>
        private double QuantizeDelta(int p, int range, Span<int> v, Span<int> end)
        {
            ReadOnlySpan<double> e = _endpoints.AsSpan(p * 8, 8);
            if (_cem == 4)
            {
                double error = 3 * DeltaPair(range, (e[0] + e[1] + e[2]) / 3, (e[4] + e[5] + e[6]) / 3, out v[0], out v[1], out int l0, out int l1);
                error += DeltaPair(range, e[3], e[7], out v[2], out v[3], out end[3], out end[7]);
                end[0] = end[1] = end[2] = l0;
                end[4] = end[5] = end[6] = l1;
                return error;
            }

            int channels = _channels / 2;
            double sum = 0;
            int offsets = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += DeltaPair(range, e[c], e[4 + c], out v[c * 2], out v[(c * 2) + 1], out end[c], out end[4 + c]);
                if (c < 3)
                {
                    offsets += Offset(AstcIse.ColorUnquantized[range][v[(c * 2) + 1]]);
                }
            }

            if (channels == 3)
            {
                end[3] = end[7] = 255;
            }

            return offsets < 0 ? double.PositiveInfinity : sum;
        }

        private static int Offset(int value)
        {
            int d = (value >> 1) & 0x3F;
            return d >= 32 ? d - 64 : d;
        }

        /// <summary>
        /// Quantises one channel's endpoints <paramref name="lo"/> and <paramref name="hi"/> as a base and a signed offset (the
        /// offset value lends its top bit to the base); returns the squared error of the decoded pair.
        /// </summary>
        private static double DeltaPair(int range, double lo, double hi, out int v0, out int v1, out int d0, out int d1)
        {
            byte[] q = ColorQuantize[range];
            byte[] unq = AstcIse.ColorUnquantized[range];
            (byte[] sorted, byte[] rank) = ColorOrder[range];
            int last = unq.Length - 1;
            double best = double.MaxValue;
            v0 = v1 = d0 = d1 = 0;
            int offset = Math.Clamp((int)Math.Round(hi - lo), -32, 31);
            for (int top = 0; top <= 128; top += 128)
            {
                int r1 = rank[q[top | ((offset & 0x3F) << 1)]];
                for (int k1 = Math.Max(r1 - 1, 0); k1 <= Math.Min(r1 + 1, last); k1++)
                {
                    int i1 = sorted[k1];
                    int u1 = unq[i1];
                    if ((u1 & 0x80) != top)
                    {
                        continue;
                    }

                    int d = Offset(u1);
                    int low = Math.Clamp((int)Math.Round(((lo + hi - d) / 2) - top), 0, 127);
                    int r0 = rank[q[low * 2]];
                    for (int k0 = Math.Max(r0 - 1, 0); k0 <= Math.Min(r0 + 1, last); k0++)
                    {
                        int i0 = sorted[k0];
                        int b = (unq[i0] >> 1) | top;
                        int b1 = Math.Clamp(b + d, 0, 255);
                        double error = ((b - lo) * (b - lo)) + ((b1 - hi) * (b1 - hi));
                        if (error < best)
                        {
                            best = error;
                            (v0, v1, d0, d1) = (i0, i1, b, b1);
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>Quantises partition <paramref name="p"/>'s endpoints for the base+scale modes (6, or 10 with alpha): endpoint 1 as the base, endpoint 0 as its scale.</summary>
        private void QuantizeScale(int p, int range, Span<int> v)
        {
            byte[] q = ColorQuantize[range];
            byte[] unq = AstcIse.ColorUnquantized[range];
            (byte[] sorted, byte[] rank) = ColorOrder[range];
            ReadOnlySpan<double> e = _endpoints.AsSpan(p * 8, 8);
            Span<int> ends = _ends.AsSpan(p * 8, 8);
            double dot = 0, base2 = 0;
            for (int c = 0; c < 3; c++)
            {
                v[c] = q[Round(e[4 + c])];
                int b = unq[v[c]];
                ends[4 + c] = b;
                dot += e[c] * b;
                base2 += b * b;
            }

            int target = base2 > 0 ? Round(256 * dot / base2) : 0;
            int r = rank[q[target]];
            double best = double.MaxValue;
            for (int k = Math.Max(r - 1, 0); k <= Math.Min(r + 1, unq.Length - 1); k++)
            {
                int s = unq[sorted[k]];
                double error = 0;
                for (int c = 0; c < 3; c++)
                {
                    double d = ((ends[4 + c] * s) >> 8) - e[c];
                    error += d * d;
                }

                if (error < best)
                {
                    best = error;
                    v[3] = sorted[k];
                }
            }

            int scale = unq[v[3]];
            for (int c = 0; c < 3; c++)
            {
                ends[c] = (ends[4 + c] * scale) >> 8;
            }

            if (_cem == 12)
            {
                v[4] = q[Round(e[3])];
                v[5] = q[Round(e[7])];
                ends[3] = unq[v[4]];
                ends[7] = unq[v[5]];
            }
            else
            {
                ends[3] = ends[7] = 255;
            }
        }

        /// <summary>Quantises partition <paramref name="p"/>'s endpoints for the direct modes (0, 4, 8 or 12) into <paramref name="v"/> and the 8-bit endpoints.</summary>
        private void QuantizeDirect(int p, int range, Span<int> v)
        {
            byte[] q = ColorQuantize[range];
            byte[] unq = AstcIse.ColorUnquantized[range];
            (byte[] sorted, byte[] rank) = ColorOrder[range];
            ReadOnlySpan<double> e = _endpoints.AsSpan(p * 8, 8);
            Span<int> ends = _ends.AsSpan(p * 8, 8);
            if (_cem is 0 or 4)
            {
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

                return;
            }

            for (int c = 0; c < _channels / 2; c++)
            {
                v[c * 2] = q[Round(e[c])];
                v[(c * 2) + 1] = q[Round(e[4 + c])];
            }

            // The direct modes swap the endpoints and contract blue when endpoint 1 is the darker: step it up.
            int top = sorted.Length - 1;
            while (unq[v[1]] + unq[v[3]] + unq[v[5]] < unq[v[0]] + unq[v[2]] + unq[v[4]])
            {
                int channel = -1;
                int gap = int.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    int g = unq[v[k * 2]] - unq[v[(k * 2) + 1]];
                    if (g > gap && rank[v[(k * 2) + 1]] < top)
                    {
                        gap = g;
                        channel = k;
                    }
                }

                if (channel < 0)
                {
                    break;
                }

                v[(channel * 2) + 1] = sorted[rank[v[(channel * 2) + 1]] + 1];
            }

            for (int c = 0; c < 4; c++)
            {
                bool present = c < _channels / 2;
                ends[c] = present ? unq[v[c * 2]] : 255;
                ends[4 + c] = present ? unq[v[(c * 2) + 1]] : 255;
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
                        long bestError = FeedError(config, feeds, plane, 0, out long slope);
                        int bestRank = rank;
                        int r = slope > 0 ? rank - 1 : slope < 0 ? rank + 1 : -1;
                        if ((uint)r < (uint)levels)
                        {
                            long error = FeedError(config, feeds, plane, tables.Unquantized[tables.Sorted[r]] - current, out _);
                            if (error < bestError)
                            {
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
        private long FeedError(Config config, (short Texel, byte Weight)[] feeds, int plane, int delta, out long slope)
        {
            byte[] part = config.Table ?? Zeros;
            int plane2 = config.Plane2;
            int shift = _profile == AstcProfile.Srgb ? 6 : 14;
            int scale = _profile == AstcProfile.Srgb ? 0 : 8;
            ref int ends = ref MemoryMarshal.GetArrayDataReference(_ends);
            ref int px = ref MemoryMarshal.GetArrayDataReference(_px);
            int[] sums = _sums;
            int texels = _texels;
            Vector128<int> sum = Vector128<int>.Zero;
            Vector128<int> gradient = Vector128<int>.Zero;
            Vector128<int> round = Vector128.Create(32);
            Vector128<int> full = Vector128.Create(64);
            Vector128<int> mask = plane2 >= 0 ? PlaneMasks[plane2] : Vector128<int>.Zero;

            // The lanes the moved weight drives: the second plane's channel, or every other channel.
            Vector128<int> moves = plane == 1 ? mask : ~mask;
            int at = plane * texels;
            foreach ((short t, byte f) in feeds)
            {
                // Channels on the other plane keep their weight; they still count, so errors compare like for like.
                int w0 = sums[t] >> 4;
                int w1 = plane2 >= 0 ? sums[texels + t] >> 4 : 0;
                int moved = (sums[at + t] + (delta * f)) >> 4;
                if (plane == 0)
                {
                    w0 = moved;
                }
                else
                {
                    w1 = moved;
                }

                Vector128<int> w = Vector128.ConditionalSelect(mask, Vector128.Create(w1), Vector128.Create(w0));
                int p = part[t] * 8;
                Vector128<int> e0 = Vector128.LoadUnsafe(ref ends, (nuint)p);
                Vector128<int> e1 = Vector128.LoadUnsafe(ref ends, (nuint)(p + 4));
                Vector128<int> d = (((e0 * (full - w)) + (e1 * w) + round) >> shift) - Vector128.LoadUnsafe(ref px, (nuint)(t * 4));
                sum += d * d;
                gradient += (d * ((e1 - e0) >> scale) * Vector128.Create((int)f)) & moves;
            }

            slope = Vector128.Sum(gradient);
            return Vector128.Sum(sum);
        }

        private void Pack(int mode, Config config, int weightRange, int colorRange, ReadOnlySpan<int> weights, ReadOnlySpan<int> colors)
        {
            Span<byte> block = _trial;
            block.Clear();
            int offset = 0;
            AstcIse.Write(block, ref offset, mode, 11);
            AstcIse.Write(block, ref offset, config.Partitions - 1, 2);
            int below = 128 - AstcIse.BitCount(weights.Length, weightRange);
            if (config.Partitions == 1)
            {
                AstcIse.Write(block, ref offset, _cems[0], 4);
            }
            else if (!config.Mixed)
            {
                AstcIse.Write(block, ref offset, config.Seed, 10);
                AstcIse.Write(block, ref offset, _cems[0] << 2, 6);
            }
            else
            {
                // Mixed modes: the lower class plus one, a class bit per partition, then a 2-bit mode per partition; the
                // bits past the first six go just below the weights.
                int partitions = config.Partitions;
                int low = _cems[0] >> 2;
                for (int p = 1; p < partitions; p++)
                {
                    low = Math.Min(low, _cems[p] >> 2);
                }

                int encoded = low + 1;
                for (int p = 0; p < partitions; p++)
                {
                    encoded |= ((_cems[p] >> 2) - low) << (2 + p);
                    encoded |= (_cems[p] & 3) << (2 + partitions + (2 * p));
                }

                int extra = (3 * partitions) - 4;
                AstcIse.Write(block, ref offset, config.Seed, 10);
                AstcIse.Write(block, ref offset, encoded & 63, 6);
                below -= extra;
                int at = below;
                AstcIse.Write(block, ref at, encoded >> 6, extra);
            }

            AstcIse.Encode(colorRange, colors, block, offset);
            if (config.Plane2 >= 0)
            {
                int ccs = below - 2;
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
    }
}
