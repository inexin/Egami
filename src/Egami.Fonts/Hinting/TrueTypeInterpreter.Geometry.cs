namespace Egami.Fonts.Hinting;

internal sealed partial class TrueTypeInterpreter
{
    private const int One = 0x4000;

    private bool Compat => _target.Compatibility == CompatibilityMode.FrozenX && (_gs.InstructControl & 4) == 0;

    private bool AfterIup => _iupX && _iupY;

    // ---- Fixed-point arithmetic ----

    /// <summary>a × b / c with the quotient rounded half away from zero, in 64 bits.</summary>
    internal static long MulDiv(long a, long b, long c)
    {
        if (c == 0)
        {
            return a * b >= 0 ? int.MaxValue : int.MinValue;
        }

        long p = a * b;
        bool negative = (p < 0) != (c < 0);
        long q = (Math.Abs(p) + (Math.Abs(c) / 2)) / Math.Abs(c);
        return negative ? -q : q;
    }

    /// <summary>A value times a 16.16 scale, rounded half away from zero.</summary>
    internal static long MulFix(long a, long scale)
    {
        long p = a * scale;
        return p >= 0 ? (p + 0x8000) >> 16 : -((-p + 0x8000) >> 16);
    }

    // The dot product of a vector with a 2.14 unit vector, rounded as the study describes (0x2000, or 0x1FFF when negative).
    private static int Dot(long dx, long dy, int vx, int vy)
    {
        long v = (dx * vx) + (dy * vy);
        return (int)((v + (v < 0 ? 0x1FFF : 0x2000)) >> 14);
    }

    private int Project(int dx, int dy)
        => _gs.ProjY == 0 && _gs.ProjX == One ? dx : _gs.ProjX == 0 && _gs.ProjY == One ? dy : Dot(dx, dy, _gs.ProjX, _gs.ProjY);

    private int DualProject(int dx, int dy)
        => _gs.DualY == 0 && _gs.DualX == One ? dx : _gs.DualX == 0 && _gs.DualY == One ? dy : Dot(dx, dy, _gs.DualX, _gs.DualY);

    // The distance between two points in the original outline along the dual projection vector: from the unscaled positions in the
    // glyph zone, so rounding of the scaled outline never leaks into it, and from the original scaled positions in the twilight zone.
    private int OriginalDistance(Zone a, int i, Zone b, int j)
    {
        if (a.IsTwilight || b.IsTwilight)
        {
            return DualProject(a.OrgX[i] - b.OrgX[j], a.OrgY[i] - b.OrgY[j]);
        }

        int d = DualProject(a.UnscaledX[i] - b.UnscaledX[j], a.UnscaledY[i] - b.UnscaledY[j]);
        return (int)MulFix(d, _glyphScale);
    }

    private static (int X, int Y) Normalize(long x, long y, bool keepZero)
    {
        if (x == 0 && y == 0)
        {
            return keepZero ? (0, 0) : (One, 0);
        }

        double length = Math.Sqrt(((double)x * x) + ((double)y * y));
        return ((int)Math.Round(x * One / length), (int)Math.Round(y * One / length));
    }

    // ---- Rounding ----

    private int Round(int d)
    {
        int r;
        switch (_gs.Round)
        {
            case RoundMode.Off:
                return d;
            case RoundMode.Grid:
                r = d >= 0 ? (d + 32) & ~63 : -((-d + 32) & ~63);
                break;
            case RoundMode.HalfGrid:
                return d >= 0 ? (d & ~63) + 32 : -((-d & ~63) + 32);
            case RoundMode.DoubleGrid:
                r = d >= 0 ? (d + 16) & ~31 : -((-d + 16) & ~31);
                break;
            case RoundMode.DownToGrid:
                r = d >= 0 ? d & ~63 : -(-d & ~63);
                break;
            case RoundMode.UpToGrid:
                r = d >= 0 ? (d + 63) & ~63 : -((-d + 63) & ~63);
                break;
            default:
                return SuperRound(d);
        }

        return d != 0 && Math.Sign(r) != Math.Sign(d) && r != 0 ? 0 : r;
    }

