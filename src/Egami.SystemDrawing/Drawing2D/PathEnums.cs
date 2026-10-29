namespace Egami.SystemDrawing.Drawing2D;

/// <summary>How <see cref="GraphicsPath.Warp(PointF[], RectangleF, Matrix, WarpMode, float)"/> maps a rectangle onto four points.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="paths"/>
/// </example>
public enum WarpMode
{
    /// <summary>A perspective map.</summary>
    Perspective = 0,

    /// <summary>A bilinear map.</summary>
    Bilinear = 1,
}

/// <summary>The type of a point of a <see cref="GraphicsPath"/>: its kind in the low bits and flags above.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="paths"/>
/// </example>
public enum PathPointType
{
    /// <summary>Starts a figure.</summary>
    Start = 0,

    /// <summary>Ends a line.</summary>
    Line = 1,

    /// <summary>A point of a cubic Bezier.</summary>
    Bezier = 3,

    /// <summary>The bits of the kind.</summary>
    PathTypeMask = 7,

    /// <summary>Not used.</summary>
    DashMode = 16,

    /// <summary>A marker ends after this point.</summary>
    PathMarker = 32,

    /// <summary>The figure closes after this point.</summary>
    CloseSubpath = 128,

    /// <summary>A point of a cubic Bezier.</summary>
    Bezier3 = 3,
}
