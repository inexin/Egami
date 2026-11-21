namespace Egami.Drawing.Tracing;

/// <summary>
/// Fits a closed loop of pixel edges with the polygon of fewest sides that stays within half a pixel of it, then moves each
/// corner to where the sides' best-fit lines meet, after P. Selinger, "Potrace: a polygon-based tracing algorithm" (2003),
/// sections 2.2 and 2.3.1.
/// </summary>
internal static class PolygonFitter
{
    /// <summary>Returns the polygon's corners, in the loop's direction.</summary>
    internal static (double X, double Y)[] Fit(PixelLoop loop)
    {
        int n = loop.Count;
        int[] reach = PossibleSegments(loop);
        Sums sums = new(loop);
        int[] corners = OptimalPolygon(n, reach, sums, loop);
        return Adjust(loop, corners, sums);
    }

    /// <summary>
    /// For each corner i of the loop, the furthest j (counted on past n, as i + steps) such that the loop from i to j is a
    /// possible side: the loop from i - 1 to j + 1 is straight, and j - i is at most n - 3. Every reach is at least i + 1.
    /// </summary>
    internal static int[] PossibleSegments(PixelLoop loop)
    {
        int n = loop.Count;
        int[] straight = new int[n];
        for (int i = 0; i < n; i++)
        {
            straight[i] = StraightReach(loop, i) - i;
        }

        // A path is straight only when every part of it is, so a reach from corner i may not pass the reach from corner i + 1.
        for (int step = (2 * n) - 1; step >= 0; step--)
        {
            int i = step % n, next = (i + 1) % n;
            straight[i] = Math.Min(straight[i], straight[next] + 1);
        }

        int[] reach = new int[n];
        for (int i = 0; i < n; i++)
        {
            int before = i == 0 ? n - 1 : i - 1;

            // The loop from i - 1 to j + 1 must be straight: j + 1 <= (i - 1) + straight[before].
            int furthest = i + straight[before] - 2;
            reach[i] = Math.Max(i + 1, Math.Min(furthest, i + n - 3));
        }

        return reach;
    }

    /// <summary>
    /// The furthest k (counted on past n) such that the loop from corner i to corner k is straight: it uses at most three of
    /// the four directions, and for every corner j between, some point of the line from corner i towards corner k lies within
    /// max-distance 1 of corner j. That is the paper's triplewise test with corner i fixed as the first of each triple: the
    /// directions from corner i that pass within the 2 by 2 square around each corner j form a cone, and corner k must lie in
    /// all of them.
    /// </summary>
    private static int StraightReach(PixelLoop loop, int i)
    {
        int n = loop.Count;
        int[] xs = loop.X, ys = loop.Y;
        int x0 = xs[i], y0 = ys[i];
        int directions = 0;
        bool bounded = false;
        long loX = 0, loY = 0, hiX = 0, hiY = 0;
        int k = i;
        while (k < i + n - 1)
        {
            int a = k % n, b = (k + 1) % n;
            int ddx = xs[b] - xs[a], ddy = ys[b] - ys[a];
            directions |= ddx > 0 ? 1 : ddx < 0 ? 2 : ddy > 0 ? 4 : 8;
            if (directions == 15)
            {
                break;
            }

            long px = xs[b] - x0, py = ys[b] - y0;
            if (bounded && !Inside(loX, loY, hiX, hiY, px, py))
            {
                break;
            }

            k++;

            // Narrow the cone by the square of side 2 around this corner, when the square does not hold corner i.
            if (Math.Max(Math.Abs(px), Math.Abs(py)) > 1)
            {
                (long cloX, long cloY, long chiX, long chiY) = SquareCone(px, py);
                if (!bounded)
                {
                    (loX, loY, hiX, hiY) = (cloX, cloY, chiX, chiY);
                    bounded = true;
                }
                else if (!Intersect(ref loX, ref loY, ref hiX, ref hiY, cloX, cloY, chiX, chiY))
                {
                    break;
                }
            }
        }

        return k;
    }

