using System.Buffers;
using System.Numerics;

using Egami.Operations;

namespace Egami.Analysis;

/// <summary>
/// Structural similarity (Wang, Bovik, Sheikh and Simoncelli 2004) per channel: a Gaussian window (σ 1.5) gives each pixel the
/// local means, variances and covariance of the two images, SSIM = ((2μxμy + C1)(2σxy + C2)) / ((μx² + μy² + C1)(σx² + σy² + C2))
/// with C1 = 0.01² and C2 = 0.03² for values in 0..1, all in double precision (in float, E[x²] − μ² loses the small variances
/// of 16-bit noise), and the channel's SSIM is the mean over its pixels. Pixels outside the
/// image repeat the edge. The window runs as two separable passes: each band of rows filters its input rows across into a ring
/// of 2r + 1 rows, then down, so a band holds 2r + 1 filtered rows rather than whole planes.
/// </summary>
internal static class Ssim
{
    /// <summary>The window's σ.</summary>
    internal const double Sigma = 1.5;

    /// <summary>The window's radius: 11 × 11.</summary>
    internal const int Radius = 5;

    private const double C1 = 0.01 * 0.01;
    private const double C2 = 0.03 * 0.03;

    /// <summary>The five filtered quantities: x, y, x², y² and xy.</summary>
    private const int Planes = 5;

