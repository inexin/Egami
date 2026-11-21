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
        if (options.Mode != TraceMode.BlackAndWhite)
        {
            return ColorTracer.Trace(LabelImage.FromRaster(raster, options, cancellationToken), options, cancellationToken);
        }

        float level = options.Threshold ?? raster.FindThreshold();
        byte[] intensities = new byte[checked(width * height)];
        InkMap ink = InkMap.FromRaster(raster, level, intensities, cancellationToken);
        ink.RemoveNoise(options.Noise, cancellationToken);
        return TraceInk(ink, intensities, options, cancellationToken);
    }

    /// <summary>
    /// Traces an ink map: one black shape per ink region (its outer edge and its holes), behind them the paper.
    /// <paramref name="intensities"/>, each pixel's intensity over white from 0 to 255, places antialiased edges between pixels;
    /// without it every edge is hard.
    /// </summary>
    internal static TraceResult TraceInk(InkMap ink, byte[]? intensities, TraceOptions options, CancellationToken cancellationToken)
    {
        List<PixelLoop> loops = BoundaryTracer.Trace(ink, cancellationToken);

        // Group each region's outer edge with its holes by the region an ink pixel on the edge belongs to. Regions are numbered
        // in scan order, so a region inside another's hole comes after it: back to front.
        int[] labels = new int[ink.Width * ink.Height];
        _ = ink.Label(labels, cancellationToken);
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
        Span<byte> pixels = ink.Pixels;
        long[] sum = new long[2], count = new long[2];
        for (int i = 0; i < pixels.Length; i++)
        {
            int value = intensities?[i] ?? (pixels[i] != 0 ? 0 : 255);
            sum[pixels[i]] += value;
            count[pixels[i]]++;
        }

        byte paper = count[0] > 0 ? (byte)(sum[0] / count[0]) : (byte)255, dark = count[1] > 0 ? (byte)(sum[1] / count[1]) : (byte)0;
        LabelImage image = new(ink.Width, ink.Height, [new Rgba8(paper, paper, paper, 255), new Rgba8(dark, dark, dark, 255)]);
        for (int i = 0; i < pixels.Length; i++)
        {
            image.Labels[i] = pixels[i];
            byte value = intensities?[i] ?? (pixels[i] != 0 ? dark : paper);
            image.Rgb[3 * i] = image.Rgb[(3 * i) + 1] = image.Rgb[(3 * i) + 2] = value;
        }

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