    /// <summary>The directions from the origin that meet the square of half-size 1 around (px, py), from clockwise to counter-clockwise bound.</summary>
    private static (long LoX, long LoY, long HiX, long HiY) SquareCone(long px, long py)
    {
        Span<long> cx = [px - 1, px + 1, px + 1, px - 1];
        Span<long> cy = [py - 1, py - 1, py + 1, py + 1];
        int lo = 0, hi = 0;

        // lo is the corner every other is counter-clockwise of (math sense), hi the one every other is clockwise of.
        for (int c = 0; c < 4; c++)
        {
            bool all = true;
            for (int o = 0; o < 4 && all; o++)
            {
                all = Cross(cx[c], cy[c], cx[o], cy[o]) <= 0;
            }

            if (all)
            {
                hi = c;
            }

            bool allLo = true;
            for (int o = 0; o < 4 && allLo; o++)
            {
                allLo = Cross(cx[c], cy[c], cx[o], cy[o]) >= 0;
            }

            if (allLo)
            {
                lo = c;
            }
        }

        return (cx[lo], cy[lo], cx[hi], cy[hi]);
    }

    /// <summary>Whether direction (x, y) lies in the cone from lo counter-clockwise (math sense) to hi, which spans less than 180 degrees.</summary>
    private static bool Inside(long loX, long loY, long hiX, long hiY, long x, long y)
    {
        return Cross(loX, loY, x, y) >= 0 && Cross(x, y, hiX, hiY) >= 0 && ((x * (loX + hiX)) + (y * (loY + hiY))) > 0;
    }

    /// <summary>Intersects two cones of less than 180 degrees; false when they share no direction.</summary>
    private static bool Intersect(ref long loX, ref long loY, ref long hiX, ref long hiY, long bLoX, long bLoY, long bHiX, long bHiY)
    {
        long nLoX, nLoY, nHiX, nHiY;
        if (Inside(loX, loY, hiX, hiY, bLoX, bLoY))
        {
            (nLoX, nLoY) = (bLoX, bLoY);
        }
        else if (Inside(bLoX, bLoY, bHiX, bHiY, loX, loY))
        {
            (nLoX, nLoY) = (loX, loY);
        }
        else
        {
            return false;
        }

        if (Inside(loX, loY, hiX, hiY, bHiX, bHiY))
        {
            (nHiX, nHiY) = (bHiX, bHiY);
        }
        else if (Inside(bLoX, bLoY, bHiX, bHiY, hiX, hiY))
        {
            (nHiX, nHiY) = (hiX, hiY);
        }
        else
        {
            return false;
        }

        if (Cross(nLoX, nLoY, nHiX, nHiY) < 0)
        {
            return false;
        }

        (loX, loY, hiX, hiY) = (nLoX, nLoY, nHiX, nHiY);
        return true;
    }

    private static long Cross(long ax, long ay, long bx, long by) => (ax * by) - (ay * bx);

    /// <summary>
    /// The cycle of possible sides with the fewest sides, and of those the smallest total penalty (section 2.2.4). Every polygon
    /// has a side that starts at or before the corner the fewest sides can jump over and ends after it, so trying each of those
    /// starts finds the best cycle.
    /// </summary>
    private static int[] OptimalPolygon(int n, int[] reach, Sums sums, PixelLoop loop)
    {
        // How many possible sides jump over each corner (start at or before it, end after it).
        int[] cover = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            int end = reach[i];
            if (end < n)
            {
                cover[i]++;
                cover[end]--;
            }
            else
            {
                cover[i]++;
                cover[n]--;
                cover[0]++;
                cover[end - n]--;
            }
        }

        int best = 0, run = 0, fewest = int.MaxValue;
        for (int s = 0; s < n; s++)
        {
            run += cover[s];
            if (run < fewest)
            {
                fewest = run;
                best = s;
            }
        }

        List<int> starts = [];
        for (int i = 0; i < n; i++)
        {
            int end = reach[i];
            bool covers = (i <= best && end > best) || (end >= n && end - n > best);
            if (covers)
            {
                starts.Add(i);
            }
        }

        int[]? bestCycle = null;
        int bestCount = int.MaxValue;
        double bestPenalty = double.MaxValue;
        int[] count = new int[n + 1];
        double[] penalty = new double[n + 1];
        int[] previous = new int[n + 1];
        foreach (int a in starts)
        {
            Array.Fill(count, int.MaxValue);
            count[0] = 0;
            penalty[0] = 0;
            for (int k = 0; k < n; k++)
            {
                if (count[k] == int.MaxValue || count[k] + 1 > bestCount)
                {
                    continue;
                }

                int at = (a + k) % n;
                int furthest = Math.Min(n, k + (reach[at] - at));
                for (int j = k + 1; j <= furthest; j++)
                {
                    int c = count[k] + 1;
                    double p = penalty[k] + sums.Penalty(loop, a + k, a + j);
                    if (c < count[j] || (c == count[j] && p < penalty[j]))
                    {
                        count[j] = c;
                        penalty[j] = p;
                        previous[j] = k;
                    }
                }
            }

            if (count[n] < bestCount || (count[n] == bestCount && penalty[n] < bestPenalty))
            {
                bestCount = count[n];
                bestPenalty = penalty[n];
                int[] cycle = new int[count[n]];
                int at = n;
                for (int m = cycle.Length - 1; m >= 0; m--)
                {
                    at = previous[at];
                    cycle[m] = (a + at) % n;
                }

                bestCycle = cycle;
            }
        }

