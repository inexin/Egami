using System.Numerics;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>Text on the canvas: shaping, measuring, the glyph mask cache and the path route agree.</summary>
public class TextTests
{
    internal static readonly Lazy<Typeface> Noto = new(() => Typeface.FromFile(FontPath("NotoSans-Regular.ttf")));

    internal static readonly Lazy<Typeface> Source = new(() => Typeface.FromFile(FontPath("SourceSans3-Regular.otf")));

    [Fact]
    public void ShapePlacesGlyphsAtTheirAdvancesWithKerning()
    {
        var font = new Font(Noto.Value, 20);
        GlyphRun run = font.Shape("AVAV");
        Assert.Equal(4, run.Count);
        float kern = Noto.Value.GetKerning(run.Glyphs[0], run.Glyphs[1]) * font.Scale;
        Assert.NotEqual(0, kern);
        Assert.Equal(font.GetAdvance(run.Glyphs[0]) + kern, run.Positions[1].X, 3);
        GlyphRun plain = (font with { Kerning = false }).Shape("AVAV");
        Assert.Equal(font.GetAdvance(plain.Glyphs[0]), plain.Positions[1].X, 3);
        Assert.True(plain.Advance > run.Advance);
    }

    [Fact]
    public void MeasuredWidthsEqualDrawnAdvances()
    {
        var font = new Font(Noto.Value, 16);
        foreach (string text in new[] { "Hello, World!", "AVATAR Toyota", "a\nlonger second line", string.Empty })
        {
            TextMeasure m = font.MeasureText(text);
            GlyphRun run = font.Shape(text);
            Assert.Equal(run.Advance, m.Advance);
            Assert.Equal(run.Bounds, m.InkBounds);
            Assert.Equal(text.Count(c => c == '\n') + 1, m.LineCount);
            Assert.Equal(font.LineHeight, m.LineHeight);
            Assert.Equal(m.Ascent - m.Descent + ((m.LineCount - 1) * m.LineHeight), m.Height);
        }
    }

    [Fact]
    public void ClustersMapBackToTheText()
    {
        var font = new Font(Noto.Value, 12);
        const string text = "a\U0001F600b\r\nc\u0007d\re";
        GlyphRun run = font.Shape(text);
        Assert.Equal(text, run.Text);
        Assert.Equal([0, 1, 3, 6, 8, 10], run.Clusters.ToArray());
        Assert.Equal(0, run.Glyphs[1]);
        float line = font.LineHeight;
        Assert.Equal([0f, 0, 0, line, line, 2 * line], run.Positions.ToArray().Select(p => p.Y).ToArray());
        Assert.Equal(0, run.Positions[3].X);
        Assert.Equal(3, font.MeasureText(text).LineCount);
    }

