using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// How a paragraph is laid out: its default text style, direction, alignment, line breaking, line filling, hyphens, ellipsis
/// and line limits, line box rules, tabs and writing mode. The defaults follow CSS: start-aligned, greedy filling, normal line
/// height, tab stops every 8 spaces, horizontal lines.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public sealed record class ParagraphStyle
{
    private readonly float _tabSize = 8;
    private readonly int? _maxLines;
    private readonly float? _maxHeight;

    /// <summary>Gets the default paragraph style.</summary>
    public static ParagraphStyle Default { get; } = new();

    /// <summary>Gets the text style spans inherit unset fields from; its <see cref="TextStyle.FontFamilies"/> must be set when a
    /// span's are not.</summary>
    public TextStyle DefaultStyle { get; init; } = new();

    /// <summary>Gets the paragraph direction; <see cref="TextDirection.Auto"/> takes each paragraph's from its first strong
    /// character (UAX #9 rules P2 and P3).</summary>
    public TextDirection Direction { get; init; }

    /// <summary>Gets how lines align in the layout width.</summary>
    public TextAlign Align { get; init; }

    /// <summary>Gets how the last line, and lines before a hard break, align.</summary>
    public TextAlignLast AlignLast { get; init; }

    /// <summary>Gets how justified lines stretch.</summary>
    public TextJustify Justify { get; init; }

    /// <summary>Gets how far <see cref="TextJustify.Kashida"/> lengthens words before word spaces take the rest.</summary>
    public KashidaLevel Kashida { get; init; } = KashidaLevel.Medium;

    /// <summary>Gets where lines may break.</summary>
    public LineBreakOptions LineBreaking { get; init; }

    /// <summary>Gets how lines are filled.</summary>
    public LineFill LineFill { get; init; }

    /// <summary>Gets the text drawn where a line ends at a soft hyphen: U+2010 (U+002D when the font has none), a custom
    /// string, or null for none.</summary>
    public string? Hyphen { get; init; } = "\u2010";

    /// <summary>Gets the text that ends the last line when <see cref="MaxLines"/> or <see cref="MaxHeight"/> cuts text: U+2026,
    /// a custom string, or null to cut without one.</summary>
    public string? Ellipsis { get; init; } = "\u2026";

    /// <summary>Gets the most lines laid out, or null for no limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int? MaxLines
    {
        get => _maxLines;
        init => _maxLines = value is null or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "At least one line.");
    }

    /// <summary>Gets the height the lines must fit in, in canvas units, or null for no limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not a number.</exception>
    public float? MaxHeight
    {
        get => _maxHeight;
        init => _maxHeight = value is null || value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A height is 0 or more.");
    }

    /// <summary>Gets the minimum (or, with <see cref="Strut.Force"/>, exact) line box every line has.</summary>
    public Strut? Strut { get; init; }

    /// <summary>Gets whether the first line's space above its ascent and the last line's below its descent are dropped
    /// (CSS <c>text-box-trim</c>).</summary>
    public bool TrimEdges { get; init; }

    /// <summary>Gets the distance between default tab stops, in spaces of the default font.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float TabSize
    {
        get => _tabSize;
        init => _tabSize = float.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A tab size is 0 or more.");
    }

    /// <summary>Gets explicit tab stops; past the last one, tabs fall back to <see cref="TabSize"/>.</summary>
    public IReadOnlyList<TabStop>? TabStops { get; init; }

    /// <summary>Gets whether lines run horizontally or vertically.</summary>
    public WritingMode WritingMode { get; init; }

    /// <summary>Gets how characters stand in vertical lines.</summary>
    public TextOrientation TextOrientation { get; init; }

    /// <summary>Gets the families and resolver tried after a style's own families.</summary>
    public FontFallback Fallback { get; init; } = FontFallback.None;
}

/// <summary>How lines align in their width (CSS <c>text-align</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum TextAlign
{
    /// <summary>At the start: left in left-to-right paragraphs, right in right-to-left ones.</summary>
    Start = 0,

    /// <summary>At the end.</summary>
    End = 1,

    /// <summary>At the left.</summary>
    Left = 2,

    /// <summary>At the right.</summary>
    Right = 3,

    /// <summary>Centred.</summary>
    Center = 4,

    /// <summary>Stretched to the full width, except the last line and lines before a hard break.</summary>
    Justify = 5,
}

