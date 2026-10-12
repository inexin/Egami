namespace Egami.Drawing.Geometry;

/// <summary>
/// Boolean operations on the curves themselves. Every input's outline is cut into pieces monotone in x and y (so no piece crosses
/// itself, its bounds are the bounds of its ends, and a horizontal line meets it at most once), and every pair of pieces is
/// intersected: a line against a line or a curve exactly, as the roots of the curve's distance from the line; a curve against a
/// curve by subdividing both while their bounds overlap, until the pieces are straight enough that their chords either cross
/// clearly (refined by Newton steps on the curves in double precision), stay apart, or run together. Ends of one piece near
/// another, and pieces running together, split the other there. Pieces are cut at all of these into edges whose ends merge
/// within a tenth of the tolerance; merged points split the edges that pass within that distance (snap rounding), merging can
/// bend an edge into a new crossing, which is found and cut in turn, and edges that then join the same two points along the same
/// curve, within the tolerance, merge into one that counts how many times each input runs along it, each way. The edges and the
/// order they leave each point make a planar map whose faces get each input's winding number: from a ray to the left of each
/// connected piece for its outside, then across each edge by its counts, so the windings agree by construction however close
/// edges come. Each input's fill rule says whether a face is inside it, the operation whether it is in the result; edges with the
/// result on one side only are kept, turned to have it on their right, and joined into contours, turning right as far as possible
/// where several meet, and consecutive pieces of one input curve are joined back into it. So results keep their lines,
/// quadratics and cubics, outer contours run clockwise on screen and holes counterclockwise, and no contours cross. Written from
/// the planar-arrangement and snap-rounding literature and Sederberg and Nishita's Bézier clipping (subdivision with bounds and
/// flatness tests here, rather than fat-line clipping); no code from Skia or the reference library.
/// </summary>
internal static class PathBoolean
{
    // Gauss–Legendre nodes and weights on [−1, 1], five points: exact for polynomials up to degree 9.
    private static readonly double[] Nodes = [0, -0.5384693101056831, 0.5384693101056831, -0.9061798459386640, 0.9061798459386640];
    private static readonly double[] Weights = [0.5688888888888889, 0.4786286704993665, 0.4786286704993665, 0.2369268850561891, 0.2369268850561891];

    /// <summary>Runs <paramref name="op"/> over the inputs in order, left to right: ((a op b) op c) and so on.</summary>
    internal static Path Run(IReadOnlyList<Path> inputs, PathOp op, double tolerance)
    {
        Engine engine = new(inputs, op, tolerance);
        return engine.Run();
    }

    /// <summary>Returns whether the region is in the result, given whether it is inside each input.</summary>
    private static bool Combine(PathOp op, ReadOnlySpan<bool> inside)
    {
        bool result = inside[0];
        for (int k = 1; k < inside.Length; k++)
        {
            bool b = inside[k];
            result = op switch
            {
                PathOp.Union => result || b,
                PathOp.Intersect => result && b,
                PathOp.Difference => result && !b,
                PathOp.ReverseDifference => b && !result,
                _ => result ^ b,
            };
        }

        return result;
    }

    /// <summary>A monotone piece of one input curve, between two parameters of that curve.</summary>
    private sealed class Piece
    {
        public required Segment Curve { get; init; }

        public required int Input { get; init; }

        public required int Source { get; init; }

        public required double T0 { get; init; }

        public required double T1 { get; init; }

        public List<double> Splits { get; } = [];

        public (D2 Min, D2 Max) Box { get; init; }
    }

    /// <summary>An edge of the arrangement: a piece of an input curve between two merged points.</summary>
    private sealed class Edge
    {
        public required Segment Curve { get; set; }

        public required int Source { get; init; }

        // The source curve's parameters at the edge's start and end, in the edge's direction.
        public required double T0 { get; init; }

        public required double T1 { get; init; }

        public int V0 { get; set; }

        public int V1 { get; set; }

        // How many times each input runs along the edge, forwards minus backwards.
        public List<(int Input, int Count)> Runs { get; } = [];

        public bool Dead { get; set; }

        // Whether merging bent the edge since it was last intersected with the others.
        public bool Moved { get; set; }

        public List<double> Cuts { get; } = [];

        public bool Kept { get; set; }

        public bool Reverse { get; set; }

        public bool Used { get; set; }
    }

    private sealed class Engine
    {
        private readonly IReadOnlyList<Path> _inputs;
        private readonly PathOp _op;
        private readonly double _tolerance;

        // How close ends and crossings must be to become one point: a tenth of the tolerance, so merging moves no edge far enough
        // to change the area noticeably, while edges that run within the tolerance of each other between shared points still merge.
        private readonly double _merge;
        private readonly List<Piece> _pieces = [];
        private readonly List<Segment> _sources = [];
        private readonly List<Edge> _edges = [];
        private readonly List<D2> _vertices = [];
        private readonly Dictionary<(long, long), List<int>> _grid = [];
        private readonly List<(double A, double B)> _found = [];
        private List<Edge> _live = [];
        private List<int>?[] _around = [];
        private int[] _position = [];

        internal Engine(IReadOnlyList<Path> inputs, PathOp op, double tolerance)
        {
            _inputs = inputs;
            _op = op;
            _tolerance = tolerance;
            _merge = tolerance * 0.1;
        }

        internal Path Run()
        {
            for (int k = 0; k < _inputs.Count; k++)
            {
                AddInput(k);
            }

            if (_pieces.Count == 0)
            {
                return Path.Empty;
            }

            Intersect();
            BuildEdges();
            MergeVertices();
            for (int round = 0; round < 16; round++)
            {
                SplitAtVertices();
                MergeCoincidentEdges();
                if (!Reintersect())
                {
                    break;
                }

            }


            Classify();
            return Trace();
        }

        private void AddInput(int input)
        {
            double[] cuts = new double[6];
            Segment.ReadContours(_inputs[input], true, (segments, _, _, _) =>
            {
                foreach (Segment raw in segments)
                {
                    Segment s = Straighten(raw);
                    double size = Math.Max(s.Hull(), 1);
                    (D2 min, D2 max) = s.ControlBounds();
                    if (Math.Max(max.X - min.X, max.Y - min.Y) <= size * 1e-12)
                    {
                        continue;
                    }

                    int source = _sources.Count;
                    _sources.Add(s);
                    int n = MonotoneCuts(s, cuts);
                    double previous = 0;
                    for (int i = 0; i <= n; i++)
                    {
                        double t = i < n ? cuts[i] : 1;
                        if (t > previous)
                        {
                            Segment curve = s.Sub(previous, t);
                            _pieces.Add(new Piece { Curve = curve, Input = input, Source = source, T0 = previous, T1 = t, Box = curve.ControlBounds() });
                        }

                        previous = t;
                    }
                }
            });
        }

