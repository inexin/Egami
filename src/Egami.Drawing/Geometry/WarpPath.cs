using System.Numerics;

namespace Egami.Drawing.Geometry;

/// <summary>
/// Maps a path through any function of the plane. Each segment is read as a cubic in its own parameter and its image is
/// approximated piece by piece: a piece whose image stays straight becomes a line, otherwise a cubic with the image's end points
/// and end tangents (the tangents from a four-point one-sided difference, exact for cubics, so affine maps reproduce curves
/// exactly). A piece that strays further than the tolerance from the true image at any of five probe parameters is halved.
/// </summary>
internal static class WarpPath
{
    private const int MaxDepth = 12;

    /// <summary>The step, as a fraction of a piece, of the finite differences that give the end tangents.</summary>
    private const float Step = 1f / 16f;

    /// <summary>The name of <see cref="Path.Warp"/>'s function parameter, for its exceptions.</summary>
    private static readonly string MapParameter = "map";

    private static ReadOnlySpan<float> Probes => [0.1f, 0.3f, 0.5f, 0.7f, 0.9f];

    internal static Path Warp(Path path, Func<Vector2, Vector2> map, float tolerance)
    {
        Warper warper = new(map, tolerance * tolerance);
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        Vector2 last = default, start = default, mappedLast = default, mappedStart = default;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    start = last = points[p++];
                    mappedStart = mappedLast = warper.Map(last);
                    warper.Builder.MoveTo(mappedStart);
                    break;
                case PathVerb.Line:
                    {
                        Vector2 end = points[p++];
                        Vector2 mappedEnd = warper.Map(end);
                        warper.Segment(last, Vector2.Lerp(last, end, 1f / 3f), Vector2.Lerp(last, end, 2f / 3f), end, true, mappedLast, mappedEnd);
                        (last, mappedLast) = (end, mappedEnd);
                        break;
                    }

                case PathVerb.Quad:
                    {
                        Vector2 end = points[p + 1];
                        (Vector2 c1, Vector2 c2) = Curves.Elevate(last, points[p], end);
                        Vector2 mappedEnd = warper.Map(end);
                        warper.Segment(last, c1, c2, end, false, mappedLast, mappedEnd);
                        (last, mappedLast) = (end, mappedEnd);
                        p += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    {
                        Vector2 end = points[p + 2];
                        Vector2 mappedEnd = warper.Map(end);
                        warper.Segment(last, points[p], points[p + 1], end, false, mappedLast, mappedEnd);
                        (last, mappedLast) = (end, mappedEnd);
                        p += 3;
                        break;
                    }

                default:
                    // The closing line bends like any other line; it is drawn here so the contour closes on its mapped start.
                    if (last != start)
                    {
                        warper.Segment(last, Vector2.Lerp(last, start, 1f / 3f), Vector2.Lerp(last, start, 2f / 3f), start, true, mappedLast, mappedStart);
                    }

                    warper.Builder.Close();
                    (last, mappedLast) = (start, mappedStart);
                    break;
            }
        }

        return warper.Builder.ToPath(path.FillRule);
    }

    private sealed class Warper(Func<Vector2, Vector2> map, float toleranceSquared)
    {
        private Vector2 _c0, _c1, _c2, _c3;
        private bool _line;

        internal PathBuilder Builder { get; } = new();

        internal Vector2 Map(Vector2 point)
        {
            Vector2 mapped = map(point);
            if (!float.IsFinite(mapped.X) || !float.IsFinite(mapped.Y))
            {
                throw new ArgumentException($"The map sends the point {point} to {mapped}, which is not a finite point.", MapParameter);
            }

            return mapped;
        }

        internal void Segment(Vector2 c0, Vector2 c1, Vector2 c2, Vector2 c3, bool line, Vector2 mappedStart, Vector2 mappedEnd)
        {
            (_c0, _c1, _c2, _c3, _line) = (c0, c1, c2, c3, line);
            Piece(0, 1, mappedStart, mappedEnd, 0);
        }

        private Vector2 At(float t) => Map(Curves.Cubic(_c0, _c1, _c2, _c3, t));

        private void Piece(float t0, float t1, Vector2 f0, Vector2 f1, int depth)
        {
            float dt = t1 - t0;
            Span<Vector2> truth = stackalloc Vector2[5];
            for (int i = 0; i < truth.Length; i++)
            {
                truth[i] = At(t0 + (Probes[i] * dt));
            }

            if (_line && IsStraight(f0, f1, truth))
            {
                Builder.LineTo(f1);
                return;
            }

            float h = Step * dt;
            Vector2 d0 = ((-11 * f0) + (18 * At(t0 + h)) - (9 * At(t0 + (2 * h))) + (2 * At(t0 + (3 * h)))) / (6 * Step);
            Vector2 d1 = ((11 * f1) - (18 * At(t1 - h)) + (9 * At(t1 - (2 * h))) - (2 * At(t1 - (3 * h)))) / (6 * Step);
            Vector2 a = f0 + (d0 / 3), b = f1 - (d1 / 3);
            bool fits = depth >= MaxDepth;
            for (int i = 0; i < truth.Length && !fits; i++)
            {
                if (Vector2.DistanceSquared(Curves.Cubic(f0, a, b, f1, Probes[i]), truth[i]) > toleranceSquared)
                {
                    break;
                }

                fits = i == truth.Length - 1;
            }

            if (fits)
            {
                Builder.CubicTo(a, b, f1);
                return;
            }

            float mid = t0 + (dt / 2);
            Vector2 fm = truth[2];
            Piece(t0, mid, f0, fm, depth + 1);
            Piece(mid, t1, fm, f1, depth + 1);
        }

        /// <summary>Returns whether every probe lies within the tolerance of the chord from <paramref name="f0"/> to <paramref name="f1"/>.</summary>
        private bool IsStraight(Vector2 f0, Vector2 f1, ReadOnlySpan<Vector2> truth)
        {
            Vector2 chord = f1 - f0;
            float length = chord.LengthSquared();
            foreach (Vector2 point in truth)
            {
                float t = length > 0 ? Math.Clamp(Vector2.Dot(point - f0, chord) / length, 0, 1) : 0;
                if (Vector2.DistanceSquared(f0 + (t * chord), point) > toleranceSquared)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
