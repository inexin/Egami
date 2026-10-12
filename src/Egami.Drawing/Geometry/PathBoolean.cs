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
        // One engine per thread, reused: its lists, arrays, pieces and edges keep their memory from one operation to the next.
        Engine engine = t_engine ?? new Engine();
        t_engine = null;
        try
        {
            engine.Reset(inputs, op, tolerance);
            return engine.Run();
        }
        finally
        {
            engine.Reset([], op, tolerance);
            t_engine = engine;
        }
    }

    [ThreadStatic]
    private static Engine? t_engine;

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
        public Segment Curve { get; private set; }

        public int Input { get; private set; }

        public int Source { get; private set; }

        public double T0 { get; private set; }

        public double T1 { get; private set; }

        public List<double> Splits { get; } = [];

        public (D2 Min, D2 Max) Box { get; private set; }

        public Piece Reset(Segment curve, int input, int source, double t0, double t1)
        {
            (Curve, Input, Source, T0, T1, Box) = (curve, input, source, t0, t1, curve.ControlBounds());
            Splits.Clear();
            return this;
        }
    }

    /// <summary>An edge of the arrangement: a piece of an input curve between two merged points.</summary>
    private sealed class Edge
    {
        public Segment Curve { get; set; }

        public int Source { get; private set; }

        // The source curve's parameters at the edge's start and end, in the edge's direction.
        public double T0 { get; private set; }

        public double T1 { get; private set; }

        public int V0 { get; set; }

        public int V1 { get; set; }

        public bool Dead { get; set; }

        // Whether merging bent the edge since it was last intersected with the others.
        public bool Moved { get; set; }

        public List<double> Cuts { get; } = [];

        // How many times each input runs along the edge, forwards minus backwards: nearly always one input, so it is kept inline.
        private int _input0;
        private int _count0;
        private int _runs;
        private (int Input, int Count)[]? _more;

        public int RunCount => _runs;

        public (int Input, int Count) Run(int i) => i == 0 ? (_input0, _count0) : _more![i - 1];

        public Edge Reset(Segment curve, int source, double t0, double t1)
        {
            (Curve, Source, T0, T1) = (curve, source, t0, t1);
            (V0, V1, Dead, Moved, Kept, Reverse, Used, _runs) = (0, 0, false, false, false, false, false, 0);
            Cuts.Clear();
            return this;
        }

        public bool AnyRun()
        {
            for (int i = 0; i < _runs; i++)
            {
                if (Run(i).Count != 0)
                {
                    return true;
                }
            }

            return false;
        }

        public void AddRun(int input, int count)
        {
            for (int i = 0; i < _runs; i++)
            {
                if (Run(i).Input == input)
                {
                    if (i == 0)
                    {
                        _count0 += count;
                    }
                    else
                    {
                        _more![i - 1].Count += count;
                    }

                    return;
                }
            }

            if (_runs == 0)
            {
                (_input0, _count0) = (input, count);
            }
            else
            {
                if (_more is null || _more.Length < _runs)
                {
                    Array.Resize(ref _more, Math.Max(2, 2 * _runs));
                }

                _more[_runs - 1] = (input, count);
            }

            _runs++;
        }

        public void AddRuns(Edge other, int sign)
        {
            for (int i = 0; i < other._runs; i++)
            {
                (int input, int count) = other.Run(i);
                AddRun(input, sign * count);
            }
        }

        public bool Kept { get; set; }

        public bool Reverse { get; set; }

        public bool Used { get; set; }
    }

    private sealed class Engine
    {
        private readonly List<Piece> _piecePool = [];
        private readonly List<Edge> _edgePool = [];
        private readonly RayIndex _rays = new();
        private IReadOnlyList<Path> _inputs = [];
        private PathOp _op;
        private double _tolerance;

        // How close ends and crossings must be to become one point: a tenth of the tolerance, so merging moves no edge far enough
        // to change the area noticeably, while edges that run within the tolerance of each other between shared points still merge.
        private double _merge;
        private int _piecesUsed;
        private int _edgesUsed;
        private readonly List<Piece> _pieces = [];
        private readonly List<Segment> _sources = [];
        private readonly List<Edge> _edges = [];
        private readonly List<D2> _vertices = [];

        // The points' cells (four merge distances wide), each the head of a chain through _vertexNext; cells that hash alike
        // share a chain, which costs a distance test and nothing else.
        private readonly Dictionary<long, int> _vertexCells = [];
        private readonly List<int> _vertexNext = [];

        // Whether merging moved an end by more than rounding, and whether any curves ran together.
        private bool _snapped;
        private bool _together;
        private readonly List<(double A, double B)> _found = [];
        private int[] _order = [];
        private double[] _keys = [];

        // How many pairs of pieces one pair of curves may still be cut into.
        private const int SubdivisionBudget = 20_000;
        private int _budget;
        private readonly List<Edge> _live = [];
        private readonly List<double> _areas = [];
        private readonly List<int> _firstHalf = [];
        private readonly Queue<int> _queue = new();
        private readonly List<Edge> _contour = [];
        private int[] _around = [];
        private int[] _aroundStart = [];
        private int[] _position = [];
        private int[] _fill = [];
        private double[] _angle = [];
        private double[] _tie = [];
        private int[] _next = [];
        private int[] _face = [];
        private int[] _parent = [];
        private int[] _outside = [];
        private int[] _leftmost = [];
        private int[] _pieceOf = [];
        private int[] _winding = [];
        private bool[] _done = [];
        private bool[] _member = [];

        internal void Reset(IReadOnlyList<Path> inputs, PathOp op, double tolerance)
        {
            (_inputs, _op, _tolerance, _merge) = (inputs, op, tolerance, tolerance * 0.1);
            _pieces.Clear();
            _sources.Clear();
            _edges.Clear();
            _vertices.Clear();
            _vertexCells.Clear();
            _vertexNext.Clear();
            _found.Clear();
            _live.Clear();
            (_snapped, _together, _piecesUsed, _edgesUsed) = (false, false, 0, 0);
        }

        private Piece NewPiece(Segment curve, int input, int source, double t0, double t1)
        {
            if (_piecesUsed == _piecePool.Count)
            {
                _piecePool.Add(new Piece());
            }

            return _piecePool[_piecesUsed++].Reset(curve, input, source, t0, t1);
        }

        private Edge NewEdge(Segment curve, int source, double t0, double t1)
        {
            if (_edgesUsed == _edgePool.Count)
            {
                _edgePool.Add(new Edge());
            }

            return _edgePool[_edgesUsed++].Reset(curve, source, t0, t1);
        }

        /// <summary>Returns an array at least <paramref name="length"/> long, reusing <paramref name="array"/> when it is.</summary>
        private static T[] Rent<T>(ref T[] array, int length)
        {
            if (array.Length < length)
            {
                array = new T[Math.Max(length, 2 * array.Length)];
            }

            return array;
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

            // Points only move when merging joins ends that were apart: then edges may pass near points they do not end at, or
            // cross where they did not. Curves that run together are split where the other's pieces end, which can leave points on
            // edges too. Ends that met exactly, on curves that only cross, need neither pass.
            if (_snapped || _together)
            {
                for (int round = 0; round < 16; round++)
                {
                    SplitAtVertices();
                    MergeCoincidentEdges();
                    if (!Reintersect())
                    {
                        break;
                    }
                }
            }
            else
            {
                MergeCoincidentEdges();
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
                            _pieces.Add(NewPiece(curve, input, source, previous, t));
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

        private static (D2 Min, D2 Max) EndBox(Segment s)
        {
            D2 a = s.P0, b = s.End;
            return (new D2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new D2(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        }

        private static bool Overlap((D2 Min, D2 Max) a, (D2 Min, D2 Max) b, double margin)
            => a.Min.X <= b.Max.X + margin && b.Min.X <= a.Max.X + margin && a.Min.Y <= b.Max.Y + margin && b.Min.Y <= a.Max.Y + margin;

        /// <summary>Finds every crossing and touching between pairs of pieces, as split parameters on each.</summary>
        private void Intersect()
        {
            int count = _pieces.Count;
            int[] order = Rent(ref _order, count);
            double[] keys = Rent(ref _keys, count);
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
                keys[i] = _pieces[i].Box.Min.X;
            }

            Array.Sort(keys, order, 0, count);
            for (int i = 0; i < count; i++)
            {
                Piece a = _pieces[order[i]];
                for (int j = i + 1; j < count; j++)
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
                _budget = SubdivisionBudget;
                Subdivide(a, b, a, b, 0, 1, 0, 1, 0, found);
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
        private void Subdivide(Segment a, Segment b, Segment sa, Segment sb, double a0, double a1, double b0, double b1, int depth, List<(double A, double B)> found)
        {
            // Pathological pairs (huge loops running along each other) would cut without end: past the budget, each pair of
            // pieces is decided as if it were straight.
            bool exhausted = --_budget < 0;
            // Pieces are monotone, so their ends bound them; merging bends ends by less than the merge distance, which the margin
            // covers.
            if (!Overlap(EndBox(sa), EndBox(sb), _merge))
            {
                return;
            }

            double bendA = Bend(sa), bendB = Bend(sb);

            // Fat lines, as Bézier clipping starts: each piece lies within its bend of its chord's line, so when the other piece's
            // control points all lie further than that to one side, they cannot meet.
            if (Outside(sa, bendA, sb) || Outside(sb, bendB, sa))
            {
                return;
            }
            double flat = _tolerance * 0.05;
            double sizeA = D2.Distance(sa.P0, sa.End), sizeB = D2.Distance(sb.P0, sb.End);

            if (bendA <= flat && bendB <= flat)
            {
                if (ChordGap(sa, sb) > bendA + bendB)
                {
                    return;
                }

                if (Together(sa, sb) && Math.Max(sizeA, sizeB) > 4 * _merge && depth < 50 && !exhausted)
                {
                    // Running together over a stretch, they may still cross more than once: cut both until any two crossings in a
                    // pair of pieces are close enough to merge.
                    double ha = (a0 + a1) / 2, hb = (b0 + b1) / 2;
                    (Segment aFirst, Segment aSecond) = sa.Halves();
                    (Segment bFirst, Segment bSecond) = sb.Halves();
                    Subdivide(a, b, aFirst, bFirst, a0, ha, b0, hb, depth + 1, found);
                    Subdivide(a, b, aFirst, bSecond, a0, ha, hb, b1, depth + 1, found);
                    Subdivide(a, b, aSecond, bFirst, ha, a1, b0, hb, depth + 1, found);
                    Subdivide(a, b, aSecond, bSecond, ha, a1, hb, b1, depth + 1, found);
                    return;
                }

                if (Together(sa, sb))
                {
                    _together = true;
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
                if (sine >= 0.05 || depth >= 50 || exhausted || Math.Max(sizeA, sizeB) <= _merge * 0.01)
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
                (Segment aLeft, Segment aRight) = sa.Halves();
                (Segment bLeft, Segment bRight) = sb.Halves();
                Subdivide(a, b, aLeft, bLeft, a0, ma, b0, mb, depth + 1, found);
                Subdivide(a, b, aLeft, bRight, a0, ma, mb, b1, depth + 1, found);
                Subdivide(a, b, aRight, bLeft, ma, a1, b0, mb, depth + 1, found);
                Subdivide(a, b, aRight, bRight, ma, a1, mb, b1, depth + 1, found);
                return;
            }

            if (depth >= 50 || exhausted)
            {
                return;
            }

            // Cut whichever bends more.
            if (bendA >= bendB)
            {
                double m = (a0 + a1) / 2;
                (Segment left, Segment right) = sa.Halves();
                Subdivide(a, b, left, sb, a0, m, b0, b1, depth + 1, found);
                Subdivide(a, b, right, sb, m, a1, b0, b1, depth + 1, found);
            }
            else
            {
                double m = (b0 + b1) / 2;
                (Segment left, Segment right) = sb.Halves();
                Subdivide(a, b, sa, left, a0, a1, b0, m, depth + 1, found);
                Subdivide(a, b, sa, right, a0, a1, m, b1, depth + 1, found);
            }
        }

        /// <summary>Whether every control point of <paramref name="other"/> lies more than <paramref name="bend"/> to one side of the line through <paramref name="s"/>'s chord.</summary>
        private bool Outside(Segment s, double bend, Segment other)
        {
            D2 chord = s.End - s.P0;
            double length = chord.Length;
            if (length == 0)
            {
                return false;
            }

            D2 normal = new(-chord.Y / length, chord.X / length);
            double margin = bend + _merge;
            double d0 = D2.Dot(other.P0 - s.P0, normal), d1 = D2.Dot(other.P1 - s.P0, normal), d2 = D2.Dot(other.P2 - s.P0, normal), d3 = D2.Dot(other.P3 - s.P0, normal);
            return (d0 > margin && d1 > margin && d2 > margin && d3 > margin) || (d0 < -margin && d1 < -margin && d2 < -margin && d3 < -margin);
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
                if (piece.Splits.Count == 0)
                {
                    AddEdge(piece, 0, 1);
                    continue;
                }

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
            Edge edge = NewEdge(curve, piece.Source, s0, s1);
            edge.AddRun(piece.Input, 1);
            _edges.Add(edge);
        }

        /// <summary>Merges edge ends within the merge distance into shared points, moving each edge's ends onto them.</summary>
        private void MergeVertices()
        {
            _vertexCells.EnsureCapacity(2 * _edges.Count);
            _vertices.Capacity = Math.Max(_vertices.Capacity, 2 * _edges.Count);
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
            double cell = 4 * _merge, fx = p.X / cell, fy = p.Y / cell;
            long cx = (long)Math.Floor(fx), cy = (long)Math.Floor(fy);
            fx -= cx;
            fy -= cy;

            // Only cells within the merge distance can hold a point to merge with: the neighbors when the point is near an edge.
            long x0 = fx < 0.25 ? cx - 1 : cx, x1 = fx > 0.75 ? cx + 1 : cx, y0 = fy < 0.25 ? cy - 1 : cy, y1 = fy > 0.75 ? cy + 1 : cy;
            int found = -1;
            double best = double.MaxValue;
            for (long y = y0; y <= y1; y++)
            {
                for (long x = x0; x <= x1; x++)
                {
                    if (_vertexCells.TryGetValue(CellKey(x, y), out int v))
                    {
                        for (; v >= 0; v = _vertexNext[v])
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
                _snapped |= best > _merge * 1e-6;
                return found;
            }

            int index = _vertices.Count;
            long key = CellKey(cx, cy);
            _vertices.Add(p);
            _vertexNext.Add(_vertexCells.TryGetValue(key, out int head) ? head : -1);
            _vertexCells[key] = index;
            return index;
        }

        private static long CellKey(long x, long y) => unchecked((x * -7046029254386353131L) + y);

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
            Dictionary<long, int> cells = new(_vertices.Count);
            int[] chain = new int[_vertices.Count];
            for (int v = 0; v < _vertices.Count; v++)
            {
                long key = CellKey((long)Math.Floor(_vertices[v].X / cell), (long)Math.Floor(_vertices[v].Y / cell));
                chain[v] = cells.TryGetValue(key, out int head) ? head : -1;
                cells[key] = v;
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
                            if (cells.TryGetValue(CellKey(x, y), out int first))
                            {
                                for (int v = first; v >= 0; v = chain[v])
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
                Edge piece = NewEdge(curve.Sub(t0, t1), e.Source, s0, s1);
                (piece.V0, piece.V1, piece.Moved) = (v0, v1, e.Moved);
                piece.AddRuns(e, 1);
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
            if (!_edges.Exists(e => e.Moved && !e.Dead))
            {
                return false;
            }

            List<Edge> live = _edges.FindAll(e => !e.Dead);

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
            Dictionary<long, int> first = new(_edges.Count);
            int[] next = new int[_edges.Count];
            for (int i = 0; i < _edges.Count; i++)
            {
                Edge e = _edges[i];
                if (e.Dead)
                {
                    continue;
                }

                long key = e.V0 < e.V1 ? ((long)e.V0 << 32) | (uint)e.V1 : ((long)e.V1 << 32) | (uint)e.V0;
                if (!first.TryGetValue(key, out int head))
                {
                    first[key] = i;
                    next[i] = -1;
                    continue;
                }

                for (int j = head; j >= 0; j = next[j])
                {
                    Edge other = _edges[j];
                    if (Coincident(e.Curve, other.Curve))
                    {
                        other.AddRuns(e, other.V0 == e.V0 ? 1 : -1);
                        other.Moved |= e.Moved;
                        e.Dead = true;
                        break;
                    }
                }

                if (!e.Dead)
                {
                    next[i] = head;
                    first[key] = i;
                }
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
            List<Edge> live = _live;
            live.Clear();
            foreach (Edge e in _edges)
            {
                if (e.Dead)
                {
                    continue;
                }

                if (e.AnyRun())
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

            // Half-edge 2i runs along edge i, 2i + 1 against it. Around each point, the half-edges leaving it in order of direction,
            // kept in one array: point v's are _around[_aroundStart[v].._aroundStart[v + 1]].
            int halves = 2 * live.Count, points = _vertices.Count;
            int[] start = Rent(ref _aroundStart, points + 1);
            Array.Clear(start, 0, points + 1);
            foreach (Edge e in live)
            {
                start[e.V0 + 1]++;
                start[e.V1 + 1]++;
            }

            for (int v = 0; v < points; v++)
            {
                start[v + 1] += start[v];
            }

            int[] around = Rent(ref _around, halves), fill = Rent(ref _fill, points);
            Array.Copy(start, fill, points);

            // Each half-edge's direction is where it leaves a small circle around its point, a few merge distances wide, rather than
            // its tangent: merging bends edges near their ends, so tangents there can disagree with the order the edges keep beyond.
            double[] angle = Rent(ref _angle, halves), tie = Rent(ref _tie, halves);
            double radius = 3 * _merge;
            for (int i = 0; i < live.Count; i++)
            {
                Segment c = live[i].Curve;
                (angle[2 * i], tie[2 * i]) = Leaving(c, radius);
                (angle[(2 * i) + 1], tie[(2 * i) + 1]) = Leaving(c.Reversed(), radius);
                around[fill[live[i].V0]++] = 2 * i;
                around[fill[live[i].V1]++] = (2 * i) + 1;
            }

            int[] position = Rent(ref _position, halves);
            for (int v = 0; v < points; v++)
            {
                Span<int> list = around.AsSpan(start[v], start[v + 1] - start[v]);
                if (list.Length > 1)
                {
                    SortByDirection(list, angle, tie);
                }

                for (int k = 0; k < list.Length; k++)
                {
                    position[list[k]] = k;
                }
            }

            int Head(int h) => (h & 1) == 0 ? live[h >> 1].V1 : live[h >> 1].V0;

            // The face on the right of a half-edge goes on along the turn furthest right at its end: the half-edge leaving that point
            // just before the way back, counterclockwise on screen.
            int[] next = Rent(ref _next, halves);
            for (int h = 0; h < halves; h++)
            {
                int v = Head(h), count = start[v + 1] - start[v];
                next[h] = around[start[v] + ((position[h ^ 1] - 1 + count) % count)];
            }

            int[] face = Rent(ref _face, halves);
            Array.Fill(face, -1, 0, halves);
            List<double> areas = _areas;
            List<int> firstHalf = _firstHalf;
            areas.Clear();
            firstHalf.Clear();
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
            int[] parent = Rent(ref _parent, points);
            for (int v = 0; v < points; v++)
            {
                parent[v] = v;
            }

            foreach (Edge e in live)
            {
                int a = Find(parent, e.V0), b = Find(parent, e.V1);
                if (a != b)
                {
                    parent[a] = b;
                }
            }

            // Each piece's outside: its face of least signed area, which runs counterclockwise around it; and its leftmost point.
            int faces = areas.Count;
            int[] outside = Rent(ref _outside, points), leftmost = Rent(ref _leftmost, points);
            Array.Fill(outside, -1, 0, points);
            Array.Fill(leftmost, -1, 0, points);
            for (int f = 0; f < faces; f++)
            {
                int piece = Find(parent, Head(firstHalf[f]));
                if (outside[piece] < 0 || areas[f] < areas[outside[piece]])
                {
                    outside[piece] = f;
                }
            }

            int[] pieceOf = Rent(ref _pieceOf, live.Count);
            for (int i = 0; i < live.Count; i++)
            {
                Edge e = live[i];
                pieceOf[i] = Find(parent, e.V0);
                foreach (int v in (ReadOnlySpan<int>)[e.V0, e.V1])
                {
                    int piece = pieceOf[i], w = leftmost[piece];
                    if (w < 0 || _vertices[v].X < _vertices[w].X || (_vertices[v].X == _vertices[w].X && _vertices[v].Y < _vertices[w].Y))
                    {
                        leftmost[piece] = v;
                    }
                }
            }

            // Every face's windings in one array, n to a face; a face not reached yet is marked in done.
            int n = _inputs.Count;
            int[] winding = Rent(ref _winding, faces * n);
            Array.Clear(winding, 0, faces * n);
            bool[] done = Rent(ref _done, faces);
            Array.Clear(done, 0, faces);
            _rays.Build(live);
            Queue<int> queue = _queue;
            queue.Clear();
            for (int piece = 0; piece < points; piece++)
            {
                int f = outside[piece];
                if (f < 0)
                {
                    continue;
                }

                _rays.Winding(_vertices[leftmost[piece]], pieceOf, piece, winding.AsSpan(f * n, n));
                done[f] = true;
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
                    if (!done[other])
                    {
                        winding.AsSpan(f * n, n).CopyTo(winding.AsSpan(other * n, n));
                        int sign = (h & 1) == 0 ? 1 : -1;
                        Edge e = live[h >> 1];
                        for (int r = 0; r < e.RunCount; r++)
                        {
                            (int input, int count) = e.Run(r);
                            winding[(other * n) + input] += sign * count;
                        }

                        done[other] = true;
                        queue.Enqueue(other);
                    }

                    h = next[h];
                }
                while (h != firstHalf[f] && face[h] == f);
            }

            Span<bool> inside = n <= 64 ? stackalloc bool[n] : new bool[n];
            bool[] member = Rent(ref _member, faces);
            for (int f = 0; f < faces; f++)
            {
                for (int k = 0; k < n; k++)
                {
                    int w = winding[(f * n) + k];
                    inside[k] = _inputs[k].FillRule == FillRule.NonZero ? w != 0 : (w & 1) != 0;
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

            static int Find(int[] parent, int v)
            {
                while (parent[v] != v)
                {
                    parent[v] = parent[parent[v]];
                    v = parent[v];
                }

                return v;
            }
        }

        /// <summary>Sorts the half-edges leaving one point by direction, then by tangent: insertion sort, as a point has few.</summary>
        private static void SortByDirection(Span<int> list, double[] angle, double[] tie)
        {
            for (int i = 1; i < list.Length; i++)
            {
                int x = list[i], j = i - 1;
                while (j >= 0 && Before(x, list[j]))
                {
                    list[j + 1] = list[j];
                    j--;
                }

                list[j + 1] = x;
            }

            bool Before(int p, int q)
            {
                double d = angle[p] - angle[q];
                if (Math.Abs(d) > 1e-12)
                {
                    return d < 0;
                }

                return tie[p] != tie[q] ? tie[p] < tie[q] : p < q;
            }
        }

        /// <summary>
        /// The direction from a curve's start to where it first leaves a circle of <paramref name="radius"/> around it (its end, if it
        /// never does), and as a tie-break, its tangent.
        /// </summary>
        private static (double Angle, double Tie) Leaving(Segment c, double radius)
        {
            D2 start = c.P0;
            if (c.Kind == PathVerb.Line)
            {
                D2 r = c.P1 - c.P0;
                double a = Direction(r);
                return (a, a);
            }

            D2 tangent = c.Tangent(0);
            double tie = Direction(tangent);
            D2 exit = c.End;
            if (D2.Distance(c.End, start) > radius || c.ControlBounds() is var (min, max) && Math.Max(max.X - min.X, max.Y - min.Y) > radius)
            {
                // Newton steps on |C(t) − start|² = r² from where the start's speed would carry it, kept when they land inside the
                // first stretch; else the stretch is found by sampling.
                double speed = c.Derivative(0).Length;
                double t = speed > 0 ? Math.Min(1, radius / speed) : 0.01;
                bool found = false;
                for (int i = 0; i < 8; i++)
                {
                    D2 d = c.Point(t) - start, d1 = c.Derivative(t);
                    double f = d.LengthSquared - (radius * radius), g = 2 * D2.Dot(d, d1);
                    if (Math.Abs(f) <= radius * radius * 1e-9)
                    {
                        found = true;
                        break;
                    }

                    if (!(g > 0))
                    {
                        break;
                    }

                    t = Math.Clamp(t - (f / g), 0, 1);
                }

                if (found && Before(c, start, radius, t))
                {
                    exit = c.Point(t);
                }
                else
                {
                    exit = Sampled(c, start, radius);
                }
            }

            D2 e = exit - start;
            return (Direction(e), tie);

            // Whether the curve stays inside the circle before t: checked at a few points.
            static bool Before(Segment c, D2 start, double radius, double t)
            {
                for (int i = 1; i < 8; i++)
                {
                    if (D2.Distance(c.Point(t * i / 8), start) > radius)
                    {
                        return false;
                    }
                }

                return true;
            }

            static D2 Sampled(Segment c, D2 start, double radius)
            {
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

                        return c.Point(hi);
                    }

                    previous = t;
                }

                return c.End;
            }
        }

        /// <summary>
        /// A number that orders directions as their angles do, from −π (excluded) to π, without a trigonometric call: the "diamond
        /// angle", from −2 to 2.
        /// </summary>
        private static double Direction(D2 d)
        {
            double sum = Math.Abs(d.X) + Math.Abs(d.Y);
            if (sum == 0)
            {
                return 0;
            }

            double p = d.Y / sum;
            return d.X >= 0 ? p : d.Y >= 0 ? 2 - p : -2 - p;
        }

        /// <summary>The integral of x dy along a curve, exact for its degree by five-point Gauss–Legendre: summed round a face, its signed area.</summary>
        private static double Area(Segment c, bool reversed)
        {
            if (c.Kind == PathVerb.Line)
            {
                double line = (c.P0.X + c.P1.X) * (c.P1.Y - c.P0.Y) / 2;
                return reversed ? -line : line;
            }

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
            List<Edge> contour = _contour;
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
                    int from = _aroundStart[at], count = _aroundStart[at + 1] - from;
                    int k = _position[h ^ 1], next = -1;
                    for (int step = 1; step < count; step++)
                    {
                        int g = _around[from + ((k - step + count) % count)];
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
        /// through a shared point counts the edges meeting there once between them. Reused from one operation to the next.
        /// </summary>
        private sealed class RayIndex
        {
            private List<Edge> _edges = [];
            private int[] _start = [];
            private int[] _fill = [];
            private int[] _items = [];
            private double _min;
            private double _step;
            private int _count;

            internal void Build(List<Edge> edges)
            {
                _edges = edges;
                double min = double.MaxValue, max = double.MinValue;
                foreach (Edge e in edges)
                {
                    min = Math.Min(min, Math.Min(e.Curve.P0.Y, e.Curve.End.Y));
                    max = Math.Max(max, Math.Max(e.Curve.P0.Y, e.Curve.End.Y));
                }

                _count = Math.Clamp(edges.Count / 4, 1, 4096);
                _min = min;
                _step = max > min ? (max - min) / _count : 1;
                if (_start.Length < _count + 1)
                {
                    _start = new int[_count + 1];
                    _fill = new int[_count + 1];
                }

                Array.Clear(_start, 0, _count + 1);
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 0; i < edges.Count; i++)
                    {
                        Edge e = edges[i];
                        double a = Math.Min(e.Curve.P0.Y, e.Curve.End.Y), b = Math.Max(e.Curve.P0.Y, e.Curve.End.Y);
                        if (a == b)
                        {
                            continue;
                        }

                        for (int k = Bucket(a); k <= Bucket(b); k++)
                        {
                            if (pass == 0)
                            {
                                _start[k + 1]++;
                            }
                            else
                            {
                                _items[_fill[k]++] = i;
                            }
                        }
                    }

                    if (pass == 0)
                    {
                        for (int k = 0; k < _count; k++)
                        {
                            _start[k + 1] += _start[k];
                        }

                        if (_items.Length < _start[_count])
                        {
                            _items = new int[Math.Max(_start[_count], 2 * _items.Length)];
                        }

                        Array.Copy(_start, _fill, _count + 1);
                    }
                }
            }

            /// <summary>
            /// Adds each input's crossings of the ray left from <paramref name="at"/> to <paramref name="winding"/>, through the edges
            /// of every piece but <paramref name="piece"/>.
            /// </summary>
            internal void Winding(D2 at, int[] pieceOf, int piece, Span<int> winding)
            {
                int bucket = Bucket(at.Y);
                for (int k = _start[bucket]; k < _start[bucket + 1]; k++)
                {
                    int index = _items[k];
                    Edge e = _edges[index];
                    D2 a = e.Curve.P0, b = e.Curve.End;
                    bool down = a.Y <= at.Y && at.Y < b.Y, up = b.Y <= at.Y && at.Y < a.Y;
                    if ((!down && !up) || Math.Min(a.X, b.X) >= at.X || pieceOf[index] == piece)
                    {
                        continue;
                    }

                    if (Math.Max(a.X, b.X) >= at.X && e.Curve.Point(SolveMonotone(e.Curve, at.Y, vertical: false)).X >= at.X)
                    {
                        continue;
                    }

                    // An edge crossing to the left going down counts +1: then the left of travel winds 1 more than the right.
                    for (int r = 0; r < e.RunCount; r++)
                    {
                        (int input, int count) = e.Run(r);
                        winding[input] += down ? count : -count;
                    }
                }
            }

            private int Bucket(double y) => Math.Clamp((int)((y - _min) / _step), 0, _count - 1);
        }
    }
}
