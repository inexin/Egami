using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// B macroblocks: direct prediction (B_Skip, B_Direct_16x16 and direct 8x8 blocks), and partitions predicted from list 0,
/// list 1 or both (the average of the two, 8.4.2.3.1), each list searched as P partitions are; the choice among them, intra
/// included, by squared error plus λ times the bits.
/// </summary>
internal sealed partial class H264MbEncoder
{
    private readonly H264MbCode _direct = new();
    private readonly H264MbCode _directCoded = new();
    private readonly byte[] _bi = new byte[256];
    private readonly byte[] _biCb = new byte[64];
    private readonly byte[] _biCr = new byte[64];
    private readonly byte[] _mc1 = new byte[256];
    private H264RefPlanes[] _refs1 = [];
    private int _refCount1;
    private bool _weighted;

    /// <summary>
    /// Sets the references of the B picture to code: list 0 and list 1 in order with their active counts, and the largest
    /// vertical vector component the level allows in quarter samples. B slices do not weight their references.
    /// </summary>
    public void SetBReferences(H264RefPlanes[] list0, int count0, H264RefPlanes[] list1, int count1, int maxMvY)
    {
        _refs = list0;
        _refCount = count0;
        _refs1 = list1;
        _refCount1 = count1;
        _maxMvY = maxMvY;
        _sub8x8 = false;
        _weighted = false;
        if (_frameMv.Length != _mb.WidthMbs * _mb.HeightMbs)
        {
            _frameMv = new int[_mb.WidthMbs * _mb.HeightMbs];
        }
    }

    /// <summary>Chooses, codes and reconstructs the macroblock at (mbX, mbY) of a B slice at <paramref name="qp"/>; returns its cost.</summary>
    public double EncodeB(int mbX, int mbY, int qp)
    {
        Setup(mbX, mbY, qp);
        _round = InterRound;
        double bestCost = double.MaxValue;
        H264MbCode? best = null;
        int bestSlot = 0;

        // Direct motion, for B_Skip, B_Direct_16x16 and direct 8x8 blocks; an 8x8 block whose direct vectors reach further
        // than the reference planes cannot be direct.
        H264MbCode direct = _direct;
        ClearInter(direct, H264MbKind.BSkip);
        direct.Partition = 3;
        _syntax.Direct(direct);
        int directOk = 0;
        for (int i8 = 0; i8 < 4; i8++)
        {
            int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2, at = (by * 4) + bx;
            if ((direct.RefIdx[i8] < 0 || Reachable(bx, by, 2, 2, direct.Mv[at])) && (direct.RefIdx1[i8] < 0 || Reachable(bx, by, 2, 2, direct.Mv1[at])))
            {
                directOk |= 1 << i8;
            }
        }

        if (directOk == 15)
        {
            for (int i8 = 0; i8 < 4; i8++)
            {
                int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2, at = (by * 4) + bx;
                PredictPart(bx, by, 2, 2, direct.RefIdx[i8], direct.Mv[at], direct.RefIdx1[i8], direct.Mv1[at]);
            }

            WritePrediction();
            long sse = Sse(_srcY, _y, _lumaAt, _stride, 16, 16) + Sse(_srcCb, _cb, _chromaAt, _cstride, 8, 8) + Sse(_srcCr, _cr, _chromaAt, _cstride, 8, 8);
            bestCost = sse + (_lambda * Rate(direct) / CabacCost.Bit);
            best = direct;
            SaveMb(0);
            if (_effort >= 1)
            {
                H264MbCode coded = _directCoded;
                coded.CopyFrom(direct);
                coded.Kind = H264MbKind.BDirect;
                double cost = CodeInter(coded);
                SaveMb(4);
                if (cost < bestCost)
                {
                    (bestCost, best, bestSlot) = (cost, coded, 4);
                }
            }
        }

        Span<int> starts0 = [0, direct.RefIdx[0] >= 0 ? direct.Mv[0] : 0];
        Span<int> starts1 = [0, direct.RefIdx1[0] >= 0 ? direct.Mv1[0] : 0];

        // Motion search: 16x16, then 16x8 and 8x16 from effort 1 and 8x8 from effort 2, each partition from list 0, list 1
        // or both.
        H264MbCode p16 = _inter[0];
        ClearInter(p16, H264MbKind.Inter);
        long firstCost = SearchBPart(p16, 0, 0, 0, 4, 4, 0, starts0, starts1) + (long)(2 * _sadLambda);
        H264MbCode first = p16;
        H264MbCode? second = null;
        long secondCost = long.MaxValue;
        if (_effort >= 1)
        {
            for (int partition = 1; partition <= (_effort >= 2 ? 3 : 2); partition++)
            {
                H264MbCode code = _inter[partition];
                ClearInter(code, H264MbKind.Inter);
                code.Partition = partition;
                long cost = partition == 3 ? SearchB8x8(code, direct, directOk, starts0, starts1) : (long)(5 * _sadLambda);
                if (partition < 3)
                {
                    for (int p = 0; p < 2; p++)
                    {
                        (int bx, int by, int w, int h) = partition == 1 ? (0, p * 2, 4, 2) : (p * 2, 0, 2, 4);
                        int mask = p == 0 ? 0 : partition == 1 ? 0x00FF : 0x3333;
                        cost += SearchBPart(code, p, bx, by, w, h, mask, starts0, starts1);
                    }
                }

                if (cost < firstCost)
                {
                    (second, secondCost) = (first, firstCost);
                    (first, firstCost) = (code, cost);
                }
                else if (cost < secondCost)
                {
                    (second, secondCost) = (code, cost);
                }
            }
        }

        double interCost = CodeInter(first);
        SaveMb(1);
        if (interCost < bestCost)
        {
            (bestCost, best, bestSlot) = (interCost, first, 1);
        }

        if (_effort >= 3 && second is not null)
        {
            interCost = CodeInter(second);
            SaveMb(2);
            if (interCost < bestCost)
            {
                (bestCost, best, bestSlot) = (interCost, second, 2);
            }
        }

        if (TryIntra(firstCost))
        {
            double cost = Encode(mbX, mbY, qp);
            _round = InterRound;
            SaveMb(3);
            if (cost < bestCost)
            {
                (bestCost, best, bestSlot) = (cost, Best, 3);
            }
        }

        RestoreMb(bestSlot);
        Best = best!;
        return bestCost;
    }

