using System.Numerics;

using Egami.Drawing.Geometry;
using Egami.Drawing.Rasterization;

namespace Egami.Drawing.Tests.Rasterization;

public class FlattenerTests
{
    /// <summary>Every point of a curve lies within the tolerance of the lines it was flattened to, for random quadratics and cubics.</summary>
    [Fact]
    public void FlattenedCurvesStayWithinTheTolerance()
    {
        Random random = new(17);
        for (int i = 0; i < 400; i++)
        {
            Vector2 Next() => new((float)(random.NextDouble() * 400) - 200, (float)(random.NextDouble() * 400) - 200);
            Vector2 p0 = Next(), p1 = Next(), p2 = Next(), p3 = Next();
            float tolerance = i % 2 == 0 ? 0.1f : 0.02f;

            List<Vector2> quad = [p0];
            Flattener.Quad(p0, p1, p2, tolerance, quad);
            float wq = Worst(t => Curves.Quad(p0, p1, p2, t), quad);
            Assert.True(wq <= tolerance * 1.05f, $"quad {i}: {wq} > {tolerance} with {quad.Count - 1} lines, {p0} {p1} {p2}");

            List<Vector2> cubic = [p0];
            Flattener.Cubic(p0, p1, p2, p3, tolerance, cubic);
            Assert.True(Worst(t => Curves.Cubic(p0, p1, p2, p3, t), cubic) <= tolerance * 1.05f, $"cubic {i}");
        }
    }

    /// <summary>Levien's method needs fewer lines than Wang's formula on a quadratic for the same tolerance.</summary>
    [Fact]
    public void QuadraticsNeedFewerLinesThanWangsFormula()
    {
        Vector2 p0 = new(0, 0), p1 = new(100, 200), p2 = new(200, 0);
        List<Vector2> quad = [p0];
        Flattener.Quad(p0, p1, p2, 0.1f, quad);
        (Vector2 c1, Vector2 c2) = Curves.Elevate(p0, p1, p2);
        Assert.True(quad.Count - 1 < Flattener.CubicCount(p0, c1, c2, p2, 0.1f));
    }

    [Fact]
    public void DegenerateCurvesFlattenToTheirEnds()
    {
        List<Vector2> straight = [Vector2.Zero];
        Flattener.Quad(Vector2.Zero, new Vector2(5, 5), new Vector2(10, 10), 0.1f, straight);
        Assert.Equal(new Vector2(10, 10), straight[^1]);
        List<Vector2> point = [Vector2.One];
        Flattener.Cubic(Vector2.One, Vector2.One, Vector2.One, Vector2.One, 0.1f, point);
        Assert.Equal([Vector2.One, Vector2.One], point);
    }

    private static float Worst(Func<float, Vector2> curve, List<Vector2> polyline)
    {
        float worst = 0;
        for (int s = 0; s <= 2000; s++)
        {
            Vector2 p = curve(s / 2000f);
            float best = float.MaxValue;
            for (int i = 0; i + 1 < polyline.Count; i++)
            {
                Vector2 a = polyline[i], ab = polyline[i + 1] - a;
                float t = ab.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
                best = MathF.Min(best, Vector2.Distance(a + (t * ab), p));
            }

            worst = MathF.Max(worst, best);
        }

        return worst;
    }
}
