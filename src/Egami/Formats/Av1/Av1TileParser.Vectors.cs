namespace Egami.Formats.Av1;

/// <summary>
/// Intra block copy vectors (libaom's read_intrabc_info and the INTRA_FRAME case of mvref_common.c, with dav1d's correction of
/// vectors that reach outside the decoded area) and loop restoration units.
/// </summary>
internal sealed partial class Av1TileParser
{
    private void ReadIntrabcVector()
    {
        (int refRow, int refCol) = IntrabcReference();
        Av1FrameContext.MvTables t = Av1FrameContext.Dv;
        int joint = Symbol(t.Joints[0], 4);
        int row = refRow + (joint is 2 or 3 ? ReadVectorComponent(t, 0) : 0);
        int col = refCol + (joint is 1 or 3 ? ReadVectorComponent(t, 1) : 0);

        // libaom rejects a vector that reaches outside the decoded part of the tile; dav1d, whose output the gates hold to,
        // moves the source area back inside, and only fails when it still overlaps the current superblock.
        int borderLeft = MiColStart * 4, borderTop = MiRowStart * 4;
        if (_isChromaRef && !_sequence.Monochrome)
        {
            borderLeft += _bw < 2 && _ssx != 0 ? 4 : 0;
            borderTop += _bh < 2 && _ssy != 0 ? 4 : 0;
        }

        int srcLeft = (_miCol * 4) + (col >> 3), srcTop = (_miRow * 4) + (row >> 3);
        int srcRight = srcLeft + (_bw * 4), srcBottom = srcTop + (_bh * 4);
        int borderRight = ((MiColEnd + _bw - 1) & ~(_bw - 1)) * 4;
        if (srcLeft < borderLeft)
        {
            srcRight += borderLeft - srcLeft;
            srcLeft = borderLeft;
        }
        else if (srcRight > borderRight)
        {
            srcLeft -= srcRight - borderRight;
            srcRight = borderRight;
        }

        if (srcTop < borderTop)
        {
            srcBottom += borderTop - srcTop;
            srcTop = borderTop;
        }

        int sbPixels = _sbMi * 4;
        int sbx = (_miCol >> _frame.SbMiLog2) * sbPixels, sby = (_miRow >> _frame.SbMiLog2) * sbPixels;
        if (srcBottom > sby && srcRight > sbx)
        {
            if (srcTop - borderTop >= srcBottom - sby)
            {
                srcTop -= srcBottom - sby;
                srcBottom = sby;
            }
            else if (srcLeft - borderLeft >= srcRight - sbx)
            {
                srcLeft -= srcRight - sbx;
                srcRight = sbx;
            }
        }

        if (srcBottom > sby + sbPixels)
        {
            srcTop -= srcBottom - (sby + sbPixels);
            srcBottom = sby + sbPixels;
        }

        if (srcBottom > sby && srcRight > sbx)
        {
            throw Corrupt("An AV1 intra block copy vector points into the superblock being decoded.");
        }

        _b.MvRow = (srcTop - (_miRow * 4)) * 8;
        _b.MvCol = (srcLeft - (_miCol * 4)) * 8;
    }

    private int ReadVectorComponent(Av1FrameContext.MvTables t, int comp)
    {
        int sign = Symbol(t.Sign[comp], 2);
        int mvClass = Symbol(t.Classes[comp], 11);
        int d, mag;
        if (mvClass == 0)
        {
            d = Symbol(t.Class0[comp], 2);
            mag = 0;
        }
        else
        {
            d = 0;
            for (int i = 0; i < mvClass; i++)
            {
                d |= Symbol(t.Bits[(comp * 10) + i], 2) << i;
            }

            mag = 2 << (mvClass + 2);
        }

        // Integer vectors only: the fraction and the high precision bit take their defaults.
        mag += ((d << 3) | (3 << 1) | 1) + 1;
        return sign != 0 ? -mag : mag;
    }

