using System.Buffers;

namespace Egami.Formats.Psd;

/// <summary>The flattener's distance field: how far each pixel's center is from a shape's edge, for strokes, glows and bevels.</summary>
internal sealed partial class PsdFlattener
{
    /// <summary>
    /// Writes the signed distance from each pixel's center to the edge of <paramref name="shape"/> (coverage 0 to 1, a plane of
    /// <paramref name="width"/> by <paramref name="height"/>) into <paramref name="distance"/>: positive outside, negative inside.
    /// A partly covered pixel's edge is taken to cross it at 0.5 − coverage; a pixel wholly outside (or inside) is that far from
    /// the nearest partly or wholly covered (uncovered) pixel, measured exactly by the lower envelope of parabolas of
    /// Felzenszwalb and Huttenlocher's "Distance Transforms of Sampled Functions", corrected by that pixel's own crossing.
    /// </summary>
    private void SignedDistance(float[] shape, int width, int height, float[] distance)
    {
        int n = width * height;
        float[] squared = Allocate(n);
        float[] nearestRow = Allocate(n);
        int longest = Math.Max(width, height);
        float[] f = ArrayPool<float>.Shared.Rent(longest);
        float[] d = ArrayPool<float>.Shared.Rent(longest);
        int[] arg = ArrayPool<int>.Shared.Rent(longest);
        int[] v = ArrayPool<int>.Shared.Rent(longest);
        double[] z = ArrayPool<double>.Shared.Rent(longest + 1);
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool outside = pass == 0;

                // Columns: the squared distance to the nearest seed in the column, and its row.
                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        float s = shape[(y * width) + x];
                        f[y] = (outside ? s > Empty : s < Full) ? 0 : float.PositiveInfinity;
                    }

                    Envelope(f, d, arg, v, z, height);
                    for (int y = 0; y < height; y++)
                    {
                        squared[(y * width) + x] = d[y];
                        nearestRow[(y * width) + x] = arg[y];
                    }
                }

                // Rows: over the columns' results, which gives the nearest seed's column, and through it its row.
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    squared.AsSpan(row, width).CopyTo(f);
                    Envelope(f, d, arg, v, z, width);
                    for (int x = 0; x < width; x++)
                    {
                        float s = shape[row + x];
                        bool target = outside ? s <= Empty : s >= Full;
                        if (!target)
                        {
                            if (pass == 0 && s < Full)
                            {
                                distance[row + x] = 0.5f - s;
                            }

                            continue;
                        }

                        if (arg[x] < 0 || float.IsPositiveInfinity(d[x]))
                        {
                            distance[row + x] = outside ? float.MaxValue / 4 : -float.MaxValue / 4;
                            continue;
                        }

                        int qx = arg[x], qy = (int)nearestRow[(y * width) + qx];
                        float c = shape[(qy * width) + qx];
                        float reach = MathF.Sqrt(d[x]) - ((outside ? c : 1 - c) - 0.5f);
                        distance[row + x] = outside ? reach : -reach;
                    }
                }
            }
        }
        finally
        {
            Release(squared);
            Release(nearestRow);
            ArrayPool<float>.Shared.Return(f);
            ArrayPool<float>.Shared.Return(d);
            ArrayPool<int>.Shared.Return(arg);
            ArrayPool<int>.Shared.Return(v);
            ArrayPool<double>.Shared.Return(z);
        }
    }

    /// <summary>Coverage at or under which a pixel counts as wholly outside a shape.</summary>
    private const float Empty = 1e-4f;

    /// <summary>Coverage at or over which a pixel counts as wholly inside a shape.</summary>
    private const float Full = 1 - 1e-4f;

    /// <summary>
    /// The one-dimensional transform: <paramref name="d"/>[q] = min over p of (q − p)² + <paramref name="f"/>[p], and
    /// <paramref name="arg"/>[q] the p that gives it (−1 and infinity when every f is infinite).
    /// </summary>
    private static void Envelope(float[] f, float[] d, int[] arg, int[] v, double[] z, int n)
    {
        int k = -1;
        for (int q = 0; q < n; q++)
        {
            if (float.IsPositiveInfinity(f[q]))
            {
                continue;
            }

            if (k < 0)
            {
                k = 0;
                v[0] = q;
                z[0] = double.NegativeInfinity;
                z[1] = double.PositiveInfinity;
                continue;
            }

            // Where q's parabola meets the lowest so far; those it hides from there on are dropped (z[0] is −∞, so k stays ≥ 0).
            double s;
            while (true)
            {
                int p = v[k];
                s = ((f[q] + ((double)q * q)) - (f[p] + ((double)p * p))) / (2.0 * (q - p));
                if (s > z[k])
                {
                    break;
                }

                k--;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        if (k < 0)
        {
            d.AsSpan(0, n).Fill(float.PositiveInfinity);
            arg.AsSpan(0, n).Fill(-1);
            return;
        }

        int j = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[j + 1] < q)
            {
                j++;
            }

            int p = v[j];
            d[q] = ((float)(q - p) * (q - p)) + f[p];
            arg[q] = p;
        }
    }
}
