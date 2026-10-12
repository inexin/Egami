using System.Numerics;

namespace Egami.Drawing.Geometry;

/// <summary>Where horizontal lines cross a filled path: the spans inside it by its fill rule, for hatching.</summary>
internal static class Scanlines
{
    /// <summary>
    /// Returns the path's outline as straight edges after a transform, every contour closed as fills close them. Curves are cut until
    /// each piece lies within a ten-thousandth of the path's size of its chord, in the path's own space.
    /// </summary>
    internal static List<(D2 A, D2 B)> Edges(Path path, Matrix3x2 matrix)
    {
        RectF bounds = path.Bounds;
        double tolerance = Math.Max(1e-4 * Math.Max(bounds.Width, bounds.Height), 1e-9);
        List<(D2 A, D2 B)> edges = [];
        Segment.ReadContours(path, true, (segments, _, _, _) =>
        {
            foreach (Segment s in segments)
            {
                Flatten(s, 0);
            }
        });
        return edges;

        void Flatten(Segment s, int depth)
        {
            if (depth >= 16 || s.IsFlat(tolerance))
            {
                D2 a = Vector2.Transform(s.P0.ToVector2(), matrix), b = Vector2.Transform(s.End.ToVector2(), matrix);
                if (a.Y != b.Y)
                {
                    edges.Add((a, b));
                }

                return;
            }

            Flatten(s.Sub(0, 0.5), depth + 1);
            Flatten(s.Sub(0.5, 1), depth + 1);
        }
    }

    /// <summary>Returns the spans of the line at <paramref name="y"/> inside the edges by the fill rule, left to right.</summary>
    internal static IEnumerable<(double X0, double X1)> Spans(List<(D2 A, D2 B)> edges, double y, FillRule fillRule, List<(double X, int Winding)> crossings)
    {
        crossings.Clear();
        foreach ((D2 a, D2 b) in edges)
        {
            // Half-open in y, so a line through a vertex counts the two edges meeting there once between them.
            bool down = a.Y <= y && y < b.Y, up = b.Y <= y && y < a.Y;
            if (down || up)
            {
                double x = a.X + ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y));
                crossings.Add((x, down ? 1 : -1));
            }
        }

        crossings.Sort((p, q) => p.X.CompareTo(q.X));
        int winding = 0;
        double start = 0;
        foreach ((double x, int w) in crossings)
        {
            bool wasInside = fillRule == FillRule.NonZero ? winding != 0 : (winding & 1) != 0;
            winding += w;
            bool isInside = fillRule == FillRule.NonZero ? winding != 0 : (winding & 1) != 0;
            if (!wasInside && isInside)
            {
                start = x;
            }
            else if (wasInside && !isInside && x > start)
            {
                yield return (start, x);
            }
        }
    }
}
