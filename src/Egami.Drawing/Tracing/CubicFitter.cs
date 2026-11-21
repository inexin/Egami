namespace Egami.Drawing.Tracing;

/// <summary>How the cubic fitter treats corners, tolerance and snapping.</summary>
/// <param name="Tolerance">The largest distance, in pixels, a fitted curve may stray from the points it replaces.</param>
/// <param name="CornerAngle">The turn, in radians, above which a point is a corner.</param>
/// <param name="Lines">Whether straight runs become lines.</param>
/// <param name="Arcs">Whether runs on a circle become circular arcs, and round outlines circles.</param>
/// <param name="Reach">How many points either side a corner's turn is measured over: about one point per pixel gives 3.</param>
internal sealed record CurveSettings(double Tolerance, double CornerAngle, bool Lines, bool Arcs, int Reach = 3)
{
    /// <summary>The distance within which points count as on one line or one circle.</summary>
    internal double SnapTolerance => Math.Min(0.3, 0.6 * Tolerance);

    /// <summary>The settings a <see cref="TraceOptions"/> sets.</summary>
    internal static CurveSettings From(TraceOptions options)
    {
        double f = options.PathFidelity, c = options.Corners;
        double tolerance = 0.2 + (1.2 * (1 - f) * (1 - f));

        // 180 degrees (no corners) at 0, 60 at the default 0.25, 0 (every bend a corner) at 1.
        double degrees = c <= 0.25 ? 180 - (480 * c) : 60 * (1 - c) / 0.75;
        return new CurveSettings(tolerance, degrees * Math.PI / 180, options.SnapToLines, options.SnapToArcs);
    }
}

/// <summary>
/// Fits lines and cubic Béziers to a run of boundary points, written from P. J. Schneider, "An algorithm for automatically fitting
/// digitized curves" (Graphics Gems, 1990): corners split the run, each piece is fitted by least squares with its end tangents fixed,
/// reparameterized by Newton steps, and split at its worst point until it is within tolerance. Corners are restored to where the
/// lines on either side meet; straight pieces snap to lines and pieces on a circle to circular arcs.
/// </summary>
internal static class CubicFitter
{
    private const int Reach = 3;

    /// <summary>Fits an open run whose first and last points stay fixed (the junctions a shared edge runs between).</summary>
    internal static FittedCurve FitOpen(Vec[] points, CurveSettings settings)
    {
        int n = points.Length;
        List<CurveSegment> segments = [];
        if (n < 2)
        {
            return new FittedCurve(points.Length > 0 ? points[0] : default, segments, false);
        }

        List<int> breaks = [0];
        breaks.AddRange(Corners(points, false, settings.CornerAngle, settings.Reach));
        breaks.Add(n - 1);
        Vec[] p = Prepare(points, false, breaks, settings.Reach);
        for (int k = 0; k + 1 < breaks.Count; k++)
        {
            FitSpan(p[breaks[k]..(breaks[k + 1] + 1)], settings, segments);
        }

        return new FittedCurve(p[0], segments, false);
    }

    /// <summary>
    /// A loop this short (a speck of up to two pixels) has too few points to place corners; it is traced as its pixel edges.
    /// </summary>
    internal const int Speck = 8;

    /// <summary>The outline of a speck: its pixel edges, one line per straight stretch.</summary>
    internal static FittedCurve Polygon(ReadOnlySpan<int> x, ReadOnlySpan<int> y, ReadOnlySpan<byte> directions)
    {
        int n = directions.Length;
        List<CurveSegment> segments = [];
        int first = -1;
        for (int i = 0; i < n; i++)
        {
            if (directions[i] != directions[(i + n - 1) % n])
            {
                first = first < 0 ? i : first;
                if (segments.Count > 0 || first != i)
                {
                    segments.Add(new CurveSegment(false, default, default, new Vec(x[i], y[i])));
                }
            }
        }

        Vec start = new(x[first], y[first]);
        segments.Add(new CurveSegment(false, default, default, start));
        return new FittedCurve(start, segments, true);
    }

