using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Stage D metrics (task 9): sizes, intrinsic widths, baselines, ink, line lookup and placeholder boxes.</summary>
public sealed class MetricsTests(ITestOutputHelper output)
{
    /// <summary>Chromium's layout unit; its intrinsic widths add up items each snapped up to it.</summary>
    private const float Unit = 1f / 64;

    [Theory]
    [MemberData(nameof(LineBoxTests.HorizontalCases), MemberType = typeof(LineBoxTests))]
    public void IntrinsicWidthsEqualChromium(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        if (LineFillTests.KnownDifferences.Any(k => k.Case == name))
        {
            return;
        }

        TextLayout layout = LayoutCases.Paragraph(c).Layout(float.PositiveInfinity);
        LayoutReferences.Layout reference = c.Layouts[0];

        // Chromium snaps each inline item (runs and spans) up to its unit before adding them.
        int items = layout.Lines.Sum(l => l.Runs.Count) + (2 * c.Spans.GetArrayLength()) + 1;
        output.WriteLine($"min {reference.MinContent} / {layout.MinIntrinsicWidth}, max {reference.MaxContent} / {layout.MaxIntrinsicWidth}");
        Assert.InRange(layout.MaxIntrinsicWidth, reference.MaxContent - (items * Unit) - 0.01f, reference.MaxContent + 0.01f);
        Assert.InRange(layout.MinIntrinsicWidth, reference.MinContent - (items * Unit) - 0.01f, reference.MinContent + 0.01f);
    }

    [Fact]
    public void PlaceholderBoxesEqualChromium()
    {
        LayoutReferences.Case c = LayoutReferences.Find("placeholders");
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            TextLayout layout = LayoutCases.Paragraph(c).Layout(reference.Width!.Value);
            int k = 0;
            foreach (LayoutReferences.Grapheme g in reference.Graphemes.Where(g => c.Text[g.Index] == '\uFFFC'))
            {
                TextBounds box = layout.PlaceholderRects[k++];
                Assert.Equal(g.Rect![1], box.Top, 0.01f);
                Assert.Equal(g.Rect[3], box.Height, 0.01f);
                Assert.InRange(box.Left, g.Rect[0] - (8 * Unit) - 0.01f, g.Rect[0] + Unit + 0.01f);
            }

            Assert.Equal(6, k);
        }
    }

    [Fact]
    public void SizesBaselinesInkAndLookup()
    {
        LayoutReferences.Case c = LayoutReferences.Find("latin");
        TextLayout layout = LayoutCases.Paragraph(c).Layout(200);
        Assert.Equal(200, layout.Width);
        Assert.Equal(layout.Lines[^1].Bottom, layout.Height);
        Assert.Equal(layout.Lines.Max(l => l.Width), layout.LongestLine);
        Assert.Equal(layout.Lines[0].Baseline, layout.FirstBaseline);
        Assert.Equal(layout.Lines[^1].Baseline, layout.LastBaseline);

        TextBounds ink = layout.InkBounds;
        Assert.True(ink.Top > 0 && ink.Top < layout.Lines[0].Baseline);
        Assert.True(ink.Bottom > layout.Lines[^1].Baseline && ink.Bottom <= layout.Height + 1);
        Assert.True(ink.Left >= 0 && ink.Right <= 200 + 1);

        foreach ((TextLine line, int i) in layout.Lines.Select((l, i) => (l, i)))
        {
            Assert.Equal(i, layout.GetLineAt(line.Start));
            Assert.Equal(i, layout.GetLineAt(line.End - 1));
            Assert.Equal(i, layout.GetLineAtY((line.Top + line.Bottom) / 2));
        }

        Assert.Equal(layout.Lines.Count - 1, layout.GetLineAt(c.Text.Length));
        Assert.Equal(0, layout.GetLineAtY(-50));
        Assert.Equal(layout.Lines.Count - 1, layout.GetLineAtY(1e6f));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetLineAt(c.Text.Length + 1));
    }
}
