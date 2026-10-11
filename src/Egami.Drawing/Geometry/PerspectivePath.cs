using System.Numerics;

using Egami.Operations;

namespace Egami.Drawing.Geometry;

/// <summary>
/// Applies a perspective transform to a path. A projected line is a line, so lines map point for point; a projected curve is a
/// rational curve, so each curve is raised to a cubic and halved until the cubic through its projected control points stays within
/// <see cref="Tolerance"/> of the true projection at its quarter points.
/// </summary>
internal static class PerspectivePath
{
    private const float Tolerance = 0.01f;
    private const int MaxDepth = 10;

    internal static Path Transform(Path path, Homography h)
    {
        PathBuilder builder = new();
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        Vector2 last = default;
        Span<Vector2> cubic = stackalloc Vector2[4];
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    last = points[p++];
                    builder.MoveTo(Map(h, last));
                    break;
                case PathVerb.Line:
                    last = points[p++];
                    builder.LineTo(Map(h, last));
                    break;
                case PathVerb.Quad:
                    {
                        (Vector2 c1, Vector2 c2) = Curves.Elevate(last, points[p], points[p + 1]);
                        cubic[0] = last;
                        cubic[1] = c1;
                        cubic[2] = c2;
                        cubic[3] = points[p + 1];
                        Cubic(builder, h, cubic, 0);
                        last = points[p + 1];
                        p += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    cubic[0] = last;
                    cubic[1] = points[p];
                    cubic[2] = points[p + 1];
                    cubic[3] = points[p + 2];
                    Cubic(builder, h, cubic, 0);
                    last = points[p + 2];
                    p += 3;
                    break;
                default:
                    builder.Close();
                    break;
            }
        }

        return builder.ToPath(path.FillRule);
    }

    private static void Cubic(PathBuilder builder, Homography h, ReadOnlySpan<Vector2> c, int depth)
    {
        Vector2 a = Map(h, c[1]), b = Map(h, c[2]), e = Map(h, c[3]), s = Map(h, c[0]);
        bool close = depth >= MaxDepth;
        if (!close)
        {
            close = true;
            for (int i = 1; i <= 3 && close; i++)
            {
                float t = i / 4f;
                Vector2 truth = Map(h, Curves.Cubic(c[0], c[1], c[2], c[3], t));
                Vector2 guess = Curves.Cubic(s, a, b, e, t);
                close = Vector2.DistanceSquared(truth, guess) <= Tolerance * Tolerance;
            }
        }

        if (close)
        {
            builder.CubicTo(a, b, e);
            return;
        }

        Span<Vector2> halves = stackalloc Vector2[7];
        Curves.SplitCubic(c, 0.5f, halves);
        Cubic(builder, h, halves[..4], depth + 1);
        Cubic(builder, h, halves[3..], depth + 1);
    }

    private static Vector2 Map(Homography h, Vector2 p)
    {
        double w = (p.X * h.M13) + (p.Y * h.M23) + h.M33;
        if (!(w > 0))
        {
            throw new ArgumentException("The perspective sends a point of the path to infinity or behind the viewer.", nameof(h));
        }

        Vector2 mapped = h.Map(p);
        if (!float.IsFinite(mapped.X) || !float.IsFinite(mapped.Y))
        {
            throw new ArgumentException("The perspective sends a point of the path outside the range of float coordinates.", nameof(h));
        }

        return mapped;
    }
}
