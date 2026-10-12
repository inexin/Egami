using System.Numerics;

namespace Egami.Drawing.Tests.Geometry;

public class BooleanTests
{
    private static readonly int RandomCases = int.TryParse(Environment.GetEnvironmentVariable("EGAMI_BOOLEAN_CASES"), out int n) ? n : 2000;

    private static readonly SvgPathFormat Exact = new() { Precision = 9 };

    private static readonly Path A = Path.Rect(new RectF(0, 0, 10, 10));
    private static readonly Path B = Path.Rect(new RectF(5, 5, 10, 10));

    [Fact]
    public void SquaresCombine()
    {
        Assert.Equal(175, Area(A.Union(B)), 1e-9);
        Assert.Equal(25, Area(A.Intersect(B)), 1e-9);
        Assert.Equal(75, Area(A.Difference(B)), 1e-9);
        Assert.Equal(75, Area(A.Op(B, PathOp.ReverseDifference)), 1e-9);
        Assert.Equal(150, Area(A.Xor(B)), 1e-9);
        Assert.Single(A.Union(B).Contours);
        Assert.Equal(2, A.Xor(B).Contours.Count);
    }

    [Fact]
    public void ResultsKeepCurves()
    {
        Path ring = Path.Circle(new Vector2(50, 50), 40).Difference(Path.Circle(new Vector2(50, 50), 20));
        Assert.Equal(2, ring.Contours.Count);
        Assert.DoesNotContain(PathVerb.Line, ring.Verbs.ToArray());
        Assert.Equal(Area(Path.Circle(new Vector2(50, 50), 40)) - Area(Path.Circle(new Vector2(50, 50), 20)), Area(ring), 1e-6);

        // Outer contours run clockwise on screen, holes counterclockwise.
        Assert.True(Area(ring.Contours[0]) * Area(ring.Contours[1]) < 0);

        // A lens: two circles crossing; the result's curves are pieces of the inputs' cubics.
        Path lens = Path.Circle(new Vector2(40, 50), 30).Intersect(Path.Circle(new Vector2(60, 50), 30));
        Assert.Single(lens.Contours);
        Assert.All(lens.Verbs[1..^1].ToArray(), v => Assert.Equal(PathVerb.Cubic, v));
        Assert.True(Area(lens) > 0);
    }

    [Fact]
    public void SharedEdgesAndCornersMerge()
    {
        Path left = Path.Rect(new RectF(0, 0, 10, 10)), right = Path.Rect(new RectF(10, 0, 10, 10));
        Path both = left.Union(right);
        Assert.Equal(200, Area(both), 1e-9);
        Assert.Single(both.Contours);
        Assert.True(left.Intersect(right).IsEmpty);
        Assert.Equal(100, Area(left.Difference(right)), 1e-9);

        // Touching at one corner: two separate squares.
        Path corner = Path.Rect(new RectF(10, 10, 10, 10));
        Assert.Equal(2, left.Union(corner).Contours.Count);

        // A path with itself.
        Assert.Equal(100, Area(left.Union(left)), 1e-9);
        Assert.Equal(100, Area(left.Intersect(left)), 1e-9);
        Assert.True(left.Difference(left).IsEmpty);
        Assert.True(left.Xor(left).IsEmpty);

        // Overlapping along part of an edge, and a vertex on another's edge.
        Path partial = Path.Rect(new RectF(10, 5, 10, 10));
        Assert.Equal(200, Area(left.Union(partial)), 1e-9);
        Path tee = Path.Polygon([new(10, 5), new(20, 0), new(20, 10)]);
        Assert.Equal(150, Area(left.Union(tee)), 1e-9);
    }

    [Fact]
    public void SimplifyFollowsTheFillRule()
    {
        Path star = Path.Polygon([new(50, 0), new(79, 90), new(2, 35), new(98, 35), new(21, 90)]);
        Path nonZero = star.Simplify(), evenOdd = star.WithFillRule(FillRule.EvenOdd).Simplify();
        Assert.True(Area(nonZero) > Area(evenOdd));
        Assert.Single(nonZero.Contours);
        Assert.Equal(5, evenOdd.Contours.Count);
        Assert.Equal(Area(nonZero) - Area(evenOdd), Math.Abs(Area(Path.Polygon(Pentagon(star)))), 1e-3);

        // A figure eight's lobes wind opposite ways: both fill under either rule.
        Path eight = Path.Polygon([new(0, 0), new(10, 10), new(10, 0), new(0, 10)]);
        Assert.Equal(50, Area(eight.Simplify()), 1e-9);
        Assert.Equal(2, eight.Simplify().Contours.Count);
        Assert.All(eight.Simplify().Contours, c => Assert.True(Area(c) > 0));
    }