    private int SuperRound(int d)
    {
        int period = _gs.Period, phase = _gs.Phase, threshold = _gs.Threshold;
        if (d >= 0)
        {
            int v = FloorTo(d - phase + threshold, period) + phase;
            return v < 0 ? phase : v;
        }

        int w = -(FloorTo(-d - phase + threshold, period) + phase);
        return w > 0 ? -phase : w;
    }

    private static int FloorTo(int v, int period) => (int)Math.Floor(v / (double)period) * period;

    // SROUND and S45ROUND: bits 6-7 the period (1/2, 1 or 2 grid units; 3 acts as 1), bits 4-5 the phase (0 to 3 quarters of the
    // period), bits 0-3 the threshold ((n - 4) / 8 of the period; 0 is the period less one). The grid unit is 64, or about 45.25
    // (64 × √2 / 2) for S45ROUND.
    private void SetSuperRound(int selector, int grid)
    {
        int period = ((selector >> 6) & 3) switch
        {
            0 => grid == 64 ? 32 : 23,
            2 => grid == 64 ? 128 : 91,
            _ => grid,
        };
        _gs.Period = period;
        _gs.Phase = ((selector >> 4) & 3) * period / 4;
        int t = selector & 15;
        _gs.Threshold = t == 0 ? period - 1 : (t - 4) * period / 8;
    }

    // ---- Vectors ----

    private void SetVectors(bool x, bool projection, bool freedom)
    {
        (int vx, int vy) = x ? (One, 0) : (0, One);
        if (projection)
        {
            _gs.ProjX = _gs.DualX = vx;
            _gs.ProjY = _gs.DualY = vy;
        }

        if (freedom)
        {
            _gs.FreeX = vx;
            _gs.FreeY = vy;
        }
    }

    // SPVTL, SFVTL and SDPVTL pop p1 (the top of the stack, in zp2) and then p2 (in zp1), and set the vector from p1 to p2, or
    // perpendicular to it (rotated 90° counter-clockwise). Coinciding points give the x axis. SDPVTL takes the dual vector from
    // the original positions, the projection from the current.
    private void VectorToLine(int perpendicular, bool projection, bool dual)
    {
        int p1 = Pop(), p2 = Pop();
        Zone a = Z(_gs.Zp2), b = Z(_gs.Zp1);
        CheckPoint(a, p1);
        CheckPoint(b, p2);
        long dx = b.CurX[p2] - a.CurX[p1], dy = b.CurY[p2] - a.CurY[p1];
        if (perpendicular != 0)
        {
            (dx, dy) = (-dy, dx);
        }

        (int x, int y) = Normalize(dx, dy, keepZero: false);
        if (!projection)
        {
            _gs.FreeX = x;
            _gs.FreeY = y;
            return;
        }

        _gs.ProjX = x;
        _gs.ProjY = y;
        if (dual)
        {
            long ox = b.OrgX[p2] - a.OrgX[p1], oy = b.OrgY[p2] - a.OrgY[p1];
            if (perpendicular != 0)
            {
                (ox, oy) = (-oy, ox);
            }

            (_gs.DualX, _gs.DualY) = Normalize(ox, oy, keepZero: false);
        }
        else
        {
            _gs.DualX = x;
            _gs.DualY = y;
        }
    }

    // ---- Moving points ----

    // F·P in 2.14: nearly equal vectors count as equal, and nearly orthogonal ones as no move at all.
    private int FreedomDotProjection()
    {
        int f = (int)(((long)_gs.FreeX * _gs.ProjX + ((long)_gs.FreeY * _gs.ProjY)) >> 14);
        return f >= 0x3FFE ? One : Math.Abs(f) < 0x1555 ? 0 : f;
    }

