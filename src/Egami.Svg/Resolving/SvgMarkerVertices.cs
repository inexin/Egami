using System.Numerics;

using Egami.Drawing;

namespace Egami.Svg.Resolving;

/// <summary>Which markers a vertex takes.</summary>
internal enum SvgMarkerKind : byte
{
    Start,
    Middle,
    End,
}

/// <summary>
/// Where markers go on a path and which way they face (SVG 1.1 section 11.6.2): marker-start at the first vertex, marker-end at
/// the last, marker-mid at every other, each turned to the bisector of the incoming and outgoing directions. The vertex and
/// angle rules, including how zero-length control handles and closed subpaths are handled, follow resvg's
/// usvg/src/parser/marker.rs (Apache-2.0 or MIT; see THIRD-PARTY-NOTICES).
/// </summary>
internal static class SvgMarkerVertices
{
    private enum Kind : byte
    {
        Move,
        Line,
        Cubic,
        Close,
    }

    private readonly record struct Segment(Kind Kind, Vector2 P1, Vector2 P2, Vector2 P);

    /// <summary>The vertices of one kind with their positions and auto angles in degrees.</summary>
    public static List<(Vector2 Position, float Angle)> Vertices(Path path, SvgMarkerKind kind)
    {
        List<Segment> segments = Segments(path);
        List<(Vector2, float)> result = [];
        if (segments.Count == 0)
        {
            return result;
        }

        switch (kind)
        {
            case SvgMarkerKind.Start:
                if (segments[0].Kind == Kind.Move)
                {
                    result.Add((segments[0].P, segments.Count > 1 ? Angle(segments, 0) : 0));
                }

                break;
            case SvgMarkerKind.Middle:
                for (int i = 1; i < segments.Count - 1; i++)
                {
                    if (segments[i].Kind != Kind.Close)
                    {
                        result.Add((segments[i].P, Angle(segments, i)));
                    }
                }

                break;
            default:
                int last = segments.Count - 1;
                if (last < 1)
                {
                    break;
                }

                switch (segments[last].Kind)
                {
                    case Kind.Line or Kind.Cubic:
                        result.Add((segments[last].P, Angle(segments, last)));
                        break;
                    case Kind.Close:
                        result.Add((SubpathStart(segments, last), Angle(segments, last)));
                        break;
                }

                break;
        }

        return result;
    }

