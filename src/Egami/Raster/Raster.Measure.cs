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

    /// <summary>
    /// Returns the tilt of the first frame's text lines or other strong horizontal structure, in degrees clockwise, to 0.05°:
    /// <see cref="Operations.RasterOps.Deskew"/> turns the image back by it. A gray copy about 1,500 pixels on its long side is
    /// binarized with Otsu's threshold and every angle up to <paramref name="maxAngle"/> is scored by how sharply the ink's
    /// projection piles up in rows (Postl's projection-profile method).
    /// </summary>
    /// <param name="maxAngle">The largest tilt looked for, in degrees, more than 0 and at most 45.</param>
    /// <returns>The tilt, or 0 when no angle stands out clearly from level (a picture, noise, or an already straight page).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAngle"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Crops.cs" region="deskew"/>
    /// </example>
    public float DetectSkew(float maxAngle = 15)
    {
        Operations.RasterOps.CheckMaxAngle(maxAngle);
        return Accept(new SkewVisitor(maxAngle));
    }

    private sealed class SkewVisitor(float maxAngle) : IRasterVisitor<float>
    {
        public float Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            return (float)SkewDetector.Detect(raster.Frames[0].Plane, maxAngle, CancellationToken.None);
        }
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
