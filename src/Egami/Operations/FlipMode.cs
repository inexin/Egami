namespace Egami.Operations;

/// <summary>Which way <see cref="RasterOps.Flip"/> mirrors the image.</summary>
public enum FlipMode
{
    /// <summary>Left to right: each row is reversed.</summary>
    Horizontal,

    /// <summary>Top to bottom: the row order is reversed.</summary>
    Vertical,

    /// <summary>Both ways, which is the same as rotating by 180°.</summary>
    Both,
}
