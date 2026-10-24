namespace Egami.Formats.Av1;

/// <summary>
/// The motion pre-pass of an inter frame: before the block search, a whole-sample vector and its sum of absolute differences for
/// every 16x16 luma cell against every reference the frame searches, found hierarchically on the source (the open-loop
/// hierarchical motion estimation of SVT-AV1 and the first-pass and temporal-model searches of libaom; BSD): an exhaustive search
/// of each 64x64 area at a quarter of the size each way, refined around its own and its neighbours' vectors at half the size for
/// each 32x32 area, then at the full size for each 16x16 cell. Each level's rows run in parallel, each reading only the level
/// before, so the vectors do not depend on the thread count. The block search starts from them, ranks its references by them
/// and sizes its partitions by them; the temporal model of later frames reads them too (<see cref="Vector"/>, <see cref="Sad"/>).
/// </summary>
internal sealed class Av1MotionPrepass
{
    /// <summary>The cells' side, in luma samples.</summary>
    internal const int Cell = 16;

    /// <summary>How far the quarter-size search reaches each way, in quarter samples.</summary>
    private const int Range = 12;

    private readonly int[]?[] _cells = new int[]?[8];

    private Av1MotionPrepass(int columns, int rows) => (Columns, Rows) = (columns, rows);

    /// <summary>Gets the columns of 16x16 cells.</summary>
    internal int Columns { get; }

    /// <summary>Gets the rows of 16x16 cells.</summary>
    internal int Rows { get; }

    /// <summary>Gets whether the pre-pass searched a reference (LAST at 1).</summary>
    internal bool Has(int rf) => _cells[rf] is not null;

    /// <summary>Gets a cell's whole-sample vector against a reference.</summary>
    internal (int Row, int Col) Vector(int rf, int row, int col)
    {
        int[] cells = _cells[rf]!;
        int at = ((row * Columns) + col) * 3;
        return (cells[at], cells[at + 1]);
    }

    /// <summary>Gets a cell's sum of absolute differences at its vector against a reference.</summary>
    internal int Sad(int rf, int row, int col) => _cells[rf]![(((row * Columns) + col) * 3) + 2];

    /// <summary>
    /// Searches every reference of <paramref name="refs"/> for every cell of the luma <paramref name="source"/> (the frame's padded
    /// plane at its stride), on the frame's threads.
    /// </summary>
    internal static Av1MotionPrepass Build(Av1Frame frame, ushort[] source, int stride, Av1ReferencePlane?[] planes, int[] refs)
    {
        int width = frame.Header.UpscaledWidth, height = frame.Header.FrameHeight;
        Av1MotionPrepass pass = new((width + Cell - 1) / Cell, (height + Cell - 1) / Cell);
        if (refs.Length == 0)
        {
            return pass;
        }

        // The source at half and a quarter of its size; the planes are whole superblocks, so both stay whole cells.
        int rows = source.Length / stride;
        ushort[] half = new ushort[(stride / 2) * (rows / 2)];
        Halve(source, stride, half, stride / 2, stride / 2, rows / 2);
        ushort[] quarter = new ushort[(stride / 4) * (rows / 4)];
        Halve(half, stride / 2, quarter, stride / 4, stride / 4, rows / 4);
        foreach (int rf in refs)
        {
            _ = planes[rf]!.Half.Half;
        }

        int cols2 = (width + 63) / 64, rows2 = (height + 63) / 64;
        int cols1 = (width + 31) / 32, rows1 = (height + 31) / 32;
        foreach (int rf in refs)
        {
            Av1ReferencePlane full = planes[rf]!, halfPlane = full.Half, quarterPlane = halfPlane.Half;
            int[] level2 = new int[cols2 * rows2 * 3], level1 = new int[cols1 * rows1 * 3], level0 = new int[pass.Columns * pass.Rows * 3];
            frame.For(rows2, row =>
            {
                for (int col = 0; col < cols2; col++)
                {
                    Exhaustive(quarter, stride / 4, quarterPlane, col * 16, row * 16, level2, ((row * cols2) + col) * 3);
                }
            });
            frame.For(rows1, row =>
            {
                for (int col = 0; col < cols1; col++)
                {
                    Refine(half, stride / 2, halfPlane, col, row, level2, cols2, rows2, level1, cols1);
                }
            });
            frame.For(pass.Rows, row =>
            {
                for (int col = 0; col < pass.Columns; col++)
                {
                    Refine(source, stride, full, col, row, level1, cols1, rows1, level0, pass.Columns);
                }
            });
            pass._cells[rf] = level0;
        }

        return pass;
    }

