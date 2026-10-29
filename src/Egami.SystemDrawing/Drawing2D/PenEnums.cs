namespace Egami.SystemDrawing.Drawing2D;

/// <summary>How a line ends: flat, square, round, a triangle, an anchor shape centered on the end, or a custom cap.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public enum LineCap
{
    /// <summary>Ends at the end point.</summary>
    Flat = 0,

    /// <summary>A half-width square beyond the end.</summary>
    Square = 1,

    /// <summary>A half circle beyond the end.</summary>
    Round = 2,

    /// <summary>A triangle beyond the end.</summary>
    Triangle = 3,

    /// <summary>No anchor; drawn flat.</summary>
    NoAnchor = 16,

    /// <summary>A square centered on the end.</summary>
    SquareAnchor = 17,

    /// <summary>A circle centered on the end.</summary>
    RoundAnchor = 18,

    /// <summary>A diamond centered on the end.</summary>
    DiamondAnchor = 19,

    /// <summary>An arrowhead at the end.</summary>
    ArrowAnchor = 20,

    /// <summary>The pen's custom cap.</summary>
    Custom = 255,

    /// <summary>The bits that mark an anchor cap.</summary>
    AnchorMask = 240,
}

/// <summary>How two segments of a line meet.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public enum LineJoin
{
    /// <summary>A sharp corner, beveled past the miter limit.</summary>
    Miter = 0,

    /// <summary>A cut corner.</summary>
    Bevel = 1,

    /// <summary>A rounded corner.</summary>
    Round = 2,

    /// <summary>A sharp corner, cut at the miter limit.</summary>
    MiterClipped = 3,
}

/// <summary>A pen's dash pattern, in multiples of its width.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public enum DashStyle
{
    /// <summary>No dashes.</summary>
    Solid = 0,

    /// <summary>Dashes of 3, gaps of 1.</summary>
    Dash = 1,

    /// <summary>Dots of 1, gaps of 1.</summary>
    Dot = 2,

    /// <summary>A dash of 3, then a dot.</summary>
    DashDot = 3,

    /// <summary>A dash of 3, then two dots.</summary>
    DashDotDot = 4,

    /// <summary>The pen's <see cref="Pen.DashPattern"/>.</summary>
    Custom = 5,
}

/// <summary>How each dash ends.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public enum DashCap
{
    /// <summary>Square ends at the dash.</summary>
    Flat = 0,

    /// <summary>Rounded ends, inside the dash.</summary>
    Round = 2,

    /// <summary>Pointed ends; drawn flat.</summary>
    Triangle = 3,
}

/// <summary>Where a pen lies along a closed shape's outline.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public enum PenAlignment
{
    /// <summary>Centered on the outline.</summary>
    Center = 0,

    /// <summary>Inside the shape.</summary>
    Inset = 1,

    /// <summary>Centered, as GDI+ draws it.</summary>
    Outset = 2,

    /// <summary>Centered, as GDI+ draws it.</summary>
    Left = 3,

    /// <summary>Centered, as GDI+ draws it.</summary>
    Right = 4,
}

/// <summary>What a pen paints with: the kind of its brush.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public enum PenType
{
    /// <summary>A solid color.</summary>
    SolidColor = 0,

    /// <summary>A hatch brush.</summary>
    HatchFill = 1,

    /// <summary>A texture brush.</summary>
    TextureFill = 2,

    /// <summary>A path gradient brush.</summary>
    PathGradient = 3,

    /// <summary>A linear gradient brush.</summary>
    LinearGradient = 4,
}