    [Fact]
    public void TextDrawsInsideItsInkBounds()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(200, 60);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas())
        {
            canvas.Clear(Rgba8.White);
            canvas.DrawText("Egami fonts", new Vector2(10, 40), new Font(Noto.Value, 24), Rgba8.Black);
        }

        TextBounds ink = new Font(Noto.Value, 24).MeasureText("Egami fonts").InkBounds.Offset(new Vector2(10, 40));
        int dark = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).R < 255)
                {
                    dark++;
                    Assert.InRange(x, MathF.Floor(ink.Left) - 1, MathF.Ceiling(ink.Right) + 1);
                    Assert.InRange(y, MathF.Floor(ink.Top) - 1, MathF.Ceiling(ink.Bottom) + 1);
                }
            }
        }

        Assert.True(dark > 300);
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf", 9)]
    [InlineData("NotoSans-Regular.ttf", 16)]
    [InlineData("SourceSans3-Regular.otf", 12)]
    [InlineData("SourceSans3-Regular.otf", 48)]
    public void CachedAndUncachedPixelsAreIdentical(string file, float size)
    {
        Typeface face = file.StartsWith("Noto", StringComparison.Ordinal) ? Noto.Value : Source.Value;
        long limit = GlyphCache.ByteLimit;
        try
        {
            GlyphCache.Clear();
            using Raster<Rgba8> cold = Draw(face, size);
            using Raster<Rgba8> warm = Draw(face, size);
            GlyphCache.ByteLimit = 0;
            using Raster<Rgba8> uncached = Draw(face, size);
            AssertSame(cold, warm, 0);
            AssertSame(cold, uncached, 0);
        }
        finally
        {
            GlyphCache.ByteLimit = limit;
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(300)]
    public void TextAtWholePixelsEqualsFillingItsPath(float size)
    {
        // Glyphs far enough apart that no pixel holds two of them, at whole-pixel origins.
        var font = new Font(Noto.Value, size);
        ushort[] glyphs = [.. "Rag&"u8.ToArray().Select(c => Noto.Value.GetGlyph(c))];
        Vector2[] positions = [.. glyphs.Select((_, i) => new Vector2(i * (int)(size * 1.5f), 0))];
        GlyphRun run = GlyphRun.Create(font, glyphs, positions);
        int width = (int)(size * 6.5f), height = (int)(size * 1.6f);
        var origin = new Vector2(3, (int)(size * 1.1f));
        using Raster<Rgba8> text = Raster.Create<Rgba8>(width, height);
        using Raster<Rgba8> path = Raster.Create<Rgba8>(width, height);
        using (RasterCanvas<Rgba8> canvas = text.CreateCanvas())
        {
            canvas.DrawGlyphRun(run, origin, Rgba8.Black);
        }

        using (RasterCanvas<Rgba8> canvas = path.CreateCanvas())
        {
            canvas.FillPath(run.ToPath(origin), Rgba8.Black);
        }

        AssertSame(path, text, size > RasterCanvas<Rgba8>.MaxCachedEm ? 0 : 1);
    }

    [Fact]
    public void RotatedTextFillsAsAPath()
    {
        var font = new Font(Source.Value, 20);
        using Raster<Rgba8> a = Raster.Create<Rgba8>(80, 80);
        using Raster<Rgba8> b = Raster.Create<Rgba8>(80, 80);
        using (RasterCanvas<Rgba8> canvas = a.CreateCanvas())
        {
            canvas.Rotate(30, new Vector2(40, 40));
            canvas.DrawText("Rot", new Vector2(20, 45), font, Rgba8.Black);
        }

        using (RasterCanvas<Rgba8> canvas = b.CreateCanvas())
        {
            canvas.Rotate(30, new Vector2(40, 40));
            canvas.FillPath(font.GetTextPath("Rot", new Vector2(20, 45)), Rgba8.Black);
        }

        AssertSame(a, b, 0);
    }

    [Fact]
    public void ClipsApplyToText()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(100, 40);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas())
        {
            canvas.ClipRect(new RectF(0, 0, 30, 40));
            canvas.DrawText("MMMMMM", new Vector2(2, 30), new Font(Noto.Value, 24), Rgba8.Black);
        }

        for (int y = 0; y < 40; y++)
        {
            for (int x = 30; x < 100; x++)
            {
                Assert.Equal(0, image.GetPixel(x, y).A);
            }
        }

        Assert.NotEqual(0, image.GetPixel(10, 20).A + image.GetPixel(12, 25).A + image.GetPixel(5, 15).A);
    }

    [Fact]
    public void GlyphPathsAreInCanvasUnitsYDown()
    {
        var font = new Font(Noto.Value, 100);
        ushort h = Noto.Value.GetGlyph('H');
        Path path = font.GetGlyphPath(h);
        GlyphExtents e = font.GetGlyphExtents(h);
        Assert.Equal(e.XMin, path.TightBounds.Left, 2);
        Assert.Equal(-e.YMax, path.TightBounds.Top, 2);
        Assert.Equal(e.YMin, -path.TightBounds.Bottom, 2);
        Assert.Equal(RectF.FromEdges(1, 2, 3, 4), new TextBounds(1, 2, 3, 4).ToRectF());
    }

    [Fact]
    public void BadArgumentsThrowAndEmptyTextDrawsNothing()
    {
        using TraceCanvas canvas = new(50, 50);
        var font = new Font(Noto.Value, 12);
        Assert.Throws<ArgumentException>(() => canvas.DrawText("a", Vector2.Zero, default, Rgba8.Black));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawText(null!, Vector2.Zero, font, Rgba8.Black));
        Assert.Throws<ArgumentException>(() => canvas.DrawText("a", new Vector2(float.NaN, 0), font, Rgba8.Black));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawGlyphRun(null!, Vector2.Zero, Rgba8.Black));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Font(Noto.Value, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlyphCache.ByteLimit = -1);
        canvas.DrawText(string.Empty, Vector2.Zero, font, Rgba8.Black);
        canvas.DrawText("\n\r\n", Vector2.Zero, font, Rgba8.Black);
        Assert.Empty(canvas.Calls);
    }

    [Fact]
    public void ADefaultBackendDrawsTextAsOnePath()
    {
        using PathCounter canvas = new();
        canvas.DrawText("ab c", new Vector2(1, 9), new Font(Noto.Value, 8), Rgba8.Black);
        Assert.Equal(1, canvas.Paths);
    }

    [Fact]
    public void PointsSizeAtAResolution()
    {
        Font font = Font.FromPoints(Noto.Value, 12, 96);
        Assert.Equal(16, font.Size);
        Assert.Throws<ArgumentOutOfRangeException>(() => Font.FromPoints(Noto.Value, 12, 0));
    }

    internal static string FontPath(string file)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return System.IO.Path.Combine(dir!.FullName, "tests", "fonts", file);
    }

    private static Raster<Rgba8> Draw(Typeface face, float size)
    {
        Raster<Rgba8> image = Raster.Create<Rgba8>(400, (int)(size * 4));
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        canvas.DrawText("The quick brown fox jumps\nover 13 lazy dogs: Ångström!", new Vector2(4.3f, size * 1.2f), new Font(face, size), new Rgba8(20, 40, 160, 255));
        return image;
    }

    private static void AssertSame(Raster<Rgba8> a, Raster<Rgba8> b, int tolerance)
    {
        int worst = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgba8 p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                worst = Math.Max(worst, Math.Max(Math.Abs(p.A - q.A), Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.B - q.B))));
            }
        }

        Assert.True(worst <= tolerance, $"The largest difference is {worst} levels.");
    }

    private sealed class PathCounter() : Drawing.Canvas(20, 20)
    {
        public int Paths { get; private set; }

        protected override void OnClipPath(Path path, ClipOp op, bool antialias)
        {
        }

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint) => Paths++;

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, Operations.SamplingOptions sampling, Paint? paint)
        {
        }
    }
}