    /// <summary>Moves a point by <paramref name="d"/> measured along the projection vector, travelling along the freedom vector.</summary>
    private void MovePoint(Zone z, int p, int d, bool touch)
    {
        int fdotp = FreedomDotProjection();
        if (fdotp == 0)
        {
            return;
        }

        bool projectionIsX = _gs.ProjX == One && _gs.ProjY == 0;
        if (_gs.FreeX != 0)
        {
            // Compatibility mode A keeps x as it is (but touched), except a diagonal move measured along x.
            bool frozen = Compat && !(projectionIsX && _gs.FreeY != 0) && !z.IsTwilight;
            if (!frozen && !(Compat && AfterIup && !projectionIsX))
            {
                z.CurX[p] += _gs.FreeX == One ? d : (int)MulDiv(d, _gs.FreeX, fdotp);
            }

            if (touch)
            {
                z.Touched[p] |= Zone.TouchX;
            }
        }

        if (_gs.FreeY != 0)
        {
            if (!(Compat && AfterIup && !projectionIsX))
            {
                z.CurY[p] += _gs.FreeY == One ? d : (int)MulDiv(d, _gs.FreeY, fdotp);
            }

            if (touch)
            {
                z.Touched[p] |= Zone.TouchY;
            }
        }
    }

    // Moves a point's original position too, for the twilight-zone set-ups of MIAP, MIRP and MSIRP.
    private void PlaceTwilight(Zone z, int p, int baseX, int baseY, int d)
    {
        z.OrgX[p] = baseX + (int)MulDiv(d, _gs.FreeX, One);
        z.OrgY[p] = baseY + (int)MulDiv(d, _gs.FreeY, One);
        z.CurX[p] = z.OrgX[p];
        z.CurY[p] = z.OrgY[p];
    }

    private (int Dx, int Dy) Displacement(int d)
    {
        int fdotp = FreedomDotProjection();
        return fdotp == 0 ? (0, 0) : ((int)MulDiv(d, _gs.FreeX, fdotp), (int)MulDiv(d, _gs.FreeY, fdotp));
    }

    private void Shift(Zone z, int p, int dx, int dy, bool touch)
    {
        bool frozen = Compat && !z.IsTwilight;
        if (Compat && AfterIup)
        {
            return;
        }

        if (_gs.FreeX != 0)
        {
            if (!frozen)
            {
                z.CurX[p] += dx;
            }

            if (touch)
            {
                z.Touched[p] |= Zone.TouchX;
            }
        }

        if (_gs.FreeY != 0)
        {
            z.CurY[p] += dy;
            if (touch)
            {
                z.Touched[p] |= Zone.TouchY;
            }
        }
    }

    private void Mdap(bool round)
    {
        int p = Pop();
        Zone z = Z(_gs.Zp0);
        CheckPoint(z, p);
        int d = 0;
        if (round)
        {
            int cur = Project(z.CurX[p], z.CurY[p]);
            d = Round(cur) - cur;
        }

        MovePoint(z, p, d, true);
        _gs.Rp0 = _gs.Rp1 = p;
    }

    private void Miap(bool round)
    {
        int index = Pop(), p = Pop();
        Zone z = Z(_gs.Zp0);
        CheckPoint(z, p);
        int distance = CvtValue(index);
        if (z.IsTwilight)
        {
            PlaceTwilight(z, p, 0, 0, distance);
        }

        int original = Project(z.CurX[p], z.CurY[p]);
        if (round)
        {
            if (!z.IsTwilight && Math.Abs((long)distance - original) > _gs.CvtCutIn)
            {
                distance = original;
            }

            distance = Round(distance);
        }

        MovePoint(z, p, distance - original, true);
        _gs.Rp0 = _gs.Rp1 = p;
    }

    private int SingleWidth(int distance)
    {
        if (Math.Abs((long)distance - _gs.SingleWidth) < _gs.SingleWidthCutIn)
        {
            return distance >= 0 ? _gs.SingleWidth : -_gs.SingleWidth;
        }

        return distance;
    }

    private int MinimumDistance(int distance, int original)
    {
        if (original >= 0)
        {
            return distance < _gs.MinDistance ? _gs.MinDistance : distance;
        }

        return distance > -_gs.MinDistance ? -_gs.MinDistance : distance;
    }

