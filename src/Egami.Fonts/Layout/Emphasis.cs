using System.Globalization;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;
using Egami.Pixels;

namespace Egami.Fonts.Layout;

/// <summary>
/// Emphasis marks (CSS <c>text-emphasis</c>, Word's emphasis marks): a small mark beside each character of a stretch of text,
/// as East Asian text stresses words. Set it on <see cref="TextStyle.Emphasis"/>.
/// </summary>
/// <remarks>
/// One mark goes beside each grapheme cluster, centred on its advance; spaces, control characters and punctuation get none
/// (CSS Text Decoration 3), and a tate-chu-yoko run (<see cref="TextStyle.CombineUpright"/>) gets one. Marks are half the font
/// size and stand outside ruby on the same side; lines grow to hold them unless their line height already does. Laid out
/// marks come back as <see cref="LayoutRunKind.Emphasis"/> runs in <see cref="TextLine.AnnotationRuns"/>.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="emphasis"/>
/// </example>
public sealed record class TextEmphasis
{
    /// <summary>Gets the mark's shape; <see cref="EmphasisMark.Dot"/> by default, <see cref="EmphasisMark.None"/> for no marks.</summary>
    public EmphasisMark Mark { get; init; } = EmphasisMark.Dot;

    /// <summary>Gets whether the mark is filled (●) or open (○); <see cref="EmphasisMark.Custom"/> marks ignore it.</summary>
    public bool Filled { get; init; } = true;

    /// <summary>Gets the mark for <see cref="EmphasisMark.Custom"/>: its first grapheme cluster, such as Word's comma "、".</summary>
    public string? Custom { get; init; }

    /// <summary>Gets which side of the text the marks go on.</summary>
    public EmphasisPosition Position { get; init; }

    /// <summary>Gets the marks' color; the text's color when unset and no <see cref="Paint"/> is set.</summary>
    public Rgba8? Color { get; init; }

    /// <summary>Gets the marks' paint, which wins over <see cref="Color"/>; the text's paint when neither is set.</summary>
    public ITextPaint? Paint { get; init; }

    /// <summary>The mark's text: CSS Text Decoration 3's characters for each shape, or the first grapheme of <see cref="Custom"/>.</summary>
    internal string MarkText()
    {
        if (!Enum.IsDefined(Mark))
        {
            throw new ArgumentException($"{Mark} is not an emphasis mark.", nameof(Mark));
        }

        if (!Enum.IsDefined(Position))
        {
            throw new ArgumentException($"{Position} is not an emphasis position.", nameof(Position));
        }

        if (Mark == EmphasisMark.Custom)
        {
            if (string.IsNullOrEmpty(Custom))
            {
                throw new ArgumentException("A custom emphasis mark needs its text.", nameof(Custom));
            }

            return Custom[..TextSegmenter.NextGraphemeBoundary(Custom, 0)];
        }

        return (Mark, Filled) switch
        {
            (EmphasisMark.Dot, true) => "•",
            (EmphasisMark.Dot, false) => "◦",
            (EmphasisMark.Circle, true) => "●",
            (EmphasisMark.Circle, false) => "○",
            (EmphasisMark.DoubleCircle, true) => "◉",
            (EmphasisMark.DoubleCircle, false) => "◎",
            (EmphasisMark.Triangle, true) => "▲",
            (EmphasisMark.Triangle, false) => "△",
            (_, true) => "﹅",
            _ => "﹆",
        };
    }
}

/// <summary>The shape of an emphasis mark, as CSS <c>text-emphasis-style</c> names them.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="emphasis"/>
/// </example>
public enum EmphasisMark
{
    /// <summary>No mark: a span's way to switch off the emphasis it inherits, as Word's <c>w:em="none"</c>.</summary>
    None = 0,

    /// <summary>A small dot: • filled, ◦ open (Word's dot). The default.</summary>
    Dot = 1,

    /// <summary>A large circle: ● filled, ○ open.</summary>
    Circle = 2,

    /// <summary>A double circle: ◉ filled, ◎ open.</summary>
    DoubleCircle = 3,

    /// <summary>A triangle: ▲ filled, △ open.</summary>
    Triangle = 4,

