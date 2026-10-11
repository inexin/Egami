using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>Strokes against the SVG stroke definitions: areas of straight strokes, caps, joins, miter limits and hairlines.</summary>
public class StrokeTests
{
    [Theory]
    [InlineData(LineCap.Butt, 0)]
    [InlineData(LineCap.Square, 1)]
    [InlineData(LineCap.Round, 2)]
    public void StraightStrokesCoverWidthTimesLengthPlusTheirCaps(LineCap cap, int kind)
    {
        float width = 3.4f, length = 41.3f;
        foreach (float degrees in new[] { 0f, 17f, 45f, 90f })
        {
            Vector2 a = new(30, 30), d = new(MathF.Cos(degrees * MathF.PI / 180), MathF.Sin(degrees * MathF.PI / 180));
            double area = Coverage(c => c.DrawLine(a - (d * length / 2), a + (d * length / 2), new Stroke(width) { Cap = cap }, Rgba8.White));
            double expected = (width * length) + kind switch
            {
                1 => width * width,
                2 => Math.PI * width * width / 4,
                _ => 0,
            };
            // Round caps are polylines within the 0.1-pixel tolerance, inside the true circle.
            double slack = 0.2 + (kind == 2 ? 0.1 * Math.PI * width : 0);
            Assert.True(Math.Abs(area - expected) < slack, $"{cap} at {degrees}°: {area:F3}, expected {expected:F3}");
        }
    }

    [Fact]
    public void ClosedRectanglesStrokeAsRings()
    {
        float w = 30, h = 20, pen = 4;

        // Miter corners: the outer rectangle less the inner one.
        double miter = Coverage(c => c.StrokeRect(new RectF(10, 10, w, h), new Stroke(pen), Rgba8.White));
        Assert.Equal(((w + pen) * (h + pen)) - ((w - pen) * (h - pen)), miter, 1);

        // Bevel corners cut half a pen-square triangle from each corner; round corners a square's corner less a quarter circle.
        double bevel = Coverage(c => c.StrokeRect(new RectF(10, 10, w, h), new Stroke(pen) { Join = LineJoin.Bevel }, Rgba8.White));
        Assert.Equal(miter - (4 * (pen / 2) * (pen / 2) / 2), bevel, 1);
        double round = Coverage(c => c.StrokeRect(new RectF(10, 10, w, h), new Stroke(pen) { Join = LineJoin.Round }, Rgba8.White));
        Assert.Equal(miter - (4 * ((pen / 2) * (pen / 2) * (1 - (Math.PI / 4)))), round, 0.1 * Math.PI * pen);
    }

    [Fact]
    public void SharpCornersBecomeBevelsPastTheMiterLimit()
    {
        // A corner of 20 degrees: the miter is 1/sin(10°) ≈ 5.76 widths long, beyond the default limit of 4.
        Vector2[] corner = [new(5, 40), new(50, 30), new(5, 20)];
        double limited = Coverage(c => c.DrawPoints(PointMode.Polygon, corner, new Stroke(4), Rgba8.White));
        double bevel = Coverage(c => c.DrawPoints(PointMode.Polygon, corner, new Stroke(4) { Join = LineJoin.Bevel }, Rgba8.White));
        double mitered = Coverage(c => c.DrawPoints(PointMode.Polygon, corner, new Stroke(4) { MiterLimit = 6 }, Rgba8.White));
        Assert.Equal(bevel, limited, 3);
        Assert.True(mitered > bevel + 10, $"{mitered} {bevel}");
    }

