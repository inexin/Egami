using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>Hub 2.8.6 stage B: emphasis marks and tate-chu-yoko on the canvas, in pictures and in paths.</summary>
public class EmphasisDrawingTests
{
    private static readonly Lazy<FontCollection> s_fonts = new(() =>
    {
        FontCollection fonts = new();
        fonts.Add(Typeface.FromFile(TextTests.FontPath("derived/NotoSansCJK-layout-subset.otf")));
        fonts.Add(Typeface.FromFile(TextTests.FontPath("DejaVuSans.ttf")));
        return fonts;
    });

    private static readonly Rgba8 s_red = new(220, 0, 0, 255);

    private static TextLayout Layout(TextEmphasis emphasis, WritingMode mode = WritingMode.HorizontalTb, TextCombine? combine = null, string text = "国際価値")
    {
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [.. s_fonts.Value.Families], Size = 20, Language = "ja", Emphasis = emphasis, CombineUpright = combine },
            WritingMode = mode,
        };
        return Paragraph.Create(text, style).Layout(float.PositiveInfinity);
    }

    private static Raster<Rgba8> Draw(Action<Egami.Drawing.Canvas> draw, int width = 100, int height = 50)
    {
        Raster<Rgba8> image = Raster.Create<Rgba8>(width, height);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        draw(canvas);
        return image;
    }

    private static int Reds(Raster<Rgba8> image, Func<int, int, bool>? where = null)
    {
        int reds = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y) is { R: > 150, G: < 100 })
                {
                    Assert.True(where?.Invoke(x, y) ?? true, $"red at {x},{y}");
                    reds++;
                }
            }
        }

        return reds;
    }

    [Fact]
    public void DrawTextLayoutDrawsMarksInTheirColorOverTheText()
    {
        TextLayout layout = Layout(new TextEmphasis { Color = s_red });
        Vector2 origin = new(5, 5);
        using Raster<Rgba8> drawn = Draw(c => c.DrawTextLayout(layout, origin));
        using Raster<Rgba8> byHand = Draw(c =>
        {
            foreach (LayoutRun run in layout.Lines[0].Runs)
            {
                c.DrawGlyphRun(run.Glyphs!, origin, new SolidPaint(Rgba8.Black));
            }

            foreach (LayoutRun run in layout.Lines[0].AnnotationRuns)
            {
                c.DrawGlyphRun(run.Glyphs!, origin, new SolidPaint(s_red));
            }
        });
        Advanced.Pixels.Equal(drawn, byHand);

        // Red ink only above the text's ascent.
        float above = origin.Y + layout.Lines[0].Baseline - 18;
        Assert.True(Reds(drawn, (_, y) => y < above) >= 4);
    }

    [Fact]
    public void MarksWithoutAColorTakeTheTextPaint()
    {
        TextLayout layout = Layout(new TextEmphasis());
        using Raster<Rgba8> drawn = Draw(c => c.DrawTextLayout(layout, new Vector2(5, 5)));
        Assert.Equal(0, Reds(drawn));
        float above = 5 + layout.Lines[0].Baseline - 18;
        int dark = 0;
        for (int y = 0; y < above; y++)
        {
            for (int x = 0; x < drawn.Width; x++)
            {
                dark += drawn.GetPixel(x, y).R < 100 ? 1 : 0;
            }
        }

        Assert.True(dark >= 4, $"{dark} dark pixels above the text");
    }

    [Fact]
    public void PaintWinsOverColor()
    {
        TextLayout layout = Layout(new TextEmphasis { Color = Rgba8.Black, Paint = new SolidPaint(s_red) });
        using Raster<Rgba8> drawn = Draw(c => c.DrawTextLayout(layout, new Vector2(5, 5)));
        Assert.True(Reds(drawn) >= 4);
    }

    [Fact]
    public void PicturesRecordMarks()
    {
        TextLayout layout = Layout(new TextEmphasis { Color = s_red });
        PictureRecorder recorder = new(new RectF(0, 0, 100, 50));
        recorder.DrawTextLayout(layout, new Vector2(5, 5));
        Picture picture = recorder.Finish();
        using Raster<Rgba8> played = Draw(c => c.DrawPicture(picture));
        using Raster<Rgba8> direct = Draw(c => c.DrawTextLayout(layout, new Vector2(5, 5)));
        Assert.Equal(Reds(direct), Reds(played));
    }

    [Fact]
    public void LayoutPathsHoldMarks()
    {
        TextLayout plain = Paragraph.Create("国際価値", new ParagraphStyle { DefaultStyle = new TextStyle { FontFamilies = [.. s_fonts.Value.Families], Size = 20 } }).Layout(float.PositiveInfinity);
        TextLayout layout = Layout(new TextEmphasis());
        Path path = layout.ToPath(Vector2.Zero);
        Assert.True(path.Bounds.Y < layout.Lines[0].Baseline - plain.Lines[0].Ascent, $"{path.Bounds} {layout.Lines[0].Baseline}");
    }

    [Fact]
    public void TateChuYokoDrawsUprightInItsEm()
    {
        TextLayout layout = Layout(new TextEmphasis { Color = s_red }, WritingMode.VerticalRl, TextCombine.Digits(2), "年12年");
        using Raster<Rgba8> drawn = Draw(c => c.DrawTextLayout(layout, new Vector2(5, 5)), 60, 80);

        // The digits' ink stays inside the middle em, down the line.
        int ink = 0;
        for (int y = 0; y < drawn.Height; y++)
        {
            for (int x = 0; x < drawn.Width; x++)
            {
                Rgba8 p = drawn.GetPixel(x, y);
                if (p.R < 100 && x < 5 + layout.Lines[0].Baseline + 12 && y > 5 + 20 + 1 && y < 5 + 40 - 1)
                {
                    ink++;
                }
            }
        }

        Assert.True(ink > 10, $"{ink}");

        // One red mark a character, three in all, right of the line.
        Assert.True(Reds(drawn, (x, _) => x > 5 + layout.Lines[0].Baseline) > 0);
        Assert.Equal(3, layout.Lines[0].AnnotationRuns.Sum(r => r.Glyphs!.Count));
    }
}
