using System.Numerics;

using Egami.Drawing;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// A region as GDI+ rasterizes it: the pixels whose whole coordinates lie inside, with edges closed at the top and left and open
/// at the bottom and right, so a rectangle from 1.5 to 11.5 holds pixels 2 to 11. The result is rectangles, rows with the same
/// spans merged into bands, top to bottom and left to right.
/// </summary>
internal static class PixelScans
{
    /// <summary>The most rows scanned one by one; taller shapes with slanted edges are left as they are.</summary>
    private const int MaxRows = 1 << 16;

    /// <summary>The pixel rectangles of a shape, or null when it is too tall to scan row by row.</summary>
    public static List<RectangleF>? Rectangles(Path shape)
    {
        List<RectangleF> result = [];
        if (shape.IsEmpty)
        {
            return result;
        }

        List<(Vector2 A, Vector2 B)> edges = Edges(shape);
        if (edges.Count == 0)
        {
            return result;
        }

        bool rectilinear = edges.TrueForAll(e => e.A.X == e.B.X || e.A.Y == e.B.Y);
        int top = (int)MathF.Ceiling(edges.Min(e => MathF.Min(e.A.Y, e.B.Y)));
        int bottom = (int)MathF.Ceiling(edges.Max(e => MathF.Max(e.A.Y, e.B.Y)));
        List<int> rows;
        if (rectilinear)
        {
            // Only the rows where an edge starts or ends can differ from the row above.
            rows = [.. edges.SelectMany(e => new[] { (int)MathF.Ceiling(e.A.Y), (int)MathF.Ceiling(e.B.Y) }).Distinct().Order()];
        }
        else if ((long)bottom - top > MaxRows)
        {
            return null;
        }
        else
        {
            rows = [.. Enumerable.Range(top, bottom - top + 1)];
        }

        bool nonZero = shape.FillRule == FillRule.NonZero;
        List<(int Start, int End)> current = [];
        int bandTop = top;
        List<(float X, int Winding)> crossings = [];
        foreach (int y in rows)
        {
            List<(int Start, int End)> spans = y >= bottom ? [] : Spans(edges, y, nonZero, crossings);
            if (!spans.SequenceEqual(current))
            {
                Emit(result, current, bandTop, y);
                current = spans;
                bandTop = y;
            }
        }

        return result;
    }

    /// <summary>Whether the pixel at whole coordinates is one of the shape's, as GDI+ hit-tests a point.</summary>
    public static bool Contains(Path shape, int x, int y)
    {
        if (shape.IsEmpty)
        {
            return false;
        }

        List<(Vector2 A, Vector2 B)> edges = Edges(shape);
        foreach ((int start, int end) in Spans(edges, y, shape.FillRule == FillRule.NonZero, []))
        {
            if (x >= start && x < end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The pixel rectangles as a path, or the shape itself when it is too tall to scan.</summary>
    public static Path Snap(Path shape)
    {
        List<RectangleF>? rects = Rectangles(shape);
        if (rects is null)
        {
            return shape;
        }

        if (rects.Count == 0)
        {
            return Path.Empty;
        }

        PathBuilder builder = new();
        foreach (RectangleF r in rects)
        {
            builder.AddRect(new RectF(r.X, r.Y, r.Width, r.Height));
        }

        return builder.ToPath().WithFillRule(FillRule.NonZero);
    }

    private static void Emit(List<RectangleF> result, List<(int Start, int End)> spans, int top, int bottom)
    {
        foreach ((int start, int end) in spans)
        {
            result.Add(new RectangleF(start, top, end - start, bottom - top));
        }
    }

    /// <summary>The runs of pixels on row y: between crossings where the winding is inside, pixel x when start &lt;= x &lt; end.</summary>
    private static List<(int Start, int End)> Spans(List<(Vector2 A, Vector2 B)> edges, int y, bool nonZero, List<(float X, int Winding)> crossings)
    {
        crossings.Clear();
        foreach ((Vector2 a, Vector2 b) in edges)
        {
            if ((a.Y <= y && y < b.Y) || (b.Y <= y && y < a.Y))
            {
                float t = (y - a.Y) / (b.Y - a.Y);
                crossings.Add((a.X + ((b.X - a.X) * t), a.Y < b.Y ? 1 : -1));
            }
        }

        crossings.Sort((p, q) => p.X.CompareTo(q.X));
        List<(int Start, int End)> spans = [];
        int winding = 0;
        float from = 0;
        foreach ((float x, int w) in crossings)
        {
            bool inside = nonZero ? winding != 0 : (winding & 1) != 0;
            winding += w;
            bool after = nonZero ? winding != 0 : (winding & 1) != 0;
            if (!inside && after)
            {
                from = x;
            }
            else if (inside && !after)
            {
                int start = (int)MathF.Ceiling(from), end = (int)MathF.Ceiling(x);
                if (end > start)
                {
                    if (spans.Count > 0 && spans[^1].End >= start)
                    {
                        spans[^1] = (spans[^1].Start, Math.Max(end, spans[^1].End));
                    }
                    else
                    {
                        spans.Add((start, end));
                    }
                }
            }
        }

        return spans;
    }

    /// <summary>The shape's outline as straight edges, curves flattened, every figure closed.</summary>
    private static List<(Vector2 A, Vector2 B)> Edges(Path shape)
    {
        List<(Vector2, Vector2)> edges = [];
        ReadOnlySpan<Vector2> p = shape.Points;
        int at = 0;
        Vector2 start = default, current = default;
        bool open = false;
        foreach (PathVerb verb in shape.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    if (open && current != start)
                    {
                        edges.Add((current, start));
                    }

                    start = current = p[at++];
                    open = true;
                    break;
                case PathVerb.Line:
                    Add(edges, ref current, p[at++]);
                    break;
                case PathVerb.Quad:
                    {
                        Vector2 a = current, c = p[at], e = p[at + 1];
                        for (int i = 1; i <= 16; i++)
                        {
                            float t = i / 16f;
                            Add(edges, ref current, ((1 - t) * (1 - t) * a) + (2 * (1 - t) * t * c) + (t * t * e));
                        }

                        at += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    {
                        Vector2 a = current, c1 = p[at], c2 = p[at + 1], e = p[at + 2];
                        for (int i = 1; i <= 24; i++)
                        {
                            float t = i / 24f, u = 1 - t;
                            Add(edges, ref current, (u * u * u * a) + (3 * u * u * t * c1) + (3 * u * t * t * c2) + (t * t * t * e));
                        }

                        at += 3;
                        break;
                    }

                case PathVerb.Close:
                    if (open && current != start)
                    {
                        edges.Add((current, start));
                    }

                    current = start;
                    break;
            }
        }

        if (open && current != start)
        {
            edges.Add((current, start));
        }

        return edges;
    }

    private static void Add(List<(Vector2, Vector2)> edges, ref Vector2 current, Vector2 next)
    {
        if (next != current)
        {
            edges.Add((current, next));
        }

        current = next;
    }
}
