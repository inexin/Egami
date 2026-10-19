using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds Data Matrix symbols: each large dark component's minimum-area rectangle gives four sides; each side's edge is found by
/// walking in from outside and fitted as a line; the two straight, solid sides are the L finder and fix the orientation; the
/// L's arm ends and the position of the fourth corner with the most regular clock tracks give the corners under perspective;
/// the period of the clock tracks counts the modules; a perspective transform samples the grid.
/// </summary>
internal static class DataMatrixDetector
{
    public static List<Found> Detect(BitImage bits, List<Component> components, Vector2 offset)
    {
        var results = new List<Found>();
        var claimed = new List<Vector2[]>();
        foreach (Component component in components.Take(200))
        {
            // Bars of linear and stacked symbols are far thinner than any Data Matrix shape (DMRE is 1 : 6 at most).
            if (component.Count < 40 || component.Boundary.Count < 16 || Math.Min(component.Width, component.Height) < Math.Max(component.Width, component.Height) * 0.12f)
            {
                continue;
            }

            var center = new Vector2((component.Left + component.Right) / 2f, (component.Top + component.Bottom) / 2f);
            if (claimed.Any(q => Search.Inside(q, center)))
            {
                continue;
            }

            if (DetectOne(bits, component) is Found found)
            {
                claimed.Add(found.Corners);
                results.Add(found with { Corners = [.. found.Corners.Select(p => p + offset)] });
            }
        }

        return results;
    }

    private static Found? DetectOne(BitImage bits, Component component)
    {
        Vector2[] rectangle = Shapes.MinimumRectangle(Shapes.Hull(component.Boundary));
        if (rectangle.Length != 4)
        {
            return null;
        }

        // Each side's edge line and how solid it is (the share of positions where the edge is at the line).
        var lines = new (Vector2 Point, Vector2 Direction)[4];
        float[] solidity = new float[4];
        Vector2 middle = (rectangle[0] + rectangle[2]) / 2;
        for (int s = 0; s < 4; s++)
        {
            if (Edge(bits, rectangle[s], rectangle[(s + 1) % 4], middle) is not (var line, float solid))
            {
                return null;
            }

            (lines[s], solidity[s]) = (line, solid);
        }

        // The L: the two adjacent sides that are most solid.
        int best = -1;
        float bestScore = 0;
        for (int s = 0; s < 4; s++)
        {
            float score = solidity[s] + solidity[(s + 1) % 4];
            if (score > bestScore)
            {
                (best, bestScore) = (s, score);
            }
        }

        // The L is clearly more solid than the clock track opposite it.
        if (bestScore < 1.2f || bestScore < solidity[(best + 2) % 4] + solidity[(best + 3) % 4] + 0.3f)
        {
            return null;
        }

        // Corners from adjacent lines; corner k is where side k - 1 meets side k.
        var corners = new Vector2[4];
        for (int k = 0; k < 4; k++)
        {
            if (Shapes.Intersect(lines[(k + 3) % 4], lines[k]) is not Vector2 c)
            {
                return null;
            }

            corners[k] = c;
        }

        // The L corner joins sides best and best + 1: corner best + 1. Clockwise from it in the image: the next corners.
        int l = (best + 1) % 4;
        bool clockwise = Clockwise(corners);
        Vector2 cornerL = corners[l];
        Vector2 next = corners[(l + 1) % 4], previous = corners[(l + 3) % 4], opposite = corners[(l + 2) % 4];
        if (!clockwise)
        {
            (next, previous) = (previous, next);
        }

        // Upright: bottom-left is the L corner, top-left follows it clockwise, bottom-right precedes it. The L's arms give
        // top-left and bottom-right where their dark ends; the fourth corner is searched for the most regular clock tracks.
        Vector2 bottomLeft = cornerL;
        Vector2 topLeft = ArmEnd(bits, cornerL, next, opposite) ?? next, bottomRight = ArmEnd(bits, cornerL, previous, opposite) ?? previous;
        Vector2? searched = BestFourth(bits, topLeft, bottomRight, bottomLeft, topLeft + bottomRight - bottomLeft);
        var candidates = new List<(Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight)> { (next, opposite, previous) };
        if (searched is Vector2 topRight)
        {
            candidates.Add((topLeft, topRight, bottomRight));
        }

        foreach ((Vector2 upperLeft, Vector2 upperRight, Vector2 lowerRight) in candidates)
        {
            foreach (bool mirrored in (ReadOnlySpan<bool>)[false, true])
            {
                Vector2 tl = mirrored ? lowerRight : upperLeft, br = mirrored ? upperLeft : lowerRight;
                int columns = 2 * Clock(bits, tl, upperRight, bottomLeft, br, top: true);
                int rows = 2 * Clock(bits, tl, upperRight, bottomLeft, br, top: false);
                if (columns < 10 || rows < 8)
                {
                    continue;
                }

                foreach ((int dc, int dr) in (ReadOnlySpan<(int, int)>)[(0, 0), (2, 0), (0, 2), (-2, 0), (0, -2), (2, 2), (-2, -2)])
                {
                    int c = columns + dc, r = rows + dr;
                    if (c < 10 || r < 8)
                    {
                        continue;
                    }

                    var transform = Perspective.QuadToQuad([new(0, 0), new(c, 0), new(c, r), new(0, r)], [tl, upperRight, br, bottomLeft]);
                    BarcodeModules? sampled = transform.Sample(bits, c, r);
                    if (sampled is BarcodeModules grid && DataMatrixDecoder.Decode(grid) is DecodedData data)
                    {
                        return new Found(mirrored ? data with { Mirrored = true } : data, [tl, upperRight, br, bottomLeft]);
                    }
                }
            }
        }

        return null;
    }

