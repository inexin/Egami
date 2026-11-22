using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Video.Codecs.Hevc.Encoder;

namespace Egami.Heic.Hevc;

/// <summary>
/// The motion of a B picture's prediction units (HEVC encoder Build Plan, PR C), with the H.264 encoder's B lessons: each list
/// searched from the AMVP predictors, the merge candidates, the caller's vectors and the last P picture's vectors scaled to the
/// list's distance; then both lists' best averaged, a 2Nx2N unit's two vectors refined together; the cheapest of list 0, list 1
/// and both by SATD plus rate, with the next one when it comes within 17/16 of it.
/// </summary>
internal sealed partial class HevcCtuSearch
{
    /// <summary>A unit's vector seed that is none.</summary>
    private const int NoSeed = int.MinValue;

    /// <summary>The share of the best candidate's rough cost within which a B unit's runner-up (between one list and both) is coded in full too.</summary>
    private const double BiRatio = 17.0 / 16;

    private readonly ushort[] _rankBlock1 = new ushort[64 * 64];
    private readonly ushort[] _fixedBlock = new ushort[64 * 64];

    // The vectors the last B unit search found in each list, used or not: they seed the halves.
    private int _lastMv0 = NoSeed, _lastMv1 = NoSeed;

    /// <summary>A list's best motion for a unit: reference, vector, predictor flag and difference, and its rough cost with and without the vector's rate.</summary>
    private struct ListMotion
    {
        internal int RefIdx;
        internal int Mv;
        internal int MvpFlag;
        internal int Mvd;
        internal long Cost;
        internal long RefRate;
        internal (int Mvp0, int Mvp1) Amvp;
    }

    /// <summary>
    /// The motion search of a B unit: gives the cheapest of list 0, list 1 and both by SATD plus rate, and in
    /// <paramref name="alternative"/> the runner-up between one list and both when it comes within <see cref="BiRatio"/> of it
    /// (else <paramref name="alternativeCost"/> is <see cref="long.MaxValue"/>).
    /// </summary>
    private HevcPredictionUnit SearchUnitB(in HevcMotionPredictor.Unit unit, int seed0, int seed1, bool merges, bool startsOnly, bool refine, out long cost,
        out HevcPredictionUnit alternative, out long alternativeCost)
    {
        int max = _slice.MaxNumMergeCand;
        Span<HevcPredictionUnit> list = stackalloc HevcPredictionUnit[5];
        _predictor!.MergeCandidates(unit, list[..max]);
        HevcPredictionUnit best = default;
        cost = long.MaxValue;
        if (merges)
        {
            Span<long> rank = stackalloc long[5];
            for (int i = 0; i < max; i++)
            {
                rank[i] = RankCostOnce(unit.XPb, unit.YPb, unit.Width, unit.Height, list, rank, i);
                long c = rank[i] + (_lambdaMe * (long)(1 + Math.Min(i + 1, max - 1)));
                if (c < cost)
                {
                    (cost, best) = (c, list[i]);
                }
            }
        }

        bool small = unit.Width + unit.Height == 12;
        Span<ListMotion> found = stackalloc ListMotion[2];
        for (int l = 0; l < 2; l++)
        {
            found[l] = SearchList(unit, l, l == 0 ? seed0 : seed1, list[..max], startsOnly);
        }

        (_lastMv0, _lastMv1) = (found[0].Mv, found[1].Mv);

        // inter_pred_idc: one bin for both lists, two for one (one for one in 8x4 and 4x8 units); merge_flag one more.
        long uniFlags = _lambdaMe * (long)(small ? 2 : 3);
        Span<long> costs =
        [
            found[0].Cost == long.MaxValue ? long.MaxValue : found[0].Cost + uniFlags,
            found[1].Cost == long.MaxValue ? long.MaxValue : found[1].Cost + uniFlags,
            long.MaxValue,
        ];
        int mvBi0 = found[0].Mv, mvBi1 = found[1].Mv;
        if (!small && found[0].Cost != long.MaxValue && found[1].Cost != long.MaxValue)
        {
            costs[2] = BiCost(unit, found[0], found[1], mvBi0, mvBi1);
            if (refine)
            {
                costs[2] = RefineBi(unit, found[0], found[1], ref mvBi0, ref mvBi1, costs[2]);
            }
        }

        // The three by cost: the cheapest searched one competes with the best merge candidate; the runner-up is offered when near.
        int first = 0;
        for (int k = 1; k < 3; k++)
        {
            first = costs[k] < costs[first] ? k : first;
        }

        int second = -1;
        for (int k = 0; k < 3; k++)
        {
            if (k != first && (first == 2 || k == 2) && costs[k] <= costs[first] * BiRatio && (second < 0 || costs[k] < costs[second]))
            {
                second = k;
            }
        }

        alternative = default;
        alternativeCost = long.MaxValue;
        if (costs[first] < cost)
        {
            cost = costs[first];
            best = BUnit(first, found, mvBi0, mvBi1);
            if (second >= 0)
            {
                alternative = BUnit(second, found, mvBi0, mvBi1);
                alternativeCost = costs[second];
            }
        }

        return best;
    }