    /// <summary>A sesame dot: ﹅ filled, ﹆ open, the default for vertical Japanese text in CSS.</summary>
    Sesame = 5,

    /// <summary>The mark in <see cref="TextEmphasis.Custom"/>.</summary>
    Custom = 6,
}

/// <summary>Which side of the text emphasis marks go on.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="emphasis"/>
/// </example>
public enum EmphasisPosition
{
    /// <summary>Over the text in horizontal lines, right of it in vertical lines.</summary>
    Over = 0,

    /// <summary>Under the text in horizontal lines, left of it in vertical lines.</summary>
    Under = 1,
}

/// <summary>The shaped emphasis marks of a paragraph's styles.</summary>
internal sealed class EmphasisTable
{
    private EmphasisTable(Mark?[] byStyle) => ByStyle = byStyle;

    /// <summary>Each style's mark, null for styles without emphasis.</summary>
    public Mark?[] ByStyle { get; }

    /// <summary>Shapes the marks of the styles that set <see cref="TextStyle.Emphasis"/>; null when none does.</summary>
    public static EmphasisTable? Create(ParagraphStyle paragraph, ResolvedStyle[] styles, int count, FontCache fonts)
    {
        Mark?[]? marks = null;
        Dictionary<(TextEmphasis, int), Mark>? shaped = null;
        for (int i = 0; i < count; i++)
        {
            ResolvedStyle style = styles[i];
            if (style.Style.Emphasis is not TextEmphasis emphasis || emphasis.Mark == EmphasisMark.None || style.Primary is null)
            {
                continue;
            }

            marks ??= new Mark?[styles.Length];
            shaped ??= [];
            if (shaped.TryGetValue((emphasis, style.ShapingGroup), out Mark? same))
            {
                marks[i] = same;
                continue;
            }

            string text = emphasis.MarkText();
            ResolvedStyle markStyle = ResolvedStyle.Resolve(StyleOf(style.Style, style.Size), styles[0], true, fonts);
            // Marks stand upright in vertical lines, shaped top to bottom so that the font's vertical origin places them.
            bool vertical = paragraph.WritingMode != WritingMode.HorizontalTb;
            (Font font, GlyphBuffer glyphs) = Itemizer.ShapeAlone(text, markStyle, paragraph.Fallback, fonts, vertical ? TextDirection.TopToBottom : TextDirection.LeftToRight);
            (float a, float d, _) = LineBuilder.Rounded(font);
            float advance = 0;
            foreach (GlyphPosition p in glyphs.Positions)
            {
                advance += vertical ? -p.YAdvance : p.XAdvance;
            }

            Mark mark = new(font, glyphs, advance, a, d, emphasis.Position == EmphasisPosition.Over, emphasis);
            shaped[(emphasis, style.ShapingGroup)] = mark;
            marks[i] = mark;
        }

        return marks is null ? null : new EmphasisTable(marks);
    }

    /// <summary>A mark's style: the text's at half its size, without what marks do not take.</summary>
    private static TextStyle StyleOf(TextStyle style, float size) => style with
    {
        Size = size / 2,
        Background = null,
        BackgroundPaint = null,
        Decoration = null,
        LetterSpacing = null,
        WordSpacing = null,
        LineHeight = null,
        BaselineShift = null,
        Emphasis = null,
        CombineUpright = null,
    };

    /// <summary>Whether a grapheme cluster starting with a code point takes a mark: not separators, controls, unassigned
    /// characters or punctuation (CSS Text Decoration 3).</summary>
    public static bool TakesMark(int cp) => UnicodeData.CategoryOf(cp) is not (UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
        or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
        or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
        or UnicodeCategory.OtherPunctuation);

    /// <summary>
    /// A shaped mark: its glyphs at half size, shaped in the line's direction (top to bottom, upright, in vertical lines), their
    /// advance along the line, and the font's rounded ascent and descent.
    /// </summary>
    public sealed record Mark(Font Font, GlyphBuffer Glyphs, float Advance, float Ascent, float Descent, bool Over, TextEmphasis Emphasis)
    {
        public float Thickness => Ascent + Descent;
    }
}