    // MDRP[abcde]: 0x10 sets rp0, 0x08 keeps the minimum distance, 0x04 rounds; the distance type bits are ignored.
    private void Mdrp(int flags)
    {
        int p = Pop();
        Zone z0 = Z(_gs.Zp0), z1 = Z(_gs.Zp1);
        int rp0 = _gs.Rp0;
        CheckPoint(z0, rp0);
        CheckPoint(z1, p);
        int original = SingleWidth(OriginalDistance(z1, p, z0, rp0));
        int distance = (flags & 0x04) != 0 ? Round(original) : original;
        if ((flags & 0x08) != 0)
        {
            distance = MinimumDistance(distance, original);
        }

        int current = Project(z1.CurX[p] - z0.CurX[rp0], z1.CurY[p] - z0.CurY[rp0]);
        MovePoint(z1, p, distance - current, true);
        _gs.Rp1 = rp0;
        _gs.Rp2 = p;
        if ((flags & 0x10) != 0)
        {
            _gs.Rp0 = p;
        }
    }

    // MIRP[abcde]: as MDRP but the distance is a control value, kept within the cut-in of the outline's own distance.
    private void Mirp(int flags)
    {
        int index = Pop(), p = Pop();
        Zone z0 = Z(_gs.Zp0), z1 = Z(_gs.Zp1);
        int rp0 = _gs.Rp0;
        CheckPoint(z0, rp0);
        CheckPoint(z1, p);
        int cvt = SingleWidth(index == -1 ? 0 : CvtValue(index));
        if (z1.IsTwilight)
        {
            PlaceTwilight(z1, p, z0.OrgX[rp0], z0.OrgY[rp0], cvt);
        }

        int original = OriginalDistance(z1, p, z0, rp0);
        int current = Project(z1.CurX[p] - z0.CurX[rp0], z1.CurY[p] - z0.CurY[rp0]);
        if (_gs.AutoFlip && (original ^ cvt) < 0)
        {
            cvt = -cvt;
        }

        int distance;
        if ((flags & 0x04) != 0)
        {
            if (_gs.Zp0 == _gs.Zp1 && Math.Abs((long)cvt - original) > _gs.CvtCutIn)
            {
                cvt = original;
            }

            distance = Round(cvt);
        }
        else
        {
            distance = cvt;
        }

        if ((flags & 0x08) != 0)
        {
            distance = MinimumDistance(distance, original);
        }

        MovePoint(z1, p, distance - current, true);
        _gs.Rp1 = rp0;
        _gs.Rp2 = p;
        if ((flags & 0x10) != 0)
        {
            _gs.Rp0 = p;
        }
    }

    private void Msirp(bool setRp0)
    {
        int d = Pop(), p = Pop();
        Zone z0 = Z(_gs.Zp0), z1 = Z(_gs.Zp1);
        int rp0 = _gs.Rp0;
        CheckPoint(z0, rp0);
        CheckPoint(z1, p);
        if (z1.IsTwilight)
        {
            PlaceTwilight(z1, p, z0.OrgX[rp0], z0.OrgY[rp0], d);
        }

        int current = Project(z1.CurX[p] - z0.CurX[rp0], z1.CurY[p] - z0.CurY[rp0]);
        MovePoint(z1, p, d - current, true);
        _gs.Rp1 = rp0;
        _gs.Rp2 = p;
        if (setRp0)
        {
            _gs.Rp0 = p;
        }
    }

    private void AlignRp()
    {
        Zone z0 = Z(_gs.Zp0), z1 = Z(_gs.Zp1);
        int rp0 = _gs.Rp0;
        CheckPoint(z0, rp0);
        for (int n = _gs.Loop; n > 0; n--)
        {
            int p = Pop();
            CheckPoint(z1, p);
            MovePoint(z1, p, Project(z0.CurX[rp0] - z1.CurX[p], z0.CurY[rp0] - z1.CurY[p]), true);
        }

        _gs.Loop = 1;
    }

    private void AlignPoints()
    {
        int p2 = Pop(), p1 = Pop();
        Zone z0 = Z(_gs.Zp0), z1 = Z(_gs.Zp1);
        CheckPoint(z1, p1);
        CheckPoint(z0, p2);
        int d = Project(z0.CurX[p2] - z1.CurX[p1], z0.CurY[p2] - z1.CurY[p1]) / 2;
        MovePoint(z1, p1, d, true);
        MovePoint(z0, p2, -d, true);
    }