    /// <summary>The best motion of one list for a unit: each active reference searched from all its starts.</summary>
    private ListMotion SearchList(in HevcMotionPredictor.Unit unit, int l, int seed, ReadOnlySpan<HevcPredictionUnit> merges, bool startsOnly)
    {
        HevcReference[] references = l == 0 ? _interContext!.References : _interContext!.References1;
        int refs = Math.Min(l == 0 ? _slice.NumRefIdxL0 : _slice.NumRefIdxL1, references.Length);
        ListMotion best = new() { Cost = long.MaxValue, Mv = NoSeed };
        Span<int> starts = stackalloc int[12];
        int stride = _picture.Widths[0];
        for (int r = 0; r < refs; r++)
        {
            int n = 0;
            if (seed != NoSeed)
            {
                starts[n++] = seed;
            }

            foreach (HevcPredictionUnit c in merges)
            {
                int mv = l == 0 ? c.Mv0 : c.Mv1;
                if ((c.Flags & (1 << l)) != 0 && (l == 0 ? c.RefIdx0 : c.RefIdx1) == r && !starts[..n].Contains(mv))
                {
                    starts[n++] = mv;
                }
            }

            n = AnchorSeeds(unit, references[r].Picture.Poc, starts, n);
            (int mvp0, int mvp1) = _predictor!.Amvp(unit, l, r);
            (int found, long c0, int flag) = _motionSearch.Search(_source.Planes[0], stride, unit.XPb, unit.YPb, unit.Width, unit.Height, references[r],
                (mvp0, mvp1), starts[..n], _lambdaMe, startsOnly);
            long refRate = _lambdaMe * (long)(refs > 1 ? Math.Min(r + 1, refs - 1) : 0);
            if (c0 + refRate < best.Cost)
            {
                int mvp = flag == 0 ? mvp0 : mvp1;
                best = new ListMotion
                {
                    RefIdx = r,
                    Mv = found,
                    MvpFlag = flag,
                    Mvd = HevcMotionPredictor.Pack((short)found - (short)mvp, (found >> 16) - (mvp >> 16)),
                    Cost = c0 + refRate,
                    RefRate = refRate,
                    Amvp = (mvp0, mvp1),
                };
            }
        }

        return best;
    }

    /// <summary>
    /// Adds search starts from the last P picture's vectors at the unit's centre and the four 16x16 blocks around it, each scaled
    /// from the distance it covers to the distance from this picture to the reference at <paramref name="refPoc"/>.
    /// </summary>
    private int AnchorSeeds(in HevcMotionPredictor.Unit unit, int refPoc, Span<int> starts, int n)
    {
        if (_interContext!.Seeds is not { } anchor || anchor.Picture.Picture.ColMotion is not { } col)
        {
            return n;
        }

        int d = _interContext.Refs.Poc - refPoc, anchorPoc = anchor.Picture.Poc;
        int cx = unit.XPb + (unit.Width >> 1), cy = unit.YPb + (unit.Height >> 1);
        foreach ((int dx, int dy) in (ReadOnlySpan<(int, int)>)[(0, 0), (-16, 0), (16, 0), (0, -16), (0, 16)])
        {
            int x = cx + dx, y = cy + dy;
            if (x < 0 || y < 0 || x >= _sps.Width || y >= _sps.Height || n >= starts.Length)
            {
                continue;
            }

            int i = ((y >> 4) * col.Width16) + (x >> 4);
            if ((col.PredFlags[i] & 1) == 0)
            {
                continue;
            }

            int span = anchorPoc - col.RefPoc[0][i], mv = col.Mv[0][i];
            if (span == 0)
            {
                continue;
            }

            int seed = HevcMotionPredictor.Pack(Scale((short)mv, d, span), Scale(mv >> 16, d, span));
            if (!starts[..n].Contains(seed))
            {
                starts[n++] = seed;
            }
        }

        return n;
    }

