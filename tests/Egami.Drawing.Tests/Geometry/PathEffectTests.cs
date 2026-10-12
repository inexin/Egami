using System.Numerics;

using Egami.Drawing.Tests.Canvas;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Geometry;

public class PathEffectTests
{
    private static readonly Path Square = Path.Rect(new RectF(0, 0, 100, 100));

    [Fact]
    public void IdentityEffectsLeavePathsUnchanged()
    {
        Path wave = new PathBuilder().MoveTo(0, 0).CubicTo(10, 20, 30, -20, 40, 0).LineTo(50, 50).ToPath();
        Assert.Same(wave, wave.Apply(PathEffect.Trim(0, 1)));
        Assert.Same(wave, wave.Apply(PathEffect.Corner(0)));
        Assert.True(wave.Apply(PathEffect.Trim(0, 1, inverted: true)).IsEmpty);
    }

    [Fact]
    public void TrimKeepsFractionsOfTheTotalLength()
    {
        Path two = new PathBuilder().MoveTo(0, 0).LineTo(100, 0).MoveTo(0, 10).LineTo(100, 10).ToPath();
        Path half = two.Apply(PathEffect.Trim(0.25f, 0.75f));
        Assert.Equal(100, new PathMeasure(half).TotalLength, 1e-3);
        Assert.Equal(2, half.Contours.Count);
        Assert.Equal(new Vector2(50, 0), half.Points[0]);
        Assert.Equal(new Vector2(50, 10), half.Points[^1]);

        Path rest = two.Apply(PathEffect.Trim(0.25f, 0.75f, inverted: true));
        Assert.Equal(100, new PathMeasure(rest).TotalLength, 1e-3);
        Assert.True(two.Apply(PathEffect.Trim(0.5f, 0.5f)).IsEmpty);

        Path circle = Path.Circle(Vector2.Zero, 10).Apply(PathEffect.Trim(0, 0.5f));
        Assert.All(circle.Verbs[1..].ToArray(), v => Assert.Equal(PathVerb.Cubic, v));
        Assert.Throws<ArgumentOutOfRangeException>(() => PathEffect.Trim(-0.1f, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PathEffect.Trim(0, float.NaN));
    }

    [Fact]
    public void CornersBecomeQuadratics()
    {
        Path rounded = Square.Apply(PathEffect.Corner(10));
        Assert.Equal(4, rounded.Verbs.ToArray().Count(v => v == PathVerb.Quad));

        // Each quadratic cuts off a sixth of the square of its radius.
        Assert.Equal(10000 - (4 * 100 / 6.0), Area(rounded), 1e-3);

        // Lines shorter than twice the radius round from their middles; open ends stay put.
        Path open = Path.Polygon([new(0, 0), new(10, 0), new(10, 100)], close: false).Apply(PathEffect.Corner(20));
        Assert.Equal(new Vector2(0, 0), open.Points[0]);
        Assert.Equal(new Vector2(5, 0), open.Points[1]);
        Assert.Equal(new Vector2(10, 100), open.Points[^1]);

        // Corners next to curves stay sharp.
        Path mixed = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).QuadTo(20, 0, 20, 10).LineTo(0, 10).ToPath().Apply(PathEffect.Corner(2));
        Assert.Equal(1, mixed.Verbs.ToArray().Count(v => v == PathVerb.Quad));
    }

    [Fact]
    public void JitterIsTheSameEverywhere()
    {
        PathEffect jitter = PathEffect.Jitter(5, 2, seed: 7);
        Path first = Square.Apply(jitter);
        string[] runs = new string[16];
        Parallel.For(0, runs.Length, i => runs[i] = Square.Apply(jitter).ToSvg());
        Assert.All(runs, r => Assert.Equal(first.ToSvg(), r));
        Assert.NotEqual(first.ToSvg(), Square.Apply(PathEffect.Jitter(5, 2, seed: 8)).ToSvg());
        Assert.Equal(80, first.Points.Length);

        // Points move across the path by at most the deviation.
        foreach (Vector2 p in first.Points)
        {
            float distance = MathF.Min(MathF.Min(MathF.Abs(p.X), MathF.Abs(p.X - 100)), MathF.Min(MathF.Abs(p.Y), MathF.Abs(p.Y - 100)));
            Assert.True(distance <= 2.0001f);
        }
    }

    [Fact]
    public void StampsGoAlongThePath()
    {
        Path dot = Path.Circle(Vector2.Zero, 1);
        Path line = Path.Line(Vector2.Zero, new Vector2(100, 0));
        Path dots = line.Apply(PathEffect.Stamp(dot, 10));
        Assert.Equal(11, dots.Contours.Count);
        Assert.Equal(new Vector2(31, 0), dots.Contours[3].Points[0]);
        Assert.Equal(10, line.Apply(PathEffect.Stamp(dot, 10, phase: 5)).Contours.Count);

        // Rotated: an arrow pointing along +x points down a downward line.
        Path arrow = Path.Polygon([new(0, -1), new(4, 0), new(0, 1)]);
        Path down = Path.Line(Vector2.Zero, new Vector2(0, 50)).Apply(PathEffect.Stamp(arrow, 20, 0, StampStyle.Rotate));
        Assert.Equal(new Vector2(0, 4), down.Points[1], new ApproximateVector(1e-5f));

        // Morphed: a bar along +x bends round a circle and stays on it.
        Path bar = Path.Rect(new RectF(0, -1, 30, 2));
        Path bent = Path.Circle(Vector2.Zero, 50).Apply(PathEffect.Stamp(bar, 100, 0, StampStyle.Morph));
        foreach (Vector2 p in bent.Contours[0].Points)
        {
            Assert.InRange(p.Length(), 48.5f, 51.5f);
        }
    }

