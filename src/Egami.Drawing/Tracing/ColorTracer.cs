using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// Traces a label image into one layer of shapes per palette color: stacked (each layer also covers the layers above it, so no
/// gaps show between colors) or abutting (each color only its own pixels, with neighbours sharing one fitted edge).
/// </summary>
internal static class ColorTracer
{
    /// <summary>The level each channel of a color must reach to count as white for <see cref="TraceOptions.IgnoreWhite"/>.</summary>
    internal const int WhiteLevel = 240;

    internal static TraceResult Trace(LabelImage image, TraceOptions options, CancellationToken cancellationToken)
    {
        image.RemoveNoise(options.Noise, cancellationToken);
        image.Compact();
        return Trace(image, options, -1, null, 1, cancellationToken);
    }

    /// <summary>Traces a label image whose noise is already removed and whose palette is compact.</summary>
    /// <param name="image">The labels.</param>
    /// <param name="options">The settings.</param>
    /// <param name="background">A label that is never traced (Logo mode's transparent background), or -1.</param>
    /// <param name="margins">The coverage leads that place edges in Logo mode, or <see langword="null"/> to place them by color.</param>
    /// <param name="scale">The samples per output pixel along each axis.</param>
    /// <param name="cancellationToken">Cancels the trace.</param>
    internal static TraceResult Trace(LabelImage image, TraceOptions options, int background, float[]? margins, int scale, CancellationToken cancellationToken)
    {
        int[] regions = new int[image.Labels.Length];
        int count = image.LabelRegions(regions, cancellationToken);
        int[] regionLabel = new int[count];
        for (int i = 0; i < regions.Length; i++)
        {
            regionLabel[regions[i]] = image.Labels[i];
        }

        RegionTracer tracer = new(image, regions, margins, scale);
        CurveSettings settings = CurveSettings.From(options);
        if (scale > 1)
        {
            // The points come scale to a pixel: look as far along them for corners as at one point to a pixel.
            settings = settings with { Reach = 3 * scale };
        }

        bool[] skip = new bool[image.Palette.Length];
        for (int k = 0; k < skip.Length; k++)
        {
            Rgba8 c = image.Palette[k];
            skip[k] = k == background || (options.IgnoreWhite && c.R >= WhiteLevel && c.G >= WhiteLevel && c.B >= WhiteLevel);
        }

        List<TracedShape> shapes = options.Layering == TraceLayering.Abutting
            ? Abutting(tracer, image, count, regionLabel, skip, settings, cancellationToken)
            : Stacked(tracer, image, count, regionLabel, skip, settings, cancellationToken);
        return new TraceResult(shapes, image.Width / scale, image.Height / scale);
    }

    /// <summary>
    /// Stacked layers, largest area first. Layer i covers each connected set of pixels of its own color and the colors above it
    /// that holds some of its own color, so the layers above it lie on it with no gap; ignored white is a hole in every layer.
    /// </summary>
    private static List<TracedShape> Stacked(RegionTracer tracer, LabelImage image, int count, int[] regionLabel, bool[] skip, CurveSettings settings, CancellationToken cancellationToken)
    {
        int colors = image.Palette.Length;
        long[] area = new long[colors];
        foreach (byte label in image.Labels)
        {
            area[label]++;
        }

        int[] order = [.. Enumerable.Range(0, colors).Where(k => !skip[k]).OrderByDescending(k => area[k]).ThenBy(k => k)];
        int[] rank = new int[colors];
        Array.Fill(rank, -1);
        for (int i = 0; i < order.Length; i++)
        {
            rank[order[i]] = i;
        }

        // Regions that touch across a pixel edge.
        // (Each set's root is its least region whatever order the pairs are joined in, so the order of the pairs is free.)
        HashSet<long> pairs = [];
        int w = image.Width;
        int[] regions = tracer.Regions;
        long lastRight = -1, lastDown = -1;
        for (int y = 0; y < image.Height; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                int a = regions[i];
                if (x + 1 < w && regions[i + 1] != a)
                {
                    long pair = Pair(a, regions[i + 1]);
                    if (pair != lastRight)
                    {
                        pairs.Add(lastRight = pair);
                    }
                }

                if (i + w < regions.Length && regions[i + w] != a)
                {
                    long pair = Pair(a, regions[i + w]);
                    if (pair != lastDown)
                    {
                        pairs.Add(lastDown = pair);
                    }
                }
            }
        }

