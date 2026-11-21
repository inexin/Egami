using System.Runtime.CompilerServices;

using Egami.Heic.Hevc;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// The motion vector predictions of a prediction unit as the decoder derives them (H.265 8.5.3.2): the whole merge candidate list
/// (spatial, temporal, combined bi-predictive and zero candidates) and both AMVP predictors of a reference, from the motion the
/// picture's block maps hold for the blocks coded before. It follows <see cref="HevcSliceDecoder"/>'s derivation step for step,
/// but gives every candidate rather than the one an index names, as the encoder chooses among them.
/// </summary>
internal sealed class HevcMotionPredictor
{
    private readonly HevcPicture _picture;
    private readonly HevcSps _sps;
    private readonly HevcPps _pps;
    private readonly HevcSliceHeader _slice;
    private readonly HevcSliceRefs _refs;

    internal HevcMotionPredictor(HevcPicture picture, HevcSliceHeader slice, HevcSliceRefs refs)
    {
        _picture = picture;
        _sps = picture.Sps;
        _pps = picture.Pps;
        _slice = slice;
        _refs = refs;
    }

    /// <summary>A prediction unit's place in its coding unit: the coding unit, the unit's rectangle, its index and PartMode.</summary>
    internal readonly record struct Unit(int XCb, int YCb, int NCbS, int XPb, int YPb, int Width, int Height, int PartIdx, int PartMode);

    /// <summary>
    /// Fills <paramref name="list"/> with the merge candidates (8.5.3.2.2 to 8.5.3.2.5), MaxNumMergeCand of them, each as the
    /// decoder takes it for a merge_idx (8x4 and 4x8 units predict from list 0 only).
    /// </summary>
    internal void MergeCandidates(in Unit unit, Span<HevcPredictionUnit> list)
    {
        (int xCb, int yCb, int nCbS, int xPb, int yPb, int nPbW, int nPbH, int partIdx, int partMode) = unit;
        int origW = nPbW, origH = nPbH;
        bool singleMcl = _pps.Log2ParMrgLevel > 2 && nCbS == 8;
        if (singleMcl)
        {
            (xPb, yPb, nPbW, nPbH, partIdx, partMode) = (xCb, yCb, nCbS, nCbS, 0, HevcCodingUnit.Part2Nx2N);
        }

        int max = _slice.MaxNumMergeCand;
        int count = 0;

        // Up to four spatial candidates come before the list is cut to MaxNumMergeCand.
        Span<HevcPredictionUnit> all = stackalloc HevcPredictionUnit[5];
        int level = _pps.Log2ParMrgLevel;

        int xA1 = xPb - 1, yA1 = yPb + nPbH - 1;
        bool availableA1 = !SameMergeRegion(xPb, yPb, xA1, yA1, level) && !(partMode == HevcCodingUnit.PartNx2N && partIdx == 1)
            && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA1, yA1);
        HevcPredictionUnit a1 = availableA1 ? MotionAt(xA1, yA1) : default;
        if (availableA1)
        {
            all[count++] = a1;
        }

