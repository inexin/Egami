using System.Numerics;

using Egami.Pixels;

namespace Egami.Metafiles.Playback;

/// <summary>A GDI object a record created: a pen, brush, font, palette or region, kept in the object table by index.</summary>
internal abstract class GdiObject;

/// <summary>A logical or extended pen (LOGPEN, LOGPEN32, LOGPENEX).</summary>
internal sealed class GdiPen : GdiObject
{
    public const int Solid = 0, Dash = 1, Dot = 2, DashDot = 3, DashDotDot = 4, Null = 5, InsideFrame = 6, UserStyle = 7, Alternate = 8;

    /// <summary>Gets the style (PS_SOLID to PS_ALTERNATE).</summary>
    public int Style { get; init; }

    /// <summary>Gets the end cap: 0 round, 1 square, 2 flat.</summary>
    public int Cap { get; init; }

    /// <summary>Gets the join: 0 round, 1 bevel, 2 miter.</summary>
    public int Join { get; init; }

    /// <summary>Gets a value indicating whether the pen is geometric (ExtCreatePen with PS_GEOMETRIC): its dashes scale with its width.</summary>
    public bool Geometric { get; init; }

    /// <summary>Gets the width in logical units; 0 is one device pixel.</summary>
    public float Width { get; init; }

    /// <summary>Gets the color.</summary>
    public Rgba8 Color { get; init; }

    /// <summary>Gets the PS_USERSTYLE dash lengths, in logical units for geometric pens and device pixels for cosmetic ones.</summary>
    public float[]? Dashes { get; init; }

    /// <summary>Gets the stock black pen.</summary>
    public static GdiPen Black { get; } = new() { Color = new Rgba8(0, 0, 0, 255) };

    /// <summary>Gets the stock white pen.</summary>
    public static GdiPen White { get; } = new() { Color = new Rgba8(255, 255, 255, 255) };

    /// <summary>Gets the stock null pen.</summary>
    public static GdiPen None { get; } = new() { Style = Null };

    /// <summary>Reads a COLORREF; palette indices and palette-relative colors take their low bytes as RGB.</summary>
    public static Rgba8 ColorRef(uint value) => new((byte)value, (byte)(value >> 8), (byte)(value >> 16), 255);
}

/// <summary>A brush (LOGBRUSH, LOGBRUSH32, a DIB or monochrome pattern).</summary>
internal sealed class GdiBrush : GdiObject
{
    public const int Solid = 0, Null = 1, Hatched = 2, Pattern = 3, DibPattern = 5, DibPatternPt = 6;

    /// <summary>Gets the style (BS_SOLID, BS_NULL, BS_HATCHED, BS_PATTERN, BS_DIBPATTERN, BS_DIBPATTERNPT).</summary>
    public int Style { get; init; }

    /// <summary>Gets the color of a solid or hatched brush.</summary>
    public Rgba8 Color { get; init; }

    /// <summary>Gets the hatch style (HS_HORIZONTAL to HS_DIAGCROSS).</summary>
    public int Hatch { get; init; }

    /// <summary>Gets the pattern of a pattern brush, decoded.</summary>
    public Raster<Rgba8>? Image { get; init; }

    /// <summary>Gets a value indicating whether the pattern is monochrome: its 0 bits take the text color and its 1 bits the background color.</summary>
    public bool Monochrome { get; init; }

    /// <summary>Gets the monochrome pattern's bits, 1 where the pattern is set, row by row.</summary>
    public bool[]? Bits { get; init; }

    /// <summary>Gets the stock brushes by stock index.</summary>
    public static GdiBrush Stock(int index) => index switch
    {
        0 => new() { Color = new Rgba8(255, 255, 255, 255) },
        1 => new() { Color = new Rgba8(192, 192, 192, 255) },
        2 => new() { Color = new Rgba8(128, 128, 128, 255) },
        3 => new() { Color = new Rgba8(64, 64, 64, 255) },
        4 => new() { Color = new Rgba8(0, 0, 0, 255) },
        _ => new() { Style = Null },
    };
}

