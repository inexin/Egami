using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// Centre-line tracing: ink thinned to a skeleton (Zhang–Suen), the skeleton read as a graph of strokes between ends and
/// junctions, short spurs pruned by the ink's distance to paper (Felzenszwalb–Huttenlocher), strokes joined straight through
/// junctions, each point moved to the middle of the ink across the stroke, and each stroke fitted as an open path and given the
/// width its ink covers.
/// </summary>
internal static class CenterlineTracer
{
    /// <summary>How far a stroke may bend at a junction and still run straight through it: about 45 degrees.</summary>
    private const double Through = -0.7;

    /// <summary>The ink a round end adds beyond the skeleton's last point, in widths squared: half a disc.</summary>
    private const double CapArea = Math.PI / 8;

    internal static TraceResult Trace(InkMap ink, byte[] intensities, TraceOptions options, CancellationToken cancellationToken)
    {
        int width = ink.Width, height = ink.Height, n = width * height;
        float[] coverage = Coverage(ink, intensities);

        bool[] paper = new bool[n];
        Span<byte> pixels = ink.Pixels;
        for (int i = 0; i < n; i++)
        {
            paper[i] = pixels[i] == 0;
        }

        double[] toPaper = DistanceTransform.Squared(paper, width, height, null, cancellationToken);
        byte[] skeleton = Thinning.Skeleton(pixels, width, height, cancellationToken);
        KeepSmallParts(ink, skeleton, toPaper, cancellationToken);

        Graph graph = Graph.Build(skeleton, width, height, cancellationToken);
        graph.Prune(toPaper);
        graph.MergeJunctions(toPaper);
        List<Chain> chains = graph.Chains();

        // Each pixel's ink goes to the stroke whose skeleton is nearest.
        int[] owner = new int[n];
        Array.Fill(owner, -1);
        bool[] seeds = new bool[n];
        for (int c = 0; c < chains.Count; c++)
        {
            foreach (int p in chains[c].Owned)
            {
                owner[p] = c;
            }
        }

        foreach (int p in graph.Kept())
        {
            seeds[p] = true;
        }

        int[] nearest = new int[n];
        double[] toSkeleton = DistanceTransform.Squared(seeds, width, height, nearest, cancellationToken);
        double[] area = new double[chains.Count];
        Vec[] centroid = new Vec[chains.Count];
        for (int i = 0; i < n; i++)
        {
            int seed = nearest[i];
            if (coverage[i] <= 0 || seed < 0 || owner[seed] < 0)
            {
                continue;
            }

            double reach = Math.Sqrt(toPaper[seed]) + 1.5;
            if (toSkeleton[i] > reach * reach)
            {
                continue;
            }

            int c = owner[seed];
            area[c] += coverage[i];
            centroid[c] += new Vec((i % width) + 0.5, (i / width) + 0.5) * coverage[i];
        }

        List<TracedShape> shapes = [];
        if (!options.IgnoreWhite && paper.AsSpan().Contains(true))
        {
            shapes.Add(new TracedShape(Path.Rect(new RectF(0, 0, width, height)), Rgba8.White));
        }

        CurveSettings settings = CurveSettings.From(options);
        for (int c = 0; c < chains.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Chain chain = chains[c];
            if (chain.IsDot || chain.Length < 1)
            {
                // A dot: a disc of the ink's area, where the ink is.
                if (area[c] <= 0)
                {
                    continue;
                }

                Vec middle = centroid[c] * (1 / area[c]);
                float radius = options.StrokeWidth is { } fixedWidth ? fixedWidth / 2 : (float)Math.Sqrt(area[c] / Math.PI);
                shapes.Add(new TracedShape(Path.Circle(new Vector2((float)middle.X, (float)middle.Y), radius), Rgba8.Black));
                continue;
            }

            Vec[] points = Refine(chain, coverage, toPaper, width, height);
            if (!chain.Closed && chain.FreeEnds > 0)
            {
                // Thinning stops short of a stroke's ends; each free end moves out to half the stroke's width inside the ink's
                // end, so a round pen reaches where the ink does.
                double rough = options.StrokeWidth ?? Width(area[c], chain.OwnedLength, chain.FreeEnds);
                points = Extend(points, chain.Pixel[0] >= 0, chain.Pixel[^1] >= 0, rough, coverage, width, height);
            }

            FittedCurve curve = chain.Closed ? CubicFitter.FitClosed(points, settings) : CubicFitter.FitOpen(points, settings);
            PathBuilder builder = new();
            curve.AddTo(builder, true);
            if (chain.Closed)
            {
                builder.Close();
            }

            // The skeleton's steps are longer than the line they follow (a diagonal step for a shallow slope), so the length is
            // the fitted curve's, the share of it the owned pixels span.
            double length = Length(curve) * Math.Min(1, chain.OwnedLength / Math.Max(chain.Length, 1e-6));
            float strokeWidth = options.StrokeWidth ?? (float)Width(area[c], length, chain.FreeEnds);
            shapes.Add(new TracedShape(builder.ToPath(), Rgba8.Black, TracedShapeKind.Stroke, strokeWidth));
        }

        return new TraceResult(shapes, width, height);
    }

