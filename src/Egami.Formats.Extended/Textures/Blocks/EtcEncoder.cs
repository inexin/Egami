using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Encodes ETC1, ETC2 and EAC blocks at effort 0 to 6. Colour blocks try both subblock orientations in the individual and
/// differential modes, fitting each subblock's base colour by least squares on the chosen modifiers; from effort 3 the ETC2
/// encoder also tries the T and H modes (two colour clusters) and the planar mode (a least-squares gradient), and higher efforts
/// search neighbouring base colours; effort 0 codes the subblock means alone and tries only the likeliest tables. EAC blocks
/// search the base value, multiplier and modifier table. Every candidate is scored with the decoder's own arithmetic (clamping
/// included), so the error measured is the error a reader sees; the scoring works on four or eight texels at once with
/// <see cref="Vector128"/>. The work for one block depends only on that block, so the output never depends on how many
/// threads encode.
/// </summary>
internal static class EtcEncoder
{
    private const long Impossible = long.MaxValue / 4;

    /// <summary>
    /// Encodes 16 RGBA pixels (4 bytes each, row order) into an 8-byte colour block. <paramref name="etc2"/> allows the T, H
    /// and planar modes (ETC1 never uses them); <paramref name="punchThrough"/> encodes ETC2 RGB8A1, where pixels with alpha
    /// below 128 become transparent black.
    /// </summary>
    public static void EncodeRgb(ReadOnlySpan<byte> rgba, Span<byte> block, int effort, bool etc2, bool punchThrough)
    {
        Span<int> px = stackalloc int[48];
        Span<bool> clear = stackalloc bool[16];
        bool anyClear = false;
        for (int i = 0; i < 16; i++)
        {
            px[i * 3] = rgba[i * 4];
            px[(i * 3) + 1] = rgba[(i * 4) + 1];
            px[(i * 3) + 2] = rgba[(i * 4) + 2];
            clear[i] = punchThrough && rgba[(i * 4) + 3] < 128;
            anyClear |= clear[i];
        }

        bool opaque = !anyClear;
        Best best = new(Impossible, 0);
        Subblocks(px, clear, effort, punchThrough, opaque, ref best);
        if (etc2 && effort >= 3)
        {
            Rows rows = new(px);
            TwoColors(px, clear, rows, effort, punchThrough, opaque, ref best);
            if (opaque)
            {
                Planar(px, rows, effort, ref best);
            }
        }

        BinaryPrimitives.WriteUInt64BigEndian(block, best.Bits);
    }

    /// <summary>Encodes 16 RGBA pixels (4 bytes each, row order) into a 16-byte ETC2 RGBA8 block: EAC alpha, then ETC2 colour.</summary>
    public static void EncodeRgba(ReadOnlySpan<byte> rgba, Span<byte> block, int effort)
    {
        EncodeEac8(rgba[3..], 4, block, effort);
        EncodeRgb(rgba, block[8..], effort, etc2: true, punchThrough: false);
    }

    /// <summary>Encodes 16 8-bit values, every <paramref name="stride"/>th byte of <paramref name="values"/>, into an EAC block.</summary>
    public static void EncodeEac8(ReadOnlySpan<byte> values, int stride, Span<byte> block, int effort)
    {
        Span<int> v = stackalloc int[16];
        int min = 255, max = 0;
        for (int i = 0; i < 16; i++)
        {
            v[i] = values[i * stride];
            min = Math.Min(min, v[i]);
            max = Math.Max(max, v[i]);
        }

        ulong best = 0;
        long bestError = Impossible;
        if (min == max)
        {
            // Table 13 holds 0: the base alone, multiplier 1.
            best = Eac((ulong)min, 1, 13, 4);
            BinaryPrimitives.WriteUInt64BigEndian(block, best);
            return;
        }

        (int multiplierSpread, int keep, int baseSpread) = EacSearch(effort);
        Vector128<short> v0 = Vector128.Create((short)v[0], (short)v[1], (short)v[2], (short)v[3], (short)v[4], (short)v[5], (short)v[6], (short)v[7]);
        Vector128<short> v1 = Vector128.Create((short)v[8], (short)v[9], (short)v[10], (short)v[11], (short)v[12], (short)v[13], (short)v[14], (short)v[15]);
        int bestBase = 0, bestMultiplier = 1, bestTable = 0;
        Shortlist shortlist = new(stackalloc long[Shortlist.Max], stackalloc int[Shortlist.Max], keep);
        for (int t = 0; t < 16; t++)
        {
            ReadOnlySpan<sbyte> table = Etc.EacTables.Slice(t * 8, 8);
            int low = table[3], high = table[7];
            int guess = Math.Max(1, (int)Math.Round((max - min) / (double)(high - low)));
            for (int m = Math.Max(1, guess - multiplierSpread); m <= Math.Min(15, guess + multiplierSpread); m++)
            {
                int center = Math.Clamp((int)Math.Round(((min + max) / 2.0) - ((low + high) * m / 2.0)), 0, 255);
                long error = Eac8Error(v0, v1, center, table, m);
                if (error < bestError)
                {
                    (bestError, bestBase, bestMultiplier, bestTable) = (error, center, m, t);
                }

                shortlist.Add(error, (center << 8) | (t << 4) | m);
            }
        }

        // Around the base values of the best table and multiplier pairs.
        for (int i = 0; i < shortlist.Count; i++)
        {
            int key = shortlist.Key(i), center = key >> 8, t = (key >> 4) & 15, m = key & 15;
            ReadOnlySpan<sbyte> table = Etc.EacTables.Slice(t * 8, 8);
            for (int b = Math.Max(0, center - baseSpread); b <= Math.Min(255, center + baseSpread); b++)
            {
                long error = b == center ? Impossible : Eac8Error(v0, v1, b, table, m);
                if (error < bestError)
                {
                    (bestError, bestBase, bestMultiplier, bestTable) = (error, b, m, t);
                }
            }
        }

        ReadOnlySpan<sbyte> chosen = Etc.EacTables.Slice(bestTable * 8, 8);
        ulong indices = 0;
        for (int k = 0; k < 16; k++)
        {
            int target = v[Position(k)];
            int bestIndex = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                int d = Math.Abs(Math.Clamp(bestBase + (chosen[i] * bestMultiplier), 0, 255) - target);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestIndex = i;
                }
            }

