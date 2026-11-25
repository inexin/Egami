using System.Numerics;

namespace Egami.Drawing.Tests.Geometry;

public class PathWarpTests
{
    [Fact]
    public void IdentityKeepsEverySegment()
    {
        Path path = new PathBuilder()
            .MoveTo(0, 0).LineTo(40, 0).QuadTo(60, 20, 40, 40).CubicTo(30, 50, 10, 50, 0, 40).LineTo(0, 0).Close()
            .AddCircle(new Vector2(100, 100), 30)
            .ToPath(FillRule.EvenOdd);
        Path warped = path.Warp(p => p);
        Assert.Equal(FillRule.EvenOdd, warped.FillRule);
        Assert.Equal(
            path.Verbs.ToArray().Select(v => v == PathVerb.Quad ? PathVerb.Cubic : v),
            warped.Verbs.ToArray());
        Assert.Equal(0, MaxDistance(warped, Sample(path), 0), 3);
    }

    [Fact]
    public void AffineMatchesTransform()
    {
        Path path = Path.Circle(new Vector2(20, 30), 15);
        Matrix3x2 matrix = Matrix3x2.CreateRotation(0.7f) * Matrix3x2.CreateScale(2, 0.5f) * Matrix3x2.CreateTranslation(10, -4);
        Path transformed = path.Transform(matrix);
        Path warped = path.Warp(p => Vector2.Transform(p, matrix));
        Assert.Equal(transformed.Verbs.ToArray(), warped.Verbs.ToArray());
        for (int i = 0; i < transformed.Points.Length; i++)
        {
            Assert.True(Vector2.Distance(transformed.Points[i], warped.Points[i]) < 1e-3f, $"point {i}");
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.1f)]
    [InlineData(0.01f)]
    public void SineWaveStaysWithinTolerance(float tolerance)
    {
        static Vector2 Wave(Vector2 p) => new(p.X, p.Y + (10 * MathF.Sin(p.X / 20)));

        Path line = new PathBuilder().MoveTo(0, 50).LineTo(200, 50).ToPath();
        Path warped = line.Warp(Wave, tolerance);
        Assert.True(warped.Verbs.Length > 2);
        foreach (Vector2 p in Sample(warped))
        {
            Assert.True(Math.Abs(p.Y - Wave(new Vector2(p.X, 50)).Y) <= tolerance * 1.5f, $"{p} strays from the wave");
        }

        Assert.Equal(new Vector2(0, 50), warped.Points[0]);
        Assert.Equal(Wave(new Vector2(200, 50)), warped.Points[^1]);
    }

    [Fact]
    public void FinerToleranceGivesMoreSegments()
    {
        Path circle = Path.Circle(new Vector2(50, 50), 40);
        static Vector2 Swirl(Vector2 p)
        {
            Vector2 d = p - new Vector2(50, 50);
            float a = d.Length() / 30;
            return new Vector2(50, 50) + Vector2.Transform(d, Matrix3x2.CreateRotation(a)) + new Vector2(0, 5 * MathF.Sin(p.X / 7));
        }

        int coarse = circle.Warp(Swirl, 1).Verbs.Length;
        int fine = circle.Warp(Swirl, 0.01f).Verbs.Length;
        Assert.True(fine > coarse, $"{fine} segments at 0.01, {coarse} at 1");
        foreach (float tolerance in new[] { 1f, 0.05f, 0.01f })
        {
            Path warped = circle.Warp(Swirl, tolerance);
            Assert.True(MaxDistance(warped, Sample(circle).Select(Swirl), 0) <= tolerance * 1.5f);
        }
    }

    [Fact]
    public void ClosedContoursStayClosedAndTheClosingLineBends()
    {
        // An arch: x stays, y lifts most in the middle. The rectangle's closing line (the left side) runs from the bottom back to the top.
        static Vector2 Arch(Vector2 p) => new(p.X, p.Y - (30 * MathF.Sin(MathF.PI * p.X / 100)));

        Path rect = new PathBuilder().MoveTo(0, 50).LineTo(100, 50).LineTo(100, 70).LineTo(0, 70).Close().ToPath();
        Path warped = rect.Warp(Arch);
        Assert.Equal(PathVerb.Close, warped.Verbs[^1]);
        Assert.Single(warped.Contours);
        Assert.Equal(warped.Points[0], warped.Points[^1]);
        Assert.True(warped.Contains(new Vector2(50, 30)));
        Assert.False(warped.Contains(new Vector2(50, 55)));
        Assert.True(warped.Contains(new Vector2(5, 59)));

        Path open = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).MoveTo(20, 0).LineTo(30, 0).Close().ToPath();
        Path both = open.Warp(p => p + new Vector2(1, 1));
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Close], both.Verbs.ToArray());
    }

    [Fact]
    public void EmptyPathAndBadArguments()
    {
        Assert.Same(Path.Empty, Path.Empty.Warp(p => p * 2));
        Path square = Path.Rect(new RectF(0, 0, 10, 10));
        Assert.Throws<ArgumentNullException>(() => square.Warp(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => square.Warp(p => p, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => square.Warp(p => p, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => square.Warp(p => p, float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => square.Warp(p => p.X > 5 ? new Vector2(float.NaN, 0) : p));
    }

    /// <summary>Returns points along every segment of the path, closing lines included.</summary>
    private static List<Vector2> Sample(Path path)
    {
        List<Vector2> result = [];
        ReadOnlySpan<Vector2> points = path.Points;
        Vector2 last = default, start = default;
        int p = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            Vector2 end = verb switch
            {
                PathVerb.Move => points[p],
                PathVerb.Line => points[p],
                PathVerb.Quad => points[p + 1],
                PathVerb.Cubic => points[p + 2],
                _ => start,
            };
            for (int i = 0; i <= 64 && verb != PathVerb.Move; i++)
            {
                float t = i / 64f, u = 1 - t;
                result.Add(verb switch
                {
                    PathVerb.Quad => (u * u * last) + (2 * u * t * points[p]) + (t * t * end),
                    PathVerb.Cubic => (u * u * u * last) + (3 * u * u * t * points[p]) + (3 * u * t * t * points[p + 1]) + (t * t * t * end),
                    _ => Vector2.Lerp(last, end, t),
                });
            }

            if (verb == PathVerb.Move)
            {
                start = end;
            }

            p += verb switch { PathVerb.Move or PathVerb.Line => 1, PathVerb.Quad => 2, PathVerb.Cubic => 3, _ => 0 };
            last = end;
        }

        return result;
    }

    /// <summary>Returns the largest distance from any of <paramref name="truth"/> to the nearest sampled point of the path.</summary>
    private static float MaxDistance(Path path, IEnumerable<Vector2> truth, float floor)
    {
        Vector2[] samples = [.. Sample(path)];
        float worst = floor;
        foreach (Vector2 t in truth)
        {
            float nearest = float.MaxValue;
            for (int i = 1; i < samples.Length; i++)
            {
                Vector2 a = samples[i - 1], b = samples[i] - a;
                float s = b.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(t - a, b) / b.LengthSquared(), 0, 1) : 0;
                nearest = Math.Min(nearest, Vector2.Distance(a + (s * b), t));
            }

            worst = Math.Max(worst, nearest);
        }

        return worst;
    }
}
