using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami;

/// <summary>Measuring content: the rectangles and angles the measured steps would use, without changing anything.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Returns the rectangle <see cref="Operations.RasterOps.Trim"/> would keep: the union over the frames of the pixels farther
    /// than <paramref name="tolerance"/> from the border color.
    /// </summary>
    /// <param name="tolerance">The color distance still counted as border, from 0 (the exact color) to 1 (every color).</param>
    /// <param name="color">The border color, or <see langword="null"/> (the default) for each frame's top-left pixel's.</param>
    /// <returns>The rectangle, or an empty one when every pixel is border.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is outside 0..1.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Crops.cs" region="trim"/>
    /// </example>
    public Rect TrimBounds(float tolerance = 0, Rgba8? color = null)
    {
        Operations.RasterOps.CheckTolerance(tolerance, nameof(tolerance));
        return Accept(new TrimVisitor(tolerance, color));
    }

    /// <summary>
    /// Returns the rectangle <see cref="Operations.RasterOps.EntropyCrop"/> would keep: the union over the frames of the 16 × 16
    /// tiles whose luminance entropy reaches <paramref name="threshold"/>.
    /// </summary>
    /// <param name="threshold">The entropy a tile needs, from 0 to 1.</param>
    /// <returns>The rectangle, or an empty one when no tile reaches the threshold.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0..1.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Crops.cs" region="entropy-crop"/>
    /// </example>
    public Rect EntropyBounds(float threshold = 0.5f)
    {
        if (!(threshold >= 0 && threshold <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be from 0 to 1.");
        }

        return Accept(new EntropyVisitor(threshold));
    }

    private sealed class TrimVisitor(float tolerance, Rgba8? color) : IRasterVisitor<Rect>
    {
        public Rect Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Rect union = default;
            foreach (Frame<TPixel> frame in raster.Frames)
            {
                union = Bounds.Union(union, Bounds.Trim(frame.Plane, tolerance, color, raster.Context.MaxParallelism, CancellationToken.None));
            }

            return union;
        }
    }

    private sealed class EntropyVisitor(float threshold) : IRasterVisitor<Rect>
    {
        public Rect Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Rect union = default;
            foreach (Frame<TPixel> frame in raster.Frames)
            {
                union = Bounds.Union(union, Bounds.Entropy(frame.Plane, threshold, raster.Context.MaxParallelism, CancellationToken.None));
            }

            return union;
        }
    }
}
