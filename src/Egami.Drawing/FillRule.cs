namespace Egami.Drawing;

/// <summary>Which points a filled path covers, from how often its outline winds around them.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paths"/>
/// </example>
public enum FillRule
{
    /// <summary>A point is inside when the outline winds around it a nonzero number of times, counting direction. The default, as in SVG, PDF and Skia.</summary>
    NonZero = 0,

    /// <summary>A point is inside when a ray from it crosses the outline an odd number of times, so overlaps and nested contours make holes.</summary>
    EvenOdd = 1,
}
