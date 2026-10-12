using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Shaping;

namespace Egami.Svg.Tests;

/// <summary>
/// Task 10: text layout against hand-computed glyph positions (resvg's text chapters and Chromium's renders are gated in
/// egami-compare). The fonts are the suite's own Noto Sans, with system fonts off.
/// </summary>
public class TextTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    private static readonly Lazy<SvgFontOptions> s_fonts = new(() =>
    {
        FontCollection fonts = new();
        fonts.AddFile(Path.Combine(TestSvg.Root, "tests", "svg", "resvg", "fonts", "NotoSans-Regular.ttf"));
        return new SvgFontOptions { Collection = fonts, UseSystemFonts = false, DefaultFamily = "Noto Sans" };
    });

    private static Font Font(float size) => new(s_fonts.Value.Collection!.Match("Noto Sans", default)!.Value.Typeface, size);

    private static SvgDocument Load(string body) =>
        SvgDocument.Parse($"<svg {Ns} width='200' height='100' font-family='Noto Sans' font-size='20'>{body}</svg>", new SvgLoadOptions { Fonts = s_fonts.Value });

    private static List<SvgTextRun> Runs(SvgNode node)
    {
        List<SvgTextRun> runs = [];
        void Walk(SvgNode n)
        {
            if (n is SvgTextRun run)
            {
                runs.Add(run);
            }
            else if (n is SvgGroup group)
            {
                foreach (SvgNode child in group.Children)
                {
                    Walk(child);
                }
            }
        }

        Walk(node);
        return runs;
    }

    private static float[] Advances(string text, float size)
    {
        GlyphBuffer buffer = TextShaper.Shape(text, Font(size)).Lines[0].Runs[0].Glyphs;
        float[] advances = new float[buffer.Count];
        for (int i = 0; i < buffer.Count; i++)
        {
            advances[i] = buffer.Positions[i].XAdvance;
        }

        return advances;
    }

    [Fact]
    public void GlyphsFollowTheAdvancesFromTheStartPosition()
    {
        SvgTextRun run = Assert.Single(Runs(Load("<text x='10' y='50'>Tab</text>").RenderTree));
        float[] advances = Advances("Tab", 20);
        Assert.Equal(new Vector2(10, 50), run.Run.Positions[0]);
        Assert.Equal(10 + advances[0], run.Run.Positions[1].X, 3);
        Assert.Equal(10 + advances[0] + advances[1], run.Run.Positions[2].X, 3);
    }

    [Fact]
    public void PositionListsGoToCharactersAndDxAddsUp()
    {
        // Each absolute x starts a chunk of its own.
        List<Vector2> positions = [.. Runs(Load("<text x='10 40 70' y='50' dx='0 5 5'>abcd</text>").RenderTree).SelectMany(r => r.Run.Positions.ToArray())];
        float advanceC = Advances("c", 20)[0];

        // dx shifts its character and the rest of its chunk: b by 5, c by 5 more, d with c.
        Assert.Equal(10, positions[0].X, 3);
        Assert.Equal(45, positions[1].X, 3);
        Assert.Equal(75, positions[2].X, 3);
        Assert.Equal(75 + advanceC, positions[3].X, 3);
    }

    [Theory]
    [InlineData("start", 0)]
    [InlineData("middle", -0.5)]
    [InlineData("end", -1)]
    public void AnchorsMoveTheChunkByItsAdvance(string anchor, float fraction)
    {
        SvgTextRun run = Assert.Single(Runs(Load($"<text x='100' y='50' text-anchor='{anchor}'>Anchor</text>").RenderTree));
        float width = Advances("Anchor", 20).Sum();
        Assert.Equal(100 + (fraction * width), run.Run.Positions[0].X, 3);
    }

    [Fact]
    public void WhiteSpaceCollapsesAndTrims()
    {
        SvgTextRun run = Assert.Single(Runs(Load("<text x='0' y='50'>\n   a  \t b   </text>").RenderTree));
        Assert.Equal(3, run.Run.Count);
        Assert.Equal(Advances("a", 20)[0] + Advances(" ", 20)[0], run.Run.Positions[2].X, 3);
    }

    [Fact]
    public void RotateRepeatsItsLastValue()
    {
        SvgTextRun run = Assert.Single(Runs(Load("<text x='10' y='50' rotate='0 90'>abc</text>").RenderTree));
        Assert.Equal(3, run.Run.Transforms.Length);
        Assert.Equal(1, run.Run.Transforms[0].ScaleCos, 4);
        Assert.Equal(1, run.Run.Transforms[1].ScaleSin, 4);
        Assert.Equal(1, run.Run.Transforms[2].ScaleSin, 4);
    }

    [Fact]
    public void TextOnAStraightPathCentersEachGlyphOnIt()
    {
        SvgDocument document = Load("<path id='p' d='M 20 60 H 180'/><text><textPath xlink:href='#p'>ab</textPath></text>");
        SvgTextRun run = Assert.Single(Runs(document.RenderTree));
        float[] advances = Advances("ab", 20);
        Assert.Equal(20, run.Run.Positions[0].X, 3);
        Assert.Equal(60, run.Run.Positions[0].Y, 3);
        Assert.Equal(20 + advances[0], run.Run.Positions[1].X, 3);
    }

    [Fact]
    public void AnUnderlineIsAPathOfTheTextsPaint()
    {
        SvgDocument document = Load("<text x='10' y='50' fill='#00f' text-decoration='underline'>Under</text>");
        static IEnumerable<SvgNode> All(SvgNode n) => n is SvgGroup g ? g.Children.SelectMany(All).Prepend(n) : [n];
        SvgPath underline = All(document.RenderTree).OfType<SvgPath>().First();
        Assert.Equal(new Egami.Pixels.RgbaF32(0, 0, 1, 1), Assert.IsType<Egami.Drawing.SolidPaint>(underline.Fill).Color);
        Assert.True(underline.Path.TightBounds.Top > 50, "The underline lies below the baseline.");
    }

    [Fact]
    public void WithoutFontsTextIsReportedAndNotDrawn()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='20' height='20'><text y='10'>No fonts</text></svg>", new SvgLoadOptions { Fonts = new SvgFontOptions { UseSystemFonts = false } });
        Assert.Empty(Runs(document.RenderTree));
        Assert.Contains(document.Warnings, w => w.Id == "Svg.FontNotFound");
    }

    [Fact]
    public void FontFaceRulesAddFamilies()
    {
        string folder = Path.Combine(TestSvg.Root, "tests", "svg", "resvg", "fonts");
        SvgLoadOptions options = new()
        {
            Fonts = new SvgFontOptions { UseSystemFonts = false },
            Resolver = SvgResolvers.SameDirectory(folder),
            BaseUri = new Uri(Path.Combine(folder, "page.svg")),
        };
        SvgDocument document = SvgDocument.Parse($$"""
            <svg {{Ns}} width='100' height='40'>
              <style>@font-face { font-family: Brand; src: url(missing.woff2), url(NotoSans-Regular.ttf); }</style>
              <text x='5' y='30' font-family='Brand' font-size='20'>Brand</text>
            </svg>
            """, options);
        SvgTextRun run = Assert.Single(Runs(document.RenderTree));
        Assert.Equal("Noto Sans", run.Run.Font.Typeface.FamilyName);
        Assert.DoesNotContain(document.Warnings, w => w.Id == "Svg.FontNotFound");
    }

    [Fact]
    public void InlineSizeWrapsAsParagraphLayout()
    {
        string text = "Wrapped text fills lines no longer than the inline size, as paragraph layout fills them.";
        SvgDocument document = Load($"<text x='10' y='30' style='inline-size:120px'>{text}</text>");
        List<Vector2> positions = [.. Runs(document.RenderTree).SelectMany(r => r.Run.Positions.ToArray())];

        Fonts.Layout.TextLayout layout = Fonts.Layout.Paragraph.Create(text, new Fonts.Layout.ParagraphStyle
        {
            DefaultStyle = new Fonts.Layout.TextStyle { FontFamilies = [s_fonts.Value.Collection!.Families[0]], Size = 20, LetterSpacing = 0, WordSpacing = 0 },
        }).Layout(120);
        Vector2 origin = new(10, 30 - layout.FirstBaseline);
        List<Vector2> expected = [.. layout.Lines.SelectMany(l => l.Runs).Where(r => r.Glyphs is not null).SelectMany(r => r.Glyphs!.Positions.ToArray()).Select(p => p + origin)];
        Assert.True(layout.Lines.Count > 2);
        Assert.Equal(expected.Count, positions.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(Vector2.Distance(expected[i], positions[i]) < 1e-3f, $"glyph {i}: {positions[i]} instead of {expected[i]}");
        }
    }
}
