using System.Buffers.Binary;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Encodes UASTC LDR 4 × 4 blocks at effort 0 to 6, written from Binomial's UASTC LDR 4x4 Texture Specification (public
/// domain). A constant block becomes a solid-colour block (mode 8). Otherwise the block's content picks the family of modes:
/// the luminance-alpha modes (15 to 17, and 18 when opaque) when red, green and blue are equal in every texel, the opaque RGB
/// modes (0 to 7 and 18) when alpha is 255 everywhere, and the RGBA modes (9 to 14) otherwise; from effort 4 a gray block also
/// tries the RGB or RGBA family. The effort decides how many of the family's modes, partition patterns (ranked by how far each
/// subset's texels lie from a line) and dual-plane component selectors (least correlated first) are tried. Each candidate fits
/// every subset's endpoints along the principal axis of its texels, quantises them, picks each texel's weight with the
/// decoder's own interpolation, then refits the endpoints by least squares (and, at efforts 5 and 6, nudges each quantised
/// endpoint a level either way). A subset whose anchor texel's weight has its top bit set swaps its endpoints and inverts its
/// weights, which decodes identically. The transcoder hints are filled in for the winner: the ETC1 flip, mode and intensity
/// tables that suit the decoded texels best (with the neutral bias, 13), the ETC2 EAC alpha table and multiplier, and for a
/// solid block its ETC1 colour, table and selector; the BC1 hints are left clear, so a transcoder fits BC1 from scratch. The
/// work for one block depends only on that block. <see cref="Rdo"/> then trades a little quality for a block stream Zstandard
/// compresses better.
/// </summary>
internal static class UastcEncoder
{
    private const int SolidMode = 8;
    private const int BandRows = 16;
    private const int MinMatch = 4;

    /// <summary>The modes of each family, in the order they are tried; an effort tries a prefix.</summary>
    private static ReadOnlySpan<byte> OpaqueModes => [0, 18, 4, 2, 6, 1, 5, 7, 3];

    private static ReadOnlySpan<byte> AlphaModes => [10, 12, 9, 11, 14, 13];

    private static ReadOnlySpan<byte> GrayModes => [15, 16, 17];

    private static ReadOnlySpan<byte> GrayOpaqueModes => [15, 18, 16, 17];

    private static readonly Pattern[] Patterns2 = BuildPatterns(Uastc.Seeds2, 2);
    private static readonly Pattern[] Patterns3 = BuildPatterns(Uastc.Seeds3, 3);
    private static readonly Pattern[] Patterns7 = BuildPatterns(Uastc.Seeds7, 2);

    /// <summary>Per colour range: the values in increasing unquantised order, and each value's place in that order.</summary>
    private static readonly byte[][] Sorted = BuildSorted();
    private static readonly byte[][] Rank = BuildRank();

    /// <summary>Per weight count (by its base-2 logarithm): the nearest weight index of each weight value 0 to 64.</summary>
    private static readonly byte[][] WeightNearest = BuildWeightNearest();

    [ThreadStatic]
    private static Search? t_search;

    /// <summary>
    /// Encodes 16 RGBA texels (4 bytes each, row order) into a 16-byte block at effort 0 to 6 and returns the squared RGBA error
    /// of the block as <see cref="Uastc.Unpack"/> decodes it.
    /// </summary>
    public static long Encode(ReadOnlySpan<byte> rgba, Span<byte> block, int effort)
    {
        bool constant = true;
        for (int i = 4; i < 64 && constant; i++)
        {
            constant = rgba[i] == rgba[i & 3];
        }

        if (constant)
        {
            WriteSolid(rgba, block);
            return 0;
        }

        Search search = t_search ??= new Search();
        return search.Run(rgba, block, Settings.For(effort));
    }

