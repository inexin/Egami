using System.Buffers;

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

        // Each column, then each row, is transformed on its own, so they run in parallel: one chunk of them per processor, each
        // with buffers rented from the shared pools.
        int chunks = Math.Max(1, Math.Min(Environment.ProcessorCount, longest));

        // Down each column: the distance to the nearest seed in the column, and its row.
        Parallel.For(0, chunks, parallel, chunk =>
        {
            double[] doubles = ArrayPool<double>.Shared.Rent((3 * longest) + 1);
            int[] ints = ArrayPool<int>.Shared.Rent(3 * longest);
            Span<double> f = doubles.AsSpan(0, height), output = doubles.AsSpan(longest, longest), z = doubles.AsSpan(2 * longest, longest + 1);
            Span<int> from = ints.AsSpan(0, longest), where = ints.AsSpan(longest, longest), v = ints.AsSpan(2 * longest, longest);
            for (int x = (int)((long)width * chunk / chunks); x < (int)((long)width * (chunk + 1) / chunks); x++)
            {
                for (int y = 0; y < height; y++)
                {
                    f[y] = seeds[(y * width) + x] ? 0 : Far;
                    from[y] = y;
                }

                Envelope(f, from, output, where, v, z);
                for (int y = 0; y < height; y++)
                {
                    d[(y * width) + x] = output[y];
                    arg[(y * width) + x] = output[y] >= Far ? -1 : (where[y] * width) + x;
                }
            }

            ArrayPool<double>.Shared.Return(doubles);
            ArrayPool<int>.Shared.Return(ints);
        });

        // Along each row of the column distances.
        Parallel.For(0, chunks, parallel, chunk =>
        {
            double[] doubles = ArrayPool<double>.Shared.Rent((3 * longest) + 1);
            int[] ints = ArrayPool<int>.Shared.Rent(3 * longest);
            Span<double> f = doubles.AsSpan(0, width), output = doubles.AsSpan(longest, longest), z = doubles.AsSpan(2 * longest, longest + 1);
            Span<int> from = ints.AsSpan(0, longest), where = ints.AsSpan(longest, longest), v = ints.AsSpan(2 * longest, longest);
            for (int y = (int)((long)height * chunk / chunks); y < (int)((long)height * (chunk + 1) / chunks); y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    f[x] = d[row + x];
                    from[x] = arg[row + x];
                }

                Envelope(f, from, output, where, v, z);
                for (int x = 0; x < width; x++)
                {
                    d[row + x] = Math.Min(output[x], Far);
                    arg[row + x] = output[x] >= Far ? -1 : where[x];
                }
            }

            ArrayPool<double>.Shared.Return(doubles);
            ArrayPool<int>.Shared.Return(ints);
        });

        return d;
    }

    /// <summary>
    /// The one-dimensional transform: output[q] = min over p of (q − p)² + f[p], with where[q] = from[p] for the minimizing p.
    /// The parabolas' lower envelope is built left to right (v holds their roots, z the bounds between them), then read off.
    /// </summary>
    private static void Envelope(ReadOnlySpan<double> f, ReadOnlySpan<int> from, Span<double> output, Span<int> where, Span<int> v, Span<double> z)
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
}
