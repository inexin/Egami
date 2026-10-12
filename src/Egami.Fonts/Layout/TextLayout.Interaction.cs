using System.Numerics;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>Which side of a position a caret belongs to where one index has two places: at a line wrap or a bidi boundary.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
/// </example>
public enum TextAffinity
{
    /// <summary>With the character after the index: the start of the next line at a wrap.</summary>
    Downstream = 0,

    /// <summary>With the character before the index: the end of the previous line at a wrap.</summary>
    Upstream = 1,
}

/// <summary>A caret position: a UTF-16 index at a grapheme cluster boundary and the side it belongs to.</summary>
/// <param name="Index">The UTF-16 index, from 0 to the text's length.</param>
/// <param name="Affinity">Which character the position goes with where the index has two places.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
/// </example>
public readonly record struct TextPosition(int Index, TextAffinity Affinity = TextAffinity.Downstream);

/// <summary>A vertical caret segment.</summary>
/// <param name="X">The caret's x.</param>
/// <param name="Top">The top of the segment.</param>
/// <param name="Bottom">The bottom of the segment.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
/// </example>
public readonly record struct CaretLine(float X, float Top, float Bottom);

/// <summary>
/// Where a caret is drawn: the primary segment, and at a bidi boundary, where the position touches text of both directions, a
/// secondary segment at the other text's edge.
/// </summary>
/// <param name="Primary">The caret for the position's affinity.</param>
/// <param name="Secondary">The caret at the other visual edge, or null.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
/// </example>
public readonly record struct Caret(CaretLine Primary, CaretLine? Secondary);

/// <summary>How <see cref="TextLayout.MoveCaret"/> moves a caret.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
/// </example>
public enum CaretMove
{
    /// <summary>To the next grapheme cluster boundary in logical order.</summary>
    NextGrapheme = 0,

    /// <summary>To the previous grapheme cluster boundary in logical order.</summary>
    PreviousGrapheme = 1,

    /// <summary>To the next word boundary (UAX #29).</summary>
    NextWord = 2,

    /// <summary>To the previous word boundary (UAX #29).</summary>
    PreviousWord = 3,

    /// <summary>To the start of the line.</summary>
    LineStart = 4,

    /// <summary>To the end of the line's text, before its hard break.</summary>
    LineEnd = 5,

    /// <summary>To the start of the text.</summary>
    ParagraphStart = 6,

    /// <summary>To the end of the text.</summary>
    ParagraphEnd = 7,

    /// <summary>To the line above, keeping the preferred x.</summary>
    LineUp = 8,

    /// <summary>To the line below, keeping the preferred x.</summary>
    LineDown = 9,
}

/// <summary>How tall selection rectangles are.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
/// </example>
public enum SelectionHeight
{
    /// <summary>The whole line box.</summary>
    Line = 0,

    /// <summary>The fonts' ascent and descent, as browsers' range rectangles.</summary>
    Tight = 1,
}

/// <content>Hit testing, carets, caret movement and selection (design stage D).</content>
public sealed partial class TextLayout
{
    private Cell[][]? _cells;

    /// <summary>
    /// A grapheme cluster (or a placeholder, tab, break or the ellipsis) on a line: its UTF-16 range, its left and right x and
    /// the tight box of its font, and whether it runs right to left.
    /// </summary>
    private readonly record struct Cell(int Start, int End, float Left, float Right, float Top, float Bottom, bool RightToLeft, bool Break, bool Ellipsis, int BidiLevel = 0);

    private static int Level(Cell cell) => cell.BidiLevel;

    /// <summary>Finds the caret position nearest a point; points outside the lines go to the nearest line, and points left or
    /// right of a line to its visual ends.</summary>
    /// <param name="point">A point in layout coordinates.</param>
    /// <returns>The position, at a grapheme cluster boundary.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
    /// </example>
    public TextPosition HitTest(Vector2 point)
    {
        int lineIndex = GetLineAtY(point.Y);
        if (lineIndex < 0)
        {
            return new TextPosition(0);
        }

        Cell[] cells = [.. CellsOf(lineIndex).Where(c => !c.Break).OrderBy(c => c.Left)];
        if (cells.Length == 0)
        {
            return new TextPosition(Lines[lineIndex].Start);
        }

        Cell hit = point.X < cells[0].Left ? cells[0] : point.X >= cells[^1].Right ? cells[^1] : cells.FirstOrDefault(c => point.X < c.Right, cells[^1]);
        if (hit.Ellipsis)
        {
            return new TextPosition(hit.Start, TextAffinity.Upstream);
        }

        // The half of the cell the point is in picks its visual left or right edge, which is its start or end by direction.
        bool leftHalf = point.X < (hit.Left + hit.Right) / 2;

        // Where that edge meets a run of a lower bidi level, the position is the one that run's text gives the edge, as browsers
        // resolve hits at a direction change.
        int at = Array.IndexOf(cells, hit);
        int across = leftHalf ? at - 1 : at + 1;
        if (across >= 0 && across < cells.Length && Level(cells[across]) < Level(hit) && !cells[across].Ellipsis)
        {
            Cell n = cells[across];
            bool nStart = leftHalf == n.RightToLeft;
            return nStart ? new TextPosition(n.Start) : new TextPosition(n.End, TextAffinity.Upstream);
        }

        bool start = leftHalf != hit.RightToLeft;
        return start ? new TextPosition(hit.Start) : new TextPosition(hit.End, TextAffinity.Upstream);
    }