    /// <summary>Fits a closed outline.</summary>
    internal static FittedCurve FitClosed(Vec[] points, CurveSettings settings)
    {
        int n = points.Length;
        List<CurveSegment> segments = [];
        if (n < 4)
        {
            foreach (Vec point in points[1..])
            {
                segments.Add(new CurveSegment(false, default, default, point));
            }

            segments.Add(new CurveSegment(false, default, default, points[0]));
            return new FittedCurve(points[0], segments, true);
        }

        List<int> corners = Corners(points, true, settings.CornerAngle, settings.Reach);
        if (corners.Count == 0 && settings.CornerAngle <= 0)
        {
            corners.Add(0);
        }

        Vec[] p = Prepare(points, true, corners, settings.Reach);
        if (corners.Count == 0)
        {
            if (settings.Arcs && TryCircle(p, settings.SnapTolerance, segments, out Vec start))
            {
                return new FittedCurve(start, segments, true);
            }

            // Two smooth halves, with matching tangents where they meet.
            int half = n / 2;
            Vec t0 = Tangent(p, 0), th = Tangent(p, half);
            Schneider(p[..(half + 1)], t0, -th, settings, segments);
            Schneider([.. p[half..], p[0]], th, -t0, settings, segments);
            return new FittedCurve(p[0], segments, true);
        }

        // Start at the first corner and fit corner to corner, around.
        for (int k = 0; k < corners.Count; k++)
        {
            int from = corners[k], to = corners[(k + 1) % corners.Count];
            int length = ((to - from + n - 1) % n) + 1;
            Vec[] span = new Vec[length + 1];
            for (int i = 0; i <= length; i++)
            {
                span[i] = p[(from + i) % n];
            }

            FitSpan(span, settings, segments);
        }

        return new FittedCurve(p[corners[0]], segments, true);
    }

    /// <summary>
    /// The corners of a run: points where the direction turns by more than <paramref name="cornerAngle"/> over a few points on
    /// either side, the sharpest of any close together.
    /// </summary>
    internal static List<int> Corners(Vec[] p, bool closed, double cornerAngle, int reach = Reach)
    {
        int n = p.Length;
        List<int> corners = [];
        if (n < 3 || cornerAngle >= Math.PI)
        {
            return corners;
        }

        // The turn over a few points either side and over twice as many: a real corner turns at both reaches, a staircase step or a
        // speck of noise only at the shorter one.
        double[] turn = new double[n];
        for (int i = 0; i < n; i++)
        {
            turn[i] = Math.Min(Turn(p, closed, i, reach), Turn(p, closed, i, 2 * reach));
        }

        for (int i = 0; i < n; i++)
        {
            if (turn[i] <= cornerAngle || turn[i] < 1e-6)
            {
                continue;
            }

            bool sharpest = true;
            for (int j = -reach; j <= reach && sharpest; j++)
            {
                int other = i + j;
                if (closed)
                {
                    other = (other + n) % n;
                }
                else if (other < 0 || other >= n)
                {
                    continue;
                }

                // Only a sharper point close by wins: the two corners at the end of a line a pixel wide both stay.
                if (j != 0 && (turn[other] > turn[i] || (turn[other] == turn[i] && j < 0)) && (p[other] - p[i]).Length < 0.9)
                {
                    sharpest = false;
                }
            }

            if (sharpest)
            {
                corners.Add(i);
            }
        }

        return corners;
    }

    /// <summary>The angle the run turns through at point i, between the chords to the points <paramref name="reach"/> steps either side.</summary>
    private static double Turn(Vec[] p, bool closed, int i, int reach)
    {
        int n = p.Length;
        int k = closed ? Math.Min(reach, (n - 1) / 2) : Math.Min(reach, Math.Min(i, n - 1 - i));
        if (k < 1)
        {
            return 0;
        }

        Vec a = p[i] - p[closed ? (i - k + n) % n : i - k], b = p[closed ? (i + k) % n : i + k] - p[i];
        return Math.Atan2(Math.Abs(Vec.Cross(a, b)), Vec.Dot(a, b));
    }