    private static int Scale(int c, int d, int span) => (int)Math.Round((double)c * d / span);

    /// <summary>The rough cost of predicting from both lists' vectors averaged: SATD plus both vectors' and references' rate and inter_pred_idc.</summary>
    private long BiCost(in HevcMotionPredictor.Unit unit, in ListMotion m0, in ListMotion m1, int mv0, int mv1)
    {
        RoughBlock(0, m0.RefIdx, mv0, unit.XPb, unit.YPb, unit.Width, unit.Height, _rankBlock);
        RoughBlock(1, m1.RefIdx, mv1, unit.XPb, unit.YPb, unit.Width, unit.Height, _rankBlock1);
        Average(_rankBlock, _rankBlock1, _rankBlock, unit.Width, unit.Height);
        return BiRate(m0, m1, mv0, mv1) + SourceSatd(unit, _rankBlock);
    }

    /// <summary>Both vectors' and references' rate and inter_pred_idc, from each list's AMVP predictors.</summary>
    private long BiRate(in ListMotion m0, in ListMotion m1, int mv0, int mv1) =>
        m0.RefRate + m1.RefRate + (_lambdaMe * (2 + (long)VectorRate(m0.Amvp, mv0) + VectorRate(m1.Amvp, mv1)));

    private long SourceSatd(in HevcMotionPredictor.Unit unit, ushort[] block)
    {
        int stride = _picture.Widths[0];
        return HevcMotionSearch.Satd(_source.Planes[0], (unit.YPb * stride) + unit.XPb, stride, block, 0, 64, unit.Width, unit.Height) << 8;
    }

    /// <summary>The bits of a vector's difference from the nearer of its AMVP predictors.</summary>
    private static int VectorRate((int Mvp0, int Mvp1) amvp, int mv)
    {
        (int mvp0, int mvp1) = amvp;
        return Math.Min(HevcMotionSearch.VectorBits((short)mv - (short)mvp0, (mv >> 16) - (mvp0 >> 16)),
            HevcMotionSearch.VectorBits((short)mv - (short)mvp1, (mv >> 16) - (mvp1 >> 16)));
    }

    /// <summary>
    /// Refines both vectors of a bi-predicted unit together (the H.264 encoder's joint refine): each in turn moves a quarter
    /// sample in any of eight directions while the other stays, for two rounds or until neither moves.
    /// </summary>
    private long RefineBi(in HevcMotionPredictor.Unit unit, in ListMotion m0, in ListMotion m1, ref int mv0, ref int mv1, long cost)
    {
        int x = unit.XPb, y = unit.YPb, w = unit.Width, h = unit.Height;
        for (int round = 0; round < 2; round++)
        {
            bool moved = false;
            for (int l = 0; l < 2; l++)
            {
                int mv = l == 0 ? mv0 : mv1, bestMv = mv;
                RoughBlock(1 - l, l == 0 ? m1.RefIdx : m0.RefIdx, l == 0 ? mv1 : mv0, x, y, w, h, _fixedBlock);
                HevcReference moving = l == 0 ? _interContext!.References[m0.RefIdx] : _interContext!.References1[m1.RefIdx];
                for (int d = 0; d < 8; d++)
                {
                    int cx = (short)mv + (d is 0 or 3 or 5 ? -1 : d is 2 or 4 or 7 ? 1 : 0), cy = (mv >> 16) + (d < 3 ? -1 : d > 4 ? 1 : 0);
                    if (!Reachable(moving, x, y, w, h, cx, cy))
                    {
                        continue;
                    }

                    int c = HevcMotionPredictor.Pack(cx, cy);
                    HevcEncodeStats.Add(HevcEncodeStat.SubpelCosts);
                    moving.Block(x, y, cx, cy, w, h, _rankBlock1, 64);
                    Average(_fixedBlock, _rankBlock1, _rankBlock, w, h);
                    long cc = SourceSatd(unit, _rankBlock) + BiRate(m0, m1, l == 0 ? c : mv0, l == 0 ? mv1 : c);
                    if (cc < cost)
                    {
                        (cost, bestMv) = (cc, c);
                    }
                }

                if (bestMv != mv)
                {
                    moved = true;
                    if (l == 0)
                    {
                        mv0 = bestMv;
                    }
                    else
                    {
                        mv1 = bestMv;
                    }
                }
            }

            if (!moved)
            {
                break;
            }
        }

        return cost;
    }