    /// <summary>
    /// Rate-distortion optimisation of a whole image's blocks (in raster order, encoded from <paramref name="rgba"/>, the image's
    /// pixels): each block may be replaced by one that repeats bytes of a recent block, so Zstandard finds more and longer
    /// matches. The candidates for a block come from a window of earlier blocks (the few before it and the three above it): the
    /// earlier block itself, its mode, hints and endpoints with this block's best weights for them, and its mode and weights with
    /// endpoints refitted to this block. Each candidate's cost is its squared error (summed over the block's texels inside the
    /// image and their four channels) plus <paramref name="lambda"/> times an estimate of its compressed bits (eight a literal
    /// byte, a match of four or more bytes by its distance, less for the distance the previous block matched at); the cheapest
    /// wins. A copied block keeps the earlier block's hints when its own would cost bits. Useful lambdas run from about 1 (a few
    /// per cent smaller) to 16 (about a third of the size, 3 to 4 dB lower). A <paramref name="lambda"/> of 0 leaves the blocks
    /// alone. <paramref name="effort"/> (0 to 6) sets the window and candidates. The image is
    /// worked in bands of 16 block rows that see only their own blocks, so the result does not depend on
    /// <paramref name="maxParallelism"/>.
    /// </summary>
    public static void Rdo(ReadOnlyMemory<byte> rgba, int width, int height, Memory<byte> blocks, float lambda, int effort, int maxParallelism = 1)
    {
        if (!(lambda > 0))
        {
            return;
        }

        int columns = (width + 3) / 4;
        int rows = (height + 3) / 4;
        int bands = (rows + BandRows - 1) / BandRows;
        RdoSettings settings = RdoSettings.For(effort);
        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, maxParallelism) };
        Parallel.For(0, bands, options, band =>
        {
            var state = new RdoState(columns, Math.Min(BandRows, rows - (band * BandRows)));
            state.Run(rgba.Span, width, height, blocks.Span, band * BandRows, lambda, settings);
        });
    }

    private static byte[][] BuildSorted()
    {
        byte[][] tables = new byte[21][];
        for (int r = 0; r < 21; r++)
        {
            byte[] unq = AstcIse.ColorUnquantized[r];
            byte[] order = new byte[unq.Length];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = (byte)i;
            }

            Array.Sort(order, (a, b) => unq[a] != unq[b] ? unq[a].CompareTo(unq[b]) : a.CompareTo(b));
            tables[r] = order;
        }

        return tables;
    }

    private static byte[][] BuildRank()
    {
        byte[][] tables = new byte[21][];
        for (int r = 0; r < 21; r++)
        {
            tables[r] = new byte[Sorted[r].Length];
            for (int i = 0; i < Sorted[r].Length; i++)
            {
                tables[r][Sorted[r][i]] = (byte)i;
            }
        }

        return tables;
    }

    private static byte[][] BuildWeightNearest()
    {
        byte[][] tables = new byte[6][];
        foreach (int range in (ReadOnlySpan<int>)[0, 2, 5, 8, 11])
        {
            byte[] unq = AstcIse.WeightUnquantized[range];
            byte[] table = new byte[65];
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

                table[v] = (byte)best;
            }

            tables[System.Numerics.BitOperations.Log2((uint)unq.Length)] = table;
        }

        return tables;
    }

    private static Pattern[] PatternsOf(int mode) => mode switch
    {
        3 => Patterns3,
        7 => Patterns7,
        _ => Patterns2,
    };

    private static Pattern[] BuildPatterns(ReadOnlySpan<short> seeds, int subsets)
    {
        var patterns = new Pattern[seeds.Length];
        for (int i = 0; i < seeds.Length; i++)
        {
            byte[] table = Astc.For(4, 4)!.PartitionTable(subsets, seeds[i]);
            byte[] anchors = [255, 255, 255];
            for (int s = 0; s < subsets; s++)
            {
                int at = Array.IndexOf(table, (byte)s);
                anchors[s] = at < 0 ? (byte)255 : (byte)at;
            }

            patterns[i] = new Pattern(table, anchors);
        }

        return patterns;
    }

    /// <summary>A partition pattern: each texel's subset and each subset's anchor texel (255 for an empty subset).</summary>
    private sealed record Pattern(byte[] Table, byte[] Anchors);

    /// <summary>The component a dual-plane mode's second plane holds (in the mode's component order), or -1.</summary>
    private static int Plane2Component(int mode, int ccs) => Uastc.Planes[mode] == 1 ? -1 : mode == 17 ? 1 : ccs;

    /// <summary>Writes a solid-colour block with the ETC1 hints that encode its colour best.</summary>
    private static void WriteSolid(ReadOnlySpan<byte> rgba, Span<byte> block)
    {
        UInt128 bits = Uastc.ModeCodes[SolidMode];
        int at = Uastc.ModeLengths[SolidMode];
        for (int c = 0; c < 4; c++, at += 8)
        {
            bits |= (UInt128)rgba[c] << at;
        }

        bits |= (UInt128)(uint)SolidEtcHints(rgba[0], rgba[1], rgba[2]) << at;
        Store(bits, block);
    }

    /// <summary>
    /// The solid block's ETC1 hints: the differential bit, the intensity table, the selector (0 to 3 for the modifiers from the
    /// most negative up) and the 5-bit (or 4-bit) colour, chosen for the least squared error.
    /// </summary>
    private static int SolidEtcHints(int r, int g, int b)
    {
        int best = int.MaxValue;
        int hints = 0;
        Span<int> target = [r, g, b];
        Span<int> colour = stackalloc int[3];
        for (int diff = 1; diff >= 0; diff--)
        {
            int top = diff == 1 ? 31 : 15;
            for (int inten = 0; inten < 8; inten++)
            {
                for (int sel = 0; sel < 4; sel++)
                {
                    int modifier = sel switch
                    {
                        0 => -Etc.Modifiers[(inten * 2) + 1],
                        1 => -Etc.Modifiers[inten * 2],
                        2 => Etc.Modifiers[inten * 2],
                        _ => Etc.Modifiers[(inten * 2) + 1],
                    };
                    int error = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        int bestChannel = int.MaxValue;
                        for (int v = 0; v <= top; v++)
                        {
                            int value = Math.Clamp((diff == 1 ? Etc.Extend5(v) : Etc.Extend4(v)) + modifier, 0, 255);
                            int d = (value - target[c]) * (value - target[c]);
                            if (d < bestChannel)
                            {
                                bestChannel = d;
                                colour[c] = v;
                            }
                        }

                        error += bestChannel;
                    }

                    if (error < best)
                    {
                        best = error;
                        hints = diff | (inten << 1) | (sel << 4) | (colour[0] << 6) | (colour[1] << 11) | (colour[2] << 16);
                    }
                }
            }
        }

        return hints;
    }

    private static void Store(UInt128 bits, Span<byte> block)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(block, (ulong)bits);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], (ulong)(bits >> 64));
    }

    private static UInt128 Load(ReadOnlySpan<byte> block) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(block[8..]), BinaryPrimitives.ReadUInt64LittleEndian(block));

    /// <summary>Returns the anchor texel of subset <paramref name="s"/> (255 for none).</summary>
    private static int Anchor(int mode, int pattern, int s) => Uastc.Subsets[mode] == 1 ? (s == 0 ? 0 : 255) : PatternsOf(mode)[pattern].Anchors[s];

    /// <summary>
    /// Packs <paramref name="b"/> (not the solid mode) into 16 bytes: the mode's code, the hints, the pattern or component
    /// selector, the endpoints' trits or quints as whole values then their plain bits, and the weights, anchors one bit short.
    /// </summary>
    private static void Pack(BlockFields b, Span<byte> block)
    {
        int mode = b.Mode;
        UInt128 bits = Uastc.ModeCodes[mode];
        int at = Uastc.ModeLengths[mode];
        bits |= (UInt128)(uint)b.Hints << at;
        at += Uastc.HintBits[mode];
        int subsets = Uastc.Subsets[mode];
        if (subsets > 1)
        {
            bits |= (UInt128)(uint)b.Pattern << at;
            at += mode == 3 ? 4 : 5;
        }

        int planes = Uastc.Planes[mode];
        if (planes == 2 && mode != 17)
        {
            bits |= (UInt128)(uint)b.Ccs << at;
            at += 2;
        }

        int count = Uastc.Components[mode] * 2 * subsets;
        int range = Uastc.EndpointRanges[mode];
        int plain = AstcIse.Bits[range];
        int kind = AstcIse.Kind[range];
        if (kind != 0)
        {
            int per = kind == 1 ? 5 : 3;
            int radix = kind == 1 ? 3 : 5;
            for (int first = 0; first < count; first += per)
            {
                int n = Math.Min(per, count - first);
                int width = kind == 1 ? n switch { 1 => 2, 2 => 4, 3 => 5, 4 => 7, _ => 8 } : n switch { 1 => 3, 2 => 5, _ => 7 };
                int packed = 0;
                for (int i = n - 1; i >= 0; i--)
                {
                    packed = (packed * radix) + (b.Endpoints[first + i] >> plain);
                }

                bits |= (UInt128)(uint)packed << at;
                at += width;
            }
        }

        int mask = (1 << plain) - 1;
        for (int i = 0; i < count; i++)
        {
            bits |= (UInt128)(uint)(b.Endpoints[i] & mask) << at;
            at += plain;
        }

        int weightBits = Uastc.WeightBits[mode];
        int a0 = Anchor(mode, b.Pattern, 0);
        int a1 = Anchor(mode, b.Pattern, 1);
        int a2 = Anchor(mode, b.Pattern, 2);
        for (int t = 0; t < 16; t++)
        {
            bool anchor = t == a0 || t == a1 || t == a2;
            for (int p = 0; p < planes; p++)
            {
                bits |= (UInt128)b.Weights[(t * planes) + p] << at;
                at += anchor ? weightBits - 1 : weightBits;
            }
        }

        Store(bits, block);
    }

    /// <summary>Reads the fields of a block that is not solid; returns <see langword="false"/> for the solid and reserved modes.</summary>
    private static bool Parse(ReadOnlySpan<byte> block, BlockFields b)
    {
        int mode = Uastc.Mode(block[0]);
        if (mode >= Uastc.Modes || mode == SolidMode)
        {
            return false;
        }

        UInt128 bits = Load(block);
        int at = Uastc.ModeLengths[mode];
        b.Mode = mode;
        b.Hints = (int)Read(bits, at, Uastc.HintBits[mode]);
        at += Uastc.HintBits[mode];
        int subsets = Uastc.Subsets[mode];
        b.Pattern = 0;
        if (subsets > 1)
        {
            int width = mode == 3 ? 4 : 5;
            b.Pattern = (int)Read(bits, at, width);
            at += width;
            if (b.Pattern >= PatternsOf(mode).Length)
            {
                return false;
            }
        }

        int planes = Uastc.Planes[mode];
        b.Ccs = 0;
        if (planes == 2 && mode != 17)
        {
            b.Ccs = (int)Read(bits, at, 2);
            at += 2;
            if (b.Ccs >= Uastc.Components[mode])
            {
                return false;
            }
        }

        int count = Uastc.Components[mode] * 2 * subsets;
        int range = Uastc.EndpointRanges[mode];
        int plain = AstcIse.Bits[range];
        int kind = AstcIse.Kind[range];
        Span<int> high = stackalloc int[18];
        if (kind != 0)
        {
            int per = kind == 1 ? 5 : 3;
            int radix = kind == 1 ? 3 : 5;
            for (int first = 0; first < count; first += per)
            {
                int n = Math.Min(per, count - first);
                int width = kind == 1 ? n switch { 1 => 2, 2 => 4, 3 => 5, 4 => 7, _ => 8 } : n switch { 1 => 3, 2 => 5, _ => 7 };
                int packed = (int)Read(bits, at, width);
                at += width;
                for (int i = 0; i < n; i++)
                {
                    high[first + i] = packed % radix;
                    packed /= radix;
                }
            }
        }

        int levels = AstcIse.ColorUnquantized[range].Length;
        for (int i = 0; i < count; i++)
        {
            b.Endpoints[i] = (high[i] << plain) | (int)Read(bits, at, plain);
            at += plain;
            if (b.Endpoints[i] >= levels)
            {
                return false;
            }
        }

        int weightBits = Uastc.WeightBits[mode];
        int a0 = Anchor(mode, b.Pattern, 0);
        int a1 = Anchor(mode, b.Pattern, 1);
        int a2 = Anchor(mode, b.Pattern, 2);
        for (int t = 0; t < 16; t++)
        {
            bool anchor = t == a0 || t == a1 || t == a2;
            for (int p = 0; p < planes; p++)
            {
                int n = anchor ? weightBits - 1 : weightBits;
                b.Weights[(t * planes) + p] = (byte)Read(bits, at, n);
                at += n;
            }
        }

        return true;
    }

    private static uint Read(UInt128 bits, int at, int count) => count == 0 ? 0 : (uint)((bits >> at) & ((UInt128.One << count) - 1));

    /// <summary>
    /// The hints for a block of <paramref name="mode"/> that decodes to <paramref name="decoded"/>, in field order: the BC1 hints
    /// (clear), the ETC1 flip, differential bit and two intensity tables, the neutral ETC1 bias and the ETC2 EAC alpha table and
    /// multiplier.
    /// </summary>
    private static int Hints(ReadOnlySpan<byte> decoded, int mode)
    {
        bool hasHint1 = mode is not (10 or 11 or 12);
        bool hasBias = hasHint1;
        bool alpha = Uastc.Components[mode] != 3;
        (int flip, int diff, int i0, int i1) = EtcHints(decoded, hasBias);
        int value = 0;
        int at = hasHint1 ? 2 : 1;
        value |= flip << at++;
        value |= diff << at++;
        value |= i0 << at;
        at += 3;
        value |= i1 << at;
        at += 3;
        if (hasBias)
        {
            value |= 13 << at;
            at += 5;
        }

        if (alpha)
        {
            value |= EacHints(decoded) << at;
        }

        return value;
    }

    /// <summary>
    /// Picks the ETC1 hints a transcoder will use with the block's texels: it averages each sub-block, quantises the averages to
    /// 5 bits (differential) or 4, applies the bias field (the neutral 13 here, which only moves a channel off 0 or its top)
    /// and picks each texel's modifier; the hints are the flip, mode and intensity tables with the least squared error.
    /// </summary>
    private static (int Flip, int Diff, int I0, int I1) EtcHints(ReadOnlySpan<byte> px, bool applyBias)
    {
        long best = long.MaxValue;
        (int, int, int, int) result = (0, 0, 0, 0);
        Span<int> colours = stackalloc int[6];
        Span<int> sums = stackalloc int[6];
        Span<int> intenError = stackalloc int[8];
        Span<int> modifiers = stackalloc int[4];
        Span<int> inten = stackalloc int[2];
        for (int flip = 0; flip < 2; flip++)
        {
            sums.Clear();
            for (int t = 0; t < 16; t++)
            {
                int sub = flip == 0 ? ((t & 3) >= 2 ? 1 : 0) : (t >= 8 ? 1 : 0);
                for (int c = 0; c < 3; c++)
                {
                    sums[(sub * 3) + c] += px[(t * 4) + c];
                }
            }

            for (int diff = 0; diff < 2; diff++)
            {
                int limit = diff == 1 ? 31 : 15;
                for (int i = 0; i < 6; i++)
                {
                    int v = ((sums[i] * limit) + 1020) / (8 * 255);
                    colours[i] = !applyBias ? v : v == 0 ? 1 : v == limit ? limit - 1 : v;
                }

                if (diff == 1)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        colours[3 + c] = colours[c] + Math.Clamp(colours[3 + c] - colours[c], -4, 3);
                    }
                }

                for (int i = 0; i < 6; i++)
                {
                    colours[i] = diff == 1 ? Etc.Extend5(colours[i]) : Etc.Extend4(colours[i]);
                }

                long total = 0;
                for (int sub = 0; sub < 2; sub++)
                {
                    intenError.Clear();
                    for (int t = 0; t < 16; t++)
                    {
                        if ((flip == 0 ? ((t & 3) >= 2 ? 1 : 0) : (t >= 8 ? 1 : 0)) != sub)
                        {
                            continue;
                        }

                        for (int table = 0; table < 8; table++)
                        {
                            modifiers[2] = Etc.Modifiers[table * 2];
                            modifiers[3] = Etc.Modifiers[(table * 2) + 1];
                            modifiers[1] = -modifiers[2];
                            modifiers[0] = -modifiers[3];
                            int e = int.MaxValue;
                            for (int m = 0; m < 4; m++)
                            {
                                int d = 0;
                                for (int c = 0; c < 3; c++)
                                {
                                    int v = Math.Clamp(colours[(sub * 3) + c] + modifiers[m], 0, 255) - px[(t * 4) + c];
                                    d += v * v;
                                }

                                e = Math.Min(e, d);
                            }

                            intenError[table] += e;
                        }
                    }

                    int pick = 0;
                    for (int table = 1; table < 8; table++)
                    {
                        if (intenError[table] < intenError[pick])
                        {
                            pick = table;
                        }
                    }

                    inten[sub] = pick;
                    total += intenError[pick];
                }

                if (total < best)
                {
                    best = total;
                    result = (flip, diff, inten[0], inten[1]);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Picks the ETC2 EAC alpha table and multiplier a transcoder will use: it centres the table on the block's alpha range and
    /// picks each texel's modifier; the hint is the table (low 4 bits) and multiplier (high 4) with the least squared error.
    /// </summary>
    private static int EacHints(ReadOnlySpan<byte> px)
    {
        int lo = 255;
        int hi = 0;
        for (int t = 0; t < 16; t++)
        {
            lo = Math.Min(lo, px[(t * 4) + 3]);
            hi = Math.Max(hi, px[(t * 4) + 3]);
        }

        if (lo == hi)
        {
            return 13 | (1 << 4);
        }

        long best = long.MaxValue;
        int hints = 0;
        for (int table = 0; table < 16; table++)
        {
            ReadOnlySpan<sbyte> modifiers = Etc.EacTables.Slice(table * 8, 8);
            int spread = modifiers[7] - modifiers[3];
            float t = (float)(0 - modifiers[3]) / spread;
            int centre = (int)MathF.Round(lo + ((hi - lo) * t), MidpointRounding.AwayFromZero);
            int guess = Math.Clamp((hi - lo) / spread, 1, 15);
            for (int multiplier = Math.Max(1, guess - 1); multiplier <= Math.Min(15, guess + 2); multiplier++)
            {
                long error = 0;
                for (int i = 0; i < 16; i++)
                {
                    int a = px[(i * 4) + 3];
                    int e = int.MaxValue;
                    for (int j = 0; j < 8; j++)
                    {
                        int d = Math.Clamp(centre + (modifiers[j] * multiplier), 0, 255) - a;
                        e = Math.Min(e, d * d);
                    }

                    error += e;
                }

                if (error < best)
                {
                    best = error;
                    hints = table | (multiplier << 4);
                }
            }
        }

        return hints;
    }

    /// <summary>A block's fields: mode, pattern, component selector, hint bits, endpoint values and weight indices.</summary>
    private sealed class BlockFields
    {
        public int Mode { get; set; }

        public int Pattern { get; set; }

        public int Ccs { get; set; }

        public int Hints { get; set; }

        public long Error { get; set; }

        /// <summary>Gets the endpoint values (in the range's encoding), ASTC's order: per subset, per component, low then high.</summary>
        public int[] Endpoints { get; } = new int[18];

        /// <summary>Gets the weight indices, per texel and, in dual-plane modes, per plane.</summary>
        public byte[] Weights { get; } = new byte[32];

        public void CopyFrom(BlockFields other)
        {
            Mode = other.Mode;
            Pattern = other.Pattern;
            Ccs = other.Ccs;
            Hints = other.Hints;
            Error = other.Error;
            other.Endpoints.CopyTo(Endpoints, 0);
            other.Weights.CopyTo(Weights, 0);
        }
    }

    /// <summary>How hard each effort searches.</summary>
    private readonly record struct Settings(int Opaque, int Alpha, int Gray, bool BothFamilies, int Patterns, int Finalists, int Selectors, int Refits, bool Nudge)
    {
        public static Settings For(int effort) => Math.Clamp(effort, 0, 6) switch
        {
            0 => new(1, 1, 1, false, 1, 1, 1, 0, false),
            1 => new(3, 2, 2, false, 2, 1, 1, 1, false),
            2 => new(4, 3, 3, false, 3, 2, 1, 1, false),
            3 => new(7, 5, 3, false, 4, 2, 1, 2, false),
            4 => new(9, 6, 3, true, 6, 2, 2, 2, false),
            5 => new(9, 6, 3, true, 12, 3, 4, 3, true),
            _ => new(9, 6, 3, true, 30, 4, 4, 4, true),
        };
    }

    /// <summary>The search for one block, reused per thread.</summary>
    private sealed class Search
    {
        private readonly int[] _px = new int[64];
        private readonly byte[] _decoded = new byte[64];
        private readonly BlockFields _best = new();
        private readonly BlockFields _work = new();
        private readonly int[] _texels = new int[16];
        private readonly int[] _src = new int[4 * 16];
        private readonly int[] _comp = new int[4];
        private readonly int[] _cw = new int[4];
        private readonly byte[] _idx = new byte[16];
        private readonly byte[] _trialIdx = new byte[16];
        private readonly int[] _qlo = new int[4];
        private readonly int[] _qhi = new int[4];
        private readonly int[] _tlo = new int[4];
        private readonly int[] _thi = new int[4];
        private readonly double[] _lo = new double[4];
        private readonly double[] _hi = new double[4];
        private readonly int[] _ranked = new int[3 * 3 * 30];
        private readonly bool[] _rankedValid = new bool[3 * 3];
        private readonly (long Error, int Pattern)[] _finalists = new (long, int)[30];
        private readonly long[] _moments = new long[16 * 15];
        private readonly long[] _totals = new long[15];
        private readonly long[] _sums = new long[3 * 15];
        private readonly double[] _scores = new double[30];
        private readonly double[] _cov = new double[16];
        private readonly int[] _palette = new int[32 * 4];
        private readonly int[] _start = new int[4];
        private readonly int[] _span = new int[4];
        private int _n;
        private int _nc;

        public long Run(ReadOnlySpan<byte> rgba, Span<byte> block, Settings settings)
        {
            bool opaque = true;
            bool gray = true;
            for (int t = 0; t < 16; t++)
            {
                int i = t * 4;
                _px[i] = rgba[i];
                _px[i + 1] = rgba[i + 1];
                _px[i + 2] = rgba[i + 2];
                _px[i + 3] = rgba[i + 3];
                opaque &= rgba[i + 3] == 255;
                gray &= rgba[i] == rgba[i + 1] && rgba[i] == rgba[i + 2];
            }

            Array.Clear(_rankedValid);
            _best.Error = long.MaxValue;
            if (gray)
            {
                TryModes(opaque ? GrayOpaqueModes[..(settings.Gray + 1)] : GrayModes[..settings.Gray], settings);
            }

            if (!gray || settings.BothFamilies)
            {
                TryModes(opaque ? OpaqueModes[..settings.Opaque] : AlphaModes[..settings.Alpha], settings);
            }

            Pack(_best, block);
            Uastc.Unpack(block, _decoded);
            _best.Hints = Hints(_decoded, _best.Mode);
            Pack(_best, block);
            return _best.Error;
        }

        /// <summary>Fits the block with a mode's fields fixed, filling <paramref name="b"/>'s endpoints and weights; returns the squared error.</summary>
        public long FitMode(BlockFields b, int mode, int pattern, int ccs, int refits, bool nudge)
        {
            b.Mode = mode;
            b.Pattern = pattern;
            b.Ccs = ccs;
            b.Hints = 0;
            int subsets = Uastc.Subsets[mode];
            int planes = Uastc.Planes[mode];
            int comps = Uastc.Components[mode];
            int plane2 = Plane2Component(mode, ccs);
            byte[]? table = subsets > 1 ? PatternsOf(mode)[pattern].Table : null;
            long total = 0;
            for (int s = 0; s < subsets; s++)
            {
                GatherTexels(table, s);
                for (int p = 0; p < planes; p++)
                {
                    int mask = planes == 1 ? (1 << comps) - 1 : p == 1 ? 1 << plane2 : ((1 << comps) - 1) & ~(1 << plane2);
                    total += FitGroup(b, s, p, mask, refits, nudge);
                }
            }

            // The anchor rule: swap a group's endpoints and invert its weights when its anchor's weight has its top bit set.
            int levels = 1 << Uastc.WeightBits[mode];
            for (int s = 0; s < subsets; s++)
            {
                int anchor = Anchor(mode, pattern, s);
                if (anchor == 255)
                {
                    continue;
                }

                for (int p = 0; p < planes; p++)
                {
                    if (b.Weights[(anchor * planes) + p] < levels / 2)
                    {
                        continue;
                    }

                    int mask = planes == 1 ? (1 << comps) - 1 : p == 1 ? 1 << plane2 : ((1 << comps) - 1) & ~(1 << plane2);
                    for (int k = 0; k < comps; k++)
                    {
                        if ((mask & (1 << k)) != 0)
                        {
                            int e = ((s * comps) + k) * 2;
                            (b.Endpoints[e], b.Endpoints[e + 1]) = (b.Endpoints[e + 1], b.Endpoints[e]);
                        }
                    }

                    for (int t = 0; t < 16; t++)
                    {
                        if (table is null || table[t] == s)
                        {
                            b.Weights[(t * planes) + p] = (byte)(levels - 1 - b.Weights[(t * planes) + p]);
                        }
                    }
                }
            }

            b.Error = total;
            return total;
        }

        /// <summary>
        /// Keeps <paramref name="b"/>'s mode, pattern, selector and endpoints and picks this block's best weights for them, the
        /// anchors held to their lower half so the endpoints need no swap.
        /// </summary>
        public void RefitWeights(BlockFields b)
        {
            int mode = b.Mode;
            int subsets = Uastc.Subsets[mode];
            int planes = Uastc.Planes[mode];
            int comps = Uastc.Components[mode];
            int plane2 = Plane2Component(mode, b.Ccs);
            byte[]? table = subsets > 1 ? PatternsOf(mode)[b.Pattern].Table : null;
            int range = Uastc.EndpointRanges[mode];
            byte[] wTable = AstcIse.WeightUnquantized[Uastc.WeightRanges[mode]];
            for (int s = 0; s < subsets; s++)
            {
                GatherTexels(table, s);
                for (int p = 0; p < planes; p++)
                {
                    int mask = planes == 1 ? (1 << comps) - 1 : p == 1 ? 1 << plane2 : ((1 << comps) - 1) & ~(1 << plane2);
                    GatherGroup(comps, mask);
                    for (int k = 0; k < comps; k++)
                    {
                        _qlo[k] = b.Endpoints[((s * comps) + k) * 2];
                        _qhi[k] = b.Endpoints[(((s * comps) + k) * 2) + 1];
                    }

                    Assign(range, wTable, _qlo, _qhi, _idx, anchorFirst: true);
                    for (int j = 0; j < _n; j++)
                    {
                        b.Weights[(_texels[j] * planes) + p] = _idx[j];
                    }
                }
            }
        }

        /// <summary>Keeps <paramref name="b"/>'s mode, pattern, selector and weights and fits this block's endpoints to them.</summary>
        public void RefitEndpoints(BlockFields b)
        {
            int mode = b.Mode;
            int subsets = Uastc.Subsets[mode];
            int planes = Uastc.Planes[mode];
            int comps = Uastc.Components[mode];
            int plane2 = Plane2Component(mode, b.Ccs);
            byte[]? table = subsets > 1 ? PatternsOf(mode)[b.Pattern].Table : null;
            int range = Uastc.EndpointRanges[mode];
            byte[] wTable = AstcIse.WeightUnquantized[Uastc.WeightRanges[mode]];
            for (int s = 0; s < subsets; s++)
            {
                GatherTexels(table, s);
                for (int p = 0; p < planes; p++)
                {
                    int mask = planes == 1 ? (1 << comps) - 1 : p == 1 ? 1 << plane2 : ((1 << comps) - 1) & ~(1 << plane2);
                    GatherGroup(comps, mask);
                    for (int j = 0; j < _n; j++)
                    {
                        _idx[j] = b.Weights[(_texels[j] * planes) + p];
                    }

                    LeastSquares(wTable, _idx);
                    Quantize(range, _qlo, _qhi);
                    for (int c = 0; c < _nc; c++)
                    {
                        int k = _comp[c];
                        b.Endpoints[((s * comps) + k) * 2] = _qlo[k];
                        b.Endpoints[(((s * comps) + k) * 2) + 1] = _qhi[k];
                    }
                }
            }
        }

        /// <summary>Loads 16 RGBA texels for <see cref="RefitWeights"/> and <see cref="RefitEndpoints"/>.</summary>
        public void Load(ReadOnlySpan<byte> rgba)
        {
            for (int i = 0; i < 64; i++)
            {
                _px[i] = rgba[i];
            }
        }

        private void TryModes(ReadOnlySpan<byte> modes, Settings settings)
        {
            foreach (int mode in modes)
            {
                if (_best.Error == 0)
                {
                    return;
                }

                int subsets = Uastc.Subsets[mode];
                if (subsets > 1)
                {
                    TryPatterns(mode, settings);
                }
                else if (Uastc.Planes[mode] == 2)
                {
                    TrySelectors(mode, settings);
                }
                else
                {
                    Consider(FitMode(_work, mode, 0, 0, settings.Refits, settings.Nudge));
                }
            }
        }

        private void Consider(long error)
        {
            if (error < _best.Error)
            {
                _best.CopyFrom(_work);
            }
        }

        /// <summary>Fits the best-ranked patterns quickly, then the best few of those in full.</summary>
        private void TryPatterns(int mode, Settings settings)
        {
            Pattern[] patterns = PatternsOf(mode);
            ReadOnlySpan<int> ranked = RankPatterns(mode, patterns);
            int count = Math.Min(settings.Patterns, patterns.Length);
            if (count <= settings.Finalists)
            {
                for (int i = 0; i < count; i++)
                {
                    Consider(FitMode(_work, mode, ranked[i], 0, settings.Refits, settings.Nudge));
                }

                return;
            }

            for (int i = 0; i < count; i++)
            {
                _finalists[i] = (FitMode(_work, mode, ranked[i], 0, 0, false), i);
                Consider(_work.Error);
            }

            Array.Sort(_finalists, 0, count);
            for (int i = 0; i < settings.Finalists; i++)
            {
                Consider(FitMode(_work, mode, ranked[_finalists[i].Pattern], 0, settings.Refits, settings.Nudge));
            }
        }

        /// <summary>Tries the second plane on the components least correlated with the others first.</summary>
        private void TrySelectors(int mode, Settings settings)
        {
            if (mode == 17)
            {
                Consider(FitMode(_work, mode, 0, 0, settings.Refits, settings.Nudge));
                return;
            }

            int comps = Uastc.Components[mode];
            Span<double> mean = stackalloc double[4];
            Span<double> cov = stackalloc double[16];
            Covariance(null, 0, comps, mean, cov);
            Span<double> score = stackalloc double[4];
            Span<int> order = stackalloc int[4];
            for (int c = 0; c < comps; c++)
            {
                order[c] = c;
                for (int k = 0; k < comps; k++)
                {
                    if (k != c)
                    {
                        double d = Math.Sqrt(cov[(c * 4) + c] * cov[(k * 4) + k]);
                        score[c] += d > 1e-9 ? Math.Abs(cov[(c * 4) + k]) / d : 1;
                    }
                }
            }

            for (int i = 1; i < comps; i++)
            {
                for (int j = i; j > 0 && score[order[j]] < score[order[j - 1]]; j--)
                {
                    (order[j], order[j - 1]) = (order[j - 1], order[j]);
                }
            }

            for (int i = 0; i < Math.Min(settings.Selectors, comps); i++)
            {
                Consider(FitMode(_work, mode, 0, order[i], settings.Refits, settings.Nudge));
            }
        }

        /// <summary>Ranks a mode's patterns by how far each subset's texels lie from their principal axis, best first.</summary>
        private ReadOnlySpan<int> RankPatterns(int mode, Pattern[] patterns)
        {
            int comps = Uastc.Components[mode];
            int key = ((mode == 3 ? 1 : mode == 7 ? 2 : 0) * 3) + (comps - 2);
            Span<int> ranked = _ranked.AsSpan(key * 30, patterns.Length);
            if (_rankedValid[key])
            {
                return ranked;
            }

            // Each texel's count, values and products, so a subset's moments are sums; the last subset's are the block's less
            // the others'.
            int m = 1 + comps + (comps * (comps + 1) / 2);
            Span<long> moments = _moments;
            Span<long> total = _totals;
            total.Clear();
            for (int t = 0; t < 16; t++)
            {
                Span<long> row = moments.Slice(t * 15, 15);
                row[0] = 1;
                int at = 1 + comps;
                for (int c = 0; c < comps; c++)
                {
                    row[1 + c] = Source(t, c, comps);
                    for (int k = 0; k <= c; k++)
                    {
                        row[at++] = row[1 + c] * row[1 + k];
                    }
                }

                for (int k = 0; k < m; k++)
                {
                    total[k] += row[k];
                }
            }

            Span<long> sums = _sums;
            Span<double> scores = _scores;
            Span<double> cov = _cov;
            int subsets = Uastc.Subsets[mode];
            for (int i = 0; i < patterns.Length; i++)
            {
                byte[] table = patterns[i].Table;
                sums.Clear();
                for (int t = 0; t < 16; t++)
                {
                    int s = table[t];
                    if (s == subsets - 1)
                    {
                        continue;
                    }

                    Span<long> sum = sums.Slice(s * 15, m);
                    ReadOnlySpan<long> row = moments.Slice(t * 15, m);
                    for (int k = 0; k < m; k++)
                    {
                        sum[k] += row[k];
                    }
                }

                Span<long> last = sums.Slice((subsets - 1) * 15, m);
                total[..m].CopyTo(last);
                for (int s = 0; s < subsets - 1; s++)
                {
                    for (int k = 0; k < m; k++)
                    {
                        last[k] -= sums[(s * 15) + k];
                    }
                }

                double score = 0;
                for (int s = 0; s < subsets; s++)
                {
                    score += Residual(sums.Slice(s * 15, m), comps, cov);
                }

                scores[i] = score;
                ranked[i] = i;
            }

            for (int i = 1; i < patterns.Length; i++)
            {
                for (int j = i; j > 0 && scores[ranked[j]] < scores[ranked[j - 1]]; j--)
                {
                    (ranked[j], ranked[j - 1]) = (ranked[j - 1], ranked[j]);
                }
            }

            _rankedValid[key] = true;
            return ranked;
        }

        /// <summary>A subset's squared distance from its principal axis, from its moments (count, values, products).</summary>
        private static double Residual(ReadOnlySpan<long> moments, int comps, Span<double> cov)
        {
            long n = moments[0];
            if (n < 2)
            {
                return 0;
            }

            cov.Clear();
            double trace = 0;
            int at = 1 + comps;
            for (int c = 0; c < comps; c++)
            {
                for (int k = 0; k <= c; k++)
                {
                    double v = moments[at++] - ((double)moments[1 + c] * moments[1 + k] / n);
                    cov[(c * 4) + k] = cov[(k * 4) + c] = v;
                }

                trace += cov[(c * 4) + c];
            }

            return trace - LargestEigenvalue(cov, comps);
        }

        /// <summary>The mean and covariance (4 × 4, row-major) of a subset's texels in a mode's components; returns the texel count.</summary>
        private int Covariance(byte[]? table, int subset, int comps, Span<double> mean, Span<double> cov)
        {
            mean.Clear();
            cov.Clear();
            int n = 0;
            Span<int> v = stackalloc int[4];
            for (int t = 0; t < 16; t++)
            {
                if (table is not null && table[t] != subset)
                {
                    continue;
                }

                n++;
                for (int c = 0; c < comps; c++)
                {
                    v[c] = Source(t, c, comps);
                    mean[c] += v[c];
                    for (int k = 0; k <= c; k++)
                    {
                        cov[(c * 4) + k] += v[c] * v[k];
                    }
                }
            }

            if (n == 0)
            {
                return 0;
            }

            for (int c = 0; c < comps; c++)
            {
                mean[c] /= n;
            }

            for (int c = 0; c < comps; c++)
            {
                for (int k = 0; k <= c; k++)
                {
                    cov[(c * 4) + k] = cov[(k * 4) + c] = (cov[(c * 4) + k] / n) - (mean[c] * mean[k]);
                }
            }

            return n;
        }

        private static double LargestEigenvalue(ReadOnlySpan<double> cov, int comps)
        {
            Span<double> axis = stackalloc double[4];
            PrincipalAxis(cov, comps, axis);
            double lambda = 0;
            for (int c = 0; c < 4; c++)
            {
                lambda += ((cov[c * 4] * axis[0]) + (cov[(c * 4) + 1] * axis[1]) + (cov[(c * 4) + 2] * axis[2]) + (cov[(c * 4) + 3] * axis[3])) * axis[c];
            }

            return lambda;
        }

        /// <summary>
        /// The principal axis of a covariance (4 × 4, zero past <paramref name="comps"/>) by power iteration from the row of the
        /// largest variance.
        /// </summary>
        private static void PrincipalAxis(ReadOnlySpan<double> cov, int comps, Span<double> axis)
        {
            int widest = 0;
            for (int c = 1; c < comps; c++)
            {
                if (cov[(c * 4) + c] > cov[(widest * 4) + widest])
                {
                    widest = c;
                }
            }

            double x = cov[widest * 4];
            double y = cov[(widest * 4) + 1];
            double z = cov[(widest * 4) + 2];
            double w = cov[(widest * 4) + 3];
            for (int iteration = 0; iteration < 4; iteration++)
            {
                double nx = (cov[0] * x) + (cov[1] * y) + (cov[2] * z) + (cov[3] * w);
                double ny = (cov[4] * x) + (cov[5] * y) + (cov[6] * z) + (cov[7] * w);
                double nz = (cov[8] * x) + (cov[9] * y) + (cov[10] * z) + (cov[11] * w);
                double nw = (cov[12] * x) + (cov[13] * y) + (cov[14] * z) + (cov[15] * w);
                double scale = Math.Max(Math.Max(Math.Abs(nx), Math.Abs(ny)), Math.Max(Math.Abs(nz), Math.Abs(nw)));
                if (scale < 1e-12)
                {
                    break;
                }

                (x, y, z, w) = (nx / scale, ny / scale, nz / scale, nw / scale);
            }

            double length = Math.Sqrt((x * x) + (y * y) + (z * z) + (w * w));
            if (length < 1e-12)
            {
                axis.Clear();
                axis[0] = 1;
                return;
            }

            (axis[0], axis[1], axis[2], axis[3]) = (x / length, y / length, z / length, w / length);
        }

        /// <summary>A texel's value in a mode's component <paramref name="c"/>: luminance and alpha for two components.</summary>
        private int Source(int t, int c, int comps) => comps != 2 ? _px[(t * 4) + c]
            : c == 1 ? _px[(t * 4) + 3]
            : (_px[t * 4] + _px[(t * 4) + 1] + _px[(t * 4) + 2] + 1) / 3;

        private void GatherTexels(byte[]? table, int subset)
        {
            _n = 0;
            for (int t = 0; t < 16; t++)
            {
                if (table is null || table[t] == subset)
                {
                    _texels[_n++] = t;
                }
            }
        }

        /// <summary>Lists a group's components and gathers its texels' values; luminance counts three times, once per colour channel.</summary>
        private void GatherGroup(int comps, int mask)
        {
            _nc = 0;
            for (int k = 0; k < comps; k++)
            {
                if ((mask & (1 << k)) == 0)
                {
                    continue;
                }

                _comp[_nc] = k;
                _cw[_nc] = comps == 2 && k == 0 ? 3 : 1;
                for (int j = 0; j < _n; j++)
                {
                    _src[(_nc * 16) + j] = Source(_texels[j], k, comps);
                }

                _nc++;
            }
        }

        /// <summary>Fits one subset's endpoints in the components of one plane and picks its weights; returns the squared error.</summary>
        private long FitGroup(BlockFields b, int s, int p, int mask, int refits, bool nudge)
        {
            int mode = b.Mode;
            int comps = Uastc.Components[mode];
            int planes = Uastc.Planes[mode];
            int range = Uastc.EndpointRanges[mode];
            byte[] wTable = AstcIse.WeightUnquantized[Uastc.WeightRanges[mode]];
            GatherGroup(comps, mask);
            if (_n == 0)
            {
                for (int k = 0; k < comps; k++)
                {
                    if ((mask & (1 << k)) != 0)
                    {
                        b.Endpoints[((s * comps) + k) * 2] = b.Endpoints[(((s * comps) + k) * 2) + 1] = 0;
                    }
                }

                return 0;
            }

            InitialEndpoints();
            Quantize(range, _qlo, _qhi);
            long error = Assign(range, wTable, _qlo, _qhi, _idx, anchorFirst: false);
            for (int r = 0; r < refits && error > 0; r++)
            {
                LeastSquares(wTable, _idx);
                Quantize(range, _tlo, _thi);
                long trial = Assign(range, wTable, _tlo, _thi, _trialIdx, anchorFirst: false);
                if (trial >= error)
                {
                    break;
                }

                error = trial;
                Accept();
            }

            if (nudge)
            {
                error = Nudge(range, wTable, error);
            }

            for (int c = 0; c < _nc; c++)
            {
                int k = _comp[c];
                b.Endpoints[((s * comps) + k) * 2] = _qlo[k];
                b.Endpoints[(((s * comps) + k) * 2) + 1] = _qhi[k];
            }

            for (int j = 0; j < _n; j++)
            {
                b.Weights[(_texels[j] * planes) + p] = _idx[j];
            }

            return error;
        }

        private void Accept()
        {
            _tlo.CopyTo(_qlo, 0);
            _thi.CopyTo(_qhi, 0);
            _trialIdx.CopyTo(_idx, 0);
        }

        /// <summary>Moves each quantised endpoint a level either way while that lowers the error.</summary>
        private long Nudge(int range, byte[] wTable, long error)
        {
            byte[] sorted = Sorted[range];
            byte[] rank = Rank[range];
            for (int pass = 0; pass < 2 && error > 0; pass++)
            {
                bool improved = false;
                for (int c = 0; c < _nc; c++)
                {
                    int k = _comp[c];
                    for (int end = 0; end < 2; end++)
                    {
                        for (int dir = -1; dir <= 1; dir += 2)
                        {
                            _qlo.CopyTo(_tlo, 0);
                            _qhi.CopyTo(_thi, 0);
                            int[] ends = end == 0 ? _tlo : _thi;
                            int r = rank[ends[k]] + dir;
                            if (r < 0 || r >= sorted.Length)
                            {
                                continue;
                            }

                            ends[k] = sorted[r];
                            long trial = Assign(range, wTable, _tlo, _thi, _trialIdx, anchorFirst: false);
                            if (trial < error)
                            {
                                error = trial;
                                Accept();
                                improved = true;
                            }
                        }
                    }
                }

                if (!improved)
                {
                    break;
                }
            }

            return error;
        }

        /// <summary>Starts the endpoints at the ends of the texels' spread along their principal axis.</summary>
        private void InitialEndpoints()
        {
            Span<double> mean = stackalloc double[4];
            for (int c = 0; c < _nc; c++)
            {
                double sum = 0;
                for (int j = 0; j < _n; j++)
                {
                    sum += _src[(c * 16) + j];
                }

                mean[c] = sum / _n;
            }

            if (_nc == 1)
            {
                int min = 255;
                int max = 0;
                for (int j = 0; j < _n; j++)
                {
                    min = Math.Min(min, _src[j]);
                    max = Math.Max(max, _src[j]);
                }

                _lo[_comp[0]] = min;
                _hi[_comp[0]] = max;
                return;
            }

            Span<double> cov = stackalloc double[16];
            for (int c = 0; c < _nc; c++)
            {
                for (int k = 0; k <= c; k++)
                {
                    double sum = 0;
                    for (int j = 0; j < _n; j++)
                    {
                        sum += (_src[(c * 16) + j] - mean[c]) * (_src[(k * 16) + j] - mean[k]);
                    }

                    cov[(c * 4) + k] = cov[(k * 4) + c] = sum / _n;
                }
            }

            Span<double> axis = stackalloc double[4];
            PrincipalAxis(cov, _nc, axis);
            double tmin = double.MaxValue;
            double tmax = double.MinValue;
            for (int j = 0; j < _n; j++)
            {
                double t = 0;
                for (int c = 0; c < _nc; c++)
                {
                    t += (_src[(c * 16) + j] - mean[c]) * axis[c];
                }

                tmin = Math.Min(tmin, t);
                tmax = Math.Max(tmax, t);
            }

            for (int c = 0; c < _nc; c++)
            {
                _lo[_comp[c]] = Math.Clamp(mean[c] + (tmin * axis[c]), 0, 255);
                _hi[_comp[c]] = Math.Clamp(mean[c] + (tmax * axis[c]), 0, 255);
            }
        }

        /// <summary>Fits the endpoints to the texels for fixed weights by least squares.</summary>
        private void LeastSquares(byte[] wTable, ReadOnlySpan<byte> idx)
        {
            double aa = 0;
            double ab = 0;
            double bb = 0;
            Span<double> xa = stackalloc double[4];
            Span<double> xb = stackalloc double[4];
            for (int j = 0; j < _n; j++)
            {
                double a = wTable[idx[j]] / 64.0;
                double b = 1 - a;
                aa += a * a;
                ab += a * b;
                bb += b * b;
                for (int c = 0; c < _nc; c++)
                {
                    xa[c] += a * _src[(c * 16) + j];
                    xb[c] += b * _src[(c * 16) + j];
                }
            }

            double det = (aa * bb) - (ab * ab);
            for (int c = 0; c < _nc; c++)
            {
                int k = _comp[c];
                if (Math.Abs(det) < 1e-9)
                {
                    double mean = (xa[c] + xb[c]) / _n;
                    _lo[k] = _hi[k] = mean;
                    continue;
                }

                _lo[k] = Math.Clamp(((xb[c] * aa) - (xa[c] * ab)) / det, 0, 255);
                _hi[k] = Math.Clamp(((xa[c] * bb) - (xb[c] * ab)) / det, 0, 255);
            }
        }

        private void Quantize(int range, int[] lo, int[] hi)
        {
            byte[] nearest = AstcEncoder.ColorQuantize[range];
            for (int c = 0; c < _nc; c++)
            {
                int k = _comp[c];
                lo[k] = nearest[(int)(_lo[k] + 0.5)];
                hi[k] = nearest[(int)(_hi[k] + 0.5)];
            }
        }

        /// <summary>
        /// Picks each texel's weight with the decoder's interpolation and returns the squared error; with
        /// <paramref name="anchorFirst"/> the first texel (the subset's anchor) keeps to the lower half of the weights. With 8 or
        /// more weights only the three nearest the texel's projection on the endpoints' segment are tried.
        /// </summary>
        private long Assign(int range, byte[] wTable, int[] lo, int[] hi, Span<byte> idx, bool anchorFirst)
        {
            byte[] unq = AstcIse.ColorUnquantized[range];
            int levels = wTable.Length;
            Span<int> palette = _palette;
            Span<int> start = _start;
            Span<int> span = _span;
            long length = 0;
            for (int c = 0; c < _nc; c++)
            {
                int k = _comp[c];
                start[c] = unq[lo[k]];
                span[c] = unq[hi[k]] - start[c];
                length += _cw[c] * span[c] * span[c];
                for (int l = 0; l < levels; l++)
                {
                    palette[(l * 4) + c] = Uastc.Interpolate(start[c], unq[hi[k]], wTable[l]);
                }
            }

            byte[] nearest = WeightNearest[System.Numerics.BitOperations.Log2((uint)levels)];
            bool project = levels >= 8 && length > 0;
            long total = 0;
            for (int j = 0; j < _n; j++)
            {
                int limit = anchorFirst && j == 0 ? levels / 2 : levels;
                int first = 0;
                int last = limit - 1;
                if (project)
                {
                    long dot = 0;
                    for (int c = 0; c < _nc; c++)
                    {
                        dot += _cw[c] * (_src[(c * 16) + j] - start[c]) * span[c];
                    }

                    int guess = nearest[Math.Clamp((int)(((dot * 64) + (length / 2)) / length), 0, 64)];
                    last = Math.Min(last, guess + 1);
                    first = Math.Min(Math.Max(0, guess - 1), last);
                }

                int best = int.MaxValue;
                int pick = first;
                for (int l = first; l <= last; l++)
                {
                    int e = 0;
                    for (int c = 0; c < _nc; c++)
                    {
                        int d = palette[(l * 4) + c] - _src[(c * 16) + j];
                        e += _cw[c] * d * d;
                    }

                    if (e < best)
                    {
                        best = e;
                        pick = l;
                    }
                }

                idx[j] = (byte)pick;
                total += best;
            }

            return total;
        }
    }

    /// <summary>How much of the window each RDO effort searches.</summary>
    private readonly record struct RdoSettings(int Window, bool Above, bool Endpoints, bool Weights)
    {
        public static RdoSettings For(int effort) => Math.Clamp(effort, 0, 6) switch
        {
            0 => new(2, false, false, false),
            1 => new(4, true, true, false),
            2 => new(8, true, true, false),
            3 => new(12, true, true, true),
            4 => new(16, true, true, true),
            5 => new(32, true, true, true),
            _ => new(64, true, true, true),
        };
    }

    /// <summary>The RDO pass over one band of block rows.</summary>
    private sealed class RdoState(int columns, int rows)
    {
        private readonly byte[] _decoded = new byte[columns * rows * 64];
        private readonly byte[] _texels = new byte[64];
        private readonly bool[] _inside = new bool[16];
        private readonly byte[] _candidate = new byte[16];
        private readonly byte[] _best = new byte[16];
        private readonly byte[] _scratch = new byte[64];
        private readonly int[] _window = new int[64 + 3];
        private readonly BlockFields _source = new();
        private readonly BlockFields _trial = new();
        private readonly Search _search = new();
        private int _windowCount;
        private int _lastDistance;
        private double _bestCost;
        private int _bestDistance;
        private bool _changed;

        public void Run(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> blocks, int firstRow, float lambda, RdoSettings settings)
        {
            int start = firstRow * columns;
            for (int i = 0; i < columns * rows; i++)
            {
                int bx = i % columns;
                int by = firstRow + (i / columns);
                Gather(rgba, width, height, bx, by);
                Span<byte> block = blocks.Slice((start + i) * 16, 16);
                Span<byte> decoded = _decoded.AsSpan(i * 64, 64);
                Decode(block, decoded);
                ListWindow(i, settings);
                long error = Error(decoded);
                _bestCost = error + (lambda * Bits(block, blocks, start, i, out _bestDistance));
                block.CopyTo(_best);
                _changed = false;
                _search.Load(_texels);
                for (int w = 0; w < _windowCount; w++)
                {
                    int j = _window[w];
                    ReadOnlySpan<byte> earlier = blocks.Slice((start + j) * 16, 16);
                    Try(earlier, Error(_decoded.AsSpan(j * 64, 64)), blocks, start, i, lambda);
                    if (!settings.Endpoints || !Parse(earlier, _source))
                    {
                        continue;
                    }

                    _trial.CopyFrom(_source);
                    _search.RefitWeights(_trial);
                    Pack(_trial, _candidate);
                    Try(_candidate, -1, blocks, start, i, lambda);
                    if (settings.Weights)
                    {
                        _trial.CopyFrom(_source);
                        _search.RefitEndpoints(_trial);
                        Pack(_trial, _candidate);
                        Try(_candidate, -1, blocks, start, i, lambda);
                    }
                }

                if (_changed)
                {
                    _best.CopyTo(block);

                    // A block with refitted endpoints keeps the earlier block's hints; give it its own.
                    if (Parse(block, _trial))
                    {
                        Decode(block, decoded);
                        int hints = Hints(decoded, _trial.Mode);
                        if (hints != _trial.Hints)
                        {
                            _trial.Hints = hints;
                            Pack(_trial, _candidate);
                            if (Bits(_candidate, blocks, start, i, out int distance) <= Bits(block, blocks, start, i, out _))
                            {
                                _candidate.CopyTo(block);
                                _bestDistance = distance;
                            }
                        }
                    }

                    Decode(block, decoded);
                }

                _lastDistance = _bestDistance;
            }
        }

        /// <summary>Keeps <paramref name="candidate"/> if its error plus lambda times its bits is the lowest yet.</summary>
        private void Try(ReadOnlySpan<byte> candidate, long knownError, ReadOnlySpan<byte> blocks, int start, int i, float lambda)
        {
            long e = knownError;
            if (e < 0)
            {
                Decode(candidate, _scratch);
                e = Error(_scratch);
            }

            double cost = e + (lambda * Bits(candidate, blocks, start, i, out int distance));
            if (cost < _bestCost)
            {
                _bestCost = cost;
                _bestDistance = distance;
                candidate.CopyTo(_best);
                _changed = true;
            }
        }

        private static void Decode(ReadOnlySpan<byte> block, Span<byte> rgba)
        {
            if (!Uastc.Unpack(block, rgba))
            {
                rgba.Clear();
            }
        }

        /// <summary>Copies the block's texels, repeating the image's last row and column, and notes which lie inside the image.</summary>
        private void Gather(ReadOnlySpan<byte> rgba, int width, int height, int bx, int by)
        {
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int px = (bx * 4) + x;
                    int py = (by * 4) + y;
                    _inside[(y * 4) + x] = px < width && py < height;
                    int source = ((Math.Min(py, height - 1) * width) + Math.Min(px, width - 1)) * 4;
                    rgba.Slice(source, 4).CopyTo(_texels.AsSpan(((y * 4) + x) * 4));
                }
            }
        }

        private long Error(ReadOnlySpan<byte> decoded)
        {
            long sum = 0;
            for (int t = 0; t < 16; t++)
            {
                if (!_inside[t])
                {
                    continue;
                }

                for (int c = 0; c < 4; c++)
                {
                    int d = decoded[(t * 4) + c] - _texels[(t * 4) + c];
                    sum += d * d;
                }
            }

            return sum;
        }

        /// <summary>Lists the earlier blocks of the band a block may copy from, nearest first: the ones just before it, then above it.</summary>
        private void ListWindow(int i, RdoSettings settings)
        {
            _windowCount = 0;
            for (int j = i - 1; j >= Math.Max(0, i - settings.Window); j--)
            {
                _window[_windowCount++] = j;
            }

            if (!settings.Above || i < columns)
            {
                return;
            }

            int bx = i % columns;
            for (int x = Math.Max(0, bx - 1); x <= Math.Min(columns - 1, bx + 1); x++)
            {
                int j = i - columns + x - bx;
                if (j < i - settings.Window)
                {
                    _window[_windowCount++] = j;
                }
            }
        }

        /// <summary>
        /// Estimates a block's compressed bits next to the window: per earlier block, eight bits for each byte outside a run of
        /// at least four equal bytes and a match's cost for each run; the cheapest earlier block (or none) counts.
        /// </summary>
        private double Bits(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> blocks, int start, int i, out int bestDistance)
        {
            double best = 128;
            bestDistance = 0;
            for (int w = 0; w < _windowCount; w++)
            {
                int j = _window[w];
                ReadOnlySpan<byte> earlier = blocks.Slice((start + j) * 16, 16);
                int distance = (i - j) * 16;
                double match = distance == _lastDistance ? 8 : 12 + Math.Log2(distance);
                double cost = 0;
                for (int at = 0; at < 16 && cost < best;)
                {
                    if (candidate[at] != earlier[at])
                    {
                        cost += 8;
                        at++;
                        continue;
                    }

                    int end = at + 1;
                    while (end < 16 && candidate[end] == earlier[end])
                    {
                        end++;
                    }

                    cost += end - at >= MinMatch ? match : 8 * (end - at);
                    at = end;
                }

                if (cost < best)
                {
                    best = cost;
                    bestDistance = distance;
                }
            }

            return best;
        }
    }
}
