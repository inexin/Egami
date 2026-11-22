using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>The tracing pipeline: label image, boundaries, edge points, curves, shapes.</summary>
internal static class Tracer
{
    internal static TraceResult Trace<TPixel>(Raster<TPixel> raster, TraceOptions options, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width, height = raster.Height;
        long pixels = (long)width * height;
        if (pixels > context.Limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), context.Limits.MaxPixels, pixels);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode == TraceMode.PixelArt)
        {
            return PixelArtTracer.Trace(raster, options, cancellationToken);
        }

        if (options.Mode == TraceMode.Logo)
        {
            return LogoTracer.Trace(raster, options, cancellationToken);
        }

        if (options.Mode is not TraceMode.BlackAndWhite and not TraceMode.Centerline)
        {
            return ColorTracer.Trace(LabelImage.FromRaster(raster, options, cancellationToken), options, cancellationToken);
        }

        float level = options.Threshold ?? raster.FindThreshold();
        byte[] intensities = new byte[checked(width * height)];
        InkMap ink = InkMap.FromRaster(raster, level, intensities, cancellationToken);
        int[]? regions = ink.RemoveNoise(options.Noise, cancellationToken);
        if (options.Mode == TraceMode.Centerline)
        {
            return CenterlineTracer.Trace(ink, intensities, options, cancellationToken);
        }

        return TraceInk(ink, intensities, options, cancellationToken, regions);
    }

    /// <summary>
    /// Traces an ink map: one black shape per ink region (its outer edge and its holes), behind them the paper.
    /// <paramref name="intensities"/>, each pixel's intensity over white from 0 to 255, places antialiased edges between pixels;
    /// without it every edge is hard. <paramref name="labelled"/>, when given, are the map's regions as
    /// <see cref="InkMap.Label(int[], CancellationToken)"/> numbers them, so they are not labelled again.
    /// </summary>
    internal static TraceResult TraceInk(InkMap ink, byte[]? intensities, TraceOptions options, CancellationToken cancellationToken, int[]? labelled = null)
    {
        List<PixelLoop> loops = BoundaryTracer.Trace(ink, cancellationToken);

        // Group each region's outer edge with its holes by the region an ink pixel on the edge belongs to. Regions are numbered
        // in scan order, so a region inside another's hole comes after it: back to front.
        int[] labels = labelled ?? new int[ink.Width * ink.Height];
        if (labelled is null)
        {
            _ = ink.Label(labels, cancellationToken);
        }

        SortedDictionary<int, List<PixelLoop>> regions = [];
        foreach (PixelLoop loop in loops)
        {
            int region = labels[(loop.InkY * ink.Width) + loop.InkX];
            if (!regions.TryGetValue(region, out List<PixelLoop>? list))
            {
                regions[region] = list = [];
            }

            // The outer edge first.
            if (loop.IsHole)
            {
                list.Add(loop);
            }
            else
            {
                list.Insert(0, loop);
            }
        }

        List<TracedShape> shapes = [];
        bool anyPaper = ink.Pixels.Contains((byte)0);
        if (!options.IgnoreWhite && anyPaper)
        {
            shapes.Add(new TracedShape(Path.Rect(new RectF(0, 0, ink.Width, ink.Height)), Rgba8.White));
        }

        RegionTracer edges = new(Levels(ink, intensities), []);
        CurveSettings settings = CurveSettings.From(options);
        List<PixelLoop>[] groups = [.. regions.Values];
        Path[] paths = new Path[groups.Length];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, groups.Length, parallel, g =>
        {
            PathBuilder builder = new();
            foreach (PixelLoop loop in groups[g])
            {
                Fit(edges, loop, settings).AddTo(builder, true);
                builder.Close();
            }

            paths[g] = builder.ToPath();
        });

        foreach (Path path in paths)
        {
            shapes.Add(new TracedShape(path, Rgba8.Black));
        }

        return new TraceResult(shapes, ink.Width, ink.Height);
    }

    /// <summary>
    /// The ink map as a two-color label image: paper and ink, each at the mean intensity of its pixels, so a pixel between the
    /// two reads as a blend of them.
    /// </summary>
    private static LabelImage Levels(InkMap ink, byte[]? intensities)
    {
        // Rows in parallel bands, each summing its own, then the bands in order.
        byte[] pixels = ink.Data;
        int width = ink.Width, height = ink.Height;
        int bands = Math.Max(1, Math.Min(height, Environment.ProcessorCount * 4));
        long[] sums = new long[bands * 4];
        Parallel.For(0, bands, band =>
        {
            long paperSum = 0, paperCount = 0, darkSum = 0, darkCount = 0;
            for (int i = (int)((long)band * height / bands) * width; i < (int)((long)(band + 1) * height / bands) * width; i++)
            {
                int value = intensities?[i] ?? (pixels[i] != 0 ? 0 : 255);
                if (pixels[i] != 0)
                {
                    darkSum += value;
                    darkCount++;
                }
                else
                {
                    paperSum += value;
                    paperCount++;
                }
            }

            (sums[4 * band], sums[(4 * band) + 1], sums[(4 * band) + 2], sums[(4 * band) + 3]) = (paperSum, paperCount, darkSum, darkCount);
        });

        long[] sum = new long[2], count = new long[2];
        for (int band = 0; band < bands; band++)
        {
            sum[0] += sums[4 * band];
            count[0] += sums[(4 * band) + 1];
            sum[1] += sums[(4 * band) + 2];
            count[1] += sums[(4 * band) + 3];
        }

        byte paper = count[0] > 0 ? (byte)(sum[0] / count[0]) : (byte)255, dark = count[1] > 0 ? (byte)(sum[1] / count[1]) : (byte)0;
        LabelImage image = new(width, height, [new Rgba8(paper, paper, paper, 255), new Rgba8(dark, dark, dark, 255)]);
        byte[] labels = image.Labels, rgb = image.Rgb;
        Parallel.For(0, height, y =>
        {
            for (int i = y * width; i < (y + 1) * width; i++)
            {
                labels[i] = pixels[i];
                byte value = intensities?[i] ?? (pixels[i] != 0 ? dark : paper);
                rgb[3 * i] = rgb[(3 * i) + 1] = rgb[(3 * i) + 2] = value;
            }
        });

        return image;
    }

    /// <summary>Fits a boundary through its points, placed by the pixels on either side; a speck keeps its pixel edges.</summary>
    private static FittedCurve Fit(RegionTracer edges, PixelLoop loop, CurveSettings settings)
    {
        int n = loop.Count;
        byte[] directions = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int dx = loop.X[(i + 1) % n] - loop.X[i], dy = loop.Y[(i + 1) % n] - loop.Y[i];
            directions[i] = (byte)(dx > 0 ? 0 : dy > 0 ? 1 : dx < 0 ? 2 : 3);
        }

        return n <= CubicFitter.Speck
            ? CubicFitter.Polygon(loop.X, loop.Y, directions)
            : CubicFitter.FitClosed(edges.Read(loop.X, loop.Y, directions, true), settings);
    }
}