    // The reference point of SHP, SHC and SHZ (rp2 in zp1, or rp1 in zp0 with the flag) and how far it has moved.
    private (Zone Zone, int Point, int Dx, int Dy) Reference(bool useRp1)
    {
        Zone z = Z(useRp1 ? _gs.Zp0 : _gs.Zp1);
        int p = useRp1 ? _gs.Rp1 : _gs.Rp2;
        CheckPoint(z, p);
        int d = Project(z.CurX[p] - z.OrgX[p], z.CurY[p] - z.OrgY[p]);
        (int dx, int dy) = Displacement(d);
        return (z, p, dx, dy);
    }

    private void Shp(bool useRp1)
    {
        (_, _, int dx, int dy) = Reference(useRp1);
        Zone z = Z(_gs.Zp2);
        for (int n = _gs.Loop; n > 0; n--)
        {
            int p = Pop();
            CheckPoint(z, p);
            Shift(z, p, dx, dy, true);
        }

        _gs.Loop = 1;
    }

    private void Shc(bool useRp1)
    {
        int contour = Pop();
        (Zone rz, int rp, int dx, int dy) = Reference(useRp1);
        Zone z = Z(_gs.Zp2);
        int start, end;
        if (z.IsTwilight)
        {
            (start, end) = contour == 0 ? (0, z.Count - 1) : throw Error("SHC of a twilight contour other than 0.");
        }
        else
        {
            if ((uint)contour >= (uint)z.ContourEnds.Length)
            {
                throw Error($"SHC of contour {contour}.");
            }

            start = contour == 0 ? 0 : z.ContourEnds[contour - 1] + 1;
            end = z.ContourEnds[contour];
        }

        for (int p = start; p <= end; p++)
        {
            if (!(ReferenceEquals(rz, z) && p == rp))
            {
                Shift(z, p, dx, dy, true);
            }
        }
    }

    // SHZ shifts every point of the zone but the reference and the phantom points, without touching them.
    private void Shz(bool useRp1)
    {
        int zone = ZoneIndex(Pop());
        (Zone rz, int rp, int dx, int dy) = Reference(useRp1);
        Zone z = Z(zone);
        int count = z.IsTwilight ? z.Count : z.OutlineCount;
        for (int p = 0; p < count; p++)
        {
            if (!(ReferenceEquals(rz, z) && p == rp))
            {
                Shift(z, p, dx, dy, false);
            }
        }
    }

    // SHPIX moves points by a distance in pixels along the freedom vector. In compatibility mode A it acts only before both IUPs,
    // only along y, and only in the twilight zone, in a composite glyph, or on a point already touched in y.
    private void Shpix()
    {
        int d = Pop();
        Zone z = Z(_gs.Zp2);
        int dx = (int)MulDiv(d, _gs.FreeX, One), dy = (int)MulDiv(d, _gs.FreeY, One);
        for (int n = _gs.Loop; n > 0; n--)
        {
            int p = Pop();
            CheckPoint(z, p);
            if (Compat)
            {
                if (!AfterIup && (z.IsTwilight || (_composite && _gs.FreeY != 0) || (z.Touched[p] & Zone.TouchY) != 0))
                {
                    z.CurY[p] += dy;
                    z.Touched[p] |= Zone.TouchY;
                }

                continue;
            }

            if (_gs.FreeX != 0)
            {
                z.CurX[p] += dx;
                z.Touched[p] |= Zone.TouchX;
            }

            if (_gs.FreeY != 0)
            {
                z.CurY[p] += dy;
                z.Touched[p] |= Zone.TouchY;
            }
        }

        _gs.Loop = 1;
    }

