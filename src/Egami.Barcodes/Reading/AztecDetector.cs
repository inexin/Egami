using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds Aztec Codes: rows with seven equal runs around a dark center are bullseye candidates, checked down the column; rays
/// from the center find the inner edge of the dark ring 4 modules out (6 for full-range), whose four corners fix the
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

                var point = new Vector2(refinedX, refinedY);
                if (!centers.Any(c => Vector2.Distance(c.Point, point) < c.Module * 2))
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
        float limit = module * 6;
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

        // Runs: the center from both sides, then three each way.
        float center = edges[0] + edges[4];
        for (int side = 0; side < 2; side++)
        {
            for (int k = 1; k < 4; k++)
            {
                float run = edges[(side * 4) + k] - edges[(side * 4) + k - 1];
                if (run < module * 0.5f || run > module * 1.6f)
                {
                    return null;
                }
            }
        }

        if (center < module * 0.5f || center > module * 1.6f)
        {
            return null;
        }

        float along = Vector2.Dot(point, direction);
        return along + ((edges[4] - edges[0]) / 2);
    }

    /// <summary>Reads the symbol around a bullseye center: compact first, then full-range.</summary>
    private static Found? Read(BitImage bits, Center center)
    {
        if (Ring(bits, center, 2) is not Vector2[] inner)
        {
            return null;
        }

        foreach (bool compact in (ReadOnlySpan<bool>)[true, false])
        {
            int coreSize = compact ? 11 : 15;
            BarcodeModules core = Sample(bits, inner, 3.5f, coreSize);
            if (!Bullseye(core, compact ? 4 : 6) || AztecDecoder.SymbolSize(core, compact) is not int size)
            {
                continue;
            }

            // A full-range symbol's dark ring 6 modules out gives the corners further apart, and so more precisely.
            Vector2[] corners = inner;
            float half = 3.5f;
            if (!compact && Ring(bits, center, 3) is Vector2[] outer)
            {
                (corners, half) = (outer, 5.5f);
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

        return null;
    }

    /// <summary>
    /// Follows the reference grid of a full-range symbol out along the center row and column, where modules alternate dark
    /// and light from the center to the edge: each module edge is looked for where the last two predict it. The farthest
    /// edge found on each of the four half-axes gives a new perspective.
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
            Vector2 direction = Vector2.Normalize(transform.Map(c + (unit.X * 6), c + (unit.Y * 6)) - center);

            // Transitions along the ray, with the polarity they turn to.
            float limit = Vector2.Distance(center, transform.Map(c + (unit.X * (c + 2)), c + (unit.Y * (c + 2))));
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

            // Edge k (k = 1, 2, ...) lies k - 0.5 modules out and turns to dark when k is even.
            float Predicted(int k) => Vector2.Dot(transform.Map(c + (unit.X * (k - 0.5f)), c + (unit.Y * (k - 0.5f))) - center, direction);
            float previous = Predicted(5), last = Predicted(6);
            int reached = 0;
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
                    break;
                }

                (previous, last, reached) = (last, match.Value.T, k);
            }

            if (reached < 7)
            {
                return null;
            }

            from[axis] = new Vector2(c + (unit.X * (reached - 0.5f)), c + (unit.Y * (reached - 0.5f)));
            to[axis] = center + (direction * last);
            improved++;
        }

        return improved == 4 ? Perspective.QuadToQuad(from, to) : null;
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
    /// The four corners of the inner edge of a dark ring (ring 2 is 4 modules out, ring 3 is 6): rays from the center find
    /// where they cross into it; the points farthest out in four directions are the corners' neighbourhoods, and lines fitted
    /// to each side's points meet at the corners. The corners go clockwise in the image.
    /// </summary>
    private static Vector2[]? Ring(BitImage bits, Center center, int ring)
    {
        const int Rays = 64;
        var points = new Vector2?[Rays];
        float limit = center.Module * ((ring * 2) + 3) * 1.6f;
        for (int r = 0; r < Rays; r++)
        {
            float angle = r * 2 * MathF.PI / Rays;
            var d = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            bool dark = true;
            int crossings = 0;
            float runStart = 0;
            for (float t = 0; t < limit; t += 0.25f)
            {
                Vector2 p = center.Point + (d * t);
                bool now = bits.IsDark((int)p.X, (int)p.Y);
                if (now == dark)
                {
                    continue;
                }

                // A run shorter than a third of a module is noise.
                if (t - runStart < center.Module * 0.3f && crossings > 0)
                {
                    continue;
                }

                dark = now;
                runStart = t;
                if (now && ++crossings == ring)
                {
                    points[r] = p;
                    break;
                }
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
