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

            // The alignment patterns near where the bottom-right one should be, nearest first (under strong perspective the
            // nearest can be another symbol module ring), or the parallelogram estimate; then nearby positions of the fourth
            // point for perspective.
            List<Vector2?> firsts = [null];
            if (dimension >= 25)
            {
                Vector2 bottomRight = topRight.Center - topLeft.Center + bottomLeft.Center;
                float correction = 1 - (3f / (dimension - 7));
                List<Vector2> alignments = FindAlignments(bits, topLeft.Center + (correction * (bottomRight - topLeft.Center)), module);
                if (alignments.Count > 0)
                {
                    // The patterns found, then around the nearest one a quarter module at a time: on a curled symbol its
                    // measured center can be a pixel off where the grid needs it.
                    firsts = [.. alignments.Take(6).Select(p => (Vector2?)p)];
                    for (int k = 1; k < 25; k++)
                    {
                        var nudge = new Vector2((k % 5) - 2, (k / 5) - 2) * (module * 0.25f);
                        if (nudge != Vector2.Zero)
                        {
                            firsts.Add(alignments[0] + nudge);
                        }
                    }
                }
            }

            for (int attempt = 0; attempt < Shifts.Length; attempt++)
            {
                Vector2 shift = Shifts[attempt] * (module * 0.75f);
                if (attempt > 0 && shift == Vector2.Zero)
                {
                    continue;
                }

                foreach (Vector2? alignment in attempt == 0 ? firsts : [null])
                {
                    if (Sample(bits, topLeft, topRight, bottomLeft, dimension, attempt == 0 ? null : shift, alignment) is (BarcodeModules grid, Perspective transform)
                        && QrDecoder.Decode(grid) is DecodedData data)
                    {
                        Vector2[] corners = [transform.Map(0, 0), transform.Map(dimension, 0), transform.Map(dimension, dimension), transform.Map(0, dimension)];
                        return new Found(data, corners);
                    }
                }

                if (attempt == 0 && dimension >= 25 && delta != 0)
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the timing patterns (row and column 6, alternating dark and light between the finders) are mostly right under a
    /// transform: one placed off the symbol gets about half of them wrong, and sampling and decoding its grid would only fail
    /// later. The mirrored grid has the same timing modules.
    /// </summary>
    private static bool TimingLooksRight(BitImage bits, Perspective transform, int size)
    {
        int wrong = 0;
        for (int i = 8; i < size - 8; i++)
        {
            bool dark = (i & 1) == 0;
            Vector2 p = transform.Map(i + 0.5f, 6.5f), q = transform.Map(6.5f, i + 0.5f);
            wrong += (bits.IsDark((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)) != dark ? 1 : 0) + (bits.IsDark((int)MathF.Floor(q.X), (int)MathF.Floor(q.Y)) != dark ? 1 : 0);
        }

        return wrong * 3 <= (size - 16) * 2;
    }

    /// <summary>The shifts of the fourth point tried, in steps of three quarters of a module: the 5 x 5 around the estimate.</summary>
    private static readonly Vector2[] Shifts = [.. Enumerable.Range(0, 25).Select(a => new Vector2((a % 5) - 2, (a / 5) - 2))];

    /// <summary>Rounds to the nearest QR size: 17 plus a multiple of 4.</summary>
    private static int Dimension(int estimate) => (estimate & 3) switch
    {
        0 => estimate + 1,
        2 => estimate - 1,
        3 => estimate + 2,
        _ => estimate,
    };

    /// <summary>
    /// Samples the grid through the finders and a fourth point: the bottom-right alignment pattern when given (from version 2),
    /// else the parallelogram's corner, shifted when asked.
    /// </summary>
    private static (BarcodeModules, Perspective)? Sample(BitImage bits, Finder topLeft, Finder topRight, Finder bottomLeft, int dimension, Vector2? shift, Vector2? alignment)
    {
        Vector2 tl = topLeft.Center, tr = topRight.Center, bl = bottomLeft.Center;
        Vector2 fourth = alignment ?? (tr - tl + bl + (shift ?? Vector2.Zero));
        float target = alignment is null ? dimension - 3.5f : dimension - 6.5f;
        var transform = Perspective.QuadToQuad(
            [new(3.5f, 3.5f), new(dimension - 3.5f, 3.5f), new(target, target), new(3.5f, dimension - 3.5f)],
            [tl, tr, fourth, bl]);
        return TimingLooksRight(bits, transform, dimension) && transform.Sample(bits, dimension, dimension) is BarcodeModules grid ? (grid, transform) : null;
    }

    /// <summary>
    /// Searches around an estimate for alignment patterns: a dark module in a light ring in a dark ring (1:1:1 runs). Returns
    /// those of the smallest search square that has any, nearest first, one per pattern.
    /// </summary>
    private static List<Vector2> FindAlignments(BitImage bits, Vector2 estimate, float module)
    {
        var found = new List<Vector2>();
        for (int radius = 4; radius <= 16 && found.Count == 0; radius *= 2)
        {
            int allowance = (int)(radius * module);
            int left = Math.Max(0, (int)(estimate.X - allowance)), right = Math.Min(bits.Width - 1, (int)(estimate.X + allowance));
            int top = Math.Max(0, (int)(estimate.Y - allowance)), bottom = Math.Min(bits.Height - 1, (int)(estimate.Y + allowance));
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
                                found.Add(new Vector2(cx, cy));
                            }
                        }
                    }
                    else
                    {
                        x++;
                    }
                }
            }
        }

        // Nearest first (ties in the order found, as before), then one per pattern: the rows through one pattern find it
        // several times.
        var ordered = found.Select((p, i) => (p, i)).OrderBy(t => Vector2.DistanceSquared(t.p, estimate)).ThenBy(t => t.i).Select(t => t.p).ToList();
        var distinct = new List<Vector2>();
        foreach (Vector2 p in ordered)
        {
            if (!distinct.Any(q => Vector2.Distance(p, q) < module))
            {
                distinct.Add(p);
            }
        }

        return distinct;
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
        int[] runs = new int[bits.Width + 2];
        for (int y = step - 1; y < bits.Height; y += step)
        {
            // Runs start light; windows of five runs from each dark run.
            int count = Binarizer.IntRuns(bits.Row(y), runs);
            int at = runs[0];
            for (int i = 1; i + 4 < count; i += 2)
            {
                ReadOnlySpan<int> counts = runs.AsSpan(i, 5);
                if (Ratio(counts))
                {
                    Confirm(bits, counts, at + counts[0] + counts[1] + counts[2] + counts[3] + counts[4], y, finders);
                }

                at += runs[i] + runs[i + 1];
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