    private static bool Clockwise(Vector2[] q)
    {
        float area = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = q[i], b = q[(i + 1) % 4];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area > 0;
    }

    /// <summary>
    /// The edge along a side of the rectangle: from points spaced along it, walks inward to the first dark pixel, keeps the
    /// nearer half of those hits and fits a line; also returns the share of hits close to the line (1 for a solid side).
    /// </summary>
    private static ((Vector2, Vector2), float)? Edge(BitImage bits, Vector2 a, Vector2 b, Vector2 middle)
    {
        Vector2 along = b - a;
        float length = along.Length();
        if (length < 8)
        {
            return null;
        }

        Vector2 inward = Vector2.Normalize(new Vector2(-along.Y, along.X));
        if (Vector2.Dot(middle - a, inward) < 0)
        {
            inward = -inward;
        }

        int samples = Math.Clamp((int)(length / 2), 12, 80);
        float reach = length * 0.25f;
        var hits = new List<(Vector2 Point, float Depth)>();
        for (int i = 1; i < samples; i++)
        {
            // A hit is the first dark pixel after a light one: a ray that starts on a dark background outside the label has
            // no edge to find there.
            Vector2 start = a + (along * ((float)i / samples)) - (inward * 2);
            bool light = false;
            for (float d = 0; d < reach; d += 0.5f)
            {
                Vector2 p = start + (inward * d);
                if (!bits.IsDark((int)p.X, (int)p.Y))
                {
                    light = true;
                }
                else if (light)
                {
                    hits.Add((p, d));
                    break;
                }
            }
        }

        if (hits.Count < samples / 3)
        {
            return null;
        }

        // The edge line by consensus: of lines through a hit near each end, the one most hits lie on (the outer one on a clock
        // track, where half the hits go a module deeper), refitted to its inliers.
        int third = Math.Max(1, hits.Count / 3);
        List<Vector2> near = [];
        for (int i = 0; i < third; i += Math.Max(1, third / 8))
        {
            for (int j = hits.Count - third; j < hits.Count; j += Math.Max(1, third / 8))
            {
                Vector2 p = hits[i].Point, q = hits[j].Point;
                if (Vector2.DistanceSquared(p, q) < 16)
                {
                    continue;
                }

                Vector2 d = Vector2.Normalize(q - p), n = new(-d.Y, d.X);
                List<Vector2> inliers = [.. hits.Where(h => MathF.Abs(Vector2.Dot(h.Point - p, n)) <= 1.5f).Select(h => h.Point)];
                if (inliers.Count > near.Count || (inliers.Count == near.Count && inliers.Count > 0 && Depth(inliers, hits) < Depth(near, hits)))
                {
                    near = inliers;
                }
            }
        }

        if (near.Count < 4 || Shapes.FitLine(near) is not (Vector2, Vector2) line)
        {
            return null;
        }

        // Refit twice with the hits within 2 pixels of the line.
        for (int pass = 0; pass < 2; pass++)
        {
            Vector2 n = new(-line.Item2.Y, line.Item2.X), origin = line.Item1;
            List<Vector2> inliers = [.. hits.Where(h => MathF.Abs(Vector2.Dot(h.Point - origin, n)) <= 2).Select(h => h.Point)];
            if (inliers.Count < near.Count || Shapes.FitLine(inliers) is not (Vector2, Vector2) refit)
            {
                break;
            }

            (near, line) = (inliers, refit);
        }

        // Solidity: the dark share along the fitted edge, 1.5 and 2.5 pixels inside it, between 10 % and 90 % of its length.
        Vector2 direction = line.Item2, normal = Vector2.Dot(new Vector2(-direction.Y, direction.X), inward) > 0 ? new Vector2(-direction.Y, direction.X) : new Vector2(direction.Y, -direction.X);
        float ta = Vector2.Dot(a - line.Item1, direction), tb = Vector2.Dot(b - line.Item1, direction);
        int dark = 0, total = 0;
        foreach (float inset in (ReadOnlySpan<float>)[1.5f, 2.5f])
        {
            for (int i = 0; i <= 40; i++)
            {
                float t = ta + ((tb - ta) * (0.1f + (0.8f * i / 40)));
                Vector2 p = line.Item1 + (direction * t) + (normal * inset);
                dark += bits.IsDark((int)p.X, (int)p.Y) ? 1 : 0;
                total++;
            }
        }

        return (line, (float)dark / total);
    }

