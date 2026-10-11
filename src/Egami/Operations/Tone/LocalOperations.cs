using System.Buffers;
using System.Numerics;

using Egami.Operations.Filters;
using Egami.Pixels;

namespace Egami.Operations.Tone;

/// <summary>Disposes a scratch plane at the end of a using block.</summary>
internal readonly struct Owned(PixelPlane<GrayF32> plane) : IDisposable
{
    internal PixelPlane<GrayF32> Plane => plane;

    public void Dispose() => plane.Dispose();
}

/// <summary>Helpers for the neighbourhood tone steps: a lightness plane from the chain's memory, and writing lightness back.</summary>
internal static class LightnessPlane
{
    /// <summary>Allocates a plane through the chain's budgeted memory, so it counts against MaxTotalBytes.</summary>
    internal static PixelPlane<GrayF32> Allocate<TPixel>(OperationInput<TPixel> input, int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return PixelPlane.Allocate<GrayF32>(width, height, (IPixelMemorySource?)input.Memory ?? input.Context.Memory);
    }

    /// <summary>Fills <paramref name="plane"/> with each source pixel's L*, from 0 to 100.</summary>
    internal static void Measure<TPixel>(OperationInput<TPixel> input, PixelPlane<GrayF32> plane)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width;
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    Span<GrayF32> l = plane.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        l[x].L = LabMath.Lightness(row[x].R, row[x].G, row[x].B);
                    }
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>Writes each pixel with its L* replaced by what <paramref name="lightness"/> returns for it, keeping a*, b* and alpha.</summary>
    internal static void Write<TPixel>(OperationInput<TPixel> input, Func<int, int, float, float> lightness)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width;
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    for (int x = 0; x < width; x++)
                    {
                        ref RgbaF32 p = ref row[x];
                        Vector3 lab = LabMath.ToLab(p.R, p.G, p.B);
                        Vector3 c = LabMath.FromLab(new Vector3(Math.Clamp(lightness(x, y, lab.X), 0, 100), lab.Y, lab.Z));
                        p = new RgbaF32(c.X, c.Y, c.Z, p.A);
                    }

                    PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }
}