    /// <summary>
    /// B_8x8: each 8x8 block direct (where its direct vectors are in reach) or searched in list 0, list 1 or both, whichever
    /// costs less by SATD and vector bits.
    /// </summary>
    private long SearchB8x8(H264MbCode code, H264MbCode direct, int directOk, ReadOnlySpan<int> starts0, ReadOnlySpan<int> starts1)
    {
        long total = (long)(5 * _sadLambda);
        int mask = 0;
        for (int i8 = 0; i8 < 4; i8++)
        {
            int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2, at = (by * 4) + bx;
            long cost = SearchBPart(code, i8, bx, by, 2, 2, mask, starts0, starts1) + (long)(2 * _sadLambda);
            code.SubTypes[i8] = H264MbSyntax.PredOf(code, i8);
            if ((directOk & (1 << i8)) != 0)
            {
                int r0 = direct.RefIdx[i8], r1 = direct.RefIdx1[i8];
                long directCost = PartSatd(bx, by, 2, 2, r0, direct.Mv[at], r1, direct.Mv1[at]) + (long)_sadLambda;
                if (directCost < cost)
                {
                    cost = directCost;
                    code.SubTypes[i8] = 0;
                    code.RefIdx[i8] = r0;
                    code.RefIdx1[i8] = r1;
                    for (int y = by; y < by + 2; y++)
                    {
                        direct.Mv.AsSpan((y * 4) + bx, 2).CopyTo(code.Mv.AsSpan((y * 4) + bx, 2));
                        direct.Mv1.AsSpan((y * 4) + bx, 2).CopyTo(code.Mv1.AsSpan((y * 4) + bx, 2));
                    }

                    _syntax.SetBlocks(0, bx, by, 2, 2, r0, direct.Mv[at]);
                    _syntax.SetBlocks(1, bx, by, 2, 2, r1, direct.Mv1[at]);
                }
            }

            mask |= 0x33 << (bx + (by * 4));
            total += cost;
        }

        return total;
    }

