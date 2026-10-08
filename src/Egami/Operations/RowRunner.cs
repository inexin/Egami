namespace Egami.Operations;

/// <summary>
/// Splits row work into bands and runs them, in parallel when the image is large enough to be worth it.
/// Every operation and <see cref="Raster{TPixel}.ForEachRow"/> run through it.
/// </summary>
internal static class RowRunner
{
    /// <summary>Images smaller than this run on the calling thread.</summary>
    internal const long ParallelThresholdBytes = 256 * 1024;

    private const int MinBandRows = 16;
    private const long TargetBandBytes = 64 * 1024;

    /// <summary>Runs <paramref name="action"/> over <paramref name="rows"/> rows in bands, checking for cancellation between bands.</summary>
    /// <param name="rows">The number of rows.</param>
    /// <param name="rowBytes">The size of one row, used to size bands and decide whether to go parallel.</param>
    /// <param name="maxParallelism">The most threads to use; 1 runs everything on the calling thread.</param>
    /// <param name="action">The work for one band.</param>
    /// <param name="cancellationToken">Cancels between bands.</param>
    internal static void Run(int rows, long rowBytes, int maxParallelism, RowBandAction action, CancellationToken cancellationToken)
    {
        if (rows <= 0)
        {
            return;
        }

        int bandRows = BandRows(rowBytes);
        int bands = (rows + bandRows - 1) / bandRows;
        if (maxParallelism <= 1 || bands == 1 || rows * Math.Max(rowBytes, 1) < ParallelThresholdBytes)
        {
            for (int start = 0; start < rows; start += bandRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action(start, Math.Min(start + bandRows, rows));
            }

            return;
        }

        ParallelOptions options = new() { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellationToken };
        Parallel.For(0, bands, options, band =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = band * bandRows;
            action(start, Math.Min(start + bandRows, rows));
        });
    }

    /// <summary>Returns the rows per band: about 64 KB of pixels, and never fewer than 16 rows.</summary>
    internal static int BandRows(long rowBytes)
    {
        return (int)Math.Max(MinBandRows, Math.Min(int.MaxValue, TargetBandBytes / Math.Max(rowBytes, 1)));
    }
}
