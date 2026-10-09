namespace Egami.Operations;

/// <summary>Where an image sits inside a larger area, or which part of it is kept when it is cut down.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Geometry.cs" region="crop-pad-rotate"/>
/// </example>
public enum Anchor
{
    /// <summary>The center.</summary>
    Center,

    /// <summary>The top-left corner.</summary>
    TopLeft,

    /// <summary>The middle of the top edge.</summary>
    Top,

    /// <summary>The top-right corner.</summary>
    TopRight,

    /// <summary>The middle of the left edge.</summary>
    Left,

    /// <summary>The middle of the right edge.</summary>
    Right,

    /// <summary>The bottom-left corner.</summary>
    BottomLeft,

    /// <summary>The middle of the bottom edge.</summary>
    Bottom,

    /// <summary>The bottom-right corner.</summary>
    BottomRight,
}

/// <summary>Helpers for <see cref="Anchor"/>.</summary>
internal static class AnchorExtensions
{
    /// <summary>Returns the offset that places an <paramref name="inner"/> area inside an <paramref name="outer"/> one at the anchor.</summary>
    /// <remarks>Works when the inner area is larger too: the offset is then negative.</remarks>
    internal static Point Offset(this Anchor anchor, Size outer, Size inner)
    {
        int dx = outer.Width - inner.Width;
        int dy = outer.Height - inner.Height;
        int x = anchor switch
        {
            Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => 0,
            Anchor.TopRight or Anchor.Right or Anchor.BottomRight => dx,
            _ => dx / 2,
        };
        int y = anchor switch
        {
            Anchor.TopLeft or Anchor.Top or Anchor.TopRight => 0,
            Anchor.BottomLeft or Anchor.Bottom or Anchor.BottomRight => dy,
            _ => dy / 2,
        };
        return new Point(x, y);
    }
}