        /// <summary>A curve whose control points lie on its chord, in order, is the line it draws.</summary>
        private static Segment Straighten(Segment s)
        {
            if (s.Kind == PathVerb.Line)
            {
                return s;
            }

            double size = Math.Max(s.Hull(), 1e-300);
            D2 chord = s.End - s.P0;
            double length = chord.Length;
            if (length <= size * 1e-12)
            {
                return s;
            }

            bool OnChord(D2 p)
            {
                double along = D2.Dot(p - s.P0, chord) / (length * length);
                return Math.Abs(D2.Cross(chord, p - s.P0)) / length <= size * 1e-12 && along >= 0 && along <= 1;
            }

            // Collinear control points in order still move non-uniformly, but the set of points drawn is the chord's.
            return OnChord(s.P1) && (s.Kind == PathVerb.Quad || OnChord(s.P2)) ? Segment.Line(s.P0, s.End) : s;
        }

        /// <summary>Writes the sorted parameters inside (0, 1) where x or y turns; returns the count.</summary>
        private static int MonotoneCuts(Segment s, Span<double> cuts)
        {
            int n = 0;
            if (s.Kind == PathVerb.Quad)
            {
                n = QuadRoot(s.P0.X, s.P1.X, s.P2.X, cuts, n);
                n = QuadRoot(s.P0.Y, s.P1.Y, s.P2.Y, cuts, n);
            }
            else if (s.Kind == PathVerb.Cubic)
            {
                n = CubicRoots(s.P0.X, s.P1.X, s.P2.X, s.P3.X, cuts, n);
                n = CubicRoots(s.P0.Y, s.P1.Y, s.P2.Y, s.P3.Y, cuts, n);
            }

            cuts[..n].Sort();
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                if (cuts[i] > 1e-9 && cuts[i] < 1 - 1e-9 && (m == 0 || cuts[i] - cuts[m - 1] > 1e-9))
                {
                    cuts[m++] = cuts[i];
                }
            }

            return m;

            static int QuadRoot(double a, double b, double c, Span<double> cuts, int n)
            {
                double d = a - (2 * b) + c;
                if (d != 0)
                {
                    double t = (a - b) / d;
                    if (t > 0 && t < 1)
                    {
                        cuts[n++] = t;
                    }
                }

                return n;
            }

            static int CubicRoots(double p0, double p1, double p2, double p3, Span<double> cuts, int n)
            {
                double a = -p0 + (3 * p1) - (3 * p2) + p3, b = 2 * (p0 - (2 * p1) + p2), c = p1 - p0;
                Span<double> roots = stackalloc double[2];
                int count = Curves.SolveQuadratic(a, b, c, roots);
                for (int i = 0; i < count; i++)
                {
                    if (roots[i] > 0 && roots[i] < 1)
                    {
                        cuts[n++] = roots[i];
                    }
                }

                return n;
            }
        }

        private static bool Overlap((D2 Min, D2 Max) a, (D2 Min, D2 Max) b, double margin)
            => a.Min.X <= b.Max.X + margin && b.Min.X <= a.Max.X + margin && a.Min.Y <= b.Max.Y + margin && b.Min.Y <= a.Max.Y + margin;

