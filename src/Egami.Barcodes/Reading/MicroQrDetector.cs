using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Finds Micro QR Codes in a binarized image. Each QR finder pattern may be a Micro QR symbol's one finder: the outer edge of
/// its dark ring, found along rays from its center and fitted with a line per side, gives its four corners; each corner in turn
/// is taken as the symbol's top left, the timing patterns that go on from the finder along the two sides that meet there give
/// the symbol's size (11 to 17 modules), and the grid is sampled through the finder's corners.
/// </summary>
internal static class MicroQrDetector
{
    private const int Rays = 72;

    /// <summary>The rays' directions, every 5 degrees.</summary>
    private static readonly Vector2[] Directions = [.. Enumerable.Range(0, Rays).Select(r => new Vector2(MathF.Cos(r * MathF.Tau / Rays), MathF.Sin(r * MathF.Tau / Rays)))];

    /// <summary>The larger of |cos| and |sin| for each whole degree from 0 to 89: a square's edge is its inradius over this.</summary>
    private static readonly float[] SquareFactor = [.. Enumerable.Range(0, 90).Select(d => MathF.Max(MathF.Abs(MathF.Cos(d * MathF.PI / 180)), MathF.Abs(MathF.Sin(d * MathF.PI / 180))))];

    /// <summary>The points each module is sampled at, in modules from its top left.</summary>
    private static readonly Vector2[] Votes = [new(0.5f, 0.5f), new(0.25f, 0.5f), new(0.75f, 0.5f), new(0.5f, 0.25f), new(0.5f, 0.75f)];

    /// <summary>The far corner's moves, in half modules: none first, then the 5 x 5 around it.</summary>
    private static readonly Vector2[] Shifts = [.. Enumerable.Range(0, 25).Select(a => new Vector2((a % 5) - 2, (a / 5) - 2)).OrderBy(v => v.LengthSquared())];

    public static List<Found> Detect(BitImage bits, IReadOnlyList<QrDetector.Finder> finders, Vector2 offset)
    {
        var results = new List<Found>();
        foreach (QrDetector.Finder finder in finders.Take(60))
        {
            if (results.Any(r => Search.Inside(r.Corners, finder.Center + offset)))
            {
                continue;
            }

            if (Read(bits, finder.Center, finder.Module) is Found found)
            {
                results.Add(found with { Corners = [.. found.Corners.Select(p => p + offset)] });
            }
        }

        return results;
    }

