using Egami.Operations.Filters;

namespace Egami.Operations;

/// <summary>Edges and morphology (Phase 2.5.4 stage B).</summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Replaces each pixel with the strength of the edge through it: the gradient magnitude of <paramref name="op"/>'s kernels, scaled
    /// so a full black-to-white step gives 1, clamped to 0 to 1. Computed on premultiplied values with edges repeating the edge
    /// pixel; alpha is kept. In gray mode the pixels are first reduced to Rec. 709 luma and the magnitude is written as gray;
    /// otherwise each color channel gets its own.
    /// </summary>
    /// <param name="op">The kernels; <see cref="EdgeOperator.Sobel"/> by default.</param>
    /// <param name="grayscale">Whether to find edges in the luma (gray result) rather than per channel.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="edges"/>
    /// </example>
    public RasterOps Edges(EdgeOperator op = EdgeOperator.Sobel, bool grayscale = true)
    {
        if (!Enum.IsDefined(op))
        {
            throw new ArgumentOutOfRangeException(nameof(op), op, "The edge operator is not defined.");
        }

        return Add(new EdgeOperation(op, grayscale));
    }

    /// <summary>
    /// Finds thin edges with Canny's detector, as ImageMagick's -canny 0x{sigma}+{low × 100}%+{high × 100}% does: the luma is
    /// smoothed by a Gaussian, Canny's first differences give the gradient, pixels that are not the strongest across their edge are
    /// dropped, and hysteresis keeps pixels whose magnitude reaches <paramref name="high"/> times the largest, plus those
    /// 8-connected to them that reach <paramref name="low"/> times it. Writes white edges on black, one pixel wide, with alpha
    /// kept: a gray result works as a <see cref="Mask"/> as it is, a color one with <see cref="MaskChannel.Luminance"/>. Reads the
    /// whole image, so inside a scope it sees all of it.
    /// </summary>
    /// <param name="sigma">The Gaussian's standard deviation in pixels, 0 (no smoothing) to 100.</param>
    /// <param name="low">The low threshold, as a fraction 0 to 1 of the largest gradient; at most <paramref name="high"/>.</param>
    /// <param name="high">The high threshold, as a fraction 0 to 1 of the largest gradient.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is negative, not finite or above 100; a threshold is outside 0 to 1, or <paramref name="low"/> is above <paramref name="high"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="canny"/>
    /// </example>
    public RasterOps CannyEdges(float sigma = 1, float low = 0.1f, float high = 0.3f)
    {
        if (!float.IsFinite(sigma) || sigma < 0 || sigma > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be 0 to 100.");
        }

        if (!(high >= 0 && high <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(high), high, "The high threshold must be 0 to 1.");
        }

        if (!(low >= 0 && low <= high))
        {
            throw new ArgumentOutOfRangeException(nameof(low), low, "The low threshold must be 0 to the high threshold.");
        }

        return Add(new CannyOperation(sigma, low, high));
    }

    /// <summary>
    /// Flat morphology: each channel's maximum (dilate) or minimum (erode) over <paramref name="shape"/> around each pixel, or one
    /// of their combinations, as ImageMagick's -morphology. Works on stored values, alpha included, so 8 and 16-bit results are
    /// exact; <see cref="MorphologyOp.Gradient"/>, <see cref="MorphologyOp.TopHat"/> and <see cref="MorphologyOp.BottomHat"/>
    /// keep alpha. Edges repeat the edge pixel. Rectangles cost the same per pixel at any size; other shapes grow with their height.
    /// </summary>
    /// <param name="op">The operation.</param>
    /// <param name="shape">The structuring element.</param>
    /// <param name="iterations">How many times each dilation and erosion runs, 1 to 100: Open with 2 erodes twice, then dilates twice.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not defined, or <paramref name="iterations"/> is outside 1 to 100.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="morphology"/>
    /// </example>
    public RasterOps Morphology(MorphologyOp op, StructuringElement shape, int iterations = 1)
    {
        if (!Enum.IsDefined(op))
        {
            throw new ArgumentOutOfRangeException(nameof(op), op, "The morphology operation is not defined.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MorphologyOperation.MaxIterations);
        return Add(new MorphologyOperation(op, shape, iterations));
    }

    /// <summary>The maximum over the (2r + 1) square around each pixel: light areas grow by <paramref name="radius"/> pixels.</summary>
    /// <param name="radius">The radius in pixels, 0 (changes nothing) to 1000.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or above 1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="dilate-erode"/>
    /// </example>
    public RasterOps Dilate(int radius) => Morphology(MorphologyOp.Dilate, StructuringElement.Square(radius));

    /// <summary>The minimum over the (2r + 1) square around each pixel: light areas shrink by <paramref name="radius"/> pixels.</summary>
    /// <param name="radius">The radius in pixels, 0 (changes nothing) to 1000.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or above 1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="dilate-erode"/>
    /// </example>
    public RasterOps Erode(int radius) => Morphology(MorphologyOp.Erode, StructuringElement.Square(radius));

    /// <summary>The maximum over the (2rx + 1) × (2ry + 1) rectangle around each pixel, as Skia's dilate filter with those radii.</summary>
    /// <param name="radiusX">The horizontal radius in pixels, 0 to 1000.</param>
    /// <param name="radiusY">The vertical radius in pixels, 0 to 1000.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative or above 1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="dilate-erode"/>
    /// </example>
    public RasterOps Dilate(int radiusX, int radiusY) => Morphology(MorphologyOp.Dilate, StructuringElement.Rectangle(radiusX, radiusY));

    /// <summary>The minimum over the (2rx + 1) × (2ry + 1) rectangle around each pixel, as Skia's erode filter with those radii.</summary>
    /// <param name="radiusX">The horizontal radius in pixels, 0 to 1000.</param>
    /// <param name="radiusY">The vertical radius in pixels, 0 to 1000.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative or above 1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="dilate-erode"/>
    /// </example>
    public RasterOps Erode(int radiusX, int radiusY) => Morphology(MorphologyOp.Erode, StructuringElement.Rectangle(radiusX, radiusY));
}
