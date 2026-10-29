namespace Egami.SystemDrawing;

/// <summary>
/// A rotation clockwise and a flip for <see cref="Image.RotateFlip(RotateFlipType)"/>. Several names mean the same
/// transformation and share a value, as in System.Drawing.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="bitmap"/>
/// </example>
public enum RotateFlipType
{
    /// <summary>No change.</summary>
    RotateNoneFlipNone = 0,

    /// <summary>Rotate 90 degrees.</summary>
    Rotate90FlipNone = 1,

    /// <summary>Rotate 180 degrees.</summary>
    Rotate180FlipNone = 2,

    /// <summary>Rotate 270 degrees.</summary>
    Rotate270FlipNone = 3,

    /// <summary>Flip left to right.</summary>
    RotateNoneFlipX = 4,

    /// <summary>Rotate 90 degrees, then flip left to right.</summary>
    Rotate90FlipX = 5,

    /// <summary>Rotate 180 degrees, then flip left to right.</summary>
    Rotate180FlipX = 6,

    /// <summary>Rotate 270 degrees, then flip left to right.</summary>
    Rotate270FlipX = 7,

    /// <summary>Flip top to bottom; the same as <see cref="Rotate180FlipX"/>.</summary>
    RotateNoneFlipY = 6,

    /// <summary>Rotate 90 degrees, then flip top to bottom; the same as <see cref="Rotate270FlipX"/>.</summary>
    Rotate90FlipY = 7,

    /// <summary>Rotate 180 degrees, then flip top to bottom; the same as <see cref="RotateNoneFlipX"/>.</summary>
    Rotate180FlipY = 4,

    /// <summary>Rotate 270 degrees, then flip top to bottom; the same as <see cref="Rotate90FlipX"/>.</summary>
    Rotate270FlipY = 5,

    /// <summary>Flip both ways; the same as <see cref="Rotate180FlipNone"/>.</summary>
    RotateNoneFlipXY = 2,

    /// <summary>Rotate 90 degrees, then flip both ways; the same as <see cref="Rotate270FlipNone"/>.</summary>
    Rotate90FlipXY = 3,

    /// <summary>Rotate 180 degrees, then flip both ways; the same as <see cref="RotateNoneFlipNone"/>.</summary>
    Rotate180FlipXY = 0,

    /// <summary>Rotate 270 degrees, then flip both ways; the same as <see cref="Rotate90FlipNone"/>.</summary>
    Rotate270FlipXY = 1,
}
