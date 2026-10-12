using System.Numerics;

using Egami.Fonts.Shaping;
using Egami.Pixels;

namespace Egami.Fonts.Layout;

/// <summary>
/// A paint that Egami.Fonts carries for text but never reads: Egami.Drawing's <c>Paint</c> implements it, so gradients, images
/// and blend modes reach text drawn from a layout without Egami.Fonts depending on the drawing package.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public interface ITextPaint
{
}

/// <summary>
/// How a stretch of text looks. Every field is optional: an unset field takes the enclosing builder style's, then the
/// paragraph's <see cref="ParagraphStyle.DefaultStyle"/>, then Egami's default (16 pixels, black, normal line height, no
/// decoration). Fields that change shaping (fonts, size, style, variations, language, features, letter spacing) split a
/// paragraph into separately shaped runs; the others (colors, paints, background, decoration, shadows) never do, so a color
/// change inside a ligature keeps the ligature.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public sealed record class TextStyle
{
    /// <summary>Gets the font families in order of preference; later ones and the paragraph's fallback cover characters the
    /// first does not have.</summary>
    public IReadOnlyList<FontFamily>? FontFamilies { get; init; }

    /// <summary>Gets the font size in canvas units (pixels when drawn at scale 1).</summary>
    public float? Size { get; init; }

    /// <summary>Gets the weight, width and slant the families are matched against.</summary>
    public FontStyle? FontStyle { get; init; }

    /// <summary>Gets the variation axis settings for variable fonts.</summary>
    public IReadOnlyList<FontVariation>? Variations { get; init; }

    /// <summary>Gets the BCP 47 language of the text, which picks OpenType language systems and line breaking rules.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the OpenType features to turn on or off, added to the paragraph's.</summary>
    public IReadOnlyList<FontFeature>? Features { get; init; }

    /// <summary>Gets the text color, used where <see cref="Paint"/> is not set. Egami's default is opaque black.</summary>
    public Rgba8? Color { get; init; }

    /// <summary>Gets the paint the text is drawn with, such as an Egami.Drawing gradient; overrides <see cref="Color"/>.</summary>
    public ITextPaint? Paint { get; init; }

    /// <summary>Gets the color behind the text, filling each run's line box height.</summary>
    public Rgba8? Background { get; init; }

    /// <summary>Gets the paint behind the text; overrides <see cref="Background"/>.</summary>
    public ITextPaint? BackgroundPaint { get; init; }

    /// <summary>Gets the underline, overline or line-through.</summary>
    public TextDecoration? Decoration { get; init; }

    /// <summary>Gets the shadows, drawn in order under the text, as CSS's <c>text-shadow</c> list.</summary>
    public IReadOnlyList<TextShadow>? Shadows { get; init; }

    /// <summary>Gets the space added after each grapheme cluster, in canvas units (negative tightens). Non-zero spacing turns off
    /// optional ligatures, and cursive scripts such as Arabic get none, as CSS.</summary>
    public float? LetterSpacing { get; init; }

    /// <summary>Gets the space added to each word separator, in canvas units.</summary>
    public float? WordSpacing { get; init; }

    /// <summary>Gets the height of the line box this text asks for.</summary>
    public LineHeight? LineHeight { get; init; }

    /// <summary>Gets how far the text moves down from the baseline, in canvas units (negative raises it, for superscripts).</summary>
    public float? BaselineShift { get; init; }

    /// <summary>A style with every unset field taken from <paramref name="parent"/>.</summary>
    internal TextStyle InheritFrom(TextStyle? parent) => parent is null ? this : new TextStyle
    {
        FontFamilies = FontFamilies ?? parent.FontFamilies,
        Size = Size ?? parent.Size,
        FontStyle = FontStyle ?? parent.FontStyle,
        Variations = Variations ?? parent.Variations,
        Language = Language ?? parent.Language,
        Features = Features ?? parent.Features,
        Color = Color ?? parent.Color,
        Paint = Paint ?? parent.Paint,
        Background = Background ?? parent.Background,
        BackgroundPaint = BackgroundPaint ?? parent.BackgroundPaint,
        Decoration = Decoration ?? parent.Decoration,
        Shadows = Shadows ?? parent.Shadows,
        LetterSpacing = LetterSpacing ?? parent.LetterSpacing,
        WordSpacing = WordSpacing ?? parent.WordSpacing,
        LineHeight = LineHeight ?? parent.LineHeight,
        BaselineShift = BaselineShift ?? parent.BaselineShift,
    };
}

