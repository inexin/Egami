namespace Egami.SystemDrawing;

/// <summary>A font's style: bold and italic choose or synthesize a face; underline and strikeout draw lines.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
[Flags]
public enum FontStyle
{
    /// <summary>Plain text.</summary>
    Regular = 0,

    /// <summary>Bold: the family's bold face, or a synthesized one.</summary>
    Bold = 1,

    /// <summary>Italic: the family's italic face, or a slanted one.</summary>
    Italic = 2,

    /// <summary>Underlined.</summary>
    Underline = 4,

    /// <summary>Struck out.</summary>
    Strikeout = 8,
}

/// <summary>Where text lines up in its layout rectangle, along a line or across the lines.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
public enum StringAlignment
{
    /// <summary>At the start: left for left-to-right text, the top for lines.</summary>
    Near = 0,

    /// <summary>In the middle.</summary>
    Center = 1,

    /// <summary>At the end: right for left-to-right text, the bottom for lines.</summary>
    Far = 2,
}

/// <summary>How digits would be substituted for a language; Egami keeps the digits as written.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
public enum StringDigitSubstitute
{
    /// <summary>As the user's settings say.</summary>
    User = 0,

    /// <summary>No substitution.</summary>
    None = 1,

    /// <summary>The language's national digits.</summary>
    National = 2,

    /// <summary>The script's traditional digits.</summary>
    Traditional = 3,
}

/// <summary>How DrawString and MeasureString lay text out.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
[Flags]
public enum StringFormatFlags
{
    /// <summary>Right to left.</summary>
    DirectionRightToLeft = 1,

    /// <summary>Vertical lines, top to bottom; Egami lays the text out horizontally and turns it.</summary>
    DirectionVertical = 2,

    /// <summary>Glyphs may reach past the layout rectangle.</summary>
    FitBlackBox = 4,

    /// <summary>Control characters are shown.</summary>
    DisplayFormatControl = 32,

    /// <summary>No fallback fonts for missing characters.</summary>
    NoFontFallback = 1024,

    /// <summary>Trailing spaces count in measurements.</summary>
    MeasureTrailingSpaces = 2048,

    /// <summary>No wrapping: one line per line break.</summary>
    NoWrap = 4096,

    /// <summary>Only whole lines are drawn.</summary>
    LineLimit = 8192,

    /// <summary>Text is not clipped to the layout rectangle.</summary>
    NoClip = 16384,
}

/// <summary>How a line that does not fit is cut.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
public enum StringTrimming
{
    /// <summary>Not cut; the text is clipped.</summary>
    None = 0,

    /// <summary>At a character.</summary>
    Character = 1,

    /// <summary>At a word.</summary>
    Word = 2,

    /// <summary>At a character, with an ellipsis.</summary>
    EllipsisCharacter = 3,

    /// <summary>At a word, with an ellipsis.</summary>
    EllipsisWord = 4,

    /// <summary>In the middle, keeping the start and the last part, with an ellipsis.</summary>
    EllipsisPath = 5,
}

/// <summary>The unit of a string format's measurements.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
public enum StringUnit
{
    /// <summary>World units.</summary>
    World = 0,

    /// <summary>Display units.</summary>
    Display = 1,

    /// <summary>Pixels.</summary>
    Pixel = 2,

    /// <summary>Points.</summary>
    Point = 3,

    /// <summary>Inches.</summary>
    Inch = 4,

    /// <summary>Three-hundredths of an inch.</summary>
    Document = 5,

    /// <summary>Millimeters.</summary>
    Millimeter = 6,

    /// <summary>The font's em size.</summary>
    Em = 32,
}