    /// <summary>
    /// Smooths the points once with weights 1, 2, 1 (corners and ends stay), which evens out the half-pixel steps of a staircase,
    /// then moves each corner to where lines fitted to the points on either side of it meet.
    /// </summary>
    private static Vec[] Prepare(Vec[] points, bool closed, List<int> breaks, int reach)
    {
        int n = points.Length;
        bool[] fixedPoint = new bool[n];
        foreach (int b in breaks)
        {
            fixedPoint[b] = true;
        }

        Vec[] p = new Vec[n];
        for (int i = 0; i < n; i++)
        {
            // Corners, ends and their neighbours stay, so the points beside a corner keep to its sides.
            bool end = !closed && (i <= 1 || i >= n - 2);
            if (fixedPoint[i] || end || fixedPoint[(i + 1) % n] || fixedPoint[(i - 1 + n) % n])
            {
                p[i] = points[i];
                continue;
            }

            Vec before = points[(i - 1 + n) % n], after = points[(i + 1) % n];
            p[i] = (before + (2 * points[i]) + after) * 0.25;
        }

        // Dense points (several to a pixel) skip the first pixel either side, where an edge rounds into its corner.
        int side = (5 * reach) / 3, skip = (reach - 3) / 3;
        foreach (int c in breaks)
        {
            if (!closed && (c == 0 || c == n - 1))
            {
                continue;
            }

            // The points on each side, up to the next break.
            List<Vec> left = [], right = [];
            for (int j = 1; j <= side + skip; j++)
            {
                int i = closed ? (c - j + n) % n : c - j;
                if (i < 0 || fixedPoint[i])
                {
                    break;
                }

                if (j > skip)
                {
                    left.Add(p[i]);
                }
            }

            for (int j = 1; j <= side + skip; j++)
            {
                int i = closed ? (c + j) % n : c + j;
                if (i >= n || fixedPoint[i])
                {
                    break;
                }

                if (j > skip)
                {
                    right.Add(p[i]);
                }
            }

            if (left.Count >= 2 && right.Count >= 2 && Intersect(FitLine(left), FitLine(right), out Vec meet) && (meet - p[c]).Length <= 1.5)
            {
                p[c] = meet;
            }
        }

        return p;
    }

    /// <summary>Fits one piece between corners: a line, a circular arc or cubics.</summary>
    private static void FitSpan(Vec[] q, CurveSettings settings, List<CurveSegment> output)
    {
        int m = q.Length - 1;
        if (m < 1)
        {
            return;
        }

        if (settings.Lines && ChordDistance(q, 0, m) <= settings.SnapTolerance)
        {
            output.Add(new CurveSegment(false, default, default, q[m]));
            return;
        }

        if (m == 1)
        {
            // Two points and no lines wanted: a straight cubic.
            Vec third = (q[1] - q[0]) * (1.0 / 3);
            output.Add(settings.CornerAngle <= 0 ? new CurveSegment(false, default, default, q[1]) : new CurveSegment(true, q[0] + third, q[1] - third, q[1]));
            return;
        }

        if (settings.CornerAngle <= 0)
        {
            // Every corner kept: the outline is a polygon, its sides placed by Douglas and Peucker's simplification.
            Simplify(q, 0, m, settings.Tolerance, output);
            output.Add(new CurveSegment(false, default, default, q[m]));
            return;
        }

        if (settings.Arcs && m >= 5 && TryArc(q, settings.SnapTolerance, output))
        {
            return;
        }

        Vec t1 = (q[Math.Min(settings.Reach, m)] - q[0]).Normalize();
        Vec t2 = (q[m - Math.Min(settings.Reach, m)] - q[m]).Normalize();
        Schneider(q, t1, t2, settings, output);
    }

    /// <summary>Adds the inner points Douglas and Peucker's simplification keeps between two kept points, in order.</summary>
    private static void Simplify(Vec[] q, int first, int last, double tolerance, List<CurveSegment> output)
    {
        if (last - first < 2)
        {
            return;
        }

        Vec a = q[first], chord = q[last] - a;
        double length = chord.Length, worst = -1;
        int split = first;
        for (int i = first + 1; i < last; i++)
        {
            double distance = length < 1e-9 ? (q[i] - a).Length : Math.Abs(Vec.Cross(chord, q[i] - a)) / length;
            if (distance > worst)
            {
                (worst, split) = (distance, i);
            }
        }

        if (worst <= tolerance)
        {
            return;
        }

        Simplify(q, first, split, tolerance, output);
        output.Add(new CurveSegment(false, default, default, q[split]));
        Simplify(q, split, last, tolerance, output);
    }