            indices |= (ulong)bestIndex << (45 - (3 * k));
        }

        best = Eac((ulong)bestBase, bestMultiplier, bestTable, 0) | indices;
        BinaryPrimitives.WriteUInt64BigEndian(block, best);
    }

    /// <summary>
    /// Encodes 16 16-bit values, every <paramref name="stride"/>th value of <paramref name="values"/>, into an EAC R11 block,
    /// unsigned or signed (the values then mapping 0 to 65535 onto −1 to 1, as the decoder maps back).
    /// </summary>
    public static void EncodeEac11(ReadOnlySpan<ushort> values, int stride, Span<byte> block, int effort, bool signed)
    {
        // Work in the 11-bit domain the block stores: 0 to 2047, or −1023 to 1023 when signed.
        Span<int> target = stackalloc int[16];
        Span<int> wide = stackalloc int[16];
        int min = int.MaxValue, max = int.MinValue;
        for (int i = 0; i < 16; i++)
        {
            wide[i] = values[i * stride];
            target[i] = signed ? (int)Math.Round((wide[i] * 2046.0 / 65535.0) - 1023) : (int)Math.Round(wide[i] * 2047.0 / 65535.0);
            min = Math.Min(min, target[i]);
            max = Math.Max(max, target[i]);
        }

        (int multiplierSpread, int keep, int baseSpread) = EacSearch(effort);
        long bestError = Impossible;
        int bestBase = 0, bestMultiplier = 0, bestTable = 0;
        int baseLow = signed ? -127 : 0, baseHigh = signed ? 127 : 255;
        Vector128<int> w0 = Vector128.Create(wide[0], wide[1], wide[2], wide[3]), w1 = Vector128.Create(wide[4], wide[5], wide[6], wide[7]);
        Vector128<int> w2 = Vector128.Create(wide[8], wide[9], wide[10], wide[11]), w3 = Vector128.Create(wide[12], wide[13], wide[14], wide[15]);
        Shortlist shortlist = new(stackalloc long[Shortlist.Max], stackalloc int[Shortlist.Max], keep);
        for (int t = 0; t < 16; t++)
        {
            ReadOnlySpan<sbyte> table = Etc.EacTables.Slice(t * 8, 8);
            int low = table[3], high = table[7];
            double spread = max - min;
            int guess = (int)Math.Round(spread / (8.0 * (high - low)));
            for (int m = Math.Max(0, guess - multiplierSpread); m <= Math.Min(15, guess + multiplierSpread); m++)
            {
                int scale = m == 0 ? 1 : m * 8;
                double center = ((min + max) / 2.0) - ((low + high) * scale / 2.0);
                int b = Math.Clamp((int)Math.Round(signed ? center / 8.0 : (center - 4) / 8.0), baseLow, baseHigh);
                long error = Eac11Error(w0, w1, w2, w3, b, table, scale, signed);
                if (error < bestError)
                {
                    (bestError, bestBase, bestMultiplier, bestTable) = (error, b, m, t);
                }

                shortlist.Add(error, ((b + 128) << 8) | (t << 4) | m);
            }
        }

        // Around the base values of the best table and multiplier pairs.
        for (int i = 0; i < shortlist.Count; i++)
        {
            int key = shortlist.Key(i), center = (key >> 8) - 128, t = (key >> 4) & 15, m = key & 15;
            ReadOnlySpan<sbyte> table = Etc.EacTables.Slice(t * 8, 8);
            int scale = m == 0 ? 1 : m * 8;
            for (int b = Math.Max(baseLow, center - baseSpread); b <= Math.Min(baseHigh, center + baseSpread); b++)
            {
                long error = b == center ? Impossible : Eac11Error(w0, w1, w2, w3, b, table, scale, signed);
                if (error < bestError)
                {
                    (bestError, bestBase, bestMultiplier, bestTable) = (error, b, m, t);
                }
            }
        }

        ReadOnlySpan<sbyte> chosen = Etc.EacTables.Slice(bestTable * 8, 8);
        int chosenScale = bestMultiplier == 0 ? 1 : bestMultiplier * 8;
        ulong indices = 0;
        for (int k = 0; k < 16; k++)
        {
            int goal = wide[Position(k)];
            int bestIndex = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                int d = Math.Abs(Decoded(bestBase, chosen[i] * chosenScale, signed) - goal);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestIndex = i;
                }
            }

            indices |= (ulong)bestIndex << (45 - (3 * k));
        }

        BinaryPrimitives.WriteUInt64BigEndian(block, Eac((ulong)(bestBase & 0xFF), bestMultiplier, bestTable, 0) | indices);
    }

    /// <summary>
    /// The EAC search at each effort: how far the multiplier strays from its guess, how many of the best table and multiplier
    /// pairs (scored at their guessed base) then try the base values around it, and how far.
    /// </summary>
    private static (int Multipliers, int Keep, int Bases) EacSearch(int effort) => effort switch
    {
        0 => (0, 0, 0),
        1 => (1, 2, 1),
        2 => (1, 3, 2),
        3 => (1, 6, 3),
        4 => (2, 12, 3),
        5 => (3, 16, 5),
        _ => (4, 32, 8),
    };

    /// <summary>Returns the error of 16 8-bit values from the nearest of base + table × multiplier, eight at a time.</summary>
    private static long Eac8Error(Vector128<short> v0, Vector128<short> v1, int b, ReadOnlySpan<sbyte> table, int m)
    {
        // The distance to the nearest of the eight values; its square fits 16 bits.
        Vector128<short> d0 = Vector128.Create((short)Math.Clamp(b + (table[0] * m), 0, 255));
        Vector128<short> d1 = Vector128.Abs(v1 - d0);
        d0 = Vector128.Abs(v0 - d0);
        for (int i = 1; i < 8; i++)
        {
            Vector128<short> value = Vector128.Create((short)Math.Clamp(b + (table[i] * m), 0, 255));
            d0 = Vector128.Min(d0, Vector128.Abs(v0 - value));
            d1 = Vector128.Min(d1, Vector128.Abs(v1 - value));
        }

        Vector128<ushort> s0 = d0.AsUInt16() * d0.AsUInt16(), s1 = d1.AsUInt16() * d1.AsUInt16();
        return Vector128.Sum(Vector128.WidenLower(s0) + Vector128.WidenUpper(s0) + Vector128.WidenLower(s1) + Vector128.WidenUpper(s1));
    }

    /// <summary>Returns the error of 16 16-bit values from the nearest of the values an EAC R11 block decodes, four at a time.</summary>
    private static long Eac11Error(Vector128<int> w0, Vector128<int> w1, Vector128<int> w2, Vector128<int> w3, int b, ReadOnlySpan<sbyte> table, int scale, bool signed)
    {
        // The distance to the nearest of the eight values; its square fits 32 bits unsigned.
        Vector128<int> value = Vector128.Create(Decoded(b, table[0] * scale, signed));
        Vector128<int> d0 = Vector128.Abs(w0 - value), d1 = Vector128.Abs(w1 - value), d2 = Vector128.Abs(w2 - value), d3 = Vector128.Abs(w3 - value);
        for (int i = 1; i < 8; i++)
        {
            value = Vector128.Create(Decoded(b, table[i] * scale, signed));
            d0 = Vector128.Min(d0, Vector128.Abs(w0 - value));
            d1 = Vector128.Min(d1, Vector128.Abs(w1 - value));
            d2 = Vector128.Min(d2, Vector128.Abs(w2 - value));
            d3 = Vector128.Min(d3, Vector128.Abs(w3 - value));
        }

        return (long)(SquareSum(d0) + SquareSum(d1) + SquareSum(d2) + SquareSum(d3));
    }

    /// <summary>The few lowest-error keys seen, kept in order.</summary>
    private ref struct Shortlist
    {
        public const int Max = 32;
        private readonly Span<long> _errors;
        private readonly Span<int> _keys;
        private readonly int _capacity;

        public Shortlist(Span<long> errors, Span<int> keys, int capacity)
        {
            _errors = errors;
            _keys = keys;
            _capacity = capacity;
        }

        public int Count { get; private set; }

        public readonly int Key(int i) => _keys[i];

        public void Add(long error, int key)
        {
            if (_capacity == 0 || (Count == _capacity && error >= _errors[Count - 1]))
            {
                return;
            }

            int i = Count < _capacity ? Count++ : Count - 1;
            for (; i > 0 && _errors[i - 1] > error; i--)
            {
                _errors[i] = _errors[i - 1];
                _keys[i] = _keys[i - 1];
            }

            _errors[i] = error;
            _keys[i] = key;
        }
    }

    /// <summary>Returns the sum of the squares of four distances below 65536.</summary>
    private static ulong SquareSum(Vector128<int> d)
    {
        Vector128<uint> square = d.AsUInt32() * d.AsUInt32();
        return Vector128.Sum(Vector128.WidenLower(square) + Vector128.WidenUpper(square));
    }

    /// <summary>Returns the 16-bit value the decoder gives for base <paramref name="b"/> plus <paramref name="offset"/>.</summary>
    private static int Decoded(int b, int offset, bool signed)
    {
        if (signed)
        {
            int v = Math.Clamp((b * 8) + offset, -1023, 1023);
            return (((v + 1023) * 65535 * 2) + 2046) / 4092;
        }

        int u = Math.Clamp((b * 8) + 4 + offset, 0, 2047);
        return (u << 5) | (u >> 6);
    }

    private static ulong Eac(ulong b, int multiplier, int table, int index) => (b << 56) | ((ulong)multiplier << 52) | ((ulong)table << 48)
        | (index == 0 ? 0 : Repeat(index));

    private static ulong Repeat(int index)
    {
        ulong bits = 0;
        for (int k = 0; k < 16; k++)
        {
            bits |= (ulong)index << (45 - (3 * k));
        }

        return bits;
    }

    /// <summary>Returns the row-order position of the texel the block's column-order index <paramref name="k"/> names.</summary>
    private static int Position(int k) => ((k & 3) * 4) + (k >> 2);

    private static void Subblocks(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, int effort, bool punchThrough, bool opaque, ref Best best)
    {
        if (effort == 0 && !punchThrough)
        {
            Quick(px, clear, ref best);
            return;
        }

        Span<Candidate> first = stackalloc Candidate[Candidate.Max];
        Span<Candidate> second = stackalloc Candidate[Candidate.Max];
        for (int flip = 0; flip < 2; flip++)
        {
            Sub left = new(px, clear, flip, 0), right = new(px, clear, flip, 1);

            // Individual mode (4-bit colours), which punch-through blocks do not have.
            if (!punchThrough)
            {
                Candidate a = BestOf(first[..Search(px, clear, left, 4, effort, true, first)]);
                Candidate b = BestOf(second[..Search(px, clear, right, 4, effort, true, second)]);
                if (a.Error + b.Error < best.Error)
                {
                    a = Indexed(px, clear, left, a, 4, true);
                    b = Indexed(px, clear, right, b, 4, true);
                    ulong hi = ((ulong)(uint)a.R << 28) | ((ulong)(uint)b.R << 24) | ((ulong)(uint)a.G << 20) | ((ulong)(uint)b.G << 16) | ((ulong)(uint)a.B << 12) | ((ulong)(uint)b.B << 8)
                        | ((ulong)(uint)a.Table << 5) | ((ulong)(uint)b.Table << 2) | (ulong)(uint)flip;
                    best = new Best(a.Error + b.Error, (hi << 32) | a.Indices | b.Indices);
                }
            }

            // Differential mode (5-bit colours, the second within −4 to 3 of the first).
            int na = Search(px, clear, left, 5, effort, opaque, first);
            int nb = Search(px, clear, right, 5, effort, opaque, second);
            Candidate bestA = default, bestB = default;
            long pair = Impossible;
            for (int i = 0; i < na; i++)
            {
                for (int j = 0; j < nb; j++)
                {
                    Candidate a = first[i], b = second[j];
                    if (a.Error + b.Error < pair && Fits(b.R - a.R) && Fits(b.G - a.G) && Fits(b.B - a.B))
                    {
                        pair = a.Error + b.Error;
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            if (pair == Impossible || effort >= 2)
            {
                // The second subblock pulled within reach of the best first colours.
                Candidate b = BestOf(second[..nb]);
                for (int i = 0; i < na; i++)
                {
                    Candidate a = first[i];
                    if (a.Error >= pair)
                    {
                        continue;
                    }

                    int r = a.R + Math.Clamp(b.R - a.R, -4, 3), g = a.G + Math.Clamp(b.G - a.G, -4, 3), bl = a.B + Math.Clamp(b.B - a.B, -4, 3);
                    Candidate near = Evaluate(px, clear, right, r, g, bl, 5, opaque);
                    if (a.Error + near.Error < pair)
                    {
                        pair = a.Error + near.Error;
                        bestA = a;
                        bestB = near;
                    }
                }
            }

            if (pair < best.Error)
            {
                bestA = Indexed(px, clear, left, bestA, 5, opaque);
                bestB = Indexed(px, clear, right, bestB, 5, opaque);
                ulong hi = ((ulong)(uint)bestA.R << 27) | ((ulong)(uint)((bestB.R - bestA.R) & 7) << 24) | ((ulong)(uint)bestA.G << 19) | ((ulong)(uint)((bestB.G - bestA.G) & 7) << 16)
                    | ((ulong)(uint)bestA.B << 11) | ((ulong)(uint)((bestB.B - bestA.B) & 7) << 8) | ((ulong)(uint)bestA.Table << 5) | ((ulong)(uint)bestB.Table << 2)
                    | ((punchThrough ? (opaque ? 1UL : 0) : 1UL) << 1) | (ulong)(uint)flip;
                best = new Best(pair, (hi << 32) | bestA.Indices | bestB.Indices);
            }
        }
    }

    /// <summary>
    /// Effort 0: codes the subblock means of both orientations, differentially when they are near enough, else both
    /// individually and differentially with the second pulled within reach of the first, keeping the best; each subblock
    /// tries only the tables near the one its texels' spread suggests.
    /// </summary>
    private static void Quick(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, ref Best best)
    {
        Sub a0 = new(px, clear, 0, 0), b0 = new(px, clear, 0, 1), a1 = new(px, clear, 1, 0), b1 = new(px, clear, 1, 1);
        for (int flip = 0; flip < 2; flip++)
        {
            Sub left = flip == 0 ? a0 : a1, right = flip == 0 ? b0 : b1;
            int lr = Mean5(left.SumR), lg = Mean5(left.SumG), lb = Mean5(left.SumB);
            int rr = Mean5(right.SumR), rg = Mean5(right.SumG), rb = Mean5(right.SumB);
            bool fits = Fits(rr - lr) && Fits(rg - lg) && Fits(rb - lb);
            Candidate a = Evaluate(px, clear, left, lr, lg, lb, 5, true, true);
            Candidate b = Evaluate(px, clear, right, lr + Math.Clamp(rr - lr, -4, 3), lg + Math.Clamp(rg - lg, -4, 3), lb + Math.Clamp(rb - lb, -4, 3), 5, true, true);
            if (a.Error + b.Error < best.Error)
            {
                a = Indexed(px, clear, left, a, 5, true);
                b = Indexed(px, clear, right, b, 5, true);
                ulong hi = ((ulong)(uint)a.R << 27) | ((ulong)(uint)((b.R - a.R) & 7) << 24) | ((ulong)(uint)a.G << 19) | ((ulong)(uint)((b.G - a.G) & 7) << 16)
                    | ((ulong)(uint)a.B << 11) | ((ulong)(uint)((b.B - a.B) & 7) << 8) | 2UL
                    | ((ulong)(uint)a.Table << 5) | ((ulong)(uint)b.Table << 2) | (ulong)(uint)flip;
                best = new Best(a.Error + b.Error, (hi << 32) | a.Indices | b.Indices);
            }

            if (fits)
            {
                continue;
            }

            a = Evaluate(px, clear, left, Mean4(left.SumR), Mean4(left.SumG), Mean4(left.SumB), 4, true, true);
            b = Evaluate(px, clear, right, Mean4(right.SumR), Mean4(right.SumG), Mean4(right.SumB), 4, true, true);
            if (a.Error + b.Error < best.Error)
            {
                a = Indexed(px, clear, left, a, 4, true);
                b = Indexed(px, clear, right, b, 4, true);
                ulong hi = ((ulong)(uint)a.R << 28) | ((ulong)(uint)b.R << 24) | ((ulong)(uint)a.G << 20) | ((ulong)(uint)b.G << 16) | ((ulong)(uint)a.B << 12) | ((ulong)(uint)b.B << 8)
                    | ((ulong)(uint)a.Table << 5) | ((ulong)(uint)b.Table << 2) | (ulong)(uint)flip;
                best = new Best(a.Error + b.Error, (hi << 32) | a.Indices | b.Indices);
            }
        }
    }

    /// <summary>How many tables either side of the guessed one a narrow evaluation tries.</summary>
    private const int NarrowReach = 2;

    /// <summary>The 4-bit and 5-bit colour nearest each mean of eight values, by the eight values' sum.</summary>
    private static readonly byte[] MeanLevels = BuildMeanLevels();

    private static byte[] BuildMeanLevels()
    {
        byte[] table = new byte[2 * 2041];
        for (int sum = 0; sum <= 2040; sum++)
        {
            table[sum] = (byte)Quantize(sum / 8.0, 4);
            table[2041 + sum] = (byte)Quantize(sum / 8.0, 5);
        }

        return table;
    }

    private static int Mean4(int sum) => MeanLevels[sum];

    private static int Mean5(int sum) => MeanLevels[2041 + sum];

    private static bool Fits(int delta) => delta is >= -4 and <= 3;

    private static Candidate BestOf(ReadOnlySpan<Candidate> candidates)
    {
        Candidate best = candidates[0];
        foreach (Candidate c in candidates)
        {
            if (c.Error < best.Error)
            {
                best = c;
            }
        }

        return best;
    }

    /// <summary>The row-order positions of the eight texels of each subblock: left and right (flip 0), then top and bottom.</summary>
    private static ReadOnlySpan<byte> SubPositions => [0, 1, 4, 5, 8, 9, 12, 13, 2, 3, 6, 7, 10, 11, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>
    /// Searches the base colours of one subblock at <paramref name="bits"/> bits a channel; fills <paramref name="output"/>
    /// with distinct candidates and their best tables (without indices), and returns how many.
    /// </summary>
    private static int Search(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, in Sub s, int bits, int effort, bool opaque, Span<Candidate> output)
    {
        int count = 0;
        int n = s.N;
        if (n == 0)
        {
            output[count++] = Evaluate(px, clear, s, 0, 0, 0, bits, opaque);
            return count;
        }

        double ar = s.SumR / (double)n, ag = s.SumG / (double)n, ab = s.SumB / (double)n;
        Candidate start = Evaluate(px, clear, s, Quantize(ar, bits), Quantize(ag, bits), Quantize(ab, bits), bits, opaque);
        Add(output, ref count, start);
        int refits = effort switch { <= 1 => 0, <= 3 => 1, _ => 2 };
        Candidate current = start;
        for (int i = 0; i < refits; i++)
        {
            // The base colour that best fits the chosen modifiers: the mean of each pixel less its modifier.
            int modifier = Refit(px, clear, s, current, bits, opaque);
            int r = Quantize(ar - (modifier / (double)n), bits), g = Quantize(ag - (modifier / (double)n), bits), b = Quantize(ab - (modifier / (double)n), bits);
            if (r == current.R && g == current.G && b == current.B)
            {
                break;
            }

            Candidate refit = Evaluate(px, clear, s, r, g, b, bits, opaque);
            Add(output, ref count, refit);
            if (refit.Error >= current.Error)
            {
                break;
            }

            current = refit;
        }

        if (effort >= 4)
        {
            Candidate center = BestOf(output[..count]);
            int reach = effort >= 6 ? 2 : 1;
            int max = (1 << bits) - 1;
            for (int dr = -reach; dr <= reach; dr++)
            {
                for (int dg = -reach; dg <= reach; dg++)
                {
                    for (int db = -reach; db <= reach; db++)
                    {
                        bool axis = (dr == 0 ? 1 : 0) + (dg == 0 ? 1 : 0) + (db == 0 ? 1 : 0) >= 2 || (dr == dg && dg == db);
                        if ((effort < 5 && !axis) || (dr == 0 && dg == 0 && db == 0))
                        {
                            continue;
                        }

                        int r = center.R + dr, g = center.G + dg, b = center.B + db;
                        if ((uint)r <= max && (uint)g <= max && (uint)b <= max && !Contains(output[..count], r, g, b))
                        {
                            Add(output, ref count, Evaluate(px, clear, s, r, g, b, bits, opaque));
                        }
                    }
                }
            }
        }

        return count;
    }

    private static bool Contains(ReadOnlySpan<Candidate> candidates, int r, int g, int b)
    {
        foreach (Candidate c in candidates)
        {
            if (c.R == r && c.G == g && c.B == b)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the sum of the modifiers <paramref name="c"/>'s table and best indices give the opaque members.</summary>
    private static int Refit(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, in Sub s, Candidate c, int bits, bool opaque)
    {
        uint indices = Indexed(px, clear, s, c, bits, opaque).Indices;
        int small = Etc.Modifiers[c.Table * 2], large = Etc.Modifiers[(c.Table * 2) + 1];
        int sum = 0;
        foreach (int p in SubPositions.Slice(s.Offset, 8))
        {
            if (clear[p])
            {
                continue;
            }

            int k = ((p & 3) * 4) + (p >> 2);
            int index = (int)(((indices >> (16 + k)) & 1) << 1 | ((indices >> k) & 1));
            sum += index switch { 0 => opaque ? small : 0, 1 => large, 2 => -small, _ => -large };
        }

        return sum;
    }

    private static void Add(Span<Candidate> output, ref int count, Candidate c)
    {
        if (!Contains(output[..count], c.R, c.G, c.B) && count < output.Length)
        {
            output[count++] = c;
        }
    }

    private static int Quantize(double v, int bits)
    {
        int max = (1 << bits) - 1;
        int q = (int)Math.Round(Math.Clamp(v, 0, 255) * max / 255.0);
        int e = Expand(q, bits);

        // The rounding above is in the wrong space by up to one level; take the neighbour whose expansion is nearer.
        if (q < max && Math.Abs(Expand(q + 1, bits) - v) < Math.Abs(e - v))
        {
            q++;
        }
        else if (q > 0 && Math.Abs(Expand(q - 1, bits) - v) < Math.Abs(e - v))
        {
            q--;
        }

        return q;
    }

    private static int Expand(int q, int bits) => bits switch
    {
        4 => Etc.Extend4(q),
        5 => Etc.Extend5(q),
        6 => Etc.Extend6(q),
        _ => Etc.Extend7(q),
    };

    /// <summary>
    /// Scores base colour (r, g, b) at <paramref name="bits"/> bits for one subblock with the best table. Opaque subblocks are
    /// scored four texels at a time; while no modifier of a table clamps, a texel's error with modifier m is its error with
    /// the base alone plus 3m² − 2m·d, where d sums its differences from the base, so the best modifier needs no colours.
    /// </summary>
    private static Candidate Evaluate(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, in Sub s, int r, int g, int b, int bits, bool opaque, bool narrow = false)
    {
        int r8 = Expand(r, bits), g8 = Expand(g, bits), b8 = Expand(b, bits);
        long bestError = Impossible;
        int bestTable = 0;
        if (!opaque)
        {
            for (int t = 0; t < 8; t++)
            {
                long error = Assign(px, clear, s.Offset, r8, g8, b8, t, opaque, bestError, out _);
                if (error < bestError)
                {
                    bestError = error;
                    bestTable = t;
                }
            }

            return new Candidate(r, g, b, bestTable, bestError, 0);
        }

        Vector128<int> vr = Vector128.Create(r8), vg = Vector128.Create(g8), vb = Vector128.Create(b8);
        Vector128<int> dr0 = s.R0 - vr, dg0 = s.G0 - vg, db0 = s.B0 - vb, dr1 = s.R1 - vr, dg1 = s.G1 - vg, db1 = s.B1 - vb;
        long baseError = Vector128.Sum((dr0 * dr0) + (dg0 * dg0) + (db0 * db0) + (dr1 * dr1) + (dg1 * dg1) + (db1 * db1));
        Vector128<int> a0 = Vector128.Abs(dr0 + dg0 + db0), a1 = Vector128.Abs(dr1 + dg1 + db1);
        a0 += a0;
        a1 += a1;
        int lo = Math.Min(r8, Math.Min(g8, b8)), hi = Math.Max(r8, Math.Max(g8, b8));
        int tFirst = 0, tLast = 7;
        if (narrow)
        {
            // The table whose middle modifier is nearest the mean grey offset a third of the texels' mean |d|, and its neighbours.
            int sum = Vector128.Sum(a0 + a1), guess = 0, gap = int.MaxValue;
            for (int t = 0; t < 8; t++)
            {
                int miss = Math.Abs(((Etc.Modifiers[t * 2] + Etc.Modifiers[(t * 2) + 1]) * 24) - sum);
                if (miss < gap)
                {
                    (gap, guess) = (miss, t);
                }
            }

            tFirst = Math.Max(0, guess - NarrowReach);
            tLast = Math.Min(7, guess + NarrowReach);
        }

        for (int t = tFirst; t <= tLast; t++)
        {
            int small = Etc.Modifiers[t * 2], large = Etc.Modifiers[(t * 2) + 1];
            long error;
            if (lo >= large && hi + large <= 255)
            {
                // 2|d| above 3 (small + large) picks the large modifier; the sign of d picks the sign.
                Vector128<int> threshold = Vector128.Create(3 * (small + large));
                Vector128<int> cs = Vector128.Create(3 * small * small), cl = Vector128.Create(3 * large * large);
                Vector128<int> e0 = Vector128.ConditionalSelect(Vector128.GreaterThan(a0, threshold), cl - (a0 * large), cs - (a0 * small));
                Vector128<int> e1 = Vector128.ConditionalSelect(Vector128.GreaterThan(a1, threshold), cl - (a1 * large), cs - (a1 * small));
                error = baseError + Vector128.Sum(e0 + e1);
            }
            else
            {
                error = Vector128.Sum(Nearest(s.R0, s.G0, s.B0, r8, g8, b8, small, large) + Nearest(s.R1, s.G1, s.B1, r8, g8, b8, small, large));
            }

            if (error < bestError)
            {
                bestError = error;
                bestTable = t;
            }
        }

        return new Candidate(r, g, b, bestTable, bestError, 0);
    }

    /// <summary>Returns each texel's error from the nearest of the four colours base ± small and base ± large, clamped.</summary>
    private static Vector128<int> Nearest(Vector128<int> r, Vector128<int> g, Vector128<int> b, int r8, int g8, int b8, int small, int large)
    {
        Vector128<int> d = Error(r, g, b, r8 + small, g8 + small, b8 + small);
        d = Vector128.Min(d, Error(r, g, b, r8 + large, g8 + large, b8 + large));
        d = Vector128.Min(d, Error(r, g, b, r8 - small, g8 - small, b8 - small));
        return Vector128.Min(d, Error(r, g, b, r8 - large, g8 - large, b8 - large));
    }

    private static Vector128<int> Error(Vector128<int> r, Vector128<int> g, Vector128<int> b, int cr, int cg, int cb)
    {
        Vector128<int> dr = r - Vector128.Create(Clamp(cr)), dg = g - Vector128.Create(Clamp(cg)), db = b - Vector128.Create(Clamp(cb));
        return (dr * dr) + (dg * dg) + (db * db);
    }

    /// <summary>Returns <paramref name="c"/> with the indices its table gives each texel.</summary>
    private static Candidate Indexed(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, in Sub s, Candidate c, int bits, bool opaque)
    {
        if (opaque)
        {
            int r8 = Expand(c.R, bits), g8 = Expand(c.G, bits), b8 = Expand(c.B, bits);
            int small = Etc.Modifiers[c.Table * 2], large = Etc.Modifiers[(c.Table * 2) + 1];
            uint low = Choose(s.R0, s.G0, s.B0, r8, g8, b8, small, large), high = Choose(s.R1, s.G1, s.B1, r8, g8, b8, small, large);
            ReadOnlySpan<uint> spread = IndexSpread.AsSpan(s.Offset * 64, 512);
            uint word = spread[(int)(low & 15)] | spread[16 + (int)(low >> 4)] | (spread[256 + (int)(high & 15)] | spread[272 + (int)(high >> 4)]);
            return c with { Indices = word };
        }

        Assign(px, clear, s.Offset, Expand(c.R, bits), Expand(c.G, bits), Expand(c.B, bits), c.Table, opaque, Impossible, out uint indices);
        return c with { Indices = indices };
    }

    /// <summary>
    /// Picks the nearest of base + small, + large, − small and − large (indices 0 to 3, the first on a tie) for four texels;
    /// returns the indices' low bits in bits 0 to 3 and their high bits in bits 4 to 7.
    /// </summary>
    private static uint Choose(Vector128<int> r, Vector128<int> g, Vector128<int> b, int r8, int g8, int b8, int small, int large)
    {
        Vector128<int> best = Error(r, g, b, r8 + small, g8 + small, b8 + small);
        Vector128<int> index = Vector128<int>.Zero;
        Vector128<int> e = Error(r, g, b, r8 + large, g8 + large, b8 + large);
        Vector128<int> less = Vector128.LessThan(e, best);
        best = Vector128.Min(best, e);
        index = Vector128.ConditionalSelect(less, Vector128.Create(1), index);
        e = Error(r, g, b, r8 - small, g8 - small, b8 - small);
        less = Vector128.LessThan(e, best);
        best = Vector128.Min(best, e);
        index = Vector128.ConditionalSelect(less, Vector128.Create(2), index);
        e = Error(r, g, b, r8 - large, g8 - large, b8 - large);
        index = Vector128.ConditionalSelect(Vector128.LessThan(e, best), Vector128.Create(3), index);
        return Vector128.ExtractMostSignificantBits(index << 31) | (Vector128.ExtractMostSignificantBits(index << 30) << 4);
    }

    /// <summary>
    /// For each subblock (by its offset in <see cref="SubPositions"/>), half and 4-bit mask of low or high index bits, the
    /// bits of the block's index word: 512 entries a subblock, low bits of the first half, high bits, then the second half.
    /// </summary>
    private static readonly uint[] IndexSpread = BuildIndexSpread();

    private static uint[] BuildIndexSpread()
    {
        uint[] table = new uint[4 * 512];
        for (int sub = 0; sub < 4; sub++)
        {
            for (int half = 0; half < 2; half++)
            {
                for (int mask = 0; mask < 16; mask++)
                {
                    uint lowBits = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        if ((mask >> j & 1) != 0)
                        {
                            int p = SubPositions[(sub * 8) + (half * 4) + j];
                            lowBits |= 1u << (((p & 3) * 4) + (p >> 2));
                        }
                    }

                    table[(sub * 512) + (half * 256) + mask] = lowBits;
                    table[(sub * 512) + (half * 256) + 16 + mask] = lowBits << 16;
                }
            }
        }

        return table;
    }

    /// <summary>
    /// Picks each texel's best modifier of <paramref name="table"/> around base (r8, g8, b8) the way the decoder clamps;
    /// returns the error, or stops once it reaches <paramref name="limit"/>.
    /// </summary>
    private static long Assign(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, int offset, int r8, int g8, int b8, int table, bool opaque, long limit, out uint indices)
    {
        int small = Etc.Modifiers[table * 2], large = Etc.Modifiers[(table * 2) + 1];
        long error = 0;
        indices = 0;
        foreach (int p in SubPositions.Slice(offset, 8))
        {
            int k = ((p & 3) * 4) + (p >> 2);
            if (clear[p])
            {
                if (opaque)
                {
                    return Impossible;
                }

                indices |= 1u << (16 + k);
                continue;
            }

            int pr = px[p * 3], pg = px[(p * 3) + 1], pb = px[(p * 3) + 2];
            int bestIndex = 0;
            int bestDistance = int.MaxValue;
            for (int index = 0; index < 4; index++)
            {
                if (!opaque && index == 2)
                {
                    continue;
                }

                int m = index switch { 0 => opaque ? small : 0, 1 => large, 2 => -small, _ => -large };
                int d = Distance(Clamp(r8 + m) - pr, Clamp(g8 + m) - pg, Clamp(b8 + m) - pb);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestIndex = index;
                }
            }

            error += bestDistance;
            if (error >= limit)
            {
                return Impossible;
            }

            indices |= ((uint)(bestIndex >> 1) << (16 + k)) | ((uint)(bestIndex & 1) << k);
        }

        return error;
    }

    private static int Distance(int dr, int dg, int db) => (dr * dr) + (dg * dg) + (db * db);

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);

    /// <summary>The eight texels of one subblock as two vectors of four a channel, with the sums of its opaque texels.</summary>
    private readonly struct Sub
    {
        public readonly Vector128<int> R0, R1, G0, G1, B0, B1;
        public readonly int Offset, SumR, SumG, SumB, N;

        public Sub(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, int flip, int sub)
        {
            Offset = (flip * 16) + (sub * 8);
            ReadOnlySpan<byte> at = SubPositions.Slice(Offset, 8);
            R0 = Vector128.Create(px[at[0] * 3], px[at[1] * 3], px[at[2] * 3], px[at[3] * 3]);
            R1 = Vector128.Create(px[at[4] * 3], px[at[5] * 3], px[at[6] * 3], px[at[7] * 3]);
            G0 = Vector128.Create(px[(at[0] * 3) + 1], px[(at[1] * 3) + 1], px[(at[2] * 3) + 1], px[(at[3] * 3) + 1]);
            G1 = Vector128.Create(px[(at[4] * 3) + 1], px[(at[5] * 3) + 1], px[(at[6] * 3) + 1], px[(at[7] * 3) + 1]);
            B0 = Vector128.Create(px[(at[0] * 3) + 2], px[(at[1] * 3) + 2], px[(at[2] * 3) + 2], px[(at[3] * 3) + 2]);
            B1 = Vector128.Create(px[(at[4] * 3) + 2], px[(at[5] * 3) + 2], px[(at[6] * 3) + 2], px[(at[7] * 3) + 2]);
            foreach (int p in at)
            {
                if (!clear[p])
                {
                    SumR += px[p * 3];
                    SumG += px[(p * 3) + 1];
                    SumB += px[(p * 3) + 2];
                    N++;
                }
            }
        }
    }

    /// <summary>
    /// Tries the T and H modes, which paint the block with colours around two base colours. For opaque blocks each candidate
    /// colour's error per texel, alone and with every distance either side, is worked out once, so each pairing costs only
    /// a few minimums.
    /// </summary>
    private static void TwoColors(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, in Rows rows, int effort, bool punchThrough, bool opaque, ref Best best)
    {
        Span<int> group = stackalloc int[16];
        if (!Split(px, clear, group, out double r0, out double g0, out double b0, out double r1, out double g1, out double b1))
        {
            return;
        }

        // The candidate colours: those near the first cluster, then those near the second.
        Span<int> colors = stackalloc int[3 * 54];
        int na = Neighbours(r0, g0, b0, effort, colors);
        int nb = Neighbours(r1, g1, b1, effort, colors[(3 * na)..]);
        Span<int> paint = stackalloc int[12];

        // Per colour: the error of each row's texels from the colour alone (slot 0), then from the nearer of the colour plus
        // and minus each distance (slots 1 to 8).
        Span<Vector128<int>> errors = opaque ? stackalloc Vector128<int>[(na + nb) * 36] : default;
        for (int x = 0; opaque && x < na + nb; x++)
        {
            int r8 = Etc.Extend4(colors[x * 3]), g8 = Etc.Extend4(colors[(x * 3) + 1]), b8 = Etc.Extend4(colors[(x * 3) + 2]);
            Span<Vector128<int>> mine = errors.Slice(x * 36, 36);
            for (int q = 0; q < 4; q++)
            {
                mine[q] = Error(rows.R(q), rows.G(q), rows.B(q), r8, g8, b8);
                for (int d = 0; d < 8; d++)
                {
                    mine[4 + (d * 4) + q] = Pair(rows, q, r8, g8, b8, Etc.Distances[d]);
                }
            }
        }

        // The best T or H block found: its mode (0 none, 1 T, 2 H), colours and distance; indices come at the end.
        int mode = 0, bestC = 0, bestDColor = 0, bestD = 0;
        long bestError = best.Error;
        for (int i = 0; i < na; i++)
        {
            for (int j = na; j < na + nb; j++)
            {
                for (int swap = 0; swap < 2; swap++)
                {
                    (int c, int e) = swap == 0 ? (i, j) : (j, i);
                    int cr = colors[c * 3], cg = colors[(c * 3) + 1], cb = colors[(c * 3) + 2];
                    int dr = colors[e * 3], dg = colors[(e * 3) + 1], db = colors[(e * 3) + 2];
                    bool firstHigh = ((cr << 8) | (cg << 4) | cb) >= ((dr << 8) | (dg << 4) | db);
                    ReadOnlySpan<Vector128<int>> ce = opaque ? errors.Slice(c * 36, 36) : default, de = opaque ? errors.Slice(e * 36, 36) : default;
                    Vector128<int> t0 = default, t1 = default, t2 = default, t3 = default;
                    if (opaque)
                    {
                        // The T mode's two unshifted colours, nearest of, for every distance.
                        (t0, t1, t2, t3) = (Vector128.Min(ce[0], de[0]), Vector128.Min(ce[1], de[1]), Vector128.Min(ce[2], de[2]), Vector128.Min(ce[3], de[3]));
                    }

                    for (int d = 0; d < 8; d++)
                    {
                        int at = 4 + (d * 4);

                        // T: one colour on its own, the other with the distance either side.
                        long error;
                        if (opaque)
                        {
                            error = Vector128.Sum(Vector128.Min(t0, de[at]) + Vector128.Min(t1, de[at + 1]) + Vector128.Min(t2, de[at + 2]) + Vector128.Min(t3, de[at + 3]));
                        }
                        else
                        {
                            PaintT(paint, Etc.Extend4(cr), Etc.Extend4(cg), Etc.Extend4(cb), Etc.Extend4(dr), Etc.Extend4(dg), Etc.Extend4(db), Etc.Distances[d]);
                            error = Score(px, clear, paint, opaque, bestError, out _);
                        }

                        if (error < bestError)
                        {
                            (bestError, mode, bestC, bestDColor, bestD) = (error, 1, c, e, d);
                        }

                        // H: both colours with the distance either side; the order of the colours holds the distance's low bit.
                        if (firstHigh != ((d & 1) == 1))
                        {
                            continue;
                        }

                        if (opaque)
                        {
                            error = Vector128.Sum(Vector128.Min(ce[at], de[at]) + Vector128.Min(ce[at + 1], de[at + 1]) + Vector128.Min(ce[at + 2], de[at + 2])
                                + Vector128.Min(ce[at + 3], de[at + 3]));
                        }
                        else
                        {
                            PaintH(paint, Etc.Extend4(cr), Etc.Extend4(cg), Etc.Extend4(cb), Etc.Extend4(dr), Etc.Extend4(dg), Etc.Extend4(db), Etc.Distances[d]);
                            error = Score(px, clear, paint, opaque, bestError, out _);
                        }

                        if (error < bestError)
                        {
                            (bestError, mode, bestC, bestDColor, bestD) = (error, 2, c, e, d);
                        }
                    }
                }
            }
        }

        if (mode == 0)
        {
            return;
        }

        int br1 = colors[bestC * 3], bg1 = colors[(bestC * 3) + 1], bb1 = colors[(bestC * 3) + 2];
        int br2 = colors[bestDColor * 3], bg2 = colors[(bestDColor * 3) + 1], bb2 = colors[(bestDColor * 3) + 2];
        int distanceOut = Etc.Distances[bestD];
        if (mode == 1)
        {
            PaintT(paint, Etc.Extend4(br1), Etc.Extend4(bg1), Etc.Extend4(bb1), Etc.Extend4(br2), Etc.Extend4(bg2), Etc.Extend4(bb2), distanceOut);
        }
        else
        {
            PaintH(paint, Etc.Extend4(br1), Etc.Extend4(bg1), Etc.Extend4(bb1), Etc.Extend4(br2), Etc.Extend4(bg2), Etc.Extend4(bb2), distanceOut);
        }

        Score(px, clear, paint, opaque, Impossible, out uint indices);
        bool opaqueBit = punchThrough ? opaque : true;
        best = new Best(bestError, mode == 1 ? TBits(br1, bg1, bb1, br2, bg2, bb2, bestD, opaqueBit, indices) : HBits(br1, bg1, bb1, br2, bg2, bb2, bestD, opaqueBit, indices));
    }

    /// <summary>Returns each texel of row <paramref name="q"/>'s error from the nearer of a colour plus and minus a distance.</summary>
    private static Vector128<int> Pair(in Rows rows, int q, int r8, int g8, int b8, int distance) =>
        Vector128.Min(Error(rows.R(q), rows.G(q), rows.B(q), r8 + distance, g8 + distance, b8 + distance),
            Error(rows.R(q), rows.G(q), rows.B(q), r8 - distance, g8 - distance, b8 - distance));

    private static void PaintT(Span<int> paint, int r1, int g1, int b1, int r2, int g2, int b2, int distance)
    {
        Paint(paint, 0, r1, g1, b1, 0);
        Paint(paint, 1, r2, g2, b2, distance);
        Paint(paint, 2, r2, g2, b2, 0);
        Paint(paint, 3, r2, g2, b2, -distance);
    }

    private static void PaintH(Span<int> paint, int r1, int g1, int b1, int r2, int g2, int b2, int distance)
    {
        Paint(paint, 0, r1, g1, b1, distance);
        Paint(paint, 1, r1, g1, b1, -distance);
        Paint(paint, 2, r2, g2, b2, distance);
        Paint(paint, 3, r2, g2, b2, -distance);
    }

    /// <summary>The 16 texels of a block as one vector of four a row and channel.</summary>
    private readonly struct Rows
    {
        private readonly Quad _r, _g, _b;

        public Rows(ReadOnlySpan<int> px)
        {
            for (int q = 0; q < 4; q++)
            {
                int at = q * 12;
                _r[q] = Vector128.Create(px[at], px[at + 3], px[at + 6], px[at + 9]);
                _g[q] = Vector128.Create(px[at + 1], px[at + 4], px[at + 7], px[at + 10]);
                _b[q] = Vector128.Create(px[at + 2], px[at + 5], px[at + 8], px[at + 11]);
            }
        }

        public Vector128<int> R(int q) => _r[q];

        public Vector128<int> G(int q) => _g[q];

        public Vector128<int> B(int q) => _b[q];

        public Vector128<int> Channel(int c, int q) => c == 0 ? _r[q] : c == 1 ? _g[q] : _b[q];
    }

    [InlineArray(4)]
    private struct Quad
    {
        private Vector128<int> _element;
    }

    /// <summary>Returns the 4-bit colours near a cluster's mean: the rounded one, and with effort its neighbours.</summary>
    private static int Neighbours(double r, double g, double b, int effort, Span<int> output)
    {
        int qr = Quantize(r, 4), qg = Quantize(g, 4), qb = Quantize(b, 4);
        int count = 0;
        int reach = effort >= 5 ? 1 : 0;
        for (int dr = -reach; dr <= reach; dr++)
        {
            for (int dg = -reach; dg <= reach; dg++)
            {
                for (int db = -reach; db <= reach; db++)
                {
                    if (effort < 6 && !(dr == dg && dg == db) && Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db) > 1)
                    {
                        continue;
                    }

                    int nr = qr + dr, ng = qg + dg, nb = qb + db;
                    if ((uint)nr <= 15 && (uint)ng <= 15 && (uint)nb <= 15)
                    {
                        output[count * 3] = nr;
                        output[(count * 3) + 1] = ng;
                        output[(count * 3) + 2] = nb;
                        count++;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>Splits the opaque pixels into two clusters along their principal axis, refined by two k-means steps.</summary>
    private static bool Split(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, Span<int> group, out double r0, out double g0, out double b0,
        out double r1, out double g1, out double b1)
    {
        r0 = g0 = b0 = r1 = g1 = b1 = 0;
        double mr = 0, mg = 0, mb = 0;
        int n = 0;
        for (int i = 0; i < 16; i++)
        {
            if (!clear[i])
            {
                mr += px[i * 3];
                mg += px[(i * 3) + 1];
                mb += px[(i * 3) + 2];
                n++;
            }
        }

        if (n == 0)
        {
            return false;
        }

        mr /= n;
        mg /= n;
        mb /= n;
        double crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int i = 0; i < 16; i++)
        {
            if (!clear[i])
            {
                double r = px[i * 3] - mr, g = px[(i * 3) + 1] - mg, b = px[(i * 3) + 2] - mb;
                crr += r * r;
                crg += r * g;
                crb += r * b;
                cgg += g * g;
                cgb += g * b;
                cbb += b * b;
            }
        }

        double ar = 1, ag = 1, ab = 1;
        for (int i = 0; i < 8; i++)
        {
            double nr = (crr * ar) + (crg * ag) + (crb * ab);
            double ng = (crg * ar) + (cgg * ag) + (cgb * ab);
            double nb = (crb * ar) + (cgb * ag) + (cbb * ab);
            double length = Math.Sqrt((nr * nr) + (ng * ng) + (nb * nb));
            if (length < 1e-9)
            {
                break;
            }

            ar = nr / length;
            ag = ng / length;
            ab = nb / length;
        }

        for (int i = 0; i < 16; i++)
        {
            group[i] = clear[i] ? -1 : ((px[i * 3] - mr) * ar) + ((px[(i * 3) + 1] - mg) * ag) + ((px[(i * 3) + 2] - mb) * ab) > 0 ? 1 : 0;
        }

        for (int step = 0; step < 3; step++)
        {
            double sr0 = 0, sg0 = 0, sb0 = 0, sr1 = 0, sg1 = 0, sb1 = 0;
            int n0 = 0, n1 = 0;
            for (int i = 0; i < 16; i++)
            {
                if (group[i] == 0)
                {
                    sr0 += px[i * 3];
                    sg0 += px[(i * 3) + 1];
                    sb0 += px[(i * 3) + 2];
                    n0++;
                }
                else if (group[i] == 1)
                {
                    sr1 += px[i * 3];
                    sg1 += px[(i * 3) + 1];
                    sb1 += px[(i * 3) + 2];
                    n1++;
                }
            }

            if (n0 == 0 || n1 == 0)
            {
                (r0, g0, b0) = (mr, mg, mb);
                (r1, g1, b1) = (mr, mg, mb);
                return true;
            }

            (r0, g0, b0) = (sr0 / n0, sg0 / n0, sb0 / n0);
            (r1, g1, b1) = (sr1 / n1, sg1 / n1, sb1 / n1);
            for (int i = 0; i < 16; i++)
            {
                if (group[i] >= 0)
                {
                    double d0 = Sq(px[i * 3] - r0) + Sq(px[(i * 3) + 1] - g0) + Sq(px[(i * 3) + 2] - b0);
                    double d1 = Sq(px[i * 3] - r1) + Sq(px[(i * 3) + 1] - g1) + Sq(px[(i * 3) + 2] - b1);
                    group[i] = d1 < d0 ? 1 : 0;
                }
            }
        }

        return true;
    }

    private static double Sq(double v) => v * v;

    private static void Paint(Span<int> paint, int i, int r, int g, int b, int d)
    {
        paint[i * 3] = Clamp(r + d);
        paint[(i * 3) + 1] = Clamp(g + d);
        paint[(i * 3) + 2] = Clamp(b + d);
    }

    /// <summary>Picks each pixel's nearest paint colour (index 2 is transparent when not opaque); returns the error.</summary>
    private static long Score(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, ReadOnlySpan<int> paint, bool opaque, long limit, out uint indices)
    {
        long error = 0;
        indices = 0;
        for (int p = 0; p < 16; p++)
        {
            int k = ((p & 3) * 4) + (p >> 2);
            if (clear[p])
            {
                indices |= 1u << (16 + k);
                continue;
            }

            int bestIndex = 0, bestDistance = int.MaxValue;
            for (int index = 0; index < 4; index++)
            {
                if (!opaque && index == 2)
                {
                    continue;
                }

                int d = Distance(paint[index * 3] - px[p * 3], paint[(index * 3) + 1] - px[(p * 3) + 1], paint[(index * 3) + 2] - px[(p * 3) + 2]);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestIndex = index;
                }
            }

            error += bestDistance;
            if (error >= limit)
            {
                return Impossible;
            }

            indices |= ((uint)(bestIndex >> 1) << (16 + k)) | ((uint)(bestIndex & 1) << k);
        }

        return error;
    }

    private static ulong TBits(int r1, int g1, int b1, int r2, int g2, int b2, int d, bool opaque, uint indices)
    {
        int ra = r1 >> 2, rb = r1 & 3;
        uint hi = ((uint)ra << 27) | ((uint)rb << 24) | ((uint)g1 << 20) | ((uint)b1 << 16) | ((uint)r2 << 12) | ((uint)g2 << 8) | ((uint)b2 << 4)
            | ((uint)(d >> 1) << 2) | ((opaque ? 1u : 0) << 1) | (uint)(d & 1);

        // Make the differential red overflow: below zero when it can, else above 31.
        hi |= ra + rb < 4 ? 1u << 26 : 7u << 29;
        return ((ulong)hi << 32) | indices;
    }

    private static ulong HBits(int r1, int g1, int b1, int r2, int g2, int b2, int d, bool opaque, uint indices)
    {
        uint hi = ((uint)r1 << 27) | ((uint)(g1 >> 1) << 24) | ((uint)(g1 & 1) << 20) | ((uint)(b1 >> 3) << 19) | ((uint)(b1 & 7) << 15)
            | ((uint)r2 << 11) | ((uint)g2 << 7) | ((uint)b2 << 3) | ((uint)(d >> 2) << 2) | ((opaque ? 1u : 0) << 1) | (uint)((d >> 1) & 1);

        // Keep the differential red in range and make green overflow.
        int dr = SignExtend3(g1 >> 1);
        if (r1 + dr < 0)
        {
            hi |= 1u << 31;
        }

        int gBase = ((g1 & 1) << 1) | (b1 >> 3);
        hi |= gBase + ((b1 & 7) >> 1) < 4 ? 1u << 18 : 7u << 21;
        return ((ulong)hi << 32) | indices;
    }

    private static int SignExtend3(int v) => (v << 29) >> 29;

    /// <summary>Tries the planar mode: a least-squares gradient from three corner colours.</summary>
    private static void Planar(ReadOnlySpan<int> px, in Rows rows, int effort, ref Best best)
    {
        Span<int> o = stackalloc int[3];
        Span<int> h = stackalloc int[3];
        Span<int> v = stackalloc int[3];
        long total = 0;
        for (int c = 0; c < 3; c++)
        {
            // Least squares for C(x, y) = O + x (H - O) / 4 + y (V - O) / 4, whose normal equations are the same for every block.
            double so = 0, sh = 0, sv = 0;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    double p = px[(((y * 4) + x) * 3) + c];
                    so += p * (1 - (x / 4.0) - (y / 4.0));
                    sh += p * (x / 4.0);
                    sv += p * (y / 4.0);
                }
            }

            double fo = (PlanarInverse[0] * so) + (PlanarInverse[1] * sh) + (PlanarInverse[2] * sv);
            double fh = (PlanarInverse[3] * so) + (PlanarInverse[4] * sh) + (PlanarInverse[5] * sv);
            double fv = (PlanarInverse[6] * so) + (PlanarInverse[7] * sh) + (PlanarInverse[8] * sv);
            int bits = c == 1 ? 7 : 6;
            int qo = Quantize(fo, bits), qh = Quantize(fh, bits), qv = Quantize(fv, bits);
            long channelBest = PlanarError(rows, c, Expand(qo, bits), Expand(qh, bits), Expand(qv, bits));
            if (effort >= 4)
            {
                int max = (1 << bits) - 1;
                int bo = qo, bh = qh, bv = qv;
                for (int dO = -1; dO <= 1; dO++)
                {
                    for (int dH = -1; dH <= 1; dH++)
                    {
                        for (int dV = -1; dV <= 1; dV++)
                        {
                            int to = bo + dO, th = bh + dH, tv = bv + dV;
                            if ((uint)to > max || (uint)th > max || (uint)tv > max || (dO | dH | dV) == 0)
                            {
                                continue;
                            }

                            long e = PlanarError(rows, c, Expand(to, bits), Expand(th, bits), Expand(tv, bits));
                            if (e < channelBest)
                            {
                                channelBest = e;
                                (qo, qh, qv) = (to, th, tv);
                            }
                        }
                    }
                }
            }

            o[c] = qo;
            h[c] = qh;
            v[c] = qv;
            total += channelBest;
            if (total >= best.Error)
            {
                return;
            }
        }

        ulong bitsOut = ((ulong)(uint)o[0] << 57) | ((ulong)(uint)(o[1] >> 6) << 56) | ((ulong)(uint)(o[1] & 63) << 49) | ((ulong)(uint)(o[2] >> 5) << 48)
            | ((ulong)(uint)((o[2] >> 3) & 3) << 43) | ((ulong)(uint)(o[2] & 7) << 39) | ((ulong)(uint)(h[0] >> 1) << 34) | ((ulong)(uint)(h[0] & 1) << 32)
            | ((ulong)(uint)h[1] << 25) | ((ulong)(uint)h[2] << 19) | ((ulong)(uint)v[0] << 13) | ((ulong)(uint)v[1] << 6) | (ulong)(uint)v[2] | (1UL << 33);

        // Keep the differential red and green in range and make blue overflow.
        if ((o[0] >> 2) + SignExtend3(((o[0] & 3) << 1) | (o[1] >> 6)) < 0)
        {
            bitsOut |= 1UL << 63;
        }

        int g6 = o[1] & 63;
        if ((g6 >> 2) + SignExtend3(((g6 & 3) << 1) | (o[2] >> 5)) < 0)
        {
            bitsOut |= 1UL << 55;
        }

        bitsOut |= ((o[2] >> 3) & 3) + ((o[2] & 7) >> 1) < 4 ? 1UL << 42 : 7UL << 45;
        best = new Best(total, bitsOut);
    }

    /// <summary>Returns one channel's error for the planar gradient (o, h, v), four texels of a row at a time.</summary>
    private static long PlanarError(in Rows rows, int c, int o, int h, int v)
    {
        Vector128<int> step = Vector128.Create(0, 1, 2, 3) * (h - o);
        Vector128<int> sum = Vector128<int>.Zero;
        for (int y = 0; y < 4; y++)
        {
            Vector128<int> value = Vector128.ShiftRightArithmetic(step + Vector128.Create((y * (v - o)) + (4 * o) + 2), 2);
            Vector128<int> d = Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), Vector128.Create(255)) - rows.Channel(c, y);
            sum += d * d;
        }

        return Vector128.Sum(sum);
    }

    /// <summary>The inverse of the planar mode's normal-equation matrix, row by row.</summary>
    private static readonly double[] PlanarInverse = BuildPlanarInverse();

    private static double[] BuildPlanarInverse()
    {
        double[] m = new double[9];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                double[] f = [1 - (x / 4.0) - (y / 4.0), x / 4.0, y / 4.0];
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        m[(i * 3) + j] += f[i] * f[j];
                    }
                }
            }
        }

        double det = (m[0] * ((m[4] * m[8]) - (m[5] * m[7]))) - (m[1] * ((m[3] * m[8]) - (m[5] * m[6]))) + (m[2] * ((m[3] * m[7]) - (m[4] * m[6])));
        return
        [
            ((m[4] * m[8]) - (m[5] * m[7])) / det, ((m[2] * m[7]) - (m[1] * m[8])) / det, ((m[1] * m[5]) - (m[2] * m[4])) / det,
            ((m[5] * m[6]) - (m[3] * m[8])) / det, ((m[0] * m[8]) - (m[2] * m[6])) / det, ((m[2] * m[3]) - (m[0] * m[5])) / det,
            ((m[3] * m[7]) - (m[4] * m[6])) / det, ((m[1] * m[6]) - (m[0] * m[7])) / det, ((m[0] * m[4]) - (m[1] * m[3])) / det,
        ];
    }

    private readonly record struct Best(long Error, ulong Bits);

    private readonly record struct Candidate(int R, int G, int B, int Table, long Error, uint Indices)
    {
        public const int Max = 64;
    }
}
