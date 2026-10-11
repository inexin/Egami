namespace Egami.Operations;

/// <summary>
/// The kernels <see cref="RasterOps.Edges(EdgeOperator, bool)"/> finds edges with. Every operator is scaled so a full black-to-white
/// step gives 1 at its strongest pixel; stronger responses (corners, single bright pixels) clamp to 1.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="edges"/>
/// </example>
public enum EdgeOperator
{
    /// <summary>Sobel and Feldman's 3 × 3 pair, weights 1, 2, 1 across the derivative: the usual choice.</summary>
    Sobel,

    /// <summary>Scharr's 3 × 3 pair (weights 3, 10, 3), whose magnitude depends least on the edge's direction.</summary>
    Scharr,

    /// <summary>Prewitt's 3 × 3 pair, weights 1, 1, 1 across the derivative.</summary>
    Prewitt,

    /// <summary>Roberts' 2 × 2 cross of diagonal differences, the pixel and the one below and to its right: the sharpest and noisiest.</summary>
    RobertsCross,

    /// <summary>Kirsch's eight 3 × 3 compass kernels (5 on one side, −3 on the others); the strongest response is kept.</summary>
    Kirsch,

    /// <summary>Robinson's eight 3 × 3 compass kernels with weights 1, 2, 1 (Sobel's, turned in 45° steps); the strongest response is kept.</summary>
    Robinson,

    /// <summary>The 3 × 3 Laplacian (8 in the middle, −1 around it), as an absolute value: edges come out as thin double lines.</summary>
    Laplacian3x3,

    /// <summary>The 5 × 5 Laplacian (24 in the middle, −1 around it), as an absolute value.</summary>
    Laplacian5x5,

    /// <summary>The 5 × 5 Laplacian of Gaussian (16 in the middle, then −2, −1), as an absolute value: smoothed against noise.</summary>
    LaplacianOfGaussian,
}
