namespace Egami.Drawing.Tracing;

/// <summary>
/// Coverage fields for Logo mode. Each palette color's share of each pixel becomes the signed distance from the pixel's centre to
/// the color's edge, which a cubic B-spline interpolates; the splines are sampled on a grid finer than the pixels, each sample
/// labelled by the color whose field is largest there. The spline is written from M. Unser, A. Aldroubi and M. Eden, "B-spline
/// signal processing" (IEEE Trans. Signal Processing, 1993), for the recursive prefilter that makes it pass through the samples,
/// and P. Thévenaz, T. Blu and M. Unser, "Interpolation revisited" (IEEE Trans. Medical Imaging, 2000), for its mirror boundaries
/// and evaluation.
/// </summary>
internal static class CoverageField
{
    /// <summary>How far past a color's pixels its field is worked out: the prefilter's response falls to 0.268 per pixel.</summary>
    private const int Margin = 8;

    /// <summary>How far from an edge, in pixels, distances are measured; farther pixels sit at this distance.</summary>
    private const float Reach = 4;

    /// <summary>The pole of the cubic B-spline's inverse filter, √3 − 2.</summary>
    private static readonly double Pole = Math.Sqrt(3) - 2;

    /// <summary>Samples the fields of every color at <paramref name="scale"/> by <paramref name="scale"/> points per pixel.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="colors">The number of colors.</param>
    /// <param name="labels">Up to three colors per pixel, at 3i, 3i + 1 and 3i + 2.</param>
    /// <param name="weights">Each of those colors' share of the pixel; 0 where the slot is unused.</param>
    /// <param name="scale">The samples per pixel along each axis.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// Each sample's color, and how far its field leads the next largest field at that sample (0 or more), row by row.
    /// </returns>
    internal static (byte[] Labels, float[] Margins) Sample(int width, int height, int colors, byte[] labels, float[] weights, int scale, CancellationToken cancellationToken)
    {
        int fineWidth = checked(width * scale), fineHeight = checked(height * scale);
        int samples = checked(fineWidth * fineHeight);
        float[] best = new float[samples], second = new float[samples];
        byte[] winner = new byte[samples];
        Array.Fill(best, float.NegativeInfinity);
        Array.Fill(second, float.NegativeInfinity);

        // Each color's pixels, as a box.
        int[] minX = new int[colors], minY = new int[colors], maxX = new int[colors], maxY = new int[colors];
        Array.Fill(minX, int.MaxValue);
        Array.Fill(minY, int.MaxValue);
        Array.Fill(maxX, -1);
        Array.Fill(maxY, -1);
        for (int i = 0; i < width * height; i++)
        {
            int x = i % width, y = i / width;
            for (int s = 0; s < 3; s++)
            {
                if (weights[(3 * i) + s] != 0)
                {
                    int k = labels[(3 * i) + s];
                    minX[k] = Math.Min(minX[k], x);
                    maxX[k] = Math.Max(maxX[k], x);
                    minY[k] = Math.Min(minY[k], y);
                    maxY[k] = Math.Max(maxY[k], y);
                }
            }
        }

        // The pixels and weights each fine sample interpolates from, one table per axis.
        (int[] fineBaseX, float[] fineWeightX) = Taps(fineWidth, scale);
        (int[] fineBaseY, float[] fineWeightY) = Taps(fineHeight, scale);
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        DistanceWork work = new();
        float[]? share = null, rows = null;
        for (int k = 0; k < colors; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maxX[k] < 0)
            {
                continue;
            }

            int x0 = Math.Max(0, minX[k] - Margin), x1 = Math.Min(width - 1, maxX[k] + Margin);
            int y0 = Math.Max(0, minY[k] - Margin), y1 = Math.Min(height - 1, maxY[k] + Margin);
            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;

            // The color's share of each pixel in its box, turned into distances to its edge, then into the coefficients of the
            // spline through them.
            if (share is null || share.Length < cw * ch)
            {
                share = new float[cw * ch];
            }

            float[] shares = share;
            int color = k;
            Parallel.For(0, ch, parallel, j =>
            {
                for (int i = 0; i < cw; i++)
                {
                    int p = ((j + y0) * width) + i + x0;
                    float sum = 0;
                    for (int s = 0; s < 3; s++)
                    {
                        if (labels[(3 * p) + s] == color)
                        {
                            sum += weights[(3 * p) + s];
                        }
                    }

                    shares[(j * cw) + i] = Math.Clamp(sum, 0, 1);
                }
            });

            float[] coefficients = Distances(shares, cw, ch, work, cancellationToken);
            Parallel.For(0, ch, parallel, j => Prefilter(coefficients.AsSpan(j * cw, cw)));
            Parallel.For(0, cw, parallel, () => new double[ch], (i, _, column) =>
            {
                for (int j = 0; j < ch; j++)
                {
                    column[j] = coefficients[(j * cw) + i];
                }

                Prefilter(column);
                for (int j = 0; j < ch; j++)
                {
                    coefficients[(j * cw) + i] = (float)column[j];
                }

                return column;
            }, _ => { });

            // Along x at the fine spacing, then along y, keeping each sample's largest and second largest field.
            int fx0 = x0 * scale, fw = cw * scale, fy0 = y0 * scale, fh = ch * scale;
            if (rows is null || rows.Length < ch * fw)
            {
                rows = new float[ch * fw];
            }

            float[] spline = rows;
            Parallel.For(0, ch, parallel, j =>
            {
                Span<float> row = coefficients.AsSpan(j * cw, cw);
                Span<float> output = spline.AsSpan(j * fw, fw);
                for (int f = 0; f < fw; f++)
                {
                    int fx = fx0 + f;
                    int b = fineBaseX[fx] - x0;
                    float v = 0;
                    for (int m = 0; m < 4; m++)
                    {
                        v += fineWeightX[(4 * fx) + m] * row[Mirror(b + m, cw)];
                    }

                    output[f] = v;
                }
            });

            Parallel.For(0, fh, parallel, g =>
            {
                int fy = fy0 + g;
                int b = fineBaseY[fy] - y0;
                Span<float> r0 = spline.AsSpan(Mirror(b, ch) * fw, fw), r1 = spline.AsSpan(Mirror(b + 1, ch) * fw, fw);
                Span<float> r2 = spline.AsSpan(Mirror(b + 2, ch) * fw, fw), r3 = spline.AsSpan(Mirror(b + 3, ch) * fw, fw);
                float w0 = fineWeightY[4 * fy], w1 = fineWeightY[(4 * fy) + 1], w2 = fineWeightY[(4 * fy) + 2], w3 = fineWeightY[(4 * fy) + 3];
                int at = (fy * fineWidth) + fx0;
                for (int f = 0; f < fw; f++)
                {
                    float v = (w0 * r0[f]) + (w1 * r1[f]) + (w2 * r2[f]) + (w3 * r3[f]);
                    int s = at + f;
                    if (v > best[s])
                    {
                        second[s] = best[s];
                        best[s] = v;
                        winner[s] = (byte)color;
                    }
                    else if (v > second[s])
                    {
                        second[s] = v;
                    }
                }
            });
        }

