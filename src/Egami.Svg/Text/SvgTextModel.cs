using System.Numerics;
using System.Text;

using Egami.Css;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Svg.Styling;

namespace Egami.Svg.Text;

/// <summary>A character's absolute position and relative shift from the x, y, dx and dy lists, where any applies.</summary>
internal struct SvgCharPosition
{
    public float? X;
    public float? Y;
    public float? Dx;
    public float? Dy;
}

/// <summary>The kinds of baseline-shift.</summary>
internal enum SvgBaselineShiftKind : byte
{
    Baseline,
    Sub,
    Super,
    Length,
}

/// <summary>Where a text decoration (underline, overline, line-through) takes its paint: the element that declared it.</summary>
internal sealed record SvgTextDecoration(SvgElement Element, SvgStyle Style);

/// <summary>A font with the metrics text layout reads, in pixels at its size, y down.</summary>
internal sealed class SvgTextFont
{
    public SvgTextFont(Font font)
    {
        Font = font;
        FontExtents e = font.Extents;
        Ascent = e.Ascent;
        Descent = e.Descent;
        float height = Ascent - Descent;
        XHeight = e.XHeight > 0 ? e.XHeight : height * 0.45f;
        float em = font.Size;
        UnderlineThickness = e.UnderlineThickness > 0 ? e.UnderlineThickness : em / 12;
        UnderlinePosition = e.UnderlineThickness > 0 || e.UnderlinePosition != 0 ? e.UnderlinePosition : -em / 9;
        LineThroughPosition = e.StrikeoutPosition != 0 ? e.StrikeoutPosition : XHeight / 2;

        // OS/2 ySubscriptYOffset and ySuperscriptYOffset; generic fractions of the em when the table has none.
        Subscript = em * 0.2f;
        Superscript = em * 0.4f;
        if (font.Typeface.TryGetTable(new FontTag("OS/2"), out ReadOnlyMemory<byte> os2) && os2.Length >= 26)
        {
            ReadOnlySpan<byte> t = os2.Span;
            float scale = em / font.Typeface.UnitsPerEm;
            Subscript = (short)((t[16] << 8) | t[17]) * scale;
            Superscript = (short)((t[24] << 8) | t[25]) * scale;
        }
    }

    public Font Font { get; }

    /// <summary>Gets the ascent, positive above the baseline.</summary>
    public float Ascent { get; }

    /// <summary>Gets the descent, negative below the baseline.</summary>
    public float Descent { get; }

    public float Height => Ascent - Descent;

    public float XHeight { get; }

    public float UnderlinePosition { get; }

    public float UnderlineThickness { get; }

    public float LineThroughPosition { get; }

    public float Subscript { get; }

    public float Superscript { get; }

    /// <summary>
    /// How far a baseline lies above the alphabetic one, with the fixed fractions Chromium uses (most fonts have no BASE
    /// table): the ascent for text-before-edge, half the x-height for middle, and so on.
    /// </summary>
    public float BaselineOffset(string baseline) => baseline switch
    {
        "before-edge" or "text-before-edge" or "text-top" => Ascent,
        "middle" => XHeight * 0.5f,
        "central" => Ascent - (Height * 0.5f),
        "after-edge" or "text-after-edge" or "text-bottom" or "ideographic" => Descent,
        "hanging" => Ascent * 0.8f,
        "mathematical" => Ascent * 0.5f,
        _ => 0,
    };
}

/// <summary>A run of a chunk's characters with one style: its font, paints' source, spacing, baseline and decorations.</summary>
internal sealed class SvgTextSpan
{
    /// <summary>Gets or sets the first UTF-16 index in the chunk's text.</summary>
    public int Start { get; set; }

    /// <summary>Gets or sets the index after the last.</summary>
    public int End { get; set; }

    public required SvgElement Element { get; init; }

    public required SvgStyle Style { get; init; }

    public required SvgTextFont Font { get; init; }

    public bool SmallCaps { get; init; }

    public bool Kerning { get; init; } = true;

    public bool Visible { get; init; } = true;

    public string DominantBaseline { get; init; } = "auto";

    public string AlignmentBaseline { get; init; } = "auto";

    public IReadOnlyList<(SvgBaselineShiftKind Kind, float Value)> BaselineShift { get; init; } = [];

    public float LetterSpacing { get; init; }

    public float WordSpacing { get; init; }

    public float? TextLength { get; init; }

    public bool SpacingAndGlyphs { get; init; }

    public SvgTextDecoration? Underline { get; init; }

    public SvgTextDecoration? Overline { get; init; }

    public SvgTextDecoration? LineThrough { get; init; }

    public SvgTextSpan Copy(int start, int end) => new()
    {
        Start = start,
        End = end,
        Element = Element,
        Style = Style,
        Font = Font,
        SmallCaps = SmallCaps,
        Kerning = Kerning,
        Visible = Visible,
        DominantBaseline = DominantBaseline,
        AlignmentBaseline = AlignmentBaseline,
        BaselineShift = BaselineShift,
        LetterSpacing = LetterSpacing,
        WordSpacing = WordSpacing,
        TextLength = TextLength,
        SpacingAndGlyphs = SpacingAndGlyphs,
        Underline = Underline,
        Overline = Overline,
        LineThrough = LineThrough,
    };

    public bool Contains(int index) => index >= Start && index < End;
}

/// <summary>The path a textPath lays its characters along, and where on it they start.</summary>
internal sealed record SvgTextPathFlow(Path Path, float StartOffset);

/// <summary>A text chunk (SVG 2 section 11.5.2): characters laid out and anchored together from one absolute position.</summary>
internal sealed class SvgTextChunk
{
    public float? X { get; init; }

    public float? Y { get; init; }

    public string Anchor { get; init; } = "start";

    public SvgTextPathFlow? Path { get; init; }

    /// <summary>Gets the index of the chunk's first character among the text element's characters.</summary>
    public int FirstChar { get; init; }

    public StringBuilder Text { get; } = new();

    public List<SvgTextSpan> Spans { get; } = [];

    public SvgTextSpan? SpanAt(int index)
    {
        foreach (SvgTextSpan span in Spans)
        {
            if (span.Contains(index))
            {
                return span;
            }
        }

        return null;
    }
}

/// <summary>A laid out glyph: its font, id and where its origin and baseline go (font size, y down, in the text's coordinates).</summary>
internal readonly record struct SvgPlacedGlyph(Font Font, ushort Glyph, Matrix3x2 Transform);

/// <summary>A span after layout: its glyphs and the paths of its decorations, all in the text element's coordinates.</summary>
internal sealed record SvgLaidSpan(SvgTextSpan Span, List<SvgPlacedGlyph> Glyphs, Path? Underline, Path? Overline, Path? LineThrough);
