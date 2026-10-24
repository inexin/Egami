namespace Egami.Formats.Av1;

/// <summary>
/// The intra block copy search of <see cref="Av1TileEncoder"/>, after libaom's rd_pick_intrabc_mode_sb (rdopt.c; BSD 2-clause,
/// see THIRD-PARTY-NOTICES): the reference vectors and the earlier places the block's content repeats at (<see cref="Av1BlockHash"/>)
/// are each checked against av1_is_dv_valid, ranked by their prediction error and vector rate, and the best coded in full.
/// </summary>
internal sealed partial class Av1TileEncoder
{
    private const int IntrabcDelaySb64 = 4;
    private readonly List<(long Cost, int Row, int Col)> _vectors = [];

    /// <summary>Gets whether a block may use intra block copy: the frame allows it, and 8x8 to 64x64.</summary>
    private bool IntrabcAllowed(int bsize) => _header.AllowIntrabc && _source.Hash is not null
        && Av1Tables.BlockSizeWide[bsize] is >= 8 and <= 64 && Av1Tables.BlockSizeHigh[bsize] is >= 8 and <= 64;

    /// <summary>
    /// Searches the current block's intra block copy vectors from <paramref name="start"/>; returns the best vector's cost and
    /// the vector, in eighth samples, or <see cref="long.MaxValue"/> when none is valid. Leaves the block as it started.
    /// </summary>
    private (long Cost, int Row, int Col) SearchIntrabc(int bsize, Snapshot start, long budget, bool chroma)
    {
        _vectors.Clear();
        int x0 = _miCol * 4, y0 = _miRow * 4;
        int bw = Av1Tables.BlockSizeWide[bsize], bh = Av1Tables.BlockSizeHigh[bsize];
        Av1BlockInfo b = _b;
        b.UseIntrabc = true;
        (int nearestRow, int nearestCol, int nearRow, int nearCol) = FindIntraReferenceVectors();
        (int refRow, int refCol) = IntrabcReference();
        AddVector(nearestRow, nearestCol);
        AddVector(nearRow, nearCol);
        AddVector(refRow, refCol);

        int level = Av1BlockHash.Level(bsize);
        if (level >= 0)
        {
            Av1BlockHash hash = _source.Hash!;
            int checkedCount = 0;
            foreach ((_, int position) in hash.Find(level, hash.Hash(level, x0, y0)))
            {
                int x = position & 0xffff, y = position >> 16;
                if ((x != x0 || y != y0) && DvValid((y - y0) * 8, (x - x0) * 8, bsize) && hash.Same(level, x, y, x0, y0))
                {
                    AddVector((y - y0) * 8, (x - x0) * 8);
                    if (++checkedCount >= 16)
                    {
                        break;
                    }
                }
            }
        }

        if (_vectors.Count == 0)
        {
            b.UseIntrabc = false;
            return (long.MaxValue, 0, 0);
        }

        // Rank by the vector's rate and the luma prediction error, which for a whole-sample vector is the reconstruction there.
        for (int i = 0; i < _vectors.Count; i++)
        {
            (_, int row, int col) = _vectors[i];
            SetIntrabc(row, col, bsize);
            long error = CopyError(x0 + (col >> 3), y0 + (row >> 3), bw, bh);
            _vectors[i] = (Av1Rate.RdCost(_rdmult, ModeRate(), error), row, col);
        }

        _vectors.Sort();
        (long Cost, int Row, int Col) best = (long.MaxValue, 0, 0);
        for (int i = 0; i < Math.Min(_vectors.Count, _profile.IntrabcCandidates); i++)
        {
            (_, int row, int col) = _vectors[i];
            start.Restore(this);
            long cost = Intrabc(row, col, bsize, Math.Min(budget, best.Cost));
            if (cost < best.Cost)
            {
                best = (cost, row, col);
            }
        }

        // The best vector's exact cost, as the block's other choices are weighed.
        if (best.Cost != long.MaxValue)
        {
            start.Restore(this);
            _winner = true;
            Intrabc(best.Row, best.Col, bsize, long.MaxValue);
            _winner = false;
            best.Cost = FinishBlock(start, bsize, chroma);
        }

        start.Restore(this);
        b.UseIntrabc = false;
        b.Skip = false;
        return best;

        void AddVector(int row, int col)
        {
            if ((row != 0 || col != 0) && DvValid(row, col, bsize) && !_vectors.Exists(v => v.Row == row && v.Col == col))
            {
                _vectors.Add((0, row, col));
            }
        }
    }