        // The lead over the next field; where no other color's field reaches, over a field far outside.
        Parallel.For(0, fineHeight, parallel, fy =>
        {
            for (int s = fy * fineWidth; s < (fy + 1) * fineWidth; s++)
            {
                best[s] = Math.Max(0, best[s] - Math.Max(second[s], -Reach));
            }
        });

        return (winner, best);
    }

    /// <summary>
    /// The signed distance, in pixels, from each pixel's centre to the edge of a color (positive inside), from the color's share of
    /// each pixel. Written from S. Gustavson and R. Strand, "Anti-aliased Euclidean distance transform" (Pattern Recognition
    /// Letters, 2011): at a pixel an edge crosses, its share and the direction the share grows in give the distance to a straight
    /// edge through it; other pixels take the distance to the edge line of the nearest such pixel, carried by two sweeps. The
    /// distance runs linearly across a straight edge, so the spline through it, unlike the spline through the shares, is zero
    /// exactly on the edge wherever the edge cuts the pixels.
    /// </summary>
    internal static float[] Distances(float[] share, int width, int height, CancellationToken cancellationToken)
    {
        return Distances(share, width, height, new DistanceWork(), cancellationToken)[..(width * height)];
    }

    /// <summary>
    /// <see cref="Distances(float[], int, int, CancellationToken)"/> in <paramref name="work"/>'s buffers, which are reused from
    /// call to call; the distances are the first <paramref name="width"/> × <paramref name="height"/> values of the array returned.
    /// </summary>
    internal static float[] Distances(float[] share, int width, int height, DistanceWork work, CancellationToken cancellationToken)
    {
        const float Low = 1 / 510f, High = 1 - Low;
        int n = width * height;
        work.Ensure(n);
        float[] distance = work.Distance;
        int[] nearest = work.Nearest, nearX = work.NearX, nearY = work.NearY;
        float[] ex = work.Ex, ey = work.Ey, ux = work.Ux, uy = work.Uy;
        Array.Fill(nearest, -1, 0, n);
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, height, parallel, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                float a = share[i];
                bool edge = a > Low && a < High;
                if (!edge)
                {
                    // A covered pixel beside an uncovered one: a hard edge runs between them.
                    bool full = a >= High;
                    edge = (x > 0 && Opposite(full, share[i - 1])) || (x + 1 < width && Opposite(full, share[i + 1]))
                        || (y > 0 && Opposite(full, share[i - width])) || (y + 1 < height && Opposite(full, share[i + width]));
                }

                if (!edge)
                {
                    continue;
                }

                // The direction the share grows in, from a 3 by 3 gradient with √2 weights across the middle.
                const float Root2 = 1.41421356f;
                float gx = At(share, width, height, x + 1, y - 1) - At(share, width, height, x - 1, y - 1)
                    + (Root2 * (At(share, width, height, x + 1, y) - At(share, width, height, x - 1, y)))
                    + At(share, width, height, x + 1, y + 1) - At(share, width, height, x - 1, y + 1);
                float gy = At(share, width, height, x - 1, y + 1) - At(share, width, height, x - 1, y - 1)
                    + (Root2 * (At(share, width, height, x, y + 1) - At(share, width, height, x, y - 1)))
                    + At(share, width, height, x + 1, y + 1) - At(share, width, height, x + 1, y - 1);
                float length = MathF.Sqrt((gx * gx) + (gy * gy));
                (gx, gy) = length > 1e-6f ? (gx / length, gy / length) : (1, 0);

                // The distance from the centre to the edge, which lies toward the growing share when the pixel is less than half
                // covered, and the edge line's nearest point.
                float d = EdgeDistance(Math.Clamp(a, 0, 1), Math.Abs(gx), Math.Abs(gy));
                nearest[i] = i;
                nearX[i] = x;
                nearY[i] = y;
                ux[i] = gx;
                uy[i] = gy;
                ex[i] = x + (d * gx);
                ey[i] = y + (d * gy);
                distance[i] = Math.Clamp(-d, -Reach, Reach);
            }
        });

        // Two sweeps carry each pixel's nearest edge pixel to its neighbours, forward then back; each pixel keeps its nearest edge
        // pixel's place, so no distance needs a division.
        cancellationToken.ThrowIfCancellationRequested();
        Sweep(nearest, nearX, nearY, width, height, forward: true);
        cancellationToken.ThrowIfCancellationRequested();
        Sweep(nearest, nearX, nearY, width, height, forward: false);
        Parallel.For(0, height, parallel, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                int seed = nearest[i];
                if (seed == i)
                {
                    continue;
                }

                bool inside = share[i] >= 0.5f;
                if (seed < 0 || Square(x - nearX[i], y - nearY[i]) > (Reach + 1) * (Reach + 1))
                {
                    distance[i] = inside ? Reach : -Reach;
                    continue;
                }

                // The distance to the edge line through the nearest edge pixel, on the side the pixel's own share says.
                float d = (ux[seed] * (x - ex[seed])) + (uy[seed] * (y - ey[seed]));
                distance[i] = inside ? Math.Clamp(d, 0.5f, Reach) : Math.Clamp(d, -Reach, -0.5f);
            }
        });

        return distance;
    }

    /// <summary>
    /// One sweep of the distance transform: each pixel takes the nearest of its own edge pixel and those of the four neighbours
    /// already swept (left, upper left, up and upper right going forward; the mirror going back).
    /// </summary>
    private static void Sweep(int[] nearest, int[] nearX, int[] nearY, int width, int height, bool forward)
    {
        int sign = forward ? 1 : -1;
        for (int row = 0; row < height; row++)
        {
            int y = forward ? row : height - 1 - row;
            int yy = y - sign;
            bool hasRow = (uint)yy < (uint)height;
            for (int column = 0; column < width; column++)
            {
                int x = forward ? column : width - 1 - column;
                int i = (y * width) + x;
                int best = nearest[i];
                if (best == i)
                {
                    continue;
                }

                int bestX = 0, bestY = 0;
                float bestDistance = float.MaxValue;
                if (best >= 0)
                {
                    (bestX, bestY) = (nearX[i], nearY[i]);
                    bestDistance = Square(x - bestX, y - bestY);
                }

                // The neighbours in the order (−1, 0), (−1, −1), (0, −1), (1, −1), times the sweep's sign.
                int xl = x - sign;
                if ((uint)xl < (uint)width)
                {
                    Consider(nearest, nearX, nearY, (y * width) + xl, x, y, ref best, ref bestX, ref bestY, ref bestDistance);
                }

                if (hasRow)
                {
                    if ((uint)xl < (uint)width)
                    {
                        Consider(nearest, nearX, nearY, (yy * width) + xl, x, y, ref best, ref bestX, ref bestY, ref bestDistance);
                    }

                    Consider(nearest, nearX, nearY, (yy * width) + x, x, y, ref best, ref bestX, ref bestY, ref bestDistance);
                    int xr = x + sign;
                    if ((uint)xr < (uint)width)
                    {
                        Consider(nearest, nearX, nearY, (yy * width) + xr, x, y, ref best, ref bestX, ref bestY, ref bestDistance);
                    }
                }

                nearest[i] = best;
                nearX[i] = bestX;
                nearY[i] = bestY;
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void Consider(int[] nearest, int[] nearX, int[] nearY, int j, int x, int y, ref int best, ref int bestX, ref int bestY, ref float bestDistance)
    {
        int candidate = nearest[j];
        if (candidate >= 0)
        {
            int cx = nearX[j], cy = nearY[j];
            float d = Square(x - cx, y - cy);
            if (d < bestDistance)
            {
                (best, bestX, bestY, bestDistance) = (candidate, cx, cy, d);
            }
        }
    }

    /// <summary>
    /// The distance from a pixel's centre to a straight edge that leaves share <paramref name="a"/> of the pixel on one side, along
    /// the edge's normal (whose components are <paramref name="gx"/> and <paramref name="gy"/>, unsigned, of unit length): positive
    /// when the centre lies on the other side, less than half covered. Gustavson and Strand (2011), for the unit square cut by a
    /// line.
    /// </summary>
    internal static float EdgeDistance(float a, float gx, float gy)
    {
        if (gx < gy)
        {
            (gx, gy) = (gy, gx);
        }

        if (gy < 1e-6f)
        {
            return 0.5f - a;
        }

        float a1 = 0.5f * gy / gx;
        if (a < a1)
        {
            return (0.5f * (gx + gy)) - MathF.Sqrt(2 * gx * gy * a);
        }

        if (a < 1 - a1)
        {
            return (0.5f - a) * gx;
        }

        return (-0.5f * (gx + gy)) + MathF.Sqrt(2 * gx * gy * (1 - a));
    }

    /// <summary>
    /// Turns samples into the coefficients of the cubic B-spline through them, in place: a gain of 6, then one causal and one
    /// anticausal first-order recursion with the pole √3 − 2, each started as if the samples were mirrored at the ends.
    /// </summary>
    internal static void Prefilter(Span<float> c)
    {
        int n = c.Length;
        if (n < 2)
        {
            return;
        }

        double[] d = new double[n];
        for (int k = 0; k < n; k++)
        {
            d[k] = 6.0 * c[k];
        }

        Run(d, Pole);
        for (int k = 0; k < n; k++)
        {
            c[k] = (float)d[k];
        }
    }

    /// <summary>The same prefilter on doubles.</summary>
    internal static void Prefilter(Span<double> c)
    {
        int n = c.Length;
        if (n < 2)
        {
            return;
        }

        for (int k = 0; k < n; k++)
        {
            c[k] *= 6.0;
        }

        Run(c, Pole);
    }

    /// <summary>The value of the centred cubic B-spline at x.</summary>
    internal static double Beta3(double x)
    {
        x = Math.Abs(x);
        if (x < 1)
        {
            return (2.0 / 3) - (x * x) + (x * x * x / 2);
        }

        if (x < 2)
        {
            double t = 2 - x;
            return t * t * t / 6;
        }

        return 0;
    }

    private static float At(float[] share, int width, int height, int x, int y)
    {
        return share[(Math.Clamp(y, 0, height - 1) * width) + Math.Clamp(x, 0, width - 1)];
    }

    /// <summary>Whether a neighbour's share is the opposite extreme: empty beside a full pixel, full beside an empty one.</summary>
    private static bool Opposite(bool full, float neighbour) => full ? neighbour <= 1 / 510f : neighbour >= 1 - (1 / 510f);

    private static float Square(int dx, int dy)
    {
        float fx = dx, fy = dy;
        return (fx * fx) + (fy * fy);
    }

    /// <summary>
    /// For each fine sample along an axis, the first of the four pixels its value comes from and their weights. A fine sample's
    /// centre lies at (f + ½) / scale pixels; pixel i's centre at i + ½.
    /// </summary>
    private static (int[] Base, float[] Weights) Taps(int fine, int scale)
    {
        int[] first = new int[fine];
        float[] weights = new float[4 * fine];
        for (int f = 0; f < fine; f++)
        {
            double u = ((f + 0.5) / scale) - 0.5;
            int i = (int)Math.Floor(u);
            first[f] = i - 1;
            for (int m = 0; m < 4; m++)
            {
                weights[(4 * f) + m] = (float)Beta3(u - (i - 1 + m));
            }
        }

        return (first, weights);
    }

    /// <summary>An index reflected into 0 to n − 1 without repeating the end samples (whole-sample mirroring).</summary>
    private static int Mirror(int i, int n)
    {
        if (n == 1)
        {
            return 0;
        }

        int period = 2 * (n - 1);
        i %= period;
        if (i < 0)
        {
            i += period;
        }

        return i < n ? i : period - i;
    }

    private static void Run(Span<double> c, double z)
    {
        int n = c.Length;

        // The causal recursion, started with the mirrored sum (cut off where the pole's powers vanish).
        int horizon = Math.Min(n, (int)Math.Ceiling(Math.Log(1e-9) / Math.Log(Math.Abs(z))));
        double sum = c[0], zk = z;
        if (horizon < n)
        {
            for (int k = 1; k < horizon; k++)
            {
                sum += zk * c[k];
                zk *= z;
            }
        }
        else
        {
            double iz = 1 / z, z2n = Math.Pow(z, n - 1);
            sum += z2n * c[n - 1];
            z2n *= z2n * iz;
            for (int k = 1; k < n - 1; k++)
            {
                sum += (zk + z2n) * c[k];
                zk *= z;
                z2n *= iz;
            }

            sum /= 1 - (zk * zk);
        }

        c[0] = sum;
        for (int k = 1; k < n; k++)
        {
            c[k] += z * c[k - 1];
        }

        // The anticausal recursion, started from the mirrored end.
        c[n - 1] = z / ((z * z) - 1) * ((z * c[n - 2]) + c[n - 1]);
        for (int k = n - 2; k >= 0; k--)
        {
            c[k] = z * (c[k + 1] - c[k]);
        }
    }

    /// <summary>The distance transform's buffers, kept from color to color.</summary>
    internal sealed class DistanceWork
    {
        public float[] Distance { get; private set; } = [];

        public int[] Nearest { get; private set; } = [];

        public int[] NearX { get; private set; } = [];

        public int[] NearY { get; private set; } = [];

        public float[] Ex { get; private set; } = [];

        public float[] Ey { get; private set; } = [];

        public float[] Ux { get; private set; } = [];

        public float[] Uy { get; private set; } = [];

        /// <summary>Makes every buffer hold at least <paramref name="n"/> values.</summary>
        public void Ensure(int n)
        {
            if (Distance.Length >= n)
            {
                return;
            }

            Distance = new float[n];
            Nearest = new int[n];
            NearX = new int[n];
            NearY = new int[n];
            Ex = new float[n];
            Ey = new float[n];
            Ux = new float[n];
            Uy = new float[n];
        }
    }
}
