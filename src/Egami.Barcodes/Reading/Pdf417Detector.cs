using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds PDF417 symbols: scan lines in several directions find the start and stop patterns, which are the same in every row
/// and read at any angle up to about 60 degrees from the rows; the hits on each pattern are grouped and fitted as an edge; the
/// two edges give the four corners, and rows resampled through the perspective between them are read as on a rendered symbol.
/// </summary>
internal static class Pdf417Detector
{
    private readonly record struct Hit(int Line, Vector2 Point, float Unit);

    public static List<Found> Detect(BitImage bits, int effort, Vector2 offset)
    {
        var results = new List<Found>();
        int spacing = effort >= 7 ? 1 : effort >= 3 ? 2 : 3;
        var angles = new List<float> { 0 };
        if (effort >= 3)
        {
            angles.Add(90);
        }

        if (effort >= 7)
        {
            angles.AddRange([45, 135]);
        }

        foreach (float angle in angles)
        {
            float radians = angle * MathF.PI / 180;
            var u = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
            foreach (Vector2 direction in (ReadOnlySpan<Vector2>)[u, -u])
            {
                foreach (Found found in DetectAlong(bits, direction, spacing))
                {
                    Vector2 center = (found.Corners[0] + found.Corners[2]) / 2;
                    if (!results.Any(r => Search.Inside(r.Corners, center + offset)))
                    {
                        results.Add(found with { Corners = [.. found.Corners.Select(p => p + offset)] });
                    }
                }
            }
        }

        return results;
    }

    /// <summary>Scans lines read in one direction for start and stop patterns and reads the symbols they bound.</summary>
    private static List<Found> DetectAlong(BitImage bits, Vector2 u, int spacing)
    {
        var starts = new List<Hit>();
        var stops = new List<Hit>();
        var runs = new List<int>();
        var at = new List<float>();
        var n = new Vector2(-u.Y, u.X);
        Vector2[] box = [new(0, 0), new(bits.Width, 0), new(bits.Width, bits.Height), new(0, bits.Height)];
        float minN = box.Min(p => Vector2.Dot(p, n)), maxN = box.Max(p => Vector2.Dot(p, n));
        float minU = box.Min(p => Vector2.Dot(p, u)), maxU = box.Max(p => Vector2.Dot(p, u));
        int line = 0;
        for (float o = minN + 0.5f; o < maxN; o += spacing, line++)
        {
            // Runs along the line in half-pixel samples; at[i] is where run i starts, in pixels along the line.
            runs.Clear();
            at.Clear();
            bool? previous = null;
            for (float t = minU; t < maxU; t += 0.5f)
            {
                Vector2 p = (n * o) + (u * t);
                if ((uint)(int)p.X >= (uint)bits.Width || (uint)(int)p.Y >= (uint)bits.Height || p.X < 0 || p.Y < 0)
                {
                    if (previous is not null)
                    {
                        break;
                    }

                    continue;
                }

                bool dark = bits[(int)p.X, (int)p.Y];
                if (dark == previous)
                {
                    runs[^1]++;
                }
                else
                {
                    runs.Add(1);
                    at.Add(t);
                    previous = dark;
                }
            }

            if (runs.Count < 17)
            {
                continue;
            }

            int firstDark = bits.IsDark((int)((n * o) + (u * at[0])).X, (int)((n * o) + (u * at[0])).Y) ? 0 : 1;
            ReadOnlySpan<int> span = [.. runs];
            for (int s = firstDark; s + 9 <= span.Length; s += 2)
            {
                if (Pdf417Decoder.IsStart(span.Slice(s, 8)))
                {
                    starts.Add(new Hit(line, (n * o) + (u * at[s]), Unit(span.Slice(s, 8), 17)));
                }
                else if (Pdf417Decoder.IsStop(span.Slice(s, 9)))
                {
                    float end = s + 9 < at.Count ? at[s + 9] : at[s + 8] + (span[s + 8] * 0.5f);
                    stops.Add(new Hit(line, (n * o) + (u * end), Unit(span.Slice(s, 9), 18)));
                }
            }
        }

        var results = new List<Found>();
        if (starts.Count < 3)
        {
            return results;
        }

        List<(Vector2 Top, Vector2 Bottom, float Unit)> startEdges = Edges(bits, Group(starts, spacing), u, spacing, 4);
        List<(Vector2 Top, Vector2 Bottom, float Unit)> stopEdges = Edges(bits, Group(stops, spacing), u, spacing, -14.5f);
        foreach ((Vector2 top, Vector2 bottom, float unit) in startEdges)
        {
            // The stop edge: about parallel, overlapping along the edge, at least a minimal symbol's width ahead; the nearest.
            Vector2 d = Vector2.Normalize(bottom - top), forward = new(-d.Y, d.X);
            if (Vector2.Dot(forward, u) < 0)
            {
                forward = -forward;
            }

            Vector2 middle = (top + bottom) / 2;
            float length = Vector2.Distance(top, bottom);
            (Vector2 Top, Vector2 Bottom)? stop = null;
            float nearest = float.MaxValue;
            foreach ((Vector2 stopTop, Vector2 stopBottom, float _) in stopEdges)
            {
                float ahead = Vector2.Dot(((stopTop + stopBottom) / 2) - middle, forward);
                float a0 = Vector2.Dot(stopTop - top, d), a1 = Vector2.Dot(stopBottom - top, d);
                float overlap = MathF.Min(MathF.Max(a0, a1), length) - MathF.Max(MathF.Min(a0, a1), 0);
                if (ahead > unit * 60 && ahead < nearest && MathF.Abs(Vector2.Dot(Vector2.Normalize(stopBottom - stopTop), d)) > 0.9f
                    && overlap > 0.5f * MathF.Min(length, Vector2.Distance(stopTop, stopBottom)))
                {
                    (stop, nearest) = ((stopTop, stopBottom), ahead);
                }
            }

            // Without a stop pattern (a compact symbol, or one cut off), the rows are read on to the image's edge.
            if (stop is null)
            {
                float reach = MathF.Min(Reach(bits, top, forward), Reach(bits, bottom, forward));
                stop = (top + (forward * reach), bottom + (forward * reach));
            }

            if (Read(bits, top, bottom, stop.Value.Top, stop.Value.Bottom, unit) is Found found)
            {
                results.Add(found);
            }
        }

        return results;
    }