    /// <summary>Makes the current block an intra block copy block with a vector, its transform the largest.</summary>
    private void SetIntrabc(int row, int col, int bsize)
    {
        Av1BlockInfo b = _b;
        b.UseIntrabc = true;
        b.Skip = false;
        (b.MvRow, b.MvCol) = (row, col);
        b.YMode = Pred.Dc;
        b.UvMode = Pred.Dc;
        b.AngleDeltaY = b.AngleDeltaUv = 0;
        b.UseFilterIntra = false;
        b.CflAlphaSigns = b.CflAlphaIdx = 0;
        b.PaletteSize[0] = b.PaletteSize[1] = 0;
        int tx = _header.LosslessArray[b.SegmentId] ? Tx.T4x4 : Av1Tables.MaxTxSizeRectLookup[bsize];
        b.TxSize = tx;
        int xMis = Math.Min(_bw, _miCols - _miCol), yMis = Math.Min(_bh, _frame.MiRows - _miRow);
        for (int y = 0; y < yMis; y++)
        {
            _frame.TxSizeMap.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)tx);
        }
    }

    /// <summary>
    /// Encodes the current block as an intra block copy with a vector: the copy predicts every plane, then each plane's residual
    /// is coded; returns the cost of its mode symbols and residual, or <see cref="long.MaxValue"/> past the budget.
    /// </summary>
    private long Intrabc(int row, int col, int bsize, long budget)
    {
        SetIntrabc(row, col, bsize);
        long cost = Av1Rate.RdCost(_rdmult, ModeRate(), 0);
        if (cost >= budget)
        {
            return long.MaxValue;
        }

        _reconstructor.InterPrediction(this);
        cost = Add(cost, EncodePlane(0, bsize, Left(budget, cost)));
        if (_planes > 1 && _isChromaRef && cost != long.MaxValue)
        {
            cost = Add(cost, EncodePlane(1, bsize, Left(budget, cost)));
            cost = Add(cost, EncodePlane(2, bsize, Left(budget, cost)));
        }

        _reconstructor.InterDone(this);
        return cost;
    }

    /// <summary>Gets 16 times the squared error, at 8-bit scale, of copying the reconstructed luma at a place over the block.</summary>
    private long CopyError(int x, int y, int bw, int bh)
    {
        int x0 = _miCol * 4, y0 = _miRow * 4;
        int w = Math.Min(bw, _source.Widths[0] - x0), h = Math.Min(bh, _source.Heights[0] - y0);
        ushort[] frame = _frame.Plane<ushort>(0);
        ushort[] source = _source.Planes[0];
        int stride = _frame.Strides[0];
        long error = 0;
        for (int r = 0; r < h; r++)
        {
            int at = ((y0 + r) * stride) + x0, from = ((y + r) * stride) + x;
            for (int c = 0; c < w; c++)
            {
                int d = source[at + c] - frame[from + c];
                error += d * d;
            }
        }

        return _sequence.BitDepth switch
        {
            10 => error,
            12 => (error + 8) >> 4,
            _ => error << 4,
        };
    }

    /// <summary>
    /// Gets whether a vector, in eighth samples, copies from inside the tile, from superblocks already coded, and keeps the
    /// delay of 256 samples and the wavefront a decoder may rely on (av1_is_dv_valid, mvref_common.h).
    /// </summary>
    private bool DvValid(int row, int col, int bsize)
    {
        int bw = Av1Tables.BlockSizeWide[bsize], bh = Av1Tables.BlockSizeHigh[bsize];
        if ((row & 7) != 0 || (col & 7) != 0)
        {
            return false;
        }

        int srcTop = (_miRow * 4 * 8) + row, srcLeft = (_miCol * 4 * 8) + col;
        int srcBottom = (((_miRow * 4) + bh) * 8) + row, srcRight = (((_miCol * 4) + bw) * 8) + col;
        if (srcTop < MiRowStart * 4 * 8 || srcLeft < MiColStart * 4 * 8 || srcBottom > MiRowEnd * 4 * 8 || srcRight > MiColEnd * 4 * 8)
        {
            return false;
        }

        if (_isChromaRef && _planes > 1)
        {
            if ((bw < 8 && _ssx != 0 && srcLeft < (MiColStart * 4 * 8) + 32) || (bh < 8 && _ssy != 0 && srcTop < (MiRowStart * 4 * 8) + 32))
            {
                return false;
            }
        }

        int mibLog2 = _sbMi == 32 ? 5 : 4;
        int sbSize = _sbMi * 4;
        int activeSbRow = _miRow >> mibLog2;
        int activeSb64Col = (_miCol * 4) >> 6;
        int srcSbRow = ((srcBottom >> 3) - 1) / sbSize;
        int srcSb64Col = ((srcRight >> 3) - 1) >> 6;
        int sb64PerRow = ((MiColEnd - MiColStart - 1) >> 4) + 1;
        int activeSb64 = (activeSbRow * sb64PerRow) + activeSb64Col;
        int srcSb64 = (srcSbRow * sb64PerRow) + srcSb64Col;
        if (srcSb64 >= activeSb64 - IntrabcDelaySb64)
        {
            return false;
        }

        int gradient = 1 + IntrabcDelaySb64 + (sbSize > 64 ? 1 : 0);
        int wavefront = gradient * (activeSbRow - srcSbRow);
        if (srcSbRow > activeSbRow || srcSb64Col >= activeSb64Col - IntrabcDelaySb64 + wavefront)
        {
            return false;
        }

        // The encoder's own row wavefront lags each row one superblock behind the row above, so a row d above is finished
        // only through the column d to the right: tighter than the decoder's, and it must hold for the output to be the same
        // at any thread count.
        if (_rowWavefront && srcSbRow < activeSbRow)
        {
            int activeSbCol = (_miCol - MiColStart) / _sbMi;
            int srcSbCol = (((srcRight >> 3) - 1) - (MiColStart * 4)) / sbSize;
            return srcSbCol <= activeSbCol + (activeSbRow - srcSbRow);
        }

        return true;
    }
}
