using System.Numerics;

namespace Egami.Drawing.Tracing;

/// <summary>
/// Turns a polygon into a smooth outline of cubic Bézier curves and corners, after P. Selinger, "Potrace: a polygon-based
/// tracing algorithm" (2003), sections 2.3.2 to 2.4. Each side's midpoint is a curve end; at each corner the curve between the
/// two midpoints either bends round the corner or, when the corner is sharp enough, becomes two straight lines through it. Runs
/// of curves that bend the same way are then joined into single curves where one stays within a tolerance of them.
/// </summary>
internal sealed class CurveFitter
{
    /// <summary>The smallest bend a curve keeps, so that curves never go flat (the paper's lower bound).</summary>
    private const double MinAlpha = 0.55;

    /// <summary>The largest total turn a joined curve may make, short of 180 degrees.</summary>
    private static readonly double s_maxTurn = 179 * Math.PI / 180;

    private readonly (double X, double Y)[] _a;
    private readonly int _m;
    private readonly bool[] _corner;
    private readonly double[] _alpha;
    private readonly double[] _turn;
    private readonly int[] _sign;

    /// <summary>Measures each corner of a polygon and decides which stay sharp.</summary>
    /// <param name="corners">The polygon's corners.</param>
    /// <param name="alphaMax">A corner whose bend measure reaches this stays sharp: 0 keeps every corner, 4/3 or more none.</param>
    internal CurveFitter((double X, double Y)[] corners, double alphaMax)
    {
        _a = corners;
        _m = corners.Length;
        _corner = new bool[_m];
        _alpha = new double[_m];
        _turn = new double[_m];
        _sign = new int[_m];
        for (int i = 0; i < _m; i++)
        {
            (double X, double Y) prev = _a[(i + _m - 1) % _m], at = _a[i], next = _a[(i + 1) % _m];
            double inX = at.X - prev.X, inY = at.Y - prev.Y, outX = next.X - at.X, outY = next.Y - at.Y;
            double cross = (inX * outY) - (inY * outX), dot = (inX * outX) + (inY * outY);
            _turn[i] = Math.Atan2(Math.Abs(cross), dot);
            _sign[i] = Math.Sign(cross);

            double alpha = Alpha(prev, at, next);
            _corner[i] = alphaMax <= 0 || alpha >= alphaMax;
            _alpha[i] = Math.Clamp(alpha, MinAlpha, 1);
        }
    }

    /// <summary>
    /// The bend measure at corner a between neighbours p and q (section 2.3.3): the curve from midpoint b0 to midpoint b1 that is
    /// tangent to the sides and touches the line parallel to b0b1 that just reaches the unit square around a, as a fraction
    /// 4/3 · |b0c| / |b0a|, where c is where that line crosses b0a.
    /// </summary>
    internal static double Alpha((double X, double Y) p, (double X, double Y) a, (double X, double Y) q)
    {
        double b0x = (p.X + a.X) / 2, b0y = (p.Y + a.Y) / 2, b1x = (a.X + q.X) / 2, b1y = (a.Y + q.Y) / 2;
        double cx = b1x - b0x, cy = b1y - b0y;
        double length = Math.Sqrt((cx * cx) + (cy * cy));
        if (length < 1e-12)
        {
            // The polygon doubles back on itself here: keep the point.
            return 4.0 / 3;
        }

        double nx = -cy / length, ny = cx / length;
        double distance = Math.Abs((nx * (a.X - b0x)) + (ny * (a.Y - b0y)));
        if (distance < 1e-12)
        {
            return 0;
        }

        double reach = 0.5 * (Math.Abs(nx) + Math.Abs(ny));
        return 4.0 / 3 * (distance - reach) / distance;
    }

