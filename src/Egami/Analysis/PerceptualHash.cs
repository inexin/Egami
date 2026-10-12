using System.Buffers;
using System.Numerics;

namespace Egami.Analysis;

/// <summary>
/// 64-bit perceptual hashes: images that look alike get hashes a few bits apart, however they were compressed or resized, so the
/// <see cref="Distance"/> between two hashes says how alike two pictures are. Store the hashes as keys to find near-duplicates.
/// </summary>
/// <remarks>
/// Both hashes look at the first frame's brightness only, after flattening it onto white. The bits are in reading order, the
/// first in the most significant bit, so <c>hash.ToString("x16")</c> matches the hex string of Python's imagehash.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="hashes"/>
/// </example>
public static class PerceptualHash
{
    /// <summary>
    /// Returns the DCT hash (pHash style; Zauner 2010): the image's brightness reduced to 32 × 32, a 2D DCT-II, and one bit
    /// per coefficient of the top-left 8 × 8, set when it is above the median of those coefficients without the DC term. It
    /// survives recompression, resizing and small brightness changes; pairs of unrelated photos are about 32 bits apart.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static ulong Dct(Raster image)
    {
        ArgumentNullException.ThrowIfNull(image);
        double[] luma = Luma(image, 32, 32);
        double[] cos = new double[8 * 32];
        for (int k = 0; k < 8; k++)
        {
            for (int n = 0; n < 32; n++)
            {
                cos[(k * 32) + n] = Math.Cos(Math.PI * k * ((2 * n) + 1) / 64);
            }
        }

        // Down the columns for the first 8 frequencies, then across the rows.
        double[] columns = new double[8 * 32];
        for (int k = 0; k < 8; k++)
        {
            for (int x = 0; x < 32; x++)
            {
                double sum = 0;
                for (int y = 0; y < 32; y++)
                {
                    sum += luma[(y * 32) + x] * cos[(k * 32) + y];
                }

                columns[(k * 32) + x] = sum;
            }
        }

        double[] coefficients = new double[64];
        for (int ky = 0; ky < 8; ky++)
        {
            for (int kx = 0; kx < 8; kx++)
            {
                double sum = 0;
                for (int x = 0; x < 32; x++)
                {
                    sum += columns[(ky * 32) + x] * cos[(kx * 32) + x];
                }

                // Rounded to 9 decimals, so a last-bit difference in Math.Cos between machines cannot flip a bit.
                coefficients[(ky * 8) + kx] = Math.Round(sum, 9);
            }
        }

        double[] ac = coefficients[1..];
        Array.Sort(ac);
        double median = ac[ac.Length / 2];
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            hash = (hash << 1) | (coefficients[i] > median ? 1UL : 0);
        }

        return hash;
    }

    /// <summary>
    /// Returns the difference hash (dHash; Krawetz 2013): the image's brightness reduced to 9 × 8, and one bit per pair of
    /// horizontal neighbours, set when the right one is brighter. Faster than <see cref="Dct"/> and a little less robust.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static ulong Difference(Raster image)
    {
        ArgumentNullException.ThrowIfNull(image);
        double[] luma = Luma(image, 9, 8);
        ulong hash = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                hash = (hash << 1) | (Math.Round(luma[(y * 9) + x + 1], 9) > Math.Round(luma[(y * 9) + x], 9) ? 1UL : 0);
            }
        }

        return hash;
    }

    /// <summary>Returns the number of bits in which two hashes differ (their Hamming distance), 0 to 64.</summary>
    /// <param name="a">One hash.</param>
    /// <param name="b">The other.</param>
    /// <returns>The distance: 0 to about 10 for the same picture, around 32 for unrelated ones.</returns>
    public static int Distance(ulong a, ulong b)
    {
        return BitOperations.PopCount(a ^ b);
    }

    /// <summary>
    /// Returns the first frame's brightness reduced to <paramref name="columns"/> × <paramref name="rows"/> as whole levels 0 to
    /// 255, the way Python's imagehash prepares an image: flattened onto white, 8-bit BT.601 luma, then a Lanczos-3 reduction
    /// across and then down, each rounded to whole levels. Everything is integer or double arithmetic in a fixed order, so the
    /// result is the same on every machine.
    /// </summary>
    internal static double[] Luma(Raster image, int columns, int rows)
    {
        int width = image.Width, height = image.Height;
        (int First, double[] Weights)[] spansX = Spans(width, columns);
        (int First, double[] Weights)[] spansY = Spans(height, rows);
        double[] across = new double[columns * height];
        RgbaD[] buffer = ArrayPool<RgbaD>.Shared.Rent(width);
        int[] row = ArrayPool<int>.Shared.Rent(width);
        using RowReader reader = new(image, 0, width);
        try
        {
            for (int y = 0; y < height; y++)
            {
                reader.Read(y, 0, buffer.AsSpan(0, width));
                for (int x = 0; x < width; x++)
                {
                    RgbaD p = buffer[x];
                    double white = 1 - p.A;
                    int r = (int)Math.Round(255 * ((p.R * p.A) + white));
                    int g = (int)Math.Round(255 * ((p.G * p.A) + white));
                    int b = (int)Math.Round(255 * ((p.B * p.A) + white));
                    row[x] = ((r * 19595) + (g * 38470) + (b * 7471) + 0x8000) >> 16;
                }

                for (int i = 0; i < columns; i++)
                {
                    (int first, double[] weights) = spansX[i];
                    double sum = 0;
                    for (int k = 0; k < weights.Length; k++)
                    {
                        sum += row[first + k] * weights[k];
                    }

                    across[(i * height) + y] = Level(sum);
                }
            }
        }
        finally
        {
            ArrayPool<RgbaD>.Shared.Return(buffer);
            ArrayPool<int>.Shared.Return(row);
        }

        double[] result = new double[columns * rows];
        for (int j = 0; j < rows; j++)
        {
            (int first, double[] weights) = spansY[j];
            for (int i = 0; i < columns; i++)
            {
                double sum = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    sum += across[(i * height) + first + k] * weights[k];
                }

                result[(j * columns) + i] = Level(sum);
            }
        }

        return result;
    }

    private static double Level(double value) => Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>
    /// For each of <paramref name="count"/> outputs over <paramref name="length"/> inputs, the inputs a Lanczos-3 window stretched
    /// by the reduction factor covers and their weights, summing to 1. Output i is centered on input position (i + ½) × factor.
    /// </summary>
    private static (int First, double[] Weights)[] Spans(int length, int count)
    {
        double scale = (double)length / count;
        double stretch = Math.Max(scale, 1);
        double support = 3 * stretch;
        (int, double[])[] spans = new (int, double[])[count];
        for (int i = 0; i < count; i++)
        {
            double center = (i + 0.5) * scale;
            int first = Math.Max(0, (int)(center - support + 0.5));
            int last = Math.Min(length, (int)(center + support + 0.5));
            double[] weights = new double[last - first];
            double sum = 0;
            for (int p = first; p < last; p++)
            {
                double w = Lanczos3((p - center + 0.5) / stretch);
                weights[p - first] = w;
                sum += w;
            }

            for (int k = 0; k < weights.Length; k++)
            {
                weights[k] /= sum;
            }

            spans[i] = (first, weights);
        }

        return spans;
    }

    private static double Lanczos3(double x)
    {
        if (x == 0)
        {
            return 1;
        }

        if (x <= -3 || x >= 3)
        {
            return 0;
        }

        double px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }
}
