using System.Numerics;

using Egami.Drawing.Geometry;

namespace Egami.Drawing;

/// <summary>How <see cref="PathEffect.Stamp"/> places its shape along a path.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="effects"/>
/// </example>
public enum StampStyle
{
    /// <summary>The shape is moved to each point, upright.</summary>
    Translate = 0,

    /// <summary>The shape is moved to each point and turned so its x axis follows the path.</summary>
    Rotate = 1,

    /// <summary>The shape is bent along the path: its x runs along the path and its y away from it, so it follows curves.</summary>
    Morph = 2,
}

/// <summary>
/// Turns one path into another before it is filled or stroked: dashes, rounded corners, trimming, jitter, shapes stamped along the
/// path, hatching. Effects are immutable, so one can be shared and reused; <see cref="Then"/> and <see cref="Plus"/> combine them.
/// Apply one with <see cref="Path.Apply"/>, or set it on <see cref="Stroke.Effect"/> to have the canvas apply it before stroking.
/// The set is Skia's: dash, corner, trim, discrete, 1D path, 2D line and 2D path effects, composed and summed.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="effects"/>
/// </example>
public abstract record class PathEffect
{
    /// <summary>More pieces than this from one effect on one path leave the path as it was, as Skia's dashes do.</summary>
    private const int MaxPieces = 1_000_000;

    private protected PathEffect()
    {
    }

    /// <summary>Returns an effect that cuts a path into dashes, for paths that are filled or shapes that are not stroked; a stroke's own <see cref="Stroke.Dash"/> is better for strokes, since vector backends keep it as data.</summary>
    /// <param name="pattern">The dashes.</param>
    /// <returns>The effect.</returns>
    public static PathEffect Dash(DashPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new DashEffect(pattern);
    }

    /// <summary>
    /// Returns an effect that rounds every corner between two straight lines with a quadratic curve that starts and ends
    /// <paramref name="radius"/> from the corner (less on lines shorter than twice that), as Skia's corner effect does. Corners next to
    /// curves stay sharp.
    /// </summary>
    /// <param name="radius">How far from each corner the rounding starts; 0 leaves the path as it is.</param>
    /// <returns>The effect.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite.</exception>
    public static PathEffect Corner(float radius)
    {
        if (!(radius >= 0 && float.IsFinite(radius)))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be finite and not negative.");
        }

