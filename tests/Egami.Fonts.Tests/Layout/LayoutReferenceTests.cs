using System.Text.Json;

namespace Egami.Fonts.Tests.Layout;

/// <summary>
/// The Chromium layout references load and hold together: every case's fonts load in Egami, every width has lines in logical
/// order whose boxes stack along the block axis, and every grapheme of the text is recorded once.
/// </summary>
public sealed class LayoutReferenceTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (LayoutReferences.Case c in LayoutReferences.AllCases())
        {
            data.Add(c.Name);
        }

        return data;
    }

    [Fact]
    public void ReferencesComeFromThePinnedChromium()
    {
        foreach (string group in LayoutReferences.Groups)
        {
            Assert.Equal("129.0.6668.29", LayoutReferences.Load(group).Chromium);
        }

        Assert.Equal(12, LayoutReferences.Load("corpus").Cases.Count);
        Assert.All(LayoutReferences.Load("corpus").Cases, c => Assert.Equal(5, c.Layouts.Count));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void CaseLoadsAndItsLinesHoldTogether(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        foreach (string font in c.Fonts)
        {
            Typeface face = Typeface.FromFile(Path.Combine(TestFonts.Root, "tests", "fonts", font));
            Assert.True(face.GlyphCount > 0, font);
        }

        int placeholders = c.Text.Count(ch => ch == '\uFFFC');
        Assert.Equal(placeholders, c.Placeholders.GetArrayLength());
        foreach (JsonElement span in c.Spans.EnumerateArray())
        {
            int start = span.GetProperty("start").GetInt32();
            Assert.InRange(start + span.GetProperty("length").GetInt32(), start + 1, c.Text.Length);
        }

        string writingMode = c.Style.TryGetProperty("writingMode", out JsonElement mode) ? mode.GetString()! : "HorizontalTb";
        foreach (LayoutReferences.Layout layout in c.Layouts)
        {
            Assert.NotEmpty(layout.Lines);
            Assert.Equal(0, layout.Lines[0].Start);
            Assert.True(layout.MinContent <= layout.MaxContent + 0.001f, $"{name}: min-content above max-content");

            // Line starts in logical order, each at a grapheme boundary.
            HashSet<int> boundaries = [.. layout.Graphemes.Select(g => g.Index)];
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                Assert.Contains(layout.Lines[i].Start, boundaries);
                if (i > 0)
                {
                    Assert.True(layout.Lines[i].Start > layout.Lines[i - 1].Start, $"{name} at {layout.Width}: line starts out of order");
                }
            }

            // Every grapheme once, in order, covering the text.
            Assert.Equal(0, layout.Graphemes[0].Index);
            for (int i = 1; i < layout.Graphemes.Count; i++)
            {
                Assert.True(layout.Graphemes[i].Index > layout.Graphemes[i - 1].Index);
            }

            Assert.True(layout.Graphemes[^1].Index < c.Text.Length);

            // Line boxes: over, baseline and under (over is the right side in vertical modes), and consecutive lines stack
            // without gaps: downwards, leftwards in VerticalRl and rightwards in VerticalLr.
            float sign = writingMode == "HorizontalTb" ? 1 : -1;
            float[]? previous = null;
            foreach (LayoutReferences.Line line in layout.Lines)
            {
                if (line.Box is not float[] box)
                {
                    previous = null;
                    continue;
                }

                Assert.True((box[2] - box[0]) * sign >= 0 && (box[1] - box[0]) * sign >= 0, $"{name} at {layout.Width}: line box upside down");
                if (previous is not null)
                {
                    Assert.Equal(writingMode == "VerticalLr" ? previous[0] : previous[2], writingMode == "VerticalLr" ? box[2] : box[0], 0.001f);
                }

                previous = box;
            }
        }
    }

    [Fact]
    public void AlmostEveryLineHasABox()
    {
        int lines = 0;
        int missing = 0;
        foreach (LayoutReferences.Case c in LayoutReferences.AllCases())
        {
            foreach (LayoutReferences.Layout layout in c.Layouts)
            {
                lines += layout.Lines.Count;
                missing += layout.Lines.Count(l => l.Box is null);
            }
        }

        // The recorder leaves out a line box only where no marker position keeps the layout (narrow CJK lines whose
        // punctuation spacing changes, lines hidden by a line clamp) and the neighbouring boxes do not pin it either.
        Assert.True(missing * 20 < lines, $"{missing} of {lines} line boxes missing");
    }
}
