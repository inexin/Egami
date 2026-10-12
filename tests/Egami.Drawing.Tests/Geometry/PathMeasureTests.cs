using System.Numerics;

namespace Egami.Drawing.Tests.Geometry;

public class PathMeasureTests
{
    [Fact]
    public void LinesMeasureExactly()
    {
        PathMeasure measure = new(Path.Polygon([new(0, 0), new(30, 0), new(30, 40)], close: false));
        ContourMeasure c = Assert.Single(measure.Contours);
        Assert.Equal(70, c.Length, 1e-4);
        Assert.False(c.IsClosed);
        Assert.Equal(new Vector2(30, 10), c.GetPosition(40));
        Assert.Equal(new Vector2(0, 1), c.GetTangent(40));
        Assert.Equal(new Vector2(0, 0), c.GetPosition(-5));
        Assert.Equal(new Vector2(30, 40), c.GetPosition(500));

        PathMeasure closed = new(Path.Polygon([new(0, 0), new(30, 0), new(30, 40)], close: false), forceClosed: true);
        Assert.Equal(120, closed.TotalLength, 1e-4);
        Assert.True(closed.Contours[0].IsClosed);
    }

    [Fact]
    public void CircleLengthMatchesTheFormula()
    {
        // The path's cubics are not a circle: compare with the length of the cubics themselves, by fine sampling.
        Path circle = Path.Circle(new Vector2(50, 50), 40);
        double sampled = SampledLength(circle);
        PathMeasure measure = new(circle);
        Assert.Equal(sampled, measure.TotalLength, 1e-4);
        Assert.Equal(2 * Math.PI * 40, measure.TotalLength, 0.05);
        Assert.True(measure.Contours[0].IsClosed);
    }

    [Fact]
    public void CurveLengthsAreExactToTheTolerance()
    {
        Random random = new(7);
        for (int i = 0; i < 200; i++)
        {
            PathBuilder builder = new PathBuilder().MoveTo(R());
            if (i % 2 == 0)
            {
                builder.CubicTo(R(), R(), R());
            }
            else
            {
                builder.QuadTo(R(), R());
            }

            Path path = builder.ToPath();
            Assert.Equal(SampledLength(path), new PathMeasure(path).TotalLength, 2e-3);
        }

        Vector2 R() => new((float)(random.NextDouble() * 1000), (float)(random.NextDouble() * 1000));
    }

    [Fact]
    public void PositionsInvertTheLength()
    {
        Path wave = new PathBuilder().MoveTo(0, 0).CubicTo(100, -200, 200, 200, 300, 0).QuadTo(400, -100, 500, 0).ToPath();
        ContourMeasure c = new PathMeasure(wave).Contours[0];
        Vector2 previous = c.GetPosition(0);
        double walked = 0;
        int steps = 20_000;
        for (int i = 1; i <= steps; i++)
        {
            Vector2 next = c.GetPosition(c.Length * i / steps);
            walked += Vector2.Distance(previous, next);
            previous = next;
        }

        // Equal steps of distance give points equally far apart along the curve.
        Assert.Equal(c.Length, walked, 0.01);
        for (int i = 1; i < 10; i++)
        {
            float d = c.Length * i / 10;
            Assert.Equal(d, new PathMeasure(c.GetSegment(0, d)).TotalLength, 1e-3);
        }
    }

    [Fact]
    public void ZeroLengthContoursAreLeftOut()
    {
        Path path = new PathBuilder().MoveTo(5, 5).MoveTo(1, 1).LineTo(1, 1).Close().MoveTo(0, 0).LineTo(10, 0).ToPath();
        PathMeasure measure = new(path);
        Assert.Equal(10, Assert.Single(measure.Contours).Length);
        Assert.Empty(new PathMeasure(Path.Empty).Contours);
        Assert.Equal(0, new PathMeasure(Path.Empty).TotalLength);
    }