    /// <summary>Moves a chain's free ends out along the stroke to half a pen width inside the end of its ink.</summary>
    private static Vec[] Extend(Vec[] points, bool first, bool last, double pen, float[] coverage, int width, int height)
    {
        int n = points.Length;
        if (n < 2)
        {
            return points;
        }

        List<Vec> output = [.. points];
        if (last)
        {
            Vec end = points[^1], direction = (end - points[Math.Max(0, n - 4)]).Normalize();
            if (Beyond(end, direction, pen, coverage, width, height) is { } point)
            {
                output.Add(point);
            }
        }

        if (first)
        {
            Vec end = points[0], direction = (end - points[Math.Min(n - 1, 3)]).Normalize();
            if (Beyond(end, direction, pen, coverage, width, height) is { } point)
            {
                output.Insert(0, point);
            }
        }

        return [.. output];
    }

    /// <summary>
    /// Where a stroke's end belongs: half a pen width back from where the ink, followed from <paramref name="end"/> along
    /// <paramref name="direction"/>, falls below half coverage; or <see langword="null"/> when that is not beyond the end.
    /// </summary>
    private static Vec? Beyond(Vec end, Vec direction, double pen, float[] coverage, int width, int height)
    {
        if (direction.Length < 0.5)
        {
            return null;
        }

        const double Step = 0.25;
        double previous = Sample(coverage, width, height, end);
        for (double s = Step; s <= pen + 4; s += Step)
        {
            double value = Sample(coverage, width, height, end + (direction * s));
            if (value < 0.5)
            {
                double edge = previous > value ? s - Step + (Step * (previous - 0.5) / (previous - value)) : s;
                double reach = edge - (pen / 2);
                return reach > 0.25 ? end + (direction * reach) : null;
            }

            previous = value;
        }

        return null;
    }