    /// <summary>Schneider's fit of the whole run with the given end tangents, split at the worst point until within tolerance.</summary>
    private static void Schneider(Vec[] q, Vec t1, Vec t2, CurveSettings settings, List<CurveSegment> output)
    {
        double tolerance2 = settings.Tolerance * settings.Tolerance;
        Stack<(int First, int Last, Vec T1, Vec T2)> pending = new();
        pending.Push((0, q.Length - 1, t1, t2));
        while (pending.Count > 0)
        {
            (int first, int last, Vec left, Vec right) = pending.Pop();
            if (last - first == 1)
            {
                double third = (q[last] - q[first]).Length / 3;
                Emit(q, first, last, q[first] + (left * third), q[last] + (right * third), settings, output);
                continue;
            }

            double[] u = ChordLengths(q, first, last);
            (Vec c1, Vec c2) = Generate(q, first, last, u, left, right);
            (double error, int split) = MaxError(q, first, last, c1, c2, u);
            if (error >= tolerance2 && error < 4 * tolerance2)
            {
                for (int iteration = 0; iteration < 4 && error >= tolerance2; iteration++)
                {
                    Reparameterize(q, first, last, c1, c2, u);
                    (c1, c2) = Generate(q, first, last, u, left, right);
                    (error, split) = MaxError(q, first, last, c1, c2, u);
                }
            }

            if (error < tolerance2)
            {
                Emit(q, first, last, c1, c2, settings, output);
                continue;
            }

            Vec center = (q[split - 1] - q[split + 1]).Normalize();
            if (center == default)
            {
                Vec chord = (q[split + 1] - q[split]).Normalize();
                center = -chord;
            }

            // The left piece comes out first.
            pending.Push((split, last, -center, right));
            pending.Push((first, split, left, center));
        }
    }

    private static void Emit(Vec[] q, int first, int last, Vec c1, Vec c2, CurveSettings settings, List<CurveSegment> output)
    {
        if (settings.Lines && ChordDistance(q, first, last) <= settings.SnapTolerance && ChordDistance([q[first], c1, c2, q[last]], 0, 3) <= settings.SnapTolerance)
        {
            output.Add(new CurveSegment(false, default, default, q[last]));
        }
        else
        {
            output.Add(new CurveSegment(true, c1, c2, q[last]));
        }
    }

    /// <summary>The least-squares inner control points for fixed end points and tangent directions (Schneider's equations).</summary>
    private static (Vec C1, Vec C2) Generate(Vec[] q, int first, int last, double[] u, Vec t1, Vec t2)
    {
        Vec p0 = q[first], p3 = q[last];
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (int i = 0; i < u.Length; i++)
        {
            double t = u[i], s = 1 - t;
            double b0 = s * s * s, b1 = 3 * t * s * s, b2 = 3 * t * t * s, b3 = t * t * t;
            Vec a0 = t1 * b1, a1 = t2 * b2;
            c00 += Vec.Dot(a0, a0);
            c01 += Vec.Dot(a0, a1);
            c11 += Vec.Dot(a1, a1);
            Vec rest = q[first + i] - ((p0 * (b0 + b1)) + (p3 * (b2 + b3)));
            x0 += Vec.Dot(a0, rest);
            x1 += Vec.Dot(a1, rest);
        }

        double det = (c00 * c11) - (c01 * c01);
        double alpha1 = det == 0 ? 0 : ((x0 * c11) - (x1 * c01)) / det;
        double alpha2 = det == 0 ? 0 : ((c00 * x1) - (c01 * x0)) / det;
        double length = (p3 - p0).Length;
        double epsilon = 1e-6 * length;
        if (alpha1 < epsilon || alpha2 < epsilon || alpha1 > 2 * length || alpha2 > 2 * length)
        {
            // Wu and Barsky's fallback: a third of the chord along each tangent.
            alpha1 = alpha2 = length / 3;
        }

        return (p0 + (t1 * alpha1), p3 + (t2 * alpha2));
    }

    private static double[] ChordLengths(Vec[] q, int first, int last)
    {
        double[] u = new double[last - first + 1];
        for (int i = 1; i < u.Length; i++)
        {
            u[i] = u[i - 1] + (q[first + i] - q[first + i - 1]).Length;
        }

        double total = u[^1];
        for (int i = 1; i < u.Length; i++)
        {
            u[i] = total > 0 ? u[i] / total : (double)i / (u.Length - 1);
        }

        return u;
    }

    private static (double Error, int Split) MaxError(Vec[] q, int first, int last, Vec c1, Vec c2, double[] u)
    {
        double worst = 0;
        int split = (first + last) / 2;
        for (int i = 1; i < u.Length - 1; i++)
        {
            Vec d = Bezier(q[first], c1, c2, q[last], u[i]) - q[first + i];
            double error = Vec.Dot(d, d);
            if (error >= worst)
            {
                (worst, split) = (error, first + i);
            }
        }

        return (worst, split);
    }

