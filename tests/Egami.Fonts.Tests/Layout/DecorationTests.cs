using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Stage E decorations (task 12): line positions, thicknesses and skip-ink gaps against Chromium's ink.</summary>
public sealed class DecorationTests(ITestOutputHelper output)
{
    /// <summary>Chromium's ink is measured at 4 device pixels per CSS pixel, antialiased at the edges, its clips snapped to
    /// device pixels: half a pixel and a sample.</summary>
    private const float Tolerance = 0.6f;

    public static TheoryData<string> DecorationCases()
    {
        var data = new TheoryData<string>();
        foreach (LayoutReferences.Case c in LayoutReferences.AllCases())
        {
            if (c.Name.StartsWith("decoration-", StringComparison.Ordinal))
            {
                data.Add(c.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DecorationCases))]
    public void DecorationInkEqualsChromium(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        List<string> failures = [];
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            TextLayout layout = LayoutCases.Paragraph(c).Layout(reference.Width ?? float.PositiveInfinity);
            for (int li = 0; li < reference.Decorations!.Count; li++)
            {
                if (reference.Decorations[li] is not IReadOnlyList<LayoutReferences.DecorationInk> theirs)
                {
                    continue;
                }

                // Our bands: each shape's line (two for double), top to bottom.
                List<(float Top, float Bottom, List<(float L, float R)> Runs)> ours = [];
                foreach (DecorationShape shape in layout.Decorations.Where(d => d.LineIndex == li))
                {
                    List<(float, float)> runs = [.. shape.Segments.Select(s => (s.Left, s.Right))];
                    ours.Add((shape.Top, shape.Top + shape.Thickness, runs));
                    if (shape.Style == TextDecorationStyle.Double)
                    {
                        ours.Add((shape.Top + shape.Thickness + 1, shape.Top + (2 * shape.Thickness) + 1, runs));
                    }
                }

                ours.Sort((a, b) => a.Top.CompareTo(b.Top));
                if (ours.Count != theirs.Count)
                {
                    failures.Add($"{reference.Width} line {li}: Chromium {theirs.Count} bands, Egami {ours.Count}");
                    continue;
                }

                for (int b = 0; b < ours.Count; b++)
                {
                    LayoutReferences.DecorationInk ink = theirs[b];
                    if (Math.Abs(ink.Top - ours[b].Top) > Tolerance || Math.Abs(ink.Bottom - ours[b].Bottom) > Tolerance)
                    {
                        failures.Add($"{reference.Width} line {li} band {b}: Chromium {ink.Top}..{ink.Bottom}, Egami {ours[b].Top}..{ours[b].Bottom}");
                    }

                    List<(float L, float R)> runs = Snap(ours[b].Runs);
                    bool same = runs.Count == ink.Runs.Count
                        && runs.Zip(ink.Runs).All(p => Math.Abs(p.First.L - p.Second[0]) <= Tolerance && Math.Abs(p.First.R - p.Second[1]) <= Tolerance);
                    if (!same)
                    {
                        failures.Add($"{reference.Width} line {li} band {b}: Chromium {string.Join(" ", ink.Runs.Select(r => $"{r[0]:0.##}-{r[1]:0.##}"))}; "
                            + $"Egami {string.Join(" ", runs.Select(r => $"{r.L:0.##}-{r.R:0.##}"))}");
                    }
                }
            }
        }

        foreach (string f in failures.Take(20))
        {
            output.WriteLine(f);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} differ: {string.Join(" | ", failures.Take(3))}");
    }

    /// <summary>Runs as ink lands on a pixel grid: pieces narrower than a device pixel vanish, pieces closer than one merge.</summary>
    private static List<(float L, float R)> Snap(List<(float L, float R)> runs)
    {
        List<(float L, float R)> result = [];
        foreach ((float l, float r) in runs.Where(r => r.R - r.L >= 0.25f))
        {
            if (result.Count > 0 && l - result[^1].R < 0.25f)
            {
                result[^1] = (result[^1].L, r);
            }
            else
            {
                result.Add((l, r));
            }
        }

        return result;
    }

    [Fact]
    public void LinesFollowChromiumsRules()
    {
        TextLayout layout = Paragraph.Create("Hello", Decorated(TextDecorationLines.Underline | TextDecorationLines.Overline | TextDecorationLines.LineThrough, TextDecorationStyle.Solid)).Layout(400);
        TextLine line = layout.Lines[0];
        DecorationShape under = layout.Decorations.Single(d => d.Line == TextDecorationLines.Underline);
        DecorationShape over = layout.Decorations.Single(d => d.Line == TextDecorationLines.Overline);
        DecorationShape through = layout.Decorations.Single(d => d.Line == TextDecorationLines.LineThrough);

        // 16 px: a tenth of the size floored is 1; the underline a pixel below the baseline; the overline on the rounded ascent
        // (17); the line-through centred a third of it up.
        Assert.All(layout.Decorations, d => Assert.Equal(1, d.Thickness));
        Assert.Equal(line.Baseline + 1, under.Top);
        Assert.Equal(line.Baseline - 17, over.Top + over.Thickness);
        Assert.Equal(line.Baseline - 6, through.Top);
        DecorationSegment whole = Assert.Single(under.Segments);
        Assert.Equal(line.Left, whole.Left, 0.001f);
        Assert.Equal(line.Left + line.Width, whole.Right, 0.01f);
    }

    [Theory]
    [InlineData(TextDecorationStyle.Solid, 1)]
    [InlineData(TextDecorationStyle.Double, 2)]
    [InlineData(TextDecorationStyle.Dotted, 10)]
    [InlineData(TextDecorationStyle.Dashed, 5)]
    [InlineData(TextDecorationStyle.Wavy, 1)]
    public void StylesDrawTheirShapes(TextDecorationStyle style, int atLeast)
    {
        TextLayout layout = Paragraph.Create("Hello world", Decorated(TextDecorationLines.LineThrough, style)).Layout(400);
        TextSink sink = new();
        Assert.Single(layout.Decorations).AddTo(sink);
        Assert.True(sink.Verbs.Count(v => v == 'M') >= atLeast);
        Assert.Equal(sink.Verbs.Count(v => v == 'M'), sink.Verbs.Count(v => v == 'Z'));
    }

    [Fact]
    public void ToPathWritesGlyphsThenDecorations()
    {
        TextLayout plain = Paragraph.Create("Hi", ParagraphTests.Style("NotoSans-Regular.ttf")).Layout(400);
        TextLayout underlined = Paragraph.Create("Hi", Decorated(TextDecorationLines.Underline, TextDecorationStyle.Solid)).Layout(400);
        TextSink glyphs = new(), both = new();
        plain.ToPath(glyphs);
        underlined.ToPath(both);
        Assert.True(glyphs.Verbs.Count(v => v == 'Z') >= 3);
        Assert.Equal(glyphs.Verbs.Count(v => v == 'Z') + 1, both.Verbs.Count(v => v == 'Z'));

        // Glyph outlines land in layout space: y down, inside the line.
        float[] ys = [.. glyphs.Ops.Where(o => o.Length >= 2).Select(o => (float)o[^1])];
        Assert.InRange(ys.Min(), plain.Lines[0].Top, plain.Lines[0].Baseline);
        Assert.InRange(ys.Max(), plain.Lines[0].Top, plain.Lines[0].Bottom);
    }

    private static ParagraphStyle Decorated(TextDecorationLines lines, TextDecorationStyle style)
    {
        ParagraphStyle plain = ParagraphTests.Style("NotoSans-Regular.ttf");
        return plain with
        {
            DefaultStyle = plain.DefaultStyle with
            {
                Decoration = new TextDecoration { Lines = lines, Style = style, SkipInk = TextDecorationSkipInk.None },
            },
        };
    }
}
