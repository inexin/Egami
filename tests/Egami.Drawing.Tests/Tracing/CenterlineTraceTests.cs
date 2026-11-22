using System.Numerics;

using Egami.Drawing.Tests.Geometry;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>Centerline mode end to end, against strokes drawn from known paths, and its stages.</summary>
public class CenterlineTraceTests
{
    internal static readonly TraceOptions Centerline = new() { Mode = TraceMode.Centerline, IgnoreWhite = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(4f)]
    public void StrokesFollowTheirCentreLines(float scale)
    {
        (Raster<Rgba8> raster, List<(Path Path, float Width)> strokes) = Drawing(scale);
        using (raster)
        {
            TraceResult result = raster.Trace(Centerline);
            (double mean, double max, double worstWidth, int count) = Measure(result, strokes, [new Vector2(135, 110) * scale], 1.5f);

            // Six strokes: the crossing lines run straight through each other, and the circle is one closed stroke.
            Assert.Equal(6, count);
            Assert.All(result.Shapes, s => Assert.Equal(TracedShapeKind.Stroke, s.Kind));
            Assert.InRange(mean, 0, 0.2);
            Assert.InRange(max, 0, 0.7);
            Assert.InRange(worstWidth, 0.95, 1.05);
        }
    }

    [Fact]
    public void TheEndsReachWhereTheInkDoes()
    {
        using Raster<Rgba8> raster = Raster.Create(120, 60, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.StrokePath(new PathBuilder().MoveTo(20, 30).LineTo(100, 30).ToPath(), new Stroke(8) { Cap = LineCap.Round }, Rgba8.Black);
        }

        TracedShape stroke = Assert.Single(raster.Trace(Centerline).Shapes);
        Assert.Equal(8, stroke.StrokeWidth, 0.3);
        Vector2 start = stroke.Path.Points[0], end = stroke.Path.Points[^1];
        Assert.Equal(20, Math.Min(start.X, end.X), 0.5);
        Assert.Equal(100, Math.Max(start.X, end.X), 0.5);
        Assert.Equal(30, start.Y, 0.2);
        Assert.Equal(30, end.Y, 0.2);
    }

    [Fact]
    public void AStemEndsOnTheBarItMeets()
    {
        using Raster<Rgba8> raster = Raster.Create(100, 100, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            Stroke pen = new(4) { Cap = LineCap.Round };
            canvas.StrokePath(new PathBuilder().MoveTo(10, 20).LineTo(90, 20).ToPath(), pen, Rgba8.Black);
            canvas.StrokePath(new PathBuilder().MoveTo(50, 20).LineTo(50, 90).ToPath(), pen, Rgba8.Black);
        }

        TracedShape[] strokes = [.. raster.Trace(Centerline).Shapes];
        Assert.Equal(2, strokes.Length);
        TracedShape bar = strokes.Single(s => s.Path.Bounds.Width > 50), stem = strokes.Single(s => s.Path.Bounds.Height > 50);
        Assert.InRange(bar.Path.Bounds.Height, 0, 0.5);
        Assert.Equal(20, stem.Path.Bounds.Top, 1.0);
        Assert.Equal(90, stem.Path.Bounds.Bottom, 0.5);
        Assert.Equal(50, stem.Path.Bounds.Center.X, 0.3);
    }

    [Fact]
    public void ThickCornersLeaveNoSpurs()
    {
        // An L of a thick pen: thinning leaves short branches into the outer corner, which the pruning removes.
        using Raster<Rgba8> raster = Raster.Create(100, 100, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.StrokePath(new PathBuilder().MoveTo(20, 15).LineTo(20, 80).LineTo(85, 80).ToPath(), new Stroke(12) { Join = LineJoin.Miter }, Rgba8.Black);
        }

        TracedShape stroke = Assert.Single(raster.Trace(Centerline).Shapes);
        Assert.Equal(12, stroke.StrokeWidth, 1.0);
    }