    /// <summary>How far a point can go in a direction before it leaves the image.</summary>
    private static float Reach(BitImage bits, Vector2 from, Vector2 direction)
    {
        float reach = float.MaxValue;
        if (direction.X > 1e-4f)
        {
            reach = MathF.Min(reach, (bits.Width - 1 - from.X) / direction.X);
        }
        else if (direction.X < -1e-4f)
        {
            reach = MathF.Min(reach, -from.X / direction.X);
        }

        if (direction.Y > 1e-4f)
        {
            reach = MathF.Min(reach, (bits.Height - 1 - from.Y) / direction.Y);
        }
        else if (direction.Y < -1e-4f)
        {
            reach = MathF.Min(reach, -from.Y / direction.Y);
        }

        return MathF.Max(reach, 0);
    }

    /// <summary>The fitted edge of each group of hits, with its module width across the edge.</summary>
    private static List<(Vector2 Top, Vector2 Bottom, float Unit)> Edges(BitImage bits, List<List<Hit>> groups, Vector2 u, int spacing, float bar)
    {
        var edges = new List<(Vector2, Vector2, float)>();
        foreach (List<Hit> group in groups)
        {
            if (Edge(bits, group, u, spacing, bar) is (Vector2 top, Vector2 bottom, float unit))
            {
                edges.Add((top, bottom, unit));
            }
        }

        return edges;
    }

    /// <summary>The module width of runs that span a known number of modules, in pixels.</summary>
    private static float Unit(ReadOnlySpan<int> runs, int modules)
    {
        int total = 0;
        foreach (int r in runs)
        {
            total += r;
        }

        return total * 0.5f / modules;
    }

    /// <summary>Groups hits that lie near each other on nearby lines: the hits on one pattern's edge.</summary>
    private static List<List<Hit>> Group(List<Hit> hits, int spacing)
    {
        int[] parent = [.. Enumerable.Range(0, hits.Count)];
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        // Hits are ordered by line; join each to the hits a few lines on whose points are close.
        for (int i = 0; i < hits.Count; i++)
        {
            float reach = Math.Max(spacing * 3, hits[i].Unit * 4);
            for (int j = i + 1; j < hits.Count && hits[j].Line - hits[i].Line <= 4; j++)
            {
                if (Vector2.Distance(hits[i].Point, hits[j].Point) <= reach)
                {
                    parent[Find(j)] = Find(i);
                }
            }
        }

        return [.. Enumerable.Range(0, hits.Count).GroupBy(Find).Select(g => g.Select(i => hits[i]).ToList()).Where(g => g.Count >= 3)];
    }

