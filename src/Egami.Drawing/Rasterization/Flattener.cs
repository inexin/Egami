using System.Numerics;

using Egami.Drawing.Geometry;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Turns a path into straight device-space edges. Points are transformed first, so the tolerance is in device pixels whatever the
/// transform. Quadratics are cut where the integral of the square root of their curvature (Levien, "Flattening quadratic Bézier
/// curves", 2019) is evenly spaced, which needs the fewest lines for a given error; cubics are cut uniformly into the count Wang's
/// formula gives. Every contour is closed, since a fill covers what an open contour encloses.
/// </summary>
internal static class Flattener
{
    /// <summary>More lines than this per curve stop adding accuracy anyone can see, and bound the work a huge curve can cause.</summary>
    private const int MaxSegments = 1 << 14;

    internal static void Flatten(Path path, Matrix3x2 matrix, float tolerance, EdgeList edges)
    {
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        Vector2 start = default, last = default;
        bool open = false;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    if (open)
                    {
                        edges.Add(last, start);
                    }

                    start = last = Vector2.Transform(points[p++], matrix);
                    open = true;
                    break;
                case PathVerb.Line:
                    {
                        Vector2 next = Vector2.Transform(points[p++], matrix);
                        edges.Add(last, next);
                        last = next;
                        break;
                    }

                case PathVerb.Quad:
                    {
                        Vector2 c = Vector2.Transform(points[p], matrix), e = Vector2.Transform(points[p + 1], matrix);
                        Quad(last, c, e, tolerance, edges);
                        last = e;
                        p += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    {
                        Vector2 c1 = Vector2.Transform(points[p], matrix), c2 = Vector2.Transform(points[p + 1], matrix), e = Vector2.Transform(points[p + 2], matrix);
                        Cubic(last, c1, c2, e, tolerance, edges);
                        last = e;
                        p += 3;
                        break;
                    }

                default:
                    if (open)
                    {
                        edges.Add(last, start);
                    }

                    last = start;
                    open = false;
                    break;
            }
        }

