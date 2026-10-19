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

    /// <summary>Gets a scan by scanIdx: 0 diagonal, 1 horizontal, 2 vertical.</summary>
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
}
