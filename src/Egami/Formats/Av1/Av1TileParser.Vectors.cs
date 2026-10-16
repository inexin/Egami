namespace Egami.Formats.Av1;

/// <summary>Intra block copy vectors (libaom's read_intrabc_info and the INTRA_FRAME case of mvref_common.c) and loop restoration units.</summary>
internal sealed partial class Av1TileParser
{
    private readonly int[] _stackRow = new int[8];
    private readonly int[] _stackCol = new int[8];
    private readonly int[] _stackWeight = new int[8];
    private int _stackCount;

    private void ReadIntrabcVector()
    {
        (int nearestRow, int nearestCol, int nearRow, int nearCol) = FindIntraReferenceVectors();
        (int refRow, int refCol) = nearestRow != 0 || nearestCol != 0 ? (nearestRow, nearestCol) : (nearRow, nearCol);
        if (refRow == 0 && refCol == 0)
        {
            // av1_find_ref_dv
            if (_miRow - _sbMi < MiRowStart)
            {
                refRow = 0;
                refCol = ((-4 * _sbMi) - 256) * 8;
            }
            else
            {
                refRow = -4 * _sbMi * 8;
                refCol = 0;
            }
        }

        bool valid = (refRow & 7) == 0 && (refCol & 7) == 0;
        refRow = (refRow >> 3) * 8;
        refCol = (refCol >> 3) * 8;
        Av1FrameContext.MvTables t = Av1FrameContext.Dv;
        int joint = Symbol(t.Joints[0], 4);
        int row = refRow + (joint is 2 or 3 ? ReadVectorComponent(t, 0) : 0);
        int col = refCol + (joint is 1 or 3 ? ReadVectorComponent(t, 1) : 0);
        row = (row >> 3) * 8;
        col = (col >> 3) * 8;
        valid = valid && row > -(1 << 14) && row < 1 << 14 && col > -(1 << 14) && col < 1 << 14 && DvValid(row, col);
        if (!valid)
        {
            throw Corrupt("An AV1 intra block copy vector points outside the area it may copy from.");
        }

        _b.MvRow = row;
        _b.MvCol = col;
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

    private bool DvValid(int row, int col)
    {
        int bw = Av1Tables.BlockSizeWide[_b.Size], bh = Av1Tables.BlockSizeHigh[_b.Size];
        int srcTop = (_miRow * 4 * 8) + row, tileTop = MiRowStart * 4 * 8;
        int srcLeft = (_miCol * 4 * 8) + col, tileLeft = MiColStart * 4 * 8;
        int srcBottom = (((_miRow * 4) + bh) * 8) + row, srcRight = (((_miCol * 4) + bw) * 8) + col;
        if (srcTop < tileTop || srcLeft < tileLeft || srcBottom > MiRowEnd * 4 * 8 || srcRight > MiColEnd * 4 * 8)
        {
            return false;
        }

        if (_isChromaRef && _planes > 1)
        {
            if ((bw < 8 && _ssx != 0 && srcLeft < tileLeft + 32) || (bh < 8 && _ssy != 0 && srcTop < tileTop + 32))
            {
                return false;
            }
        }

        int sbLog2 = _frame.SbMiLog2;
        int activeSbRow = _miRow >> sbLog2;
        int activeSb64Col = (_miCol * 4) >> 6;
        int sbSize = _sbMi * 4;
        int srcSbRow = ((srcBottom >> 3) - 1) / sbSize;
        int srcSb64Col = ((srcRight >> 3) - 1) >> 6;
        int sb64PerRow = ((MiColEnd - MiColStart - 1) >> 4) + 1;
        int activeSb64 = (activeSbRow * sb64PerRow) + activeSb64Col;
        int srcSb64 = (srcSbRow * sb64PerRow) + srcSb64Col;
        if (srcSb64 >= activeSb64 - 4)
        {
            return false;
        }

        int gradient = 1 + 4 + (sbSize > 64 ? 1 : 0);
        int wfOffset = gradient * (activeSbRow - srcSbRow);
        return srcSbRow <= activeSbRow && srcSb64Col < activeSb64Col - 4 + wfOffset;
    }

    /// <summary>Finds the nearest and near reference vectors of an intra block copy (av1_find_mv_refs for INTRA_FRAME).</summary>
    private (int NearestRow, int NearestCol, int NearRow, int NearCol) FindIntraReferenceVectors()
    {
        int bs = Math.Max(_bw, _bh);
        bool hasTopRight = HasTopRight(bs);
        int maxRowOffset = 0, maxColOffset = 0;
        int rowAdj = _bh < 2 && (_miRow & 1) != 0 ? 1 : 0;
        int colAdj = _bw < 2 && (_miCol & 1) != 0 ? 1 : 0;
        int processedRows = 0, processedCols = 0;
        _stackCount = 0;
        if (_upAvailable)
        {
            maxRowOffset = _bh < 2 ? -(2 << 1) + rowAdj : -(3 << 1) + rowAdj;
            maxRowOffset = Math.Clamp(maxRowOffset, MiRowStart - _miRow, MiRowEnd - _miRow - 1);
        }

        if (_leftAvailable)
        {
            maxColOffset = _bw < 2 ? -(2 << 1) + colAdj : -(3 << 1) + colAdj;
            maxColOffset = Math.Clamp(maxColOffset, MiColStart - _miCol, MiColEnd - _miCol - 1);
        }

        if (Math.Abs(maxRowOffset) >= 1)
        {
            ScanRow(-1, maxRowOffset, ref processedRows);
        }

        if (Math.Abs(maxColOffset) >= 1)
        {
            ScanCol(-1, maxColOffset, ref processedCols);
        }

        if (hasTopRight)
        {
            ScanBlock(-1, _bw);
        }

        int nearestCount = _stackCount;
        for (int i = 0; i < nearestCount; i++)
        {
            _stackWeight[i] += 640;
        }

        ScanBlock(-1, -1);
        for (int idx = 2; idx <= 3; idx++)
        {
            int rowOffset = -(idx << 1) + 1 + rowAdj;
            int colOffset = -(idx << 1) + 1 + colAdj;
            if (Math.Abs(rowOffset) <= Math.Abs(maxRowOffset) && Math.Abs(rowOffset) > processedRows)
            {
                ScanRow(rowOffset, maxRowOffset, ref processedRows);
            }

            if (Math.Abs(colOffset) <= Math.Abs(maxColOffset) && Math.Abs(colOffset) > processedCols)
            {
                ScanCol(colOffset, maxColOffset, ref processedCols);
            }
        }

        SortStack(0, nearestCount);
        SortStack(nearestCount, _stackCount);

        // Clamp to the block's surroundings (clamp_mv_ref), then take the first two.
        int bwPx = _bw * 4, bhPx = _bh * 4;
        for (int i = 0; i < _stackCount; i++)
        {
            _stackCol[i] = Math.Clamp(_stackCol[i], _toLeftEdge - (bwPx * 8) - 128, _toRightEdge + (bwPx * 8) + 128);
            _stackRow[i] = Math.Clamp(_stackRow[i], _toTopEdge - (bhPx * 8) - 128, _toBottomEdge + (bhPx * 8) + 128);
        }

        int nearestRow = _stackCount > 0 ? LowerPrecision(_stackRow[0]) : 0, nearestCol = _stackCount > 0 ? LowerPrecision(_stackCol[0]) : 0;
        int nearRow = _stackCount > 1 ? LowerPrecision(_stackRow[1]) : 0, nearCol = _stackCount > 1 ? LowerPrecision(_stackCol[1]) : 0;
        return (nearestRow, nearestCol, nearRow, nearCol);

        static int LowerPrecision(int v) => (v & 1) != 0 ? v + (v > 0 ? -1 : 1) : v;
    }

    private void SortStack(int start, int end)
    {
        int len = end;
        while (len > start)
        {
            int newLen = start;
            for (int idx = start + 1; idx < len; idx++)
            {
                if (_stackWeight[idx - 1] < _stackWeight[idx])
                {
                    (_stackRow[idx - 1], _stackRow[idx]) = (_stackRow[idx], _stackRow[idx - 1]);
                    (_stackCol[idx - 1], _stackCol[idx]) = (_stackCol[idx], _stackCol[idx - 1]);
                    (_stackWeight[idx - 1], _stackWeight[idx]) = (_stackWeight[idx], _stackWeight[idx - 1]);
                    newLen = idx;
                }
            }

            len = newLen;
        }
    }

    private Av1BlockInfo? GridAt(int row, int col) => _frame.Grid[((_miRow + row) * _miCols) + _miCol + col];

    private void AddCandidate(Av1BlockInfo? candidate, int weight)
    {
        if (candidate is null || !candidate.IsInter)
        {
            return;
        }

        int index = 0;
        for (; index < _stackCount; index++)
        {
            if (_stackRow[index] == candidate.MvRow && _stackCol[index] == candidate.MvCol)
            {
                _stackWeight[index] += weight;
                break;
            }
        }

        if (index == _stackCount && _stackCount < 8)
        {
            _stackRow[index] = candidate.MvRow;
            _stackCol[index] = candidate.MvCol;
            _stackWeight[index] = weight;
            _stackCount++;
        }
    }

    private void ScanRow(int rowOffset, int maxRowOffset, ref int processedRows)
    {
        int endMi = Math.Min(Math.Min(_bw, _miCols - _miCol), 16);
        int colOffset = 0;
        if (Math.Abs(rowOffset) > 1)
        {
            colOffset = 1;
            if ((_miCol & 1) != 0 && _bw < 2)
            {
                colOffset--;
            }
        }

        bool step16 = _bw >= 16;
        for (int i = 0; i < endMi;)
        {
            Av1BlockInfo? candidate = GridAt(rowOffset, colOffset + i);
            int candidateSize = candidate?.Size ?? Bs.B4x4;
            int n4w = Av1Tables.MiSizeWide[candidateSize];
            int len = Math.Min(_bw, n4w);
            len = step16 ? Math.Max(4, len) : Math.Abs(rowOffset) > 1 ? Math.Max(len, 2) : len;
            int weight = 2;
            if (_bw >= 2 && _bw <= n4w)
            {
                int inc = Math.Min(-maxRowOffset + rowOffset + 1, Av1Tables.MiSizeHigh[candidateSize]);
                weight = Math.Max(weight, inc);
                processedRows = inc - rowOffset - 1;
            }

            AddCandidate(candidate, len * weight);
            i += len;
        }
    }

    private void ScanCol(int colOffset, int maxColOffset, ref int processedCols)
    {
        int endMi = Math.Min(Math.Min(_bh, _frame.MiRows - _miRow), 16);
        int rowOffset = 0;
        if (Math.Abs(colOffset) > 1)
        {
            rowOffset = 1;
            if ((_miRow & 1) != 0 && _bh < 2)
            {
                rowOffset--;
            }
        }

        bool step16 = _bh >= 16;
        for (int i = 0; i < endMi;)
        {
            Av1BlockInfo? candidate = GridAt(rowOffset + i, colOffset);
            int candidateSize = candidate?.Size ?? Bs.B4x4;
            int n4h = Av1Tables.MiSizeHigh[candidateSize];
            int len = Math.Min(_bh, n4h);
            len = step16 ? Math.Max(4, len) : Math.Abs(colOffset) > 1 ? Math.Max(len, 2) : len;
            int weight = 2;
            if (_bh >= 2 && _bh <= n4h)
            {
                int inc = Math.Min(-maxColOffset + colOffset + 1, Av1Tables.MiSizeWide[candidateSize]);
                weight = Math.Max(weight, inc);
                processedCols = inc - colOffset - 1;
            }

            AddCandidate(candidate, len * weight);
            i += len;
        }
    }

    private void ScanBlock(int rowOffset, int colOffset)
    {
        int row = _miRow + rowOffset, col = _miCol + colOffset;
        if (row >= MiRowStart && col >= MiColStart && row < MiRowEnd && col < MiColEnd)
        {
            AddCandidate(GridAt(rowOffset, colOffset), 2 * 2);
        }
    }

    private bool HasTopRight(int bs)
    {
        int sbMiSize = _sbMi;
        int maskRow = _miRow & (sbMiSize - 1), maskCol = _miCol & (sbMiSize - 1);
        if (bs > 16)
        {
            return false;
        }

        bool hasTr = !((maskRow & bs) != 0 && (maskCol & bs) != 0);
        while (bs < sbMiSize)
        {
            if ((maskCol & bs) != 0)
            {
                if ((maskCol & (2 * bs)) != 0 && (maskRow & (2 * bs)) != 0)
                {
                    hasTr = false;
                    break;
                }
            }
            else
            {
                break;
            }

            bs <<= 1;
        }

        if (_bw < _bh && !_isLastVerticalRect)
        {
            hasTr = true;
        }

        if (_bw > _bh && !_isFirstHorizontalRect)
        {
            hasTr = false;
        }

        if (_b.Partition == Partition.VertA && _bw == _bh && (maskRow & bs) != 0)
        {
            hasTr = false;
        }

        return hasTr;
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
