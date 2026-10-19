using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds Aztec Codes: rows with seven equal runs around a dark center are bullseye candidates, checked down the column and a
/// diagonal; rays from the center find the light ring 3 modules out (5 for full-range), whose four corners fix the
/// perspective; the mode message in the core gives the symbol size, and the whole grid is sampled.
/// </summary>
internal static class AztecDetector
{
    private readonly record struct Center(Vector2 Point, float Module);

    public static List<Found> Detect(BitImage bits, int effort, Vector2 offset)
    {
        var results = new List<Found>();
        foreach (Center center in Centers(bits, effort >= 7 ? 1 : 2))
        {
            Vector2 c = center.Point + offset;
            if (results.Any(r => Search.Inside(r.Corners, c)))
            {
                continue;
            }

            if (Read(bits, center) is Found found)
            {
                results.Add(found with { Corners = [.. found.Corners.Select(p => p + offset)] });
            }
        }

        return results;
    }

    /// <summary>Bullseye centers: seven runs of about equal width, light first, across a row and then down its column.</summary>
    private static List<Center> Centers(BitImage bits, int step)
    {
        var centers = new List<Center>();
        var runs = new List<float>();
        var offsets = new List<float>();
        for (int y = 0; y < bits.Height; y += step)
        {
            Binarizer.BitRuns(bits.Row(y), runs, offsets);

            // Runs alternate from the first; find dark ones (the bullseye's center) with three equal runs on each side.
            bool firstDark = bits[0, y];
            for (int i = 3; i + 3 < runs.Count; i++)
            {
                if (((i % 2 == 0) != firstDark) || !Equal(runs, i - 3, 7))
                {
                    continue;
                }

                float cx = offsets[i] + (runs[i] / 2), module = Mean(runs, i - 3, 7);
                if (Across(bits, new Vector2(cx, y + 0.5f), new Vector2(0, 1), module) is not float cy
                    || Across(bits, new Vector2(cx, cy), new Vector2(1, 0), module) is not float refinedX
                    || Across(bits, new Vector2(refinedX, cy), new Vector2(0, 1), module) is not float refinedY)
                {
                    continue;
                }

                // A diagonal too: off the center of a bullseye turned by 45 degrees, a row and a column cross the rings at even
                // spacing, but both diagonals run along its sides.
                var point = new Vector2(refinedX, refinedY);
                if (Across(bits, point, Vector2.Normalize(new Vector2(1, 1)), module) is null
                    && Across(bits, point, Vector2.Normalize(new Vector2(1, -1)), module) is null)
                {
                    continue;
                }

                if (!centers.Any(c => Vector2.Distance(c.Point, point) < c.Module * 0.5f))
                {
                    centers.Add(new Center(point, module));
                }
            }
        }

        return centers;
    }

    private static bool Equal(List<float> runs, int start, int count)
    {
        float mean = Mean(runs, start, count);
        for (int k = start; k < start + count; k++)
        {
            if (runs[k] < mean * 0.5f || runs[k] > mean * 1.6f)
            {
                return false;
            }
        }

        return mean >= 1;
    }

    private static float Mean(List<float> runs, int start, int count)
    {
        float sum = 0;
        for (int k = start; k < start + count; k++)
        {
            sum += runs[k];
        }

        return sum / count;
    }

    /// <summary>
    /// Checks the bullseye pattern along a direction through a point: the dark center run and three alternating runs each way
    /// of about one module; returns the middle of the center run along the direction, or null.
    /// </summary>
    private static float? Across(BitImage bits, Vector2 point, Vector2 direction, float module)
    {
        if (!bits.IsDark((int)point.X, (int)point.Y))
        {
            return null;
        }

        Span<float> edges = stackalloc float[8];
        float limit = module * 9;
        for (int side = 0; side < 2; side++)
        {
            Vector2 d = side == 0 ? -direction : direction;
            bool dark = true;
            int found = 0;
            for (float t = 0; t < limit && found < 4; t += 0.5f)
            {
                Vector2 p = point + (d * t);
                if (bits.IsDark((int)p.X, (int)p.Y) != dark)
                {
                    edges[(side * 4) + found++] = t;
                    dark = !dark;
                }
            }

            if (found < 4)
            {
                return null;
            }
        }

        // Runs: the center from both sides, then three each way; all about their mean, which is about the module (a diagonal
        // crosses the rings up to 1.41 times wider or narrower than a row does).
        float center = edges[0] + edges[4], mean = (edges[3] + edges[7]) / 7;
        if (mean < module * 0.6f || mean > module * 1.7f || center < mean * 0.5f || center > mean * 1.6f)
        {
            return null;
        }

        for (int side = 0; side < 2; side++)
        {
            for (int k = 1; k < 4; k++)
            {
                float run = edges[(side * 4) + k] - edges[(side * 4) + k - 1];
                if (run < mean * 0.5f || run > mean * 1.6f)
                {
                    return null;
                }
            }
        }

        float along = Vector2.Dot(point, direction);
        return along + ((edges[4] - edges[0]) / 2);
    }

