using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>Paragraphs on the canvas (2.8.2 task 13): DrawTextLayout's order, culling, shadows, placeholders and ActualText.</summary>
public class TextLayoutDrawingTests
{
    private const string Text = "Typography quickly gives jumpy paragraphs a shape, line after line after line.";

    private static FontFamily Family()
    {
        FontCollection fonts = new();
        fonts.Add(TextTests.Noto.Value);
        return fonts.Families[0];
    }

    private static ParagraphStyle Style(TextStyle? text = null) => new()
    {
        DefaultStyle = (text ?? new TextStyle()) with { FontFamilies = [Family()], Size = 16 },
    };

    private static Raster<Rgba8> Draw(Action<RasterCanvas<Rgba8>> draw)
    {
        Raster<Rgba8> image = Raster.Create<Rgba8>(240, 120);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        draw(canvas);
        return image;
    }

    [Fact]
    public void DrawTextLayoutEqualsDrawingItsRunsAndShapes()
    {
        TextStyle decorated = new()
        {
            Color = new Rgba8(20, 40, 160, 255),
            Decoration = new TextDecoration { Lines = TextDecorationLines.Underline | TextDecorationLines.LineThrough },
        };
        TextLayout layout = Paragraph.Create(Text, Style(decorated)).Layout(220);
        Vector2 origin = new(8, 6);
        using Raster<Rgba8> drawn = Draw(c => c.DrawTextLayout(layout, origin));
        using Raster<Rgba8> byHand = Draw(c =>
        {
            SolidPaint text = new(decorated.Color!.Value);
            foreach (DecorationShape shape in layout.Decorations.Where(d => d.Line == TextDecorationLines.Underline))
            {
                c.FillPath(shape.ToPath(origin), new SolidPaint(shape.Color));
            }

            foreach (TextLine line in layout.Lines)
            {
                foreach (LayoutRun run in line.Runs.Where(r => r.Glyphs is { Count: > 0 }))
                {
                    c.DrawGlyphRun(run.Glyphs!, origin, text);
                }
            }

            foreach (DecorationShape shape in layout.Decorations.Where(d => d.Line == TextDecorationLines.LineThrough))
            {
                c.FillPath(shape.ToPath(origin), new SolidPaint(shape.Color));
            }
        });
        Advanced.Pixels.Equal(drawn, byHand);
        Assert.True(layout.Lines.Count >= 3);
    }

    [Fact]
    public void DrawTextIntoARectEqualsLayoutAndDrawTextLayout()
    {
        RectF rect = new(10, 5, 200, 110);
        ParagraphStyle style = Style();
        TextLayout? returned = null;
        using Raster<Rgba8> boxed = Draw(c => returned = c.DrawText(Text, rect, style, VerticalAlign.Bottom));
        TextLayout layout = Paragraph.Create(Text, style).Layout(rect.Width);
        using Raster<Rgba8> manual = Draw(c => c.DrawTextLayout(layout, new Vector2(rect.X, rect.Y + rect.Height - layout.Height)));
        Advanced.Pixels.Equal(boxed, manual);
        Assert.Equal(layout.Height, returned!.Height);
    }

    [Fact]
    public void LinesOutsideTheVisibleRectAreSkipped()
    {
        TextLayout layout = Paragraph.Create(Text, Style()).Layout(220);
        TextLine first = layout.Lines[0];
        RectF visible = RectF.FromEdges(0, 0, 240, first.Bottom);
        using Raster<Rgba8> culled = Draw(c => c.DrawTextLayout(layout, Vector2.Zero, visible));
        using Raster<Rgba8> all = Draw(c => c.DrawTextLayout(layout, Vector2.Zero));

        // The first line (and its neighbour, within a line's height of the rect) draw; the last does not.
        TextLine last = layout.Lines[^1];
        Assert.True(Ink(culled, 0, (int)first.Bottom) > 0);
        Assert.Equal(0, Ink(culled, (int)MathF.Ceiling(last.Top), (int)last.Bottom));
        Assert.True(Ink(all, (int)MathF.Ceiling(last.Top), (int)last.Bottom) > 0);
    }

