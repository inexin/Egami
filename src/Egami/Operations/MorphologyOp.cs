namespace Egami.Operations;

/// <summary>The operations of <see cref="RasterOps.Morphology(MorphologyOp, StructuringElement, int)"/>, all flat (gray-level) and per channel.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="morphology"/>
/// </example>
public enum MorphologyOp
{
    /// <summary>The maximum over the shape: light areas grow, dark specks vanish.</summary>
    Dilate,

    /// <summary>The minimum over the shape: light areas shrink, light specks vanish.</summary>
    Erode,

    /// <summary>Erode, then dilate: removes light details smaller than the shape and keeps the rest. Never brightens a pixel.</summary>
    Open,

    /// <summary>Dilate, then erode: fills dark gaps and holes smaller than the shape. Never darkens a pixel.</summary>
    Close,

    /// <summary>Dilate minus erode: the outlines of shapes, as wide as the shape. Alpha is kept.</summary>
    Gradient,

    /// <summary>The image minus its <see cref="Open"/>: the light details smaller than the shape. Alpha is kept.</summary>
    TopHat,

    /// <summary>The <see cref="Close"/> minus the image: the dark details smaller than the shape. Alpha is kept.</summary>
    BottomHat,
}
