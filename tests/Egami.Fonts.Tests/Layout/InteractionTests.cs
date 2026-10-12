using System.Numerics;

using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Stage D interaction (task 10): hit tests, carets and selection rectangles against Chromium.</summary>
public sealed class InteractionTests(ITestOutputHelper output)
{
    /// <summary>Chromium's layout unit snapping, a few units across a line, and its rounding of rects to the unit.</summary>
    private const float Tolerance = 0.1f;

    public static TheoryData<string> HorizontalInteractionCases()
    {
        var data = new TheoryData<string>();
        foreach (LayoutReferences.Case c in LayoutReferences.AllCases())
        {
            if (c.Name.StartsWith("interaction-", StringComparison.Ordinal))
            {
                data.Add(c.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HorizontalInteractionCases))]
    public void HitTestsEqualChromium(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        List<string> failures = [];
        int count = 0;
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            TextLayout layout = LayoutCases.Paragraph(c).Layout(reference.Width ?? float.PositiveInfinity);
            foreach (LayoutReferences.Hit hit in reference.Hits!)
            {
                if (hit.Index is not int expected)
                {
                    continue;
                }

                count++;
                int actual = layout.HitTest(new Vector2(hit.X, hit.Y)).Index;
                if (actual != expected)
                {
                    failures.Add($"{reference.Width} ({hit.X}, {hit.Y}): Chromium {expected}, Egami {actual}");
                }
            }
        }

        foreach (string f in failures.Take(30))
        {
            output.WriteLine(f);
        }

        Assert.True(count > 0);
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} hits differ: {string.Join(" | ", failures.Take(5))}");
    }

    [Theory]
    [MemberData(nameof(HorizontalInteractionCases))]
    public void CaretsEqualChromium(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        List<string> failures = [];
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            TextLayout layout = LayoutCases.Paragraph(c).Layout(reference.Width ?? float.PositiveInfinity);
            foreach (LayoutReferences.Caret caret in reference.Carets!)
            {
                if (layout.HiddenRange is TextSegment hidden && caret.Index > hidden.Start)
                {
                    continue;
                }

                // Chromium's collapsed range sits on one line: at a wrap, the line it is on picks the affinity. In vertical
                // layouts the position along the line is y and the line is found by x.
                bool vertical = layout.Paragraph.Style.WritingMode != WritingMode.HorizontalTb;
                (float along, float across) = vertical ? (caret.Y, caret.X + (caret.Length / 2)) : (caret.X, caret.Y + (caret.Length / 2));
                int line = layout.GetLineAtY(across);
                TextAffinity affinity = line >= 0 && layout.Lines[line].Start == caret.Index ? TextAffinity.Downstream : TextAffinity.Upstream;
                Caret ours = layout.GetCaret(new TextPosition(caret.Index, affinity));
                bool matches = Math.Abs(ours.Primary.X - along) <= Tolerance || (ours.Secondary is CaretLine s && Math.Abs(s.X - along) <= Tolerance);
                if (!matches)
                {
                    failures.Add($"{reference.Width} caret {caret.Index}: Chromium {along}, Egami {ours.Primary.X}/{ours.Secondary?.X}");
                }
            }
        }

        foreach (string f in failures.Take(30))
        {
            output.WriteLine(f);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} carets differ: {string.Join(" | ", failures.Take(5))}");
    }

    [Theory]
    [MemberData(nameof(HorizontalInteractionCases))]
    public void SelectionRectanglesCoverWhatChromiumsDo(string name)
    {
        // Chromium gives a rectangle per text fragment; the covered x ranges on each line, merged, are compared.
        LayoutReferences.Case c = LayoutReferences.Find(name);
        List<string> failures = [];
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            TextLayout layout = LayoutCases.Paragraph(c).Layout(reference.Width ?? float.PositiveInfinity);
            foreach (LayoutReferences.Selection selection in reference.Selections!)
            {
                if (layout.HiddenRange is not null)
                {
                    continue;
                }

                // Vertical rectangles are compared turned: lines by x, covered ranges in y.
                bool vertical = layout.Paragraph.Style.WritingMode != WritingMode.HorizontalTb;
                static TextBounds Turn(TextBounds r) => new(r.Top, r.Left, r.Bottom, r.Right);
                IEnumerable<TextBounds> theirs = selection.Rects.Select(r => new TextBounds(r[0], r[1], r[0] + r[2], r[1] + r[3]));
                IEnumerable<TextBounds> ours = layout.GetSelectionRects(selection.Start, selection.End, SelectionHeight.Tight);
                List<List<(float L, float R)>> expected = Coverage(vertical ? theirs.Select(Turn) : theirs);
                List<List<(float L, float R)>> actual = Coverage(vertical ? ours.Select(Turn) : ours);
                if (!Same(expected, actual))
                {
                    failures.Add($"{reference.Width} [{selection.Start}, {selection.End}): Chromium {Show(expected)}; Egami {Show(actual)}");
                }
            }
        }

        foreach (string f in failures.Take(10))
        {
            output.WriteLine(f);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} selections differ: {string.Join(" | ", failures.Take(2))}");
    }

    /// <summary>The merged x ranges per line (grouped by vertical centre).</summary>
    private static List<List<(float L, float R)>> Coverage(IEnumerable<TextBounds> rects)
    {
        List<List<(float L, float R)>> lines = [];
        foreach (IGrouping<int, TextBounds> line in rects.Where(r => r.Width > 0.01f).GroupBy(r => (int)MathF.Round((r.Top + r.Bottom) / 2 / 4)).OrderBy(g => g.Key))
        {
            List<(float L, float R)> merged = [];
            foreach (TextBounds r in line.OrderBy(r => r.Left))
            {
                if (merged.Count > 0 && r.Left <= merged[^1].R + Tolerance)
                {
                    merged[^1] = (merged[^1].L, Math.Max(merged[^1].R, r.Right));
                }
                else
                {
                    merged.Add((r.Left, r.Right));
                }
            }

            lines.Add(merged);
        }

        return lines;
    }

    private static bool Same(List<List<(float L, float R)>> a, List<List<(float L, float R)>> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Count == p.Second.Count
            && p.First.Zip(p.Second).All(q => Math.Abs(q.First.L - q.Second.L) <= Tolerance && Math.Abs(q.First.R - q.Second.R) <= Tolerance));

    private static string Show(List<List<(float L, float R)>> lines) => string.Join(" / ", lines.Select(l => string.Join(" ", l.Select(m => $"{m.L:0.##}-{m.R:0.##}"))));

    [Fact]
    public void CaretsMoveByGraphemeWordLineAndParagraph()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf");
        string text = "One two three four five six seven";
        TextLayout layout = Paragraph.Create(text, style).Layout(90);
        Assert.True(layout.Lines.Count >= 3);

        Assert.Equal(1, layout.MoveCaret(new TextPosition(0), CaretMove.NextGrapheme).Index);
        Assert.Equal(0, layout.MoveCaret(new TextPosition(1), CaretMove.PreviousGrapheme).Index);
        Assert.Equal(3, layout.MoveCaret(new TextPosition(0), CaretMove.NextWord).Index);
        Assert.Equal(4, layout.MoveCaret(new TextPosition(6), CaretMove.PreviousWord).Index);
        Assert.Equal(0, layout.MoveCaret(new TextPosition(9), CaretMove.ParagraphStart).Index);
        Assert.Equal(text.Length, layout.MoveCaret(new TextPosition(9), CaretMove.ParagraphEnd).Index);

        TextLine second = layout.Lines[1];
        TextPosition inSecond = new(second.Start + 1);
        Assert.Equal(second.Start, layout.MoveCaret(inSecond, CaretMove.LineStart).Index);
        TextPosition end = layout.MoveCaret(inSecond, CaretMove.LineEnd);
        Assert.Equal(new TextPosition(second.End, TextAffinity.Upstream), end);

        // An upstream position at a wrap stays on its line: its caret is at the line's end, not the next line's start.
        Caret upstream = layout.GetCaret(end);
        Caret downstream = layout.GetCaret(new TextPosition(second.End));
        Assert.True(upstream.Primary.Top < downstream.Primary.Top);

        // Up and down keep the preferred x.
        Caret here = layout.GetCaret(inSecond);
        TextPosition down = layout.MoveCaret(inSecond, CaretMove.LineDown);
        Assert.Equal(2, layout.GetLineAt(down.Index));
        Assert.Equal(here.Primary.X, layout.GetCaret(down).Primary.X, 5f);
        Assert.Equal(1, layout.GetLineAt(layout.MoveCaret(down, CaretMove.LineUp, here.Primary.X).Index));
        Assert.Equal(0, layout.MoveCaret(new TextPosition(1), CaretMove.LineUp).Index);
    }

    [Fact]
    public void BoundariesAndLigatureCarets()
    {
        ParagraphStyle style = ParagraphTests.Style("SourceSerif4-Regular.otf");
        TextLayout layout = Paragraph.Create("an office desk", style).Layout(400);
        Assert.Equal(new TextSegment(3, 6), layout.GetWordBoundary(5));
        Assert.Equal(new TextSegment(9, 1), layout.GetWordBoundary(9));
        Assert.Equal(new TextSegment(0, 14), layout.GetLineBoundary(7));

        // "ffi" is one glyph; carets inside it split it into three steps, left to right.
        float[] xs = [.. Enumerable.Range(4, 4).Select(i => layout.GetCaret(new TextPosition(i)).Primary.X)];
        Assert.True(xs[0] < xs[1] && xs[1] < xs[2] && xs[2] < xs[3]);
        Assert.Equal(xs[1] - xs[0], xs[2] - xs[1], 0.5f);

        // Selection of a range inside the ligature covers part of it.
        TextBounds part = Assert.Single(layout.GetSelectionRects(5, 6));
        Assert.Equal(xs[1], part.Left, 0.01f);
        Assert.Equal(xs[2], part.Right, 0.01f);
    }

    [Fact]
    public void BidiBoundariesHaveTwoCarets()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf", "shaping/NotoSansHebrew-Regular.ttf");
        TextLayout layout = Paragraph.Create("abc \u05D0\u05D1\u05D2 def", style).Layout(400);
        Caret caret = layout.GetCaret(new TextPosition(4));
        Assert.NotNull(caret.Secondary);
        Assert.NotEqual(caret.Primary.X, caret.Secondary!.Value.X);
    }
}