/// <summary>A logical font (LOGFONT): what text records draw with.</summary>
internal sealed class GdiFont : GdiObject
{
    /// <summary>Gets the height in logical units: negative for the em height, positive for the cell height, 0 for a default.</summary>
    public int Height { get; init; }

    /// <summary>Gets the average character width, 0 for the font's own.</summary>
    public int Width { get; init; }

    /// <summary>Gets the escapement in tenths of a degree, counterclockwise from the x axis.</summary>
    public int Escapement { get; init; }

    /// <summary>Gets the orientation in tenths of a degree.</summary>
    public int Orientation { get; init; }

    /// <summary>Gets the weight, 100 to 900; 0 is 400.</summary>
    public int Weight { get; init; }

    /// <summary>Gets a value indicating whether the font is italic.</summary>
    public bool Italic { get; init; }

    /// <summary>Gets a value indicating whether text is underlined.</summary>
    public bool Underline { get; init; }

    /// <summary>Gets a value indicating whether text is struck out.</summary>
    public bool StrikeOut { get; init; }

    /// <summary>Gets the character set.</summary>
    public int CharSet { get; init; }

    /// <summary>Gets the face name.</summary>
    public string Face { get; init; } = "";

    /// <summary>Gets a value indicating whether this is a stock font, whose height is in device pixels, not logical units.</summary>
    public bool Stock { get; init; }

    /// <summary>Gets the stock system font (SYSTEM_FONT, DEFAULT_GUI_FONT and the rest map to it).</summary>
    public static GdiFont System { get; } = new() { Height = 16, Weight = 700, Face = "System", Stock = true };
}

/// <summary>A logical palette: the colors palette-index COLORREFs and DIB_PAL_COLORS bitmaps refer to.</summary>
internal sealed class GdiPalette : GdiObject
{
    /// <summary>Gets the entries.</summary>
    public Rgba8[] Entries { get; init; } = [];
}

/// <summary>A WMF region object (META_CREATEREGION): its shape in logical units.</summary>
internal sealed class GdiRegion : GdiObject
{
    /// <summary>Gets the region as rectangles in logical units.</summary>
    public required Path Shape { get; init; }
}

/// <summary>A device context's state, saved and restored by SaveDC and RestoreDC.</summary>
internal sealed class DcState
{
    public int MapMode { get; set; } = 1;

    public Vector2 WindowOrg { get; set; }

    public Vector2 WindowExt { get; set; } = Vector2.One;

    public Vector2 ViewportOrg { get; set; }

    public Vector2 ViewportExt { get; set; } = Vector2.One;

    public Matrix3x2 World { get; set; } = Matrix3x2.Identity;

    public int GraphicsMode { get; set; } = 1;

    public Vector2 Position { get; set; }

    public GdiPen Pen { get; set; } = GdiPen.Black;

    public GdiBrush Brush { get; set; } = GdiBrush.Stock(0);

    public GdiFont Font { get; set; } = GdiFont.System;

    public GdiPalette? Palette { get; set; }

    public Rgba8 TextColor { get; set; } = new(0, 0, 0, 255);

    public Rgba8 BkColor { get; set; } = new(255, 255, 255, 255);

    public int BkMode { get; set; } = 2;

    public int Rop2 { get; set; } = 13;

    public int PolyFillMode { get; set; } = 1;

    public int StretchMode { get; set; } = 1;

    public uint TextAlign { get; set; }

    public float MiterLimit { get; set; } = 10;

    public bool ArcClockwise { get; set; }

    public Vector2 BrushOrg { get; set; }

    public int TextCharExtra { get; set; }

    /// <summary>Gets or sets the clip region in the canvas's device coordinates, or null for none.</summary>
    public Path? Clip { get; set; }

    public DcState Clone() => (DcState)MemberwiseClone();
}
