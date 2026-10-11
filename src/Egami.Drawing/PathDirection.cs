namespace Egami.Drawing;

/// <summary>The direction a shape's outline runs, on a y-down screen.</summary>
public enum PathDirection
{
    /// <summary>Clockwise on screen. The default for added shapes.</summary>
    Clockwise = 0,

    /// <summary>Counterclockwise on screen: added inside a clockwise shape, it makes a hole under <see cref="FillRule.NonZero"/>.</summary>
    CounterClockwise = 1,
}