    /// <summary>
    /// Reads rows sampled through the perspective between the start and stop edges, every half pixel across and every pixel
    /// down.
    /// </summary>
    private static Found? Read(BitImage bits, Vector2 startTop, Vector2 startBottom, Vector2 stopTop, Vector2 stopBottom, float unit)
    {
        Vector2[] quad = [startTop, stopTop, stopBottom, startBottom];
        var square = Perspective.SquareToQuad(quad);
        float width = MathF.Max(Vector2.Distance(startTop, stopTop), Vector2.Distance(startBottom, stopBottom));
        float height = MathF.Max(Vector2.Distance(startTop, startBottom), Vector2.Distance(stopTop, stopBottom));
        if (width < 20 || height < 3)
        {
            return null;
        }

        // Rows start two modules before the start edge, so the first run is light, and end two modules after the stop edge.
        float margin = unit * 2 / width;
        int across = (int)(width * 2 * (1 + (2 * margin))), down = (int)height;
        var lines = new List<Pdf417Decoder.Line>();
        var runs = new List<int>();
        var positions = new List<int>();
        for (int row = 0; row < down; row++)
        {
            float v = (row + 0.5f) / down;
            runs.Clear();
            positions.Clear();
            bool? previous = null;
            for (int i = 0; i < across; i++)
            {
                float x = -margin + ((1 + (2 * margin)) * i / across);
                Vector2 p = square.Map(x, v);
                bool dark = bits.IsDark((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));
                if (dark == previous)
                {
                    runs[^1]++;
                }
                else
                {
                    runs.Add(1);
                    positions.Add(i);
                    previous = dark;
                }
            }

            int firstDark = runs.Count > 0 && bits.IsDark((int)square.Map(-margin, v).X, (int)square.Map(-margin, v).Y) ? 0 : 1;
            if (Pdf417Decoder.ReadLine([.. runs], firstDark, row, [.. positions]) is Pdf417Decoder.Line line)
            {
                lines.Add(line);
            }
        }

        if (Pdf417Decoder.Decode(lines, out Vector2[] found) is not DecodedData data)
        {
            return null;
        }

        // The decoder's corners are in samples across and rows down; map them back into the image.
        Vector2 Back(Vector2 p) => square.Map(-margin + ((1 + (2 * margin)) * p.X / across), (p.Y + 0.5f) / down);
        return new Found(data, [Back(found[0]), Back(found[1]), Back(found[2]), Back(found[3])]);
    }

    /// <summary>
    /// A pattern's edge: a line fitted to its hits, oriented to run down the symbol (to the right of the reading direction).
    /// Its ends are found by walking along the middle of the pattern's widest bar, <paramref name="bar"/> modules from the edge
    /// (negative: behind it), until it turns light: near the symbol's corners a slanted scan line leaves the symbol before it
    /// crosses the whole pattern, so the hits stop short of the ends. Also returns the module width across the edge.
    /// </summary>
    private static (Vector2 Top, Vector2 Bottom, float Unit)? Edge(BitImage bits, List<Hit> hits, Vector2 u, int spacing, float bar)
    {
        List<Vector2> points = [.. hits.Select(h => h.Point)];
        float unit = hits.Select(h => h.Unit).Order().ElementAt(hits.Count / 2);
        if (Shapes.FitLine(points) is not (Vector2 origin, Vector2 direction))
        {
            return null;
        }

        // Refit to the hits within a module of the line: a stray hit off the edge does not tilt it.
        for (int pass = 0; pass < 2; pass++)
        {
            Vector2 normal = new(-direction.Y, direction.X), o = origin;
            List<Vector2> inliers = [.. points.Where(p => MathF.Abs(Vector2.Dot(p - o, normal)) <= Math.Max(unit, 1.5f))];
            if (inliers.Count < 3 || Shapes.FitLine(inliers) is not (Vector2, Vector2) refit)
            {
                break;
            }

            (points, origin, direction) = (inliers, refit.Item1, refit.Item2);
        }

        if ((u.X * direction.Y) - (u.Y * direction.X) < 0)
        {
            direction = -direction;
        }

        // The hits' module width is along the scan line; across the edge it is shorter by the angle between them.
        var forward = new Vector2(-direction.Y, direction.X);
        if (Vector2.Dot(forward, u) < 0)
        {
            forward = -forward;
        }

        unit *= MathF.Abs(Vector2.Dot(forward, u));
        float min = points.Min(p => Vector2.Dot(p - origin, direction)) - (spacing * 0.5f);
        float max = points.Max(p => Vector2.Dot(p - origin, direction)) + (spacing * 0.5f);
        Vector2 inside = forward * (bar * unit);
        min = Walk(bits, origin + inside, -direction, -min) is float up ? -up : min;
        max = Walk(bits, origin + inside, direction, max) is float down ? down : max;
        return (origin + (direction * min), origin + (direction * max), unit);
    }

    /// <summary>
    /// How far the dark bar goes from a point in a direction, starting the walk at <paramref name="from"/> (a distance known to
    /// be dark) and allowing gaps of two pixels; null when the bar is not dark there.
    /// </summary>
    private static float? Walk(BitImage bits, Vector2 origin, Vector2 direction, float from)
    {
        float last = float.NaN;
        int gap = 0;
        for (float t = from - 2; gap <= 4; t += 0.5f)
        {
            Vector2 p = origin + (direction * t);
            if (bits.IsDark((int)p.X, (int)p.Y))
            {
                last = t;
                gap = 0;
            }
            else
            {
                gap++;
            }
        }

        return float.IsNaN(last) || last < from - 2 ? null : last + 0.25f;
    }
}