    /// <summary>A B unit of the search's choice <paramref name="kind"/>: list 0 (0), list 1 (1) or both (2).</summary>
    private static HevcPredictionUnit BUnit(int kind, ReadOnlySpan<ListMotion> found, int mvBi0, int mvBi1)
    {
        HevcPredictionUnit pu = new() { Flags = kind == 2 ? 3 : 1 << kind, RefIdx0 = -1, RefIdx1 = -1 };
        if ((pu.Flags & 1) != 0)
        {
            (pu.RefIdx0, pu.Mv0, pu.MvpFlag0, pu.Mvd0) = Coded(found[0], kind == 2 ? mvBi0 : found[0].Mv);
        }

        if ((pu.Flags & 2) != 0)
        {
            (pu.RefIdx1, pu.Mv1, pu.MvpFlag1, pu.Mvd1) = Coded(found[1], kind == 2 ? mvBi1 : found[1].Mv);
        }

        return pu;
    }

    /// <summary>A list's syntax for a vector: the reference, the vector, and the predictor flag and difference that code it cheaper.</summary>
    private static (int RefIdx, int Mv, int MvpFlag, int Mvd) Coded(in ListMotion m, int mv)
    {
        if (mv == m.Mv)
        {
            return (m.RefIdx, mv, m.MvpFlag, m.Mvd);
        }

        (int mvp0, int mvp1) = m.Amvp;
        int flag = HevcMotionSearch.VectorBits((short)mv - (short)mvp1, (mv >> 16) - (mvp1 >> 16)) < HevcMotionSearch.VectorBits((short)mv - (short)mvp0, (mv >> 16) - (mvp0 >> 16))
            ? 1 : 0;
        int mvp = flag == 0 ? mvp0 : mvp1;
        return (m.RefIdx, mv, flag, HevcMotionPredictor.Pack((short)mv - (short)mvp, (mv >> 16) - (mvp >> 16)));
    }

    /// <summary>
    /// The rough luma prediction of a list's reference and vector into a block of row length 64 (a vector far outside the picture
    /// as the nearest block inside the search planes).
    /// </summary>
    private void RoughBlock(int l, int refIdx, int mv, int x, int y, int w, int h, ushort[] block)
    {
        HevcReference reference = l == 0 ? _interContext!.References[Math.Max(refIdx, 0)] : _interContext!.References1[Math.Max(refIdx, 0)];
        int margin = HevcReference.Pad - 8;
        int mvx = Math.Clamp((int)(short)mv, (-margin - x) * 4, (reference.Width + margin - w - x) * 4);
        int mvy = Math.Clamp(mv >> 16, (-margin - y) * 4, (reference.Height + margin - h - y) * 4);
        reference.Block(x, y, mvx, mvy, w, h, block, 64);
    }

    private static bool Reachable(HevcReference reference, int x, int y, int w, int h, int mvx, int mvy)
    {
        int margin = HevcReference.Pad - 8;
        return x + (mvx >> 2) >= -margin && y + (mvy >> 2) >= -margin
            && x + (mvx >> 2) + w + 1 <= reference.Width + margin && y + (mvy >> 2) + h + 1 <= reference.Height + margin;
    }

    /// <summary>The rounded mean of two blocks of row length 64, as bi-prediction's default weights make it.</summary>
    private static void Average(ushort[] a, ushort[] b, ushort[] to, int w, int h)
    {
        for (int r = 0; r < h; r++)
        {
            ref ushort pa = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), r * 64);
            ref ushort pb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), r * 64);
            ref ushort d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(to), r * 64);
            int k = 0;
            for (; k + 16 <= w; k += 16)
            {
                Vector256<ushort> va = Vector256.LoadUnsafe(ref pa, (nuint)k), vb = Vector256.LoadUnsafe(ref pb, (nuint)k);
                ((va + vb + Vector256<ushort>.One) >> 1).StoreUnsafe(ref d, (nuint)k);
            }

            for (; k + 8 <= w; k += 8)
            {
                Vector128<ushort> va = Vector128.LoadUnsafe(ref pa, (nuint)k), vb = Vector128.LoadUnsafe(ref pb, (nuint)k);
                ((va + vb + Vector128<ushort>.One) >> 1).StoreUnsafe(ref d, (nuint)k);
            }

            for (; k < w; k++)
            {
                Unsafe.Add(ref d, k) = (ushort)((Unsafe.Add(ref pa, k) + Unsafe.Add(ref pb, k) + 1) >> 1);
            }
        }
    }
}
