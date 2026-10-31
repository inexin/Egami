using System.Buffers;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Noise reduction by wavelet shrinkage: the stationary ("à trous") wavelet transform with the B3-spline kernel (Starck and
/// Murtagh), with soft thresholds on the detail coefficients (Donoho and Johnstone). In the raw domain each CFA position is its
/// own plane, made to have unit noise by the generalized Anscombe transform (Starck, Murtagh and Bijaoui; Mäkitalo and Foi)
/// from a noise model, variance = S × signal + O, which DNG's NoiseProfile gives or which is measured from the image. After
/// demosaicing, chroma noise is shrunk in two colour-difference planes of square-root RGB, the luminance left alone.
/// </summary>
internal static class RawDenoise
{
    /// <summary>The standard deviation of unit white noise in each detail level of the B3-spline à trous transform.</summary>
    private static readonly float[] Attenuation = [0.8908f, 0.2007f, 0.0856f, 0.0413f, 0.0205f];

    /// <summary>Shrinks the noise of a mosaic, <paramref name="amount"/> 0 to 1, in the raw domain.</summary>
    public static void Mosaic(Memory<float> mosaic, int width, int height, RawCfa cfa, double[]? noiseProfile, double amount, ParallelOptions parallel)
    {
        int positions = cfa.Rows * cfa.Columns;
        float k = (float)(amount * 3);
        Parallel.For(0, positions, parallel, p =>
        {
            int py = p / cfa.Columns, px = p % cfa.Columns;
            int w = (width - px + cfa.Columns - 1) / cfa.Columns;
            int h = (height - py + cfa.Rows - 1) / cfa.Rows;
            if (w < 8 || h < 8)
            {
                return;
            }

            float[] plane = ArrayPool<float>.Shared.Rent(w * h);
            try
            {
                Span<float> m = mosaic.Span;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        plane[(y * w) + x] = m[((((y * cfa.Rows) + py) * width) + (x * cfa.Columns)) + px];
                    }
                }

                int color = cfa.ColorAt(px, py);
                (double s, double o) = Model(noiseProfile, color) ?? Estimate(plane.AsSpan(0, w * h), w, h);
                Anscombe(plane.AsSpan(0, w * h), s, o);
                Shrink(plane, w * h, w, h, k, 1, 5, parallel);
                InverseAnscombe(plane.AsSpan(0, w * h), s, o);
                m = mosaic.Span;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        m[((((y * cfa.Rows) + py) * width) + (x * cfa.Columns)) + px] = plane[(y * w) + x];
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(plane);
            }
        });
    }

    /// <summary>Shrinks the chroma noise of interleaved RGB, <paramref name="amount"/> 0 to 1.</summary>
    public static void Chroma(Memory<float> rgb, int width, int height, double amount, ParallelOptions parallel, EgamiInput input)
    {
        if (width < 8 || height < 8)
        {
            return;
        }

        int count = width * height;
        input.CheckMemory((long)count * 4 * 2);
        float[] c1 = ArrayPool<float>.Shared.Rent(count);
        float[] c2 = ArrayPool<float>.Shared.Rent(count);
        try
        {
            Parallel.For(0, height, parallel, y =>
            {
                ReadOnlySpan<float> row = rgb.Span.Slice(y * width * 3, width * 3);
                for (int x = 0; x < width; x++)
                {
                    float r = Root(row[x * 3]), g = Root(row[(x * 3) + 1]), b = Root(row[(x * 3) + 2]);
                    c1[(y * width) + x] = r - g;
                    c2[(y * width) + x] = b - g;
                }
            });

            float k = (float)(amount * 4);
            foreach (float[] plane in (float[][])[c1, c2])
            {
                float sigma = Sigma(plane.AsSpan(0, count), width, height);
                if (sigma > 0)
                {
                    Shrink(plane, count, width, height, k, sigma, 4, parallel);
                }
            }

            Parallel.For(0, height, parallel, y =>
            {
                Span<float> row = rgb.Span.Slice(y * width * 3, width * 3);
                for (int x = 0; x < width; x++)
                {
                    float r = Root(row[x * 3]), g = Root(row[(x * 3) + 1]), b = Root(row[(x * 3) + 2]);
                    float luma = (r + g + b) / 3;
                    float d1 = c1[(y * width) + x], d2 = c2[(y * width) + x];
                    float ng = luma - ((d1 + d2) / 3);
                    row[x * 3] = Square(d1 + ng);
                    row[(x * 3) + 1] = Square(ng);
                    row[(x * 3) + 2] = Square(d2 + ng);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(c1);
            ArrayPool<float>.Shared.Return(c2);
        }
    }

    private static float Root(float v) => v >= 0 ? MathF.Sqrt(v) : -MathF.Sqrt(-v);

    private static float Square(float v) => v >= 0 ? v * v : -(v * v);

    /// <summary>Returns the NoiseProfile's (S, O) for a colour plane: one pair for all, or one per plane.</summary>
    private static (double S, double O)? Model(double[]? profile, int color)
    {
        if (profile is null || profile.Length < 2)
        {
            return null;
        }

        int at = profile.Length >= 6 ? Math.Min(color, (profile.Length / 2) - 1) * 2 : 0;
        double s = profile[at], o = profile[at + 1];
        return s >= 0 && o >= 0 && s + o > 0 && double.IsFinite(s) && double.IsFinite(o) ? (s, o) : null;
    }

    /// <summary>
    /// Measures the noise model of a plane: the spread of neighbour differences (median absolute deviation) in bands of
    /// brightness, and a straight line through variance against brightness.
    /// </summary>
    private static (double S, double O) Estimate(ReadOnlySpan<float> plane, int w, int h)
    {
        // Neighbour differences in 16 bands of brightness, at most 8,192 per band from a grid over the plane.
        const int Bins = 16, PerBin = 8192;
        float[] diffs = ArrayPool<float>.Shared.Rent(Bins * PerBin);
        Span<int> counts = stackalloc int[Bins];
        counts.Clear();
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)w * h / (Bins * PerBin))));
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x + 1 < w; x += step)
            {
                float a = plane[(y * w) + x], b = plane[(y * w) + x + 1];
                float mean = (a + b) / 2;
                if (mean <= 0 || mean >= 0.95f)
                {
                    continue;
                }

                int bin = Math.Min(Bins - 1, (int)(mean * Bins));
                if (counts[bin] < PerBin)
                {
                    diffs[(bin * PerBin) + counts[bin]++] = MathF.Abs(a - b) / MathF.Sqrt(2);
                }
            }
        }

        double sx = 0, sy = 0, sxx = 0, sxy = 0, n = 0;
        for (int i = 0; i < Bins; i++)
        {
            if (counts[i] < 64)
            {
                continue;
            }

            Span<float> bin = diffs.AsSpan(i * PerBin, counts[i]);
            bin.Sort();
            double sigma = bin[counts[i] / 2] * 1.4826;
            double x = (i + 0.5) / Bins, v = sigma * sigma, wgt = counts[i];
            sx += wgt * x;
            sy += wgt * v;
            sxx += wgt * x * x;
            sxy += wgt * x * v;
            n += wgt;
        }

        ArrayPool<float>.Shared.Return(diffs);
        if (n == 0)
        {
            return (0, 1e-6);
        }

        double det = (n * sxx) - (sx * sx);
        double slope = det > 1e-12 ? ((n * sxy) - (sx * sy)) / det : 0;
        double offset = (sy - (slope * sx)) / n;
        return (Math.Max(slope, 0), Math.Max(offset, 1e-8));
    }

    private static void Anscombe(Span<float> plane, double s, double o)
    {
        if (s < 1e-9)
        {
            float scale = (float)(1 / Math.Sqrt(o));
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] *= scale;
            }

            return;
        }

        float fs = (float)s, add = (float)((0.375 * s * s) + o), k = (float)(2 / s);
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = k * MathF.Sqrt(MathF.Max((fs * plane[i]) + add, 0));
        }
    }

    private static void InverseAnscombe(Span<float> plane, double s, double o)
    {
        if (s < 1e-9)
        {
            float scale = (float)Math.Sqrt(o);
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] *= scale;
            }

            return;
        }

        float half = (float)(s / 2), sub = (float)((0.375 * s * s) + o), inv = (float)(1 / s);
        for (int i = 0; i < plane.Length; i++)
        {
            float y = MathF.Max(plane[i], 0) * half;
            plane[i] = ((y * y) - sub) * inv;
        }
    }

    /// <summary>Returns the noise of a plane from its finest detail level: the median absolute coefficient over 0.6745.</summary>
    private static float Sigma(ReadOnlySpan<float> plane, int w, int h)
    {
        // A grid of at most about 65,536 samples, so the cost and memory do not grow with the image.
        const int Samples = 1 << 16;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)w * h / Samples)));
        float[] values = ArrayPool<float>.Shared.Rent(Samples * 2);
        try
        {
            int count = 0;
            for (int y = 1; y + 1 < h; y += step)
            {
                for (int x = 1; x + 1 < w && count < values.Length; x += step)
                {
                    int i = (y * w) + x;
                    float smooth = ((4 * plane[i]) + (2 * (plane[i - 1] + plane[i + 1] + plane[i - w] + plane[i + w])) + plane[i - w - 1] + plane[i - w + 1] + plane[i + w - 1] + plane[i + w + 1]) / 16;
                    values[count++] = MathF.Abs(plane[i] - smooth);
                }
            }

            if (count == 0)
            {
                return 0;
            }

            Span<float> used = values.AsSpan(0, count);
            used.Sort();
            return used[count / 2] / 0.6745f / 0.8f;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(values);
        }
    }

    /// <summary>
    /// The à trous transform with soft thresholds: each level's details shrink by <paramref name="k"/> × <paramref name="sigma"/>
    /// × that level's noise attenuation, and the plane is rebuilt from the shrunk details and the coarsest approximation. Rows
    /// run in parallel; each output value depends only on the level before, so the thread count does not change the result.
    /// </summary>
    private static void Shrink(float[] plane, int count, int w, int h, float k, float sigma, int levels, ParallelOptions parallel)
    {
        float[] current = ArrayPool<float>.Shared.Rent(count);
        float[] next = ArrayPool<float>.Shared.Rent(count);
        float[] temp = ArrayPool<float>.Shared.Rent(count);
        float[] sum = ArrayPool<float>.Shared.Rent(count);
        try
        {
            plane.AsSpan(0, count).CopyTo(current);
            Array.Clear(sum, 0, count);
            for (int level = 0; level < levels; level++)
            {
                int hole = 1 << level;
                float t = k * sigma * Attenuation[Math.Min(level, Attenuation.Length - 1)];
                float[] c = current, n = next, tmp = temp, acc = sum;
                Parallel.For(0, h, parallel, y => SmoothRow(c, tmp, w, y, hole));
                Parallel.For(0, h, parallel, y =>
                {
                    SmoothColumn(tmp, n, w, h, y, hole);
                    int row = y * w;
                    for (int x = row; x < row + w; x++)
                    {
                        float d = c[x] - n[x];
                        acc[x] += d > t ? d - t : d < -t ? d + t : 0;
                    }
                });
                (current, next) = (next, current);
            }

            for (int i = 0; i < count; i++)
            {
                plane[i] = current[i] + sum[i];
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(current);
            ArrayPool<float>.Shared.Return(next);
            ArrayPool<float>.Shared.Return(temp);
            ArrayPool<float>.Shared.Return(sum);
        }
    }

    /// <summary>Convolves one row with the B3-spline kernel [1 4 6 4 1] / 16, its taps <paramref name="hole"/> apart, mirrored at the edges.</summary>
    private static void SmoothRow(float[] source, float[] target, int w, int y, int hole)
    {
        int row = y * w;
        int inner0 = Math.Min(2 * hole, w), inner1 = Math.Max(inner0, w - (2 * hole));
        for (int x = 0; x < w; x++)
        {
            if (x == inner0)
            {
                // Away from the edges, no mirroring is needed.
                for (; x < inner1; x++)
                {
                    int i = row + x;
                    target[i] = ((6 * source[i]) + (4 * (source[i - hole] + source[i + hole])) + source[i - (2 * hole)] + source[i + (2 * hole)]) * (1f / 16);
                }

                if (x >= w)
                {
                    break;
                }
            }

            target[row + x] = ((6 * source[row + x])
                + (4 * (source[row + Mirror(x - hole, w)] + source[row + Mirror(x + hole, w)]))
                + source[row + Mirror(x - (2 * hole), w)] + source[row + Mirror(x + (2 * hole), w)]) * (1f / 16);
        }
    }

    /// <summary>Convolves one row's pixels down the columns with the same kernel.</summary>
    private static void SmoothColumn(float[] temp, float[] target, int w, int h, int y, int hole)
    {
        int up1 = Mirror(y - hole, h) * w, down1 = Mirror(y + hole, h) * w;
        int up2 = Mirror(y - (2 * hole), h) * w, down2 = Mirror(y + (2 * hole), h) * w;
        int row = y * w;
        for (int x = 0; x < w; x++)
        {
            target[row + x] = ((6 * temp[row + x]) + (4 * (temp[up1 + x] + temp[down1 + x])) + temp[up2 + x] + temp[down2 + x]) * (1f / 16);
        }
    }

    private static int Mirror(int v, int n)
    {
        if (n == 1)
        {
            return 0;
        }

        while (v < 0 || v >= n)
        {
            v = v < 0 ? -v : (2 * (n - 1)) - v;
        }

        return v;
    }
}
