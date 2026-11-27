using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>Hub 2.8.6 stage A: ruby on the canvas, in pictures and in paths.</summary>
public class RubyDrawingTests
{
    private static readonly Lazy<Typeface> s_cjk = new(() => Typeface.FromFile(TextTests.FontPath("derived/NotoSansCJK-layout-subset.otf")));

    private static readonly Rgba8 s_red = new(220, 0, 0, 255);

    private static TextLayout Layout()
    {
        FontCollection fonts = new();
        fonts.Add(s_cjk.Value);
        ParagraphStyle style = new() { DefaultStyle = new TextStyle { FontFamilies = [fonts.Families[0]], Size = 20, Language = "ja" } };
        RichText text = new RichTextBuilder()
            .Append("の国際の")
            .AddRuby(new RubyAnnotation(1, 2, "こくさい") { Style = new TextStyle { Color = s_red } })
            .ToRichText();
        return Paragraph.Create(text, style).Layout(float.PositiveInfinity);
    }

    private static Raster<Rgba8> Draw(Action<Egami.Drawing.Canvas> draw)
    {
        Raster<Rgba8> image = Raster.Create<Rgba8>(100, 50);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        draw(canvas);
        return image;
    }

    [Fact]
    public void DrawTextLayoutDrawsRubyInItsOwnPaint()
    {
        TextLayout layout = Layout();
        Vector2 origin = new(5, 5);
        using Raster<Rgba8> drawn = Draw(c => c.DrawTextLayout(layout, origin));
        using Raster<Rgba8> byHand = Draw(c =>
        {
            foreach (LayoutRun run in layout.Lines[0].Runs.Where(r => r.Glyphs is { Count: > 0 }))
            {
                c.DrawGlyphRun(run.Glyphs!, origin, new SolidPaint(Rgba8.Black));
            }

            foreach (LayoutRun run in layout.Lines[0].AnnotationRuns)
            {
                c.DrawGlyphRun(run.Glyphs!, origin, new SolidPaint(s_red));
            }
        });
        Advanced.Pixels.Equal(drawn, byHand);

        // Red ink only in the ruby's box, above the base.
        TextBounds box = layout.GetRubyBounds(0);
        int reds = 0;
        for (int y = 0; y < drawn.Height; y++)
        {
            for (int x = 0; x < drawn.Width; x++)
            {
                Rgba8 p = drawn.GetPixel(x, y);
                if (p.R > 150 && p.G < 100)
                {
                    reds++;
                    Assert.InRange(y, origin.Y + box.Top - 1, origin.Y + box.Bottom + 1);
                }
            }
        }

        Assert.True(reds > 20, $"{reds} red pixels");
    }

    [Fact]
    public void PicturesRecordRuby()
    {
        TextLayout layout = Layout();
        PictureRecorder recorder = new(new RectF(0, 0, 100, 50));
        recorder.DrawTextLayout(layout, new Vector2(5, 5));
        Picture picture = recorder.Finish();
        using Raster<Rgba8> played = Draw(c => c.DrawPicture(picture));
        using Raster<Rgba8> direct = Draw(c => c.DrawTextLayout(layout, new Vector2(5, 5)));
        Assert.Contains(Enumerable.Range(0, played.Width * played.Height), i => played.GetPixel(i % played.Width, i / played.Width) is { R: > 150, G: < 100 });
        int differ = 0;
        for (int y = 0; y < played.Height; y++)
        {
            for (int x = 0; x < played.Width; x++)
            {
                differ += Math.Abs(played.GetPixel(x, y).R - direct.GetPixel(x, y).R) > 64 ? 1 : 0;
            }
        }

        Assert.True(differ < 10, $"{differ} pixels differ");
    }

    [Fact]
    public void LayoutPathsHoldRuby()
    {
        TextLayout layout = Layout();
        Path path = layout.ToPath(Vector2.Zero);
        TextBounds ruby = layout.GetRubyBounds(0);
        Assert.True(path.Bounds.Y < ruby.Bottom - 2, $"{path.Bounds} {ruby}");
        Assert.True(path.Bounds.Y < layout.Lines[0].Baseline - 20);
    }
}