    /// <summary>Returns the mean SSIM of red, green, blue and alpha (1 for alpha when neither image has it).</summary>
    internal static double[] Compute(Raster a, Raster b, Rect region, int frame, bool premultiply, bool withAlpha, CancellationToken cancellationToken)
    {
        int width = region.Width, height = region.Height;
        int channels = withAlpha ? 4 : 3;
        double[] weights = Weights(Radius);
        const int bandRows = 64;
        int bands = (height + bandRows - 1) / bandRows;
        double[][] sums = new double[bands][];

        // 1,024 bytes a row gives bands of 64 rows (RowRunner's minimum is 16), so the 2r rows each band filters twice stay few.
        RowRunner.Run(height, 1024, a.Context.MaxParallelism, (start, end) =>
        {
            int window = (2 * Radius) + 1;
            int padded = width + (2 * Radius);
            int filteredLength = channels * Planes * width;
            double[] raw = ArrayPool<double>.Shared.Rent(channels * Planes * padded);
            double[] ring = ArrayPool<double>.Shared.Rent(window * filteredLength);
            RgbaD[] pa = ArrayPool<RgbaD>.Shared.Rent(width);
            RgbaD[] pb = ArrayPool<RgbaD>.Shared.Rent(width);
            using RowReader readA = new(a, frame, width);
            using RowReader readB = new(b, frame, width);
            double[] total = new double[4];
            try
            {
                // Rows start - r .. end + r - 1 (clamped to the image), each filtered across into its ring slot.
                int first = start - Radius;
                for (int v = first; v < end + Radius; v++)
                {
                    int y = Math.Clamp(v, 0, height - 1);
                    Span<RgbaD> ra = pa.AsSpan(0, width);
                    Span<RgbaD> rb = pb.AsSpan(0, width);
                    readA.Read(region.Y + y, region.X, ra);
                    readB.Read(region.Y + y, region.X, rb);
                    Fill(ra, rb, raw, channels, padded, premultiply);
                    int slot = (v - first) % window;
                    Across(raw, ring.AsSpan(slot * filteredLength, filteredLength), channels, width, padded, weights);
                    int output = v - Radius;
                    if (output >= start)
                    {
                        Down(ring, (output - Radius - first) % window, window, filteredLength, channels, width, weights, total);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                ArrayPool<double>.Shared.Return(raw);
                ArrayPool<double>.Shared.Return(ring);
                ArrayPool<RgbaD>.Shared.Return(pa);
                ArrayPool<RgbaD>.Shared.Return(pb);
            }

            sums[start / bandRows] = total;
        }, cancellationToken);

        double[] result = [0, 0, 0, channels == 4 ? 0 : 1];
        foreach (double[] band in sums)
        {
            for (int c = 0; c < channels; c++)
            {
                result[c] += band[c];
            }
        }

        long pixels = (long)width * height;
        for (int c = 0; c < channels; c++)
        {
            result[c] /= pixels;
        }

        return result;
    }

    /// <summary>Returns the normalized 1D Gaussian weights for offsets −r to r.</summary>
    internal static double[] Weights(int radius)
    {
        double[] w = new double[(2 * radius) + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            w[i + radius] = Math.Exp(-(i * i) / (2 * Sigma * Sigma));
            sum += w[i + radius];
        }

        return [.. w.Select(x => x / sum)];
    }

    /// <summary>Lays one row of both images out as x, y, x², y² and xy per channel, each padded by r repeated edge values.</summary>
    private static void Fill(ReadOnlySpan<RgbaD> ra, ReadOnlySpan<RgbaD> rb, double[] raw, int channels, int padded, bool premultiply)
    {
        int width = ra.Length;
        for (int c = 0; c < channels; c++)
        {
            Span<double> x = raw.AsSpan(((c * Planes) + 0) * padded, padded);
            Span<double> y = raw.AsSpan(((c * Planes) + 1) * padded, padded);
            Span<double> xx = raw.AsSpan(((c * Planes) + 2) * padded, padded);
            Span<double> yy = raw.AsSpan(((c * Planes) + 3) * padded, padded);
            Span<double> xy = raw.AsSpan(((c * Planes) + 4) * padded, padded);
            for (int i = 0; i < padded; i++)
            {
                int s = Math.Clamp(i - Radius, 0, width - 1);
                double p = Value(ra[s], c, premultiply);
                double q = Value(rb[s], c, premultiply);
                x[i] = p;
                y[i] = q;
                xx[i] = p * p;
                yy[i] = q * q;
                xy[i] = p * q;
            }
        }
    }

    private static double Value(RgbaD pixel, int channel, bool premultiply)
    {
        double alpha = premultiply ? pixel.A : 1;
        return channel switch
        {
            0 => pixel.R * alpha,
            1 => pixel.G * alpha,
            2 => pixel.B * alpha,
            _ => pixel.A,
        };
    }

    /// <summary>Filters each padded plane across into <paramref name="filtered"/>, one plane of width values after another.</summary>
    private static void Across(double[] raw, Span<double> filtered, int channels, int width, int padded, double[] weights)
    {
        int planes = channels * Planes;
        int lanes = Vector<double>.Count;
        for (int p = 0; p < planes; p++)
        {
            ReadOnlySpan<double> source = raw.AsSpan(p * padded, padded);
            Span<double> target = filtered.Slice(p * width, width);
            int x = 0;
            for (; x <= width - lanes; x += lanes)
            {
                Vector<double> sum = new Vector<double>(source[x..]) * weights[0];
                for (int k = 1; k < weights.Length; k++)
                {
                    sum += new Vector<double>(source[(x + k)..]) * weights[k];
                }

                sum.CopyTo(target[x..]);
            }

            for (; x < width; x++)
            {
                double sum = source[x] * weights[0];
                for (int k = 1; k < weights.Length; k++)
                {
                    sum += source[x + k] * weights[k];
                }

                target[x] = sum;
            }
        }
    }

    /// <summary>
    /// Filters the ring's rows down for one output row (its window starts at ring slot <paramref name="oldest"/>) and adds each
    /// pixel's SSIM to <paramref name="total"/>.
    /// </summary>
    private static void Down(double[] ring, int oldest, int window, int length, int channels, int width, double[] weights, double[] total)
    {
        double[] row = ArrayPool<double>.Shared.Rent(Planes * width);
        try
        {
            int lanes = Vector<double>.Count;
            for (int c = 0; c < channels; c++)
            {
                for (int p = 0; p < Planes; p++)
                {
                    Span<double> target = row.AsSpan(p * width, width);
                    int offset = ((c * Planes) + p) * width;
                    int x = 0;
                    for (; x <= width - lanes; x += lanes)
                    {
                        Vector<double> sum = new Vector<double>(ring.AsSpan((oldest * length) + offset + x)) * weights[0];
                        for (int k = 1; k < window; k++)
                        {
                            int slot = (oldest + k) % window;
                            sum += new Vector<double>(ring.AsSpan((slot * length) + offset + x)) * weights[k];
                        }

                        sum.CopyTo(target[x..]);
                    }

                    for (; x < width; x++)
                    {
                        double sum = ring[(oldest * length) + offset + x] * weights[0];
                        for (int k = 1; k < window; k++)
                        {
                            sum += ring[(((oldest + k) % window) * length) + offset + x] * weights[k];
                        }

                        target[x] = sum;
                    }
                }

                double channelSum = 0;
                for (int x = 0; x < width; x++)
                {
                    double mx = row[x], my = row[width + x];
                    double vx = row[(2 * width) + x] - (mx * mx);
                    double vy = row[(3 * width) + x] - (my * my);
                    double cxy = row[(4 * width) + x] - (mx * my);
                    channelSum += ((2 * mx * my) + C1) * ((2 * cxy) + C2) / (((mx * mx) + (my * my) + C1) * (vx + vy + C2));
                }

                total[c] += channelSum;
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(row);
        }
    }
}
