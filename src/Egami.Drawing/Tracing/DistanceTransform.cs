namespace Egami.Drawing.Tracing;

/// <summary>
/// The exact squared Euclidean distance transform of a pixel grid, written from P. F. Felzenszwalb and D. P. Huttenlocher,
/// "Distance transforms of sampled functions" (Theory of Computing 8, 2012): a one-dimensional transform down each column, then
/// one along each row of the result, each the lower envelope of the parabolas rooted at the samples. It also gives each pixel
/// its nearest seed.
/// </summary>
internal static class DistanceTransform
{
    /// <summary>The value that stands for "no seed": far beyond any distance an image can hold.</summary>
    internal const double Far = 1e20;

    /// <summary>
    /// Returns each pixel's squared distance to the nearest seed pixel, centre to centre, or <see cref="Far"/> when there are
    /// none; <paramref name="nearest"/>, when given, receives the index of that seed (-1 when there are none).
    /// </summary>
    internal static double[] Squared(bool[] seeds, int width, int height, int[]? nearest, CancellationToken cancellationToken)
    {
        int n = width * height;
        double[] d = new double[n];
        int[] arg = nearest ?? new int[n];
        int longest = Math.Max(width, height);
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };

        // Each column, then each row, is transformed on its own, so they run in parallel with a set of buffers per thread.
        // Down each column: the distance to the nearest seed in the column, and its row.
        Parallel.For(
            0,
            width,
            parallel,
            () => new Work(longest),
            (x, _, w) =>
            {
                for (int y = 0; y < height; y++)
                {
                    w.F[y] = seeds[(y * width) + x] ? 0 : Far;
                    w.From[y] = y;
                }

                Envelope(w.F.AsSpan(0, height), w.From, w.Output, w.Where, w.V, w.Z);
                for (int y = 0; y < height; y++)
                {
                    d[(y * width) + x] = w.Output[y];
                    arg[(y * width) + x] = w.Output[y] >= Far ? -1 : (w.Where[y] * width) + x;
                }

                return w;
            },
            _ => { });

        // Along each row of the column distances.
        Parallel.For(
            0,
            height,
            parallel,
            () => new Work(longest),
            (y, _, w) =>
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    w.F[x] = d[row + x];
                    w.From[x] = arg[row + x];
                }

                Envelope(w.F.AsSpan(0, width), w.From, w.Output, w.Where, w.V, w.Z);
                for (int x = 0; x < width; x++)
                {
                    d[row + x] = Math.Min(w.Output[x], Far);
                    arg[row + x] = w.Output[x] >= Far ? -1 : w.Where[x];
                }

                return w;
            },
            _ => { });

        return d;
    }

    /// <summary>
    /// The one-dimensional transform: output[q] = min over p of (q − p)² + f[p], with where[q] = from[p] for the minimizing p.
    /// The parabolas' lower envelope is built left to right (v holds their roots, z the bounds between them), then read off.
    /// </summary>
    private static void Envelope(ReadOnlySpan<double> f, int[] from, double[] output, int[] where, int[] v, double[] z)
    {
        int n = f.Length;
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = Intersection(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, q, v[k]);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            double dq = q - v[k];
            output[q] = (dq * dq) + f[v[k]];
            where[q] = from[v[k]];
        }
    }

    /// <summary>Where the parabolas rooted at q and p cross.</summary>
    private static double Intersection(ReadOnlySpan<double> f, int q, int p)
    {
        return ((f[q] + ((double)q * q)) - (f[p] + ((double)p * p))) / (2.0 * (q - p));
    }

    /// <summary>One thread's buffers for <see cref="Envelope"/>.</summary>
    private sealed class Work(int length)
    {
        public double[] F { get; } = new double[length];

        public double[] Output { get; } = new double[length];

        public int[] From { get; } = new int[length];

        public int[] Where { get; } = new int[length];

        public int[] V { get; } = new int[length];

        public double[] Z { get; } = new double[length + 1];
    }
}