    private static List<Segment> Segments(Path path)
    {
        List<Segment> segments = new(path.Verbs.Length);
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        Vector2 previous = default;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    previous = points[p++];
                    segments.Add(new Segment(Kind.Move, default, default, previous));
                    break;
                case PathVerb.Line:
                    previous = points[p++];
                    segments.Add(new Segment(Kind.Line, default, default, previous));
                    break;
                case PathVerb.Quad:
                {
                    Vector2 c = points[p], e = points[p + 1];
                    p += 2;
                    segments.Add(new Segment(Kind.Cubic, (previous + (2 * c)) / 3, (e + (2 * c)) / 3, e));
                    previous = e;
                    break;
                }

                case PathVerb.Cubic:
                    segments.Add(new Segment(Kind.Cubic, points[p], points[p + 1], points[p + 2]));
                    previous = points[p + 2];
                    p += 3;
                    break;
                case PathVerb.Close:
                    segments.Add(new Segment(Kind.Close, default, default, default));
                    previous = SubpathStart(segments, segments.Count - 1);
                    break;
            }
        }

        return segments;
    }

    private static float Angle(List<Segment> path, int index)
    {
        if (index == 0)
        {
            Segment move = path[0], next = path[1];
            return (move.Kind, next.Kind) switch
            {
                (Kind.Move, Kind.Line) => LineAngle(move.P, next.P),
                (Kind.Move, Kind.Cubic) => LineAngle(move.P, Near(move.P, next.P1) ? next.P : next.P1),
                _ => 0,
            };
        }

        if (index == path.Count - 1)
        {
            Segment previous = path[index - 1], current = path[index];
            switch (current.Kind)
            {
                case Kind.Line:
                    return LineAngle(PreviousVertex(path, index), current.P);
                case Kind.Cubic:
                    return Near(current.P2, current.P) ? LineAngle(current.P1, current.P) : LineAngle(current.P2, current.P);
                case Kind.Close when previous.Kind == Kind.Line:
                    return LineAngle(previous.P, SubpathStart(path, index));
                case Kind.Close when previous.Kind == Kind.Cubic:
                {
                    Vector2 prev = PreviousVertex(path, index), next = SubpathStart(path, index);
                    return CurvesAngle(prev, previous.P2, previous.P, next, next);
                }

                default:
                    return 0;
            }
        }

        Segment a = path[index], b = path[index + 1];
        switch (a.Kind, b.Kind)
        {
            case (Kind.Move, Kind.Line):
                return LineAngle(a.P, b.P);
            case (Kind.Move, Kind.Cubic):
                return LineAngle(a.P, b.P1);
            case (Kind.Line, Kind.Line):
            {
                Vector2 prev = PreviousVertex(path, index);
                return BisectorAngle(prev, a.P, a.P, b.P);
            }

            case (Kind.Cubic, Kind.Cubic):
                return CurvesAngle(PreviousVertex(path, index), a.P2, a.P, b.P1, b.P);
            case (Kind.Line, Kind.Cubic):
            {
                Vector2 prev = PreviousVertex(path, index);
                return CurvesAngle(prev, prev, a.P, b.P1, b.P);
            }

            case (Kind.Cubic, Kind.Line):
                return CurvesAngle(PreviousVertex(path, index), a.P2, a.P, b.P, b.P);
            case (Kind.Line, Kind.Move):
                return LineAngle(PreviousVertex(path, index), a.P);
            case (Kind.Cubic, Kind.Move):
                return Near(a.P, a.P2) ? LineAngle(PreviousVertex(path, index), a.P) : LineAngle(a.P2, a.P);
            case (Kind.Line, Kind.Close):
            {
                Vector2 prev = PreviousVertex(path, index), next = SubpathStart(path, index);
                return BisectorAngle(prev, a.P, a.P, next);
            }

            case (Kind.Move or Kind.Cubic, Kind.Close):
                return LineAngle(PreviousVertex(path, index), SubpathStart(path, index));
            default:
                return 0;
        }
    }

    private static bool Near(Vector2 a, Vector2 b) => MathF.Abs(a.X - b.X) <= 1e-5f * Math.Max(1, MathF.Abs(a.X)) && MathF.Abs(a.Y - b.Y) <= 1e-5f * Math.Max(1, MathF.Abs(a.Y));

    private static float LineAngle(Vector2 a, Vector2 b) => BisectorAngle(a, b, a, b);

    private static float CurvesAngle(Vector2 previous, Vector2 control1, Vector2 vertex, Vector2 control2, Vector2 next)
    {
        if (Near(control1, vertex))
        {
            return BisectorAngle(previous, vertex, vertex, control2);
        }

        if (Near(vertex, control2))
        {
            return BisectorAngle(control1, vertex, vertex, next);
        }

        return BisectorAngle(control1, vertex, vertex, control2);
    }

    private static float BisectorAngle(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
    {
        float incoming = Direction(a2 - a1);
        float outgoing = Direction(b2 - b1);
        float half = (outgoing - incoming) / 2;
        float angle = incoming + half;
        if (MathF.Abs(half) > MathF.PI / 2)
        {
            angle -= MathF.PI;
        }

        return Normalize(angle) * 180 / MathF.PI;
    }

    private static float Direction(Vector2 v)
    {
        float a = MathF.Atan2(v.Y, v.X);
        return float.IsNaN(a) ? 0 : Normalize(a);
    }

    private static float Normalize(float radians)
    {
        float v = radians % (2 * MathF.PI);
        return v < 0 ? v + (2 * MathF.PI) : v;
    }

    private static Vector2 SubpathStart(List<Segment> segments, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (segments[i].Kind == Kind.Move)
            {
                return segments[i].P;
            }
        }

        return Vector2.Zero;
    }

    private static Vector2 PreviousVertex(List<Segment> segments, int index) =>
        segments[index - 1].Kind == Kind.Close ? SubpathStart(segments, index) : segments[index - 1].P;
}