        return bestCycle!;
    }

    /// <summary>
    /// Moves each corner within half a pixel (max-distance) of where it was to where the sum of its squared distances to the
    /// best-fit lines of its two sides is least (section 2.3.1).
    /// </summary>
    private static (double X, double Y)[] Adjust(PixelLoop loop, int[] corners, Sums sums)
    {
        int n = loop.Count, m = corners.Length;
        (double Nx, double Ny, double Cx, double Cy)[] lines = new (double, double, double, double)[m];
        for (int k = 0; k < m; k++)
        {
            int i = corners[k], j = corners[(k + 1) % m];
            if (j <= i)
            {
                j += n;
            }

            lines[k] = sums.FitLine(loop, i, j);
        }

        (double X, double Y)[] adjusted = new (double, double)[m];
        for (int k = 0; k < m; k++)
        {
            int i = corners[k];
            double vx = loop.X[i], vy = loop.Y[i];
            (double ax, double ay, double acx, double acy) = lines[(k + m - 1) % m];
            (double bx, double by, double bcx, double bcy) = lines[k];
            (double px, double py) = Minimize(ax, ay, ax * (acx - vx) + ay * (acy - vy), bx, by, bx * (bcx - vx) + by * (bcy - vy));
            adjusted[k] = (vx + px, vy + py);
        }

        return adjusted;
    }

    /// <summary>
    /// Minimizes (n1·p - d1)² + (n2·p - d2)² over the square |x|, |y| ≤ 1/2, for unit normals n1 and n2. The form is convex, so
    /// the least value is the free minimum when that lies in the square, else on the square's edges.
    /// </summary>
    internal static (double X, double Y) Minimize(double n1x, double n1y, double d1, double n2x, double n2y, double d2)
    {
        double qxx = (n1x * n1x) + (n2x * n2x), qxy = (n1x * n1y) + (n2x * n2y), qyy = (n1y * n1y) + (n2y * n2y);
        double gx = (n1x * d1) + (n2x * d2), gy = (n1y * d1) + (n2y * d2);
        double det = (qxx * qyy) - (qxy * qxy);
        if (det > 1e-9)
        {
            double x = ((qyy * gx) - (qxy * gy)) / det, y = ((qxx * gy) - (qxy * gx)) / det;
            if (Math.Abs(x) <= 0.5 && Math.Abs(y) <= 0.5)
            {
                return (x, y);
            }
        }
        else
        {
            // Parallel lines: the least values lie on a line; take its point nearest the corner when that is in the square.
            double nn = (n1x * n1x) + (n1y * n1y);
            double t = nn > 0 ? ((n1x * gx) + (n1y * gy)) / (2 * nn) : 0;
            double x = n1x * t, y = n1y * t;
            if (Math.Abs(x) <= 0.5 && Math.Abs(y) <= 0.5)
            {
                return (x, y);
            }
        }

        (double X, double Y) best = (0, 0);
        double least = double.MaxValue;
        for (int edge = 0; edge < 4; edge++)
        {
            // Fix one coordinate at ±1/2 and minimize the quadratic in the other, clamped to the edge.
            bool fixX = edge < 2;
            double fixedValue = (edge % 2 == 0) ? -0.5 : 0.5;
            double a = fixX ? qyy : qxx;
            double b = fixX ? (qxy * fixedValue) - gy : (qxy * fixedValue) - gx;
            double free = a > 1e-12 ? Math.Clamp(-b / a, -0.5, 0.5) : 0;
            double x = fixX ? fixedValue : free, y = fixX ? free : fixedValue;
            double v1 = (n1x * x) + (n1y * y) - d1, v2 = (n2x * x) + (n2y * y) - d2;
            double value = (v1 * v1) + (v2 * v2);
            if (value < least)
            {
                least = value;
                best = (x, y);
            }
        }

        return best;
    }

    /// <summary>Prefix sums of the corners' coordinates and their products, relative to the first corner, for penalties and line fits in constant time.</summary>
    internal sealed class Sums
    {
        private readonly double[] _x, _y, _xx, _xy, _yy;
        private readonly int _n;
        private readonly int _ox, _oy;

        internal Sums(PixelLoop loop)
        {
            _n = loop.Count;
            _ox = loop.X[0];
            _oy = loop.Y[0];
            _x = new double[_n + 1];
            _y = new double[_n + 1];
            _xx = new double[_n + 1];
            _xy = new double[_n + 1];
            _yy = new double[_n + 1];
            for (int i = 0; i < _n; i++)
            {
                double x = loop.X[i] - _ox, y = loop.Y[i] - _oy;
                _x[i + 1] = _x[i] + x;
                _y[i + 1] = _y[i] + y;
                _xx[i + 1] = _xx[i] + (x * x);
                _xy[i + 1] = _xy[i] + (x * y);
                _yy[i + 1] = _yy[i] + (y * y);
            }
        }

        /// <summary>
        /// The penalty of a side from corner i to corner j (counted on past n): its length times the root mean square distance of
        /// the corners from i to j from the line through corners i and j.
        /// </summary>
        internal double Penalty(PixelLoop loop, int i, int j)
        {
            (double sx, double sy, double sxx, double sxy, double syy, int count) = Range(i, j);
            double x0 = loop.X[i % _n] - _ox, y0 = loop.Y[i % _n] - _oy;
            double x1 = loop.X[j % _n] - _ox, y1 = loop.Y[j % _n] - _oy;
            double dx = x1 - x0, dy = y1 - y0, mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            double ex = sx / count, ey = sy / count;

            // Second moments about the side's midpoint.
            double cxx = (sxx / count) - (2 * mx * ex) + (mx * mx);
            double cxy = (sxy / count) - (mx * ey) - (my * ex) + (mx * my);
            double cyy = (syy / count) - (2 * my * ey) + (my * my);
            double value = (dx * dx * cyy) - (2 * dx * dy * cxy) + (dy * dy * cxx);
            return Math.Sqrt(Math.Max(0, value));
        }

        /// <summary>The least-squares line through corners i to j (counted on past n): its unit normal and a point on it.</summary>
        internal (double Nx, double Ny, double Cx, double Cy) FitLine(PixelLoop loop, int i, int j)
        {
            (double sx, double sy, double sxx, double sxy, double syy, int count) = Range(i, j);
            double ex = sx / count, ey = sy / count;
            double a = (sxx / count) - (ex * ex), b = (sxy / count) - (ex * ey), c = (syy / count) - (ey * ey);

            // The direction is the eigenvector of the larger eigenvalue of [[a, b], [b, c]].
            double half = (a - c) / 2;
            double lambda = ((a + c) / 2) + Math.Sqrt((half * half) + (b * b));
            double ux = b, uy = lambda - a;
            double vx = lambda - c, vy = b;
            if ((vx * vx) + (vy * vy) > (ux * ux) + (uy * uy))
            {
                (ux, uy) = (vx, vy);
            }

            double length = Math.Sqrt((ux * ux) + (uy * uy));
            if (length < 1e-12)
            {
                // No preferred direction: use the chord.
                ux = loop.X[j % _n] - loop.X[i % _n];
                uy = loop.Y[j % _n] - loop.Y[i % _n];
                length = Math.Sqrt((ux * ux) + (uy * uy));
                if (length < 1e-12)
                {
                    ux = 1;
                    uy = 0;
                    length = 1;
                }
            }

            ux /= length;
            uy /= length;
            return (-uy, ux, ex + _ox, ey + _oy);
        }

        private (double X, double Y, double XX, double XY, double YY, int Count) Range(int i, int j)
        {
            return (Sum(_x, i, j), Sum(_y, i, j), Sum(_xx, i, j), Sum(_xy, i, j), Sum(_yy, i, j), j - i + 1);
        }

        /// <summary>The sum over corners i to j inclusive, j counted on past n (and j - i below 2n).</summary>
        private double Sum(double[] prefix, int i, int j)
        {
            return Upto(prefix, j + 1) - Upto(prefix, i);
        }

        private double Upto(double[] prefix, int m)
        {
            int wraps = m / _n;
            return (wraps * prefix[_n]) + prefix[m - (wraps * _n)];
        }
    }
}