/// <summary>How the last line, and lines before a hard break, align (CSS <c>text-align-last</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum TextAlignLast
{
    /// <summary>As <see cref="TextAlign"/>, except that justified paragraphs end at the start.</summary>
    Auto = 0,

    /// <summary>At the start.</summary>
    Start = 1,

    /// <summary>At the end.</summary>
    End = 2,

    /// <summary>At the left.</summary>
    Left = 3,

    /// <summary>At the right.</summary>
    Right = 4,

    /// <summary>Centred.</summary>
    Center = 5,

    /// <summary>Stretched to the full width.</summary>
    Justify = 6,
}

/// <summary>How justified lines stretch (CSS <c>text-justify</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum TextJustify
{
    /// <summary>Word separators in scripts that use spaces; between characters in Chinese, Japanese and scripts without spaces.</summary>
    Auto = 0,

    /// <summary>Word separators only.</summary>
    InterWord = 1,

    /// <summary>Between every two grapheme clusters, except inside cursive scripts.</summary>
    InterCharacter = 2,

    /// <summary>No stretching; justified lines align at the start.</summary>
    None = 3,

    /// <summary>Tatweels (kashida) lengthen words in Arabic and the other scripts that join with U+0640, as Word's kashida
    /// justification does, up to <see cref="ParagraphStyle.Kashida"/>; word separators take the rest, and the only stretch in other
    /// scripts.</summary>
    Kashida = 4,
}

/// <summary>How far <see cref="TextJustify.Kashida"/> lengthens a word, as Word's three kashida justifications.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="kashida"/>
/// </example>
public enum KashidaLevel
{
    /// <summary>Up to one tatweel per word (Word's <c>lowKashida</c>).</summary>
    Low = 0,

    /// <summary>Up to two tatweels per word (Word's <c>mediumKashida</c>).</summary>
    Medium = 1,

    /// <summary>Up to four tatweels per word (Word's <c>highKashida</c>).</summary>
    High = 2,
}

/// <summary>How lines are filled.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum LineFill
{
    /// <summary>Each line takes as much as fits, as browsers fill.</summary>
    Greedy = 0,

    /// <summary>Lines of even width, for headings (CSS <c>text-wrap: balance</c>); paragraphs of more than 6 lines fill
    /// greedily.</summary>
    Balanced = 1,
}

/// <summary>Whether lines run horizontally or vertically (CSS <c>writing-mode</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum WritingMode
{
    /// <summary>Horizontal lines stacked downwards.</summary>
    HorizontalTb = 0,

    /// <summary>Vertical lines stacked right to left, as Chinese and Japanese books.</summary>
    VerticalRl = 1,

    /// <summary>Vertical lines stacked left to right, as Mongolian.</summary>
    VerticalLr = 2,
}

/// <summary>How characters stand in vertical lines (CSS <c>text-orientation</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum TextOrientation
{
    /// <summary>Each character as UAX #50 says: Chinese and Japanese upright, Latin turned sideways.</summary>
    Mixed = 0,

    /// <summary>Every character upright.</summary>
    Upright = 1,

    /// <summary>The whole line turned sideways, as horizontal text rotated a quarter turn.</summary>
    Sideways = 2,
}

/// <summary>An explicit tab stop.</summary>
/// <param name="Position">The stop's distance from the line's start, in canvas units.</param>
/// <param name="Alignment">How the text after the tab aligns on the stop.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public readonly record struct TabStop(float Position, TabAlignment Alignment = TabAlignment.Start);

/// <summary>How text aligns on a tab stop.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Decimal is CSS and word processors' name for this tab stop.")]
public enum TabAlignment
{
    /// <summary>The text starts at the stop.</summary>
    Start = 0,

    /// <summary>The text ends at the stop.</summary>
    End = 1,

    /// <summary>The text is centred on the stop.</summary>
    Center = 2,

    /// <summary>The first full stop or decimal separator after the tab sits at the stop, for columns of numbers.</summary>
    Decimal = 3,
}

/// <summary>A minimum line box every line has, from a font and a line height (CSS's strut), or with <see cref="Force"/> the exact
/// line box, for line grids in forms and columns.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public sealed record class Strut
{
    /// <summary>Gets the families whose first match gives the strut's ascent and descent; the paragraph's default style's
    /// when unset.</summary>
    public IReadOnlyList<FontFamily>? FontFamilies { get; init; }

    /// <summary>Gets the strut's font size; the paragraph's default style's when unset.</summary>
    public float? Size { get; init; }

    /// <summary>Gets the strut's font style.</summary>
    public FontStyle? FontStyle { get; init; }

    /// <summary>Gets the strut's line height.</summary>
    public LineHeight LineHeight { get; init; }

    /// <summary>Gets whether every line box is exactly the strut's, whatever its text asks for.</summary>
    public bool Force { get; init; }
}