    /// <summary>Where the solid arm from the L corner toward an end stops being dark, walking 1.5 pixels inside it.</summary>
    private static Vector2? ArmEnd(BitImage bits, Vector2 corner, Vector2 end, Vector2 opposite)
    {
        Vector2 along = end - corner;
        float length = along.Length();
        if (length < 8)
        {
            return null;
        }

        Vector2 direction = along / length, inward = Vector2.Normalize(opposite - corner - (direction * Vector2.Dot(opposite - corner, direction)));
        float last = -1;
        int gap = 0;
        for (float t = 0; t < length * 1.3f; t += 0.5f)
        {
            Vector2 p = corner + (direction * t) + (inward * 1.5f);
            if (bits.IsDark((int)p.X, (int)p.Y))
            {
                last = t;
                gap = 0;
            }
            else if (++gap > 4)
            {
                break;
            }
        }

        return last < length * 0.6f ? null : corner + (direction * (last + 0.5f));
    }

    /// <summary>
    /// Searches around an estimate of the corner opposite the L for the position whose two clock tracks (from top-left and to
    /// bottom-right) have the most runs of a regular length: a track that drifts into the data loses its rhythm.
    /// </summary>
    private static Vector2? BestFourth(BitImage bits, Vector2 topLeft, Vector2 bottomRight, Vector2 bottomLeft, Vector2 estimate)
    {
        // The module size from the first runs of the top clock track, where it is still on the edge.
        float module = FirstRuns(bits, topLeft, estimate, Vector2.Normalize(bottomLeft - topLeft));
        if (module <= 0)
        {
            return null;
        }

        Vector2? best = null;
        int bestScore = -1;
        for (int dy = -6; dy <= 6; dy++)
        {
            for (int dx = -6; dx <= 6; dx++)
            {
                Vector2 candidate = estimate + (new Vector2(dx, dy) * (module / 3));
                int score = Regular(bits, topLeft, candidate, Vector2.Normalize(bottomLeft - topLeft) * (module / 2))
                    + Regular(bits, candidate, bottomRight, Vector2.Normalize(topLeft - candidate) * (module / 2));
                if (score > bestScore)
                {
                    (best, bestScore) = (candidate, score);
                }
            }
        }

        return best;
    }

    /// <summary>The mean length of the first four runs along a line shifted inward by 1.5 pixels: about one module.</summary>
    private static float FirstRuns(BitImage bits, Vector2 from, Vector2 to, Vector2 inward)
    {
        var runs = Runs(bits, from + (inward * 1.5f), to + (inward * 1.5f));
        return runs.Count < 6 ? 0 : (runs[1] + runs[2] + runs[3] + runs[4]) / 4f;
    }