    [Fact]
    public void SelfOverlapsNeverBlendTwice()
    {
        using Raster<Rgba8> cross = Raster.Create(40, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = cross.CreateCanvas())
        {
            Path lines = new PathBuilder().MoveTo(5, 20).LineTo(35, 20).MoveTo(20, 5).LineTo(20, 35).ToPath();
            canvas.StrokePath(lines, new Stroke(6), new Rgba8(0, 0, 0, 128));
        }

        // Where the two lines cross, the pixel holds one layer, as on either arm.
        Assert.Equal(cross.GetPixel(10, 20), cross.GetPixel(20, 20));
        Assert.InRange(cross.GetPixel(20, 20).R, 126, 129);

        // A self-intersecting star outline: wherever its sides cross, still one layer.
        using Raster<Rgba8> star = Raster.Create(40, 40, Rgba8.White);
        Vector2[] pentagram = new Vector2[5];
        for (int i = 0; i < 5; i++)
        {
            double angle = (4 * Math.PI * i / 5) - (Math.PI / 2);
            pentagram[i] = new Vector2(20 + (float)(17 * Math.Cos(angle)), 20 + (float)(17 * Math.Sin(angle)));
        }

        using (RasterCanvas<Rgba8> canvas = star.CreateCanvas())
        {
            canvas.StrokePath(Path.Polygon(pentagram), new Stroke(3) { Join = LineJoin.Round }, new Rgba8(0, 0, 0, 128));
        }

        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Assert.True(star.GetPixel(x, y).R >= 126, $"({x}, {y}) {star.GetPixel(x, y)}");
            }
        }
    }

    [Fact]
    public void ZeroLengthContoursDrawDotsWithRoundAndSquareCaps()
    {
        Path dot = new PathBuilder().MoveTo(20, 20).LineTo(20, 20).ToPath();
        Assert.Equal(Math.PI * 4 * 4, Coverage(c => c.StrokePath(dot, new Stroke(8) { Cap = LineCap.Round }, Rgba8.White)), 0.1 * Math.PI * 8);
        Assert.Equal(64, Coverage(c => c.StrokePath(dot, new Stroke(8) { Cap = LineCap.Square }, Rgba8.White)), 1);
        Assert.Equal(0, Coverage(c => c.StrokePath(dot, new Stroke(8), Rgba8.White)));
        Assert.Equal(0, Coverage(c => c.StrokePath(new PathBuilder().MoveTo(20, 20).ToPath(), new Stroke(8) { Cap = LineCap.Round }, Rgba8.White)));
        Assert.Equal(2 * Math.PI * 9, Coverage(c => c.DrawPoints(PointMode.Points, [new(10, 10), new(30, 30)], new Stroke(6) { Cap = LineCap.Round }, Rgba8.White)), 0.2 * Math.PI * 6);
    }

    [Fact]
    public void HairlinesAreOneDevicePixelWhateverTheTransform()
    {
        double plain = Coverage(c => c.DrawLine(new Vector2(5, 20.5f), new Vector2(45, 20.5f), Stroke.Hairline, Rgba8.White));
        double scaled = Coverage(c =>
        {
            c.Scale(4);
            c.DrawLine(new Vector2(1.25f, 5.125f), new Vector2(11.25f, 5.125f), Stroke.Hairline, Rgba8.White);
        });
        Assert.Equal(40, plain, 1);
        Assert.Equal(40, scaled, 1);

        // A stroke thinner than a pixel draws as a hairline at a fraction of its coverage.
        double thin = Coverage(c => c.DrawLine(new Vector2(5, 20.5f), new Vector2(45, 20.5f), new Stroke(0.5f), Rgba8.White));
        Assert.Equal(20, thin, 0);
    }

    [Fact]
    public void NonUniformScaleGivesAnEllipticalPen()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(60, 60);
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.Scale(1, 3);
            canvas.StrokeRect(new RectF(10, 5, 40, 10), new Stroke(2), Rgba8.White);
        }

        // Vertical sides stay 2 pixels wide; horizontal sides become 6.
        Assert.Equal(255, raster.GetPixel(9, 30).L);
        Assert.Equal(0, raster.GetPixel(12, 30).L);
        Assert.Equal(255, raster.GetPixel(30, 13).L);
        Assert.Equal(255, raster.GetPixel(30, 17).L);
        Assert.Equal(0, raster.GetPixel(30, 19).L);
    }

    [Fact]
    public void CurvedStrokesStayWithinTheTolerance()
    {
        // A circle stroked at width 20: the ring between radii 10 and 30.
        double ring = Coverage(c => c.StrokeCircle(new Vector2(40, 40), 20, new Stroke(20), Rgba8.White), 80);
        Assert.Equal(Math.PI * ((30 * 30) - (10 * 10)), ring, 0.1 * 2 * Math.PI * (30 + 10));
    }

    internal static double Coverage(Action<Drawing.Canvas> draw, int size = 64)
    {
        using Raster<GrayF32> raster = Raster.Create<GrayF32>(size, size);
        using (RasterCanvas<GrayF32> canvas = raster.CreateCanvas())
        {
            draw(canvas);
        }

        double sum = 0;
        for (int y = 0; y < size; y++)
        {
            foreach (GrayF32 p in raster.Row(y))
            {
                sum += p.L;
            }
        }

        return sum;
    }
}