    private void ReadRestorationUnits(int miRow, int miCol, int bsize)
    {
        for (int plane = 0; plane < _planes; plane++)
        {
            Av1RestorationPlane lr = _frame.Restoration[plane];
            if (lr.Type == Av1RestorationType.None)
            {
                continue;
            }

            // av1_loop_restoration_corners_in_sb
            int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
            int size = lr.UnitSize;
            bool scaled = _header.FrameWidth != _header.UpscaledWidth;
            int miToNumX = scaled ? (4 >> ssx) * _header.SuperresDenom : 4 >> ssx;
            int miToNumY = 4 >> ssy;
            int denomX = scaled ? size * 8 : size, denomY = size;
            int rcol0 = ((miCol * miToNumX) + denomX - 1) / denomX;
            int rrow0 = ((miRow * miToNumY) + denomY - 1) / denomY;
            int rcol1 = Math.Min((((miCol + Av1Tables.MiSizeWide[bsize]) * miToNumX) + denomX - 1) / denomX, lr.HorzUnits);
            int rrow1 = Math.Min((((miRow + Av1Tables.MiSizeHigh[bsize]) * miToNumY) + denomY - 1) / denomY, lr.VertUnits);
            for (int rrow = rrow0; rrow < rrow1; rrow++)
            {
                for (int rcol = rcol0; rcol < rcol1; rcol++)
                {
                    ReadRestorationUnit(plane, ref lr.Units[(rrow * lr.HorzUnits) + rcol], lr.Type);
                }
            }
        }
    }

    private void ReadRestorationUnit(int plane, ref Av1RestorationUnit unit, Av1RestorationType frameType)
    {
        int type = frameType switch
        {
            Av1RestorationType.Switchable => Symbol(Av1FrameContext.SwitchableRestore[0], 3),
            Av1RestorationType.Wiener => Symbol(Av1FrameContext.WienerRestore[0], 2) != 0 ? 1 : 0,
            _ => Symbol(Av1FrameContext.SgrprojRestore[0], 2) != 0 ? 2 : 0,
        };
        unit.Type = type;
        if (type == 1)
        {
            // read_wiener_filter: taps 0 to 2 vertically then horizontally, each against the previous unit's.
            bool full = plane == 0;
            unit.V0 = full ? Tap(0, -5, 10, 1) : 0;
            unit.V1 = Tap(1, -23, 8, 2);
            unit.V2 = Tap(2, -17, 46, 3);
            unit.H0 = full ? Tap(3, -5, 10, 1) : 0;
            unit.H1 = Tap(4, -23, 8, 2);
            unit.H2 = Tap(5, -17, 46, 3);
            _wienerRef[plane, 0] = unit.V0;
            _wienerRef[plane, 1] = unit.V1;
            _wienerRef[plane, 2] = unit.V2;
            _wienerRef[plane, 3] = unit.H0;
            _wienerRef[plane, 4] = unit.H1;
            _wienerRef[plane, 5] = unit.H2;
        }
        else if (type == 2)
        {
            int set = _r.Literal(4);
            unit.SgrSet = set;
            (int r0, int r1) = Av1Tables.SgrRadii(set);
            if (r0 == 0)
            {
                unit.Xqd0 = 0;
                unit.Xqd1 = _r.RefSubexpFin(128, 4, _sgrRef[plane, 1] + 32) - 32;
            }
            else if (r1 == 0)
            {
                unit.Xqd0 = _r.RefSubexpFin(128, 4, _sgrRef[plane, 0] + 96) - 96;
                unit.Xqd1 = Math.Clamp(128 - unit.Xqd0, -32, 95);
            }
            else
            {
                unit.Xqd0 = _r.RefSubexpFin(128, 4, _sgrRef[plane, 0] + 96) - 96;
                unit.Xqd1 = _r.RefSubexpFin(128, 4, _sgrRef[plane, 1] + 32) - 32;
            }

            _sgrRef[plane, 0] = unit.Xqd0;
            _sgrRef[plane, 1] = unit.Xqd1;
        }

        int Tap(int index, int min, int max, int k) => _r.RefSubexpFin(max - min + 1, k, _wienerRef[plane, index] - min) + min;
    }
}
