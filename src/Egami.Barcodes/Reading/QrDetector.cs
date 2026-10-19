using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds QR Codes in a binarized image: finder patterns by their 1:1:3:1:1 runs along rows, checked across columns and rows
/// again; triples of them that form a right angle with like module sizes; the symbol's size from their distances; the
/// bottom-right alignment pattern; and a perspective transform that samples the module grid.
/// </summary>
internal static class QrDetector
{
    /// <summary>A finder pattern center, its module size and how many rows found it.</summary>
    internal sealed class Finder(Vector2 center, float module)
    {
        public Vector2 Center = center;
        public float Module = module;
        public int Count = 1;
        public bool Used;
    }

    public static List<Found> Detect(BitImage bits, int effort, Vector2 offset)
    {
        var results = new List<Found>();
        List<Finder> finders = FindFinders(bits, effort);
        if (finders.Count < 3)
        {
            return results;
        }

        // Triples, best first: like module sizes, a right angle at the corner, legs of like length.
        var triples = new List<(Finder A, Finder B, Finder C, float Score)>();
        // Up to 20 symbols' worth of finders; pairs too far apart for one symbol are skipped early.
        int n = Math.Min(finders.Count, 60);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (Vector2.Distance(finders[i].Center, finders[j].Center) > MathF.Max(finders[i].Module, finders[j].Module) * 300)
                {
                    continue;
                }

                for (int k = j + 1; k < n; k++)
                {
                    if (Score(finders[i], finders[j], finders[k]) is float score)
                    {
                        triples.Add((finders[i], finders[j], finders[k], score));
                    }
                }
            }
        }

        foreach ((Finder a, Finder b, Finder c, _) in triples.OrderBy(t => t.Score))
        {
            if (a.Used || b.Used || c.Used)
            {
                continue;
            }

            if (DecodeTriple(bits, a, b, c) is Found found)
            {
                a.Used = b.Used = c.Used = true;
                results.Add(found with { Corners = [.. found.Corners.Select(p => p + offset)] });
            }
        }

        return results;
    }

    /// <summary>Scores a triple (lower is better), or returns null when it cannot be one symbol's finders.</summary>
    private static float? Score(Finder a, Finder b, Finder c)
    {
        float min = MathF.Min(a.Module, MathF.Min(b.Module, c.Module)), max = MathF.Max(a.Module, MathF.Max(b.Module, c.Module));
        if (max > min * 1.6f)
        {
            return null;
        }

        // The corner is opposite the longest side; the two legs should be equal and the angle right.
        (Finder corner, Finder p, Finder q) = Order(a, b, c);
        float d1 = Vector2.Distance(corner.Center, p.Center), d2 = Vector2.Distance(corner.Center, q.Center);
        float module = (a.Module + b.Module + c.Module) / 3;
        if (d1 < module * 9 || d2 < module * 9 || d1 > module * 200 || d2 > module * 200)
        {
            return null;
        }

        float legRatio = MathF.Abs(d1 - d2) / MathF.Max(d1, d2);
        Vector2 u = Vector2.Normalize(p.Center - corner.Center), v = Vector2.Normalize(q.Center - corner.Center);
        float cosine = MathF.Abs(Vector2.Dot(u, v));
        if (legRatio > 0.5f || cosine > 0.5f)
        {
            return null;
        }

        return legRatio + cosine + ((max - min) / max);
    }

    /// <summary>Puts the corner finder first, then the other two in clockwise order (top right, then bottom left).</summary>
    private static (Finder Corner, Finder Right, Finder Down) Order(Finder a, Finder b, Finder c)
    {
        float ab = Vector2.DistanceSquared(a.Center, b.Center), bc = Vector2.DistanceSquared(b.Center, c.Center), ac = Vector2.DistanceSquared(a.Center, c.Center);
        (Finder corner, Finder p, Finder q) = bc >= ab && bc >= ac ? (a, b, c) : ac >= ab && ac >= bc ? (b, a, c) : (c, a, b);

        // In image coordinates (y down) the cross product of (p - corner) and (q - corner) is positive when p is clockwise first.
        Vector2 u = p.Center - corner.Center, v = q.Center - corner.Center;
        return (u.X * v.Y) - (u.Y * v.X) > 0 ? (corner, p, q) : (corner, q, p);
    }

    private static Found? DecodeTriple(BitImage bits, Finder a, Finder b, Finder c)
    {
        (Finder topLeft, Finder topRight, Finder bottomLeft) = Order(a, b, c);
        float module = ModuleSize(bits, topLeft, topRight, bottomLeft);
        if (module < 1)
        {
            return null;
        }

        int estimate = (int)MathF.Round(((Vector2.Distance(topLeft.Center, topRight.Center) + Vector2.Distance(topLeft.Center, bottomLeft.Center)) / 2 / module) + 7);
        foreach (int delta in (ReadOnlySpan<int>)[0, -4, 4])
        {
            int dimension = Dimension(estimate) + delta;
            if (dimension is < 21 or > 177)
            {
                continue;
            }

            // The parallelogram (or alignment) estimate first, then nearby positions of the fourth point for perspective.
            for (int attempt = 0; attempt < 25; attempt++)
            {
                var shift = new Vector2((attempt % 5) - 2, (attempt / 5) - 2) * (module * 0.75f);
                if (attempt > 0 && shift == Vector2.Zero)
                {
                    continue;
                }

                if (Sample(bits, topLeft, topRight, bottomLeft, dimension, module, attempt == 0 ? null : shift) is (BarcodeModules grid, Perspective transform)
                    && QrDecoder.Decode(grid) is DecodedData data)
                {
                    Vector2[] corners = [transform.Map(0, 0), transform.Map(dimension, 0), transform.Map(dimension, dimension), transform.Map(0, dimension)];
                    return new Found(data, corners);
                }

                if (attempt == 0 && dimension >= 25 && delta != 0)
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>Rounds to the nearest QR size: 17 plus a multiple of 4.</summary>
    private static int Dimension(int estimate) => (estimate & 3) switch
    {
        0 => estimate + 1,
        2 => estimate - 1,
        3 => estimate + 2,
        _ => estimate,
    };

    /// <summary>Samples the grid through the finders and, from version 2, the bottom-right alignment pattern when found.</summary>
    private static (BarcodeModules, Perspective)? Sample(BitImage bits, Finder topLeft, Finder topRight, Finder bottomLeft, int dimension, float module, Vector2? shift)
    {
        Vector2 tl = topLeft.Center, tr = topRight.Center, bl = bottomLeft.Center;
        Vector2 bottomRight = tr - tl + bl;
        Vector2 fourth = bottomRight + (shift ?? Vector2.Zero);
        float target = dimension - 3.5f;
        if (dimension >= 25 && shift is null)
        {
            // The alignment pattern centered 3 modules in from the bottom-right corner's finder position.
            float correction = 1 - (3f / (dimension - 7));
            var estimate = tl + (correction * (bottomRight - tl));
            if (FindAlignment(bits, estimate, module) is Vector2 alignment)
            {
                fourth = alignment;
                target = dimension - 6.5f;
            }
        }

        var transform = Perspective.QuadToQuad(
            [new(3.5f, 3.5f), new(dimension - 3.5f, 3.5f), new(target, target), new(3.5f, dimension - 3.5f)],
            [tl, tr, fourth, bl]);
        return transform.Sample(bits, dimension, dimension) is BarcodeModules grid ? (grid, transform) : null;
    }

    /// <summary>Searches around an estimate for an alignment pattern: a dark module in a light ring in a dark ring (1:1:1 runs).</summary>
    private static Vector2? FindAlignment(BitImage bits, Vector2 estimate, float module)
    {
        for (int radius = 4; radius <= 16; radius *= 2)
        {
            int allowance = (int)(radius * module);
            int left = Math.Max(0, (int)(estimate.X - allowance)), right = Math.Min(bits.Width - 1, (int)(estimate.X + allowance));
            int top = Math.Max(0, (int)(estimate.Y - allowance)), bottom = Math.Min(bits.Height - 1, (int)(estimate.Y + allowance));
            Vector2? best = null;
            float bestDistance = float.MaxValue;
            for (int y = top; y <= bottom; y++)
            {
                int x = left;
                while (x <= right)
                {
                    // Light, dark, light runs of about one module each, with the center run dark.
                    if (bits[x, y] && (x == left || !bits[x - 1, y]))
                    {
                        int start = x;
                        while (x <= right && bits[x, y])
                        {
                            x++;
                        }

                        float run = x - start;
                        if (MathF.Abs(run - module) <= module * 0.6f && LightAround(bits, start, x, y, module))
                        {
                            float cx = (start + x) / 2f;
                            if (CrossCheckAlignment(bits, cx, y, module) is float cy)
                            {
                                var p = new Vector2(cx, cy);
                                float d = Vector2.DistanceSquared(p, estimate);
                                if (d < bestDistance)
                                {
                                    (best, bestDistance) = (p, d);
                                }
                            }
                        }
                    }
                    else
                    {
                        x++;
                    }
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    private static bool LightAround(BitImage bits, int start, int end, int y, float module)
    {
        int reach = Math.Max(1, (int)MathF.Round(module * 0.6f));
        for (int k = 1; k <= reach; k++)
        {
            if (bits.IsDark(start - k, y) || bits.IsDark(end - 1 + k, y))
            {
                return false;
            }
        }

        return bits.IsDark(start - (int)MathF.Round(module * 1.5f), y) && bits.IsDark(end - 1 + (int)MathF.Round(module * 1.5f), y);
    }

    private static float? CrossCheckAlignment(BitImage bits, float cx, int y, float module)
    {
        int x = (int)cx, up = y, down = y;
        while (bits.IsDark(x, up - 1))
        {
            up--;
        }

        while (bits.IsDark(x, down + 1))
        {
            down++;
        }

        float run = down - up + 1;
        return MathF.Abs(run - module) <= module * 0.6f && !bits.IsDark(x, up - 1) && !bits.IsDark(x, down + 1) ? (up + down) / 2f + 0.5f : null;
    }

    /// <summary>The module size from the finders' black-white-black runs toward each other.</summary>
    private static float ModuleSize(BitImage bits, Finder topLeft, Finder topRight, Finder bottomLeft)
    {
        float a = (RunsToward(bits, topLeft.Center, topRight.Center) + RunsToward(bits, topRight.Center, topLeft.Center)) / 14;
        float b = (RunsToward(bits, topLeft.Center, bottomLeft.Center) + RunsToward(bits, bottomLeft.Center, topLeft.Center)) / 14;
        float estimate = (topLeft.Module + topRight.Module + bottomLeft.Module) / 3;
        float measured = a > 0 && b > 0 ? (a + b) / 2 : estimate;
        return MathF.Abs(measured - estimate) > estimate * 0.5f ? estimate : measured;
    }

    /// <summary>The length from a finder's center through its dark core, light ring and dark ring along a line, both ways: about 7 modules.</summary>
    private static float RunsToward(BitImage bits, Vector2 from, Vector2 to)
    {
        float total = 0;
        foreach (float sign in (ReadOnlySpan<float>)[1, -1])
        {
            Vector2 step = Vector2.Normalize(to - from) * sign;
            int state = 0;
            float distance = 0;
            for (int i = 0; i < 400; i++)
            {
                Vector2 p = from + (step * i);
                bool dark = bits.IsDark((int)p.X, (int)p.Y);
                if ((state == 0 && !dark) || (state == 1 && dark) || (state == 2 && !dark))
                {
                    state++;
                }

                if (state == 3)
                {
                    distance = i;
                    break;
                }
            }

            if (distance == 0)
            {
                return 0;
            }

            total += distance;
        }

        return total;
    }

    // ---- Finder patterns ----

    internal static List<Finder> FindFinders(BitImage bits, int effort)
    {
        var finders = new List<Finder>();
        int step = effort >= 7 ? 1 : Math.Max(1, Math.Min(3, bits.Height / 200));
        var runs = new List<float>();
        var offsets = new List<float>();
        Span<int> counts = stackalloc int[5];
        for (int y = step - 1; y < bits.Height; y += step)
        {
            // Runs start light; windows of five runs from each dark run.
            Binarizer.BitRuns(bits.Row(y), runs, offsets);
            for (int i = 1; i + 4 < runs.Count; i += 2)
            {
                for (int k = 0; k < 5; k++)
                {
                    counts[k] = (int)runs[i + k];
                }

                if (Ratio(counts))
                {
                    Confirm(bits, counts, (int)offsets[i + 5], y, finders);
                }
            }
        }

        // Keep centers found on more than one row (or all, when few are found), most found first.
        List<Finder> confirmed = [.. finders.Where(f => f.Count >= 2)];
        if (confirmed.Count < 3)
        {
            confirmed = finders;
        }

        return [.. confirmed.OrderByDescending(f => f.Count)];
    }

    /// <summary>Whether five runs are 1:1:3:1:1, each within half a module (the center within a module and a half).</summary>
    private static bool Ratio(ReadOnlySpan<int> counts)
    {
        int total = 0;
        foreach (int c in counts)
        {
            if (c == 0)
            {
                return false;
            }

            total += c;
        }

        if (total < 7)
        {
            return false;
        }

        float module = total / 7f, tolerance = module * 0.5f;
        return MathF.Abs(module - counts[0]) < tolerance && MathF.Abs(module - counts[1]) < tolerance
            && MathF.Abs((3 * module) - counts[2]) < 3 * tolerance
            && MathF.Abs(module - counts[3]) < tolerance && MathF.Abs(module - counts[4]) < tolerance;
    }

    private static bool Confirm(BitImage bits, ReadOnlySpan<int> counts, int end, int y, List<Finder> finders)
    {
        int total = counts[0] + counts[1] + counts[2] + counts[3] + counts[4];
        float cx = end - counts[4] - counts[3] - (counts[2] / 2f);
        if (CrossCheck(bits, (int)cx, y, total, vertical: true) is not float cy)
        {
            return false;
        }

        if (CrossCheck(bits, (int)cy, (int)cx, total, vertical: false) is not float refinedX)
        {
            return false;
        }

        float module = total / 7f;
        var center = new Vector2(refinedX, cy);
        foreach (Finder f in finders)
        {
            if (Vector2.Distance(f.Center, center) <= module * 1.5f && MathF.Abs(f.Module - module) <= module * 0.5f)
            {
                f.Center = ((f.Center * f.Count) + center) / (f.Count + 1);
                f.Module = ((f.Module * f.Count) + module) / (f.Count + 1);
                f.Count++;
                return true;
            }
        }

        finders.Add(new Finder(center, module));
        return true;
    }

    /// <summary>Checks the 1:1:3:1:1 pattern across a line through a point and returns the refined center along it.</summary>
    private static float? CrossCheck(BitImage bits, int across, int along, int expected, bool vertical)
    {
        bool Dark(int i) => vertical ? bits.IsDark(across, i) : bits.IsDark(i, across);
        Span<int> counts = stackalloc int[5];
        int i = along;
        if (!Dark(i))
        {
            return null;
        }

        while (Dark(i))
        {
            counts[2]++;
            i--;
        }

        while (i >= 0 && !Dark(i) && counts[1] <= expected)
        {
            counts[1]++;
            i--;
        }

        while (i >= 0 && Dark(i) && counts[0] <= expected)
        {
            counts[0]++;
            i--;
        }

        int limit = vertical ? bits.Height : bits.Width;
        i = along + 1;
        while (i < limit && Dark(i))
        {
            counts[2]++;
            i++;
        }

        while (i < limit && !Dark(i) && counts[3] <= expected)
        {
            counts[3]++;
            i++;
        }

        while (i < limit && Dark(i) && counts[4] <= expected)
        {
            counts[4]++;
            i++;
        }

        int total = counts[0] + counts[1] + counts[2] + counts[3] + counts[4];
        if (MathF.Abs(total - expected) > expected * 0.4f || !Ratio(counts))
        {
            return null;
        }

        return i - counts[4] - counts[3] - (counts[2] / 2f);
    }
}
