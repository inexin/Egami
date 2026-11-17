namespace Egami.Video.Codecs.Encoding;

/// <summary>One frame's block costs from <see cref="FrameAnalyzer"/>, kept in the lookahead for <see cref="MacroblockTree"/>.</summary>
internal sealed class BlockCosts(int count)
{
    /// <summary>Gets each block's intra error, in frame samples.</summary>
    public int[] Intra { get; } = new int[count];

    /// <summary>Gets each block's error predicted from the frame before (with its vector's charge), at most its intra error.</summary>
    public int[] Inter { get; } = new int[count];

    /// <summary>Gets each block's vector into the frame before, in half-size pixels, x then y; zero where intra is better.</summary>
    public short[] Vectors { get; } = new short[count * 2];

    /// <summary>Gets each block's detail the half-size copy averaged away, in frame samples, which every prediction leaves.</summary>
    public int[] Detail { get; } = new int[count];
}

/// <summary>
/// A macroblock tree on the lookahead's half-size analysis: walking back from the last frame held, each block passes on to the
/// frame before the share of its content that frame predicts (its intra cost and what later frames take from it, times one
/// minus its inter over its intra cost), spread over the blocks its vector overlaps by area. A frame's reuse is the mean over
/// its blocks of the base-2 logarithm of (intra cost + what propagates into it) over the intra cost: 0 when no later frame
/// predicts from it. The idea is the published macroblock-tree algorithm (temporal propagation of prediction dependence);
/// Egami turns the reuse into a step for the whole frame rather than one per macroblock, which measured better.
/// </summary>
internal static class MacroblockTree
{
    /// <summary>
    /// Fills the reuse of the first <paramref name="reuse"/>.Length frames of <paramref name="frames"/>, consecutive frames in
    /// display order each predicted from the one before; a keyframe takes nothing from the frames before it. Each frame has
    /// <paramref name="columns"/> by <paramref name="rows"/> blocks.
    /// </summary>
    public static void Reuse(IReadOnlyList<BlockCosts> frames, IReadOnlyList<bool> keys, int columns, int rows, Span<double> reuse)
    {
        int n = columns * rows;
        double[] carried = new double[n], into = new double[n];
        for (int k = frames.Count - 1; k >= 0; k--)
        {
            // carried holds what the frames after k propagate into frame k.
            BlockCosts f = frames[k];
            if (k < reuse.Length)
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    double intra = Math.Max(f.Intra[i], 1);
                    sum += Math.Log2((intra + carried[i]) / intra);
                }

                reuse[k] = sum / n;
            }

            if (k == 0)
            {
                break;
            }

            Array.Clear(into);
            if (!keys[k])
            {
                Spread(f, carried, into, columns, rows);
            }

            (carried, into) = (into, carried);
        }
    }

    /// <summary>Adds what each block of a frame propagates into the frame before, spread over the blocks its vector overlaps.</summary>
    private static void Spread(BlockCosts f, double[] carried, double[] into, int columns, int rows)
    {
        for (int by = 0; by < rows; by++)
        {
            for (int bx = 0; bx < columns; bx++)
            {
                int i = (by * columns) + bx;
                int intra = f.Intra[i], inter = f.Inter[i];
                if (intra <= 0 || inter >= intra)
                {
                    continue;
                }

                double amount = (intra + carried[i]) * (intra - inter) / intra;
                int x = (bx * 8) + f.Vectors[i * 2], y = (by * 8) + f.Vectors[(i * 2) + 1];
                int cx = x >> 3, cy = y >> 3, fx = x & 7, fy = y & 7;
                Add(into, columns, rows, cx, cy, amount * (8 - fx) * (8 - fy) / 64);
                Add(into, columns, rows, cx + 1, cy, amount * fx * (8 - fy) / 64);
                Add(into, columns, rows, cx, cy + 1, amount * (8 - fx) * fy / 64);
                Add(into, columns, rows, cx + 1, cy + 1, amount * fx * fy / 64);
            }
        }
    }

    private static void Add(double[] into, int columns, int rows, int x, int y, double amount)
    {
        if (amount > 0 && x >= 0 && y >= 0 && x < columns && y < rows)
        {
            into[(y * columns) + x] += amount;
        }
    }
}

/// <summary>One picture's block estimates against the references the coded structure gives it, for <see cref="BlockTree"/>.</summary>
internal sealed class TreeCosts(int count)
{
    /// <summary>The mode of a block that intra predicts at least as well.</summary>
    public const byte IntraMode = 255;

    /// <summary>Gets each block's intra error, in frame samples.</summary>
    public int[] Intra { get; } = new int[count];

    /// <summary>Gets each block's best inter error (list 0, list 1 or their mean), at most its intra error.</summary>
    public int[] Inter { get; } = new int[count];

    /// <summary>Gets each block's fine-detail term in <see cref="Inter"/> (<see cref="BlockCosts.Detail"/>), in frame samples.</summary>
    public int[] Detail { get; } = new int[count];

    /// <summary>Gets each block's best prediction: 0 from list 0, 1 from list 1, 2 from both, or <see cref="IntraMode"/>.</summary>
    public byte[] Modes { get; } = new byte[count];

