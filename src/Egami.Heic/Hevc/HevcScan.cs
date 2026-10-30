namespace Egami.Heic.Hevc;

/// <summary>The scan orders of H.265 6.5.3 to 6.5.5, as (x, y) pairs: up-right diagonal, horizontal and vertical.</summary>
internal static class HevcScan
{
    private static readonly byte[][] Diagonals = [.. Enumerable.Range(0, 6).Select(MakeDiagonal)];
    private static readonly byte[][] Horizontals = [.. Enumerable.Range(0, 6).Select(log2 => Make(log2, (i, s) => (i % s, i / s)))];
    private static readonly byte[][] Verticals = [.. Enumerable.Range(0, 6).Select(log2 => Make(log2, (i, s) => (i / s, i % s)))];

    /// <summary>Gets the up-right diagonal scan of a (1 &lt;&lt; log2) square block: x0, y0, x1, y1, ...</summary>
    internal static byte[] Diagonal(int log2) => Diagonals[log2];

    /// <summary>Gets the horizontal (row by row) scan.</summary>
    internal static byte[] Horizontal(int log2) => Horizontals[log2];

    /// <summary>Gets the vertical (column by column) scan.</summary>
    internal static byte[] Vertical(int log2) => Verticals[log2];

    private static readonly byte[][] Inverses = [.. Enumerable.Range(0, 3 * 6).Select(i => Invert(Get(i / 6, i % 6), i % 6))];

    /// <summary>Gets the position in a scan of each (x, y) of the block, at y &lt;&lt; log2 + x.</summary>
    internal static byte[] Inverse(int scanIdx, int log2) => Inverses[(scanIdx * 6) + log2];

    private static readonly byte[][] OffsetTables = [.. Enumerable.Range(0, 3 * 6).Select(i => Offsetize(Get(i / 6, 2), i % 6))];

    /// <summary>
    /// Gets, for each position of a 4x4 sub-block's scan, its offset y &lt;&lt; log2 + x in a block (1 &lt;&lt; log2) samples
    /// wide.
    /// </summary>
    internal static byte[] Offsets(int scanIdx, int log2) => OffsetTables[(scanIdx * 6) + log2];

    /// <summary>Gets a scan by scanIdx: 0 diagonal, 1 horizontal, 2 vertical.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static byte[] Get(int scanIdx, int log2) => scanIdx switch { 1 => Horizontals[log2], 2 => Verticals[log2], _ => Diagonals[log2] };

    private static byte[] MakeDiagonal(int log2)
    {
        int size = 1 << log2;
        byte[] scan = new byte[2 * size * size];
        int i = 0, x = 0, y = 0;
        while (i < size * size)
        {
            while (y >= 0)
            {
                if (x < size && y < size)
                {
                    scan[2 * i] = (byte)x;
                    scan[(2 * i) + 1] = (byte)y;
                    i++;
                }

                y--;
                x++;
            }

            y = x;
            x = 0;
        }

        return scan;
    }

    private static byte[] Make(int log2, Func<int, int, (int X, int Y)> position)
    {
        int size = 1 << log2;
        byte[] scan = new byte[2 * size * size];
        for (int i = 0; i < size * size; i++)
        {
            (int x, int y) = position(i, size);
            scan[2 * i] = (byte)x;
            scan[(2 * i) + 1] = (byte)y;
        }

        return scan;
    }

    private static byte[] Invert(byte[] scan, int log2)
    {
        // Positions of a 32x32 block's scan of 4x4 sub-blocks fit a byte; the inverse of larger scans is not needed.
        byte[] inverse = new byte[scan.Length / 2];
        for (int i = 0; i < inverse.Length && i < 256; i++)
        {
            inverse[(scan[(2 * i) + 1] << log2) + scan[2 * i]] = (byte)i;
        }

        return inverse;
    }

    private static byte[] Offsetize(byte[] scan, int log2)
    {
        byte[] offsets = new byte[16];
        for (int p = 0; p < 16; p++)
        {
            offsets[p] = (byte)((scan[(2 * p) + 1] << log2) + scan[2 * p]);
        }

        return offsets;
    }
}