        long[] adjacent = [.. pairs];
        List<TracedShape>[] layers = new List<TracedShape>[order.Length];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, order.Length, parallel, layer =>
        {
            int label = order[layer];

            // Join the regions of this layer and the layers above it into connected sets.
            int[] parent = new int[count];
            for (int r = 0; r < count; r++)
            {
                parent[r] = r;
            }

            foreach (long pair in adjacent)
            {
                int a = (int)(pair >> 32), b = (int)(pair & 0xFFFFFFFF);
                if (rank[regionLabel[a]] >= layer && rank[regionLabel[b]] >= layer)
                {
                    int ra = Find(parent, a), rb = Find(parent, b);
                    if (ra != rb)
                    {
                        parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                    }
                }
            }

            bool[] holds = new bool[count];
            for (int r = 0; r < count; r++)
            {
                if (regionLabel[r] == label)
                {
                    holds[Find(parent, r)] = true;
                }
            }

            int[] owner = new int[count];
            for (int r = 0; r < count; r++)
            {
                int root = Find(parent, r);
                owner[r] = rank[regionLabel[r]] >= layer && holds[root] ? root : -1;
            }

            // One shape per connected set, its outer edge and holes together, in the order a scan meets them.
            List<EdgeLoop> loops = tracer.Walk(owner, cancellationToken);
            Dictionary<int, PathBuilder> builders = [];
            List<int> sets = [];
            foreach (EdgeLoop loop in loops)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!builders.TryGetValue(loop.Owner, out PathBuilder? builder))
                {
                    builders[loop.Owner] = builder = new PathBuilder();
                    sets.Add(loop.Owner);
                }