    [Fact]
    public void HatchesFillTheArea()
    {
        Path lines = Square.Apply(PathEffect.Hatch(Matrix3x2.CreateScale(1, 10)));
        Assert.Equal(10, lines.Contours.Count);
        Assert.All(lines.Contours, c => Assert.Equal(100, new PathMeasure(c).TotalLength, 1e-3));

        Path bands = Square.Apply(PathEffect.Hatch(Matrix3x2.CreateScale(1, 10), 2));
        Assert.Equal(10 * 100 * 2, Math.Abs(Area(bands)), 1e-2);

        // Diagonal hatching of a circle stays inside it.
        Path circle = Path.Circle(new Vector2(50, 50), 40);
        Path diagonal = circle.Apply(PathEffect.Hatch(Matrix3x2.CreateScale(1, 6) * Matrix3x2.CreateRotation(MathF.PI / 4)));
        Assert.True(diagonal.Contours.Count > 10);
        foreach (Vector2 p in diagonal.Points)
        {
            Assert.True(Vector2.Distance(p, new Vector2(50, 50)) <= 40.01f);
        }

        Path holes = Square.Apply(PathEffect.HatchPath(Matrix3x2.CreateScale(25), Path.Circle(Vector2.Zero, 2)));
        Assert.Equal(16, holes.Contours.Count);
        Assert.Throws<ArgumentException>(() => PathEffect.Hatch(Matrix3x2.CreateScale(0)));
    }

    [Fact]
    public void EffectsCompose()
    {
        PathEffect dash = PathEffect.Dash(new DashPattern([10, 10]));
        PathEffect trim = PathEffect.Trim(0, 0.5f);
        Path line = Path.Line(Vector2.Zero, new Vector2(100, 0));
        Assert.Equal(3, line.Apply(trim.Then(dash)).Contours.Count);
        Assert.Equal(5 + 1, line.Apply(dash.Plus(trim)).Contours.Count);
        Assert.Equal(PathEffect.Corner(2), PathEffect.Corner(2));
    }

    [Fact]
    public void TheCanvasAppliesStrokeEffects()
    {
        using TraceCanvas canvas = new(100, 100);
        Stroke stroke = new(2) { Effect = PathEffect.Corner(5), Dash = new DashPattern([4, 4]) };
        canvas.StrokeRect(new RectF(10, 10, 50, 50), stroke, Rgba8.Black);
        canvas.StrokePath(Square, stroke, Rgba8.Black);
        canvas.DrawLine(Vector2.Zero, Vector2.One, stroke with { Effect = PathEffect.Trim(0, 0.5f) }, Rgba8.Black);

        // Backends see the effect's path, never the effect, and the dashes as data.
        Assert.Equal(3, canvas.Calls.Count(c => c.StartsWith("DrawPath ", StringComparison.Ordinal) && c.Contains("dash [4 4] 0", StringComparison.Ordinal)));
        Assert.DoesNotContain(canvas.Calls, c => c.StartsWith("DrawRect", StringComparison.Ordinal));

        double plain = StrokeTests.Coverage(c => c.StrokePath(Path.Line(new(5, 30), new(60, 30)), new Stroke(4), Rgba8.White));
        double trimmed = StrokeTests.Coverage(c => c.StrokePath(Path.Line(new(5, 30), new(60, 30)), new Stroke(4) { Effect = PathEffect.Trim(0, 0.5f) }, Rgba8.White));
        Assert.Equal(plain / 2, trimmed, 1.0);
    }

    /// <summary>The signed area a path encloses (positive clockwise on screen), exact for its curves: Green's theorem, ∮ x dy.</summary>
    internal static double Area(Path path)
    {
        double[] nodes = [0, -0.5384693101056831, 0.5384693101056831, -0.9061798459386640, 0.9061798459386640];
        double[] weights = [0.5688888888888889, 0.4786286704993665, 0.4786286704993665, 0.2369268850561891, 0.2369268850561891];
        double area = 0;
        Egami.Drawing.Geometry.Segment.ReadContours(path, true, (segments, _, _, _) =>
        {
            foreach (Egami.Drawing.Geometry.Segment s in segments)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    double t = (nodes[i] + 1) / 2;
                    area += weights[i] / 2 * s.Point(t).X * s.Derivative(t).Y;
                }
            }
        });
        return area;
    }

    private sealed class ApproximateVector(float tolerance) : IEqualityComparer<Vector2>
    {
        public bool Equals(Vector2 x, Vector2 y) => Vector2.Distance(x, y) <= tolerance;

        public int GetHashCode(Vector2 obj) => 0;
    }
}