    /// <summary>Gets each block's vector into the list 0 reference, in half-size pixels, x then y.</summary>
    public short[] Vectors0 { get; } = new short[count * 2];

    /// <summary>Gets each block's vector into the list 1 reference.</summary>
    public short[] Vectors1 { get; } = new short[count * 2];

    /// <summary>Gets or sets whether the picture has a list 1 reference (a B picture).</summary>
    public bool Bi { get; set; }
}

/// <summary>A picture in a <see cref="BlockTree"/> plan: its display index, references (-1 for none) and estimates (null for a keyframe).</summary>
internal readonly record struct TreePicture(int Index, int Ref0, int Ref1, TreeCosts? Costs);

/// <summary>
/// The macroblock tree per block along the coded structure (the published macroblock-tree algorithm: temporal propagation of
/// prediction dependence). Walking the pictures in reverse coding order, each block passes on to the pictures it predicts
/// from the share of its content they explain: its intra cost plus what later pictures take from it, times one minus its
/// inter over its intra cost. A block predicted from both sides gives half to each; each part lands where its vector points,
/// spread over the blocks it overlaps by area. A picture no other predicts from passes on but takes nothing.
/// </summary>
internal static class BlockTree
{
    /// <summary>
    /// Fills <paramref name="carried"/>, by display index, with what the later pictures of <paramref name="pictures"/> (in
    /// coding order) propagate into each block of each picture; references missing from <paramref name="carried"/> are left out.
    /// With <paramref name="noise"/> above 0, each block's amount is weighted by 1 / (1 + (m / noise)²), where m is its inter
    /// error a sample without the fine-detail term: a residual well above the quantizer's step will be coded anyway, so the
    /// reference explains less of the block than the estimate says.
    /// </summary>
    public static void Propagate(IReadOnlyList<TreePicture> pictures, int columns, int rows, IReadOnlyDictionary<int, double[]> carried, double noise = 0)
    {
        for (int k = pictures.Count - 1; k >= 0; k--)
        {
            TreePicture p = pictures[k];
            if (p.Costs is not { } costs || !carried.TryGetValue(p.Index, out double[]? own))
            {
                continue;
            }

            carried.TryGetValue(p.Ref0, out double[]? into0);
            double[]? into1 = p.Ref1 >= 0 && carried.TryGetValue(p.Ref1, out double[]? i1) ? i1 : null;
            for (int by = 0; by < rows; by++)
            {
                for (int bx = 0; bx < columns; bx++)
                {
                    int i = (by * columns) + bx;
                    int intra = costs.Intra[i], inter = costs.Inter[i];
                    byte mode = costs.Modes[i];
                    if (intra <= 0 || inter >= intra || mode == TreeCosts.IntraMode)
                    {
                        continue;
                    }

                    double amount = (intra + own[i]) * (intra - inter) / intra;
                    if (noise > 0)
                    {
                        double m = Math.Max(inter - costs.Detail[i], 0) / (256 * noise);
                        amount /= 1 + (m * m);
                    }

                    if (mode == 2)
                    {
                        Spread(into0, columns, rows, bx, by, costs.Vectors0, i, amount / 2);
                        Spread(into1, columns, rows, bx, by, costs.Vectors1, i, amount / 2);
                    }
                    else
                    {
                        Spread(mode == 0 ? into0 : into1, columns, rows, bx, by, mode == 0 ? costs.Vectors0 : costs.Vectors1, i, amount);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Writes each block's step offset in base-2 logarithms, minus <paramref name="strength"/> times log2((intra + carried) /
    /// intra) but at least minus <paramref name="clamp"/>, and returns their mean.
    /// </summary>
    public static double Offsets(int[] intraCosts, double[] carried, double strength, double clamp, Span<double> offsets)
    {
        double sum = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            double intra = Math.Max(intraCosts[i], 1);
            offsets[i] = Math.Max(-strength * Math.Log2((intra + carried[i]) / intra), -clamp);
            sum += offsets[i];
        }

        return offsets.Length == 0 ? 0 : sum / offsets.Length;
    }

    private static void Spread(double[]? into, int columns, int rows, int bx, int by, short[] vectors, int i, double amount)
    {
        if (into is null || amount <= 0)
        {
            return;
        }

        int x = (bx * 8) + vectors[i * 2], y = (by * 8) + vectors[(i * 2) + 1];
        int cx = x >> 3, cy = y >> 3, fx = x & 7, fy = y & 7;
        Add(into, columns, rows, cx, cy, amount * (8 - fx) * (8 - fy) / 64);
        Add(into, columns, rows, cx + 1, cy, amount * fx * (8 - fy) / 64);
        Add(into, columns, rows, cx, cy + 1, amount * (8 - fx) * fy / 64);
        Add(into, columns, rows, cx + 1, cy + 1, amount * fx * fy / 64);
    }

    private static void Add(double[] into, int columns, int rows, int x, int y, double amount)
    {
        if (amount > 0 && x >= 0 && y >= 0 && x < columns && y < rows)
        {
            into[(y * columns) + x] += amount;
        }
    }
}
