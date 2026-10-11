using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations;

/// <content>Steps that measure the pixels to choose their size (Phase 2.5.2, stage C).</content>
public sealed partial class RasterOps
{
    /// <summary>
    /// Removes the border: the rows and columns at the edges whose pixels all lie within <paramref name="tolerance"/> of the
    /// border color, by ImageMagick's -fuzz distance, so the rectangle equals ImageMagick's -trim. Every frame of an animation is
    /// cropped to the union of the frames' rectangles, so they keep one size; an image that is all border is left as it is.
    /// </summary>
    /// <param name="tolerance">The color distance still counted as border, from 0 (the exact color) to 1 (every color).</param>
    /// <param name="color">The border color, or <see langword="null"/> (the default) for the top-left pixel's.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is outside 0..1.</exception>
    /// <remarks>
    /// The size depends on the pixels, so the chain runs up to the trim, measures, and plans the rest from the real size; a trim
    /// cannot run inside <c>Within</c>. <see cref="Raster.TrimBounds"/> returns the rectangle without changing anything.
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Crops.cs" region="trim"/>
    /// </example>
    public RasterOps Trim(float tolerance = 0, Rgba8? color = null)
    {
        CheckTolerance(tolerance, nameof(tolerance));
        return Add(new TrimOperation(tolerance, color));
    }

    /// <summary>
    /// Crops to the busiest part of the image: the bounding box of the 16 × 16 tiles whose luminance entropy (the Shannon entropy
    /// of the tile's 256-level histogram, over 8 bits) reaches <paramref name="threshold"/>, as the reference library's EntropyCrop does with
    /// the same default. Frames are cropped to the union of their boxes; an image with no busy tile is left as it is.
    /// </summary>
    /// <param name="threshold">The entropy a tile needs, from 0 (every tile) to 1 (only uniformly random noise).</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0..1.</exception>
    /// <remarks>Like <see cref="Trim"/>, the size depends on the pixels, so it cannot run inside <c>Within</c>.</remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Crops.cs" region="entropy-crop"/>
    /// </example>
    public RasterOps EntropyCrop(float threshold = 0.5f)
    {
        if (!(threshold >= 0 && threshold <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be from 0 to 1.");
        }

        return Add(new EntropyCropOperation(threshold));
    }

    /// <summary>
    /// Straightens a scanned or photographed page: detects the tilt of its text lines (see <see cref="Raster.DetectSkew"/>) and
    /// turns the image back by it. Every frame is turned by the first frame's angle; an image with no clear lines is left as it is.
    /// </summary>
    /// <param name="maxAngle">The largest tilt looked for, in degrees, more than 0 and at most 45.</param>
    /// <param name="background">The color of the uncovered corners, or <see langword="null"/> (the default) for the top-left pixel's, the border color Trim would use.</param>
    /// <param name="keepSize">Whether the page keeps its size (the default, as a scanner user expects), or the canvas grows to the turned bounds as Rotate does.</param>
    /// <param name="filter">The resampling kernel for the turn.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAngle"/> is out of range, or <paramref name="filter"/> is not defined.</exception>
    /// <remarks>The angle depends on the pixels, so the chain runs up to the deskew and measures; it cannot run inside <c>Within</c>.</remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Crops.cs" region="deskew"/>
    /// </example>
    public RasterOps Deskew(float maxAngle = 15, Rgba8? background = null, bool keepSize = true, ResampleFilter filter = ResampleFilter.Bicubic)
    {
        CheckMaxAngle(maxAngle);
        CheckFilter(filter, ColorSpace.Encoded);
        return Add(new DeskewOperation(maxAngle, background, keepSize, filter));
    }

    internal static void CheckMaxAngle(float maxAngle)
    {
        if (!(maxAngle > 0 && maxAngle <= 45))
        {
            throw new ArgumentOutOfRangeException(nameof(maxAngle), maxAngle, "The largest angle must be more than 0 and at most 45 degrees.");
        }
    }
}
