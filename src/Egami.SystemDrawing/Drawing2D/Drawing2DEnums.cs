namespace Egami.SystemDrawing.Drawing2D;
/// <summary>Whether a transform is applied before the existing ones (<see cref="Prepend"/>, the default) or after them.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="transforms"/>
/// </example>
public enum MatrixOrder
{
    /// <summary>The new transform is applied first.</summary>
    Prepend = 0,

    /// <summary>The new transform is applied last.</summary>
    Append = 1,
}

/// <summary>A coordinate space of a <see cref="Graphics"/>: world (before any transform), page (after the world transform) or device (pixels).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="transforms"/>
/// </example>
public enum CoordinateSpace
{
    /// <summary>Before the world transform.</summary>
    World = 0,

    /// <summary>After the world transform, before the page unit and scale.</summary>
    Page = 1,

    /// <summary>Device pixels.</summary>
    Device = 2,
}

/// <summary>How a region or clip combines with another.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="clipping"/>
/// </example>
public enum CombineMode
{
    /// <summary>The new one replaces the old.</summary>
    Replace = 0,

    /// <summary>Where both are.</summary>
    Intersect = 1,

    /// <summary>Where either is.</summary>
    Union = 2,

    /// <summary>Where exactly one is.</summary>
    Xor = 3,

    /// <summary>The old less the new.</summary>
    Exclude = 4,

    /// <summary>The new less the old.</summary>
    Complement = 5,
}

/// <summary>Which points are inside a shape that crosses itself.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="shapes"/>
/// </example>
public enum FillMode
{
    /// <summary>Even-odd: inside after an odd number of crossings.</summary>
    Alternate = 0,

    /// <summary>Nonzero: inside where the winding number is not zero.</summary>
    Winding = 1,
}

/// <summary>Whether <see cref="Graphics.Flush(FlushIntention)"/> waits for drawing to finish. Egami draws immediately, so both are the same.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum FlushIntention
{
    /// <summary>Do not wait.</summary>
    Flush = 0,

    /// <summary>Wait.</summary>
    Sync = 1,
}

/// <summary>How a texture or image repeats beyond its edges.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public enum WrapMode
{
    /// <summary>Repeat.</summary>
    Tile = 0,

    /// <summary>Repeat, mirrored left to right every other time.</summary>
    TileFlipX = 1,

    /// <summary>Repeat, mirrored top to bottom every other time.</summary>
    TileFlipY = 2,

    /// <summary>Repeat, mirrored both ways.</summary>
    TileFlipXY = 3,

    /// <summary>Do not repeat.</summary>
    Clamp = 4,
}