                FittedCurve curve = Fit(tracer, loop, settings);
                curve.AddTo(builder, true);
                builder.Close();
            }

            layers[layer] = [.. sets.Select(s => new TracedShape(builders[s].ToPath(), image.Palette[label]))];
        });

        return [.. layers.SelectMany(l => l)];
    }

    /// <summary>
    /// Abutting regions: each region's own pixels. The boundaries are cut where three regions meet, each piece between two regions
    /// is fitted once, and both regions use it, one forward and one reversed, so neighbours share their edge exactly.
    /// </summary>
    private static List<TracedShape> Abutting(RegionTracer tracer, LabelImage image, int count, int[] regionLabel, bool[] skip, CurveSettings settings, CancellationToken cancellationToken)
    {
        int[] identity = [.. Enumerable.Range(0, count)];
        List<EdgeLoop> loops = tracer.Walk(identity, cancellationToken);

        // Each loop as a list of chain pieces; each chain stored once.
        List<(Vec[] Points, bool Closed, FittedCurve? Speck)> chains = [];
        Dictionary<long, int> openChains = [];
        Dictionary<long, (int Chain, int Direction)> closedChains = [];
        List<(int Chain, bool Reversed)>[] pieces = new List<(int, bool)>[loops.Count];
        for (int l = 0; l < loops.Count; l++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EdgeLoop loop = loops[l];
            (int X, int Y)[] vertices = RegionTracer.Vertices(loop);
            int n = vertices.Length;
            List<int> junctions = [];
            for (int i = 0; i < n; i++)
            {
                if (tracer.IsJunction(vertices[i].X, vertices[i].Y))
                {
                    junctions.Add(i);
                }
            }

            List<(int, bool)> list = pieces[l] = [];
            if (junctions.Count == 0)
            {
                // A whole loop shared with one other region (or the outside): keyed by its least undirected edge.
                int least = 0;
                long leastKey = long.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    long key = Undirected(tracer, vertices[i].X, vertices[i].Y, loop.Directions[i]);
                    if (key < leastKey)
                    {
                        (least, leastKey) = (i, key);
                    }
                }

                int direction = loop.Directions[least] < 2 ? 0 : 1;
                if (closedChains.TryGetValue(leastKey, out (int Chain, int Direction) found))
                {
                    list.Add((found.Chain, found.Direction != direction));
                }
                else
                {
                    int[] xs = new int[n], ys = new int[n];
                    byte[] ds = new byte[n];
                    for (int i = 0; i < n; i++)
                    {
                        int k = (least + i) % n;
                        (xs[i], ys[i], ds[i]) = (vertices[k].X, vertices[k].Y, loop.Directions[k]);
                    }

                    closedChains[leastKey] = (chains.Count, direction);
                    list.Add((chains.Count, false));
                    chains.Add(n <= CubicFitter.Speck ? ([], true, tracer.Scale(CubicFitter.Polygon(xs, ys, ds))) : (tracer.Read(xs, ys, ds, true), true, null));
                }

                continue;
            }

            for (int j = 0; j < junctions.Count; j++)
            {
                int from = junctions[j], to = junctions[(j + 1) % junctions.Count];
                int length = ((to - from + n - 1) % n) + 1;
                int last = (from + length - 1) % n;
                long firstKey = tracer.EdgeKey(vertices[from].X, vertices[from].Y, loop.Directions[from]);
                long reverseKey = tracer.ReverseKey(vertices[last].X, vertices[last].Y, loop.Directions[last]);
                if (openChains.TryGetValue(reverseKey, out int shared))
                {
                    list.Add((shared, true));
                    continue;
                }

                int[] xs = new int[length], ys = new int[length];
                byte[] ds = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    int k = (from + i) % n;
                    (xs[i], ys[i], ds[i]) = (vertices[k].X, vertices[k].Y, loop.Directions[k]);
                }

                // The junctions the piece runs between, around the points of its edges.
                (int ex, int ey) = vertices[to];
                Vec[] points = [tracer.Point(vertices[from].X, vertices[from].Y), .. tracer.Read(xs, ys, ds, false), tracer.Point(ex, ey)];
                openChains[firstKey] = chains.Count;
                list.Add((chains.Count, false));
                chains.Add((points, false, null));
            }
        }

        FittedCurve[] fitted = new FittedCurve[chains.Count];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, chains.Count, parallel, c =>
        {
            (Vec[] points, bool closed, FittedCurve? speck) = chains[c];
            fitted[c] = speck ?? (closed ? CubicFitter.FitClosed(points, settings) : CubicFitter.FitOpen(points, settings));
        });

        // One shape per region, in scan order.
        PathBuilder?[] builders = new PathBuilder?[count];
        for (int l = 0; l < loops.Count; l++)
        {
            int region = loops[l].Owner;
            if (skip[regionLabel[region]])
            {
                continue;
            }

            PathBuilder builder = builders[region] ??= new PathBuilder();
            bool first = true;
            foreach ((int chain, bool reversed) in pieces[l])
            {
                FittedCurve curve = reversed ? fitted[chain].Reversed() : fitted[chain];
                curve.AddTo(builder, first);
                first = false;
            }

            builder.Close();
        }

        List<TracedShape> shapes = [];
        for (int r = 0; r < count; r++)
        {
            if (builders[r] is { } builder)
            {
                shapes.Add(new TracedShape(builder.ToPath(), image.Palette[regionLabel[r]]));
            }
        }

        return shapes;
    }

    /// <summary>Fits a closed loop through its points, placed by the pixels on either side; a speck keeps its pixel edges.</summary>
    internal static FittedCurve Fit(RegionTracer tracer, EdgeLoop loop, CurveSettings settings)
    {
        (int X, int Y)[] vertices = RegionTracer.Vertices(loop);
        int[] xs = [.. vertices.Select(v => v.X)], ys = [.. vertices.Select(v => v.Y)];
        return loop.Directions.Length <= CubicFitter.Speck
            ? tracer.Scale(CubicFitter.Polygon(xs, ys, loop.Directions))
            : CubicFitter.FitClosed(tracer.Read(xs, ys, loop.Directions, true), settings);
    }

    private static long Undirected(RegionTracer tracer, int x, int y, int d) => d switch
    {
        0 => tracer.EdgeKey(x, y, 0),
        1 => tracer.EdgeKey(x, y, 1),
        2 => tracer.EdgeKey(x - 1, y, 0),
        _ => tracer.EdgeKey(x, y - 1, 1),
    };

    private static long Pair(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static int Find(int[] parent, int i)
    {
        int root = i;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[i] != root)
        {
            int next = parent[i];
            parent[i] = root;
            i = next;
        }

        return root;
    }
}