/// <summary>
/// CLAHE (Zuiderveld, 1994) on L*: one histogram per tile, clipped at clipLimit times its mean bin with the excess spread over every
/// bin, equalized, and the tiles' curves blended bilinearly between tile centers; past the outer centers the edge tiles' curves
/// hold, as mirrored border tiles give. a* and b* are kept.
/// </summary>
internal sealed class ClaheOperation(int tilesX, int tilesY, float clipLimit, int bins) : IRasterOperation, Scopes.IRunsOnViews
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        int tx = Math.Min(tilesX, width), ty = Math.Min(tilesY, height);
        using Owned lightnessLease = new(LightnessPlane.Allocate(input, width, height));
        PixelPlane<GrayF32> lightness = lightnessLease.Plane;
        LightnessPlane.Measure(input, lightness);

        float[] maps = ArrayPool<float>.Shared.Rent(tx * ty * bins);
        try
        {
            RunTiles(input, lightness, maps, tx, ty);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(maps);
        }
    }

    private void RunTiles<TPixel>(OperationInput<TPixel> input, PixelPlane<GrayF32> lightness, float[] maps, int tx, int ty)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        Parallel.For(0, tx * ty, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, input.Context.MaxParallelism), CancellationToken = input.CancellationToken }, tile =>
        {
            int i = tile % tx, j = tile / tx;
            int x0 = (int)((long)width * i / tx), x1 = (int)((long)width * (i + 1) / tx);
            int y0 = (int)((long)height * j / ty), y1 = (int)((long)height * (j + 1) / ty);
            double[] rented = ArrayPool<double>.Shared.Rent(bins);
            try
            {
                Span<double> histogram = rented.AsSpan(0, bins);
                histogram.Clear();
                for (int y = y0; y < y1; y++)
                {
                    ReadOnlySpan<GrayF32> row = lightness.Row(y);
                    for (int x = x0; x < x1; x++)
                    {
                        histogram[Bin(row[x].L, bins)]++;
                    }
                }

                Curve(histogram, (x1 - x0) * (double)(y1 - y0), clipLimit, maps.AsSpan(tile * bins, bins));
            }
            finally
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        });

        // Blending runs from tile center to tile center, each tile's center being its first pixel past half its size, as Zuiderveld's
        // regions place it.
        float tileWidth = width / (float)tx, tileHeight = height / (float)ty;
        float centerX = MathF.Floor(tileWidth / 2), centerY = MathF.Floor(tileHeight / 2);
        LightnessPlane.Write(input, (x, y, l) =>
        {
            float gx = Math.Clamp((x - centerX) / tileWidth, 0, tx - 1), gy = Math.Clamp((y - centerY) / tileHeight, 0, ty - 1);
            int i0 = Math.Min((int)gx, Math.Max(0, tx - 2)), j0 = Math.Min((int)gy, Math.Max(0, ty - 2));
            int i1 = Math.Min(i0 + 1, tx - 1), j1 = Math.Min(j0 + 1, ty - 1);
            float fx = gx - i0, fy = gy - j0;
            int bin = Bin(lightness.Row(y)[x].L, bins);
            float a = maps[(((j0 * tx) + i0) * bins) + bin], b = maps[(((j0 * tx) + i1) * bins) + bin];
            float c = maps[(((j1 * tx) + i0) * bins) + bin], d = maps[(((j1 * tx) + i1) * bins) + bin];
            float top = a + ((b - a) * fx), bottom = c + ((d - c) * fx);
            return (top + ((bottom - top) * fy)) * 100;
        });
    }

    /// <summary>
    /// The bin of an L* value: L* in 16-bit steps divided into equal bins of 65,535 / bins + 1 steps, as ImageMagick's -clahe bins
    /// its 16-bit lightness.
    /// </summary>
    internal static int Bin(float lightness, int bins)
    {
        int steps = (int)Math.Round(Math.Clamp(lightness / 100, 0, 1) * 65535);
        return Math.Min(bins - 1, steps / ((65535 / bins) + 1));
    }

    /// <summary>
    /// One tile's curve: the histogram clipped at the limit and the excess spread as Zuiderveld spreads it (an even share to every
    /// bin that stays under the limit, bins near it filled to the limit, and what is left handed to the bins still under it), then
    /// its cumulative share, from 0 to 1.
    /// </summary>
    internal static void Curve(Span<double> histogram, double pixels, float clipLimit, Span<float> map)
    {
        int bins = histogram.Length;
        if (clipLimit > 0)
        {
            double limit = Math.Max(1, Math.Floor(clipLimit * pixels / bins)), excess = 0;
            for (int k = 0; k < bins; k++)
            {
                excess += Math.Max(0, histogram[k] - limit);
            }

            double step = Math.Floor(excess / bins), upper = limit - step;
            for (int k = 0; k < bins; k++)
            {
                if (histogram[k] > limit)
                {
                    histogram[k] = limit;
                }
                else if (histogram[k] > upper)
                {
                    excess -= limit - histogram[k];
                    histogram[k] = limit;
                }
                else
                {
                    excess -= step;
                    histogram[k] += step;
                }
            }

            // What is left goes one count at a time to bins still under the limit, spread across the range.
            while (excess >= 1)
            {
                double before = excess;
                int stride = Math.Max(1, (int)(bins / excess));
                for (int k = 0; k < bins && excess >= 1; k += stride)
                {
                    if (histogram[k] < limit)
                    {
                        histogram[k]++;
                        excess--;
                    }
                }

                if (excess >= before)
                {
                    break;
                }
            }
        }

        // Shares of the tile's pixel count, so excess that found no room leaves the curve short of 1, as in Zuiderveld's mapping.
        double cumulative = 0;
        for (int k = 0; k < bins; k++)
        {
            cumulative += histogram[k];
            map[k] = pixels > 0 ? (float)Math.Min(1, cumulative / pixels) : k / (float)(bins - 1);
        }
    }
}