    // IP: each point keeps the ratio it had between rp1 and rp2 in the original outline. Without an original distance between
    // them, it keeps its original offset from rp1.
    private void Ip()
    {
        Zone z0 = Z(_gs.Zp0), z1 = Z(_gs.Zp1), z2 = Z(_gs.Zp2);
        int rp1 = _gs.Rp1, rp2 = _gs.Rp2;
        CheckPoint(z0, rp1);
        bool rp2Valid = (uint)rp2 < (uint)z1.Count;
        int originalRange = rp2Valid ? OriginalDistance(z1, rp2, z0, rp1) : 0;
        int currentRange = rp2Valid ? Project(z1.CurX[rp2] - z0.CurX[rp1], z1.CurY[rp2] - z0.CurY[rp1]) : 0;
        for (int n = _gs.Loop; n > 0; n--)
        {
            int p = Pop();
            CheckPoint(z2, p);
            int original = OriginalDistance(z2, p, z0, rp1);
            int current = Project(z2.CurX[p] - z0.CurX[rp1], z2.CurY[p] - z0.CurY[rp1]);
            int wanted = originalRange != 0 ? (int)MulDiv(original, currentRange, originalRange) : original;
            MovePoint(z2, p, wanted - current, true);
        }

        _gs.Loop = 1;
    }

    // ISECT: the point goes where the lines a0-a1 (zp1) and b0-b1 (zp0) cross, or to the average of the four ends when they are
    // within about 3 degrees of parallel.
    private void Isect()
    {
        int b1 = Pop(), b0 = Pop(), a1 = Pop(), a0 = Pop(), p = Pop();
        Zone za = Z(_gs.Zp1), zb = Z(_gs.Zp0), z = Z(_gs.Zp2);
        CheckPoint(za, a0);
        CheckPoint(za, a1);
        CheckPoint(zb, b0);
        CheckPoint(zb, b1);
        CheckPoint(z, p);
        long dbx = zb.CurX[b1] - zb.CurX[b0], dby = zb.CurY[b1] - zb.CurY[b0];
        long dax = za.CurX[a1] - za.CurX[a0], day = za.CurY[a1] - za.CurY[a0];
        long dx = zb.CurX[b0] - za.CurX[a0], dy = zb.CurY[b0] - za.CurY[a0];
        long discriminant = (dax * -dby) + (day * dbx);
        long dot = (dax * dbx) + (day * dby);
        if (19 * Math.Abs(discriminant) > Math.Abs(dot))
        {
            long v = (dx * -dby) + (dy * dbx);
            z.CurX[p] = za.CurX[a0] + (int)MulDiv(v, dax, discriminant);
            z.CurY[p] = za.CurY[a0] + (int)MulDiv(v, day, discriminant);
        }
        else
        {
            z.CurX[p] = (za.CurX[a0] + za.CurX[a1] + zb.CurX[b0] + zb.CurX[b1]) / 4;
            z.CurY[p] = (za.CurY[a0] + za.CurY[a1] + zb.CurY[b0] + zb.CurY[b1]) / 4;
        }

        z.Touched[p] |= Zone.TouchX | Zone.TouchY;
    }

    // IUP: per contour, untouched points between two touched neighbours interpolate between them (ratio from unscaled positions,
    // the in-between test from original scaled ones); points outside shift with the nearer one. A contour with one touched point
    // shifts with it; one with none stays.
    private void Iup(bool xAxis)
    {
        if (Compat && AfterIup)
        {
            return;
        }

        if (xAxis)
        {
            _iupX = true;
        }
        else
        {
            _iupY = true;
        }

        // Compatibility mode A leaves x alone, but the call still counts.
        if (xAxis && Compat)
        {
            return;
        }

        Zone z = _glyph;
        byte flag = xAxis ? Zone.TouchX : Zone.TouchY;
        int[] cur = xAxis ? z.CurX : z.CurY, org = xAxis ? z.OrgX : z.OrgY, unscaled = xAxis ? z.UnscaledX : z.UnscaledY;
        int start = 0;
        foreach (int end in z.ContourEnds)
        {
            if (end >= z.Count)
            {
                break;
            }

            int first = -1;
            for (int p = start; p <= end; p++)
            {
                if ((z.Touched[p] & flag) != 0)
                {
                    first = p;
                    break;
                }
            }

            if (first >= 0)
            {
                int current = first;
                while (true)
                {
                    int next = current;
                    do
                    {
                        next = next == end ? start : next + 1;
                    }
                    while ((z.Touched[next] & flag) == 0);

                    if (next == current)
                    {
                        // One touched point: everything shifts with it.
                        int shift = cur[current] - org[current];
                        for (int p = start; p <= end; p++)
                        {
                            if (p != current)
                            {
                                cur[p] += shift;
                            }
                        }

                        break;
                    }

                    InterpolateRange(cur, org, unscaled, current, next, start, end);
                    if (next == first)
                    {
                        break;
                    }

                    current = next;
                }
            }

            start = end + 1;
        }
    }