        /// <summary>Finds every crossing and touching between pairs of pieces, as split parameters on each.</summary>
        private void Intersect()
        {
            int[] order = new int[_pieces.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) => _pieces[a].Box.Min.X.CompareTo(_pieces[b].Box.Min.X));
            for (int i = 0; i < order.Length; i++)
            {
                Piece a = _pieces[order[i]];
                for (int j = i + 1; j < order.Length; j++)
                {
                    Piece b = _pieces[order[j]];
                    if (b.Box.Min.X > a.Box.Max.X + _merge)
                    {
                        break;
                    }

                    if (!Overlap(a.Box, b.Box, _merge))
                    {
                        continue;
                    }

                    // Ends of one piece on the other: T-junctions, touching points and the ends of runs along the same curve.
                    EndsOn(a, b);
                    EndsOn(b, a);
                    _found.Clear();
                    Crossings(a.Curve, b.Curve, _found);

                    foreach ((double ta, double tb) in _found)
                    {
                        AddSplit(a, ta);
                        AddSplit(b, tb);
                    }
                }
            }
        }

        private void EndsOn(Piece on, Piece of)
        {
            foreach (D2 p in (ReadOnlySpan<D2>)[of.Curve.P0, of.Curve.End])
            {
                if (!Overlap(on.Box, (p, p), _merge))
                {
                    continue;
                }

                (double t, double distance) = Project(on.Curve, p);
                if (distance <= _merge)
                {
                    AddSplit(on, t);
                }
            }
        }

        private static void AddSplit(Piece piece, double t)
        {
            if (t > 0 && t < 1)
            {
                piece.Splits.Add(t);
            }
        }

        /// <summary>
        /// Adds the parameters where two curves cross, or where one runs along the other (with NaN for the other's parameter), to
        /// <paramref name="found"/>.
        /// </summary>
        private void Crossings(Segment a, Segment b, List<(double A, double B)> found)
        {
            if (a.Kind == PathVerb.Line && b.Kind == PathVerb.Line)
            {
                LineLineParams(a.P0, a.P1, b.P0, b.P1, out double t, out double s, out _);
                if (t > 0 && t < 1 && s > 0 && s < 1)
                {
                    found.Add((t, s));
                }
            }
            else if (a.Kind == PathVerb.Line)
            {
                LineCurve(a, b, found, swap: true);
            }
            else if (b.Kind == PathVerb.Line)
            {
                LineCurve(b, a, found, swap: false);
            }
            else
            {
                Subdivide(a, b, 0, 1, 0, 1, 0, found);
            }
        }

        private static void LineLineParams(D2 a0, D2 a1, D2 b0, D2 b1, out double t, out double s, out double denominator)
        {
            D2 r = a1 - a0, q = b1 - b0;
            denominator = D2.Cross(r, q);
            D2 d = b0 - a0;
            t = denominator == 0 ? double.NaN : D2.Cross(d, q) / denominator;
            s = denominator == 0 ? double.NaN : D2.Cross(d, r) / denominator;
        }

        /// <summary>
        /// A line against a curve, exactly: the curve's signed distance from the line is a polynomial of its degree in t, whose roots
        /// are found on the intervals where it is monotone, so two crossings however close are both found.
        /// </summary>
        private static void LineCurve(Segment line, Segment curve, List<(double A, double B)> found, bool swap)
        {
            Span<double> roots = stackalloc double[3];
            int n = LineRoots(curve, line.P0, line.P1, roots);
            D2 r = line.P1 - line.P0;
            for (int i = 0; i < n; i++)
            {
                double t = roots[i];
                double s = D2.Dot(curve.Point(t) - line.P0, r) / r.LengthSquared;
                if (t > 0 && t < 1 && s > 0 && s < 1)
                {
                    found.Add(swap ? (s, t) : (t, s));
                }
            }
        }

        /// <summary>Writes the parameters in [0, 1] where a curve crosses the infinite line through two points; returns the count.</summary>
        private static int LineRoots(Segment c, D2 p0, D2 p1, Span<double> roots)
        {
            D2 normal = new(-(p1.Y - p0.Y), p1.X - p0.X);
            double length = normal.Length;
            if (length == 0)
            {
                return 0;
            }

            normal /= length;

            // The signed distances of the control points, then the power basis of the distance polynomial.
            double d0 = D2.Dot(c.P0 - p0, normal), d1 = D2.Dot(c.P1 - p0, normal), d2 = D2.Dot(c.P2 - p0, normal), d3 = D2.Dot(c.P3 - p0, normal);
            Span<double> k = stackalloc double[4];
            switch (c.Kind)
            {
                case PathVerb.Line:
                    k[0] = d0;
                    k[1] = d1 - d0;
                    k[2] = k[3] = 0;
                    break;
                case PathVerb.Quad:
                    k[0] = d0;
                    k[1] = 2 * (d1 - d0);
                    k[2] = d0 - (2 * d1) + d2;
                    k[3] = 0;
                    break;
                default:
                    k[0] = d0;
                    k[1] = 3 * (d1 - d0);
                    k[2] = 3 * (d0 - (2 * d1) + d2);
                    k[3] = -d0 + (3 * d1) - (3 * d2) + d3;
                    break;
            }

            return PolynomialRoots(k, roots);
        }

        /// <summary>
        /// The roots in [0, 1] of k0 + k1·t + k2·t² + k3·t³: the interval is cut where the derivative is 0, and each piece where the
        /// sign changes is bisected to double precision.
        /// </summary>
        private static int PolynomialRoots(ReadOnlySpan<double> k, Span<double> roots)
        {
            Span<double> cuts = stackalloc double[4];
            Span<double> extrema = stackalloc double[2];
            int m = 0;
            cuts[m++] = 0;
            int e = Curves.SolveQuadratic(3 * k[3], 2 * k[2], k[1], extrema);
            if (e == 2 && extrema[1] < extrema[0])
            {
                (extrema[0], extrema[1]) = (extrema[1], extrema[0]);
            }

            for (int i = 0; i < e; i++)
            {
                if (extrema[i] > 0 && extrema[i] < 1)
                {
                    cuts[m++] = extrema[i];
                }
            }

            cuts[m++] = 1;
            int n = 0;
            for (int i = 0; i + 1 < m && n < roots.Length; i++)
            {
                double lo = cuts[i], hi = cuts[i + 1];
                double flo = Evaluate(k, lo), fhi = Evaluate(k, hi);
                if (flo == 0)
                {
                    if (n == 0 || roots[n - 1] != lo)
                    {
                        roots[n++] = lo;
                    }

                    continue;
                }

                if (Math.Sign(flo) == Math.Sign(fhi) || fhi == 0)
                {
                    if (fhi == 0 && i + 2 == m)
                    {
                        roots[n++] = hi;
                    }

                    continue;
                }

                for (int iteration = 0; iteration < 64; iteration++)
                {
                    double mid = (lo + hi) / 2;
                    if (mid <= lo || mid >= hi)
                    {
                        break;
                    }

                    double f = Evaluate(k, mid);
                    if (Math.Sign(f) == Math.Sign(flo))
                    {
                        lo = mid;
                        flo = f;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                roots[n++] = (lo + hi) / 2;
            }

            return n;

            static double Evaluate(ReadOnlySpan<double> k, double t) => k[0] + (t * (k[1] + (t * (k[2] + (t * k[3])))));
        }

        /// <summary>
        /// A curve against a curve: both are cut while their bounds overlap. Once both pieces are straight to within a twentieth of the
        /// tolerance, their chords tell: apart by more than the pieces bend, they cannot meet; running together (each end near the
        /// other's chord line), each end splits the other curve where it projects, so the run becomes shared edges; crossing at a
        /// clear angle, the chords' crossing is refined by Newton steps on the curves; otherwise both are cut again, and bend less.
        /// </summary>
        private void Subdivide(Segment a, Segment b, double a0, double a1, double b0, double b1, int depth, List<(double A, double B)> found)
        {
            Segment sa = a.Sub(a0, a1), sb = b.Sub(b0, b1);
            if (!Overlap(sa.ControlBounds(), sb.ControlBounds(), 0))
            {
                return;
            }

            double flat = _tolerance * 0.05;
            double bendA = Bend(sa), bendB = Bend(sb);
            double sizeA = D2.Distance(sa.P0, sa.End), sizeB = D2.Distance(sb.P0, sb.End);

            if (bendA <= flat && bendB <= flat)
            {
                if (ChordGap(sa, sb) > bendA + bendB)
                {
                    return;
                }

                if (Together(sa, sb) && Math.Max(sizeA, sizeB) > 4 * _merge && depth < 50)
                {
                    // Running together over a stretch, they may still cross more than once: cut both until any two crossings in a
                    // pair of pieces are close enough to merge.
                    double ha = (a0 + a1) / 2, hb = (b0 + b1) / 2;
                    Subdivide(a, b, a0, ha, b0, hb, depth + 1, found);
                    Subdivide(a, b, a0, ha, hb, b1, depth + 1, found);
                    Subdivide(a, b, ha, a1, b0, hb, depth + 1, found);
                    Subdivide(a, b, ha, a1, hb, b1, depth + 1, found);
                    return;
                }

                if (Together(sa, sb))
                {
                    foreach (D2 p in (ReadOnlySpan<D2>)[sb.P0, sb.End])
                    {
                        (double t, double d) = Project(a, p);
                        if (d <= _merge)
                        {
                            found.Add((t, double.NaN));
                        }
                    }

                    foreach (D2 p in (ReadOnlySpan<D2>)[sa.P0, sa.End])
                    {
                        (double t, double d) = Project(b, p);
                        if (d <= _merge)
                        {
                            found.Add((double.NaN, t));
                        }
                    }

                    // Running together, they may still cross: where the chords do.
                    LineLineParams(sa.P0, sa.End, sb.P0, sb.End, out double cu, out double cv, out _);
                    if (cu >= 0 && cu <= 1 && cv >= 0 && cv <= 1)
                    {
                        double ta = a0 + ((a1 - a0) * cu), tb = b0 + ((b1 - b0) * cv);
                        (ta, tb) = Refine(a, b, ta, tb);
                        if (ta > 0 && ta < 1 && tb > 0 && tb < 1 && D2.Distance(a.Point(ta), b.Point(tb)) <= _merge * 0.1)
                        {
                            found.Add((ta, tb));
                        }
                    }

                    return;
                }

                LineLineParams(sa.P0, sa.End, sb.P0, sb.End, out double u, out double v, out double denominator);
                double sine = sizeA > 0 && sizeB > 0 ? Math.Abs(denominator) / (sizeA * sizeB) : 0;
                if (sine >= 0.05 || depth >= 50 || Math.Max(sizeA, sizeB) <= _merge * 0.01)
                {
                    // The curves lie within their bends of the chords, so they cross within that much over the sine of where the
                    // chords do.
                    double margin = sine > 0 ? (bendA + bendB) / (sine * Math.Max(1e-300, Math.Min(sizeA, sizeB))) : 0;
                    margin = Math.Min(Math.Max(margin, 1e-6), 0.5);
                    if (double.IsFinite(u) && u >= -margin && u <= 1 + margin && v >= -margin && v <= 1 + margin)
                    {
                        double ta = a0 + ((a1 - a0) * Math.Clamp(u, 0, 1)), tb = b0 + ((b1 - b0) * Math.Clamp(v, 0, 1));
                        (ta, tb) = Refine(a, b, ta, tb);
                        if (ta >= a0 - (a1 - a0) && ta <= a1 + (a1 - a0) && tb >= b0 - (b1 - b0) && tb <= b1 + (b1 - b0) && ta > 0 && ta < 1 && tb > 0 && tb < 1 && D2.Distance(a.Point(ta), b.Point(tb)) <= _merge * 0.1)
                        {
                            found.Add((ta, tb));
                        }
                    }

                    return;
                }

                // Nearly parallel and close, but not together: cut both, which straightens them, until they part, cross clearly or meet.
                double ma = (a0 + a1) / 2, mb = (b0 + b1) / 2;
                Subdivide(a, b, a0, ma, b0, mb, depth + 1, found);
                Subdivide(a, b, a0, ma, mb, b1, depth + 1, found);
                Subdivide(a, b, ma, a1, b0, mb, depth + 1, found);
                Subdivide(a, b, ma, a1, mb, b1, depth + 1, found);
                return;
            }

            if (depth >= 50)
            {
                return;
            }

            // Cut whichever bends more.
            if (bendA >= bendB)
            {
                double m = (a0 + a1) / 2;
                Subdivide(a, b, a0, m, b0, b1, depth + 1, found);
                Subdivide(a, b, m, a1, b0, b1, depth + 1, found);
            }
            else
            {
                double m = (b0 + b1) / 2;
                Subdivide(a, b, a0, a1, b0, m, depth + 1, found);
                Subdivide(a, b, a0, a1, m, b1, depth + 1, found);
            }
        }

        /// <summary>How far the control points stray from the chord: the curve stays within that of it.</summary>
        private static double Bend(Segment s)
        {
            if (s.Kind == PathVerb.Line)
            {
                return 0;
            }

            D2 chord = s.End - s.P0;
            double length = chord.Length;
            if (length == 0)
            {
                return Math.Max(D2.Distance(s.P1, s.P0), s.Kind == PathVerb.Cubic ? D2.Distance(s.P2, s.P0) : 0);
            }

            double bend = Math.Abs(D2.Cross(chord, s.P1 - s.P0)) / length;
            return s.Kind == PathVerb.Cubic ? Math.Max(bend, Math.Abs(D2.Cross(chord, s.P2 - s.P0)) / length) : bend;
        }

        /// <summary>The distance between two chords as segments: 0 when they cross.</summary>
        private static double ChordGap(Segment a, Segment b)
        {
            LineLineParams(a.P0, a.End, b.P0, b.End, out double t, out double s, out _);
            if (t >= 0 && t <= 1 && s >= 0 && s <= 1)
            {
                return 0;
            }

            return Math.Min(Math.Min(PointSegment(a.P0, b), PointSegment(a.End, b)), Math.Min(PointSegment(b.P0, a), PointSegment(b.End, a)));

            static double PointSegment(D2 p, Segment s)
            {
                D2 r = s.End - s.P0;
                double l2 = r.LengthSquared;
                double t = l2 > 0 ? Math.Clamp(D2.Dot(p - s.P0, r) / l2, 0, 1) : 0;
                return D2.Distance(s.P0 + (t * r), p);
            }
        }

        /// <summary>Whether two straight pieces run together: each one's ends lie within half the merge distance of the line through the other's chord.</summary>
        private bool Together(Segment a, Segment b)
        {
            double limit = _merge * 0.5;
            return LineDistance(a, b.P0) <= limit && LineDistance(a, b.End) <= limit && LineDistance(b, a.P0) <= limit && LineDistance(b, a.End) <= limit;

            static double LineDistance(Segment s, D2 p)
            {
                D2 r = s.End - s.P0;
                double length = r.Length;
                return length > 0 ? Math.Abs(D2.Cross(r, p - s.P0)) / length : D2.Distance(s.P0, p);
            }
        }

        /// <summary>Newton steps on A(t) − B(s) = 0 from a chord estimate, kept when they converge.</summary>
        private static (double T, double S) Refine(Segment a, Segment b, double t, double s)
        {
            double t0 = t, s0 = s;
            for (int i = 0; i < 24; i++)
            {
                D2 f = a.Point(t) - b.Point(s);
                D2 da = a.Derivative(t), db = b.Derivative(s);
                double det = D2.Cross(da, db);
                if (Math.Abs(det) < 1e-300)
                {
                    break;
                }

                // Solve da·Δt − db·Δs = −f.
                double dt = D2.Cross(db, f) / det, ds = D2.Cross(da, f) / det;
                t = Math.Clamp(t + dt, 0, 1);
                s = Math.Clamp(s + ds, 0, 1);
                if (Math.Abs(dt) < 1e-15 && Math.Abs(ds) < 1e-15)
                {
                    break;
                }
            }

            D2 before = a.Point(t0) - b.Point(s0), after = a.Point(t) - b.Point(s);
            return after.LengthSquared <= before.LengthSquared ? (t, s) : (t0, s0);
        }

        /// <summary>Returns the parameter on a monotone curve closest to a point, and the distance.</summary>
        private static (double T, double Distance) Project(Segment c, D2 p)
        {
            if (c.Kind == PathVerb.Line)
            {
                D2 r = c.P1 - c.P0;
                double t = Math.Clamp(D2.Dot(p - c.P0, r) / r.LengthSquared, 0, 1);
                return (t, D2.Distance(c.Point(t), p));
            }

            double best = 0, bestDistance = double.MaxValue;
            const int Samples = 16;
            for (int i = 0; i <= Samples; i++)
            {
                double t = i / (double)Samples;
                double d = (c.Point(t) - p).LengthSquared;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = t;
                }
            }

            // Newton on (C(t) − p)·C′(t) = 0, kept inside the neighboring samples.
            double lo = Math.Max(0, best - (1.0 / Samples)), hi = Math.Min(1, best + (1.0 / Samples)), u = best;
            for (int i = 0; i < 30; i++)
            {
                D2 d = c.Point(u) - p, d1 = c.Derivative(u), d2 = c.SecondDerivative(u);
                double g = D2.Dot(d, d1), h = D2.Dot(d1, d1) + D2.Dot(d, d2);
                double next = h > 0 ? u - (g / h) : (lo + hi) / 2;
                if (!(next >= lo && next <= hi))
                {
                    next = g > 0 ? (lo + u) / 2 : (u + hi) / 2;
                }

                if (g > 0)
                {
                    hi = u;
                }
                else
                {
                    lo = u;
                }

                if (Math.Abs(next - u) < 1e-15)
                {
                    u = next;
                    break;
                }

                u = next;
            }

            double distance = D2.Distance(c.Point(u), p);
            return distance * distance <= bestDistance ? (u, distance) : (best, Math.Sqrt(bestDistance));
        }

        /// <summary>Cuts every piece at its splits into edges; splits closer than half the merge distance along the piece count once.</summary>
        private void BuildEdges()
        {
            foreach (Piece piece in _pieces)
            {
                piece.Splits.Sort();
                double previous = 0;
                D2 previousPoint = piece.Curve.P0;
                foreach (double t in piece.Splits)
                {
                    D2 point = piece.Curve.Point(t);
                    if (D2.Distance(point, previousPoint) <= _merge * 0.5 || D2.Distance(point, piece.Curve.End) <= _merge * 0.5)
                    {
                        continue;
                    }

                    AddEdge(piece, previous, t);
                    previous = t;
                    previousPoint = point;
                }

                AddEdge(piece, previous, 1);
            }
        }

        private void AddEdge(Piece piece, double t0, double t1)
        {
            // The piece's own ends exactly, so pieces of one curve meet at equal parameters and join back up on output.
            double s0 = t0 == 0 ? piece.T0 : piece.T0 + ((piece.T1 - piece.T0) * t0);
            double s1 = t1 == 1 ? piece.T1 : piece.T0 + ((piece.T1 - piece.T0) * t1);
            Segment curve = t0 == 0 && t1 == 1 ? piece.Curve : _sources[piece.Source].Sub(s0, s1);
            Edge edge = new() { Curve = curve, Source = piece.Source, T0 = s0, T1 = s1 };
            edge.Runs.Add((piece.Input, 1));
            _edges.Add(edge);
        }

        /// <summary>Merges edge ends within the merge distance into shared points, moving each edge's ends onto them.</summary>
        private void MergeVertices()
        {
            foreach (Edge e in _edges)
            {
                e.V0 = Vertex(e.Curve.P0);
                e.V1 = Vertex(e.Curve.End);
                Settle(e);
            }
        }

        /// <summary>Moves an edge's ends onto its points; an edge whose ends merged and that is no longer than that dies.</summary>
        private void Settle(Edge e)
        {
            if (e.V0 == e.V1)
            {
                (D2 min, D2 max) = e.Curve.ControlBounds();
                if (Math.Max(max.X - min.X, max.Y - min.Y) <= 2 * _merge)
                {
                    e.Dead = true;
                    return;
                }
            }

            D2 p0 = _vertices[e.V0], p1 = _vertices[e.V1];
            if (p0 != e.Curve.P0 || p1 != e.Curve.End)
            {
                e.Moved |= D2.Distance(p0, e.Curve.P0) + D2.Distance(p1, e.Curve.End) > _merge * 1e-6;
                e.Curve = e.Curve.WithEnds(p0, p1);
            }
        }

        /// <summary>
        /// The point for a position: the nearest point already made within the merge distance, or a new one. Each position joins an
        /// existing point or makes its own, so merging never chains along a row of close points.
        /// </summary>
        private int Vertex(D2 p)
        {
            double cell = _merge;
            long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell);
            int found = -1;
            double best = double.MaxValue;
            for (long y = cy - 1; y <= cy + 1; y++)
            {
                for (long x = cx - 1; x <= cx + 1; x++)
                {
                    if (_grid.TryGetValue((x, y), out List<int>? list))
                    {
                        foreach (int v in list)
                        {
                            double d = D2.Distance(_vertices[v], p);
                            if (d <= _merge && d < best)
                            {
                                best = d;
                                found = v;
                            }
                        }
                    }
                }
            }

            if (found >= 0)
            {
                return found;
            }

            _vertices.Add(p);
            if (!_grid.TryGetValue((cx, cy), out List<int>? cellList))
            {
                _grid[(cx, cy)] = cellList = [];
            }

            cellList.Add(_vertices.Count - 1);
            return _vertices.Count - 1;
        }

        /// <summary>
        /// Splits every edge at the points that lie within the merge distance of it, as snap rounding does: merging moves ends a
        /// little, which can lay one edge along another that was cut at a point between, and edges must meet at the same points to
        /// be recognized as running together. Only existing points are used, so this ends after a few passes.
        /// </summary>
        private void SplitAtVertices()
        {
            // The points, in cells sized so each holds a few: edges look only in the cells their bounds cross.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (D2 p in _vertices)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }

            double cell = Math.Max(2 * _merge, Math.Max(maxX - minX, maxY - minY) / Math.Max(1, Math.Sqrt(_vertices.Count)));
            Dictionary<(long, long), List<int>> cells = [];
            for (int v = 0; v < _vertices.Count; v++)
            {
                (long, long) key = ((long)Math.Floor(_vertices[v].X / cell), (long)Math.Floor(_vertices[v].Y / cell));
                if (!cells.TryGetValue(key, out List<int>? list))
                {
                    cells[key] = list = [];
                }

                list.Add(v);
            }

            List<(double T, int Vertex)> hits = [];
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                int count = _edges.Count;
                for (int i = 0; i < count; i++)
                {
                    Edge e = _edges[i];
                    if (e.Dead)
                    {
                        continue;
                    }

                    hits.Clear();
                    (D2 min, D2 max) = e.Curve.ControlBounds();
                    long x0 = (long)Math.Floor((min.X - _merge) / cell), x1 = (long)Math.Floor((max.X + _merge) / cell);
                    long y0 = (long)Math.Floor((min.Y - _merge) / cell), y1 = (long)Math.Floor((max.Y + _merge) / cell);
                    for (long y = y0; y <= y1; y++)
                    {
                        for (long x = x0; x <= x1; x++)
                        {
                            if (cells.TryGetValue((x, y), out List<int>? list))
                            {
                                foreach (int v in list)
                                {
                                    D2 p = _vertices[v];
                                    if (v != e.V0 && v != e.V1 && p.X >= min.X - _merge && p.X <= max.X + _merge && p.Y >= min.Y - _merge && p.Y <= max.Y + _merge)
                                    {
                                        (double t, double distance) = Project(e.Curve, p);
                                        if (distance <= _merge && t > 0 && t < 1 && D2.Distance(p, e.Curve.P0) > _merge && D2.Distance(p, e.Curve.End) > _merge)
                                        {
                                            hits.Add((t, v));
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (hits.Count > 0)
                    {
                        changed = true;
                        hits.Sort((p, q) => p.T.CompareTo(q.T));
                        Cut(e, hits);
                    }
                }

                if (!changed)
                {
                    return;
                }
            }
        }

        /// <summary>Replaces an edge with its pieces between the given points, in order along it.</summary>
        private void Cut(Edge e, List<(double T, int Vertex)> cuts)
        {
            Segment curve = e.Curve;
            double previous = 0;
            int from = e.V0;
            e.Dead = true;
            foreach ((double t, int v) in cuts)
            {
                if (v == from || t <= previous)
                {
                    continue;
                }

                AddPiece(previous, t, from, v);
                previous = t;
                from = v;
            }

            if (from != e.V1)
            {
                AddPiece(previous, 1, from, e.V1);
            }

            void AddPiece(double t0, double t1, int v0, int v1)
            {
                double s0 = t0 == 0 ? e.T0 : e.T0 + ((e.T1 - e.T0) * t0), s1 = t1 == 1 ? e.T1 : e.T0 + ((e.T1 - e.T0) * t1);
                Edge piece = new() { Curve = curve.Sub(t0, t1), Source = e.Source, T0 = s0, T1 = s1, V0 = v0, V1 = v1, Moved = e.Moved };
                piece.Runs.AddRange(e.Runs);
                Settle(piece);
                if (!piece.Dead)
                {
                    _edges.Add(piece);
                }
            }
        }

        /// <summary>
        /// Finds crossings that merging made, between edges it bent and the others, and cuts both there at a new or nearby point.
        /// Returns whether any were found.
        /// </summary>
        private bool Reintersect()
        {
            List<Edge> live = _edges.FindAll(e => !e.Dead);
            if (!live.Exists(e => e.Moved))
            {
                return false;
            }

            List<(D2 Min, D2 Max)> boxes = live.ConvertAll(e => e.Curve.ControlBounds());
            int[] order = new int[live.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) => boxes[a].Min.X.CompareTo(boxes[b].Min.X));
            bool any = false;
            for (int i = 0; i < order.Length; i++)
            {
                Edge a = live[order[i]];
                for (int j = i + 1; j < order.Length; j++)
                {
                    Edge b = live[order[j]];
                    if (boxes[order[j]].Min.X > boxes[order[i]].Max.X)
                    {
                        break;
                    }

                    if ((!a.Moved && !b.Moved) || !Overlap(boxes[order[i]], boxes[order[j]], 0))
                    {
                        continue;
                    }

                    _found.Clear();
                    Crossings(a.Curve, b.Curve, _found);
                    foreach ((double ta, double tb) in _found)
                    {
                        // Only crossings: runs together were split in the first pass, and following them again would zip close
                        // edges together a step at a time.
                        if (double.IsNaN(ta) || double.IsNaN(tb))
                        {
                            continue;
                        }

                        any |= Mark(a, ta);
                        any |= Mark(b, tb);
                    }
                }
            }

            foreach (Edge e in live)
            {
                e.Moved = false;
            }

            if (!any)
            {
                return false;
            }

            List<(double T, int Vertex)> cuts = [];
            foreach (Edge e in live)
            {
                if (e.Cuts.Count == 0)
                {
                    continue;
                }

                e.Cuts.Sort();
                cuts.Clear();
                foreach (double t in e.Cuts)
                {
                    cuts.Add((t, Vertex(e.Curve.Point(t))));
                }

                e.Cuts.Clear();
                Cut(e, cuts);
            }

            return true;

            bool Mark(Edge e, double t)
            {
                if (!(t > 0 && t < 1))
                {
                    return false;
                }

                D2 p = e.Curve.Point(t);
                if (D2.Distance(p, e.Curve.P0) <= _merge || D2.Distance(p, e.Curve.End) <= _merge)
                {
                    return false;
                }

                e.Cuts.Add(t);
                return true;
            }
        }

        /// <summary>Edges between the same two points along the same curve become one, counting each input's runs along it.</summary>
        private void MergeCoincidentEdges()
        {
            Dictionary<(int, int), List<Edge>> byEnds = [];
            foreach (Edge e in _edges)
            {
                if (e.Dead)
                {
                    continue;
                }

                (int, int) key = e.V0 < e.V1 ? (e.V0, e.V1) : (e.V1, e.V0);
                if (!byEnds.TryGetValue(key, out List<Edge>? list))
                {
                    byEnds[key] = list = [];
                }

                foreach (Edge other in list)
                {
                    if (Coincident(e.Curve, other.Curve))
                    {
                        int sign = other.V0 == e.V0 ? 1 : -1;
                        foreach ((int input, int count) in e.Runs)
                        {
                            AddRun(other, input, sign * count);
                        }

                        other.Moved |= e.Moved;
                        e.Dead = true;
                        break;
                    }
                }

                if (!e.Dead)
                {
                    list.Add(e);
                }
            }

            static void AddRun(Edge edge, int input, int count)
            {
                for (int i = 0; i < edge.Runs.Count; i++)
                {
                    if (edge.Runs[i].Input == input)
                    {
                        edge.Runs[i] = (input, edge.Runs[i].Count + count);
                        return;
                    }
                }

                edge.Runs.Add((input, count));
            }
        }

        private bool Coincident(Segment a, Segment b)
        {
            foreach (double t in (ReadOnlySpan<double>)[0.25, 0.5, 0.75])
            {
                if (Project(b, a.Point(t)).Distance > _tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Finds each input's winding number in every face of the arrangement and keeps the edges the result lies on one side of.
        /// The faces come from the order of the edges around each point; a ray from the leftmost point of each connected piece finds
        /// the winding outside it, and crossing an edge from its right to its left adds the edge's runs. So the windings agree with
        /// each other by construction, however close edges come, and the kept edges always join into closed contours.
        /// </summary>
        private void Classify()
        {
            List<Edge> live = [];
            foreach (Edge e in _edges)
            {
                if (e.Dead)
                {
                    continue;
                }

                bool any = false;
                foreach ((_, int count) in e.Runs)
                {
                    any |= count != 0;
                }

                if (any)
                {
                    live.Add(e);
                }
                else
                {
                    e.Dead = true;
                }
            }

            if (live.Count == 0)
            {
                return;
            }

            // Half-edge 2i runs along edge i, 2i + 1 against it. Around each point, the half-edges leaving it in order of direction.
            int halves = 2 * live.Count;
            List<int>?[] around = new List<int>?[_vertices.Count];
            // Each half-edge's direction is where it leaves a small circle around its point, a few merge distances wide, rather than
            // its tangent: merging bends edges near their ends, so tangents there can disagree with the order the edges keep beyond.
            double[] angle = new double[halves], tie = new double[halves];
            double radius = 3 * _merge;
            for (int i = 0; i < live.Count; i++)
            {
                Segment c = live[i].Curve;
                (angle[2 * i], tie[2 * i]) = Leaving(c, radius);
                (angle[(2 * i) + 1], tie[(2 * i) + 1]) = Leaving(c.Reversed(), radius);
                (around[live[i].V0] ??= []).Add(2 * i);
                (around[live[i].V1] ??= []).Add((2 * i) + 1);
            }

            int[] position = new int[halves];
            foreach (List<int>? list in around)
            {
                if (list is null)
                {
                    continue;
                }

                list.Sort((p, q) =>
                {
                    double d = angle[p] - angle[q];
                    if (Math.Abs(d) > 1e-12)
                    {
                        return d < 0 ? -1 : 1;
                    }

                    int byTie = tie[p].CompareTo(tie[q]);
                    return byTie != 0 ? byTie : p.CompareTo(q);
                });
                for (int k = 0; k < list.Count; k++)
                {
                    position[list[k]] = k;
                }
            }

            _live = live;
            _around = around;
            _position = position;

            int Head(int h) => (h & 1) == 0 ? live[h >> 1].V1 : live[h >> 1].V0;

            // The face on the right of a half-edge goes on along the turn furthest right at its end: the half-edge leaving that point
            // just before the way back, counterclockwise on screen.
            int[] next = new int[halves];
            for (int h = 0; h < halves; h++)
            {
                List<int> list = around[Head(h)]!;
                next[h] = list[(position[h ^ 1] - 1 + list.Count) % list.Count];
            }

            int[] face = new int[halves];
            Array.Fill(face, -1);
            List<double> areas = [];
            List<int> firstHalf = [];
            for (int h = 0; h < halves; h++)
            {
                if (face[h] >= 0)
                {
                    continue;
                }

                int id = areas.Count;
                double area = 0;
                int g = h;
                do
                {
                    face[g] = id;
                    area += Area(live[g >> 1].Curve, (g & 1) == 1);
                    g = next[g];
                }
                while (face[g] < 0);
                areas.Add(area);
                firstHalf.Add(h);
            }

            // Connected pieces, by their points.
            int[] parent = new int[_vertices.Count];
            for (int v = 0; v < parent.Length; v++)
            {
                parent[v] = v;
            }

            int Find(int v)
            {
                while (parent[v] != v)
                {
                    parent[v] = parent[parent[v]];
                    v = parent[v];
                }

                return v;
            }

            foreach (Edge e in live)
            {
                int a = Find(e.V0), b = Find(e.V1);
                if (a != b)
                {
                    parent[a] = b;
                }
            }

            // Each piece's outside: its face of least signed area, which runs counterclockwise around it.
            Dictionary<int, int> outside = [];
            for (int f = 0; f < areas.Count; f++)
            {
                int piece = Find(Head(firstHalf[f]));
                if (!outside.TryGetValue(piece, out int best) || areas[f] < areas[best])
                {
                    outside[piece] = f;
                }
            }

            // The winding outside each piece, from a ray to the left of its leftmost point through the other pieces.
            Dictionary<int, int> leftmost = [];
            foreach (Edge e in live)
            {
                foreach (int v in (ReadOnlySpan<int>)[e.V0, e.V1])
                {
                    int piece = Find(v);
                    if (!leftmost.TryGetValue(piece, out int w) || _vertices[v].X < _vertices[w].X || (_vertices[v].X == _vertices[w].X && _vertices[v].Y < _vertices[w].Y))
                    {
                        leftmost[piece] = v;
                    }
                }
            }

            int n = _inputs.Count;
            int[]?[] winding = new int[]?[areas.Count];
            RayIndex rays = new(live);
            Queue<int> queue = new();
            foreach ((int piece, int f) in outside)
            {
                int[] w = new int[n];
                rays.Winding(_vertices[leftmost[piece]], e => Find(e.V0) == piece, w);
                winding[f] = w;
                queue.Enqueue(f);
            }

            // Across each edge: the face on its left winds more by the edge's runs than the face on its right.
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                int h = firstHalf[f];
                do
                {
                    int other = face[h ^ 1];
                    if (winding[other] is null)
                    {
                        int[] w = (int[])winding[f]!.Clone();
                        int sign = (h & 1) == 0 ? 1 : -1;
                        foreach ((int input, int count) in live[h >> 1].Runs)
                        {
                            w[input] += sign * count;
                        }

                        winding[other] = w;
                        queue.Enqueue(other);
                    }

                    h = next[h];
                }
                while (h != firstHalf[f] && face[h] == f);
            }

            bool[] inside = new bool[n];
            bool[] member = new bool[areas.Count];
            for (int f = 0; f < areas.Count; f++)
            {
                int[] w = winding[f] ?? new int[n];
                for (int k = 0; k < n; k++)
                {
                    inside[k] = _inputs[k].FillRule == FillRule.NonZero ? w[k] != 0 : (w[k] & 1) != 0;
                }

                member[f] = PathBoolean.Combine(_op, inside);
            }



            for (int i = 0; i < live.Count; i++)
            {
                bool right = member[face[2 * i]], left = member[face[(2 * i) + 1]];
                if (right != left)
                {
                    live[i].Kept = true;
                    live[i].Reverse = !right;
                }
            }
        }

        /// <summary>
        /// The direction from a curve's start to where it first leaves a circle of <paramref name="radius"/> around it (its end, if it
        /// never does), and as a tie-break, its tangent.
        /// </summary>
        private static (double Angle, double Tie) Leaving(Segment c, double radius)
        {
            D2 start = c.P0, exit = c.End;
            const int Steps = 32;
            double previous = 0;
            for (int i = 1; i <= Steps; i++)
            {
                double t = i / (double)Steps;
                if (D2.Distance(c.Point(t), start) >= radius)
                {
                    double lo = previous, hi = t;
                    for (int k = 0; k < 40; k++)
                    {
                        double mid = (lo + hi) / 2;
                        if (D2.Distance(c.Point(mid), start) >= radius)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                        }
                    }

                    exit = c.Point(hi);
                    break;
                }

                previous = t;
            }

            D2 d = exit - start, tangent = c.Tangent(0);
            return (Math.Atan2(d.Y, d.X), Math.Atan2(tangent.Y, tangent.X));
        }

        /// <summary>The integral of x dy along a curve, exact for its degree by five-point Gauss–Legendre: summed round a face, its signed area.</summary>
        private static double Area(Segment c, bool reversed)
        {
            double sum = 0;
            for (int i = 0; i < 5; i++)
            {
                double t = (Nodes[i] + 1) / 2;
                sum += Weights[i] * c.Point(t).X * c.Derivative(t).Y;
            }

            return reversed ? -sum / 2 : sum / 2;
        }

        /// <summary>
        /// Joins kept edges into closed contours. At each point the contour turns as far right as it can, in the same order of edges
        /// around the point the faces were found with, so contours never cross.
        /// </summary>
        private Path Trace()
        {
            PathBuilder builder = new();
            List<Edge> contour = [];
            for (int i = 0; i < _live.Count; i++)
            {
                Edge first = _live[i];
                if (!first.Kept || first.Used)
                {
                    continue;
                }

                contour.Clear();
                int h = Out(i);
                int start = Start(first);
                while (true)
                {
                    Edge current = _live[h >> 1];
                    current.Used = true;
                    contour.Add(current);
                    int at = End(current);
                    if (at == start)
                    {
                        break;
                    }

                    // From the way back, counterclockwise: the first kept, unused edge leaving here.
                    List<int> list = _around[at]!;
                    int k = _position[h ^ 1], next = -1;
                    for (int step = 1; step < list.Count; step++)
                    {
                        int g = list[(k - step + list.Count) % list.Count];
                        Edge e = _live[g >> 1];
                        if (e.Kept && !e.Used && g == Out(g >> 1))
                        {
                            next = g;
                            break;
                        }
                    }

                    if (next < 0)
                    {
                        break;
                    }

                    h = next;
                }

                Emit(contour, builder);
            }

            return builder.ToPathUnchecked();
        }

        /// <summary>The half-edge along which a kept edge runs in the result.</summary>
        private int Out(int i) => _live[i].Reverse ? (2 * i) + 1 : 2 * i;

        private static int Start(Edge e) => e.Reverse ? e.V1 : e.V0;

        private static int End(Edge e) => e.Reverse ? e.V0 : e.V1;

        /// <summary>Writes a contour, joining consecutive pieces of one input curve back into one curve.</summary>
        private void Emit(List<Edge> contour, PathBuilder builder)
        {
            if (contour.Count == 0)
            {
                return;
            }

            builder.MoveTo(_vertices[Start(contour[0])].ToVector2());
            int i = 0;
            while (i < contour.Count)
            {
                Edge e = contour[i];
                double t0 = e.Reverse ? e.T1 : e.T0, t1 = e.Reverse ? e.T0 : e.T1;
                int j = i + 1;
                while (j < contour.Count)
                {
                    Edge next = contour[j];
                    double n0 = next.Reverse ? next.T1 : next.T0, n1 = next.Reverse ? next.T0 : next.T1;
                    if (next.Source != e.Source || n0 != t1 || Math.Sign(n1 - n0) != Math.Sign(t1 - t0))
                    {
                        break;
                    }

                    t1 = n1;
                    j++;
                }

                D2 from = _vertices[Start(e)], to = _vertices[End(contour[j - 1])];
                Segment source = _sources[e.Source];
                Segment piece = t0 == 0 && t1 == 1 ? source : t0 == 1 && t1 == 0 ? source.Reversed() : source.Sub(t0, t1);
                piece.WithEnds(from, to).AddTo(builder);
                i = j;
            }

            builder.Close();
        }

        /// <summary>The parameter where a monotone curve reaches a height (or, vertical, an x), by bisection.</summary>
        internal static double SolveMonotone(Segment c, double value, bool vertical)
        {
            if (c.Kind == PathVerb.Line)
            {
                double a = vertical ? c.P0.X : c.P0.Y, b = vertical ? c.P1.X : c.P1.Y;
                return b == a ? 0.5 : Math.Clamp((value - a) / (b - a), 0, 1);
            }

            double lo = 0, hi = 1;
            double start = vertical ? c.P0.X : c.P0.Y, end = vertical ? c.End.X : c.End.Y;
            bool increasing = end >= start;
            for (int i = 0; i < 64; i++)
            {
                double mid = (lo + hi) / 2;
                D2 p = c.Point(mid);
                double v = vertical ? p.X : p.Y;
                if ((v < value) == increasing)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return (lo + hi) / 2;
        }

        /// <summary>
        /// Edges bucketed by height, to count crossings of rays towards −x. An edge counts over its half-open range of heights, so a ray
        /// through a shared point counts the edges meeting there once between them.
        /// </summary>
        private sealed class RayIndex
        {
            private readonly List<Edge>[] _buckets;
            private readonly double _min;
            private readonly double _step;

            internal RayIndex(List<Edge> edges)
            {
                double min = double.MaxValue, max = double.MinValue;
                foreach (Edge e in edges)
                {
                    min = Math.Min(min, Math.Min(e.Curve.P0.Y, e.Curve.End.Y));
                    max = Math.Max(max, Math.Max(e.Curve.P0.Y, e.Curve.End.Y));
                }

                int count = Math.Clamp(edges.Count / 4, 1, 4096);
                _buckets = new List<Edge>[count];
                for (int i = 0; i < count; i++)
                {
                    _buckets[i] = [];
                }

                _min = min;
                _step = max > min ? (max - min) / count : 1;
                foreach (Edge e in edges)
                {
                    double a = Math.Min(e.Curve.P0.Y, e.Curve.End.Y), b = Math.Max(e.Curve.P0.Y, e.Curve.End.Y);
                    if (a == b)
                    {
                        continue;
                    }

                    for (int i = Bucket(a); i <= Bucket(b); i++)
                    {
                        _buckets[i].Add(e);
                    }
                }
            }

            /// <summary>Adds each input's crossings of the ray left from <paramref name="at"/> to <paramref name="winding"/>, skipping some edges.</summary>
            internal void Winding(D2 at, Func<Edge, bool> skip, int[] winding)
            {
                foreach (Edge e in _buckets[Bucket(at.Y)])
                {
                    D2 a = e.Curve.P0, b = e.Curve.End;
                    bool down = a.Y <= at.Y && at.Y < b.Y, up = b.Y <= at.Y && at.Y < a.Y;
                    if ((!down && !up) || Math.Min(a.X, b.X) >= at.X || skip(e))
                    {
                        continue;
                    }

                    if (Math.Max(a.X, b.X) >= at.X && e.Curve.Point(SolveMonotone(e.Curve, at.Y, vertical: false)).X >= at.X)
                    {
                        continue;
                    }

                    // An edge crossing to the left going down counts +1: then the left of travel winds 1 more than the right.
                    foreach ((int input, int count) in e.Runs)
                    {
                        winding[input] += down ? count : -count;
                    }
                }
            }

            private int Bucket(double y) => Math.Clamp((int)((y - _min) / _step), 0, _buckets.Length - 1);
        }
    }
}