    private static Found? Read(BitImage bits, Vector2 center, float module)
    {
        if (Corners(bits, center, module) is not Vector2[] quad)
        {
            return null;
        }

        for (int corner = 0; corner < 4; corner++)
        {
            var transform = Perspective.QuadToQuad(
                [new(0, 0), new(7, 0), new(7, 7), new(0, 7)],
                [quad[corner], quad[(corner + 1) & 3], quad[(corner + 2) & 3], quad[(corner + 3) & 3]]);

            // The timing patterns along row 0 and column 0 give the size and where the symbol ends on each; one that cannot be
            // followed takes the other's.
            (int, float)? rowTiming = Timing(bits, transform, row: true), columnTiming = Timing(bits, transform, row: false);
            if ((rowTiming ?? columnTiming) is not (int rowDarks, float rowEnd) || (columnTiming ?? rowTiming) is not (int columnDarks, float columnEnd))
            {
                continue;
            }

            foreach (int darks in rowDarks == columnDarks ? [rowDarks] : (ReadOnlySpan<int>)[Math.Max(rowDarks, columnDarks), Math.Min(rowDarks, columnDarks)])
            {
                int size = 7 + (2 * darks);
                if (size is < 11 or > 17)
                {
                    continue;
                }

                // The finder's mapping stretched so that the timing patterns end where they were seen to (a pattern of another
                // length, damaged or run into the background, takes the other's end), with the far corner moved by up to a
                // module in half-module steps for perspective that the finder's corners cannot show.
                float row = rowDarks == darks ? rowEnd : columnEnd, column = columnDarks == darks ? columnEnd : rowEnd;
                if (!FormatFits(bits, transform, size))
                {
                    continue;
                }

                foreach (Vector2 shift in Shifts)
                {
                    Vector2 far = new Vector2(row, column) + (shift * 0.5f);
                    var sampling = Perspective.QuadToQuad(
                        [new(0, 0), new(size, 0), new(size, size), new(0, size)],
                        [transform.Map(0, 0), transform.Map(row, 0), transform.Map(far.X, far.Y), transform.Map(0, column)]);
                    if (Sample(bits, sampling, size) is BarcodeModules modules && MicroQrDecoder.Decode(modules) is DecodedData data)
                    {
                        return new Found(data, [sampling.Map(0, 0), sampling.Map(size, 0), sampling.Map(size, size), sampling.Map(0, size)]);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the format modules next to the finder, as they are or mirrored, are within 3 bits of a format word for a symbol
    /// of the size: the format area is too near the finder for the far corner's moves to change it, so a corner and size it
    /// rules out need not be sampled whole.
    /// </summary>
    private static bool FormatFits(BitImage bits, Perspective transform, int size)
    {
        int word = 0, mirrored = 0;
        for (int i = 1; i <= 8; i++)
        {
            word = (word << 1) | (Module(bits, transform, i, 8) ? 1 : 0);
            mirrored = (mirrored << 1) | (Module(bits, transform, 8, i) ? 1 : 0);
        }

        for (int i = 7; i >= 1; i--)
        {
            word = (word << 1) | (Module(bits, transform, 8, i) ? 1 : 0);
            mirrored = (mirrored << 1) | (Module(bits, transform, i, 8) ? 1 : 0);
        }

        int version = (size - 9) / 2;
        for (int candidate = 0; candidate < 32; candidate++)
        {
            int number = candidate >> 2;
            if ((number == 0 ? 1 : number == 7 ? 4 : ((number - 1) / 2) + 2) != version)
            {
                continue;
            }

            int format = Matrix.MicroQrEncoder.FormatBits(number, candidate & 3);
            if (BitOperations.PopCount((uint)(format ^ word)) <= 3 || BitOperations.PopCount((uint)(format ^ mirrored)) <= 3)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One module by the majority of its five sample points.</summary>
    private static bool Module(BitImage bits, Perspective transform, int x, int y)
    {
        int votes = 0;
        foreach (Vector2 offset in Votes)
        {
            Vector2 p = transform.Map(x + offset.X, y + offset.Y);
            votes += bits.IsDark((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)) ? 1 : 0;
        }

        return votes >= 3;
    }

    /// <summary>
    /// Samples the grid: each module by the majority of five points, its center and four a quarter module from it, as small
    /// modules in a noisy image are a few pixels each.
    /// </summary>
    private static BarcodeModules? Sample(BitImage bits, Perspective transform, int size)
    {
        bool[] cells = new bool[size * size];
        int outside = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int votes = 0;
                foreach (Vector2 offset in Votes)
                {
                    Vector2 p = transform.Map(x + offset.X, y + offset.Y);
                    int px = (int)MathF.Floor(p.X), py = (int)MathF.Floor(p.Y);
                    if ((uint)px >= (uint)bits.Width || (uint)py >= (uint)bits.Height)
                    {
                        outside++;
                        continue;
                    }

                    votes += bits[px, py] ? 1 : 0;
                }

                cells[(y * size) + x] = votes >= 3;
            }
        }

        return outside > size * size / 10 ? null : BarcodeModules.Wrap(size, size, cells);
    }

    /// <summary>
    /// Follows a timing pattern from the finder's separator, in steps of a tenth of a module along row 0 or column 0, over runs
    /// of about one module each: returns how many dark modules it has and where its last ends, or null when it does not
    /// alternate. Each step takes the majority of three points across the row (or column), and a change of shade counts only
    /// when it lasts three steps, so that noise and a module's blurred corners do not split a run. It ends at a light run longer
    /// than one and a half modules or at a long dark run.
    /// </summary>
    internal static (int Darks, float End)? Timing(BitImage bits, Perspective transform, bool row)
    {
        const float Step = 0.1f;
        const int Settle = 3;
        int darks = 0, pending = 0;
        float runStart = 7, end = 0;
        bool dark = false;
        for (float t = 7 + (Step / 2); t < 20; t += Step)
        {
            int votes = 0;
            foreach (float across in (ReadOnlySpan<float>)[0.3f, 0.5f, 0.7f])
            {
                Vector2 p = row ? transform.Map(t, across) : transform.Map(across, t);
                votes += bits.IsDark((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)) ? 1 : 0;
            }

            if ((votes >= 2) != dark && ++pending < Settle)
            {
                continue;
            }

            if ((votes >= 2) == dark)
            {
                pending = 0;
                float length = t - runStart;
                if (length > 1.6f && (dark || darks > 0))
                {
                    return darks > 0 ? (darks, end) : null;
                }

                continue;
            }

            // The change began Settle steps back.
            float at = t - (Settle * Step) + (Step / 2);
            if (at - runStart < 0.4f && darks > 0)
            {
                return null;
            }

            // A dark run that ends within half a module of the finder is the finder's own edge.
            if (dark && at > 7.5f)
            {
                darks++;
                end = at;
            }

            (dark, runStart, pending) = (!dark, at, 0);
        }

        return darks > 0 ? (darks, end) : null;
    }

    /// <summary>
    /// Returns the finder's outer corners in order around it: the edge of its dark ring along rays from the center, the square's
    /// turn from where those edges are nearest a square, and a line fitted to each side's edges away from the corners.
    /// </summary>
    internal static Vector2[]? Corners(BitImage bits, Vector2 center, float module)
    {
        Span<float> radius = stackalloc float[Rays];
        Span<Vector2> edge = stackalloc Vector2[Rays];
        float step = MathF.Max(0.25f, module / 8), limit = module * 7;
        int settle = Math.Max(1, (int)MathF.Round(module / 4 / step));
        int found = 0;
        for (int r = 0; r < Rays; r++)
        {
            Vector2 direction = Directions[r];
            radius[r] = -1;
            int state = 0;
            // A change of shade counts once it lasts a quarter module, so that noise does not end a ring early.
            int pending = 0;
            for (float t = 0; t <= limit; t += step)
            {
                Vector2 p = center + (direction * t);
                bool dark = bits.IsDark((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));
                if (dark == (state is 0 or 2))
                {
                    pending = 0;
                    continue;
                }

                if (++pending < settle)
                {
                    continue;
                }

                pending = 0;
                if (++state == 3)
                {
                    radius[r] = t - (settle * step) + (step / 2);
                    edge[r] = center + (direction * radius[r]);
                    found++;
                    break;
                }
            }
        }

        if (found < Rays * 3 / 4)
        {
            return null;
        }

        // The turn: the radius times the larger of |cos| and |sin| from each side's normal is the same on a square's edge.
        float bestTurn = 0, bestSpread = float.MaxValue;
        for (int degrees = 0; degrees < 90; degrees++)
        {
            float turn = degrees * MathF.PI / 180, sum = 0, sumSquares = 0;
            int n = 0;
            for (int r = 0; r < Rays; r++)
            {
                if (radius[r] < 0)
                {
                    continue;
                }

                float q = radius[r] * SquareFactor[(((r * (360 / Rays)) - degrees) % 90 + 90) % 90];
                (sum, sumSquares, n) = (sum + q, sumSquares + (q * q), n + 1);
            }

            float spread = (sumSquares / n) - ((sum / n) * (sum / n));
            if (spread < bestSpread)
            {
                (bestTurn, bestSpread) = (turn, spread);
            }
        }

        // A line through each side's edges, leaving out the rays within 12 degrees of a corner.
        Span<Vector2> points = stackalloc Vector2[Rays];
        Span<(Vector2 Point, Vector2 Direction)> lines = stackalloc (Vector2, Vector2)[4];
        for (int side = 0; side < 4; side++)
        {
            int count = 0;
            for (int r = 0; r < Rays; r++)
            {
                float off = MathF.IEEERemainder((r * MathF.Tau / Rays) - bestTurn - (side * MathF.PI / 2), MathF.Tau);
                if (radius[r] >= 0 && MathF.Abs(off) < 33 * MathF.PI / 180)
                {
                    points[count++] = edge[r];
                }
            }

            if (count < 3 || Fit(points[..count]) is not { } line)
            {
                return null;
            }

            lines[side] = line;
        }

        Vector2[] corners = new Vector2[4];
        for (int side = 0; side < 4; side++)
        {
            if (Intersect(lines[side], lines[(side + 1) & 3]) is not Vector2 corner || Vector2.Distance(corner, center) > module * 7)
            {
                return null;
            }

            corners[side] = corner;
        }

        return corners;
    }

    /// <summary>
    /// The line through points by least squares, fitted again without the points more than two and a half times the median
    /// distance from it (and more than a pixel), which noise put off the edge.
    /// </summary>
    private static (Vector2 Point, Vector2 Direction)? Fit(Span<Vector2> points)
    {
        if (FitOnce(points) is not { } line)
        {
            return null;
        }

        Span<float> distances = stackalloc float[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 d = points[i] - line.Point;
            distances[i] = MathF.Abs((d.X * line.Direction.Y) - (d.Y * line.Direction.X));
        }

        Span<float> sorted = stackalloc float[points.Length];
        distances.CopyTo(sorted);
        sorted.Sort();
        float limit = MathF.Max(1, 2.5f * sorted[sorted.Length / 2]);
        int kept = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (distances[i] <= limit)
            {
                points[kept++] = points[i];
            }
        }

        return kept >= 3 && kept < points.Length ? FitOnce(points[..kept]) : line;
    }

    private static (Vector2 Point, Vector2 Direction)? FitOnce(ReadOnlySpan<Vector2> points)
    {
        Vector2 mean = Vector2.Zero;
        foreach (Vector2 p in points)
        {
            mean += p;
        }

        mean /= points.Length;
        float xx = 0, xy = 0, yy = 0;
        foreach (Vector2 p in points)
        {
            Vector2 d = p - mean;
            (xx, xy, yy) = (xx + (d.X * d.X), xy + (d.X * d.Y), yy + (d.Y * d.Y));
        }

        float angle = 0.5f * MathF.Atan2(2 * xy, xx - yy);
        return xx + yy > 0 ? (mean, new Vector2(MathF.Cos(angle), MathF.Sin(angle))) : null;
    }

    private static Vector2? Intersect((Vector2 Point, Vector2 Direction) a, (Vector2 Point, Vector2 Direction) b)
    {
        float cross = (a.Direction.X * b.Direction.Y) - (a.Direction.Y * b.Direction.X);
        if (MathF.Abs(cross) < 0.2f)
        {
            return null;
        }

        Vector2 d = b.Point - a.Point;
        float t = ((d.X * b.Direction.Y) - (d.Y * b.Direction.X)) / cross;
        return a.Point + (a.Direction * t);
    }
}