/// <summary>
/// ImageMagick's -local-contrast: each pixel's luma (Rec. 709 weights on the stored values) is pushed away from a wide triangle
/// blur of itself by strength, and red, green and blue are scaled by the same ratio, so hue holds. The triangle reaches radius ×
/// 20% of the longer side, weighs as ImageMagick's (half-width w, weights 1 … w + 1 … 2 over 2w taps, divided by (w + 1)²), runs
/// down the columns with the edge rows repeated and then along the rows with the columns mirrored, and is computed from running
/// sums, so its cost does not grow with the radius.
/// </summary>
internal sealed class LocalContrastOperation(float radius, float strength) : IRasterOperation, Scopes.IRunsOnViews
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        int w = Math.Max(1, (int)(radius * 0.2 * Math.Max(width, height)));
        using Owned lumaLease = new(LightnessPlane.Allocate(input, width, height));
        using Owned blurLease = new(LightnessPlane.Allocate(input, width, height));
        PixelPlane<GrayF32> luma = lumaLease.Plane, blur = blurLease.Plane;
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    Span<GrayF32> l = luma.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        l[x].L = (float)ToneMath.Intensity(row[x].R, row[x].G, row[x].B);
                    }
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });

        // Down the columns, edge rows repeated; the result goes to the blur plane.
        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, input.Context.MaxParallelism), CancellationToken = input.CancellationToken };
        Parallel.For(0, Chunks(width), options, chunk =>
        {
            (int x0, int x1) = Span(chunk, width);
            double[] line = ArrayPool<double>.Shared.Rent(height);
            double[] sums = ArrayPool<double>.Shared.Rent(height + (2 * w) + 2);
            try
            {
                for (int x = x0; x < x1; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        line[y] = luma.Row(y)[x].L;
                    }

                    Triangle(line.AsSpan(0, height), w, sums, mirror: false);
                    for (int y = 0; y < height; y++)
                    {
                        blur.Row(y)[x].L = (float)line[y];
                    }
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(line);
                ArrayPool<double>.Shared.Return(sums);
            }
        });

        // Along the rows, columns mirrored, then each pixel scaled by its new luma over its old.
        float k = strength;
        input.ForEachBand((start, end) =>
        {
            double[] line = ArrayPool<double>.Shared.Rent(width);
            double[] sums = ArrayPool<double>.Shared.Rent(width + (2 * w) + 2);
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    ReadOnlySpan<GrayF32> b = blur.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        line[x] = b[x].L;
                    }

                    Triangle(line.AsSpan(0, width), w, sums, mirror: true);
                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    ReadOnlySpan<GrayF32> l = luma.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        float source = l[x].L;
                        float mult = source > 0 ? (source + ((source - (float)line[x]) * k)) / source : 0;
                        ref RgbaF32 p = ref row[x];
                        p = new RgbaF32(Clamp01(p.R * mult), Clamp01(p.G * mult), Clamp01(p.B * mult), p.A);
                    }

                    PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(line);
                ArrayPool<double>.Shared.Return(sums);
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>
    /// Replaces <paramref name="line"/> with its triangle blur: Σ (w + 1 − |k|) s[i + k] for k from −w to w − 1, over (w + 1)², with
    /// samples past the ends repeated or mirrored. The full triangle to k = w is two boxes of w + 1 taps, from prefix sums of prefix
    /// sums; the tap at k = w is then taken off.
    /// </summary>
    internal static void Triangle(Span<double> line, int w, double[] sums, bool mirror)
    {
        int n = line.Length;
        int padded = n + (2 * w);
        // sums[j] is the sum of the first j padded samples, the padded line starting at original index -w.
        sums[0] = 0;
        for (int j = 0; j < padded; j++)
        {
            sums[j + 1] = sums[j] + Sample(line, j - w, mirror);
        }

        // B(t) = s[t] + … + s[t + w] in original indices; the triangle at i is B(i - w) + … + B(i), kept as a running window.
        double window = 0;
        for (int t = -w; t <= 0; t++)
        {
            window += Box(t);
        }

        double total = (w + 1.0) * (w + 1.0);
        for (int i = 0; i < n; i++)
        {
            double value = (window - Sample(line, i + w, mirror)) / total;
            if (i + 1 < n)
            {
                window += Box(i + 1) - Box(i - w);
            }

            line[i] = value;
        }

        double Box(int t) => sums[t + (2 * w) + 1] - sums[t + w];
    }

    private static double Sample(Span<double> line, int index, bool mirror)
    {
        int n = line.Length;
        if (index < 0)
        {
            index = mirror ? Math.Min(-index, n - 1) : 0;
        }
        else if (index >= n)
        {
            index = mirror ? Math.Max((2 * n) - 2 - index, 0) : n - 1;
        }

        return line[index];
    }

    private static float Clamp01(float v) => v > 0 ? (v < 1 ? v : 1) : 0;

    private static int Chunks(int columns) => Math.Max(1, Math.Min(64, columns / 16));

    private static (int Start, int End) Span(int chunk, int width)
    {
        int chunks = Chunks(width);
        return ((int)((long)width * chunk / chunks), (int)((long)width * (chunk + 1) / chunks));
    }
}

