using System.Buffers;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Automatic lateral chromatic aberration correction for files without lens data. Lateral CA makes red and blue images slightly
/// larger or smaller than green around the optical centre, so the correction is a radial scale of each: for red and blue, the
/// scale that best lines their edge detail up with green's (the highest normalized correlation of detail, the image minus its
/// local mean, at strong edges away from the centre) is found by a search over ±0.3 % and a parabola through the best three,
/// then the channel is resampled at that scale. An image with no clear edges, or a scale within 0.02 % of 1, is left alone.
/// </summary>
internal static class RawAutoCa
{
    private const int Radius = 4;
    private const int Steps = 30;
    private const double Step = 0.0001;
    private const int MaxPoints = 50_000;

    /// <summary>Corrects the plane in place; returns the scales of red and blue it applied, or <see langword="null"/>.</summary>
    public static (double Red, double Blue)? Apply(Memory<float> rgb, int width, int height, ParallelOptions parallel)
    {
        if (width < 64 || height < 64)
        {
            return null;
        }

        int count = width * height * 3;
        float[] detail = ArrayPool<float>.Shared.Rent(count);
        float[] work = ArrayPool<float>.Shared.Rent(count);
        try
        {
            return Correct(rgb, width, height, parallel, detail, work);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(detail);
            ArrayPool<float>.Shared.Return(work);
        }
    }

    private static (double Red, double Blue)? Correct(Memory<float> rgb, int width, int height, ParallelOptions parallel, float[] detail, float[] work)
    {
        Detail(rgb.Span, width, height, parallel, detail, work);
        double cx = width / 2.0, cy = height / 2.0, far = Math.Sqrt((cx * cx) + (cy * cy));
        int[] points = ArrayPool<int>.Shared.Rent(MaxPoints * 2);
        double red, blue;
        try
        {
            int count = Edges(detail, width, height, cx, cy, far, points);
            if (count < 200)
            {
                return null;
            }

            red = Search(detail, width, height, points.AsSpan(0, count * 2), 0, cx, cy);
            blue = Search(detail, width, height, points.AsSpan(0, count * 2), 2, cx, cy);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(points);
        }

        bool fixRed = Math.Abs(red - 1) >= 0.0002, fixBlue = Math.Abs(blue - 1) >= 0.0002;
        if (!fixRed && !fixBlue)
        {
            return null;
        }

        float[] source = work;
        rgb.Span[..(width * height * 3)].CopyTo(source);
        RawParallel.For(0, height, parallel, y =>
        {
            Span<float> row = rgb.Span.Slice(y * width * 3, width * 3);
            for (int x = 0; x < width; x++)
            {
                if (fixRed)
                {
                    row[x * 3] = Sample(source, width, height, 0, cx + ((x + 0.5 - cx) * red) - 0.5, cy + ((y + 0.5 - cy) * red) - 0.5);
                }

                if (fixBlue)
                {
                    row[(x * 3) + 2] = Sample(source, width, height, 2, cx + ((x + 0.5 - cx) * blue) - 0.5, cy + ((y + 0.5 - cy) * blue) - 0.5);
                }
            }
        });
        return (fixRed ? red : 1, fixBlue ? blue : 1);
    }

