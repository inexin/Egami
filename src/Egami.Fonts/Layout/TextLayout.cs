using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// A paragraph laid out at a width: its lines of glyph runs and the size of its layout box. Positions are in canvas units
/// relative to the box's top-left corner, y down; nothing is rounded to pixels. Immutable and safe to share between threads.
/// </summary>
/// <remarks>
/// In vertical layouts (<see cref="ParagraphStyle.WritingMode"/>) the width given is the lines' length, which runs down the box:
/// <see cref="Height"/> is that length and <see cref="Width"/> the lines' thicknesses added up. Positions along a line (a line's
/// <see cref="TextLine.Left"/> and <see cref="TextLine.Width"/>, a run's <see cref="LayoutRun.X"/>, a caret's
/// <see cref="CaretLine.X"/>) are then y, and positions across the lines (a line's <see cref="TextLine.Top"/>,
/// <see cref="TextLine.Baseline"/> and <see cref="TextLine.Bottom"/>) are x; glyph runs, ink, placeholder and selection
/// rectangles stay in x and y.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="layout"/>
/// </example>
public sealed partial class TextLayout
{
    internal TextLayout(Paragraph paragraph, float width, IReadOnlyList<TextLine> lines, TextSegment? hidden = null)
    {
        HiddenRange = hidden;
        (MinIntrinsicWidth, MaxIntrinsicWidth) = paragraph.IntrinsicWidths;
        Paragraph = paragraph;
        Width = width;
        Vertical = paragraph.Style.WritingMode != WritingMode.HorizontalTb;
        Lines = lines;
        LongestLine = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
        if (paragraph.Style.WritingMode != WritingMode.HorizontalTb)
        {
            // Vertical: the lines' lengths run down the box, their thicknesses across it.
            Height = width;
            Width = lines.Sum(l => l.Height);
        }
        else
        {
            Height = lines.Count == 0 ? 0 : lines[^1].Bottom;
        }
    }

    /// <summary>Gets the paragraph that was laid out.</summary>
    public Paragraph Paragraph { get; }

    /// <summary>Whether lines run vertically.</summary>
    internal bool Vertical { get; }

    /// <summary>Gets the layout box's width: the width given, or <see cref="LongestLine"/> when it was unbounded.</summary>
    public float Width { get; }

    /// <summary>Gets the layout box's height: the bottom of the last line.</summary>
    public float Height { get; }

    /// <summary>Gets the widest line's width, without its hanging trailing spaces.</summary>
    public float LongestLine { get; }

    /// <summary>Gets the lines, top to bottom.</summary>
    public IReadOnlyList<TextLine> Lines { get; }

    /// <summary>Gets the width of the widest piece that cannot break (CSS min-content): the narrowest box the text fits without
    /// overflowing. <see cref="OverflowWrap.Anywhere"/> lowers it to the widest grapheme cluster.</summary>
    public float MinIntrinsicWidth { get; }

    /// <summary>Gets the width of the widest line laid out without wrapping (CSS max-content).</summary>
    public float MaxIntrinsicWidth { get; }

    /// <summary>Gets the first line's baseline, for aligning the box on it; 0 when there are no lines.</summary>
    public float FirstBaseline => Lines.Count == 0 ? 0 : Lines[0].Baseline;

    /// <summary>Gets the last line's baseline; 0 when there are no lines.</summary>
    public float LastBaseline => Lines.Count == 0 ? 0 : Lines[^1].Baseline;

    /// <summary>Gets the union of the glyphs' ink and the placeholders' boxes, which may reach past the layout box.</summary>
    public TextBounds InkBounds => _ink ??= ComputeInk();

    /// <summary>Gets one box per placeholder of the rich text, in order; an empty box for one hidden by a line limit.</summary>
    public IReadOnlyList<TextBounds> PlaceholderRects => _placeholders ??= ComputePlaceholders();

    private TextBounds? _ink;
    private TextBounds[]? _placeholders;

