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