    /// <summary>Gets where a caret at a position is drawn.</summary>
    /// <param name="position">The position; an index inside a grapheme cluster moves to the cluster's start.</param>
    /// <returns>The caret: its primary segment for the position's affinity, and a secondary one at a bidi boundary.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative or past the end of the text.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
    /// </example>
    public Caret GetCaret(TextPosition position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position.Index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position.Index, Paragraph.Text.Length);
        if (Lines.Count == 0)
        {
            return new Caret(new CaretLine(0, 0, 0), null);
        }

        int index = position.Index;
        if (HiddenRange is TextSegment hidden && index >= hidden.Start)
        {
            // Positions inside the hidden text sit at the ellipsis.
            Cell ellipsis = CellsOf(Lines.Count - 1).FirstOrDefault(c => c.Ellipsis);
            float x = ellipsis.RightToLeft ? ellipsis.Right : ellipsis.Left;
            return new Caret(new CaretLine(x, ellipsis.Top, ellipsis.Bottom), null);
        }

        int line = LineOf(position);
        Cell[] cells = CellsOf(line);
        Cell? after = null, before = null;
        foreach (Cell c in cells)
        {
            if (c.Ellipsis)
            {
                continue;
            }

            if (c.Start <= index && index < c.End && c.Start != index)
            {
                index = c.Start;
            }

            if (c.Start == index && after is null)
            {
                after = c;
            }

            if (c.End == index)
            {
                before = c;
            }
        }

        CaretLine? Downstream() => after is Cell a ? new CaretLine(a.RightToLeft ? a.Right : a.Left, a.Top, a.Bottom) : null;
        CaretLine? Upstream() => before is Cell b ? new CaretLine(b.RightToLeft ? b.Left : b.Right, b.Top, b.Bottom) : null;

        CaretLine? primary = position.Affinity == TextAffinity.Upstream ? Upstream() ?? Downstream() : Downstream() ?? Upstream();
        CaretLine? other = position.Affinity == TextAffinity.Upstream ? Downstream() : Upstream();
        if (primary is not CaretLine p)
        {
            TextLine l = Lines[line];
            float x = (Paragraph.Bidi.ParagraphLevelAt(l.Start) & 1) != 0 ? l.Left + l.Width : l.Left;
            return new Caret(new CaretLine(x, l.Top, l.Bottom), null);
        }