    /// <summary>The number of runs along a line within 40 % of the median run length.</summary>
    private static int Regular(BitImage bits, Vector2 from, Vector2 to, Vector2 shift)
    {
        List<int> runs = Runs(bits, from + shift, to + shift);
        if (runs.Count < 4)
        {
            return 0;
        }

        int median = runs.Order().ElementAt(runs.Count / 2);
        return runs.Count(r => MathF.Abs(r - median) <= median * 0.4f);
    }

    /// <summary>The run lengths in pixel steps along a line.</summary>
    private static List<int> Runs(BitImage bits, Vector2 from, Vector2 to)
    {
        var runs = new List<int>();
        int steps = (int)Vector2.Distance(from, to);
        bool? previous = null;
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(from, to, (float)i / Math.Max(steps, 1));
            bool dark = bits.IsDark((int)p.X, (int)p.Y);
            if (dark == previous)
            {
                runs[^1]++;
            }
            else
            {
                runs.Add(1);
                previous = dark;
            }
        }

        return runs;
    }

    /// <summary>The mean depth of the hits among the points: lower is nearer the outside.</summary>
    private static float Depth(List<Vector2> points, List<(Vector2 Point, float Depth)> hits) =>
        points.Count == 0 ? float.MaxValue : hits.Where(h => points.Contains(h.Point)).Average(h => h.Depth);

    /// <summary>
    /// Counts the dark modules of the clock track along the top (or right) side: lines just inside the edge, at several insets,
    /// each give a count; a count is believed only from insets inside its own first module, and the most common one wins.
    /// </summary>
    private static int Clock(BitImage bits, Vector2 topLeft, Vector2 topRight, Vector2 bottomLeft, Vector2 bottomRight, bool top)
    {
        Vector2 from = top ? topLeft : topRight, to = top ? topRight : bottomRight;
        Vector2 inward = Vector2.Normalize(top ? bottomLeft - topLeft : topLeft - topRight);
        float side = Vector2.Distance(from, to);
        var votes = new Dictionary<int, int>();
        foreach (float inset in (ReadOnlySpan<float>)[1, 1.5f, 2, 2.5f, 3, 4, 5, 6, 8, 10])
        {
            int runs = DarkRuns(bits, from + (inward * inset), to + (inward * inset));
            if (runs >= 4 && inset < 0.6f * side / (2 * runs))
            {
                votes[runs] = votes.GetValueOrDefault(runs) + 1;
            }
        }

        int count = votes.Count == 0 ? 0 : votes.OrderByDescending(v => v.Value).ThenByDescending(v => v.Key).First().Key;
        return count;
    }

    /// <summary>
    /// The number of dark runs along a line, from their period rather than their count, so a split or merged run does not
    /// change it: runs and gaps shorter than a pixel are noise; the period is the mean of the start-to-start distances near
    /// their median.
    /// </summary>
    private static int DarkRuns(BitImage bits, Vector2 from, Vector2 to)
    {
        int steps = (int)(Vector2.Distance(from, to) * 2) + 1;
        var starts = new List<int>();
        int runStart = -1, lightRun = 0;
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(from, to, (float)i / steps);
            if (bits.IsDark((int)p.X, (int)p.Y))
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                lightRun = 0;
            }
            else if (runStart >= 0 && ++lightRun >= 2)
            {
                // A dark run of at least two samples (a pixel) counts.
                if (i - lightRun - runStart + 1 >= 2)
                {
                    starts.Add(runStart);
                }

                runStart = -1;
            }
        }

        if (runStart >= 0 && steps - runStart + 1 >= 2)
        {
            starts.Add(runStart);
        }

        if (starts.Count < 4)
        {
            return starts.Count;
        }

        var periods = new List<int>(starts.Count - 1);
        for (int i = 1; i < starts.Count; i++)
        {
            periods.Add(starts[i] - starts[i - 1]);
        }

        int median = periods.Order().ElementAt(periods.Count / 2);
        var regular = periods.Where(d => d >= median * 0.6f && d <= median * 1.4f).ToList();
        if (regular.Count < periods.Count * 0.6f)
        {
            return starts.Count;
        }

        // The first run starts at the line's start; the count is the span to the last regular start over the period, plus one.
        float period = (float)regular.Average();
        return (int)MathF.Round((starts[^1] - starts[0]) / period) + 1;
    }
}