    /// <summary>Finds the line holding a UTF-16 index.</summary>
    /// <param name="index">An index from 0 to the text's length; the end of the text belongs to the last line.</param>
    /// <returns>The line's index in <see cref="Lines"/>, or -1 when the index is hidden by a line limit or there are no lines.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or past the end of the text.</exception>
    public int GetLineAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Paragraph.Text.Length);
        if (HiddenRange is TextSegment hidden && index >= hidden.Start && index > (Lines.Count == 0 ? 0 : Lines[^1].Start))
        {
            return Lines.Count == 0 || index > Lines[^1].End ? -1 : Lines.Count - 1;
        }

        int lo = 0, hi = Lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (Lines[mid].Start <= index)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Lines.Count == 0 ? -1 : lo;
    }

    /// <summary>Finds the line at a position across the lines; points outside the lines take the nearest.</summary>
    /// <param name="y">A y in layout coordinates, or in vertical layouts an x.</param>
    /// <returns>The line's index in <see cref="Lines"/>, or -1 when there are no lines.</returns>
    public int GetLineAtY(float y)
    {
        int nearest = -1;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < Lines.Count; i++)
        {
            TextLine line = Lines[i];
            if (y >= line.Top && y < line.Bottom)
            {
                return i;
            }

            float d = y < line.Top ? line.Top - y : y - line.Bottom;
            if (d < distance)
            {
                (nearest, distance) = (i, d);
            }
        }

        return nearest;
    }

    private TextBounds ComputeInk()
    {
        TextBounds ink = TextBounds.Empty;
        foreach (TextLine line in Lines)
        {
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Glyphs is GlyphRun glyphs)
                {
                    ink = ink.Union(glyphs.Bounds);
                }
            }
        }

        foreach (TextBounds box in PlaceholderRects)
        {
            ink = ink.Union(box);
        }

        return ink;
    }

    private TextBounds[] ComputePlaceholders()
    {
        TextBounds[] boxes = new TextBounds[Paragraph.RichText.Placeholders.Count];
        foreach (TextLine line in Lines)
        {
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Kind == LayoutRunKind.Placeholder)
                {
                    boxes[run.Placeholder] = new TextBounds(run.X, run.BoxTop, run.X + run.Advance, run.BoxTop + Paragraph.RichText.Placeholders[run.Placeholder].Height);
                }
            }
        }

        return boxes;
    }

    /// <summary>Gets whether <see cref="ParagraphStyle.MaxLines"/> or <see cref="ParagraphStyle.MaxHeight"/> cut text off.</summary>
    public bool DidExceed => HiddenRange is not null;

    /// <summary>Gets the text a line limit hid, the ellipsis's room included, or null when nothing was cut.</summary>
    public TextSegment? HiddenRange { get; }
}

/// <summary>
/// One line of a laid-out paragraph: the text it holds, its line box and its runs in visual order. Coordinates are the layout
/// box's, or for a line from a <see cref="LineFlow"/>, the line's own box with its top at 0.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="layout"/>
/// </example>
public sealed class TextLine
{
    internal TextLine(int start, int length, float top, float ascent, float descent, float left, float width, bool hardBreak, bool ellipsized, IReadOnlyList<LayoutRun> runs)
    {
        Start = start;
        Length = length;
        Top = top;
        Ascent = ascent;
        Descent = descent;
        Left = left;
        Width = width;
        HardBreak = hardBreak;
        Ellipsized = ellipsized;
        Runs = runs;
    }

    /// <summary>Gets the UTF-16 index of the line's first character.</summary>
    public int Start { get; }

    /// <summary>Gets the number of UTF-16 units on the line, its trailing spaces and hard break included.</summary>
    public int Length { get; }

    /// <summary>Gets the index just past the line's last character.</summary>
    public int End => Start + Length;

    /// <summary>Gets the top of the line box; in vertical layouts, its left edge.</summary>
    public float Top { get; }

    /// <summary>Gets the distance from the line box's top to the baseline.</summary>
    public float Ascent { get; }

    /// <summary>Gets the distance from the baseline to the line box's bottom.</summary>
    public float Descent { get; }

    /// <summary>Gets the baseline's y.</summary>
    public float Baseline => Top + Ascent;

    /// <summary>Gets the bottom of the line box.</summary>
    public float Bottom => Top + Ascent + Descent;

    /// <summary>Gets the line box's height.</summary>
    public float Height => Ascent + Descent;

    /// <summary>Gets the x where the line's content starts on the left, hanging spaces aside.</summary>
    public float Left { get; }

    /// <summary>Gets the width of the line's content, without hanging trailing spaces.</summary>
    public float Width { get; }

    /// <summary>Gets whether the line ends at a hard break (a newline or another mandatory break).</summary>
    public bool HardBreak { get; }

    /// <summary>Gets whether the line ends with an ellipsis for text cut by a line limit.</summary>
    public bool Ellipsized { get; }

    /// <summary>Gets the runs in visual order, left to right.</summary>
    public IReadOnlyList<LayoutRun> Runs { get; }
}

/// <summary>What a <see cref="LayoutRun"/> holds.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="layout"/>
/// </example>
public enum LayoutRunKind
{
    /// <summary>Glyphs.</summary>
    Text = 0,

