namespace Egami.SystemDrawing.Drawing2D;

/// <summary>The 53 hatch patterns of <see cref="HatchBrush"/>, each an 8 by 8 tile of foreground on background, from the device origin.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public enum HatchStyle
{
    /// <summary>GDI+'s horizontal pattern, an 8 by 8 tile.</summary>
    Horizontal = 0,

    /// <summary>GDI+'s vertical pattern, an 8 by 8 tile.</summary>
    Vertical = 1,

    /// <summary>GDI+'s forward diagonal pattern, an 8 by 8 tile.</summary>
    ForwardDiagonal = 2,

    /// <summary>GDI+'s backward diagonal pattern, an 8 by 8 tile.</summary>
    BackwardDiagonal = 3,

    /// <summary>GDI+'s cross pattern, an 8 by 8 tile.</summary>
    Cross = 4,

    /// <summary>GDI+'s diagonal cross pattern, an 8 by 8 tile.</summary>
    DiagonalCross = 5,

    /// <summary>GDI+'s percent 5 pattern, an 8 by 8 tile.</summary>
    Percent05 = 6,

    /// <summary>GDI+'s percent 10 pattern, an 8 by 8 tile.</summary>
    Percent10 = 7,

    /// <summary>GDI+'s percent 20 pattern, an 8 by 8 tile.</summary>
    Percent20 = 8,

    /// <summary>GDI+'s percent 25 pattern, an 8 by 8 tile.</summary>
    Percent25 = 9,

    /// <summary>GDI+'s percent 30 pattern, an 8 by 8 tile.</summary>
    Percent30 = 10,

    /// <summary>GDI+'s percent 40 pattern, an 8 by 8 tile.</summary>
    Percent40 = 11,

    /// <summary>GDI+'s percent 50 pattern, an 8 by 8 tile.</summary>
    Percent50 = 12,

    /// <summary>GDI+'s percent 60 pattern, an 8 by 8 tile.</summary>
    Percent60 = 13,

    /// <summary>GDI+'s percent 70 pattern, an 8 by 8 tile.</summary>
    Percent70 = 14,

    /// <summary>GDI+'s percent 75 pattern, an 8 by 8 tile.</summary>
    Percent75 = 15,

    /// <summary>GDI+'s percent 80 pattern, an 8 by 8 tile.</summary>
    Percent80 = 16,

    /// <summary>GDI+'s percent 90 pattern, an 8 by 8 tile.</summary>
    Percent90 = 17,

    /// <summary>GDI+'s light downward diagonal pattern, an 8 by 8 tile.</summary>
    LightDownwardDiagonal = 18,

    /// <summary>GDI+'s light upward diagonal pattern, an 8 by 8 tile.</summary>
    LightUpwardDiagonal = 19,

    /// <summary>GDI+'s dark downward diagonal pattern, an 8 by 8 tile.</summary>
    DarkDownwardDiagonal = 20,

    /// <summary>GDI+'s dark upward diagonal pattern, an 8 by 8 tile.</summary>
    DarkUpwardDiagonal = 21,

    /// <summary>GDI+'s wide downward diagonal pattern, an 8 by 8 tile.</summary>
    WideDownwardDiagonal = 22,

    /// <summary>GDI+'s wide upward diagonal pattern, an 8 by 8 tile.</summary>
    WideUpwardDiagonal = 23,

    /// <summary>GDI+'s light vertical pattern, an 8 by 8 tile.</summary>
    LightVertical = 24,

    /// <summary>GDI+'s light horizontal pattern, an 8 by 8 tile.</summary>
    LightHorizontal = 25,

    /// <summary>GDI+'s narrow vertical pattern, an 8 by 8 tile.</summary>
    NarrowVertical = 26,

    /// <summary>GDI+'s narrow horizontal pattern, an 8 by 8 tile.</summary>
    NarrowHorizontal = 27,

    /// <summary>GDI+'s dark vertical pattern, an 8 by 8 tile.</summary>
    DarkVertical = 28,

    /// <summary>GDI+'s dark horizontal pattern, an 8 by 8 tile.</summary>
    DarkHorizontal = 29,

    /// <summary>GDI+'s dashed downward diagonal pattern, an 8 by 8 tile.</summary>
    DashedDownwardDiagonal = 30,

    /// <summary>GDI+'s dashed upward diagonal pattern, an 8 by 8 tile.</summary>
    DashedUpwardDiagonal = 31,

    /// <summary>GDI+'s dashed horizontal pattern, an 8 by 8 tile.</summary>
    DashedHorizontal = 32,

    /// <summary>GDI+'s dashed vertical pattern, an 8 by 8 tile.</summary>
    DashedVertical = 33,

    /// <summary>GDI+'s small confetti pattern, an 8 by 8 tile.</summary>
    SmallConfetti = 34,

    /// <summary>GDI+'s large confetti pattern, an 8 by 8 tile.</summary>
    LargeConfetti = 35,

    /// <summary>GDI+'s zig zag pattern, an 8 by 8 tile.</summary>
    ZigZag = 36,

    /// <summary>GDI+'s wave pattern, an 8 by 8 tile.</summary>
    Wave = 37,

    /// <summary>GDI+'s diagonal brick pattern, an 8 by 8 tile.</summary>
    DiagonalBrick = 38,

    /// <summary>GDI+'s horizontal brick pattern, an 8 by 8 tile.</summary>
    HorizontalBrick = 39,

    /// <summary>GDI+'s weave pattern, an 8 by 8 tile.</summary>
    Weave = 40,

    /// <summary>GDI+'s plaid pattern, an 8 by 8 tile.</summary>
    Plaid = 41,

    /// <summary>GDI+'s divot pattern, an 8 by 8 tile.</summary>
    Divot = 42,

    /// <summary>GDI+'s dotted grid pattern, an 8 by 8 tile.</summary>
    DottedGrid = 43,

    /// <summary>GDI+'s dotted diamond pattern, an 8 by 8 tile.</summary>
    DottedDiamond = 44,

    /// <summary>GDI+'s shingle pattern, an 8 by 8 tile.</summary>
    Shingle = 45,

    /// <summary>GDI+'s trellis pattern, an 8 by 8 tile.</summary>
    Trellis = 46,

    /// <summary>GDI+'s sphere pattern, an 8 by 8 tile.</summary>
    Sphere = 47,

    /// <summary>GDI+'s small grid pattern, an 8 by 8 tile.</summary>
    SmallGrid = 48,

    /// <summary>GDI+'s small checker board pattern, an 8 by 8 tile.</summary>
    SmallCheckerBoard = 49,

    /// <summary>GDI+'s large checker board pattern, an 8 by 8 tile.</summary>
    LargeCheckerBoard = 50,

    /// <summary>GDI+'s outlined diamond pattern, an 8 by 8 tile.</summary>
    OutlinedDiamond = 51,

    /// <summary>GDI+'s solid diamond pattern, an 8 by 8 tile.</summary>
    SolidDiamond = 52,

    /// <summary>The same as <see cref="Cross"/>.</summary>
    LargeGrid = 4,

    /// <summary>The first style.</summary>
    Min = 0,

    /// <summary>The value GDI+ gives, the same as <see cref="Cross"/>.</summary>
    Max = 4,
}

/// <summary>The direction of a <see cref="LinearGradientBrush"/> made from a rectangle.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public enum LinearGradientMode
{
    /// <summary>Left to right.</summary>
    Horizontal = 0,

    /// <summary>Top to bottom.</summary>
    Vertical = 1,

    /// <summary>Top left to bottom right.</summary>
    ForwardDiagonal = 2,

    /// <summary>Top right to bottom left.</summary>
    BackwardDiagonal = 3,
}
