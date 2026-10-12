using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tests.Geometry;

public class OutlineTests
{
    private static readonly Path Wave = new PathBuilder().MoveTo(8, 30).CubicTo(20, 0, 40, 60, 56, 20).LineTo(40, 50).QuadTo(20, 60, 10, 40).ToPath();

    [Theory]
    [InlineData(LineJoin.Miter, LineCap.Butt, 1f)]
    [InlineData(LineJoin.Round, LineCap.Round, 1f)]
    [InlineData(LineJoin.Bevel, LineCap.Square, 2f)]
    [InlineData(LineJoin.Miter, LineCap.Round, 3.5f)]
    public void FilledOutlinesDrawTheStrokePixels(LineJoin join, LineCap cap, float scale)
    {
        Stroke stroke = new(5) { Join = join, Cap = cap };
        Path[] paths = [Wave, Path.Star(new Vector2(32, 32), 25, 10, 5), Path.Circle(new Vector2(30, 30), 18)];
        foreach (Path path in paths)
        {
            byte[] stroked = Draw(c => c.StrokePath(path, stroke, Rgba8.Black), scale);
            byte[] filled = Draw(c => c.FillPath(stroke.ToPath(path, scale), Rgba8.Black), scale);
            Assert.Equal(stroked, filled);
        }
    }

    [Fact]
    public void SimplifiedOutlinesHaveNoOverlaps()
    {
        Stroke stroke = new(6) { Join = LineJoin.Round };
        Path raw = stroke.ToPath(Wave), clean = stroke.ToPath(Wave, simplify: true);

        // Both cover the same area: the simplified outline's is its signed area; the raw one's, measured by filling it eight
        // times larger (an accumulating rasterizer overestimates pixels where overlapping edges cross, less the finer it draws).
        double rawCoverage = Canvas.StrokeTests.Coverage(c => { c.Scale(8); c.FillPath(raw, Rgba8.White); }, 512) / 64;
        Assert.Equal(rawCoverage, PathEffectTests.Area(clean), 0.6);
        Assert.True(Math.Abs(PathEffectTests.Area(raw)) > Math.Abs(PathEffectTests.Area(clean)) + 1);
        Assert.Equal(FillRule.NonZero, raw.FillRule);
    }

    [Fact]
    public void EffectsDashesAndHairlines()
    {
        Path line = Path.Line(Vector2.Zero, new Vector2(100, 0));
        Path dashed = new Stroke(2) { Dash = new DashPattern([10, 10]) }.ToPath(line);
        Assert.Equal(5, dashed.Contours.Count);
        Assert.Equal(100, Math.Abs(PathEffectTests.Area(dashed)), 1e-3);
        Path trimmed = new Stroke(2) { Effect = PathEffect.Trim(0, 0.25f) }.ToPath(line);
        Assert.Equal(50, Math.Abs(PathEffectTests.Area(trimmed)), 1e-3);
        Assert.True(Stroke.Hairline.ToPath(line).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Stroke(1).ToPath(line, 0));
    }

    [Theory]
    [InlineData(LineJoin.Round)]
    [InlineData(LineJoin.Miter)]
    [InlineData(LineJoin.Bevel)]
    public void OffsetGrowsConvexShapesByPerimeterTimesDistance(LineJoin join)
    {
        Path square = Path.Rect(new RectF(0, 0, 10, 10));
        double corners = join switch
        {
            LineJoin.Round => Math.PI * 4,
            LineJoin.Miter => 16,
            _ => 8,
        };
        Assert.Equal(100 + (40 * 2) + corners, PathEffectTests.Area(square.Offset(2, join)), join == LineJoin.Round ? 0.2 : 1e-3);
        Assert.Equal(36, PathEffectTests.Area(square.Offset(-2, join)), 1e-3);
        Assert.True(square.Offset(-6, join).IsEmpty);
        Assert.Equal(100, PathEffectTests.Area(square.Offset(0)), 1e-9);

        Path circle = Path.Circle(new Vector2(50, 50), 20);
        double grown = PathEffectTests.Area(circle.Offset(3, LineJoin.Round));
        Assert.Equal(PathEffectTests.Area(Path.Circle(new Vector2(50, 50), 23)), grown, 3.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => circle.Offset(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => circle.Offset(1, LineJoin.Miter, 0.5f));
    }

    private static byte[] Draw(Action<Egami.Drawing.Canvas> draw, float scale)
    {
        int size = (int)(64 * scale);
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(size, size);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.Scale(scale);
            draw(canvas);
        }

        return Canvas.RasterCanvasTests.Bytes(raster);
    }
}
