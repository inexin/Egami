using System.Numerics;

namespace Egami.Drawing.Geometry;

/// <summary>Bézier evaluation, splitting, extremes and arc-to-cubic conversion shared by paths, the flattener and the stroker.</summary>
internal static class Curves
{
    /// <summary>The handle length, as a fraction of the radius, of a cubic approximating a quarter circle: 4/3·tan(π/8).</summary>
    internal const float QuarterKappa = 0.552284749831f;

    internal static Vector2 Quad(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        float u = 1 - t;
        return (u * u * p0) + (2 * u * t * p1) + (t * t * p2);
    }

    internal static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1 - t;
        return (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3);
    }

    /// <summary>Returns the cubic's tangent (derivative over 3) at <paramref name="t"/>.</summary>
    internal static Vector2 CubicTangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1 - t;
        return (u * u * (p1 - p0)) + (2 * u * t * (p2 - p1)) + (t * t * (p3 - p2));
    }

    /// <summary>Splits a cubic at <paramref name="t"/> by de Casteljau's construction into two cubics, written as seven points.</summary>
    internal static void SplitCubic(ReadOnlySpan<Vector2> c, float t, Span<Vector2> result)
    {
        Vector2 ab = Vector2.Lerp(c[0], c[1], t), bc = Vector2.Lerp(c[1], c[2], t), cd = Vector2.Lerp(c[2], c[3], t);
        Vector2 abc = Vector2.Lerp(ab, bc, t), bcd = Vector2.Lerp(bc, cd, t);
        Vector2 mid = Vector2.Lerp(abc, bcd, t);
        result[0] = c[0];
        result[1] = ab;
        result[2] = abc;
        result[3] = mid;
        result[4] = bcd;
        result[5] = cd;
        result[6] = c[3];
    }

    /// <summary>Raises a quadratic to the cubic that draws the same curve.</summary>
    internal static (Vector2 C1, Vector2 C2) Elevate(Vector2 p0, Vector2 p1, Vector2 p2)
    {
        return (p0 + ((2f / 3f) * (p1 - p0)), p2 + ((2f / 3f) * (p1 - p2)));
    }

    /// <summary>Adds the quadratic's interior extremes on both axes (where the derivative is 0) to <paramref name="ts"/>; returns the count.</summary>
    internal static int QuadExtrema(Vector2 p0, Vector2 p1, Vector2 p2, Span<float> ts)
    {
        int n = 0;
        n = QuadExtremum(p0.X, p1.X, p2.X, ts, n);
        n = QuadExtremum(p0.Y, p1.Y, p2.Y, ts, n);
        return n;
    }

    /// <summary>Adds the cubic's interior extremes on both axes to <paramref name="ts"/>, at most four; returns the count.</summary>
    internal static int CubicExtrema(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Span<float> ts)
    {
        int n = 0;
        n = CubicExtremum(p0.X, p1.X, p2.X, p3.X, ts, n);
        n = CubicExtremum(p0.Y, p1.Y, p2.Y, p3.Y, ts, n);
        return n;
    }

    /// <summary>Adds the cubic's interior extremes in y only, sorted, to <paramref name="ts"/>; returns the count (at most 2).</summary>
    internal static int CubicExtremaY(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Span<float> ts)
    {
        int n = CubicExtremum(p0.Y, p1.Y, p2.Y, p3.Y, ts, 0);
        if (n == 2 && ts[1] < ts[0])
        {
            (ts[0], ts[1]) = (ts[1], ts[0]);
        }

        return n;
    }

    /// <summary>Adds the quadratic's interior extreme in y, if any; returns the count.</summary>
    internal static int QuadExtremaY(Vector2 p0, Vector2 p1, Vector2 p2, Span<float> ts) => QuadExtremum(p0.Y, p1.Y, p2.Y, ts, 0);

    /// <summary>
    /// Appends cubics approximating the arc of an ellipse from angle <paramref name="start"/> through <paramref name="sweep"/>
    /// (radians, positive turning clockwise on a y-down screen) to <paramref name="points"/>, three points per cubic, at most 90
    /// degrees each. The handle length is 4/3·tan(θ/4), which keeps the radial error below 0.03% of the radius.
    /// </summary>
    /// <returns>The arc's start point.</returns>
    internal static Vector2 ArcToCubics(Vector2 center, Vector2 radii, float rotation, double start, double sweep, List<Vector2> points)
    {
        (float sinR, float cosR) = MathF.SinCos(rotation);
        Vector2 At(double angle)
        {
            (double s, double c) = Math.SinCos(angle);
            float x = (float)(radii.X * c), y = (float)(radii.Y * s);
            return center + new Vector2((x * cosR) - (y * sinR), (x * sinR) + (y * cosR));
        }

        Vector2 Tangent(double angle)
        {
            (double s, double c) = Math.SinCos(angle);
            float x = (float)(-radii.X * s), y = (float)(radii.Y * c);
            return new Vector2((x * cosR) - (y * sinR), (x * sinR) + (y * cosR));
        }

        int segments = Math.Max(1, (int)Math.Ceiling((Math.Abs(sweep) / (Math.PI / 2)) - 1e-9));
        double step = sweep / segments;
        float k = (float)(4.0 / 3.0 * Math.Tan(step / 4));
        double a = start;
        Vector2 p = At(a);
        Vector2 first = p;
        for (int i = 0; i < segments; i++)
        {
            double b = i == segments - 1 ? start + sweep : a + step;
            Vector2 q = At(b);
            points.Add(p + (k * Tangent(a)));
            points.Add(q - (k * Tangent(b)));
            points.Add(q);
            p = q;
            a = b;
        }

        return first;
    }

    private static int QuadExtremum(float a, float b, float c, Span<float> ts, int n)
    {
        float denominator = a - (2 * b) + c;
        if (denominator != 0)
        {
            float t = (a - b) / denominator;
            if (t > 0 && t < 1)
            {
                ts[n++] = t;
            }
        }

        return n;
    }

    private static int CubicExtremum(float p0, float p1, float p2, float p3, Span<float> ts, int n)
    {
        // The derivative over 3 is a·t² + b·t + c with these coefficients.
        double a = -p0 + (3.0 * p1) - (3.0 * p2) + p3;
        double b = 2.0 * (p0 - (2.0 * p1) + p2);
        double c = p1 - (double)p0;
        Span<double> roots = stackalloc double[2];
        int count = SolveQuadratic(a, b, c, roots);
        for (int i = 0; i < count; i++)
        {
            if (roots[i] > 0 && roots[i] < 1)
            {
                ts[n++] = (float)roots[i];
            }
        }

        return n;
    }

    /// <summary>Solves a·t² + b·t + c = 0 with the numerically stable form; returns the number of real roots written.</summary>
    internal static int SolveQuadratic(double a, double b, double c, Span<double> roots)
    {
        double scale = Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c)));
        if (scale == 0)
        {
            return 0;
        }

        if (Math.Abs(a) <= scale * 1e-12)
        {
            if (b == 0)
            {
                return 0;
            }

            roots[0] = -c / b;
            return 1;
        }

        double d = (b * b) - (4 * a * c);
        if (d < 0)
        {
            return 0;
        }

        double q = -0.5 * (b + (Math.Sign(b == 0 ? 1 : b) * Math.Sqrt(d)));
        roots[0] = q / a;
        if (q == 0)
        {
            return 1;
        }

        roots[1] = c / q;
        return roots[0] == roots[1] ? 1 : 2;
    }
}