    /// <summary>
    /// Reads the symbol around a bullseye center: compact first, then full-range; with the ring's corners from the middle of
    /// a light ring, then from the inner edge of the dark ring after it (blur moves edges, but the middle can be off where a
    /// ring is broken).
    /// </summary>
    private static Found? Read(BitImage bits, Center center)
    {
        foreach (bool middle in (ReadOnlySpan<bool>)[true, false])
        {
            if (Ring(bits, center, 2, middle) is not Vector2[] inner)
            {
                continue;
            }

            float offset = middle ? 0 : 0.5f;
            foreach (bool compact in (ReadOnlySpan<bool>)[true, false])
            {
                // A full-range symbol's ring 5 modules out gives the corners further apart, and so more precisely.
                Vector2[] corners = inner;
                float half = 3 + offset;
                if (!compact && Ring(bits, center, 3, middle) is Vector2[] outer)
                {
                    (corners, half) = (outer, 5 + offset);
                }

                BarcodeModules core = Sample(bits, corners, half, compact ? 11 : 15);
                if (!Bullseye(core, compact ? 4 : 6) || AztecDecoder.SymbolSize(core, compact) is not int size)
                {
                    continue;
                }

                float c = size / 2f;
                var transform = Perspective.QuadToQuad(Square(half, c), corners);
                for (int pass = 0; pass < 3; pass++)
                {
                    if (transform.Sample(bits, size, size) is BarcodeModules grid && AztecDecoder.Decode(grid) is DecodedData data)
                    {
                        return new Found(data, [transform.Map(0, 0), transform.Map(size, 0), transform.Map(size, size), transform.Map(0, size)]);
                    }

                    // Larger full-range symbols: the reference grid along the center row and column locates modules further out.
                    if (compact || Refine(bits, transform, size) is not Perspective refined)
                    {
                        break;
                    }

                    transform = refined;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Follows the reference grid of a full-range symbol out along the center row and column, where modules alternate dark
    /// and light from the center to the edge. The farthest edge found on each of the four half-axes gives a new perspective.
    /// </summary>
    private static Perspective? Refine(BitImage bits, Perspective transform, int size)
    {
        float c = size / 2f;
        Vector2 center = transform.Map(c, c);
        Span<Vector2> from = stackalloc Vector2[4];
        Span<Vector2> to = stackalloc Vector2[4];
        int improved = 0;
        for (int axis = 0; axis < 4; axis++)
        {
            var unit = axis switch { 0 => new Vector2(1, 0), 1 => new Vector2(0, 1), 2 => new Vector2(-1, 0), _ => new Vector2(0, -1) };
            Vector2 aim = Vector2.Normalize(transform.Map(c + (unit.X * 6), c + (unit.Y * 6)) - center);
            float limit = Vector2.Distance(center, transform.Map(c + (unit.X * (c + 2)), c + (unit.Y * (c + 2))));

            // Edge k (k = 1, 2, ...) lies k - 0.5 modules out; edges 5 and 6 start the prediction.
            float Predicted(int k) => Vector2.Dot(transform.Map(c + (unit.X * (k - 0.5f)), c + (unit.Y * (k - 0.5f))) - center, aim);

            // The line is one module wide, so an aim a degree or two off leaves it after some modules: aims up to 4 degrees
            // either side are followed, and the one that gets furthest wins.
            int reached = 0;
            float reachedAt = 0;
            Vector2 direction = aim;
            for (int step = 0; step <= 16; step++)
            {
                float degrees = (step % 2 == 0 ? 1 : -1) * ((step + 1) / 2) * 0.5f;
                Vector2 turned = Vector2.Transform(aim, Matrix3x2.CreateRotation(degrees * MathF.PI / 180));
                (int k, float at) = Follow(bits, center, turned, limit, Predicted(5), Predicted(6), size);
                if (k > reached)
                {
                    (reached, reachedAt, direction) = (k, at, turned);
                }
            }

            if (reached < 7)
            {
                return null;
            }

            from[axis] = new Vector2(c + (unit.X * (reached - 0.5f)), c + (unit.Y * (reached - 0.5f)));
            to[axis] = center + (direction * reachedAt);
            improved++;
        }

        return improved == 4 ? Perspective.QuadToQuad(from, to) : null;
    }

    /// <summary>
    /// Follows the alternating modules along a ray from the center: each edge is looked for where the last two predict it,
    /// with the right polarity (edge k turns to dark when k is even); a missing edge (a spot of dirt, a blurred pair) is
    /// bridged at its expected place, twice in a row at most. Returns the last edge found and where.
    /// </summary>
    private static (int Reached, float At) Follow(BitImage bits, Vector2 center, Vector2 direction, float limit, float previous, float last, int size)
    {
        var edges = new List<(float T, bool Dark)>();
        bool dark = true;
        for (float t = 0; t < limit; t += 0.25f)
        {
            Vector2 p = center + (direction * t);
            if (bits.IsDark((int)p.X, (int)p.Y) != dark)
            {
                dark = !dark;
                edges.Add((t, dark));
            }
        }

        float reachedAt = 0;
        int reached = 0, missed = 0;
        for (int k = 7; k <= (size / 2) + 1; k++)
        {
            float expected = last + (last - previous), tolerance = (last - previous) * 0.4f;
            bool wantDark = k % 2 == 0;
            (float T, bool Dark)? match = null;
            foreach ((float t, bool d) in edges)
            {
                if (d == wantDark && MathF.Abs(t - expected) <= tolerance && (match is null || MathF.Abs(t - expected) < MathF.Abs(match.Value.T - expected)))
                {
                    match = (t, d);
                }
            }

            if (match is null)
            {
                if (++missed > 2)
                {
                    break;
                }

                (previous, last) = (last, expected);
                continue;
            }

            (previous, last, reached, reachedAt, missed) = (last, match.Value.T, k, match.Value.T, 0);
        }

        return (reached, reachedAt);
    }

    /// <summary>Whether the sampled core shows the bullseye's rings out to a radius, dark at even distances, with at most two modules wrong.</summary>
    private static bool Bullseye(BarcodeModules core, int radius)
    {
        int c = core.Width / 2, wrong = 0;
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (core[c + x, c + y] != (Math.Max(Math.Abs(x), Math.Abs(y)) % 2 == 0) && ++wrong > 2)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The corners of the square of a half-size around the center of a grid of a size, in module coordinates.</summary>
    private static Vector2[] Square(float half, float c) => [new(c - half, c - half), new(c + half, c - half), new(c + half, c + half), new(c - half, c + half)];

    /// <summary>Samples a grid of a size whose square of the half-size around its center lies at the corners.</summary>
    private static BarcodeModules Sample(BitImage bits, Vector2[] corners, float half, int size)
    {
        var transform = Perspective.QuadToQuad(Square(half, size / 2f), corners);
        return transform.Sample(bits, size, size) ?? BarcodeModules.Wrap(size, size, new bool[size * size]);
    }

    /// <summary>
    /// The four corners of the middle of a light ring (ring 2 is 3 modules out, ring 3 is 5), or of the inner edge of the dark
    /// ring after it (3.5 and 5.5 modules out): rays from the center find its edges; the points farthest out in four directions are the corners' neighbourhoods, and lines fitted
    /// to each side's points meet at the corners. The corners go clockwise in the image.
    /// </summary>
    private static Vector2[]? Ring(BitImage bits, Center center, int ring, bool middle)
    {
        const int Rays = 64;
        var points = new Vector2?[Rays];
        Span<float> edges = stackalloc float[2 * ring];
        float limit = center.Module * ((ring * 2) + 3) * 1.6f;
        for (int r = 0; r < Rays; r++)
        {
            float angle = r * 2 * MathF.PI / Rays;
            var d = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            bool dark = true;
            int found = 0;
            for (float t = 0; t < limit && found < edges.Length; t += 0.25f)
            {
                Vector2 p = center.Point + (d * t);
                bool now = bits.IsDark((int)p.X, (int)p.Y);
                if (now == dark)
                {
                    continue;
                }

                // A run shorter than a third of a module is noise.
                if (found > 0 && t - edges[found - 1] < center.Module * 0.3f)
                {
                    continue;
                }

                dark = now;
                edges[found++] = t;
            }

            // The middle of the light ring between the last two edges (blur that widens or narrows the dark rings moves both
            // edges, not the middle), or the last edge.
            if (found == edges.Length)
            {
                points[r] = center.Point + (d * (middle ? (edges[^2] + edges[^1]) / 2 : edges[^1]));
            }
        }

        // The corners: the four points farthest from the center that are at least a sixth of a turn apart.
        var order = Enumerable.Range(0, Rays).Where(r => points[r] is not null).OrderByDescending(r => Vector2.Distance(points[r]!.Value, center.Point)).ToList();
        var corners = new List<int>();
        foreach (int r in order)
        {
            if (corners.All(c => Math.Min(Math.Abs(c - r), Rays - Math.Abs(c - r)) >= Rays / 6))
            {
                corners.Add(r);
                if (corners.Count == 4)
                {
                    break;
                }
            }
        }

        if (corners.Count < 4)
        {
            return null;
        }

        corners.Sort();

        // Each side: the points between two corners, away from both; fitted, and the sides intersected.
        var sides = new (Vector2, Vector2)[4];
        for (int s = 0; s < 4; s++)
        {
            int from = corners[s], to = corners[(s + 1) % 4], span = (to - from + Rays) % Rays;
            var side = new List<Vector2>();
            for (int k = (span / 5) + 1; k < span - (span / 5); k++)
            {
                if (points[(from + k) % Rays] is Vector2 p)
                {
                    side.Add(p);
                }
            }

            if (side.Count < 2 || Shapes.FitLine(side) is not (Vector2, Vector2) line)
            {
                return null;
            }

            sides[s] = line;
        }

        var result = new Vector2[4];
        for (int k = 0; k < 4; k++)
        {
            if (Shapes.Intersect(sides[(k + 3) % 4], sides[k]) is not Vector2 corner || Vector2.Distance(corner, center.Point) > limit)
            {
                return null;
            }

            result[k] = corner;
        }

        return result;
    }
}