        int xB1 = xPb + nPbW - 1, yB1 = yPb - 1;
        bool availableB1 = !SameMergeRegion(xPb, yPb, xB1, yB1, level) && !(partMode == HevcCodingUnit.Part2NxN && partIdx == 1)
            && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB1, yB1);
        HevcPredictionUnit b1 = availableB1 ? MotionAt(xB1, yB1) : default;
        if (availableB1 && !(availableA1 && SameMotion(a1, b1)))
        {
            all[count++] = b1;
        }

        int xB0 = xPb + nPbW, yB0 = yPb - 1;
        if (!SameMergeRegion(xPb, yPb, xB0, yB0, level) && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB0, yB0))
        {
            HevcPredictionUnit b0 = MotionAt(xB0, yB0);
            if (!(availableB1 && SameMotion(b1, b0)))
            {
                all[count++] = b0;
            }
        }

        int xA0 = xPb - 1, yA0 = yPb + nPbH;
        if (!SameMergeRegion(xPb, yPb, xA0, yA0, level) && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA0, yA0))
        {
            HevcPredictionUnit a0 = MotionAt(xA0, yA0);
            if (!(availableA1 && SameMotion(a1, a0)))
            {
                all[count++] = a0;
            }
        }

        if (count < 4)
        {
            int xB2 = xPb - 1, yB2 = yPb - 1;
            if (!SameMergeRegion(xPb, yPb, xB2, yB2, level) && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB2, yB2))
            {
                HevcPredictionUnit b2 = MotionAt(xB2, yB2);
                if (!(availableA1 && SameMotion(a1, b2)) && !(availableB1 && SameMotion(b1, b2)))
                {
                    all[count++] = b2;
                }
            }
        }

        // The list never holds more than MaxNumMergeCand: the decoder stops at the index it reads, and later steps only append.
        count = Math.Min(count, max);
        if (count < max && _slice.TemporalMvp)
        {
            HevcPredictionUnit col = default;
            if (TemporalMv(xPb, yPb, nPbW, nPbH, 0, 0, out int mv0))
            {
                col.Flags |= 1;
                col.Mv0 = mv0;
                col.RefIdx0 = 0;
            }

            if (_slice.SliceType == HevcSliceHeader.B && TemporalMv(xPb, yPb, nPbW, nPbH, 1, 0, out int mv1))
            {
                col.Flags |= 2;
                col.Mv1 = mv1;
                col.RefIdx1 = 0;
            }

            if (col.Flags != 0)
            {
                all[count++] = col;
            }
        }

        int origCount = count;
        if (_slice.SliceType == HevcSliceHeader.B && origCount > 1 && origCount < max)
        {
            ReadOnlySpan<byte> l0 = [0, 1, 0, 2, 1, 2, 0, 3, 1, 3, 2, 3];
            ReadOnlySpan<byte> l1 = [1, 0, 2, 0, 2, 1, 3, 0, 3, 1, 3, 2];
            for (int combIdx = 0; combIdx < origCount * (origCount - 1) && count < max; combIdx++)
            {
                HevcPredictionUnit c0 = all[l0[combIdx]], c1 = all[l1[combIdx]];
                if ((c0.Flags & 1) != 0 && (c1.Flags & 2) != 0
                    && (_refs.Lists[0][c0.RefIdx0].Poc != _refs.Lists[1][c1.RefIdx1].Poc || _refs.Lists[0][c0.RefIdx0] != _refs.Lists[1][c1.RefIdx1]
                        || c0.Mv0 != c1.Mv1))
                {
                    all[count++] = new HevcPredictionUnit { Flags = 3, RefIdx0 = c0.RefIdx0, Mv0 = c0.Mv0, RefIdx1 = c1.RefIdx1, Mv1 = c1.Mv1 };
                }
            }
        }

        bool p = _slice.SliceType == HevcSliceHeader.P;
        int numRefIdx = p ? _slice.NumRefIdxL0 : Math.Min(_slice.NumRefIdxL0, _slice.NumRefIdxL1);
        for (int zeroIdx = 0; count < max; zeroIdx++)
        {
            int r = zeroIdx < numRefIdx ? zeroIdx : 0;
            all[count++] = new HevcPredictionUnit { Flags = p ? 1 : 3, RefIdx0 = r, RefIdx1 = p ? -1 : r };
        }

        for (int i = 0; i < max; i++)
        {
            ref HevcPredictionUnit c = ref all[i];
            if (c.Flags == 3 && origW + origH == 12)
            {
                c.Flags = 1;
            }

            if ((c.Flags & 1) == 0)
            {
                (c.RefIdx0, c.Mv0) = (-1, 0);
            }

            if ((c.Flags & 2) == 0)
            {
                (c.RefIdx1, c.Mv1) = (-1, 0);
            }

            c.Merge = true;
            c.MergeIdx = i;
        }

        all[..max].CopyTo(list);
    }

    /// <summary>Gives both AMVP predictors (8.5.3.2.6) of a list and reference index: mvp_lX_flag 0 and 1.</summary>
    internal (int Mvp0, int Mvp1) Amvp(in Unit unit, int list, int refIdx)
    {
        (int xCb, int yCb, int nCbS, int xPb, int yPb, int nPbW, int nPbH, int partIdx, _) = unit;
        HevcDpbPicture target = _refs.Lists[list][refIdx];
        bool targetLong = _refs.LongTerm[list][refIdx];

        int xA0 = xPb - 1, yA0 = yPb + nPbH, xA1 = xPb - 1, yA1 = yPb + nPbH - 1;
        bool availableA0 = PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA0, yA0);
        bool availableA1 = PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA1, yA1);
        bool isScaled = availableA0 || availableA1;
        bool availableA = false;
        int mvA = 0;
        for (int k = 0; k < 2 && !availableA; k++)
        {
            if (k == 0 ? availableA0 : availableA1)
            {
                availableA = SameReference(MotionAt(k == 0 ? xA0 : xA1, k == 0 ? yA0 : yA1), list, target, out mvA);
            }
        }

        for (int k = 0; k < 2 && !availableA; k++)
        {
            if (k == 0 ? availableA0 : availableA1)
            {
                availableA = ScaledReference(MotionAt(k == 0 ? xA0 : xA1, k == 0 ? yA0 : yA1), list, target, targetLong, out mvA);
            }
        }

        Span<int> xB = [xPb + nPbW, xPb + nPbW - 1, xPb - 1];
        int yB = yPb - 1;
        Span<bool> availableBk = stackalloc bool[3];
        for (int k = 0; k < 3; k++)
        {
            availableBk[k] = PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB[k], yB);
        }

        bool availableB = false;
        int mvB = 0;
        for (int k = 0; k < 3 && !availableB; k++)
        {
            if (availableBk[k])
            {
                availableB = SameReference(MotionAt(xB[k], yB), list, target, out mvB);
            }
        }

        if (!isScaled && availableB)
        {
            availableA = true;
            mvA = mvB;
        }

        if (!isScaled)
        {
            availableB = false;
            for (int k = 0; k < 3 && !availableB; k++)
            {
                if (availableBk[k])
                {
                    availableB = ScaledReference(MotionAt(xB[k], yB), list, target, targetLong, out mvB);
                }
            }
        }

        Span<int> mvp = [0, 0];
        int count = 0;
        if (availableA)
        {
            mvp[count++] = mvA;
        }

        if (availableB && !(availableA && mvA == mvB))
        {
            mvp[count++] = mvB;
        }

        if (count < 2 && _slice.TemporalMvp && TemporalMv(xPb, yPb, nPbW, nPbH, list, refIdx, out int mvCol))
        {
            mvp[count++] = mvCol;
        }

        return (mvp[0], mvp[1]);
    }

    internal static bool SameMotion(in HevcPredictionUnit a, in HevcPredictionUnit b) =>
        a.Flags == b.Flags && ((a.Flags & 1) == 0 || (a.RefIdx0 == b.RefIdx0 && a.Mv0 == b.Mv0))
        && ((a.Flags & 2) == 0 || (a.RefIdx1 == b.RefIdx1 && a.Mv1 == b.Mv1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private HevcPredictionUnit MotionAt(int x, int y)
    {
        int b = ((y >> 2) * _picture.Width4) + (x >> 2);
        return new HevcPredictionUnit
        {
            Flags = _picture.PredFlags[b],
            RefIdx0 = _picture.RefIdx[0][b],
            RefIdx1 = _picture.RefIdx[1][b],
            Mv0 = _picture.Mv[0][b],
            Mv1 = _picture.Mv[1][b],
        };
    }

    private bool PbAvailable(int xCb, int yCb, int nCbS, int xPb, int yPb, int nPbW, int nPbH, int partIdx, int xN, int yN)
    {
        bool sameCb = xCb <= xN && yCb <= yN && xCb + nCbS > xN && yCb + nCbS > yN;
        bool available = !sameCb
            ? _picture.Available(xPb, yPb, xN, yN)
            : !((nPbW << 1) == nCbS && (nPbH << 1) == nCbS && partIdx == 1 && yCb + nPbH <= yN && xCb + nPbW > xN);
        return available && _picture.PredFlags[((yN >> 2) * _picture.Width4) + (xN >> 2)] != 0;
    }

    private static bool SameMergeRegion(int xPb, int yPb, int xN, int yN, int level) =>
        xPb >> level == xN >> level && yPb >> level == yN >> level;

    private bool SameReference(in HevcPredictionUnit n, int list, HevcDpbPicture target, out int mv)
    {
        for (int i = 0; i < 2; i++)
        {
            int l = i == 0 ? list : 1 - list;
            if ((n.Flags & (1 << l)) != 0 && _refs.Lists[l][l == 0 ? n.RefIdx0 : n.RefIdx1] == target)
            {
                mv = l == 0 ? n.Mv0 : n.Mv1;
                return true;
            }
        }

        mv = 0;
        return false;
    }

    private bool ScaledReference(in HevcPredictionUnit n, int list, HevcDpbPicture target, bool targetLong, out int mv)
    {
        for (int i = 0; i < 2; i++)
        {
            int l = i == 0 ? list : 1 - list;
            if ((n.Flags & (1 << l)) == 0)
            {
                continue;
            }

            int r = l == 0 ? n.RefIdx0 : n.RefIdx1;
            if (_refs.LongTerm[l][r] != targetLong)
            {
                continue;
            }

            mv = l == 0 ? n.Mv0 : n.Mv1;
            if (!targetLong)
            {
                mv = Scale(mv, _refs.Poc - _refs.Lists[l][r].Poc, _refs.Poc - target.Poc);
            }

            return true;
        }

        mv = 0;
        return false;
    }

    private static int Scale(int mv, int td, int tb)
    {
        td = Math.Clamp(td, -128, 127);
        tb = Math.Clamp(tb, -128, 127);
        if (td == 0 || td == tb)
        {
            return mv;
        }

        int tx = (16384 + (Math.Abs(td) >> 1)) / td;
        int distScaleFactor = Math.Clamp(((tb * tx) + 32) >> 6, -4096, 4095);
        return Pack(ScaleComponent((short)mv, distScaleFactor), ScaleComponent(mv >> 16, distScaleFactor));
    }

    private static int ScaleComponent(int v, int factor)
    {
        int product = factor * v;
        return Math.Clamp(Math.Sign(product) * ((Math.Abs(product) + 127) >> 8), -32768, 32767);
    }

    /// <summary>Packs a vector's components, each wrapped to 16 bits.</summary>
    internal static int Pack(int x, int y) => (x & 0xFFFF) | (y << 16);

    private bool TemporalMv(int xPb, int yPb, int nPbW, int nPbH, int list, int refIdx, out int mv)
    {
        mv = 0;
        HevcDpbPicture[] colList = _refs.Lists[_slice.SliceType == HevcSliceHeader.B && !_slice.CollocatedFromL0 ? 1 : 0];
        if (_slice.CollocatedRefIdx >= colList.Length)
        {
            return false;
        }

        HevcDpbPicture colPic = colList[_slice.CollocatedRefIdx];
        HevcColMotion? col = colPic.Picture.ColMotion;
        if (col is null)
        {
            return false;
        }

        int xBr = xPb + nPbW, yBr = yPb + nPbH;
        if (yPb >> _sps.CtbLog2Size == yBr >> _sps.CtbLog2Size && yBr < _sps.Height && xBr < _sps.Width
            && Collocated(col, colPic, xBr, yBr, list, refIdx, out mv))
        {
            return true;
        }

        return Collocated(col, colPic, xPb + (nPbW >> 1), yPb + (nPbH >> 1), list, refIdx, out mv);
    }

    private bool Collocated(HevcColMotion col, HevcDpbPicture colPic, int x, int y, int list, int refIdx, out int mv)
    {
        mv = 0;
        int i = ((y >> 4) * col.Width16) + (x >> 4);
        int flags = col.PredFlags[i];
        if (flags == 0)
        {
            return false;
        }

        int listCol = (flags & 1) == 0 ? 1 : flags == 1 ? 0 : NoBackwardPrediction() ? list : (_slice.CollocatedFromL0 ? 1 : 0);
        bool targetLong = _refs.LongTerm[list][refIdx];
        if (col.LongTerm[listCol][i] != targetLong)
        {
            return false;
        }

        mv = col.Mv[listCol][i];
        int colPocDiff = colPic.Poc - col.RefPoc[listCol][i];
        int currPocDiff = _refs.Poc - _refs.Lists[list][refIdx].Poc;
        if (!targetLong && colPocDiff != currPocDiff)
        {
            mv = Scale(mv, colPocDiff, currPocDiff);
        }

        return true;
    }

    private bool NoBackwardPrediction()
    {
        foreach (HevcDpbPicture[] l in _refs.Lists)
        {
            foreach (HevcDpbPicture p in l)
            {
                if (p.Poc > _refs.Poc)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