    /// <summary>A placeholder's box.</summary>
    Placeholder = 1,

    /// <summary>A tab's space.</summary>
    Tab = 2,

    /// <summary>A hard break or another control character, which takes no space.</summary>
    Control = 3,
}

/// <summary>A piece of a line: glyphs of one font and bidi level, or a placeholder, tab or break.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="layout"/>
/// </example>
public sealed class LayoutRun
{
    internal LayoutRun(LayoutRunKind kind, int start, int length, int bidiLevel, int styleIndex, float x, float advance, GlyphRun? glyphs, int placeholder)
    {
        Kind = kind;
        Start = start;
        Length = length;
        BidiLevel = bidiLevel;
        StyleIndex = styleIndex;
        X = x;
        Advance = advance;
        Glyphs = glyphs;
        Placeholder = placeholder;
    }

    /// <summary>Gets what the run holds.</summary>
    public LayoutRunKind Kind { get; }

    /// <summary>Gets the UTF-16 index of the run's first character.</summary>
    public int Start { get; }

    /// <summary>Gets the number of UTF-16 units in the run.</summary>
    public int Length { get; }

    /// <summary>Gets the run's bidi level after the line rules; odd levels run right to left.</summary>
    public int BidiLevel { get; }

    /// <summary>Gets the style of the run's first character: 0 for the paragraph's default style, else 1 plus the index of the
    /// rich text's span.</summary>
    public int StyleIndex { get; }

    /// <summary>Gets the x of the run's left edge.</summary>
    public float X { get; }

    /// <summary>Gets the run's width.</summary>
    public float Advance { get; }

    /// <summary>Gets the glyphs, positioned in layout coordinates with their clusters, for <see cref="LayoutRunKind.Text"/> runs;
    /// otherwise null.</summary>
    public GlyphRun? Glyphs { get; }

    /// <summary>Gets the index of the rich text's placeholder, for <see cref="LayoutRunKind.Placeholder"/> runs; otherwise -1.</summary>
    public int Placeholder { get; }

    /// <summary>The top of a placeholder's box, in layout coordinates.</summary>
    internal float BoxTop { get; init; }
}

/// <summary>
/// Lays a paragraph out one line at a time, each at its own width, so text can flow across columns and pages or around
/// images; <see cref="Paragraph.Layout"/> is built on it. Not thread-safe; make one per flow.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="flow"/>
/// </example>
public sealed class LineFlow
{
    private readonly LineBuilder _builder;
    private int _next;

    internal LineFlow(Paragraph paragraph)
    {
        Paragraph = paragraph;
        _builder = new LineBuilder(paragraph);
    }

    /// <summary>Gets the paragraph being laid out.</summary>
    public Paragraph Paragraph { get; }

    /// <summary>Gets the UTF-16 index the next line starts at.</summary>
    public int Position => _next;

    /// <summary>Gets whether every line has been laid out. An empty paragraph has no lines, and a final hard break adds none,
    /// as CSS lays text out.</summary>
    public bool IsComplete => _next >= Paragraph.Text.Length;

    /// <summary>Lays out the next line in the available width.</summary>
    /// <param name="availableWidth">The line's width in canvas units; <see cref="float.PositiveInfinity"/> for no wrapping.</param>
    /// <returns>The line, its top at 0, or null when the paragraph is done.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="availableWidth"/> is negative or not a number.</exception>
    public TextLine? NextLine(float availableWidth)
    {
        if (float.IsNaN(availableWidth) || availableWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableWidth), availableWidth, "A width is 0 or more.");
        }

        LineDraft? draft = NextDraft(availableWidth);
        return draft is null ? null : _builder.Finish(draft, availableWidth, 0);
    }

    /// <summary>Starts again from the paragraph's first line.</summary>
    public void Reset()
    {
        _next = 0;
    }

    /// <summary>
    /// Takes back the lines from <paramref name="position"/> on, so the next <see cref="NextLine(float)"/> lays out from there
    /// again: a line that did not fit at the foot of a column goes back to start the next one (<see cref="TextLine.Start"/>).
    /// </summary>
    /// <param name="position">A line's start, at most <see cref="Position"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative or past <see cref="Position"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="two-columns"/>
    /// </example>
    public void Rewind(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, _next);
        _next = position;
    }

    internal LineDraft? NextDraft(float width)
    {
        if (IsComplete)
        {
            return null;
        }

        LineDraft draft = _builder.Fill(_next, width);
        _next = draft.End;
        return draft;
    }

    internal LineBuilder Builder => _builder;
}
