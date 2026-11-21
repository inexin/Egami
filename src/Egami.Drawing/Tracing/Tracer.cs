using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>The tracing pipeline: label image, boundaries, polygons, curves, shapes.</summary>
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
        float level = options.Threshold ?? raster.FindThreshold();
        InkMap ink = InkMap.FromRaster(raster, level, cancellationToken);
        ink.RemoveNoise(options.Noise, cancellationToken);
        return TraceInk(ink, options, cancellationToken);
    }

    /// <summary>Traces an ink map: one black shape per ink region (its outer edge and its holes), behind them the paper.</summary>
    internal static TraceResult TraceInk(InkMap ink, TraceOptions options, CancellationToken cancellationToken)
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

        double alphaMax = options.AlphaMax, tolerance = options.Tolerance;
        foreach (List<PixelLoop> region in regions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathBuilder builder = new();
            foreach (PixelLoop loop in region)
            {
                (double X, double Y)[] polygon = PolygonFitter.Fit(loop);
                new CurveFitter(polygon, alphaMax).AddTo(builder, tolerance);
            }

            shapes.Add(new TracedShape(builder.ToPath(), Rgba8.Black));
        }

        return new TraceResult(shapes, ink.Width, ink.Height);
    }
}
