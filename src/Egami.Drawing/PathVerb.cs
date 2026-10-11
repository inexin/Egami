namespace Egami.Drawing;

/// <summary>One drawing command of a <see cref="Path"/>; the comment on each says how many points it takes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paths"/>
/// </example>
public enum PathVerb : byte
{
    /// <summary>Starts a contour at one point.</summary>
    Move = 0,

    /// <summary>A straight line to one point.</summary>
    Line = 1,

    /// <summary>A quadratic Bézier curve: one control point and the end point.</summary>
    Quad = 2,

    /// <summary>A cubic Bézier curve: two control points and the end point.</summary>
    Cubic = 3,

    /// <summary>Closes the contour with a line back to its start; takes no point.</summary>
    Close = 4,
}