    /// <summary>One Newton-Raphson step per point toward the parameter of its nearest point on the curve.</summary>
    private static void Reparameterize(Vec[] q, int first, int last, Vec c1, Vec c2, double[] u)
    {
        Vec p0 = q[first], p3 = q[last];
        for (int i = 1; i < u.Length - 1; i++)
        {
            double t = u[i], s = 1 - t;
            Vec point = Bezier(p0, c1, c2, p3, t);
            Vec d1 = (3 * s * s * (c1 - p0)) + (6 * s * t * (c2 - c1)) + (3 * t * t * (p3 - c2));
            Vec d2 = (6 * s * (c2 - (2 * c1) + p0)) + (6 * t * (p3 - (2 * c2) + c1));
            Vec diff = point - q[first + i];
            double numerator = Vec.Dot(diff, d1);
            double denominator = Vec.Dot(d1, d1) + Vec.Dot(diff, d2);
            if (Math.Abs(denominator) > 1e-12)
            {
                u[i] = Math.Clamp(t - (numerator / denominator), 0, 1);
            }
        }
    }

    internal static Vec Bezier(Vec p0, Vec c1, Vec c2, Vec p3, double t)
    {
        double s = 1 - t;
        return (p0 * (s * s * s)) + (c1 * (3 * t * s * s)) + (c2 * (3 * t * t * s)) + (p3 * (t * t * t));
    }

    /// <summary>The largest distance of the points from the chord between the first and last.</summary>
    private static double ChordDistance(Vec[] q, int first, int last)
    {
        Vec a = q[first], chord = q[last] - a;
        double length = chord.Length;
        double worst = 0;
        for (int i = first + 1; i < last; i++)
        {
            Vec d = q[i] - a;
            double distance;
            if (length < 1e-9)
            {
                distance = d.Length;
            }
            else
            {
                // Beyond the ends the distance is to the nearer end, so a run that doubles back is not a line.
                double along = Vec.Dot(d, chord) / length;
                distance = along < 0 ? d.Length : along > length ? (q[i] - q[last]).Length : Math.Abs(Vec.Cross(chord, d)) / length;
            }

            worst = Math.Max(worst, distance);
        }

        return worst;
    }

    /// <summary>A circular arc through the first and last points that every point lies within tolerance of.</summary>
    private static bool TryArc(Vec[] q, double tolerance, List<CurveSegment> output)
    {
        int m = q.Length - 1;
        Vec chord = q[m] - q[0];
        double length = chord.Length;
        if (length < 1e-9)
        {
            return false;
        }

        // The center lies on the chord's perpendicular bisector; least squares along it.
        Vec mid = (q[0] + q[m]) * 0.5, normal = new(-chord.Y / length, chord.X / length);
        double saa = 0, sab = 0;
        for (int i = 1; i < m; i++)
        {
            Vec d = q[0] - q[i];
            double a = 2 * Vec.Dot(d, normal);
            double b = Vec.Dot(q[0], q[0]) - Vec.Dot(q[i], q[i]) - (2 * Vec.Dot(d, mid));
            saa += a * a;
            sab += a * b;
        }

        if (saa < 1e-9)
        {
            return false;
        }

        Vec center = mid + (normal * (sab / saa));
        double radius = (q[0] - center).Length;
        if (radius > 1e4 || radius < 0.5)
        {
            return false;
        }

        double sweep = 0, previous = Angle(q[0] - center);
        int sign = 0;
        for (int i = 1; i <= m; i++)
        {
            if (Math.Abs((q[i] - center).Length - radius) > tolerance)
            {
                return false;
            }

            double angle = Angle(q[i] - center);
            double delta = Math.IEEERemainder(angle - previous, 2 * Math.PI);
            int s = Math.Sign(delta);
            if (s != 0)
            {
                if (sign != 0 && s != sign)
                {
                    return false;
                }

                sign = s;
            }

            sweep += delta;
            previous = angle;
        }

        if (Math.Abs(sweep) < 20 * Math.PI / 180 || Math.Abs(sweep) > 2 * Math.PI)
        {
            return false;
        }

        AddArc(center, radius, Angle(q[0] - center), sweep, q[m], output);
        return true;
    }

