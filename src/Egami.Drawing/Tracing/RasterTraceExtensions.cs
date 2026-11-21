using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>Bitmap tracing: turns a raster's first frame into filled vector shapes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="trace"/>
/// </example>
public static class RasterTraceExtensions
{
    /// <summary>
    /// Traces a raster's first frame into shapes: its ink regions (or, in grayscale and color, its color regions) become paths
    /// of lines, arcs, curves and corners fitted to the pixel edges, in the raster's pixels.
    /// </summary>
    /// <param name="raster">The raster.</param>
    /// <param name="options">The settings, or <see langword="null"/> for <see cref="TraceOptions.Default"/>.</param>
    /// <param name="context">The context whose limits apply, or <see langword="null"/> for the raster's.</param>
    /// <returns>The shapes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="LimitExceededException">The raster is larger than the context's <see cref="SafetyLimits.MaxPixels"/>.</exception>
    /// <exception cref="ArgumentException">In <see cref="TraceMode.Logo"/>, the image is not flat-color art (a photo or a
    /// gradient), or it has transparency and <see cref="TraceOptions.Palette"/> has more than 254 colors.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="trace"/>
    /// </example>
    public static TraceResult Trace(this Raster raster, TraceOptions? options = null, EgamiContext? context = null)
    {
        return TraceAny(raster, options, context, CancellationToken.None);
    }

    /// <summary>
    /// Traces a raster's first frame into shapes: its ink regions (or, in grayscale and color, its color regions) become paths
    /// of lines, arcs, curves and corners fitted to the pixel edges, in the raster's pixels.
    /// </summary>
    /// <typeparam name="TPixel">The raster's pixel type.</typeparam>
    /// <param name="raster">The raster.</param>
    /// <param name="options">The settings, or <see langword="null"/> for <see cref="TraceOptions.Default"/>.</param>
    /// <param name="context">The context whose limits apply, or <see langword="null"/> for the raster's.</param>
    /// <returns>The shapes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="LimitExceededException">The raster is larger than the context's <see cref="SafetyLimits.MaxPixels"/>.</exception>
    /// <exception cref="ArgumentException">In <see cref="TraceMode.Logo"/>, the image is not flat-color art (a photo or a
    /// gradient), or it has transparency and <see cref="TraceOptions.Palette"/> has more than 254 colors.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="options"/>
    /// </example>
    public static TraceResult Trace<TPixel>(this Raster<TPixel> raster, TraceOptions? options = null, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        return Tracer.Trace(raster, options ?? TraceOptions.Default, context ?? raster.Context, CancellationToken.None);
    }

    /// <summary>Traces a raster's first frame into shapes on a worker thread, as <see cref="Trace(Raster, TraceOptions?, EgamiContext?)"/> does.</summary>
    /// <param name="raster">The raster; it must not change or be disposed until the task completes.</param>
    /// <param name="options">The settings, or <see langword="null"/> for <see cref="TraceOptions.Default"/>.</param>
    /// <param name="context">The context whose limits apply, or <see langword="null"/> for the raster's.</param>
    /// <param name="cancellationToken">Cancels the trace; it is checked between rows and between outlines.</param>
    /// <returns>The shapes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The trace was cancelled.</exception>
    /// <exception cref="LimitExceededException">The raster is larger than the context's <see cref="SafetyLimits.MaxPixels"/>.</exception>
    /// <exception cref="ArgumentException">In <see cref="TraceMode.Logo"/>, the image is not flat-color art (a photo or a
    /// gradient), or it has transparency and <see cref="TraceOptions.Palette"/> has more than 254 colors.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="async"/>
    /// </example>
    public static Task<TraceResult> TraceAsync(this Raster raster, TraceOptions? options = null, EgamiContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raster);
        return Task.Run(() => TraceAny(raster, options, context, cancellationToken), cancellationToken);
    }

    private static TraceResult TraceAny(Raster raster, TraceOptions? options, EgamiContext? context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raster);
        options ??= TraceOptions.Default;
        context ??= raster.Context;
        switch (raster)
        {
            case Raster<Rgba8> rgba8:
                return Tracer.Trace(rgba8, options, context, cancellationToken);
            case Raster<Gray8> gray8:
                return Tracer.Trace(gray8, options, context, cancellationToken);
            default:
                {
                    // Other pixel types are read through a 16-bit copy, which keeps their precision for the threshold.
                    if (options.Mode == TraceMode.BlackAndWhite)
                    {
                        options = options with { Threshold = options.Threshold ?? raster.FindThreshold() };
                    }

                    using Raster<Rgba16> rgba16 = raster.As<Rgba16>();
                    return Tracer.Trace(rgba16, options, context, cancellationToken);
                }
        }
    }
}