        return new CornerEffect(radius);
    }

    /// <summary>
    /// Returns an effect that keeps the part of a path between two fractions of its total length, all contours counted in order,
    /// as Skia's trim effect does: animate the end from 0 to 1 and the path draws itself.
    /// </summary>
    /// <param name="start">Where the kept part starts, from 0 to 1.</param>
    /// <param name="end">Where it ends, from 0 to 1; nothing is kept when it is not after <paramref name="start"/>.</param>
    /// <param name="inverted">Whether to keep the rest instead.</param>
    /// <returns>The effect.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> or <paramref name="end"/> is outside 0 to 1.</exception>
    public static PathEffect Trim(float start, float end, bool inverted = false)
    {
        if (!(start >= 0 && start <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start must be from 0 to 1.");
        }

        if (!(end >= 0 && end <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "The end must be from 0 to 1.");
        }

        return new TrimEffect(start, end, inverted);
    }

    /// <summary>
    /// Returns an effect that cuts each contour into straight pieces about <paramref name="segmentLength"/> long and moves their
    /// ends across the path by up to <paramref name="deviation"/>, for a hand-drawn look, as Skia's discrete effect does. The moves
    /// are a hash of the seed, the contour and the point, so the same path gives the same result on every thread and machine.
    /// </summary>
    /// <param name="segmentLength">The length of the pieces.</param>
    /// <param name="deviation">The largest move, either way.</param>
    /// <param name="seed">The seed; another seed gives other moves.</param>
    /// <returns>The effect.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentLength"/> is not positive, or a value is not finite.</exception>
    public static PathEffect Jitter(float segmentLength, float deviation, ulong seed = 0)
    {
        if (!(segmentLength > 0 && float.IsFinite(segmentLength)))
        {
            throw new ArgumentOutOfRangeException(nameof(segmentLength), segmentLength, "The segment length must be positive and finite.");
        }

        if (!float.IsFinite(deviation))
        {
            throw new ArgumentOutOfRangeException(nameof(deviation), deviation, "The deviation must be finite.");
        }

        return new JitterEffect(segmentLength, deviation, seed);
    }

    /// <summary>
    /// Returns an effect that repeats a shape along each contour, every <paramref name="advance"/> units, as Skia's 1D path effect
    /// does. The result is the shapes, filled by the shape's fill rule.
    /// </summary>
    /// <param name="shape">The shape, around its own origin, which goes on the path.</param>
    /// <param name="advance">The distance between copies.</param>
    /// <param name="phase">
    /// How far into the repeat each contour starts, as a dash pattern's phase is (and as Skia and PostScript read it): the first
    /// copy goes at the advance less the phase, modulo the advance.
    /// </param>
    /// <param name="style">How each copy is placed.</param>
    /// <returns>The effect.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="advance"/> is not positive, or a value is not finite or defined.</exception>
    public static PathEffect Stamp(Path shape, float advance, float phase = 0, StampStyle style = StampStyle.Translate)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!(advance > 0 && float.IsFinite(advance)))
        {
            throw new ArgumentOutOfRangeException(nameof(advance), advance, "The advance must be positive and finite.");
        }

        if (!float.IsFinite(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "The phase must be finite.");
        }

        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style, null);
        }

        return new StampEffect(shape, advance, phase, style);
    }

    /// <summary>
    /// Returns an effect that hatches the area a path covers with parallel lines, as Skia's 2D line effect does: the lines are the
    /// rows through the centers of a lattice's cells (y = ½, 1½ and so on, and below), mapped into the path's space by <paramref name="lattice"/>, cut where they leave the
    /// area by its fill rule. With a width, each line is outlined to it, so the result is filled; with width 0 the lines are left
    /// open, for a stroke to draw.
    /// </summary>
    /// <param name="lattice">The transform from lattice space, where rows are a unit apart, into the path's space: a scale sets the spacing, a rotation the angle.</param>
    /// <param name="width">The width of the lines, or 0 for open lines.</param>
    /// <returns>The effect.</returns>
    /// <exception cref="ArgumentException"><paramref name="lattice"/> cannot be inverted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is negative or not finite.</exception>
    public static PathEffect Hatch(Matrix3x2 lattice, float width = 0)
    {
        if (!Matrix3x2.Invert(lattice, out Matrix3x2 inverse) || !Finite(inverse))
        {
            throw new ArgumentException("The lattice must be an invertible transform.", nameof(lattice));
        }

        if (!(width >= 0 && float.IsFinite(width)))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be finite and not negative.");
        }

        return new HatchEffect(lattice, inverse, width);
    }

    /// <summary>
    /// Returns an effect that stamps a shape at every point of a lattice inside the area a path covers, as Skia's 2D path effect
    /// does. The result is the shapes, filled by the shape's fill rule.
    /// </summary>
    /// <param name="lattice">The transform from lattice space, where the points are the centers of the unit cells (x and y at a half), into the path's space.</param>
    /// <param name="shape">The shape, around its own origin, placed at each point.</param>
    /// <returns>The effect.</returns>
    /// <exception cref="ArgumentException"><paramref name="lattice"/> cannot be inverted.</exception>
    public static PathEffect HatchPath(Matrix3x2 lattice, Path shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!Matrix3x2.Invert(lattice, out Matrix3x2 inverse) || !Finite(inverse))
        {
            throw new ArgumentException("The lattice must be an invertible transform.", nameof(lattice));
        }

        return new HatchPathEffect(lattice, inverse, shape);
    }

    /// <summary>Returns an effect that applies this one and then <paramref name="next"/> to its result, as Skia's compose effect does.</summary>
    /// <param name="next">The effect applied second.</param>
    /// <returns>The combined effect.</returns>
    public PathEffect Then(PathEffect next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new ComposeEffect(this, next);
    }

    /// <summary>Returns an effect that draws the results of this one and of <paramref name="other"/> together, as Skia's sum effect does.</summary>
    /// <param name="other">The other effect.</param>
    /// <returns>The combined effect.</returns>
    public PathEffect Plus(PathEffect other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new SumEffect(this, other);
    }

    /// <summary>Returns the path the effect makes of <paramref name="path"/>.</summary>
    internal abstract Path Apply(Path path);

    private static bool Finite(Matrix3x2 m) => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);

    /// <summary>A uniform number in [−1, 1) for a key, through SplitMix64's mixer, as 2.5.4's noise draws.</summary>
    private static double Signed(ulong seed, int contour, int index)
    {
        ulong key = Mix(Mix(Mix(seed + 0x9E3779B97F4A7C15UL) ^ (ulong)contour) + (0x9E3779B97F4A7C15UL * (ulong)(index + 1)));
        return ((key >> 11) * (2.0 / (1UL << 53))) - 1;
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private sealed record DashEffect(DashPattern Pattern) : PathEffect
    {
        internal override Path Apply(Path path) => Pattern.Apply(path);
    }

    private sealed record CornerEffect(float Radius) : PathEffect
    {
        internal override Path Apply(Path path)
        {
            if (Radius == 0)
            {
                return path;
            }

            PathBuilder builder = new();
            Segment.ReadContours(path, false, (segments, start, closed, _) => Round(segments, start, closed, Radius, builder));
            return builder.ToPathUnchecked(path.FillRule);
        }

        private static void Round(List<Segment> all, Vector2 start, bool closed, double radius, PathBuilder builder)
        {
            List<Segment> segments = [];
            foreach (Segment s in all)
            {
                if (s.Kind != PathVerb.Line || s.P0 != s.P1)
                {
                    segments.Add(s);
                }
            }

            int n = segments.Count;
            if (n == 0)
            {
                builder.MoveTo(start);
                if (closed)
                {
                    builder.Close();
                }

                return;
            }

            // A vertex v sits at the start of segment v; it is rounded when both segments meeting there are lines that turn.
            bool[] rounded = new bool[n + 1];
            for (int v = closed ? 0 : 1; v < n; v++)
            {
                Segment before = segments[(v + n - 1) % n], after = segments[v];
                if (before.Kind == PathVerb.Line && after.Kind == PathVerb.Line)
                {
                    D2 a = (before.P1 - before.P0).Normalized(), b = (after.P1 - after.P0).Normalized();
                    rounded[v] = !(Math.Abs(D2.Cross(a, b)) < 1e-9 && D2.Dot(a, b) > 0);
                }
            }

            rounded[n] = closed && rounded[0];

            D2 Cut(int i, bool atStart)
            {
                Segment s = segments[i];
                double length = D2.Distance(s.P0, s.P1);
                double cut = Math.Min(radius, length / 2);
                return atStart ? D2.Lerp(s.P0, s.P1, cut / length) : D2.Lerp(s.P1, s.P0, cut / length);
            }

            D2 first = rounded[0] ? Cut(0, true) : segments[0].P0;
            builder.MoveTo(first.ToVector2());
            for (int i = 0; i < n; i++)
            {
                Segment s = segments[i];
                if (s.Kind == PathVerb.Line)
                {
                    builder.LineTo((rounded[i + 1] ? Cut(i, false) : s.P1).ToVector2());
                }
                else
                {
                    s.AddTo(builder);
                }

                if (rounded[i + 1])
                {
                    D2 next = i + 1 < n ? Cut(i + 1, true) : first;
                    builder.QuadTo(s.End.ToVector2(), next.ToVector2());
                }
            }

            if (closed)
            {
                builder.Close();
            }
        }
    }

    private sealed record TrimEffect(float Start, float End, bool Inverted) : PathEffect
    {
        internal override Path Apply(Path path)
        {
            if (Start == 0 && End == 1)
            {
                return Inverted ? Path.Empty.WithFillRule(path.FillRule) : path;
            }

            PathMeasure measure = new(path);
            double total = 0;
            foreach (ContourMeasure c in measure.Contours)
            {
                total += c.ExactLength;
            }

            double a = Start * total, b = End * total;
            PathBuilder builder = new();
            double offset = 0;
            foreach (ContourMeasure c in measure.Contours)
            {
                double length = c.ExactLength;
                if (!Inverted)
                {
                    c.AddSegment(builder, a - offset, b - offset, startWithMove: true);
                }
                else if (b <= a)
                {
                    c.AddSegment(builder, 0, length, startWithMove: true);
                }
                else
                {
                    c.AddSegment(builder, 0, a - offset, startWithMove: true);
                    c.AddSegment(builder, b - offset, length, startWithMove: true);
                }

                offset += length;
            }

            return builder.ToPathUnchecked(path.FillRule);
        }
    }

    private sealed record JitterEffect(float SegmentLength, float Deviation, ulong Seed) : PathEffect
    {
        internal override Path Apply(Path path)
        {
            PathMeasure measure = new(path);
            double pieces = 0;
            foreach (ContourMeasure c in measure.Contours)
            {
                pieces += Math.Round(c.ExactLength / SegmentLength);
            }

            if (pieces > MaxPieces)
            {
                return path;
            }

            PathBuilder builder = new();
            for (int k = 0; k < measure.Contours.Count; k++)
            {
                ContourMeasure c = measure.Contours[k];
                int n = Math.Max(1, (int)Math.Round(c.ExactLength / SegmentLength));
                if (c.IsClosed && n < 3)
                {
                    n = 3;
                }

                int last = c.IsClosed ? n - 1 : n;
                for (int i = 0; i <= last; i++)
                {
                    (int s, double t) = c.Locate(c.ExactLength * i / n);
                    Segment segment = c.SegmentAt(s);
                    D2 tangent = segment.Tangent(t);
                    D2 point = segment.Point(t) + (Deviation * Signed(Seed, k, i) * new D2(-tangent.Y, tangent.X));
                    if (i == 0)
                    {
                        builder.MoveTo(point.ToVector2());
                    }
                    else
                    {
                        builder.LineTo(point.ToVector2());
                    }
                }

                if (c.IsClosed)
                {
                    builder.Close();
                }
            }

            return builder.ToPathUnchecked(path.FillRule);
        }
    }

    private sealed record StampEffect(Path Shape, float Advance, float Phase, StampStyle Style) : PathEffect
    {
        internal override Path Apply(Path path)
        {
            PathMeasure measure = new(path);
            // The phase is an offset into the repeat, so the first copy lies that much less than an advance along.
            double phase = -Phase % Advance;
            if (phase < 0)
            {
                phase += Advance;
            }

            double copies = 0;
            foreach (ContourMeasure c in measure.Contours)
            {
                copies += Math.Ceiling((c.ExactLength - phase) / Advance);
            }

            if (copies * Math.Max(1, Shape.Points.Length) > MaxPieces * 10.0)
            {
                return path;
            }

            PathBuilder builder = new();
            foreach (ContourMeasure c in measure.Contours)
            {
                for (double d = phase; d <= c.ExactLength; d += Advance)
                {
                    switch (Style)
                    {
                        case StampStyle.Translate:
                            builder.AddPath(Shape, Matrix3x2.CreateTranslation(c.GetPosition((float)d)));
                            break;
                        case StampStyle.Rotate:
                            builder.AddPath(Shape, c.GetMatrix((float)d));
                            break;
                        default:
                            Morph(c, d, builder);
                            break;
                    }
                }
            }

            return builder.ToPathUnchecked(Shape.FillRule);
        }

        /// <summary>Bends the shape along the contour: each point's x is a distance along it from <paramref name="at"/> and its y a distance across.</summary>
        private void Morph(ContourMeasure c, double at, PathBuilder builder)
        {
            Vector2 Map(D2 p)
            {
                (int s, double t) = c.Locate(at + p.X);
                Segment segment = c.SegmentAt(s);
                D2 tangent = segment.Tangent(t);
                return (segment.Point(t) + (p.Y * new D2(-tangent.Y, tangent.X))).ToVector2();
            }

            Segment.ReadContours(Shape, false, (segments, start, closed, closingLine) =>
            {
                builder.MoveTo(Map(start));
                int count = closingLine ? segments.Count - 1 : segments.Count;
                for (int i = 0; i < count; i++)
                {
                    // Each piece is split in four so the bend follows the path; control points are mapped like points.
                    for (int k = 0; k < 4; k++)
                    {
                        Segment piece = segments[i].Sub(k / 4.0, (k + 1) / 4.0);
                        switch (piece.Kind)
                        {
                            case PathVerb.Line:
                                builder.LineTo(Map(piece.P1));
                                break;
                            case PathVerb.Quad:
                                builder.QuadTo(Map(piece.P1), Map(piece.P2));
                                break;
                            default:
                                builder.CubicTo(Map(piece.P1), Map(piece.P2), Map(piece.P3));
                                break;
                        }
                    }
                }

                if (closed)
                {
                    builder.Close();
                }
            });
        }
    }

    private sealed record HatchEffect(Matrix3x2 Lattice, Matrix3x2 Inverse, float Width) : PathEffect
    {
        internal override Path Apply(Path path)
        {
            if (path.IsEmpty)
            {
                return Path.Empty;
            }

            // The outline as straight edges in lattice space, flattened finely in the path's own space.
            List<(D2 A, D2 B)> edges = Scanlines.Edges(path, Inverse);
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach ((D2 a, D2 b) in edges)
            {
                minY = Math.Min(minY, Math.Min(a.Y, b.Y));
                maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
            }

            // Rows through the cells' centers, as Skia's lattice has them.
            double first = Math.Ceiling(minY - 0.5) + 0.5;
            double rows = Math.Floor(maxY - 0.5) + 0.5 - first + 1;
            if (edges.Count == 0 || !(rows > 0))
            {
                return Path.Empty;
            }

            if (rows * Math.Max(1, edges.Count / 16.0) > MaxPieces * 10.0)
            {
                return path;
            }

            PathBuilder lines = new();
            List<(double X, int Winding)> crossings = [];
            for (double y = first; y <= maxY; y++)
            {
                foreach ((double x0, double x1) in Scanlines.Spans(edges, y, path.FillRule, crossings))
                {
                    lines.MoveTo(Vector2.Transform(new Vector2((float)x0, (float)y), Lattice));
                    lines.LineTo(Vector2.Transform(new Vector2((float)x1, (float)y), Lattice));
                }
            }

            Path open = lines.ToPathUnchecked();
            return Width > 0 ? Stroker.Outline(open, new Stroke(Width), Width * 1e-3f) : open;
        }
    }

    private sealed record HatchPathEffect(Matrix3x2 Lattice, Matrix3x2 Inverse, Path Shape) : PathEffect
    {
        internal override Path Apply(Path path)
        {
            if (path.IsEmpty)
            {
                return Path.Empty;
            }

            RectF bounds = path.TightBounds;
            Vector2[] corners =
            [
                Vector2.Transform(new Vector2(bounds.Left, bounds.Top), Inverse),
                Vector2.Transform(new Vector2(bounds.Right, bounds.Top), Inverse),
                Vector2.Transform(new Vector2(bounds.Left, bounds.Bottom), Inverse),
                Vector2.Transform(new Vector2(bounds.Right, bounds.Bottom), Inverse),
            ];
            RectF box = RectF.FromPoints(corners);
            // The cells' centers, as Skia's lattice has them.
            float left = MathF.Ceiling(box.Left - 0.5f) + 0.5f, top = MathF.Ceiling(box.Top - 0.5f) + 0.5f;
            double columns = Math.Floor(box.Right - left) + 1, rows = Math.Floor(box.Bottom - top) + 1;
            if (!(columns > 0 && rows > 0))
            {
                return Path.Empty;
            }

            if (columns * rows > MaxPieces)
            {
                return path;
            }

            PathBuilder builder = new();
            for (float j = top; j <= box.Bottom; j++)
            {
                for (float i = left; i <= box.Right; i++)
                {
                    Vector2 point = Vector2.Transform(new Vector2(i, j), Lattice);
                    if (path.Contains(point))
                    {
                        builder.AddPath(Shape, Matrix3x2.CreateTranslation(point));
                    }
                }
            }

            return builder.ToPathUnchecked(Shape.FillRule);
        }
    }

    private sealed record ComposeEffect(PathEffect First, PathEffect Second) : PathEffect
    {
        internal override Path Apply(Path path) => Second.Apply(First.Apply(path));
    }

    private sealed record SumEffect(PathEffect First, PathEffect Second) : PathEffect
    {
        internal override Path Apply(Path path)
        {
            Path a = First.Apply(path), b = Second.Apply(path);
            return new PathBuilder().AddPath(a).AddPath(b).ToPathUnchecked(a.FillRule);
        }
    }
}