/// <summary>
/// Bradley and Roth's (2007) adaptive threshold: a pixel is dark when its luminance times its window's pixel count is at most the
/// window's sum times 1 − limit. Window sums come from column sums slid down the rows, so memory stays one luminance plane.
/// </summary>
internal sealed class AdaptiveThresholdOperation(float limit, int? window, Rgba8 below, Rgba8 above) : IRasterOperation, Scopes.IRunsOnViews
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        int side = Math.Max(1, window ?? (width / 8));
        int half = side / 2;
        using Owned lumaLease = new(LightnessPlane.Allocate(input, width, height));
        PixelPlane<GrayF32> luma = lumaLease.Plane;
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    Span<GrayF32> l = luma.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        // Luminance in 8-bit steps, rounded, as the sums of a byte image.
                        l[x].L = MathF.Round(255 * ((0.2126f * row[x].R) + (0.7152f * row[x].G) + (0.0722f * row[x].B)));
                    }
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });

        float keep = 1 - limit;
        input.ForEachBand((start, end) =>
        {
            double[] columns = ArrayPool<double>.Shared.Rent(width);
            double[] prefix = ArrayPool<double>.Shared.Rent(width + 1);
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<double> sums = columns.AsSpan(0, width);
                sums.Clear();
                int top = Math.Max(0, start - half), bottom = Math.Min(height - 1, start + half);
                for (int y = top; y <= bottom; y++)
                {
                    Add(sums, luma.Row(y), 1);
                }

                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    if (y > start)
                    {
                        int leaving = y - half - 1, entering = y + half;
                        if (leaving >= 0)
                        {
                            Add(sums, luma.Row(leaving), -1);
                        }

                        if (entering < height)
                        {
                            Add(sums, luma.Row(entering), 1);
                        }
                    }

                    int rows = Math.Min(height - 1, y + half) - Math.Max(0, y - half) + 1;
                    prefix[0] = 0;
                    for (int x = 0; x < width; x++)
                    {
                        prefix[x + 1] = prefix[x] + sums[x];
                    }

                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    ReadOnlySpan<GrayF32> l = luma.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        int x0 = Math.Max(0, x - half), x1 = Math.Min(width - 1, x + half);
                        double sum = prefix[x1 + 1] - prefix[x0];
                        int count = (x1 - x0 + 1) * rows;
                        Rgba8 c = l[x].L * count <= sum * keep ? below : above;
                        row[x] = new RgbaF32(c.R / 255f, c.G / 255f, c.B / 255f, row[x].A);
                    }

                    PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(columns);
                ArrayPool<double>.Shared.Return(prefix);
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });

        static void Add(Span<double> sums, ReadOnlySpan<GrayF32> row, int sign)
        {
            for (int x = 0; x < sums.Length; x++)
            {
                sums[x] += sign * row[x].L;
            }
        }
    }
}