    /// <summary>Adds the outline as one closed contour, joining curves where the result stays within <paramref name="tolerance"/> pixels.</summary>
    internal void AddTo(PathBuilder builder, double tolerance)
    {
        // Start at a sharp corner when there is one, since no joined curve runs through it.
        int start = 0;
        for (int i = 0; i < _m; i++)
        {
            if (_corner[i])
            {
                start = i;
                break;
            }
        }

        // Fewest pieces first, then least penalty, over the corners from start round to start.
        int[] count = new int[_m + 1];
        double[] penalty = new double[_m + 1];
        int[] from = new int[_m + 1];
        for (int e = 1; e <= _m; e++)
        {
            count[e] = count[e - 1] + 1;
            penalty[e] = penalty[e - 1];
            from[e] = e - 1;
            if (tolerance <= 0)
            {
                continue;
            }

            // A run of corners r .. e - 1 (relative to start) joined into one curve.
            double turn = _corner[(start + e - 1) % _m] ? double.MaxValue : _turn[(start + e - 1) % _m];
            for (int r = e - 2; r >= 0 && turn < s_maxTurn; r--)
            {
                int at = (start + r) % _m;
                if (_corner[at] || _sign[at] != _sign[(start + e - 1) % _m] || _sign[at] == 0)
                {
                    break;
                }

                turn += _turn[at];
                if (turn >= s_maxTurn)
                {
                    break;
                }

                if (count[r] + 1 > count[e])
                {
                    continue;
                }

                if (TryJoin(start + r, e - r, tolerance, out double p, out _, out _) &&
                    (count[r] + 1 < count[e] || penalty[r] + p < penalty[e]))
                {
                    count[e] = count[r] + 1;
                    penalty[e] = penalty[r] + p;
                    from[e] = r;
                }
            }
        }

        List<(int First, int Length)> pieces = [];
        for (int e = _m; e > 0; e = from[e])
        {
            pieces.Add((from[e], e - from[e]));
        }

        pieces.Reverse();
        builder.MoveTo(Mid(start + _m - 1));
        foreach ((int first, int length) in pieces)
        {
            int at = start + first;
            if (length == 1)
            {
                int i = at % _m;
                Vector2 b1 = Mid(at);
                if (_corner[i])
                {
                    builder.LineTo(Point(i));
                    builder.LineTo(b1);
                }
                else
                {
                    Vector2 b0 = Mid(at - 1 + _m), a = Point(i);
                    float alpha = (float)_alpha[i];
                    builder.CubicTo(b0 + (alpha * (a - b0)), b1 + (alpha * (a - b1)), b1);
                }
            }
            else
            {
                _ = TryJoin(at, length, tolerance, out _, out Vector2 c1, out Vector2 c2);
                builder.CubicTo(c1, c2, Mid(at + length - 1));
            }
        }

        builder.Close();
    }

    /// <summary>
    /// Tries to replace the curves at corners first .. first + length - 1 by one curve from the midpoint before the first to the
    /// midpoint after the last (section 2.4). The curve is tangent to the outer sides, encloses the same area as the curves it
    /// replaces, and must pass within <paramref name="tolerance"/> of each inner side and reach each corner's square to within it.
    /// </summary>
    private bool TryJoin(int first, int length, double tolerance, out double penalty, out Vector2 control1, out Vector2 control2)
    {
        penalty = 0;
        control1 = control2 = default;
        int f = first % _m, l = (first + length - 1) % _m;
        (double X, double Y) b0 = MidD(first - 1 + _m), bn = MidD(first + length - 1), a1 = _a[f], an = _a[l];

        // O, where the tangent lines b0 → a1 and an → bn meet.
        double d0x = a1.X - b0.X, d0y = a1.Y - b0.Y, d1x = an.X - bn.X, d1y = an.Y - bn.Y;
        double denominator = (d0x * d1y) - (d0y * d1x);
        if (Math.Abs(denominator) < 1e-12)
        {
            return false;
        }

        double t = (((bn.X - b0.X) * d1y) - ((bn.Y - b0.Y) * d1x)) / denominator;
        double u = (((bn.X - b0.X) * d0y) - ((bn.Y - b0.Y) * d0x)) / denominator;
        if (t <= 0 || u <= 0)
        {
            return false;
        }

        (double X, double Y) o = (b0.X + (t * d0x), b0.Y + (t * d0y));

        // The signed area between the original curves and the chord bn → b0.
        double area = 0;
        (double X, double Y) last = b0;
        for (int k = 0; k < length; k++)
        {
            int i = (first + k) % _m;
            (double X, double Y) bPrev = MidD(first + k - 1 + _m), bNext = MidD(first + k);
            area += Shoelace(last, bNext);
            area += Triangle(bPrev, _a[i], bNext) * CurveShare(_alpha[i]);
            last = bNext;
        }

        area += Shoelace(last, b0);
        double triangle = Triangle(b0, o, bn);
        if (Math.Abs(triangle) < 1e-12)
        {
            return false;
        }

        // 4α − α² = (10/3) · share.
        double radicand = 4 - (10.0 / 3 * area / triangle);
        if (radicand < 0)
        {
            return false;
        }

        double alpha = 2 - Math.Sqrt(radicand);
        if (!(alpha > 0))
        {
            return false;
        }

        (double X, double Y) z1 = (b0.X + (alpha * (o.X - b0.X)), b0.Y + (alpha * (o.Y - b0.Y)));
        (double X, double Y) z2 = (bn.X + (alpha * (o.X - bn.X)), bn.Y + (alpha * (o.Y - bn.Y)));

        // Each inner side a_k → a_k+1: the point of the curve whose tangent is parallel to it must be near it and over it.
        for (int k = 0; k < length - 1; k++)
        {
            (double X, double Y) p = _a[(first + k) % _m], q = _a[(first + k + 1) % _m];
            double sx = q.X - p.X, sy = q.Y - p.Y, side = Math.Sqrt((sx * sx) + (sy * sy));
            if (side < 1e-12 || !TangentPoint(b0, z1, z2, bn, sx, sy, out (double X, double Y) z))
            {
                return false;
            }

            double along = (((z.X - p.X) * sx) + ((z.Y - p.Y) * sy)) / (side * side);
            double distance = Math.Abs(((z.X - p.X) * sy) - ((z.Y - p.Y) * sx)) / side;
            if (along < 0 || along > 1 || distance > tolerance)
            {
                return false;
            }

            penalty += distance * distance;
        }

        // Each corner: the point whose tangent is parallel to its chord must reach the line that touches the corner's square,
        // to within the tolerance.
        for (int k = 0; k < length; k++)
        {
            int i = (first + k) % _m;
            (double X, double Y) bPrev = MidD(first + k - 1 + _m), bNext = MidD(first + k), a = _a[i];
            double cx = bNext.X - bPrev.X, cy = bNext.Y - bPrev.Y, chord = Math.Sqrt((cx * cx) + (cy * cy));
            if (chord < 1e-12 || !TangentPoint(b0, z1, z2, bn, cx, cy, out (double X, double Y) z))
            {
                return false;
            }

            double nx = -cy / chord, ny = cx / chord;
            if ((nx * (a.X - bPrev.X)) + (ny * (a.Y - bPrev.Y)) < 0)
            {
                nx = -nx;
                ny = -ny;
            }

            double reach = 0.5 * (Math.Abs(nx) + Math.Abs(ny));
            double beyond = (nx * (z.X - a.X)) + (ny * (z.Y - a.Y)) + reach;
            if (beyond < -tolerance)
            {
                return false;
            }

            penalty += beyond * beyond;
        }

        control1 = new Vector2((float)z1.X, (float)z1.Y);
        control2 = new Vector2((float)z2.X, (float)z2.Y);
        return true;
    }

