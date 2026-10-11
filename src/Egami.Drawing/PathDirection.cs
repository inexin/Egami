namespace Egami.Drawing;

/// <summary>The direction a shape's outline runs, on a y-down screen.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paths"/>
/// </example>
public enum PathDirection
{
    /// <summary>Clockwise on screen. The default for added shapes.</summary>
    Clockwise = 0,

    /// <summary>Counterclockwise on screen: added inside a clockwise shape, it makes a hole under <see cref="FillRule.NonZero"/>.</summary>
    CounterClockwise = 1,
}