    [Fact]
    public void ThickCrossingsStayTwoStrokes()
    {
        // Thinning splits a thick crossing into two forks a short way apart; they merge, and each line runs straight through.
        using Raster<Rgba8> raster = Raster.Create(300, 300, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            Stroke pen = new(30) { Cap = LineCap.Round };
            canvas.StrokePath(new PathBuilder().MoveTo(40, 40).LineTo(260, 230).ToPath(), pen, Rgba8.Black);
            canvas.StrokePath(new PathBuilder().MoveTo(40, 230).LineTo(260, 40).ToPath(), pen, Rgba8.Black);
        }

        TracedShape[] strokes = [.. raster.Trace(Centerline).Shapes];
        Assert.Equal(2, strokes.Length);
        Assert.All(strokes, s => Assert.Equal(30, s.StrokeWidth, 1.5));
        Assert.All(strokes, s => Assert.Equal(220, s.Path.Bounds.Width, 2.0));
    }

    [Fact]
    public void DotsBecomeDiscs()
    {
        using Raster<Rgba8> raster = Raster.Create(60, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(15, 20), 2.5f, Rgba8.Black);
            canvas.FillRect(new RectF(40, 19, 2, 2), Rgba8.Black);
        }

        TracedShape[] shapes = [.. raster.Trace(Centerline).Shapes];
        Assert.Equal(2, shapes.Length);
        Assert.All(shapes, s => Assert.Equal(TracedShapeKind.Fill, s.Kind));
        TracedShape round = shapes.Single(s => s.Path.Bounds.Center.X < 30);
        Assert.Equal(15, round.Path.Bounds.Center.X, 0.2);
        Assert.Equal(Math.PI * 2.5 * 2.5, PathEffectTests.Area(round.Path), 1.5);
        Assert.Equal(41, shapes.Single(s => s.Path.Bounds.Center.X > 30).Path.Bounds.Center.X, 0.2);
    }

    [Fact]
    public void AFixedWidthAndTheWhiteBackground()
    {
        (Raster<Rgba8> raster, _) = Drawing(1);
        using (raster)
        {
            TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Centerline, StrokeWidth = 1 });
            Assert.Equal(Rgba8.White, result.Shapes[0].Color);
            Assert.Equal(TracedShapeKind.Fill, result.Shapes[0].Kind);
            Assert.All(result.Shapes.Skip(1), s => Assert.Equal(1, s.StrokeWidth));
            Assert.Equal([Rgba8.White, Rgba8.Black], result.Palette);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new TraceOptions { StrokeWidth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraceOptions { StrokeWidth = float.NaN });
    }

    [Fact]
    public void TheTraceDrawsBackLikeTheDrawing()
    {
        (Raster<Rgba8> raster, _) = Drawing(2);
        using (raster)
        {
            TraceResult result = raster.Trace(Centerline);
            using Raster<Rgba8> back = Raster.Create(raster.Width, raster.Height, Rgba8.White);
            using (RasterCanvas<Rgba8> canvas = back.CreateCanvas())
            {
                result.Draw(canvas);
            }

            double ink = 0, difference = 0;
            for (int y = 0; y < raster.Height; y++)
            {
                for (int x = 0; x < raster.Width; x++)
                {
                    int a = raster.GetPixel(x, y).G, b = back.GetPixel(x, y).G;
                    ink += (255 - a) / 255.0;
                    difference += Math.Abs(a - b) / 255.0;
                }
            }

            Assert.InRange(difference / ink, 0, 0.05);
        }
    }

    [Fact]
    public void InkThatFillsTheImageStaysOnTheImage()
    {
        // Found by the trace fuzz target: with no paper in the image its distance to paper overflowed, and points went to
        // −2.1e9. The edge of the image counts as paper.
        using Raster<Rgba8> raster = Raster.Create(32, 24, Rgba8.Black);
        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Centerline, IgnoreWhite = true });
        Assert.NotEmpty(result.Shapes);
        foreach (TracedShape shape in result.Shapes)
        {
            RectF bounds = shape.Path.TightBounds;
            Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= 33 && bounds.Bottom <= 25, $"{bounds}");
        }
    }

    [Fact]
    public void OtherPixelTypesTinyAndBlankImages()
    {
        (Raster<Rgba8> raster, _) = Drawing(1);
        using (raster)
        {
            using Raster<Gray8> gray = raster.As<Gray8>();
            using Raster<RgbaF32> wide = raster.As<RgbaF32>();
            Assert.Equal(6, gray.Trace(Centerline).Shapes.Count);
            Assert.Equal(6, wide.Trace(Centerline).Shapes.Count);
        }

        using Raster<Rgba8> one = Raster.Create(1, 1, Rgba8.Black);
        Assert.Equal(TracedShapeKind.Fill, Assert.Single(one.Trace(Centerline).Shapes).Kind);
        using Raster<Rgba8> blank = Raster.Create(10, 10, Rgba8.White);
        Assert.Empty(blank.Trace(Centerline).Shapes);
    }

    [Fact]
    public void TracingIsTheSameEveryTimeAndCancels()
    {
        (Raster<Rgba8> raster, _) = Drawing(1);
        using (raster)
        {
            string first = string.Join(";", raster.Trace(Centerline).Shapes.Select(s => s.Path.ToSvg()));
            string second = string.Join(";", raster.Trace(Centerline).Shapes.Select(s => s.Path.ToSvg()));
            Assert.Equal(first, second);

            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => raster.TraceAsync(Centerline, cancellationToken: cancelled.Token).GetAwaiter().GetResult());
        }
    }

    [Fact]
    public void TheDistanceTransformIsExact()
    {
        Random random = new(3);
        const int Width = 23, Height = 17;
        bool[] seeds = [.. Enumerable.Range(0, Width * Height).Select(_ => random.Next(12) == 0)];
        int[] nearest = new int[seeds.Length];
        double[] d = DistanceTransform.Squared(seeds, Width, Height, nearest, Ct);
        for (int i = 0; i < seeds.Length; i++)
        {
            double best = double.MaxValue;
            for (int s = 0; s < seeds.Length; s++)
            {
                if (seeds[s])
                {
                    double dx = (i % Width) - (s % Width), dy = (i / Width) - (s / Width);
                    best = Math.Min(best, (dx * dx) + (dy * dy));
                }
            }

            Assert.Equal(best, d[i]);
            int n = nearest[i];
            Assert.True(seeds[n]);
            Assert.Equal(best, Math.Pow((i % Width) - (n % Width), 2) + Math.Pow((i / Width) - (n / Width), 2));
        }

        double[] none = DistanceTransform.Squared(new bool[12], 4, 3, nearest, Ct);
        Assert.All(none, v => Assert.Equal(DistanceTransform.Far, v));
        Assert.All(nearest.Take(12), n => Assert.Equal(-1, n));
    }

    [Fact]
    public void ThinningLeavesALineOnePixelWide()
    {
        // A bar 7 pixels thick and a diagonal bar: their skeletons are lines whose inner pixels have exactly two neighbours.
        const int Width = 40, Height = 40;
        byte[] ink = new byte[Width * Height];
        for (int y = 5; y < 12; y++)
        {
            for (int x = 3; x < 37; x++)
            {
                ink[(y * Width) + x] = 1;
            }
        }

        for (int k = 0; k < 20; k++)
        {
            for (int t = -2; t <= 2; t++)
            {
                ink[((18 + k) * Width) + 8 + k + t] = 1;
            }
        }

        byte[] skeleton = Thinning.Skeleton(ink, Width, Height, Ct);
        int[] degrees = [.. Enumerable.Range(0, skeleton.Length).Where(i => skeleton[i] != 0)
            .Select(i => System.Numerics.BitOperations.PopCount((uint)Thinning.Neighbours(skeleton, Width, Height, i)))];
        Assert.Equal(4, degrees.Count(d => d == 1));
        Assert.All(degrees, d => Assert.InRange(d, 1, 2));
        Assert.All(Enumerable.Range(0, skeleton.Length).Where(i => skeleton[i] != 0 && i < 13 * Width), i => Assert.Equal(8, i / Width));
    }

    [Fact]
    public void WidthsCoverTheInk()
    {
        // A stroke 10 long and 3 wide with two round ends covers 30 + 2 half discs.
        Assert.Equal(3, CenterlineTracer.Width(30 + (Math.PI * 9 / 4), 10, 2), 1e-9);
        Assert.Equal(2.5, CenterlineTracer.Width(25, 10, 0), 1e-9);
        Assert.Equal(0.25, CenterlineTracer.Width(0, 10, 2));
    }

    /// <summary>A line drawing: its strokes, each a path and a pen width, drawn black on white with round ends, at a scale.</summary>
    internal static (Raster<Rgba8> Raster, List<(Path Path, float Width)> Strokes) Drawing(float scale)
    {
        List<(Path Path, float Width)> strokes =
        [
            (new PathBuilder().MoveTo(15, 15).LineTo(185, 32).ToPath(), 3),
            (new PathBuilder().MoveTo(20, 60).CubicTo(60, 30, 110, 95, 180, 55).ToPath(), 2),
            (new PathBuilder().AddCircle(new Vector2(55, 115), 24).ToPath(), 4),
            (new PathBuilder().MoveTo(100, 80).LineTo(170, 140).ToPath(), 2.5f),
            (new PathBuilder().MoveTo(100, 140).LineTo(170, 80).ToPath(), 2.5f),
            (new PathBuilder().MoveTo(15, 40).LineTo(90, 44).ToPath(), 1.5f),
        ];

        Matrix3x2 m = Matrix3x2.CreateScale(scale);
        strokes = [.. strokes.Select(s => (s.Path.Transform(m), s.Width * scale))];
        Raster<Rgba8> raster = Raster.Create((int)(200 * scale), (int)(160 * scale), Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        foreach ((Path path, float width) in strokes)
        {
            canvas.StrokePath(path, new Stroke(width) { Cap = LineCap.Round, Join = LineJoin.Round }, Rgba8.Black);
        }

        return (raster, strokes);
    }

    /// <summary>
    /// How far the traced strokes' points lie from the drawn centre lines, mean and largest, leaving out points within
    /// <paramref name="skip"/> pen widths of a crossing; and each traced stroke's width over the width of the stroke it follows.
    /// </summary>
    internal static (double Mean, double Max, double WorstWidth, int Strokes) Measure(TraceResult result, List<(Path Path, float Width)> source, Vector2[] crossings, float skip)
    {
        List<(List<Vector2> Points, float Width)> drawn = [.. source.Select(s => (Points(s.Path), s.Width))];
        double sum = 0, max = 0, worstWidth = 1;
        int count = 0, strokes = 0;
        foreach (TracedShape shape in result.Shapes.Where(s => s.Kind == TracedShapeKind.Stroke))
        {
            strokes++;
            List<Vector2> points = Points(shape.Path);
            double[] perStroke = new double[drawn.Count];
            foreach (Vector2 p in points)
            {
                double best = double.MaxValue;
                for (int k = 0; k < drawn.Count; k++)
                {
                    double d = Distance(drawn[k].Points, p);
                    perStroke[k] += d;
                    best = Math.Min(best, d);
                }

                if (crossings.Any(c => Vector2.Distance(c, p) < skip * shape.StrokeWidth))
                {
                    continue;
                }

                sum += best;
                max = Math.Max(max, best);
                count++;
            }

            int follows = Array.IndexOf(perStroke, perStroke.Min());
            double ratio = shape.StrokeWidth / drawn[follows].Width;
            worstWidth = Math.Abs(ratio - 1) > Math.Abs(worstWidth - 1) ? ratio : worstWidth;
        }

        return (sum / Math.Max(count, 1), max, worstWidth, strokes);
    }

    private static List<Vector2> Points(Path path)
    {
        List<Vector2> points = [path.Points[0], .. LogoTraceTests.Outline(path)];
        return points;
    }

    private static double Distance(List<Vector2> polyline, Vector2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < polyline.Count; i++)
        {
            Vector2 a = polyline[i], ab = polyline[i + 1] - a;
            float s = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            best = Math.Min(best, Vector2.Distance(p, a + (s * ab)));
        }

        return best;
    }
}
