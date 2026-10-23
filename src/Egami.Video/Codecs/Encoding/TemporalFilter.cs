namespace Egami.Video.Codecs.Encoding;

/// <summary>
/// Builds a hidden alternate reference frame: a frame blended with its neighbours along their motion, so noise that differs
/// from frame to frame averages out and what stays is what the frames around it can all predict from (libvpx's arnr filter is
/// the idea; this is written from the description). Per 16×16 block and neighbour, a whole-pixel search finds the neighbour's
/// block; how well it matches sets the block's weight (two, one or none), and per sample the weight falls with the squared
/// difference, more steeply at lower strengths. Blocks are filtered in parallel; the result does not depend on the threads.
/// </summary>
internal static class TemporalFilter
{
    private const int Block = 16;
    private const int Range = 16;

    /// <summary>Filters <paramref name="frames"/>[<paramref name="center"/>] with the others into <paramref name="output"/>.</summary>
    /// <param name="frames">The frames in display order, all the same size.</param>
    /// <param name="center">The frame the result stands for.</param>
    /// <param name="strength">0 (weakest) to 6.</param>
    /// <param name="output">Where the result goes, the same size; its alpha plane, if any, is not touched.</param>
    /// <param name="parallelism">The most threads to use.</param>
    public static void Apply(IReadOnlyList<YuvRaster<byte>> frames, int center, int strength, YuvRaster<byte> output, int parallelism)
    {
        YuvRaster<byte> source = frames[center];
        int width = source.Width, height = source.Height;
        int cols = (width + Block - 1) / Block, rows = (height + Block - 1) / Block;
        if (frames.Count == 1)
        {
            EncoderPictures.CopyPlane(source.Y, output.Y);
            EncoderPictures.CopyPlane(source.U!, output.U!);
            EncoderPictures.CopyPlane(source.V!, output.V!);
            return;
        }

        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, parallelism) };
        Parallel.For(0, rows, options, () => new Scratch(), (by, _, scratch) =>
        {
            for (int bx = 0; bx < cols; bx++)
            {
                FilterBlock(frames, center, strength, output, bx * Block, by * Block, scratch);
            }

            return scratch;
        }, _ => { });
    }

    private sealed class Scratch
    {
        public readonly int[] Sum = new int[Block * Block];
        public readonly int[] Count = new int[Block * Block];
        public readonly int[] SumU = new int[Block * Block / 4];
        public readonly int[] CountU = new int[Block * Block / 4];
        public readonly int[] SumV = new int[Block * Block / 4];
    }

    private static void FilterBlock(IReadOnlyList<YuvRaster<byte>> frames, int center, int strength, YuvRaster<byte> output, int x, int y, Scratch s)
    {
        YuvRaster<byte> source = frames[center];
        int width = source.Width, height = source.Height;
        int bw = Math.Min(Block, width - x), bh = Math.Min(Block, height - y);
        int cx = x / 2, cy = y / 2, cw = (bw + 1) / 2, ch = (bh + 1) / 2;
        int chromaWidth = source.U!.Width, chromaHeight = source.U.Height;
        cw = Math.Min(cw, chromaWidth - cx);
        ch = Math.Min(ch, chromaHeight - cy);
        Array.Clear(s.Sum);
        Array.Clear(s.Count);
        Array.Clear(s.SumU);
        Array.Clear(s.CountU);
        Array.Clear(s.SumV);
        int round = strength > 0 ? 1 << (strength - 1) : 0;
        for (int f = 0; f < frames.Count; f++)
        {
            YuvRaster<byte> other = frames[f];
            int mx = 0, my = 0, blockWeight = 2;
            if (f != center)
            {
                (int sad, mx, my) = Search(source.Y, other.Y, x, y, bw, bh);

                // The average difference per sample decides how much of the block the neighbour may give.
                int perSample = sad / (bw * bh);
                blockWeight = perSample < 3 ? 2 : perSample < 8 ? 1 : 0;
                if (blockWeight == 0)
                {
                    continue;
                }
            }

            for (int i = 0; i < bh; i++)
            {
                ReadOnlySpan<byte> a = source.Y.Row(y + i).Slice(x, bw);
                ReadOnlySpan<byte> b = other.Y.Row(Math.Clamp(y + i + my, 0, height - 1));
                for (int j = 0; j < bw; j++)
                {
                    int pixel = b[Math.Clamp(x + j + mx, 0, width - 1)];
                    int weight = f == center ? 16 * blockWeight : Weight(a[j] - pixel, strength, round) * blockWeight;
                    s.Sum[(i * Block) + j] += weight * pixel;
                    s.Count[(i * Block) + j] += weight;
                }
            }

            int cmx = mx / 2, cmy = my / 2;
            for (int i = 0; i < ch; i++)
            {
                ReadOnlySpan<byte> u = source.U.Row(cy + i).Slice(cx, cw), v = source.V!.Row(cy + i).Slice(cx, cw);
                int row = Math.Clamp(cy + i + cmy, 0, chromaHeight - 1);
                ReadOnlySpan<byte> ou = other.U!.Row(row), ov = other.V!.Row(row);
                for (int j = 0; j < cw; j++)
                {
                    int col = Math.Clamp(cx + j + cmx, 0, chromaWidth - 1);
                    int pu = ou[col], pv = ov[col];
                    int weight = f == center ? 16 * blockWeight : Math.Min(Weight(u[j] - pu, strength, round), Weight(v[j] - pv, strength, round)) * blockWeight;
                    int k = (i * (Block / 2)) + j;
                    s.SumU[k] += weight * pu;
                    s.SumV[k] += weight * pv;
                    s.CountU[k] += weight;
                }
            }
        }

        for (int i = 0; i < bh; i++)
        {
            Span<byte> row = output.Y.Row(y + i).Slice(x, bw);
            for (int j = 0; j < bw; j++)
            {
                int k = (i * Block) + j, count = s.Count[k];
                row[j] = (byte)((s.Sum[k] + (count / 2)) / count);
            }
        }

        for (int i = 0; i < ch; i++)
        {
            Span<byte> u = output.U!.Row(cy + i).Slice(cx, cw), v = output.V!.Row(cy + i).Slice(cx, cw);
            for (int j = 0; j < cw; j++)
            {
                int k = (i * (Block / 2)) + j, count = s.CountU[k];
                u[j] = (byte)((s.SumU[k] + (count / 2)) / count);
                v[j] = (byte)((s.SumV[k] + (count / 2)) / count);
            }
        }
    }

    /// <summary>A sample's weight, 16 for equal values falling to 0 as the squared difference grows past 16 × 2^strength / 3.</summary>
    private static int Weight(int difference, int strength, int round)
    {
        int modifier = ((difference * difference * 3) + round) >> strength;
        return modifier >= 16 ? 0 : 16 - modifier;
    }

    private static (int Sad, int X, int Y) Search(YuvPlane<byte> a, YuvPlane<byte> b, int x, int y, int bw, int bh)
    {
        int bestX = 0, bestY = 0;
        int best = Sad(a, b, x, y, 0, 0, bw, bh);
        for (int step = 8; step >= 1; step >>= 1)
        {
            bool moved = true;
            for (int round = 0; moved && round < 4; round++)
            {
                moved = false;
                int sx = bestX, sy = bestY;
                foreach ((int dx, int dy) in (ReadOnlySpan<(int, int)>)[(-step, 0), (step, 0), (0, -step), (0, step)])
                {
                    int mx = sx + dx, my = sy + dy;
                    if (Math.Abs(mx) > Range || Math.Abs(my) > Range)
                    {
                        continue;
                    }

                    int cost = Sad(a, b, x, y, mx, my, bw, bh);
                    if (cost < best)
                    {
                        (best, bestX, bestY) = (cost, mx, my);
                        moved = true;
                    }
                }
            }
        }

        return (best, bestX, bestY);
    }

    private static int Sad(YuvPlane<byte> a, YuvPlane<byte> b, int x, int y, int mx, int my, int bw, int bh)
    {
        int width = a.Width, height = a.Height, sum = 0;
        bool inside = x + mx >= 0 && x + mx + bw <= width && y + my >= 0 && y + my + bh <= height;
        for (int i = 0; i < bh; i++)
        {
            ReadOnlySpan<byte> ra = a.Row(y + i).Slice(x, bw);
            ReadOnlySpan<byte> rb = b.Row(Math.Clamp(y + i + my, 0, height - 1));
            if (inside)
            {
                rb = rb.Slice(x + mx, bw);
                for (int j = 0; j < bw; j++)
                {
                    sum += Math.Abs(ra[j] - rb[j]);
                }
            }
            else
            {
                for (int j = 0; j < bw; j++)
                {
                    sum += Math.Abs(ra[j] - rb[Math.Clamp(x + j + mx, 0, width - 1)]);
                }
            }
        }

        return sum;
    }
}
