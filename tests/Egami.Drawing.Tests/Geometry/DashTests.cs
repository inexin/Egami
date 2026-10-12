using System.Numerics;

using Egami.Drawing.Tests.Canvas;

namespace Egami.Drawing.Tests.Geometry;

public class DashTests
{
    [Fact]
    public void DashesFollowCurvesWithinTheMeasuredLength()
    {
        Path wave = new PathBuilder().MoveTo(0, 0).CubicTo(100, -200, 200, 200, 300, 0).QuadTo(400, -100, 500, 0).ToPath();
        Path dashed = new DashPattern([30, 10]).Apply(wave);
        PathMeasure measure = new(dashed);
        float total = new PathMeasure(wave).TotalLength;
        int full = (int)(total / 40);
        Assert.True(measure.Contours.Count >= full);
        for (int i = 0; i < full; i++)
        {
            Assert.Equal(30, measure.Contours[i].Length, 1e-3);
            Assert.False(measure.Contours[i].IsClosed);
        }

        // Every dash starts where the pattern says along the original.
        ContourMeasure original = new PathMeasure(wave).Contours[0];
        for (int i = 0; i < full; i++)
        {
            Vector2 expected = original.GetPosition(40 * i);
            Assert.True(Vector2.Distance(expected, dashed.Contours[i].Points[0]) < 1e-3f);
        }
    }

    [Fact]
    public void PhaseOddCountsAndSolidPatterns()
    {
        Path line = Path.Line(Vector2.Zero, new Vector2(100, 0));
        Assert.Equal(StartsOf(new DashPattern([10, 10, 10, 10]).Apply(line)), StartsOf(new DashPattern([10]).Apply(line)));
        Assert.Equal([0f, 20, 40, 60, 80], StartsOf(new DashPattern([10]).Apply(line)));
        Assert.Equal([0f, 15, 35, 55, 75, 95], StartsOf(new DashPattern([10], phase: 5).Apply(line)));
        Assert.Equal([5f, 25, 45, 65, 85], StartsOf(new DashPattern([10], phase: -5).Apply(line)));
        Assert.Equal(5, new PathMeasure(new DashPattern([10], phase: 5).Apply(line)).Contours[0].Length, 1e-4);
        Assert.Same(line, new DashPattern([0, 0]).Apply(line));

        // Each contour starts the pattern afresh.
        Path two = new PathBuilder().MoveTo(0, 0).LineTo(15, 0).MoveTo(0, 10).LineTo(15, 10).ToPath();
        Assert.Equal(4, new DashPattern([10, 2]).Apply(two).Contours.Count);
    }

    [Fact]
    public void ZeroLengthDashesAreDots()
    {
        Path dots = new DashPattern([0, 10]).Apply(Path.Line(Vector2.Zero, new Vector2(30, 0)));
        Assert.Equal(4, dots.Contours.Count);
        double round = StrokeTests.Coverage(c => c.StrokePath(Path.Line(new(5, 5), new(35, 5)), new Stroke(4) { Cap = LineCap.Round, Dash = new DashPattern([0, 10]) }, White));
        Assert.Equal(4 * Math.PI * 4, round, 5.0);
        double butt = StrokeTests.Coverage(c => c.StrokePath(Path.Line(new(5, 5), new(35, 5)), new Stroke(4) { Dash = new DashPattern([0, 10]) }, White));
        Assert.Equal(0, butt);
    }

    [Fact]
    public void TooManyDashesDrawSolid()
    {
        Path line = Path.Line(Vector2.Zero, new Vector2(1e6f, 0));
        Assert.Same(line, new DashPattern([0.1f, 0.1f]).Apply(line));
    }

    [Fact]
    public void PatternsCompareByValue()
    {
        Assert.Equal(new DashPattern([3, 1], 2), new DashPattern([3, 1], 2));
        Assert.NotEqual(new DashPattern([3, 1], 2), new DashPattern([3, 2], 2));
        Assert.Equal(new DashPattern([3, 1]).GetHashCode(), new DashPattern([3, 1]).GetHashCode());
        Assert.Equal(new Stroke(2) { Dash = new DashPattern([1, 1]) }, new Stroke(2) { Dash = new DashPattern([1, 1]) });
        Assert.Equal(new DashPattern([6, 2], 1), new DashPattern([3, 1], 0.5f).Scaled(2));
        Assert.Throws<ArgumentException>(() => new DashPattern([]));
        Assert.Throws<ArgumentException>(() => new DashPattern([1, -1]));
        Assert.Throws<ArgumentException>(() => new DashPattern([float.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DashPattern([1], float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DashPattern([1]).Scaled(-1));
    }

    [Fact]
    public void TraceBackendsGetThePatternAsData()
    {
        using TraceCanvas canvas = new(100, 100);
        canvas.StrokePath(Path.Circle(new(50, 50), 20), new Stroke(2) { Dash = new DashPattern([8, 4], 1) }, new SolidPaint(Pixels.Rgba8.Black));
        Assert.Contains(canvas.Calls, c => c.Contains("dash [8 4] 1", StringComparison.Ordinal) && c.StartsWith("DrawPath 6 ", StringComparison.Ordinal));
    }

    [Fact]
    public void RasterDashesLeaveGaps()
    {
        using Raster<Pixels.Rgba8> image = Raster.Create<Pixels.Rgba8>(60, 10);
        using (RasterCanvas<Pixels.Rgba8> canvas = image.CreateCanvas())
        {
            canvas.StrokePath(Path.Line(new(0, 5), new(60, 5)), new Stroke(2) { Dash = new DashPattern([10, 10]) }, new SolidPaint(Pixels.Rgba8.Black));
        }

        Assert.Equal(255, image.GetPixel(5, 5).A);
        Assert.Equal(0, image.GetPixel(15, 5).A);
        Assert.Equal(255, image.GetPixel(25, 5).A);
        Assert.Equal(0, image.GetPixel(35, 5).A);
    }

    private static readonly Paint White = new SolidPaint(Pixels.Rgba8.White);

    private static float[] StartsOf(Path dashed)
    {
        List<float> starts = [];
        foreach (Path contour in dashed.Contours)
        {
            starts.Add(contour.Points[0].X);
        }

        return [.. starts];
    }
}