/// <summary>A shadow under text: the glyphs and decorations again, moved and blurred (CSS <c>text-shadow</c>).</summary>
/// <param name="Offset">How far the shadow moves, in canvas units (positive y is down).</param>
/// <param name="BlurRadius">The blur radius in canvas units, 0 for a sharp shadow.</param>
/// <param name="Color">The shadow's color.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public readonly record struct TextShadow(Vector2 Offset, float BlurRadius, Rgba8 Color);

/// <summary>Lines drawn with text (CSS Text Decoration 4).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public sealed record class TextDecoration
{
    /// <summary>Gets which lines are drawn.</summary>
    public TextDecorationLines Lines { get; init; } = TextDecorationLines.Underline;

    /// <summary>Gets how the lines are drawn.</summary>
    public TextDecorationStyle Style { get; init; }

    /// <summary>Gets the lines' color; the text's color when unset.</summary>
    public Rgba8? Color { get; init; }

    /// <summary>Gets the lines' paint; overrides <see cref="Color"/>.</summary>
    public ITextPaint? Paint { get; init; }

    /// <summary>Gets the line thickness in canvas units; from the font's post and OS/2 values when unset.</summary>
    public float? Thickness { get; init; }

    /// <summary>Gets how far below the baseline the underline sits, in canvas units; from the font's post table when unset.</summary>
    public float? UnderlineOffset { get; init; }

    /// <summary>Gets whether underlines and overlines leave gaps where glyphs cross them.</summary>
    public TextDecorationSkipInk SkipInk { get; init; }
}

/// <summary>Which decoration lines are drawn.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
[Flags]
public enum TextDecorationLines
{
    /// <summary>No line.</summary>
    None = 0,

    /// <summary>A line under the text.</summary>
    Underline = 1,

    /// <summary>A line over the text, at the ascent.</summary>
    Overline = 2,

    /// <summary>A line through the text.</summary>
    LineThrough = 4,
}

/// <summary>How decoration lines are drawn.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Double is CSS Text Decoration 4's name for this style.")]
public enum TextDecorationStyle
{
    /// <summary>One solid line.</summary>
    Solid = 0,

    /// <summary>Two lines one thickness apart.</summary>
    Double = 1,

    /// <summary>Round dots.</summary>
    Dotted = 2,

    /// <summary>Dashes three thicknesses long.</summary>
    Dashed = 3,

    /// <summary>A wave.</summary>
    Wavy = 4,
}

/// <summary>Whether underlines and overlines skip glyph ink.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum TextDecorationSkipInk
{
    /// <summary>Gaps where glyphs cross an underline or overline; line-through never skips.</summary>
    Auto = 0,

    /// <summary>Lines run through the glyphs.</summary>
    None = 1,
}

/// <summary>
/// The height a run asks of its line box (CSS <c>line-height</c>): <see cref="Normal"/> from the font's metrics, a
/// <see cref="Multiple"/> of the font size or an <see cref="Absolute"/> height. The extra over the font's ascent and descent is
/// split half above and half below.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public readonly record struct LineHeight
{
    private LineHeight(LineHeightKind kind, float value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Gets the font's own line height: its ascent, descent and line gap.</summary>
    public static LineHeight Normal => default;

    /// <summary>Gets how the height is given.</summary>
    public LineHeightKind Kind { get; }

    /// <summary>Gets the multiple of the font size, or the height in canvas units; 0 for <see cref="Normal"/>.</summary>
    public float Value { get; }

    /// <summary>A height that is a multiple of the font size, as CSS's unitless <c>line-height</c>.</summary>
    /// <param name="factor">The multiple, 0 or more.</param>
    /// <returns>The line height.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is negative or not finite.</exception>
    public static LineHeight Multiple(float factor) =>
        float.IsFinite(factor) && factor >= 0 ? new(LineHeightKind.Multiple, factor) : throw new ArgumentOutOfRangeException(nameof(factor), factor, "A line height is 0 or more.");

    /// <summary>A height in canvas units.</summary>
    /// <param name="height">The height, 0 or more.</param>
    /// <returns>The line height.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="height"/> is negative or not finite.</exception>
    public static LineHeight Absolute(float height) =>
        float.IsFinite(height) && height >= 0 ? new(LineHeightKind.Absolute, height) : throw new ArgumentOutOfRangeException(nameof(height), height, "A line height is 0 or more.");
}

/// <summary>How a <see cref="LineHeight"/> is given.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum LineHeightKind
{
    /// <summary>The font's own line height.</summary>
    Normal = 0,

    /// <summary>A multiple of the font size.</summary>
    Multiple = 1,

    /// <summary>A height in canvas units.</summary>
    Absolute = 2,
}
