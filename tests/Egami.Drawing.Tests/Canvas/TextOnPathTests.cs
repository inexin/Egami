using System.Numerics;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// Transformed glyph runs and text on a path (2.7.2 task 11): placement equals placing each glyph by hand with PathMeasure, clusters
/// survive dropped glyphs, and a run stays a run for backends that write text.
/// </summary>
public class TextOnPathTests
{
    private static Typeface Noto => TextTests.Noto.Value;

    [Fact]
    public void TextOnAPathEqualsPlacingEachGlyphByHand()
    {
        var font = new Font(Noto, 20);
        Path arc = new PathBuilder().MoveTo(20, 150).CubicTo(80, 20, 220, 20, 280, 150).ToPath();
        const string text = "Around the curve";
        var options = new TextOnPathOptions { StartOffset = 12, BaselineShift = 3 };
        GlyphRun run = font.ShapeOnPath(text, arc, options);

        GlyphRun line = font.Shape(text);
        ContourMeasure contour = new PathMeasure(arc).Contours[0];
        Assert.Equal(line.Count, run.Count);
        for (int i = 0; i < run.Count; i++)
        {
            float advance = font.GetAdvance(line.Glyphs[i]);
            float mid = 12 + line.Positions[i].X + (advance / 2);
            Vector2 p = contour.GetPosition(mid), t = contour.GetTangent(mid);
            Vector2 n = new(t.Y, -t.X);
            Vector2 o = p - (t * advance / 2) + (n * 3);
            GlyphTransform g = run.Transforms[i];
            Assert.Equal(t.X, g.ScaleCos, 4);
            Assert.Equal(t.Y, g.ScaleSin, 4);
            Assert.Equal(o.X, g.Tx, 3);
            Assert.Equal(o.Y, g.Ty, 3);
            Assert.Equal(line.Glyphs[i], run.Glyphs[i]);
            Assert.Equal(new Vector2(g.Tx, g.Ty), run.Positions[i]);
        }

        Assert.Equal(line.Clusters.ToArray(), run.Clusters.ToArray());
    }

    [Fact]
    public void GlyphsPastTheEndAreDroppedWithTheirClusters()
    {
        var font = new Font(Noto, 20);
        Path shortLine = new PathBuilder().MoveTo(0, 50).LineTo(60, 50).ToPath();
        GlyphRun run = font.ShapeOnPath("abcdefghij", shortLine);
        Assert.InRange(run.Count, 2, 6);
        Assert.Equal(Enumerable.Range(0, run.Count), run.Clusters.ToArray());
        Assert.Equal("abcdefghij", run.Text);

        // Anchored at the end of the line, the text ends there and starts before it, so its first glyphs drop instead.
        GlyphRun end = font.ShapeOnPath("abcdefghij", shortLine, new TextOnPathOptions { StartFraction = 1, Anchor = TextAnchor.End });
        Assert.Equal(9, end.Clusters[^1]);
        Assert.True(end.Clusters[0] > 0);

        // A closed contour can wrap instead.
        Path circle = Path.Circle(new Vector2(50, 50), 8);
        Assert.True(font.ShapeOnPath("abcdefghij", circle).Count < 10);
        Assert.Equal(10, font.ShapeOnPath("abcdefghij", circle, new TextOnPathOptions { WrapClosed = true }).Count);
    }

    [Fact]
    public void AnchorsSidesAndShiftsPlaceTheText()
    {
        var font = new Font(Noto, 16);
        Path line = new PathBuilder().MoveTo(0, 100).LineTo(400, 100).ToPath();
        float width = font.MeasureText("Middle").Advance;
        GlyphRun middle = font.ShapeOnPath("Middle", line, new TextOnPathOptions { StartFraction = 0.5f, Anchor = TextAnchor.Middle });
        Assert.Equal(200 - (width / 2), middle.Transforms[0].Tx, 3);
        Assert.Equal(100, middle.Transforms[0].Ty, 3);

        // Upright text on a left-to-right line equals the plain run, raised by the baseline shift.
        GlyphRun raised = font.ShapeOnPath("Middle", line, new TextOnPathOptions { BaselineShift = 5 });
        Assert.All(raised.Transforms.ToArray(), t => Assert.True(t.IsTranslation && t.Ty == 95));

        // The right side walks the line backwards: the text runs right to left, upside down, below it.
        GlyphRun right = font.ShapeOnPath("Middle", line, new TextOnPathOptions { Side = TextPathSide.Right });
        Assert.Equal(-1, right.Transforms[0].ScaleCos, 4);
        Assert.Equal(400, right.Transforms[0].Tx, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextOnPathOptions { StartOffset = float.NaN });
    }

    [Fact]
    public void TransformedRunsDrawLikeTheirPaths()
    {
        var font = new Font(Noto, 18);
        GlyphTransform[] transforms =
        [
            GlyphTransform.FromRotationScale(1, 0, new Vector2(10, 40)),
            GlyphTransform.FromRotationScale(1, 30, new Vector2(40, 40)),
            GlyphTransform.FromRotationScale(1.5f, -45, new Vector2(80, 50)),
        ];
        ushort[] glyphs = [.. "AgR".Select(c => Noto.GetGlyph(c))];
        GlyphRun run = GlyphRun.CreateTransformed(font, glyphs, transforms, "AgR", [0, 1, 2]);
        using Raster<Rgba8> drawn = Raster.Create(140, 90, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = drawn.CreateCanvas(new CanvasOptions { Tolerance = RasterCanvas<Rgba8>.GlyphTolerance }))
        {
            canvas.DrawGlyphRun(run, new Vector2(5, 5), Rgba8.Black);
        }

        using Raster<Rgba8> filled = Raster.Create(140, 90, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = filled.CreateCanvas(new CanvasOptions { Tolerance = RasterCanvas<Rgba8>.GlyphTolerance }))
        {
            canvas.FillPath(run.ToPath(new Vector2(5, 5)), Rgba8.Black);
        }

        int worst = 0;
        for (int y = 0; y < 90; y++)
        {
            for (int x = 0; x < 140; x++)
            {
                worst = Math.Max(worst, Math.Abs(drawn.GetPixel(x, y).R - filled.GetPixel(x, y).R));
            }
        }

        Assert.True(worst <= 1, $"The largest difference is {worst} levels.");

        // The ink bounds cover every turned glyph.
        RectF path = run.ToPath(Vector2.Zero).Bounds;
        TextBounds bounds = run.Bounds;
        Assert.True(bounds.Left <= path.Left + 0.01f && bounds.Right >= path.Right - 0.01f && bounds.Top <= path.Top + 0.01f && bounds.Bottom >= path.Bottom - 0.01f);
        Assert.Throws<ArgumentException>(() => GlyphRun.CreateTransformed(font, glyphs, transforms.AsSpan(0, 2)));
    }

    [Fact]
    public void DrawTextOnPathRecordsOneGlyphRun()
    {
        var recorder = new PictureRecorder(new RectF(0, 0, 300, 200));
        Path arc = new PathBuilder().MoveTo(20, 150).QuadTo(150, 0, 280, 150).ToPath();
        recorder.DrawTextOnPath("Recorded", arc, new Font(Noto, 18), Rgba8.Black);
        Picture picture = recorder.Finish();
        Assert.Equal(1, picture.CallCount);
        using Raster<Rgba8> image = picture.ToRaster<Rgba8>();
        Assert.Contains(Enumerable.Range(0, 300).SelectMany(x => Enumerable.Range(0, 200).Select(y => image.GetPixel(x, y))), p => p.A > 0);
    }
}