        CaretLine? secondary = other is CaretLine o && Math.Abs(o.X - p.X) > 0.01f ? o : null;
        return new Caret(p, secondary);
    }

    /// <summary>Moves a caret.</summary>
    /// <param name="position">The caret's position.</param>
    /// <param name="move">How to move it.</param>
    /// <param name="preferredX">For <see cref="CaretMove.LineUp"/> and <see cref="CaretMove.LineDown"/>, the x to keep across
    /// lines of different lengths; <see cref="float.NaN"/> for the caret's own.</param>
    /// <returns>The new position; at the ends of the text, the end itself.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative or past the end of the text, or the move is not
    /// defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
    /// </example>
    public TextPosition MoveCaret(TextPosition position, CaretMove move, float preferredX = float.NaN)
    {
        string text = Paragraph.Text;
        ArgumentOutOfRangeException.ThrowIfNegative(position.Index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position.Index, text.Length);
        int index = position.Index;
        switch (move)
        {
            case CaretMove.NextGrapheme:
                return new TextPosition(index >= text.Length ? text.Length : TextSegmenter.NextGraphemeBoundary(text, index));
            case CaretMove.PreviousGrapheme:
                return new TextPosition(PreviousBoundary(index, i => TextSegmenter.IsGraphemeBoundary(text, i)));
            case CaretMove.NextWord:
                return new TextPosition(GetWordBoundary(Math.Min(index, Math.Max(0, text.Length - 1))) is TextSegment w && w.End > index ? w.End : text.Length);
            case CaretMove.PreviousWord:
                return new TextPosition(index == 0 ? 0 : GetWordBoundary(index - 1).Start);
            case CaretMove.LineStart:
                return Lines.Count == 0 ? new TextPosition(0) : new TextPosition(Lines[LineOf(position)].Start);
            case CaretMove.LineEnd:
                {
                    if (Lines.Count == 0)
                    {
                        return new TextPosition(0);
                    }

                    TextLine line = Lines[LineOf(position)];
                    int end = line.End;
                    while (end > line.Start && text[end - 1] is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029')
                    {
                        end--;
                    }

                    return new TextPosition(end, end == line.End && end < text.Length ? TextAffinity.Upstream : TextAffinity.Downstream);
                }

            case CaretMove.ParagraphStart:
                return new TextPosition(0);
            case CaretMove.ParagraphEnd:
                return new TextPosition(text.Length);
            case CaretMove.LineUp:
            case CaretMove.LineDown:
                {
                    if (Lines.Count == 0)
                    {
                        return position;
                    }

                    int line = LineOf(position) + (move == CaretMove.LineUp ? -1 : 1);
                    if (line < 0)
                    {
                        return new TextPosition(0);
                    }

                    if (line >= Lines.Count)
                    {
                        return new TextPosition(text.Length);
                    }

                    float x = float.IsNaN(preferredX) ? GetCaret(position).Primary.X : preferredX;
                    return HitTest(new Vector2(x, (Lines[line].Top + Lines[line].Bottom) / 2));
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(move), move, null);
        }
    }

    /// <summary>Gets the rectangles that cover a range of text: one per run of visually adjacent clusters on each line.</summary>
    /// <param name="start">The range's first UTF-16 index.</param>
    /// <param name="end">The index just past the range.</param>
    /// <param name="height">Whether the rectangles cover the line boxes or the fonts' ascent and descent.</param>
    /// <returns>The rectangles, line by line, left to right.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not inside the text.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
    /// </example>
    public IReadOnlyList<TextBounds> GetSelectionRects(int start, int end, SelectionHeight height = SelectionHeight.Line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, Paragraph.Text.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, end);
        List<TextBounds> rects = [];
        for (int i = 0; i < Lines.Count; i++)
        {
            TextLine line = Lines[i];
            if (line.End <= start || line.Start >= end)
            {
                continue;
            }

            TextBounds? current = null;
            // Every cluster the range touches, as browsers select whole clusters.
            foreach (Cell c in CellsOf(i).Where(c => !c.Ellipsis && c.Start < end && c.End > start).OrderBy(c => c.Left))
            {
                float top = height == SelectionHeight.Line ? line.Top : c.Top;
                float bottom = height == SelectionHeight.Line ? line.Bottom : c.Bottom;
                if (current is TextBounds r && Math.Abs(c.Left - r.Right) < 0.01f)
                {
                    current = new TextBounds(r.Left, Math.Min(r.Top, top), c.Right, Math.Max(r.Bottom, bottom));
                }
                else
                {
                    if (current is TextBounds done)
                    {
                        rects.Add(done);
                    }

                    current = new TextBounds(c.Left, top, c.Right, bottom);
                }
            }

            if (current is TextBounds last)
            {
                rects.Add(last);
            }
        }

        return rects;
    }

    /// <summary>Gets the word (UAX #29 segment) holding an index: a word, or the spaces or punctuation between words.</summary>
    /// <param name="index">A UTF-16 index; the end of the text gives the last segment.</param>
    /// <returns>The segment.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or past the end of the text.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
    /// </example>
    public TextSegment GetWordBoundary(int index)
    {
        string text = Paragraph.Text;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, text.Length);
        TextSegment last = new(0, 0);
        foreach (TextSegment word in TextSegmenter.Words(text))
        {
            if (index < word.End)
            {
                return word;
            }

            last = word;
        }

        return last;
    }

    /// <summary>Gets the line holding an index, as a range: its text, trailing spaces and hard break.</summary>
    /// <param name="index">A UTF-16 index; the end of the text belongs to the last line.</param>
    /// <returns>The line's range, or an empty one at 0 when there are no lines.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or past the end of the text.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="carets"/>
    /// </example>
    public TextSegment GetLineBoundary(int index)
    {
        int line = GetLineAt(index);
        return line < 0 ? new TextSegment(0, 0) : new TextSegment(Lines[line].Start, Lines[line].Length);
    }

    /// <summary>The line a position is on: at a wrap, the line before for an upstream position.</summary>
    private int LineOf(TextPosition position)
    {
        int line = Math.Max(0, GetLineAt(position.Index));
        if (position.Affinity == TextAffinity.Upstream && line > 0 && position.Index == Lines[line].Start)
        {
            line--;
        }

        return line;
    }

    private static int PreviousBoundary(int index, Func<int, bool> isBoundary)
    {
        for (int i = index - 1; i > 0; i--)
        {
            if (isBoundary(i))
            {
                return i;
            }
        }

        return 0;
    }

    private Cell[] CellsOf(int line)
    {
        _cells ??= new Cell[Lines.Count][];
        return _cells[line] ??= BuildCells(Lines[line]);
    }

    /// <summary>The line's cells: each grapheme cluster's share of its glyph cluster (split at GDEF ligature carets, else
    /// evenly), and the glyphless runs.</summary>
    private Cell[] BuildCells(TextLine line)
    {
        string text = Paragraph.Text;
        List<Cell> cells = [];
        foreach (LayoutRun run in line.Runs)
        {
            bool rtl = (run.BidiLevel & 1) != 0;
            if (run.Glyphs is not GlyphRun glyphs)
            {
                (float top, float bottom) = (line.Top, line.Bottom);
                cells.Add(new Cell(run.Start, run.Start + run.Length, run.X, run.X + run.Advance, top, bottom, rtl, run.Kind == LayoutRunKind.Control, false, run.BidiLevel));
                continue;
            }

            (float a, float d, _) = LineBuilder.Rounded(glyphs.Font);
            float boxTop = line.Baseline - a, boxBottom = line.Baseline + d;
            if (run.Length == 0)
            {
                cells.Add(new Cell(run.Start, run.Start, run.X, run.X + run.Advance, boxTop, boxBottom, rtl, false, true, run.BidiLevel));
                continue;
            }

            // Glyph clusters: their pen extents and first glyph, in visual order.
            ReadOnlySpan<int> clusters = glyphs.Clusters;
            Dictionary<int, (float Left, float Right, ushort Glyph, int Count)> extents = [];
            float pen = run.X;
            for (int g = 0; g < glyphs.Count; g++)
            {
                float advance = glyphs.AdvanceAt(g);
                int c = clusters[g];
                extents[c] = extents.TryGetValue(c, out var e) ? (Math.Min(e.Left, pen), Math.Max(e.Right, pen + advance), e.Glyph, e.Count + 1) : (pen, pen + advance, glyphs.Glyphs[g], 1);
                pen += advance;
            }

            int[] starts = [.. extents.Keys.Order()];
            for (int k = 0; k < starts.Length; k++)
            {
                int cStart = starts[k];
                int cEnd = k + 1 < starts.Length ? starts[k + 1] : run.Start + run.Length;
                (float left, float right, ushort glyph, int count) = extents[cStart];
                List<int> bounds = [cStart];
                for (int i = TextSegmenter.NextGraphemeBoundary(text, cStart); i < cEnd; i = TextSegmenter.NextGraphemeBoundary(text, i))
                {
                    bounds.Add(i);
                }

                bounds.Add(cEnd);
                int parts = bounds.Count - 1;
                float[] edges = SplitCluster(glyphs.Font, glyph, count, parts, right - left);
                for (int p = 0; p < parts; p++)
                {
                    // Edges run in logical order: from the left in left-to-right runs, from the right in right-to-left ones.
                    float x0 = rtl ? right - edges[p + 1] : left + edges[p];
                    float x1 = rtl ? right - edges[p] : left + edges[p + 1];
                    cells.Add(new Cell(bounds[p], bounds[p + 1], x0, x1, boxTop, boxBottom, rtl, false, false, run.BidiLevel));
                }
            }
        }

        return [.. cells];
    }

    /// <summary>Where the graphemes of a glyph cluster split it, from its logical start: a single ligature glyph's GDEF caret
    /// values when the font gives one per inner boundary, else equal shares.</summary>
    private static float[] SplitCluster(Font font, ushort glyph, int glyphCount, int parts, float width)
    {
        float[] edges = new float[parts + 1];
        edges[parts] = width;
        if (parts == 1)
        {
            return edges;
        }

        Span<short> carets = stackalloc short[parts - 1];
        int found = glyphCount == 1 ? OpenTypeLayoutTables.For(font.Typeface).Gdef.GetLigatureCarets(glyph, carets) : 0;
        for (int p = 1; p < parts; p++)
        {
            edges[p] = found == parts - 1 ? Math.Clamp(carets[p - 1] * font.Scale, 0, width) : width * p / parts;
        }

        return edges;
    }
}