    /// <summary>Sets each sample of <paramref name="target"/> to the rounded mean of the four it covers in <paramref name="from"/>.</summary>
    internal static void Halve(ushort[] from, int fromStride, ushort[] target, int stride, int width, int rows)
    {
        for (int y = 0; y < rows; y++)
        {
            int a = 2 * y * fromStride, b = a + fromStride, t = y * stride;
            for (int x = 0; x < width; x++)
            {
                target[t + x] = (ushort)((from[a + (2 * x)] + from[a + (2 * x) + 1] + from[b + (2 * x)] + from[b + (2 * x) + 1] + 2) >> 2);
            }
        }
    }

    /// <summary>Searches every whole-sample place within <see cref="Range"/> of a 16x16 block of the quarter-size source.</summary>
    private static void Exhaustive(ushort[] source, int stride, Av1ReferencePlane plane, int x0, int y0, int[] result, int at)
    {
        int sourceAt = (y0 * stride) + x0;
        int best = int.MaxValue, bestRow = 0, bestCol = 0, bestDistance = 0;
        for (int dy = -Range; dy <= Range; dy++)
        {
            for (int dx = -Range; dx <= Range; dx++)
            {
                if (!plane.Holds(x0 + dx, y0 + dy, 16, 16))
                {
                    continue;
                }

                int sad = Av1MotionSearch.Sad(source, sourceAt, stride, plane.Samples, plane.At(x0 + dx, y0 + dy), plane.Stride, 16, 16);
                int distance = Math.Abs(dx) + Math.Abs(dy);
                if (sad < best || (sad == best && distance < bestDistance))
                {
                    (best, bestRow, bestCol, bestDistance) = (sad, dy, dx, distance);
                }
            }
        }

        (result[at], result[at + 1], result[at + 2]) = (bestRow, bestCol, best == int.MaxValue ? 0 : best);
    }

    /// <summary>
    /// Refines the vector of the 16x16 block (<paramref name="col"/>, <paramref name="row"/>) of a level from the level above
    /// (blocks twice the size at half the scale): its own block's vector and its four neighbours', doubled, the best of them
    /// searched two samples each way.
    /// </summary>
    private static void Refine(ushort[] source, int stride, Av1ReferencePlane plane, int col, int row, int[] above, int aboveCols, int aboveRows,
        int[] result, int cols)
    {
        int x0 = col * 16, y0 = row * 16;
        int sourceAt = (y0 * stride) + x0;
        int ac = col >> 1, ar = row >> 1;
        Span<int> starts = stackalloc int[10];
        int count = 0;
        Add(ar, ac, starts, ref count);
        Add(ar - 1, ac, starts, ref count);
        Add(ar, ac - 1, starts, ref count);
        Add(ar, ac + 1, starts, ref count);
        Add(ar + 1, ac, starts, ref count);
        int best = int.MaxValue, bestRow = 0, bestCol = 0;
        for (int i = 0; i < count; i++)
        {
            int sad = Sad(starts[2 * i], starts[(2 * i) + 1]);
            if (sad < best)
            {
                (best, bestRow, bestCol) = (sad, starts[2 * i], starts[(2 * i) + 1]);
            }
        }

        // Nearest places first, so a tie keeps the vector nearer the start.
        (int centerRow, int centerCol) = (bestRow, bestCol);
        for (int ring = 1; ring <= 2; ring++)
        {
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                    {
                        continue;
                    }

                    int sad = Sad(centerRow + dy, centerCol + dx);
                    if (sad < best)
                    {
                        (best, bestRow, bestCol) = (sad, centerRow + dy, centerCol + dx);
                    }
                }
            }
        }

        int at = ((row * cols) + col) * 3;
        (result[at], result[at + 1], result[at + 2]) = (bestRow, bestCol, best == int.MaxValue ? 0 : best);

        void Add(int r, int c, Span<int> list, ref int n)
        {
            if (r < 0 || c < 0 || r >= aboveRows || c >= aboveCols)
            {
                return;
            }

            int at = ((r * aboveCols) + c) * 3;
            int vr = 2 * above[at], vc = 2 * above[at + 1];
            for (int i = 0; i < n; i++)
            {
                if (list[2 * i] == vr && list[(2 * i) + 1] == vc)
                {
                    return;
                }
            }

            list[2 * n] = vr;
            list[(2 * n) + 1] = vc;
            n++;
        }

        int Sad(int dy, int dx) => plane.Holds(x0 + dx, y0 + dy, 16, 16)
            ? Av1MotionSearch.Sad(source, sourceAt, stride, plane.Samples, plane.At(x0 + dx, y0 + dy), plane.Stride, 16, 16)
            : int.MaxValue;
    }
}
