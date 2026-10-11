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
}