    [Fact]
    public void TangentsAndMatricesFollowTheCurve()
    {
        ContourMeasure c = new PathMeasure(Path.Circle(Vector2.Zero, 10)).Contours[0];

        // The circle starts at its right end and runs clockwise on screen, y down: down first.
        Vector2 tangent = c.GetTangent(0);
        Assert.Equal(0, tangent.X, 1e-4);
        Assert.Equal(1, tangent.Y, 1e-4);
        Matrix3x2 m = c.GetMatrix(c.Length / 4);
        Vector2 bottom = Vector2.Transform(Vector2.Zero, m);
        Assert.Equal(0, bottom.X, 0.01);
        Assert.Equal(10, bottom.Y, 0.01);
        Vector2 ahead = Vector2.Transform(Vector2.UnitX, m) - bottom;
        Assert.Equal(-1, ahead.X, 1e-3);

        // A cubic whose first control point sits on its start leaves towards the second.
        ContourMeasure cusp = new PathMeasure(new PathBuilder().MoveTo(0, 0).CubicTo(0, 0, 10, 10, 20, 0).ToPath()).Contours[0];
        Vector2 start = cusp.GetTangent(0);
        Assert.Equal(MathF.Sqrt(0.5f), start.X, 1e-3);
        Assert.Equal(MathF.Sqrt(0.5f), start.Y, 1e-3);
    }

    [Fact]
    public void SegmentsKeepCurves()
    {
        Path path = new PathBuilder().MoveTo(0, 0).LineTo(100, 0).CubicTo(150, 0, 150, 100, 100, 100).QuadTo(50, 150, 0, 100).ToPath();
        ContourMeasure c = new PathMeasure(path).Contours[0];
        Path piece = c.GetSegment(50, c.Length - 30);
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Cubic, PathVerb.Quad], piece.Verbs.ToArray());
        Assert.Equal(new Vector2(50, 0), piece.Points[0]);
        Assert.Equal(c.Length - 80, new PathMeasure(piece).TotalLength, 1e-3);

        // The whole contour gives the path back.
        Path whole = c.GetSegment(0, c.Length);
        Assert.Equal(path.Verbs.ToArray(), whole.Verbs.ToArray());
        for (int i = 0; i < path.Points.Length; i++)
        {
            Assert.True(Vector2.Distance(path.Points[i], whole.Points[i]) < 1e-4f);
        }

        Path square = Path.Rect(new RectF(0, 0, 10, 10));
        Path all = new PathMeasure(square).Contours[0].GetSegment(0, 40);
        Assert.Equal(square.Verbs.ToArray(), all.Verbs.ToArray());
        Assert.Equal(square.Points.ToArray(), all.Points.ToArray());

        Assert.True(c.GetSegment(30, 30).IsEmpty);
        Assert.True(c.GetSegment(30, 10).IsEmpty);
    }

    [Fact]
    public void SegmentsChainIntoOneContour()
    {
        ContourMeasure c = new PathMeasure(Path.Line(Vector2.Zero, new Vector2(100, 0))).Contours[0];
        PathBuilder builder = new();
        c.AddSegment(builder, 0, 20);
        c.AddSegment(builder, 50, 70, startWithMove: false);
        Path path = builder.ToPath();
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line], path.Verbs.ToArray());
        Assert.Single(path.Contours);
    }

    private static double SampledLength(Path path)
    {
        double total = 0;
        foreach (Path contour in path.Contours)
        {
            Vector2[] pts = contour.Points.ToArray();
            PathVerb[] verbs = contour.Verbs.ToArray();
            int p = 0;
            Vector2 last = default, start = default;
            foreach (PathVerb verb in verbs)
            {
                switch (verb)
                {
                    case PathVerb.Move:
                        start = last = pts[p++];
                        break;
                    case PathVerb.Line:
                        total += Vector2.Distance(last, pts[p]);
                        last = pts[p++];
                        break;
                    case PathVerb.Quad:
                        total += Sample(t => Quad(last, pts[p], pts[p + 1], t));
                        last = pts[p + 1];
                        p += 2;
                        break;
                    case PathVerb.Cubic:
                        total += Sample(t => Cubic(last, pts[p], pts[p + 1], pts[p + 2], t));
                        last = pts[p + 2];
                        p += 3;
                        break;
                    default:
                        total += Vector2.Distance(last, start);
                        last = start;
                        break;
                }
            }
        }

        return total;

        static double Sample(Func<double, (double X, double Y)> f)
        {
            const int N = 200_000;
            double sum = 0;
            (double X, double Y) a = f(0);
            for (int i = 1; i <= N; i++)
            {
                (double X, double Y) b = f(i / (double)N);
                sum += Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
                a = b;
            }

            return sum;
        }

        static (double X, double Y) Quad(Vector2 p0, Vector2 p1, Vector2 p2, double t)
        {
            double u = 1 - t;
            return ((u * u * p0.X) + (2 * u * t * p1.X) + (t * t * p2.X), (u * u * p0.Y) + (2 * u * t * p1.Y) + (t * t * p2.Y));
        }

        static (double X, double Y) Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, double t)
        {
            double u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            return ((a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X), (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y));
        }
    }
}
