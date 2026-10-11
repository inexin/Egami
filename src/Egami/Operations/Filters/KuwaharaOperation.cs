using System.Buffers;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// The Kuwahara filter (Kuwahara et al., 1976) as ImageMagick's -kuwahara defines it: of the four (r + 1)² squares that have the
/// pixel as a corner, the one whose luma (0.212656 R + 0.715158 G + 0.072186 B) varies least wins, the first in the order
/// top-left, top-right, bottom-left, bottom-right on a tie, and the pixel takes the image's value sampled bilinearly at that
/// square's corner plus half its side. With σ &gt; 0 everything happens on the image blurred first with ImageMagick's kernel for
/// radius × σ. Luma is held in fixed point (1/65536) and summed in whole numbers in
/// summed-area tables built per band, so the cost per pixel does not depend on the radius and the comparisons, ties included,
/// do not depend on where a band starts. Works on premultiplied working floats, edges repeating the edge pixel.
/// </summary>
internal sealed class KuwaharaOperation(int radius, float sigma) : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    /// <summary>The largest radius.</summary>
    internal const int MaxRadius = 100;

    private const float LumaR = 0.212656f, LumaG = 0.715158f, LumaB = 0.072186f;

    /// <summary>Luma's fixed-point scale: 2²⁰, so sums of squares over a band fit a long.</summary>
    private const double Fixed = 1 << 20;

    private readonly float[] _weights = sigma > 0 ? BlurWeights(radius, sigma) : [1];

    public (int X, int Y)? Reach => (radius + 1 + (_weights.Length / 2), radius + 1 + (_weights.Length / 2));

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int r = radius, q = r + 1, pad = r + 1, br = _weights.Length / 2, width = source.Width, height = source.Height;
        int stride = width + (2 * pad);
        float[] weights = _weights;
        RowConverter<TPixel> converter = new(null);

        // PremulBgra8 rows load as they are stored, blue first.
        bool bgr = typeof(TPixel) == typeof(PremulBgra8);
        float lumaR = bgr ? LumaB : LumaR, lumaB = bgr ? LumaR : LumaB;
        RowRunner.Run(height, (long)width * 16, input.Context.MaxParallelism, (start, end) =>
        {
            // The image rows the band reads, the blurred image clamped at the edges as ImageMagick reads it past them.
            int rows = end - start + (2 * pad);
            int low = Math.Clamp(start - pad, 0, height - 1), high = Math.Clamp(end - 1 + pad, 0, height - 1);
            int acrossRows = high - low + 1 + (2 * br), line = width + (2 * br);
            float[] pixels = ArrayPool<float>.Shared.Rent((line * 4) + (acrossRows * width * 4) + (rows * stride * 4));
            long[] tables = ArrayPool<long>.Shared.Rent(2 * (rows + 1) * (stride + 1));
            float[] stored = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                // Blurred across, then down, each value summed in a fixed order, so every band gives the same values.
                bool opaque = true;
                Span<float> row = pixels.AsSpan(0, line * 4);
                Span<float> across = pixels.AsSpan(line * 4, acrossRows * width * 4);
                Span<float> plane = pixels.AsSpan((line * 4) + (acrossRows * width * 4), rows * stride * 4);
                for (int j = 0; j < acrossRows; j++)
                {
                    int y = Math.Clamp(low - br + j, 0, height - 1);
                    opaque &= converter.Load(source.Row(y), row[(br * 4)..]);
                    Repeat(row, br, width);
                    Span<float> a = across.Slice(j * width * 4, width * 4);
                    for (int i = 0; i < width * 4; i++)
                    {
                        float sum = 0;
                        for (int k = 0; k < weights.Length; k++)
                        {
                            sum += weights[k] * row[i + (k * 4)];
                        }

                        a[i] = sum;
                    }
                }

                for (int j = 0; j < rows; j++)
                {
                    int y = Math.Clamp(start - pad + j, 0, height - 1) - low;
                    Span<float> p = plane.Slice(j * stride * 4, stride * 4);
                    Span<float> inside = p.Slice(pad * 4, width * 4);
                    for (int i = 0; i < width * 4; i++)
                    {
                        float sum = 0;
                        for (int k = 0; k < weights.Length; k++)
                        {
                            sum += weights[k] * across[((y + k) * width * 4) + i];
                        }

                        inside[i] = sum;
                    }

                    Repeat(p, pad, width);
                }

                // Summed-area tables of fixed-point luma and its square, a row and column of zeros first.
                int ts = stride + 1;
                Span<long> sum1 = tables.AsSpan(0, (rows + 1) * ts), sum2 = tables.AsSpan((rows + 1) * ts, (rows + 1) * ts);
                sum1[..ts].Clear();
                sum2[..ts].Clear();
                for (int j = 0; j < rows; j++)
                {
                    ReadOnlySpan<float> blurred = plane.Slice(j * stride * 4, stride * 4);
                    int above = j * ts, here = (j + 1) * ts;
                    sum1[here] = 0;
                    sum2[here] = 0;
                    long s1 = 0, s2 = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        float luma = (lumaR * blurred[i * 4]) + (LumaG * blurred[(i * 4) + 1]) + (lumaB * blurred[(i * 4) + 2]);
                        long fixedLuma = (long)Math.Round(Math.Clamp(luma, 0, 1) * Fixed);
                        s1 += fixedLuma;
                        s2 += fixedLuma * fixedLuma;
                        sum1[here + i + 1] = sum1[above + i + 1] + s1;
                        sum2[here + i + 1] = sum2[above + i + 1] + s2;
                    }
                }

                long n = (long)q * q;
                Span<float> result = stored.AsSpan(0, width * 4);
                for (int y = start; y < end; y++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    int j0 = y - start + pad;
                    for (int x = 0; x < width; x++)
                    {
                        // n Σl² − (Σl)² is n² times the variance, in whole numbers.
                        int i0 = x + pad, best = 0;
                        Int128 least = Int128.MaxValue;
                        for (int k = 0; k < 4; k++)
                        {
                            int left = (k & 1) == 0 ? i0 - r : i0, top = (k & 2) == 0 ? j0 - r : j0;
                            long a = Box(sum1, ts, left, top, q), b = Box(sum2, ts, left, top, q);
                            Int128 spread = ((Int128)n * b) - ((Int128)a * a);
                            if (spread < least)
                            {
                                least = spread;
                                best = k;
                            }
                        }

                        float sx = ((best & 1) == 0 ? i0 - r : i0) + (q / 2f);
                        float sy = ((best & 2) == 0 ? j0 - r : j0) + (q / 2f);
                        Sample(plane, stride, sx, sy, result.Slice(x * 4, 4));
                    }

                    converter.Store(result, destination.Row(y), opaque);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(pixels);
                ArrayPool<long>.Shared.Return(tables);
                ArrayPool<float>.Shared.Return(stored);
            }
        }, input.CancellationToken);
    }

    /// <summary>
    /// ImageMagick's blur kernel for radius × σ: a Gaussian of σ sampled three times per pixel and summed into each, out to the
    /// radius, normalized.
    /// </summary>
    internal static float[] BlurWeights(int radius, float sigma)
    {
        const int Rank = 3;
        int width = (2 * radius) + 1, v = ((width * Rank) - 1) / 2;
        double[] w = new double[width];
        double s = sigma * Rank;
        for (int u = -v; u <= v; u++)
        {
            w[(u + v) / Rank] += Math.Exp(-(u * (double)u) / (2 * s * s));
        }

        double sum = w.Sum();
        return [.. w.Select(x => (float)(x / sum))];
    }

    /// <summary>Repeats a loaded row's first and last pixels into the <paramref name="pad"/> on each side.</summary>
    private static void Repeat(Span<float> row, int pad, int width)
    {
        for (int i = 0; i < pad; i++)
        {
            row.Slice(pad * 4, 4).CopyTo(row[(i * 4)..]);
            row.Slice((pad + width - 1) * 4, 4).CopyTo(row[((pad + width + i) * 4)..]);
        }
    }

    private static long Box(ReadOnlySpan<long> table, int ts, int left, int top, int size)
    {
        int a = (top * ts) + left, b = ((top + size) * ts) + left;
        return table[b + size] - table[b] - table[a + size] + table[a];
    }

    /// <summary>Samples the padded band bilinearly at (<paramref name="x"/>, <paramref name="y"/>) in its pixel indices.</summary>
    private static void Sample(ReadOnlySpan<float> plane, int stride, float x, float y, Span<float> pixel)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int x1 = fx > 0 ? x0 + 1 : x0, y1 = fy > 0 ? y0 + 1 : y0;
        ReadOnlySpan<float> top = plane.Slice(y0 * stride * 4, stride * 4), bottom = plane.Slice(y1 * stride * 4, stride * 4);
        for (int c = 0; c < 4; c++)
        {
            float t = top[(x0 * 4) + c] + (fx * (top[(x1 * 4) + c] - top[(x0 * 4) + c]));
            float b = bottom[(x0 * 4) + c] + (fx * (bottom[(x1 * 4) + c] - bottom[(x0 * 4) + c]));
            pixel[c] = t + (fy * (b - t));
        }
    }
}