    [Fact]
    public void FillRulesMixAcrossInputs()
    {
        // Two nested squares: under even-odd a frame, under nonzero (same direction) a solid square.
        Path nested = new PathBuilder().AddRect(new RectF(0, 0, 30, 30)).AddRect(new RectF(10, 10, 10, 10)).ToPath();
        Path cover = Path.Rect(new RectF(0, 0, 30, 30));
        Assert.Equal(900, Area(nested.Intersect(cover)), 1e-9);
        Assert.Equal(800, Area(nested.WithFillRule(FillRule.EvenOdd).Intersect(cover)), 1e-9);
        Assert.Equal(100, Area(cover.Difference(nested.WithFillRule(FillRule.EvenOdd))), 1e-9);
    }

    [Fact]
    public void DegenerateInputsDoNotThrow()
    {
        Assert.True(Path.Empty.Union(Path.Empty).IsEmpty);
        Assert.Equal(100, Area(A.Union(Path.Empty)), 1e-9);
        Assert.True(A.Intersect(Path.Empty).IsEmpty);
        Path line = Path.Line(Vector2.Zero, new Vector2(10, 10));
        Assert.True(line.Simplify().IsEmpty);
        Path open = Path.Polygon([new(0, 0), new(10, 0), new(10, 10)], close: false);
        Assert.Equal(50, Area(open.Simplify()), 1e-9);
        Assert.True(new PathBuilder().MoveTo(1, 1).MoveTo(2, 2).ToPath().Simplify().IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => A.Op(B, (PathOp)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => A.Union(B, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => A.Union(B, float.NaN));
    }

    /// <summary>Inputs the 2.6.2 fuzz run found slow: huge cubic loops running along each other. Their cutting has a budget.</summary>
    [Theory]
    [InlineData("M45 45 L80 80 C80 80 45 4545 80 80 C115 -4385 45 45 0 0")]
    [InlineData("M81 80 S 45 45, 0, 0,3 1 1 045 0 13 210 L20 20M.58 0.5 3 1910 145 80 S 45 45, 0, 0,3 1 1 0  ,0 Z")]
    [InlineData("M80 80 S 45 45, 0,0 S 45 45, 0, 0,3 1 1 0 45 45 1, 0,3 1 1 0 45 45 1,0 Z")]
    public void PathologicalLoopsFinish(string data)
    {
        Path path = Path.ParseSvg(data, lenient: true);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        path.Simplify();
        path.WithFillRule(FillRule.EvenOdd).Simplify();
        new Stroke(2) { Join = LineJoin.Round }.ToPath(path, simplify: true);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }

    [Fact]
    public void CombineSweepsManyPathsAtOnce()
    {
        List<Path> circles = [];
        for (int i = 0; i < 20; i++)
        {
            circles.Add(Path.Circle(new Vector2(10 + (i * 5), 50), 8));
        }

        Path union = PathOps.Combine(circles, PathOp.Union);
        Path chained = circles[0];
        foreach (Path c in circles.Skip(1))
        {
            chained = chained.Union(c);
        }

        Assert.Single(union.Contours);
        Assert.Equal(Area(chained), Area(union), 1e-3);

        // In order, left to right: ((a − b) − c).
        Path[] three = [Path.Rect(new RectF(0, 0, 30, 10)), Path.Rect(new RectF(0, 0, 10, 10)), Path.Rect(new RectF(20, 0, 10, 10))];
        Assert.Equal(100, Area(PathOps.Combine(three, PathOp.Difference)), 1e-9);
        Assert.Equal(100, Area(PathOps.Combine(three, PathOp.Xor)), 1e-9);
        Assert.True(PathOps.Combine([], PathOp.Union).IsEmpty);
    }

    [Fact]
    public void RandomPolygonsKeepTheAreaIdentities()
    {
        Random random = new(11);
        for (int i = 0; i < RandomCases; i++)
        {
            Path a = RandomShape(random, i), b = RandomShape(random, i + 1);
            CheckIdentities(a, b, $"case {i} ({a.FillRule}, {b.FillRule}): {a.ToSvg(Exact)} | {b.ToSvg(Exact)}");
        }
    }

    [Fact]
    public void NearlyParallelAndCoincidentEdgesAreRobust()
    {
        Random random = new(12);
        for (int i = 0; i < Math.Max(200, RandomCases / 10); i++)
        {
            Path a = Path.Rect(new RectF(0, 0, 100, 100));
            float angle = (float)((random.NextDouble() - 0.5) * Math.Pow(10, -random.Next(3, 9)));
            float shift = (float)((random.NextDouble() - 0.5) * Math.Pow(10, -random.Next(1, 7)));
            Matrix3x2 m = Matrix3x2.CreateRotation(angle, new Vector2(50, 50)) * Matrix3x2.CreateTranslation(shift, random.Next(3) == 0 ? 0 : shift * 0.5f);
            Path b = (random.Next(2) == 0 ? a : Path.Rect(new RectF(30, 0, 100, 60))).Transform(m);
            CheckIdentities(a, b, $"case {i}: angle {angle} shift {shift} {b.ToSvg(Exact)}", 1e-2);
        }
    }

    private static void CheckIdentities(Path a, Path b, string what, double slack = 1e-3)
    {
        double sa = Area(a.Simplify()), sb = Area(b.Simplify());
        double union = Area(a.Union(b)), intersect = Area(a.Intersect(b));
        double scale = Math.Max(1, Math.Max(Math.Abs(sa), Math.Abs(sb)));
        double tolerance = slack * scale;
        Assert.True(union >= -tolerance && intersect >= -tolerance, $"{what}: negative area");
        Assert.True(Math.Abs(union + intersect - sa - sb) <= tolerance, $"{what}: union {union} + intersect {intersect} vs {sa} + {sb}");
        Assert.True(Math.Abs(Area(a.Xor(b)) - (union - intersect)) <= tolerance, $"{what}: xor");
        Assert.True(Math.Abs(Area(a.Difference(b)) - (sa - intersect)) <= tolerance, $"{what}: difference");
    }

    private static Path RandomShape(Random random, int salt)
    {
        PathBuilder builder = new();
        int kind = salt % 4;
        int contours = 1 + random.Next(2);
        for (int c = 0; c < contours; c++)
        {
            int n = 3 + random.Next(8);
            builder.MoveTo(P());
            for (int k = 1; k < n; k++)
            {
                switch (kind == 2 ? random.Next(3) : 0)
                {
                    case 0:
                        builder.LineTo(P());
                        break;
                    case 1:
                        builder.QuadTo(P(), P());
                        break;
                    default:
                        builder.CubicTo(P(), P(), P());
                        break;
                }
            }

            builder.Close();
        }

        return builder.ToPath(random.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd);

        // Kind 1 snaps to a coarse grid, which makes shared vertices, collinear overlaps and vertices on edges common.
        Vector2 P() => kind == 1 ? new Vector2(random.Next(0, 6) * 20, random.Next(0, 6) * 20) : new Vector2((float)(random.NextDouble() * 100), (float)(random.NextDouble() * 100));
    }

    private static Vector2[] Pentagon(Path star)
    {
        // The inner pentagon of a five-pointed star: where its edges cross.
        ReadOnlySpan<Vector2> p = star.Points;
        Vector2[] corners = new Vector2[5];
        for (int i = 0; i < 5; i++)
        {
            Vector2 a0 = p[i], a1 = p[(i + 1) % 5], b0 = p[(i + 2) % 5], b1 = p[(i + 3) % 5];
            Vector2 r = a1 - a0, q = b1 - b0;
            float t = ((b0.X - a0.X) * q.Y - (b0.Y - a0.Y) * q.X) / (r.X * q.Y - r.Y * q.X);
            corners[i] = a0 + (t * r);
        }

        Vector2 center = (corners[0] + corners[1] + corners[2] + corners[3] + corners[4]) / 5;
        return [.. corners.OrderBy(c => MathF.Atan2(c.Y - center.Y, c.X - center.X))];
    }

    private static double Area(Path path) => PathEffectTests.Area(path);
}