    [Fact]
    public void ShadowsAndBackgroundsDrawBeneathTheText()
    {
        TextStyle style = new()
        {
            Color = Rgba8.Black,
            Background = new Rgba8(255, 255, 0, 255),
            Shadows = [new TextShadow(new Vector2(0, 40), 0, new Rgba8(255, 0, 0, 255))],
        };
        TextLayout layout = Paragraph.Create("Hello", Style(style)).Layout(200);
        using Raster<Rgba8> image = Draw(c => c.DrawTextLayout(layout, Vector2.Zero));
        TextLine line = layout.Lines[0];

        // Yellow behind the line, black glyphs on it, a red copy 40 below.
        Assert.Equal(new Rgba8(255, 255, 0, 255), image.GetPixel((int)(line.Left + line.Width - 1), (int)line.Top + 1));
        Assert.True(Count(image, p => p.R < 60 && p.G < 60 && p.B < 60) > 20);
        Assert.True(Count(image, p => p.R > 200 && p.G < 60 && p.B < 60) > 20);
    }

    [Fact]
    public void PlaceholdersAreHandedToThePainter()
    {
        RichTextBuilder builder = new();
        builder.Append("An ");
        builder.AddPlaceholder(new Placeholder(30, 12));
        builder.Append(" icon");
        TextLayout layout = Paragraph.Create(builder.ToRichText(), Style()).Layout(200);
        List<(int Index, RectF Box)> painted = [];
        using Raster<Rgba8> image = Draw(c => c.DrawTextLayout(layout, new Vector2(5, 7), null, (canvas, index, box) =>
        {
            painted.Add((index, box));
            canvas.FillRect(box, new SolidPaint(new Rgba8(0, 128, 0, 255)));
        }));
        (int index, RectF rect) = Assert.Single(painted);
        Assert.Equal(0, index);
        TextBounds expected = layout.PlaceholderRects[0];
        Assert.Equal(expected.Left + 5, rect.X, 0.001f);
        Assert.Equal(expected.Top + 7, rect.Y, 0.001f);
        Assert.Equal(30, rect.Width, 0.001f);
        Assert.Equal(new Rgba8(0, 128, 0, 255), image.GetPixel((int)(rect.X + 15), (int)(rect.Y + 6)));
    }

    [Fact]
    public void EllipsisAndHyphenCarryActualText()
    {
        ParagraphStyle style = Style() with { MaxLines = 1 };
        TextLayout clamped = Paragraph.Create("A line far too long to fit in this narrow box", style).Layout(120);
        RecordingCanvas canvas = new();
        canvas.DrawTextLayout(clamped, Vector2.Zero);
        string hidden = clamped.Paragraph.Text[clamped.Lines[0].Runs.Single(r => r.Length == 0 && r.Glyphs is not null).Start..clamped.Lines[0].End];
        Assert.Contains($"begin Span {hidden}", canvas.Calls);

        TextLayout hyphenated = Paragraph.Create("extra\u00ADordinarily", Style()).Layout(60);
        canvas = new RecordingCanvas();
        canvas.DrawTextLayout(hyphenated, Vector2.Zero);
        Assert.Contains("begin Span \u00AD", canvas.Calls);
    }

    private static int Ink(Raster<Rgba8> image, int top, int bottom)
    {
        int ink = 0;
        for (int y = Math.Max(0, top); y < Math.Min(image.Height, bottom); y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                ink += image.GetPixel(x, y).R < 200 ? 1 : 0;
            }
        }

        return ink;
    }

    private static int Count(Raster<Rgba8> image, Func<Rgba8, bool> match)
    {
        int count = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                count += match(image.GetPixel(x, y)) ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>Logs marked content; draws nothing.</summary>
    private sealed class RecordingCanvas() : Egami.Drawing.Canvas(300, 200)
    {
        public List<string> Calls { get; } = [];

        protected override void OnBeginMarkedContent(MarkedContent content) => Calls.Add($"begin {content.Tag} {content.ActualText}");

        protected override void OnEndMarkedContent() => Calls.Add("end");

        protected override void OnClipPath(Path path, ClipOp op, bool antialias)
        {
        }

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
        {
        }

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        {
        }
    }
}