    /// <summary>The coverage at a point, interpolated between pixel centres; outside the image there is none.</summary>
    private static double Sample(float[] coverage, int width, int height, Vec point)
    {
        double x = point.X - 0.5, y = point.Y - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double At(int px, int py) => (uint)px < (uint)width && (uint)py < (uint)height ? coverage[(py * width) + px] : 0;
        double top = (At(x0, y0) * (1 - fx)) + (At(x0 + 1, y0) * fx);
        double bottom = (At(x0, y0 + 1) * (1 - fx)) + (At(x0 + 1, y0 + 1) * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }

    /// <summary>The length of a fitted curve, its cubics measured along sixteen chords each.</summary>
    internal static double Length(FittedCurve curve)
    {
        double length = 0;
        Vec current = curve.Start;
        foreach (CurveSegment s in curve.Segments)
        {
            if (s.IsCubic)
            {
                Vec previous = current;
                for (int i = 1; i <= 16; i++)
                {
                    Vec point = CubicFitter.Bezier(current, s.C1, s.C2, s.End, i / 16.0);
                    length += (point - previous).Length;
                    previous = point;
                }
            }
            else
            {
                length += (s.End - current).Length;
            }

            current = s.End;
        }

        return length;
    }

    /// <summary>
    /// The width w a stroke of skeleton length <paramref name="length"/> and <paramref name="ends"/> free ends needs to cover
    /// <paramref name="area"/>: area = w·length + ends·(π/8)·w², the strip plus a half disc beyond each end.
    /// </summary>
    internal static double Width(double area, double length, int ends)
    {
        if (area <= 0)
        {
            return 0.25;
        }

        double a = ends * CapArea, w = a <= 0 || length > 1e3
            ? area / Math.Max(length, 1e-3)
            : (-length + Math.Sqrt((length * length) + (4 * a * area))) / (2 * a);
        return Math.Max(0.25, w);
    }

    /// <summary>
    /// Each pixel's share of ink, from 0 (paper) to 1 (ink): its intensity between the paper's (the median of the paper pixels)
    /// and the ink's (the darkest 2 % of the ink pixels).
    /// </summary>
    internal static float[] Coverage(InkMap ink, byte[] intensities)
    {
        Span<byte> pixels = ink.Pixels;
        int[] paperHistogram = new int[256], inkHistogram = new int[256];
        int paperCount = 0, inkCount = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] != 0)
            {
                inkHistogram[intensities[i]]++;
                inkCount++;
            }
            else
            {
                paperHistogram[intensities[i]]++;
                paperCount++;
            }
        }

        double paper = paperCount > 0 ? Percentile(paperHistogram, paperCount / 2) : 255;
        double dark = inkCount > 0 ? Percentile(inkHistogram, inkCount / 50) : 0;
        double span = Math.Max(paper - dark, 1);
        float[] coverage = new float[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            coverage[i] = (float)Math.Clamp((paper - intensities[i]) / span, 0, 1);
        }

        return coverage;
    }

    private static int Percentile(int[] histogram, int rank)
    {
        int seen = 0;
        for (int v = 0; v < histogram.Length; v++)
        {
            seen += histogram[v];
            if (seen > rank)
            {
                return v;
            }
        }

        return histogram.Length - 1;
    }

    /// <summary>Gives each ink region that thinning removed altogether (a speck of two by two) a skeleton pixel at its middle.</summary>
    private static void KeepSmallParts(InkMap ink, byte[] skeleton, double[] toPaper, CancellationToken cancellationToken)
    {
        int[] labels = new int[skeleton.Length];
        int count = ink.Label(labels, cancellationToken);
        bool[] has = new bool[count];
        int[] middle = new int[count];
        Array.Fill(middle, -1);
        Span<byte> pixels = ink.Pixels;
        for (int i = 0; i < skeleton.Length; i++)
        {
            if (pixels[i] == 0)
            {
                continue;
            }

            int label = labels[i];
            has[label] |= skeleton[i] != 0;
            if (middle[label] < 0 || toPaper[i] > toPaper[middle[label]])
            {
                middle[label] = i;
            }
        }

        for (int label = 0; label < count; label++)
        {
            if (!has[label] && middle[label] >= 0)
            {
                skeleton[middle[label]] = 1;
            }
        }
    }

    /// <summary>
    /// The chain's points, each skeleton pixel moved across the stroke to the middle of its ink: the coverage-weighted mean of
    /// the offsets along the normal. Points near a junction, where other strokes' ink lies, stay put.
    /// </summary>
    private static Vec[] Refine(Chain chain, float[] coverage, double[] toPaper, int width, int height)
    {
        List<Vec> source = chain.Points;
        int count = source.Count;
        Vec[] points = new Vec[count];
        bool[] keep = new bool[count];

        // How many points each point is from the nearest junction along the chain.
        int[] gap = new int[count];
        int last = -100000;
        for (int i = 0; i < count; i++)
        {
            last = chain.Junction[i] ? i : last;
            gap[i] = i - last;
        }

        last = 100000;
        for (int i = count - 1; i >= 0; i--)
        {
            last = chain.Junction[i] ? i : last;
            gap[i] = Math.Min(gap[i], last - i);
        }

        for (int i = 0; i < count; i++)
        {
            Vec p = source[i];
            int pixel = chain.Pixel[i];
            bool end = !chain.Closed && (i == 0 || i == count - 1);
            points[i] = p;
            if (pixel < 0)
            {
                // A junction's centre stays where the stroke ends at it; where the stroke runs straight through, the
                // fit bridges it.
                keep[i] = end || !chain.Junction[i];
                continue;
            }

            // Next to a junction the skeleton bends towards the other strokes and their ink lies across the slice: those
            // points are left out.
            int r = (int)Math.Ceiling(Math.Sqrt(toPaper[pixel])) + 1;
            if (gap[i] <= r + 1)
            {
                keep[i] = end;
                continue;
            }

            keep[i] = true;

            Vec before = source[chain.Closed ? (i - 3 + count) % count : Math.Max(i - 3, 0)];
            Vec after = source[chain.Closed ? (i + 3) % count : Math.Min(i + 3, count - 1)];
            Vec direction = (after - before).Normalize();
            Vec normal = new(-direction.Y, direction.X);
            // The ink's middle along the normal: its coverage, sampled between pixel centres every quarter pixel outwards
            // from the skeleton on each side until the paper, weighs each offset.
            const double Step = 0.25;
            double sum = Sample(coverage, width, height, p), weighted = 0;
            for (int side = -1; side <= 1; side += 2)
            {
                for (double s = Step; s <= r + 1; s += Step)
                {
                    double c = Sample(coverage, width, height, p + (normal * (side * s)));
                    if (c <= 0)
                    {
                        break;
                    }

                    sum += c;
                    weighted += c * side * s;
                }
            }

            double shift = sum > 0 ? Math.Clamp(weighted / sum, -r, r) : 0;
            points[i] = p + (normal * shift);
        }

        List<Vec> kept = [];
        for (int i = 0; i < count; i++)
        {
            if (keep[i])
            {
                kept.Add(points[i]);
            }
        }

        return kept.Count >= 2 || count < 2 ? [.. kept] : [points[0], points[^1]];
    }

    /// <summary>A stroke: its points from end to end (or round), the skeleton pixels it owns, and how many free ends it has.</summary>
    internal sealed class Chain
    {
        /// <summary>Gets the points, pixel centres and junction centres.</summary>
        internal List<Vec> Points { get; } = [];

        /// <summary>Gets the skeleton pixel of each point, or -1 for a junction's centre.</summary>
        internal List<int> Pixel { get; } = [];

        /// <summary>Gets whether each point is a junction's centre.</summary>
        internal List<bool> Junction { get; } = [];

        /// <summary>Gets the skeleton pixels whose nearest ink counts towards this stroke's width.</summary>
        internal List<int> Owned { get; } = [];

        internal bool Closed { get; set; }

        internal bool IsDot { get; set; }

        internal int FreeEnds { get; set; }

        /// <summary>Gets the length along the points.</summary>
        internal double Length
        {
            get
            {
                double length = 0;
                for (int i = 1; i < Points.Count; i++)
                {
                    length += (Points[i] - Points[i - 1]).Length;
                }

                return Closed && Points.Count > 1 ? length + (Points[0] - Points[^1]).Length : length;
            }
        }

        /// <summary>Gets the length the owned pixels span: for each, half the step to the point before and half to the one after.</summary>
        internal double OwnedLength
        {
            get
            {
                double length = 0;
                int count = Points.Count;
                for (int i = 0; i < count; i++)
                {
                    if (Pixel[i] < 0)
                    {
                        continue;
                    }

                    bool hasBefore = Closed || i > 0, hasAfter = Closed || i + 1 < count;
                    if (hasBefore)
                    {
                        length += 0.5 * (Points[i] - Points[(i - 1 + count) % count]).Length;
                    }

                    if (hasAfter)
                    {
                        length += 0.5 * (Points[(i + 1) % count] - Points[i]).Length;
                    }
                }

                return length;
            }
        }

        internal void Add(Vec point, int pixel, bool junction)
        {
            Points.Add(point);
            Pixel.Add(pixel);
            Junction.Add(junction);
        }
    }

    /// <summary>The skeleton as a graph: nodes (ends and junctions, each a cluster of touching pixels) and the edges between them.</summary>
    internal sealed class Graph
    {
        private readonly List<List<int>> _nodePixels = [];
        private readonly List<Edge> _edges = [];
        private readonly int _width;

        private Graph(int width)
        {
            _width = width;
        }

        internal int NodeCount => _nodePixels.Count;

        internal int EdgeCount => _edges.Count(e => !e.Removed);

        internal static Graph Build(byte[] skeleton, int width, int height, CancellationToken cancellationToken)
        {
            Graph graph = new(width);
            int n = skeleton.Length;
            int[] degree = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (skeleton[i] != 0)
                {
                    degree[i] = System.Numerics.BitOperations.PopCount((uint)Thinning.Neighbours(skeleton, width, height, i));
                }
            }

            // Nodes: clusters of touching pixels that are not on a line (ends, junctions and lone pixels).
            int[] nodeOf = new int[n];
            Array.Fill(nodeOf, -1);
            Stack<int> stack = new();
            for (int i = 0; i < n; i++)
            {
                if (skeleton[i] == 0 || degree[i] == 2 || nodeOf[i] >= 0)
                {
                    continue;
                }

                List<int> cluster = [];
                nodeOf[i] = graph._nodePixels.Count;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    cluster.Add(p);
                    foreach (int q in Around(p, width, height))
                    {
                        if (skeleton[q] != 0 && degree[q] != 2 && nodeOf[q] < 0)
                        {
                            nodeOf[q] = nodeOf[i];
                            stack.Push(q);
                        }
                    }
                }

                cluster.Sort();
                graph._nodePixels.Add(cluster);
            }

            // Edges: walks from each node along line pixels to the next node.
            bool[] visited = new bool[n];
            for (int node = 0; node < graph._nodePixels.Count; node++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (int a in graph._nodePixels[node])
                {
                    foreach (int b in Around(a, width, height))
                    {
                        if (skeleton[b] == 0 || nodeOf[b] >= 0 || visited[b])
                        {
                            continue;
                        }

                        List<int> run = [];
                        int prev = a, cur = b, end = -1;
                        while (true)
                        {
                            run.Add(cur);
                            visited[cur] = true;
                            int next = -1;
                            foreach (int q in Around(cur, width, height))
                            {
                                // A node pixel ends the walk, except the start node's own pixels at the first step; else the
                                // line goes on to the pixel not yet walked.
                                if (q == prev || skeleton[q] == 0 || (run.Count == 1 && nodeOf[q] == node))
                                {
                                    continue;
                                }

                                if (nodeOf[q] >= 0)
                                {
                                    next = q;
                                    break;
                                }

                                if (!visited[q])
                                {
                                    next = q;
                                }
                            }

                            if (next < 0)
                            {
                                break;
                            }

                            if (nodeOf[next] >= 0)
                            {
                                end = nodeOf[next];
                                break;
                            }

                            prev = cur;
                            cur = next;
                        }

                        // A pixel or two that only touches its own junction twice is part of the junction.
                        if (end < 0 || (end == node && run.Count <= 2))
                        {
                            continue;
                        }

                        graph._edges.Add(new Edge(node, end, run));
                    }
                }
            }

            // Rings: lines with no node, such as the letter O.
            for (int i = 0; i < n; i++)
            {
                if (skeleton[i] == 0 || degree[i] != 2 || visited[i])
                {
                    continue;
                }

                List<int> run = [];
                int prev = -1, cur = i;
                while (cur >= 0 && !visited[cur])
                {
                    run.Add(cur);
                    visited[cur] = true;
                    int next = -1;
                    foreach (int q in Around(cur, width, height))
                    {
                        if (q != prev && skeleton[q] != 0 && !visited[q])
                        {
                            next = q;
                            break;
                        }
                    }

                    prev = cur;
                    cur = next;
                }

                graph._edges.Add(new Edge(-1, -1, run) { Ring = true });
            }

            return graph;
        }

        /// <summary>The 8-neighbours of a pixel inside the image.</summary>
        private static IEnumerable<int> Around(int p, int width, int height)
        {
            int x = p % width, y = p / width;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Thinning.Dx[k], ny = y + Thinning.Dy[k];
                if ((uint)nx < (uint)width && (uint)ny < (uint)height)
                {
                    yield return (ny * width) + nx;
                }
            }
        }

        /// <summary>
        /// Removes spurs: an edge from a junction to a free end no longer than the stroke is thick there, which thinning leaves at
        /// the corners of thick strokes. Repeats until none is left.
        /// </summary>
        internal void Prune(double[] toPaper)
        {
            for (int round = 0; round < 8; round++)
            {
                int[] degree = Degrees();
                bool changed = false;
                foreach (Edge edge in _edges)
                {
                    if (edge.Removed || edge.Ring || edge.A < 0 || edge.B < 0 || edge.A == edge.B)
                    {
                        continue;
                    }

                    int free = degree[edge.A] == 1 ? edge.A : degree[edge.B] == 1 ? edge.B : -1;
                    int junction = free == edge.A ? edge.B : edge.A;
                    if (free < 0 || degree[junction] < 3)
                    {
                        continue;
                    }

                    double radius = Radius(junction, toPaper);
                    if (edge.Pixels.Count + 1 <= radius + 1)
                    {
                        edge.Removed = true;
                        degree[free]--;
                        degree[junction]--;
                        _nodePixels[free] = [];
                        changed = true;
                    }
                }

                if (!changed)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Merges two junctions joined by an edge no longer than the strokes there are thick into one: where thick strokes cross,
        /// thinning splits the crossing into two forks a short way apart.
        /// </summary>
        internal void MergeJunctions(double[] toPaper)
        {
            int[] degree = Degrees();
            foreach (Edge edge in _edges)
            {
                if (edge.Removed || edge.Ring || edge.A < 0 || edge.B < 0 || edge.A == edge.B || degree[edge.A] < 3 || degree[edge.B] < 3)
                {
                    continue;
                }

                double reach = Radius(edge.A, toPaper) + Radius(edge.B, toPaper);
                if (edge.Pixels.Count + 1 > reach)
                {
                    continue;
                }

                int keep = edge.A, gone = edge.B;
                _nodePixels[keep].AddRange(edge.Pixels);
                _nodePixels[keep].AddRange(_nodePixels[gone]);
                _nodePixels[gone] = [];
                edge.Removed = true;
                foreach (Edge other in _edges)
                {
                    other.A = other.A == gone ? keep : other.A;
                    other.B = other.B == gone ? keep : other.B;
                }

                degree = Degrees();
            }
        }

        private double Radius(int node, double[] toPaper) => _nodePixels[node].Max(p => Math.Sqrt(toPaper[p]));

        /// <summary>The skeleton pixels still in the graph.</summary>
        internal IEnumerable<int> Kept()
        {
            foreach (List<int> pixels in _nodePixels)
            {
                foreach (int p in pixels)
                {
                    yield return p;
                }
            }

            foreach (Edge edge in _edges)
            {
                if (!edge.Removed)
                {
                    foreach (int p in edge.Pixels)
                    {
                        yield return p;
                    }
                }
            }
        }

        private int[] Degrees()
        {
            int[] degree = new int[_nodePixels.Count];
            foreach (Edge edge in _edges)
            {
                if (!edge.Removed && !edge.Ring)
                {
                    degree[edge.A]++;
                    if (edge.B >= 0)
                    {
                        degree[edge.B]++;
                    }
                }
            }

            return degree;
        }

        private Vec Centre(int node)
        {
            List<int> pixels = _nodePixels[node];
            double x = 0, y = 0;
            foreach (int p in pixels)
            {
                x += (p % _width) + 0.5;
                y += (p / _width) + 0.5;
            }

            return new Vec(x / pixels.Count, y / pixels.Count);
        }

        private Vec PixelCentre(int p) => new((p % _width) + 0.5, (p / _width) + 0.5);

        /// <summary>
        /// Joins the edges into strokes: at a node of two edges they always continue; at a junction the pairs that run most
        /// nearly straight through (within about 45 degrees) continue, the straightest first, and the rest end there.
        /// </summary>
        internal List<Chain> Chains()
        {
            int[] degree = Degrees();
            List<Edge> edges = [.. _edges.Where(e => !e.Removed)];

            // Ends: edge index * 2 + side (0 at A, 1 at B).
            Dictionary<int, int> partner = [];
            List<List<int>> atNode = [.. Enumerable.Range(0, _nodePixels.Count).Select(_ => new List<int>())];
            for (int e = 0; e < edges.Count; e++)
            {
                if (edges[e].Ring)
                {
                    continue;
                }

                atNode[edges[e].A].Add(2 * e);
                if (edges[e].B >= 0)
                {
                    atNode[edges[e].B].Add((2 * e) + 1);
                }
            }

            for (int node = 0; node < atNode.Count; node++)
            {
                List<int> ends = atNode[node];
                if (ends.Count == 2)
                {
                    partner[ends[0]] = ends[1];
                    partner[ends[1]] = ends[0];
                }
                else if (ends.Count >= 3)
                {
                    Vec centre = Centre(node);
                    Vec[] direction = [.. ends.Select(end => Leaving(edges[end / 2], end % 2, centre))];
                    List<(double Score, int I, int J)> pairs = [];
                    for (int i = 0; i < ends.Count; i++)
                    {
                        for (int j = i + 1; j < ends.Count; j++)
                        {
                            double score = Vec.Dot(direction[i], direction[j]);
                            if (score < Through)
                            {
                                pairs.Add((score, i, j));
                            }
                        }
                    }

                    pairs.Sort((p, q) => p.Score != q.Score ? p.Score.CompareTo(q.Score) : p.I != q.I ? p.I.CompareTo(q.I) : p.J.CompareTo(q.J));
                    foreach ((_, int i, int j) in pairs)
                    {
                        if (!partner.ContainsKey(ends[i]) && !partner.ContainsKey(ends[j]))
                        {
                            partner[ends[i]] = ends[j];
                            partner[ends[j]] = ends[i];
                        }
                    }
                }
            }

            List<Chain> chains = [];
            bool[] used = new bool[edges.Count];
            for (int e = 0; e < edges.Count; e++)
            {
                if (used[e])
                {
                    continue;
                }

                if (edges[e].Ring)
                {
                    used[e] = true;
                    Chain ring = new() { Closed = true };
                    foreach (int p in edges[e].Pixels)
                    {
                        ring.Add(PixelCentre(p), p, false);
                        ring.Owned.Add(p);
                    }

                    chains.Add(ring);
                    continue;
                }

                // Walk back to the chain's start, or round to this edge again.
                int start = e;
                bool forward = true, closed = false;
                while (true)
                {
                    int entry = (2 * start) + (forward ? 0 : 1);
                    if (!partner.TryGetValue(entry, out int previous))
                    {
                        break;
                    }

                    start = previous / 2;
                    forward = previous % 2 == 1;
                    if (start == e)
                    {
                        closed = true;
                        forward = true;
                        break;
                    }
                }

                chains.Add(Walk(edges, partner, used, degree, start, forward, closed));
            }

            // Lone pixels and specks: nodes with no edge.
            for (int node = 0; node < _nodePixels.Count; node++)
            {
                if (degree[node] == 0 && _nodePixels[node].Count > 0)
                {
                    Chain dot = new() { IsDot = true };
                    dot.Owned.AddRange(_nodePixels[node]);
                    chains.Add(dot);
                }
            }

            return chains;
        }

        private Chain Walk(List<Edge> edges, Dictionary<int, int> partner, bool[] used, int[] degree, int start, bool forward, bool closed)
        {
            Chain chain = new() { Closed = closed };
            int e = start;
            bool first = true;
            while (true)
            {
                used[e] = true;
                Edge edge = edges[e];
                int entryNode = forward ? edge.A : edge.B, exitNode = forward ? edge.B : edge.A;
                if (first && !closed && entryNode >= 0)
                {
                    AddNode(chain, entryNode, degree);
                }

                first = false;
                for (int k = 0; k < edge.Pixels.Count; k++)
                {
                    int p = edge.Pixels[forward ? k : edge.Pixels.Count - 1 - k];
                    chain.Add(PixelCentre(p), p, false);
                    chain.Owned.Add(p);
                }

                int exit = (2 * e) + (forward ? 1 : 0);
                if (!partner.TryGetValue(exit, out int next) || (closed && next / 2 == start))
                {
                    if (exitNode >= 0)
                    {
                        AddNode(chain, exitNode, degree);
                    }

                    break;
                }

                if (exitNode >= 0)
                {
                    AddNode(chain, exitNode, degree);
                }

                e = next / 2;
                forward = next % 2 == 0;
                if (used[e])
                {
                    break;
                }
            }

            // A closed chain ends where it starts: its last point is its first junction again only when it began at one.
            if (closed && chain.Points.Count > 1 && chain.Points[^1] == chain.Points[0])
            {
                chain.Points.RemoveAt(chain.Points.Count - 1);
                chain.Pixel.RemoveAt(chain.Pixel.Count - 1);
                chain.Junction.RemoveAt(chain.Junction.Count - 1);
            }

            return chain;
        }

        private void AddNode(Chain chain, int node, int[] degree)
        {
            if (degree[node] == 1)
            {
                // A free end: the end pixel itself, and its ink.
                int p = _nodePixels[node][0];
                chain.Add(PixelCentre(p), p, false);
                chain.Owned.AddRange(_nodePixels[node]);
                chain.FreeEnds++;
            }
            else
            {
                chain.Add(Centre(node), -1, degree[node] >= 3);
                if (degree[node] == 2)
                {
                    chain.Owned.AddRange(_nodePixels[node]);
                }
            }
        }

        /// <summary>The direction an edge leaves a node in, over its first few pixels.</summary>
        private Vec Leaving(Edge edge, int side, Vec centre)
        {
            int count = edge.Pixels.Count;
            int k = Math.Min(count, 6) - 1;
            int p = edge.Pixels[side == 0 ? k : count - 1 - k];
            return (PixelCentre(p) - centre).Normalize();
        }

        private sealed class Edge(int a, int b, List<int> pixels)
        {
            internal int A { get; set; } = a;

            internal int B { get; set; } = b;

            internal List<int> Pixels { get; } = pixels;

            internal bool Removed { get; set; }

            internal bool Ring { get; init; }
        }
    }
}