    /// <summary>A whole outline on one circle (Kåsa's least-squares fit), written as four quarter arcs.</summary>
    private static bool TryCircle(Vec[] p, double tolerance, List<CurveSegment> output, out Vec start)
    {
        start = default;
        int n = p.Length;
        if (n < 8)
        {
            return false;
        }

        // Solve for x^2 + y^2 + D x + E y + F = 0, relative to the mean for precision.
        Vec mean = default;
        foreach (Vec v in p)
        {
            mean += v;
        }

        mean *= 1.0 / n;
        double sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (Vec v in p)
        {
            double x = v.X - mean.X, y = v.Y - mean.Y, z = (x * x) + (y * y);
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sxz += x * z;
            syz += y * z;
            sz += z;
        }

        double det = (sxx * syy) - (sxy * sxy);
        if (Math.Abs(det) < 1e-9)
        {
            return false;
        }

        // With centered data, D and E solve the 2 by 2 system; F follows from the mean of z.
        double d = -((sxz * syy) - (syz * sxy)) / det;
        double e = -((sxx * syz) - (sxy * sxz)) / det;
        Vec center = new(mean.X - (d / 2), mean.Y - (e / 2));
        double radius = Math.Sqrt((d * d / 4) + (e * e / 4) + (sz / n));
        if (radius < 1 || radius > 1e4)
        {
            return false;
        }

        double area = 0;
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs((p[i] - center).Length - radius) > tolerance)
            {
                return false;
            }

            area += Vec.Cross(p[i], p[(i + 1) % n]);
        }

        double begin = Angle(p[0] - center);
        start = center + new Vec(Math.Cos(begin) * radius, Math.Sin(begin) * radius);
        AddArc(center, radius, begin, area >= 0 ? 2 * Math.PI : -2 * Math.PI, start, output);
        return true;
    }

    /// <summary>Adds an arc as cubics of at most 90 degrees each, ending exactly at <paramref name="end"/>.</summary>
    private static void AddArc(Vec center, double radius, double begin, double sweep, Vec end, List<CurveSegment> output)
    {
        int pieces = Math.Max(1, (int)Math.Ceiling((Math.Abs(sweep) / (Math.PI / 2)) - 1e-9));
        double step = sweep / pieces;
        double k = 4.0 / 3 * Math.Tan(step / 4);
        for (int i = 0; i < pieces; i++)
        {
            double a0 = begin + (i * step), a1 = a0 + step;
            Vec from = center + new Vec(Math.Cos(a0) * radius, Math.Sin(a0) * radius);
            Vec to = i == pieces - 1 ? end : center + new Vec(Math.Cos(a1) * radius, Math.Sin(a1) * radius);
            Vec c1 = from + (new Vec(-Math.Sin(a0), Math.Cos(a0)) * (k * radius));
            Vec c2 = to - (new Vec(-Math.Sin(a1), Math.Cos(a1)) * (k * radius));
            output.Add(new CurveSegment(true, c1, c2, to));
        }
    }

    private static double Snap(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? Math.Round(v) : v;

    private static double Angle(Vec v) => Math.Atan2(v.Y, v.X);

    /// <summary>The direction of a closed outline at a point, from the points two steps either side.</summary>
    private static Vec Tangent(Vec[] p, int i)
    {
        int n = p.Length;
        return (p[(i + 2) % n] - p[(i - 2 + n) % n]).Normalize();
    }

    /// <summary>The least-squares line through points, as a point on it and its direction.</summary>
    private static (Vec Point, Vec Direction) FitLine(List<Vec> points)
    {
        Vec mean = default;
        foreach (Vec v in points)
        {
            mean += v;
        }

        mean *= 1.0 / points.Count;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (Vec v in points)
        {
            Vec d = v - mean;
            sxx += d.X * d.X;
            sxy += d.X * d.Y;
            syy += d.Y * d.Y;
        }

        double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (mean, new Vec(Math.Cos(angle), Math.Sin(angle)));
    }

    private static bool Intersect((Vec Point, Vec Direction) a, (Vec Point, Vec Direction) b, out Vec meet)
    {
        double cross = Vec.Cross(a.Direction, b.Direction);
        meet = default;
        if (Math.Abs(cross) < 0.2)
        {
            return false;
        }

        double t = Vec.Cross(b.Point - a.Point, b.Direction) / cross;
        meet = a.Point + (a.Direction * t);

        // A corner on the pixel grid lands on it exactly.
        meet = new Vec(Snap(meet.X), Snap(meet.Y));
        return true;
    }
}