        if (open)
        {
            edges.Add(last, start);
        }
    }

    /// <summary>Flattens one cubic into <paramref name="output"/> as points after the start (the start itself is not added).</summary>
    internal static void Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance, List<Vector2> output)
    {
        int n = CubicCount(p0, p1, p2, p3, tolerance);
        for (int i = 1; i < n; i++)
        {
            output.Add(Curves.Cubic(p0, p1, p2, p3, i / (float)n));
        }

        output.Add(p3);
    }

    /// <summary>Flattens one quadratic into <paramref name="output"/> as points after the start.</summary>
    internal static void Quad(Vector2 p0, Vector2 p1, Vector2 p2, float tolerance, List<Vector2> output, bool cutAtVertex = true)
    {
        // The halves have their vertices at their ends, so they are not cut again (rounding at huge coordinates could say otherwise).
        if (cutAtVertex && SplitAtVertex(p0, p1, p2, out Vector2 a1, out Vector2 m, out Vector2 b1))
        {
            Quad(p0, a1, m, tolerance, output, false);
            Quad(m, b1, p2, tolerance, output, false);
            return;
        }

        QuadPlan plan = PlanQuad(p0, p1, p2, tolerance);
        for (int i = 1; i < plan.Count; i++)
        {
            output.Add(Curves.Quad(p0, p1, p2, plan.T(i)));
        }

        output.Add(p2);
    }

    /// <summary>Wang's formula: n = ⌈√(3·2/8 · max‖second difference‖ / tolerance)⌉ lines keep a cubic within the tolerance.</summary>
    internal static int CubicCount(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance)
    {
        float d = MathF.Max((p0 - (2 * p1) + p2).Length(), (p1 - (2 * p2) + p3).Length());
        float n = MathF.Ceiling(MathF.Sqrt(0.75f * d / tolerance));
        return n >= 1 ? (int)MathF.Min(n, MaxSegments) : 1;
    }

    private static void Quad(Vector2 p0, Vector2 p1, Vector2 p2, float tolerance, EdgeList edges, bool cutAtVertex = true)
    {
        if (cutAtVertex && SplitAtVertex(p0, p1, p2, out Vector2 a1, out Vector2 m, out Vector2 b1))
        {
            Quad(p0, a1, m, tolerance, edges, false);
            Quad(m, b1, p2, tolerance, edges, false);
            return;
        }

        QuadPlan plan = PlanQuad(p0, p1, p2, tolerance);
        Vector2 last = p0;
        for (int i = 1; i < plan.Count; i++)
        {
            Vector2 next = Curves.Quad(p0, p1, p2, plan.T(i));
            edges.Add(last, next);
            last = next;
        }

        edges.Add(last, p2);
    }

    private static void Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance, EdgeList edges)
    {
        int n = CubicCount(p0, p1, p2, p3, tolerance);
        Vector2 last = p0;

        // Forward differences would drift in float; evaluating each point keeps the lines exactly on the curve.
        for (int i = 1; i < n; i++)
        {
            Vector2 next = Curves.Cubic(p0, p1, p2, p3, i / (float)n);
            edges.Add(last, next);
            last = next;
        }

        edges.Add(last, p3);
    }

    /// <summary>
    /// Splits a quadratic at its point of greatest curvature (the parabola's vertex) when that lies inside it. The subdivision count
    /// spreads lines by the integral of √curvature, which says a sharp, nearly cusped tip needs almost nothing, yet a line across
    /// the tip cuts it off; with the tip as an end point, each half curves less and less away from it, where the count holds.
    /// </summary>
    private static bool SplitAtVertex(Vector2 p0, Vector2 p1, Vector2 p2, out Vector2 a1, out Vector2 mid, out Vector2 b1)
    {
        Vector2 dd = (2 * p1) - p0 - p2;
        double u0 = Vector2.Dot(p1 - p0, dd), u2 = Vector2.Dot(p2 - p1, dd);
        a1 = mid = b1 = default;
        if (!(u0 > 0 && u2 < 0) && !(u0 < 0 && u2 > 0))
        {
            return false;
        }

        // The tangent's component along the axis is linear in t and is 0 at the vertex.
        float t = (float)(u0 / (u0 - u2));
        if (!(t > 1e-4f && t < 1 - 1e-4f))
        {
            return false;
        }

        a1 = Vector2.Lerp(p0, p1, t);
        b1 = Vector2.Lerp(p1, p2, t);
        mid = Vector2.Lerp(a1, b1, t);
        return true;
    }

    /// <summary>
    /// Maps the quadratic onto a segment of the parabola y = x², whose arc of "flatness" between x0 and x1 is close to the integral
    /// of (1 + 4x²)^(-1/4); cutting that integral into equal parts gives the cut points. Straight or degenerate quadratics fall
    /// back to Wang's count.
    /// </summary>
    private static QuadPlan PlanQuad(Vector2 p0, Vector2 p1, Vector2 p2, float tolerance)
    {
        Vector2 dd = (2 * p1) - p0 - p2;
        double ddx = dd.X, ddy = dd.Y;
        double u0 = ((p1.X - (double)p0.X) * ddx) + ((p1.Y - (double)p0.Y) * ddy);
        double u2 = ((p2.X - (double)p1.X) * ddx) + ((p2.Y - (double)p1.Y) * ddy);
        double cross = ((p2.X - (double)p0.X) * ddy) - ((p2.Y - (double)p0.Y) * ddx);
        double x0 = u0 / cross, x2 = u2 / cross;
        double scale = Math.Abs(cross) / (Math.Sqrt((ddx * ddx) + (ddy * ddy)) * Math.Abs(x2 - x0));
        if (!double.IsFinite(x0) || !double.IsFinite(x2) || !double.IsFinite(scale) || scale == 0 || Math.Abs(cross) < 1e-9)
        {
            float d = dd.Length();
            int wang = (int)Math.Clamp(MathF.Ceiling(MathF.Sqrt(0.25f * d / tolerance)), 1, MaxSegments);
            return new QuadPlan(wang, 0, 0, 0, 0, true);
        }

        double a0 = IntegralApproximation(x0), a2 = IntegralApproximation(x2);
        double count = 0.5 * Math.Abs(a2 - a0) * Math.Sqrt(scale / tolerance);
        int n = (int)Math.Clamp(Math.Ceiling(count), 1, MaxSegments);
        double v0 = InverseApproximation(a0), v2 = InverseApproximation(a2);
        return new QuadPlan(n, a0, a2, v0, v2, false);
    }

    /// <summary>A close approximation of ∫₀ˣ (1 + 4t²)^(-1/4) dt.</summary>
    private static double IntegralApproximation(double x)
    {
        const double D = 0.67;
        return x / (1 - D + Math.Pow(Math.Pow(D, 4) + (0.25 * x * x), 0.25));
    }

    /// <summary>The inverse of <see cref="IntegralApproximation"/>, to the same accuracy.</summary>
    private static double InverseApproximation(double x)
    {
        const double B = 0.39;
        return x * (1 - B + Math.Sqrt((B * B) + (0.25 * x * x)));
    }

    private readonly record struct QuadPlan(int Count, double A0, double A2, double V0, double V2, bool Uniform)
    {
        internal float T(int i)
        {
            if (Uniform)
            {
                return i / (float)Count;
            }

            double x = InverseApproximation(A0 + ((A2 - A0) * i / Count));
            return (float)Math.Clamp((x - V0) / (V2 - V0), 0, 1);
        }
    }
}