    /// <summary>Returns each channel minus its local mean (a (2 × Radius + 1)² box), interleaved like the plane.</summary>
    private static void Detail(ReadOnlySpan<float> rgb, int width, int height, ParallelOptions parallel, float[] detail, float[] across)
    {
        int count = width * height * 3;
        float[] source = ArrayPool<float>.Shared.Rent(count);
        rgb[..count].CopyTo(source);
        RawParallel.For(0, height, parallel, y =>
        {
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int x = -Radius; x <= Radius; x++)
                {
                    sum += source[(((y * width) + Math.Clamp(x, 0, width - 1)) * 3) + c];
                }

                for (int x = 0; x < width; x++)
                {
                    across[(((y * width) + x) * 3) + c] = (float)(sum / ((2 * Radius) + 1));
                    sum += source[(((y * width) + Math.Min(x + Radius + 1, width - 1)) * 3) + c] - source[(((y * width) + Math.Max(x - Radius, 0)) * 3) + c];
                }
            }
        });
        RawParallel.For(0, height, parallel, y =>
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0;
                    for (int dy = -Radius; dy <= Radius; dy++)
                    {
                        sum += across[(((Math.Clamp(y + dy, 0, height - 1) * width) + x) * 3) + c];
                    }

                    int i = (((y * width) + x) * 3) + c;
                    detail[i] = source[i] - (float)(sum / ((2 * Radius) + 1));
                }
            }
        });
        ArrayPool<float>.Shared.Return(source);
    }

    /// <summary>
    /// Picks sample points into <paramref name="points"/> (x, y pairs): on a grid, beyond 30 % of the way to the corners, where
    /// green's detail is in the strongest tenth (a threshold from a histogram, so memory does not grow with the image), at most
    /// <see cref="MaxPoints"/>. Returns how many.
    /// </summary>
    private static int Edges(float[] detail, int width, int height, double cx, double cy, double far, int[] points)
    {
        int stride = Math.Max(2, (int)Math.Sqrt((double)width * height / 400_000));
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        float top = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            float threshold = 0;
            if (pass == 2)
            {
                // The strength above which a tenth of the candidates lie.
                int total = 0;
                foreach (int h in histogram)
                {
                    total += h;
                }

                int seen = 0, bin = 255;
                while (bin > 0 && seen + histogram[bin] < total / 10)
                {
                    seen += histogram[bin--];
                }

                threshold = Math.Max(1e-4f, bin * top / 256);
            }

            int count = 0;
            for (int y = Radius + 8; y < height - Radius - 8; y += stride)
            {
                for (int x = Radius + 8; x < width - Radius - 8; x += stride)
                {
                    double r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                    if (r < far * 0.3)
                    {
                        continue;
                    }

                    float strength = MathF.Abs(detail[(((y * width) + x) * 3) + 1]);
                    if (pass == 0)
                    {
                        top = MathF.Max(top, strength);
                    }
                    else if (pass == 1)
                    {
                        histogram[Math.Min(255, (int)(strength / top * 256))]++;
                    }
                    else if (strength > threshold && count < MaxPoints)
                    {
                        points[count * 2] = x;
                        points[(count * 2) + 1] = y;
                        count++;
                    }
                }
            }

            if (pass == 0 && top <= 0)
            {
                return 0;
            }

            if (pass == 2)
            {
                return count;
            }
        }

        return 0;
    }

    private static double Search(float[] detail, int width, int height, ReadOnlySpan<int> points, int channel, double cx, double cy)
    {
        Span<double> score = stackalloc double[(2 * Steps) + 1];
        for (int k = -Steps; k <= Steps; k++)
        {
            double s = 1 + (k * Step);
            double cross = 0, a2 = 0, b2 = 0;
            for (int i = 0; i < points.Length; i += 2)
            {
                int x = points[i], y = points[i + 1];
                double g = detail[(((y * width) + x) * 3) + 1];
                double c = Sample(detail, width, height, channel, cx + ((x + 0.5 - cx) * s) - 0.5, cy + ((y + 0.5 - cy) * s) - 0.5);
                cross += g * c;
                a2 += g * g;
                b2 += c * c;
            }

            score[k + Steps] = a2 > 0 && b2 > 0 ? cross / Math.Sqrt(a2 * b2) : 0;
        }

        int best = 0;
        for (int i = 1; i < score.Length; i++)
        {
            if (score[i] > score[best])
            {
                best = i;
            }
        }

        double offset = 0;
        if (best > 0 && best < score.Length - 1)
        {
            double l = score[best - 1], m = score[best], r = score[best + 1];
            double d = l - (2 * m) + r;
            offset = d < 0 ? Math.Clamp(0.5 * (l - r) / d, -0.5, 0.5) : 0;
        }

        return 1 + ((best - Steps + offset) * Step);
    }

    private static float Sample(float[] s, int w, int h, int c, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = s[(((y0 * w) + x0) * 3) + c], b = s[(((y0 * w) + x1) * 3) + c];
        float d = s[(((y1 * w) + x0) * 3) + c], e = s[(((y1 * w) + x1) * 3) + c];
        return (((a * (1 - tx)) + (b * tx)) * (1 - ty)) + (((d * (1 - tx)) + (e * tx)) * ty);
    }
}