    // The untouched points strictly after a and before b (wrapping within the contour), between the touched references a and b.
    private static void InterpolateRange(int[] cur, int[] org, int[] unscaled, int a, int b, int start, int end)
    {
        int lo = a, hi = b;
        if (unscaled[lo] > unscaled[hi])
        {
            (lo, hi) = (hi, lo);
        }

        int orgLo = org[lo], orgHi = org[hi], curLo = cur[lo], curHi = cur[hi];
        int uLo = unscaled[lo], uHi = unscaled[hi];
        int dLo = curLo - orgLo, dHi = curHi - orgHi;
        for (int p = a == end ? start : a + 1; p != b; p = p == end ? start : p + 1)
        {
            int o = org[p];
            if (o <= orgLo)
            {
                cur[p] = o + dLo;
            }
            else if (o >= orgHi)
            {
                cur[p] = o + dHi;
            }
            else if (uHi == uLo)
            {
                cur[p] = curLo;
            }
            else
            {
                cur[p] = curLo + (int)MulDiv(unscaled[p] - uLo, curHi - curLo, uHi - uLo);
            }
        }
    }

    // DELTAP1-3: n pairs of a point and an argument whose high nibble is the ppem less the delta base (and 16 or 32) and whose low
    // nibble is the step: 0-7 for -8 to -1, 8-15 for 1 to 8, each 1/2^shift pixel. Out-of-range points are skipped.
    private void DeltaP(int offset)
    {
        int n = DeltaCount();
        Zone z = Z(_gs.Zp0);
        for (int i = 0; i < n; i++)
        {
            int p = Pop(), arg = Pop();
            int? step = DeltaStep(arg, offset);
            if (step is null || (uint)p >= (uint)z.Count)
            {
                continue;
            }

            // Compatibility mode A: only before both IUPs, along y, on a point touched in y or in a composite.
            if (Compat && (AfterIup || _gs.FreeY == 0 || !(_composite || (z.Touched[p] & Zone.TouchY) != 0)))
            {
                continue;
            }

            MovePoint(z, p, step.Value, true);
        }
    }

    private void DeltaC(int offset)
    {
        int n = DeltaCount();
        for (int i = 0; i < n; i++)
        {
            int c = Pop(), arg = Pop();
            if (DeltaStep(arg, offset) is int step && (uint)c < (uint)_cvt.Length)
            {
                _cvt[c] += step;
            }
        }
    }

    // The pair count of a DELTA instruction: each pair takes two values off the stack, so a count beyond half the stack is an
    // error, and the pairs are charged to the instruction budget.
    private int DeltaCount()
    {
        int n = Pop();
        if (n < 0 || n > _sp / 2)
        {
            throw Error($"DELTA with {n} pairs and {_sp} values on the stack.");
        }

        _budget -= n;
        return n;
    }

    private int? DeltaStep(int arg, int offset)
    {
        int ppem = _gs.DeltaBase + offset + ((arg >> 4) & 15);
        if (ppem != Ppem)
        {
            return null;
        }

        int magnitude = (arg & 15) - 8;
        if (magnitude >= 0)
        {
            magnitude++;
        }

        return magnitude * 64 / (1 << _gs.DeltaShift);
    }

    private void FlipPoints()
    {
        Zone z = Z(_gs.Zp0);
        for (int n = _gs.Loop; n > 0; n--)
        {
            int p = Pop();
            CheckPoint(z, p);
            if (!(Compat && AfterIup))
            {
                z.OnCurve[p] = !z.OnCurve[p];
            }
        }

        _gs.Loop = 1;
    }

    private void FlipRange(bool on)
    {
        int hi = Pop(), lo = Pop();
        Zone z = Z(_gs.Zp0);
        CheckPoint(z, lo);
        CheckPoint(z, hi);
        if (Compat && AfterIup)
        {
            return;
        }

        for (int p = lo; p <= hi; p++)
        {
            z.OnCurve[p] = on;
        }
    }
}
