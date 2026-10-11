namespace Egami.Operations;

/// <summary>Which channel <see cref="RasterOps.Lighting"/> reads as the surface's height.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="lighting"/>
/// </example>
public enum HeightSource
{
    /// <summary>Alpha, as SVG and Skia do: opaque is high, transparent is low, so a logo on a transparent canvas stands up.</summary>
    Alpha = 0,

    /// <summary>The Rec. 709 luminance of the stored (encoded) color, as ImageMagick's -shade: light is high, dark is low.</summary>
    Luminance = 1,
}