    /// <summary>The share of its control triangle a curve of bend α encloses with its chord: 3/10 · (4α − α²).</summary>
    private static double CurveShare(double alpha) => 0.3 * ((4 * alpha) - (alpha * alpha));

    /// <summary>The point of the cubic z0 z1 z2 z3 where its tangent is parallel to (dx, dy), for t in [0, 1].</summary>
    private static bool TangentPoint((double X, double Y) z0, (double X, double Y) z1, (double X, double Y) z2, (double X, double Y) z3, double dx, double dy, out (double X, double Y) point)
    {
        // The derivative is a quadratic Bézier with control vectors z1 - z0, z2 - z1, z3 - z2; its cross product with the
        // direction is A(1 - t)² + 2Bt(1 - t) + Ct².
        double a = ((z1.X - z0.X) * dy) - ((z1.Y - z0.Y) * dx);
        double b = ((z2.X - z1.X) * dy) - ((z2.Y - z1.Y) * dx);
        double c = ((z3.X - z2.X) * dy) - ((z3.Y - z2.Y) * dx);
        double qa = a - (2 * b) + c, qb = 2 * (b - a), qc = a;
        double t = -1;
        if (Math.Abs(qa) < 1e-12)
        {
            if (Math.Abs(qb) > 1e-12)
            {
                t = -qc / qb;
            }
        }
        else
        {
            double discriminant = (qb * qb) - (4 * qa * qc);
            if (discriminant >= 0)
            {
                double root = Math.Sqrt(discriminant);
                double t1 = (-qb + root) / (2 * qa), t2 = (-qb - root) / (2 * qa);
                t = t1 is >= 0 and <= 1 ? t1 : t2;
            }
        }

        if (!(t is >= 0 and <= 1))
        {
            point = default;
            return false;
        }

        double s = 1 - t;
        double w0 = s * s * s, w1 = 3 * s * s * t, w2 = 3 * s * t * t, w3 = t * t * t;
        point = ((w0 * z0.X) + (w1 * z1.X) + (w2 * z2.X) + (w3 * z3.X), (w0 * z0.Y) + (w1 * z1.Y) + (w2 * z2.Y) + (w3 * z3.Y));
        return true;
    }

    private static double Shoelace((double X, double Y) p, (double X, double Y) q) => ((p.X * q.Y) - (q.X * p.Y)) / 2;

    private static double Triangle((double X, double Y) p, (double X, double Y) q, (double X, double Y) r)
    {
        return (((q.X - p.X) * (r.Y - p.Y)) - ((r.X - p.X) * (q.Y - p.Y))) / 2;
    }

    /// <summary>The midpoint of the side from corner i to corner i + 1.</summary>
    private (double X, double Y) MidD(int i)
    {
        (double X, double Y) p = _a[i % _m], q = _a[(i + 1) % _m];
        return ((p.X + q.X) / 2, (p.Y + q.Y) / 2);
    }

    private Vector2 Mid(int i)
    {
        (double x, double y) = MidD(i);
        return new Vector2((float)x, (float)y);
    }

    private Vector2 Point(int i) => new((float)_a[i].X, (float)_a[i].Y);
}