    /// <summary>
    /// Searches partition (or 8x8 block) <paramref name="index"/> of a B macroblock, w by h 4x4 blocks at (bx, by), in both
    /// lists and keeps list 0, list 1 or both, whichever costs least by SATD and vector bits; sets its references and vectors in
    /// the code and the motion arrays and returns the cost.
    /// </summary>
    private long SearchBPart(H264MbCode code, int index, int bx, int by, int w, int h, int mask, ReadOnlySpan<int> starts0, ReadOnlySpan<int> starts1)
    {
        int mv0 = SearchRefs(0, bx, by, w, h, mask, starts0, out int r0, out int mvp0, out long cost0);
        int mv1 = SearchRefs(1, bx, by, w, h, mask, starts1, out int r1, out int mvp1, out long cost1);

        long costBi = PartSatd(bx, by, w, h, r0, mv0, r1, mv1)
            + MvCost(H264SliceDecoder.MvX(mv0), H264SliceDecoder.MvY(mv0), mvp0) + MvCost(H264SliceDecoder.MvX(mv1), H264SliceDecoder.MvY(mv1), mvp1)
            + RefCost(r0, _refCount) + RefCost(r1, _refCount1) + (long)(2 * _sadLambda);
        bool use0 = true, use1 = true;
        long best = costBi;
        if (cost0 <= best)
        {
            (best, use1) = (cost0, false);
        }

        if (cost1 < best)
        {
            (best, use0, use1) = (cost1, false, true);
        }

        code.RefIdx[index] = use0 ? r0 : -1;
        code.RefIdx1[index] = use1 ? r1 : -1;
        FillMv(code.Mv, bx, by, w, h, use0 ? mv0 : 0);
        FillMv(code.Mv1, bx, by, w, h, use1 ? mv1 : 0);
        _syntax.SetBlocks(0, bx, by, w, h, code.RefIdx[index], mv0);
        _syntax.SetBlocks(1, bx, by, w, h, code.RefIdx1[index], mv1);
        return best;
    }

    /// <summary>The SATD of a block's prediction from list 0, list 1 or both (a reference index of -1 leaves its list out).</summary>
    private long PartSatd(int bx, int by, int w, int h, int r0, int mv0, int r1, int mv1)
    {
        H264EncodeStats.Add(H264EncodeStat.PartCosts);
        int px = (_mbX * 16) + (bx * 4), py = (_mbY * 16) + (by * 4), pw = w * 4, ph = h * 4;
        if (r0 >= 0)
        {
            _refs[r0].Luma(px, py, pw, ph, mv0, _mc, 0, 16, false);
        }

        if (r1 >= 0)
        {
            _refs1[r1].Luma(px, py, pw, ph, mv1, r0 >= 0 ? _mc1 : _mc, 0, 16, false);
        }

        if (r0 >= 0 && r1 >= 0)
        {
            Average(_mc, _mc1, 0, 16, pw, ph);
        }

        return Satd(_srcY, _origin + (py * _stride) + px, _stride, _mc, 0, 16, pw, ph);
    }

    /// <summary>
    /// Predicts a block of w by h 4x4 blocks at (bx, by) into <see cref="_ip"/>, <see cref="_ipCb"/> and <see cref="_ipCr"/>:
    /// from list 0 (weighted in P slices that weight), list 1, or the average of both.
    /// </summary>
    private void PredictPart(int bx, int by, int w, int h, int r0, int mv0, int r1, int mv1)
    {
        int x = (_mbX * 16) + (bx * 4), y = (_mbY * 16) + (by * 4), at = (by * 64) + (bx * 4), cat = (by * 16) + (bx * 2);
        int cx = (_mbX * 8) + (bx * 2), cy = (_mbY * 8) + (by * 2);
        if (r0 >= 0)
        {
            _refs[r0].Luma(x, y, w * 4, h * 4, mv0, _ip, at, 16, _weighted);
            _refs[r0].Chroma(cx, cy, w * 2, h * 2, mv0, _ipCb, _ipCr, cat, 8, _weighted);
            if (r1 < 0)
            {
                return;
            }

            _refs1[r1].Luma(x, y, w * 4, h * 4, mv1, _bi, at, 16, false);
            _refs1[r1].Chroma(cx, cy, w * 2, h * 2, mv1, _biCb, _biCr, cat, 8, false);
            Average(_ip, _bi, at, 16, w * 4, h * 4);
            Average(_ipCb, _biCb, cat, 8, w * 2, h * 2);
            Average(_ipCr, _biCr, cat, 8, w * 2, h * 2);
            return;
        }

        _refs1[r1].Luma(x, y, w * 4, h * 4, mv1, _ip, at, 16, false);
        _refs1[r1].Chroma(cx, cy, w * 2, h * 2, mv1, _ipCb, _ipCr, cat, 8, false);
    }

    /// <summary>The default weighted prediction of two lists (8-273): the rounded mean, into <paramref name="a"/>.</summary>
    private static void Average(byte[] a, byte[] b, int at, int stride, int w, int h) => H264EncodeSimd.Average(a, at, stride, b, at, stride, a, at, stride, w, h);

    private static void FillMv(int[] mvs, int bx, int by, int w, int h, int mv)
    {
        for (int y = by; y < by + h; y++)
        {
            mvs.AsSpan((y * 4) + bx, w).Fill(mv);
        }
    }
}
