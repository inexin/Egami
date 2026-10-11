using System.Numerics;

using Egami.Operations;

namespace Egami.Drawing.Tests.Geometry;

public class PathTests
{
    [Fact]
    public void BuilderRoundTripsVerbsAndPoints()
    {
        Path path = new PathBuilder()
            .MoveTo(1, 2).LineTo(3, 4).QuadTo(5, 6, 7, 8).CubicTo(9, 10, 11, 12, 13, 14).Close()
            .LineTo(20, 20)
            .ToPath(FillRule.EvenOdd);
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Quad, PathVerb.Cubic, PathVerb.Close, PathVerb.Move, PathVerb.Line], path.Verbs.ToArray());
        Assert.Equal(
            [new(1, 2), new(3, 4), new(5, 6), new(7, 8), new(9, 10), new(11, 12), new(13, 14), new(1, 2), new(20, 20)],
            path.Points.ToArray());
        Assert.Equal(FillRule.EvenOdd, path.FillRule);
        Assert.Equal(2, path.Contours.Count);

        Path copy = new PathBuilder().AddPath(path).ToPath(FillRule.EvenOdd);
        Assert.Equal(path.Verbs.ToArray(), copy.Verbs.ToArray());
        Assert.Equal(path.Points.ToArray(), copy.Points.ToArray());
    }

    [Fact]
    public void PathsDoNotChangeWhenTheBuilderGoesOn()
    {
        PathBuilder builder = new PathBuilder().MoveTo(0, 0).LineTo(10, 0);
        Path first = builder.ToPath();
        builder.LineTo(10, 10).Close();
        Path second = builder.ToPath();
        Assert.Equal(2, first.Verbs.Length);
        Assert.Equal(4, second.Verbs.Length);
        builder.Reset();
        Assert.True(builder.IsEmpty);
        Assert.Equal(2, first.Points.Length);
    }

    [Fact]
    public void NonFiniteAndUndefinedInputsThrow()
    {
        Assert.Throws<ArgumentException>(() => new PathBuilder().MoveTo(float.NaN, 0).ToPath());
        Assert.Throws<ArgumentException>(() => new PathBuilder().MoveTo(0, 0).LineTo(float.PositiveInfinity, 0).ToPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathBuilder().ToPath((FillRule)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(1, 1), new Vector2(2, 0), -1));
    }

    [Fact]
    public void EmptyAndMoveOnlyPathsAreEmpty()
    {
        Assert.True(Path.Empty.IsEmpty);
        Assert.True(new PathBuilder().MoveTo(4, 4).ToPath().IsEmpty);
        Assert.False(Path.Line(Vector2.Zero, Vector2.One).IsEmpty);
        Assert.Equal(RectF.Empty, Path.Empty.TightBounds);
    }

    [Fact]
    public void TightBoundsMeasureCurvesAtTheirExtremes()
    {
        Path circle = Path.Circle(new Vector2(50, 40), 10);
        RectF tight = circle.TightBounds;
        Assert.Equal(40, tight.Left, 3);
        Assert.Equal(60, tight.Right, 3);
        Assert.Equal(30, tight.Top, 3);
        Assert.Equal(50, tight.Bottom, 3);

        Path bump = new PathBuilder().MoveTo(0, 0).CubicTo(0, -40, 10, -40, 10, 0).ToPath();
        Assert.Equal(-40, bump.Bounds.Top);
        Assert.Equal(-30, bump.TightBounds.Top, 3);
    }

    [Fact]
    public void ArcsStayWithinThreeHundredthsOfAPercentOfTheRadius()
    {
        foreach (float radius in new[] { 1f, 10f, 1000f })
        {
            foreach ((float start, float sweep) in new[] { (0f, 90f), (13f, 271f), (-45f, -300f), (0f, 360f) })
            {
                Path arc = Path.Arc(new RectF(-radius, -radius, 2 * radius, 2 * radius), start, sweep);
                float worst = WorstRadialError(arc, Vector2.Zero, radius);
                Assert.True(worst <= 0.0003f * radius, $"radius {radius}, {start}+{sweep}: {worst / radius:P4}");
            }
        }
    }

    [Fact]
    public void ArcsStartAndEndWhereTheAnglesSay()
    {
        Path arc = Path.Arc(new RectF(0, 0, 20, 20), 90, 90);
        Assert.Equal(10, arc.Points[0].X, 4);
        Assert.Equal(20, arc.Points[0].Y, 4);
        Assert.Equal(0, arc.Points[^1].X, 4);
        Assert.Equal(10, arc.Points[^1].Y, 4);
    }

    [Fact]
    public void SvgArcsFollowTheImplementationNotes()
    {
        // A half circle from (0,0) to (20,0), radius 10: the sweep flag picks the side.
        Path below = new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(10, 10), 0, false, false, new Vector2(20, 0)).ToPath();
        Path above = new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(10, 10), 0, false, true, new Vector2(20, 0)).ToPath();
        Assert.Equal(10, below.TightBounds.Bottom, 3);
        Assert.Equal(-10, above.TightBounds.Top, 3);
        Assert.Equal(new Vector2(20, 0), below.Points[^1]);

        // Radii too small are scaled up until they reach: radius 1 between points 20 apart draws the radius-10 half circle.
        Path scaled = new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(1, 1), 0, false, false, new Vector2(20, 0)).ToPath();
        Assert.Equal(10, scaled.TightBounds.Bottom, 3);

        // A zero radius draws a line; a zero-length arc nothing.
        Path line = new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(0, 5), 0, false, false, new Vector2(20, 0)).ToPath();
        Assert.Equal([PathVerb.Move, PathVerb.Line], line.Verbs.ToArray());
        Path none = new PathBuilder().MoveTo(3, 3).ArcTo(new Vector2(5, 5), 0, true, true, new Vector2(3, 3)).ToPath();
        Assert.Equal([PathVerb.Move], none.Verbs.ToArray());

        // The large arc of a circle of radius 10 through two points 10 apart: its center is 8.66 off the chord, and the arc
        // reaches the far side of the circle.
        Path large = new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(10, 10), 0, true, true, new Vector2(10, 0)).ToPath();
        Assert.Equal(10 + (5 * MathF.Sqrt(3)), large.TightBounds.Height, 2);
        Assert.Equal(20, large.TightBounds.Width, 2);
    }

    [Fact]
    public void TangentArcsTouchBothLines()
    {
        Path path = new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(20, 0), new Vector2(20, 20), 5).LineTo(20, 20).ToPath();
        Assert.Equal(new Vector2(15, 0), path.Points[1]);
        Assert.Equal(20, path.Points[^2].X, 4);
        Assert.Equal(5, path.Points[^2].Y, 4);
        Path arc = new PathBuilder().MoveTo(15, 0).CubicTo(path.Points[2], path.Points[3], path.Points[4]).ToPath();
        Assert.True(WorstRadialError(arc, new Vector2(15, 5), 5) < 0.002f);

        Path parallel = new PathBuilder().MoveTo(0, 0).ArcTo(new Vector2(10, 0), new Vector2(20, 0), 5).ToPath();
        Assert.Equal([PathVerb.Move, PathVerb.Line], parallel.Verbs.ToArray());
    }

    [Fact]
    public void ContainsUsesTheFillRuleAndCountsTheEdgeAsInside()
    {
        Path square = Path.Rect(new RectF(0, 0, 10, 10));
        Assert.True(square.Contains(new Vector2(5, 5)));
        Assert.True(square.Contains(new Vector2(0, 5)));
        Assert.True(square.Contains(new Vector2(10, 10)));
        Assert.False(square.Contains(new Vector2(10.01f, 5)));

        Path twice = new PathBuilder().AddRect(new RectF(0, 0, 10, 10)).AddRect(new RectF(2, 2, 6, 6)).ToPath();
        Assert.True(twice.Contains(new Vector2(5, 5)));
        Assert.False(twice.WithFillRule(FillRule.EvenOdd).Contains(new Vector2(5, 5)));
        Path hole = new PathBuilder().AddRect(new RectF(0, 0, 10, 10)).AddRect(new RectF(2, 2, 6, 6), PathDirection.CounterClockwise).ToPath();
        Assert.False(hole.Contains(new Vector2(5, 5)));
        Assert.True(hole.Contains(new Vector2(1, 5)));

        Path circle = Path.Circle(new Vector2(0, 0), 10);
        Assert.True(circle.Contains(new Vector2(7, 7)));
        Assert.False(circle.Contains(new Vector2(7.2f, 7.2f)));
        Assert.True(circle.Contains(new Vector2(10, 0)));
        Assert.True(circle.Contains(new Vector2(0, -10)));
    }

    [Fact]
    public void ContainsMatchesAFineFlatteningOnRandomPoints()
    {
        Random random = new(5);
        Path star = Path.Star(new Vector2(50, 50), 45, 18, 7, 11);
        Path blob = new PathBuilder().MoveTo(10, 50).CubicTo(10, -20, 90, 120, 90, 50).QuadTo(60, 0, 50, 90).CubicTo(30, 100, 0, 0, 10, 50).Close().ToPath(FillRule.EvenOdd);
        foreach (Path path in new[] { star, blob, blob.WithFillRule(FillRule.NonZero) })
        {
            Vector2[] polygon = Flatten(path, 2000);
            for (int i = 0; i < 4000; i++)
            {
                Vector2 p = new((float)(random.NextDouble() * 100), (float)(random.NextDouble() * 100));
                bool expected = PolygonContains(polygon, p, path.FillRule, out double distance);
                if (distance > 0.05)
                {
                    Assert.Equal(expected, path.Contains(p));
                }
            }
        }
    }

    [Fact]
    public void TransformsMoveEveryPoint()
    {
        Path square = Path.Rect(new RectF(0, 0, 10, 10));
        Path moved = square.Transform(Matrix3x2.CreateTranslation(5, 7));
        Assert.Equal(new RectF(5, 7, 10, 10), moved.Bounds);
        Assert.Same(square, square.Transform(Matrix3x2.Identity));

        Homography perspective = Homography.FromQuad(new Rect(0, 0, 10, 10), [new(0, 0), new(20, 0), new(15, 10), new(5, 10)]);
        Path projected = Path.Circle(new Vector2(5, 5), 5).Transform(perspective);
        for (int i = 0; i <= 64; i++)
        {
            double a = 2 * Math.PI * i / 64;
            Vector2 truth = perspective.Map(new Vector2(5 + (float)(5 * Math.Cos(a)), 5 + (float)(5 * Math.Sin(a))));
            Assert.True(projected.Contains(Vector2.Lerp(truth, new Vector2(10, 5), 0.02f)));
        }

        Homography behind = new(1, 0, -1, 0, 1, 0, 0, 0, 1);
        Assert.Throws<ArgumentException>(() => square.Transform(behind));
    }

    [Fact]
    public void ReverseRunsEachContourTheOtherWay()
    {
        Path path = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).QuadTo(10, 10, 0, 10).Close().MoveTo(20, 20).CubicTo(21, 21, 22, 22, 23, 20).ToPath();
        Path reversed = path.Reverse();
        Assert.Equal([PathVerb.Move, PathVerb.Quad, PathVerb.Line, PathVerb.Close, PathVerb.Move, PathVerb.Cubic], reversed.Verbs.ToArray());
        Assert.Equal([new(0, 10), new(10, 10), new(10, 0), new(0, 0), new(23, 20), new(22, 22), new(21, 21), new(20, 20)], reversed.Points.ToArray());
        Assert.Equal(path.Points.ToArray(), reversed.Reverse().Points.ToArray());
    }

    [Fact]
    public void ConvexityIsSeen()
    {
        Assert.True(Path.Rect(new RectF(0, 0, 4, 4)).IsConvex);
        Assert.True(Path.Circle(Vector2.Zero, 3).IsConvex);
        Assert.True(Path.RoundRect(new RoundRect(new RectF(0, 0, 40, 20), 5)).IsConvex);
        Assert.True(Path.RegularPolygon(Vector2.Zero, 10, 7).IsConvex);
        Assert.False(Path.Star(Vector2.Zero, 10, 4, 5).IsConvex);
        Assert.False(Path.Polygon([new(0, 0), new(10, 0), new(0, 10), new(10, 10)]).IsConvex);
        Assert.False(new PathBuilder().AddRect(new RectF(0, 0, 1, 1)).AddRect(new RectF(5, 5, 1, 1)).ToPath().IsConvex);

        // A pentagram's turns all go one way, but it winds twice.
        Vector2[] pentagram = new Vector2[5];
        for (int i = 0; i < 5; i++)
        {
            double a = (4 * Math.PI * i / 5) - (Math.PI / 2);
            pentagram[i] = new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
        }

        Assert.False(Path.Polygon(pentagram).IsConvex);
        Assert.True(Path.Line(Vector2.Zero, Vector2.One).IsConvex);
    }

    [Fact]
    public void ShapeHelpersDrawTheirShapes()
    {
        Path ring = Path.Ring(new RoundRect(new RectF(0, 0, 20, 20), 4), new RoundRect(new RectF(5, 5, 10, 10), 2));
        Assert.Equal(FillRule.EvenOdd, ring.FillRule);
        Assert.False(ring.Contains(new Vector2(10, 10)));
        Assert.True(ring.Contains(new Vector2(2, 10)));
        Assert.True(ring.WithFillRule(FillRule.NonZero).Contains(new Vector2(2, 10)));
        Assert.False(ring.WithFillRule(FillRule.NonZero).Contains(new Vector2(10, 10)));

        Path pie = Path.Pie(new RectF(0, 0, 20, 20), 0, 90);
        Assert.True(pie.Contains(new Vector2(14, 14)));
        Assert.False(pie.Contains(new Vector2(6, 14)));

        Path hexagon = Path.RegularPolygon(new Vector2(0, 0), 10, 6);
        Assert.Equal(0, hexagon.Points[0].X, 5);
        Assert.Equal(-10, hexagon.Points[0].Y, 5);
        Path star = Path.Star(new Vector2(0, 0), 10, 5, 5);
        Assert.Equal(10, star.Points.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => Path.RegularPolygon(Vector2.Zero, 1, 2));
    }

    internal static Vector2[] Flatten(Path path, int stepsPerCurve)
    {
        List<Vector2> points = [];
        ReadOnlySpan<Vector2> p = path.Points;
        int i = 0;
        Vector2 last = default;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                case PathVerb.Line:
                    last = p[i++];
                    points.Add(last);
                    break;
                case PathVerb.Quad:
                    for (int s = 1; s <= stepsPerCurve; s++)
                    {
                        float t = s / (float)stepsPerCurve, u = 1 - t;
                        points.Add((u * u * last) + (2 * u * t * p[i]) + (t * t * p[i + 1]));
                    }

                    last = p[i + 1];
                    i += 2;
                    break;
                case PathVerb.Cubic:
                    for (int s = 1; s <= stepsPerCurve; s++)
                    {
                        float t = s / (float)stepsPerCurve, u = 1 - t;
                        points.Add((u * u * u * last) + (3 * u * u * t * p[i]) + (3 * u * t * t * p[i + 1]) + (t * t * t * p[i + 2]));
                    }

                    last = p[i + 2];
                    i += 3;
                    break;
            }
        }

        return [.. points];
    }

    internal static bool PolygonContains(Vector2[] polygon, Vector2 p, FillRule rule, out double distance)
    {
        int winding = 0;
        distance = double.MaxValue;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
            Vector2 ab = b - a;
            float t = ab.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            distance = Math.Min(distance, Vector2.Distance(a + (t * ab), p));
            if (a.Y <= p.Y && p.Y < b.Y && a.X + ((p.Y - a.Y) * ab.X / ab.Y) > p.X)
            {
                winding++;
            }
            else if (b.Y <= p.Y && p.Y < a.Y && a.X + ((p.Y - a.Y) * ab.X / ab.Y) > p.X)
            {
                winding--;
            }
        }

        return rule == FillRule.NonZero ? winding != 0 : (winding & 1) != 0;
    }

    private static float WorstRadialError(Path path, Vector2 center, float radius)
    {
        float worst = 0;
        foreach (Vector2 p in Flatten(path, 64))
        {
            worst = MathF.Max(worst, MathF.Abs(Vector2.Distance(p, center) - radius));
        }

        return worst;
    }
}
