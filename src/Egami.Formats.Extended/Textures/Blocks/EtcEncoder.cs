using System.Buffers.Binary;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Encodes ETC1, ETC2 and EAC blocks at effort 0 to 6. Colour blocks try both subblock orientations in the individual and
/// differential modes, fitting each subblock's base colour by least squares on the chosen modifiers; from effort 3 the ETC2
/// encoder also tries the T and H modes (two colour clusters) and the planar mode (a least-squares gradient), and higher efforts
/// search neighbouring base colours. EAC blocks search the base value, multiplier and modifier table. Every candidate is
/// scored with the decoder's own arithmetic, so the error measured is the error a reader sees, and the work for one block
/// depends only on that block, so the output never depends on how many threads encode.
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
            TwoColors(px, clear, effort, punchThrough, opaque, ref best);
            if (opaque)
            {
                Planar(px, effort, punchThrough, ref best);
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

        int baseSpread = effort switch { 0 => 0, <= 2 => 1, <= 4 => 2, _ => 4 };
        int multiplierSpread = effort switch { 0 => 0, <= 3 => 1, _ => 2 };
        Span<int> palette = stackalloc int[8];
        for (int t = 0; t < 16; t++)
        {
            ReadOnlySpan<sbyte> table = Etc.EacTables.Slice(t * 8, 8);
            int low = table[3], high = table[7];
            int guess = Math.Max(1, (int)Math.Round((max - min) / (double)(high - low)));
            for (int m = Math.Max(1, guess - multiplierSpread); m <= Math.Min(15, guess + multiplierSpread); m++)
            {
                int center = (int)Math.Round(((min + max) / 2.0) - ((low + high) * m / 2.0));
                for (int b = Math.Max(0, center - baseSpread); b <= Math.Min(255, center + baseSpread); b++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        palette[i] = Math.Clamp(b + (table[i] * m), 0, 255);
                    }

                    long error = 0;
                    ulong indices = 0;
                    for (int k = 0; k < 16 && error < bestError; k++)
                    {
                        int target = v[Position(k)];
                        int bestIndex = 0, bestDistance = int.MaxValue;
                        for (int i = 0; i < 8; i++)
                        {
                            int d = palette[i] - target;
                            if (d * d < bestDistance)
                            {
                                bestDistance = d * d;
                                bestIndex = i;
                            }
                        }

                        error += bestDistance;
                        indices |= (ulong)bestIndex << (45 - (3 * k));
                    }

                    if (error < bestError)
                    {
                        bestError = error;
                        best = Eac((ulong)b, m, t, 0) | indices;
                    }
                }
            }
        }

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

        int baseSpread = effort switch { 0 => 0, <= 2 => 1, <= 4 => 2, _ => 3 };
        int multiplierSpread = effort switch { 0 => 0, <= 3 => 1, _ => 2 };
        ulong best = 0;
        long bestError = Impossible;
        Span<int> palette = stackalloc int[8];
        int baseLow = signed ? -127 : 0, baseHigh = signed ? 127 : 255;
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
                int baseGuess = (int)Math.Round(signed ? center / 8.0 : (center - 4) / 8.0);
                for (int b = Math.Max(baseLow, baseGuess - baseSpread); b <= Math.Min(baseHigh, baseGuess + baseSpread); b++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        palette[i] = Decoded(b, table[i] * scale, signed);
                    }

                    long error = 0;
                    ulong indices = 0;
                    for (int k = 0; k < 16 && error < bestError; k++)
                    {
                        int goal = wide[Position(k)];
                        int bestIndex = 0;
                        long bestDistance = long.MaxValue;
                        for (int i = 0; i < 8; i++)
                        {
                            long d = palette[i] - goal;
                            if (d * d < bestDistance)
                            {
                                bestDistance = d * d;
                                bestIndex = i;
                            }
                        }

                        error += bestDistance;
                        indices |= (ulong)bestIndex << (45 - (3 * k));
                    }

                    if (error < bestError)
                    {
                        bestError = error;
                        best = Eac((ulong)(b & 0xFF), m, t, 0) | indices;
                    }
                }
            }
        }

        BinaryPrimitives.WriteUInt64BigEndian(block, best);
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
        Span<int> members = stackalloc int[8];
        Span<Candidate> first = stackalloc Candidate[Candidate.Max];
        Span<Candidate> second = stackalloc Candidate[Candidate.Max];
        for (int flip = 0; flip < 2; flip++)
        {
            // Individual mode (4-bit colours), which punch-through blocks do not have.
            if (!punchThrough)
            {
                Members(flip, 0, members);
                Candidate a = BestOf(first[..Search(px, clear, members, 4, effort, true, first)]);
                Members(flip, 1, members);
                Candidate b = BestOf(second[..Search(px, clear, members, 4, effort, true, second)]);
                if (a.Error + b.Error < best.Error)
                {
                    ulong hi = ((ulong)(uint)a.R << 28) | ((ulong)(uint)b.R << 24) | ((ulong)(uint)a.G << 20) | ((ulong)(uint)b.G << 16) | ((ulong)(uint)a.B << 12) | ((ulong)(uint)b.B << 8)
                        | ((ulong)(uint)a.Table << 5) | ((ulong)(uint)b.Table << 2) | (ulong)(uint)flip;
                    best = new Best(a.Error + b.Error, (hi << 32) | a.Indices | b.Indices);
                }
            }

            // Differential mode (5-bit colours, the second within −4 to 3 of the first).
            Members(flip, 0, members);
            int na = Search(px, clear, members, 5, effort, opaque, first);
            Members(flip, 1, members);
            int nb = Search(px, clear, members, 5, effort, opaque, second);
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
                for (int i = 0; i < na; i++)
                {
                    Candidate a = first[i];
                    Candidate b = BestOf(second[..nb]);
                    int r = a.R + Math.Clamp(b.R - a.R, -4, 3), g = a.G + Math.Clamp(b.G - a.G, -4, 3), bl = a.B + Math.Clamp(b.B - a.B, -4, 3);
                    Candidate near = Evaluate(px, clear, members, r, g, bl, 5, opaque, effort);
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
                ulong hi = ((ulong)(uint)bestA.R << 27) | ((ulong)(uint)((bestB.R - bestA.R) & 7) << 24) | ((ulong)(uint)bestA.G << 19) | ((ulong)(uint)((bestB.G - bestA.G) & 7) << 16)
                    | ((ulong)(uint)bestA.B << 11) | ((ulong)(uint)((bestB.B - bestA.B) & 7) << 8) | ((ulong)(uint)bestA.Table << 5) | ((ulong)(uint)bestB.Table << 2)
                    | ((punchThrough ? (opaque ? 1UL : 0) : 1UL) << 1) | (ulong)(uint)flip;
                best = new Best(pair, (hi << 32) | bestA.Indices | bestB.Indices);
            }
        }
    }

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

    /// <summary>Gets the positions (row order) of the eight texels of one subblock.</summary>
    private static void Members(int flip, int sub, Span<int> members)
    {
        int n = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                if ((flip == 0 ? x >= 2 : y >= 2) == (sub == 1))
                {
                    members[n++] = (y * 4) + x;
                }
            }
        }
    }

    /// <summary>
    /// Searches the base colours of one subblock at <paramref name="bits"/> bits a channel; fills <paramref name="output"/>
    /// with distinct candidates and their best tables and indices, and returns how many.
    /// </summary>
    private static int Search(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, ReadOnlySpan<int> members, int bits, int effort, bool opaque, Span<Candidate> output)
    {
        int sr = 0, sg = 0, sb = 0, n = 0;
        foreach (int p in members)
        {
            if (!clear[p])
            {
                sr += px[p * 3];
                sg += px[(p * 3) + 1];
                sb += px[(p * 3) + 2];
                n++;
            }
        }

        int count = 0;
        if (n == 0)
        {
            output[count++] = Evaluate(px, clear, members, 0, 0, 0, bits, opaque, effort);
            return count;
        }

        double ar = sr / (double)n, ag = sg / (double)n, ab = sb / (double)n;
        Candidate start = Evaluate(px, clear, members, Quantize(ar, bits), Quantize(ag, bits), Quantize(ab, bits), bits, opaque, effort);
        Add(output, ref count, start);
        int refits = effort switch { <= 1 => 0, <= 3 => 1, _ => 2 };
        Candidate current = start;
        for (int i = 0; i < refits; i++)
        {
            // The base colour that best fits the chosen modifiers: the mean of each pixel less its modifier.
            int modifier = Refit(px, clear, members, current, bits, opaque);
            Candidate refit = Evaluate(px, clear, members, Quantize(ar - (modifier / (double)n), bits), Quantize(ag - (modifier / (double)n), bits),
                Quantize(ab - (modifier / (double)n), bits), bits, opaque, effort);
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
                        if ((uint)r <= max && (uint)g <= max && (uint)b <= max)
                        {
                            Add(output, ref count, Evaluate(px, clear, members, r, g, b, bits, opaque, effort));
                        }
                    }
                }
            }
        }

        return count;
    }

    /// <summary>Returns the sum of the modifiers <paramref name="c"/>'s table and indices give the opaque members.</summary>
    private static int Refit(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, ReadOnlySpan<int> members, Candidate c, int bits, bool opaque)
    {
        int small = Etc.Modifiers[c.Table * 2], large = Etc.Modifiers[(c.Table * 2) + 1];
        int sum = 0;
        foreach (int p in members)
        {
            if (clear[p])
            {
                continue;
            }

            int k = ((p & 3) * 4) + (p >> 2);
            int index = (int)(((c.Indices >> (16 + k)) & 1) << 1 | ((c.Indices >> k) & 1));
            sum += index switch { 0 => opaque ? small : 0, 1 => large, 2 => -small, _ => -large };
        }

        return sum;
    }

    private static void Add(Span<Candidate> output, ref int count, Candidate c)
    {
        for (int i = 0; i < count; i++)
        {
            if (output[i].R == c.R && output[i].G == c.G && output[i].B == c.B)
            {
                return;
            }
        }

        if (count < output.Length)
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

    /// <summary>Scores base colour (r, g, b) at <paramref name="bits"/> bits for one subblock with the best table.</summary>
    private static Candidate Evaluate(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, ReadOnlySpan<int> members, int r, int g, int b, int bits, bool opaque, int effort)
    {
        int r8 = Expand(r, bits), g8 = Expand(g, bits), b8 = Expand(b, bits);
        Candidate best = new(r, g, b, 0, Impossible, 0);
        for (int t = 0; t < 8; t++)
        {
            int small = Etc.Modifiers[t * 2], large = Etc.Modifiers[(t * 2) + 1];
            long error = 0;
            uint indices = 0;
            foreach (int p in members)
            {
                int k = ((p & 3) * 4) + (p >> 2);
                if (clear[p])
                {
                    if (opaque)
                    {
                        error = Impossible;
                        break;
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
                if (error >= best.Error)
                {
                    break;
                }

                indices |= ((uint)(bestIndex >> 1) << (16 + k)) | ((uint)(bestIndex & 1) << k);
            }

            if (error < best.Error)
            {
                best = new Candidate(r, g, b, t, error, indices);
            }
        }

        return best;
    }

    private static int Distance(int dr, int dg, int db) => (dr * dr) + (dg * dg) + (db * db);

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);

    /// <summary>Tries the T and H modes, which paint the block with colours around two base colours.</summary>
    private static void TwoColors(ReadOnlySpan<int> px, ReadOnlySpan<bool> clear, int effort, bool punchThrough, bool opaque, ref Best best)
    {
        Span<int> group = stackalloc int[16];
        if (!Split(px, clear, group, out double r0, out double g0, out double b0, out double r1, out double g1, out double b1))
        {
            return;
        }

        Span<int> first = stackalloc int[3 * 27];
        Span<int> second = stackalloc int[3 * 27];
        int na = Neighbours(r0, g0, b0, effort, first);
        int nb = Neighbours(r1, g1, b1, effort, second);
        Span<int> paint = stackalloc int[12];
        for (int i = 0; i < na; i++)
        {
            for (int j = 0; j < nb; j++)
            {
                int ar = first[i * 3], ag = first[(i * 3) + 1], ab = first[(i * 3) + 2];
                int br = second[j * 3], bg = second[(j * 3) + 1], bb = second[(j * 3) + 2];
                for (int swap = 0; swap < 2; swap++)
                {
                    (int cr, int cg, int cb, int dr, int dg, int db) = swap == 0 ? (ar, ag, ab, br, bg, bb) : (br, bg, bb, ar, ag, ab);
                    for (int d = 0; d < 8; d++)
                    {
                        int distance = Etc.Distances[d];

                        // T: one colour on its own, the other with the distance either side.
                        Paint(paint, 0, Etc.Extend4(cr), Etc.Extend4(cg), Etc.Extend4(cb), 0);
                        Paint(paint, 1, Etc.Extend4(dr), Etc.Extend4(dg), Etc.Extend4(db), distance);
                        Paint(paint, 2, Etc.Extend4(dr), Etc.Extend4(dg), Etc.Extend4(db), 0);
                        Paint(paint, 3, Etc.Extend4(dr), Etc.Extend4(dg), Etc.Extend4(db), -distance);
                        long error = Score(px, clear, paint, opaque, best.Error, out uint indices);
                        if (error < best.Error)
                        {
                            best = new Best(error, TBits(cr, cg, cb, dr, dg, db, d, punchThrough ? opaque : true, indices));
                        }

                        // H: both colours with the distance either side; the order of the colours holds the distance's low bit.
                        bool firstHigh = ((cr << 8) | (cg << 4) | cb) >= ((dr << 8) | (dg << 4) | db);
                        if (firstHigh != ((d & 1) == 1))
                        {
                            continue;
                        }

                        Paint(paint, 0, Etc.Extend4(cr), Etc.Extend4(cg), Etc.Extend4(cb), distance);
                        Paint(paint, 1, Etc.Extend4(cr), Etc.Extend4(cg), Etc.Extend4(cb), -distance);
                        Paint(paint, 2, Etc.Extend4(dr), Etc.Extend4(dg), Etc.Extend4(db), distance);
                        Paint(paint, 3, Etc.Extend4(dr), Etc.Extend4(dg), Etc.Extend4(db), -distance);
                        error = Score(px, clear, paint, opaque, best.Error, out indices);
                        if (error < best.Error)
                        {
                            best = new Best(error, HBits(cr, cg, cb, dr, dg, db, d, punchThrough ? opaque : true, indices));
                        }
                    }
                }
            }
        }
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
    private static void Planar(ReadOnlySpan<int> px, int effort, bool punchThrough, ref Best best)
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
            long channelBest = PlanarError(px, c, Expand(qo, bits), Expand(qh, bits), Expand(qv, bits));
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
                            if ((uint)to > max || (uint)th > max || (uint)tv > max)
                            {
                                continue;
                            }

                            long e = PlanarError(px, c, Expand(to, bits), Expand(th, bits), Expand(tv, bits));
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
        _ = punchThrough;
        best = new Best(total, bitsOut);
    }

    private static long PlanarError(ReadOnlySpan<int> px, int c, int o, int h, int v)
    {
        long error = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int d = Clamp(((x * (h - o)) + (y * (v - o)) + (4 * o) + 2) >> 2) - px[(((y * 4) + x) * 3) + c];
                error += d * d;
            }
        }

        return error;
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
