using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.H264;

/// <summary>Motion: reference indices and motion vectors (8.4.1), P_Skip and direct prediction.</summary>
internal sealed partial class H264SliceDecoder
{
    // Which 4x4 blocks of the current macroblock (bit bx + 4 * by) have their motion for the list being derived.
    private int _decodedMask;

    // Whether the co-located picture is coded otherwise than the current one, so its blocks are found through Colocated.
    private bool _colMapped;

    // Whether RefPicList1[0] is a short-term reference, which colZeroFlag needs (8.4.1.2.2).
    private bool _colShortTerm;

    // Whether the last direct prediction of a whole macroblock gave all 16 blocks the same motion: 1 yes, 0 no, -1 not known.
    private int _directUniform = -1;

    // Whether the last direct prediction was spatial over a whole macroblock with 8x8 inference: its motion is one reference per
    // list and one vector per 8x8 block.
    private bool _directQuarters;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PackMv(int x, int y) => (ushort)x | (y << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MvX(int mv) => (short)mv;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MvY(int mv) => mv >> 16;

    /// <summary>Reads ref_idx and mvd of the partitions and derives their motion vectors.</summary>
    private void ReadMotion(bool ref0, int directMask)
    {
        int lists = _h.IsB ? 2 : 1;
        int parts = _partShape switch { 0 => 1, 3 => 4, _ => 2 };
        for (int list = 0; list < lists; list++)
        {
            int count = list == 0 ? _refCount0 : _refCount1;
            H264Picture[] refs = list == 0 ? _list0 : _list1;
            for (int p = 0; p < parts; p++)
            {
                (int bx, int by, int w, int h) = PartRect(p);
                int refIdx;
                if ((directMask & (1 << p)) != 0)
                {
                    WriteDirectRefs(list, bx, by);
                    continue;
                }

                if (UsesList(p, list))
                {
                    refIdx = count > 1 && !ref0 ? ReadRefIdx(list, bx, by, count) : 0;
                    if (refIdx >= count || refIdx >= refs.Length)
                    {
                        throw H264Errors.Corrupt($"ref_idx_l{list} {refIdx} is beyond the {count} active references.");
                    }
                }
                else
                {
                    refIdx = -1;
                }

                FillRef(list, bx, by, w, h, refIdx, refIdx >= 0 ? refs[refIdx].Id : -1);
            }
        }

        for (int list = 0; list < lists; list++)
        {
            _decodedMask = 0;
            for (int p = 0; p < parts; p++)
            {
                (int bx, int by, int w, int h) = PartRect(p);
                if ((directMask & (1 << p)) != 0)
                {
                    WriteDirectMvs(list, bx, by);
                    continue;
                }

                if (!UsesList(p, list))
                {
                    FillMv(list, bx, by, w, h, 0, 0);
                    continue;
                }

                int refIdx = (list == 0 ? _refIdx0 : _refIdx1)[Block(bx, by)];
                if (_partShape != 3)
                {
                    ReadPartitionMv(list, bx, by, w, h, refIdx);
                    continue;
                }

                int shape = _h.IsB ? H264Tables.BSubShape[_subType[p]] : _subType[p];
                switch (shape)
                {
                    case 0:
                        ReadPartitionMv(list, bx, by, 2, 2, refIdx);
                        break;
                    case 1:
                        ReadPartitionMv(list, bx, by, 2, 1, refIdx);
                        ReadPartitionMv(list, bx, by + 1, 2, 1, refIdx);
                        break;
                    case 2:
                        ReadPartitionMv(list, bx, by, 1, 2, refIdx);
                        ReadPartitionMv(list, bx + 1, by, 1, 2, refIdx);
                        break;
                    default:
                        ReadPartitionMv(list, bx, by, 1, 1, refIdx);
                        ReadPartitionMv(list, bx + 1, by, 1, 1, refIdx);
                        ReadPartitionMv(list, bx, by + 1, 1, 1, refIdx);
                        ReadPartitionMv(list, bx + 1, by + 1, 1, 1, refIdx);
                        break;
                }
            }
        }

        if (!_h.IsB)
        {
            FillRef(1, 0, 0, 4, 4, -1, -1);
            FillMv(1, 0, 0, 4, 4, 0, 0);
        }
    }

    private (int Bx, int By, int W, int H) PartRect(int p) => _partShape switch
    {
        0 => (0, 0, 4, 4),
        1 => (0, p * 2, 4, 2),
        2 => (p * 2, 0, 2, 4),
        _ => ((p & 1) * 2, (p >> 1) * 2, 2, 2),
    };

    /// <summary>Gets whether partition p (an 8x8 block for P_8x8 and B_8x8) is predicted from the list.</summary>
    private bool UsesList(int p, int list)
    {
        int flags = _partShape == 3 ? (_h.IsB ? H264Tables.BSubPred[_subType[p]] : 1) : p == 0 ? _partPred0 : _partPred1;
        return (flags & (1 << list)) != 0;
    }

    private void ReadPartitionMv(int list, int bx, int by, int w, int h, int refIdx)
    {
        int mvdX = ReadMvd(list, bx, by, 0);
        int mvdY = ReadMvd(list, bx, by, 1);
        int mvp = PredictMv(list, bx, by, w, h, refIdx);
        int x = MvX(mvp) + mvdX, y = MvY(mvp) + mvdY;
        if (x < -32768 || x > 32767 || y < -32768 || y > 32767)
        {
            throw H264Errors.Corrupt("A motion vector is out of range.");
        }

        int mvd = Math.Min(Math.Abs(mvdX), 255) | (Math.Min(Math.Abs(mvdY), 255) << 8);
        FillMv(list, bx, by, w, h, PackMv(x, y), mvd);
    }

    private void FillRef(int list, int bx, int by, int w, int h, int refIdx, int id)
    {
        int w4 = _w4;
        int b = Block(bx, by);
        sbyte[] refIdxs = list == 0 ? _refIdx0 : _refIdx1;
        int[] ids = _mo.RefPicId[list];
        if (b < 0 || b + ((h - 1) * w4) + w > Math.Min(refIdxs.Length, ids.Length))
        {
            throw H264Errors.Corrupt("A macroblock lies outside the picture.");
        }

        // Rows of one, two or four blocks, each written at once.
        ref byte r = ref Unsafe.As<sbyte, byte>(ref MemoryMarshal.GetArrayDataReference(refIdxs));
        ref int d = ref MemoryMarshal.GetArrayDataReference(ids);
        uint refs = (byte)refIdx * 0x01010101u;
        for (int y = 0; y < h; y++, b += w4)
        {
            switch (w)
            {
                case 4:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref r, b), refs);
                    Vector128.Create(id).StoreUnsafe(ref d, (nuint)b);
                    break;
                case 2:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref r, b), (ushort)refs);
                    Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref d, b)), ((ulong)(uint)id << 32) | (uint)id);
                    break;
                default:
                    Unsafe.Add(ref r, b) = (byte)refIdx;
                    Unsafe.Add(ref d, b) = id;
                    break;
            }
        }
    }

    /// <summary>Writes a motion vector (and the absolute mvd for CABAC, x low byte, y high byte) to blocks, marking them decoded.</summary>
    private void FillMv(int list, int bx, int by, int w, int h, int mv, int mvd)
    {
        int w4 = _w4;
        int b = Block(bx, by);
        int[] mvs = list == 0 ? _mv0 : _mv1;
        byte[] mvds = _mb.Mvd[list];
        if (b < 0 || b + ((h - 1) * w4) + w > Math.Min(mvs.Length, mvds.Length / 2))
        {
            throw H264Errors.Corrupt("A macroblock lies outside the picture.");
        }

        // x in the low byte, y in the high one: a little-endian 16-bit value per block.
        uint mvd16 = BitConverter.IsLittleEndian ? (ushort)mvd : (ushort)((mvd >> 8) | (mvd << 8));
        ref int m = ref MemoryMarshal.GetArrayDataReference(mvs);
        ref byte dm = ref MemoryMarshal.GetArrayDataReference(mvds);
        int row = ((1 << w) - 1) << (bx + (by * 4));
        for (int y = 0; y < h; y++, b += w4, row <<= 4)
        {
            switch (w)
            {
                case 4:
                    Vector128.Create(mv).StoreUnsafe(ref m, (nuint)b);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, b * 2), mvd16 * 0x0001000100010001ul);
                    break;
                case 2:
                    Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref m, b)), ((ulong)(uint)mv << 32) | (uint)mv);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, b * 2), mvd16 * 0x00010001u);
                    break;
                default:
                    Unsafe.Add(ref m, b) = mv;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, b * 2), (ushort)mvd16);
                    break;
            }

            _decodedMask |= row;
        }
    }

    /// <summary>Gets a neighbouring 4x4 block's motion (6.4.11.7): whether it is available, its refIdx (-1 when the list is unused).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Neighbor(int list, int bx, int by, out int refIdx, out int mv)
    {
        refIdx = -1;
        mv = 0;
        bool available = by < 0
            ? bx < 0 ? (_neighbors & NeighborD) != 0 : bx > 3 ? (_neighbors & NeighborC) != 0 : (_neighbors & NeighborB) != 0
            : bx < 0 ? (_neighbors & NeighborA) != 0 : bx <= 3 && (_decodedMask & (1 << (bx + (by * 4)))) != 0;
        if (!available)
        {
            return false;
        }

        int b = Block(bx, by);
        refIdx = (list == 0 ? _refIdx0 : _refIdx1)[b];
        if (refIdx >= 0)
        {
            mv = (list == 0 ? _mv0 : _mv1)[b];
        }

        return true;
    }

    /// <summary>The motion vector predictor (8.4.1.3) of a partition at (bx, by), w by h 4x4 blocks, with a reference index.</summary>
    private int PredictMv(int list, int bx, int by, int w, int h, int refIdx)
    {
        bool availableA = Neighbor(list, bx - 1, by, out int refA, out int mvA);
        bool availableB = Neighbor(list, bx, by - 1, out int refB, out int mvB);
        bool availableC = Neighbor(list, bx + w, by - 1, out int refC, out int mvC);
        if (!availableC)
        {
            availableC = _mbaff && bx == 0 && by > 0 ? LeftD(list, by, out refC, out mvC) : Neighbor(list, bx - 1, by - 1, out refC, out mvC);
        }

        if (w == 4 && h == 2)
        {
            if (by == 0 && refB == refIdx)
            {
                return mvB;
            }

            if (by == 2 && refA == refIdx)
            {
                return mvA;
            }
        }
        else if (w == 2 && h == 4)
        {
            if (bx == 0 && refA == refIdx)
            {
                return mvA;
            }

            if (bx == 2 && refC == refIdx)
            {
                return mvC;
            }
        }

        return Median(availableA, availableB, availableC, refA, refB, refC, mvA, mvB, mvC, refIdx);
    }

    /// <summary>
    /// D of a partition at row by of an MBAFF macroblock's left column: the location (-1, 4 by - 1), which can lie in the other
    /// macroblock of the left pair than block (-1, by - 1) does.
    /// </summary>
    private bool LeftD(int list, int by, out int refIdx, out int mv)
    {
        refIdx = -1;
        mv = 0;
        if ((_neighbors & NeighborA) == 0)
        {
            return false;
        }

        refIdx = _leftDRef[(list * 4) + by];
        mv = _leftDMv[(list * 4) + by];
        return true;
    }

    private static int Median(bool availableA, bool availableB, bool availableC, int refA, int refB, int refC, int mvA, int mvB, int mvC, int refIdx)
    {
        if (!availableB && !availableC && availableA)
        {
            mvB = mvC = mvA;
            refB = refC = refA;
        }

        int matches = (refA == refIdx ? 1 : 0) + (refB == refIdx ? 1 : 0) + (refC == refIdx ? 1 : 0);
        if (matches == 1)
        {
            return refA == refIdx ? mvA : refB == refIdx ? mvB : mvC;
        }

        return PackMv(Median3(MvX(mvA), MvX(mvB), MvX(mvC)), Median3(MvY(mvA), MvY(mvB), MvY(mvC)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Median3(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>P_Skip (8.4.1.1): refIdx 0 and a zero or predicted motion vector.</summary>
    private void PredictPSkip()
    {
        _decodedMask = 0;
        int id = _list0.Length > 0 ? _list0[0].Id : -1;
        FillRef(0, 0, 0, 4, 4, 0, id);
        FillRef(1, 0, 0, 4, 4, -1, -1);
        FillMv(1, 0, 0, 4, 4, 0, 0);
        _decodedMask = 0;
        bool availableA = Neighbor(0, -1, 0, out int refA, out int mvA);
        bool availableB = Neighbor(0, 0, -1, out int refB, out int mvB);
        int mv = 0;
        if (availableA && availableB && !(refA == 0 && mvA == 0) && !(refB == 0 && mvB == 0))
        {
            // The median of a 16x16 partition (8-212) from the same A and B, and C or D.
            bool availableC = Neighbor(0, 4, -1, out int refC, out int mvC);
            if (!availableC)
            {
                availableC = Neighbor(0, -1, -1, out refC, out mvC);
            }

            mv = Median(availableA, availableB, availableC, refA, refB, refC, mvA, mvB, mvC, 0);
        }
        FillMv(0, 0, 0, 4, 4, mv, 0);
    }

    /// <summary>Derives the direct prediction (8.4.1.2) of the 8x8 blocks in the mask into the direct buffers.</summary>
    private void PredictDirect(int mask)
    {
        if (_list1.Length == 0 || _list0.Length == 0)
        {
            throw H264Errors.Corrupt("A B slice has an empty reference list.");
        }

        _directUniform = -1;
        _directQuarters = false;

        if (_h.DirectSpatialMvPred)
        {
            PredictSpatialDirect(mask);
        }
        else
        {
            PredictTemporalDirect(mask);
        }
    }

    private void PredictSpatialDirect(int mask)
    {
        _decodedMask = 0;
        _directQuarters = false;

        // Neighbours A, B and C (or D) of the whole macroblock: their references give refIdx, and their vectors the median
        // prediction of a 16x16 partition (8-212), so they are read once for both.
        DirectNeighbors n0 = ReadDirectNeighbors(0), n1 = ReadDirectNeighbors(1);
        int ref0 = MinPositive(n0.RefA, MinPositive(n0.RefB, n0.RefC)), ref1 = MinPositive(n1.RefA, MinPositive(n1.RefB, n1.RefC));
        bool zero = ref0 < 0 && ref1 < 0;
        if (zero)
        {
            ref0 = ref1 = 0;
        }

        int mvp0 = !zero && ref0 >= 0 ? Median(n0.AvailableA, n0.AvailableB, n0.AvailableC, n0.RefA, n0.RefB, n0.RefC, n0.MvA, n0.MvB, n0.MvC, ref0) : 0;
        int mvp1 = !zero && ref1 >= 0 ? Median(n1.AvailableA, n1.AvailableB, n1.AvailableC, n1.RefA, n1.RefB, n1.RefC, n1.MvA, n1.MvB, n1.MvC, ref1) : 0;
        Span<int> refIdx = [ref0, ref1];
        Span<int> mvp = [mvp0, mvp1];

        H264Picture col = _list1[0];
        bool colShortTerm = _colShortTerm;
        bool inferred = _sps.Direct8x8Inference;
        int colZero8 = 0;
        if (_colMapped)
        {
            SpatialDirectMapped(mask, refIdx, mvp, zero, colShortTerm);
            return;
        }

        if (mask == 15 && inferred)
        {
            // A whole macroblock with 8x8 inference: one colZero test per 8x8 block, on its corner block, and only the first
            // block of each 8x8 is written, which is all WriteDirectRows reads.
            if (colShortTerm && !zero && !col.MbIntra[_mbAddr])
            {
                bool[] colZeros = col.ColZero;
                colZero8 = (colZeros[Block(0, 0)] ? 1 : 0) | (colZeros[Block(3, 0)] ? 2 : 0) | (colZeros[Block(0, 3)] ? 4 : 0) | (colZeros[Block(3, 3)] ? 8 : 0);
            }

            for (int list = 0; list < 2; list++)
            {
                int r = refIdx[list], m = mvp[list];
                _directRefBlock[list * 16] = (sbyte)r;
                _directMv[list * 16] = r < 0 || (r == 0 && (colZero8 & 1) != 0) ? 0 : m;
                _directMv[(list * 16) + 2] = r < 0 || (r == 0 && (colZero8 & 2) != 0) ? 0 : m;
                _directMv[(list * 16) + 8] = r < 0 || (r == 0 && (colZero8 & 4) != 0) ? 0 : m;
                _directMv[(list * 16) + 10] = r < 0 || (r == 0 && (colZero8 & 8) != 0) ? 0 : m;
            }

            bool split = colZero8 is not 0 and not 15 && ((refIdx[0] == 0 && mvp[0] != 0) || (refIdx[1] == 0 && mvp[1] != 0));
            _directUniform = split ? 0 : 1;
            _directQuarters = true;
            return;
        }

        for (int blk = 0; blk < 16; blk++)
        {
            int bx = blk & 3, by = blk >> 2, quarter = ((by >> 1) * 2) + (bx >> 1);
            if ((mask & (1 << quarter)) == 0)
            {
                continue;
            }

            bool colZero = false;
            if (colShortTerm && !zero)
            {
                // With direct_8x8_inference the four blocks of an 8x8 share their co-located block.
                if (inferred && ((bx | by) & 1) != 0)
                {
                    colZero = (colZero8 & (1 << quarter)) != 0;
                }
                else
                {
                    // The co-located picture stored the test as it was decoded; an intra macroblock never passes it.
                    int cx = inferred ? (bx >> 1) * 3 : bx, cy = inferred ? (by >> 1) * 3 : by;
                    colZero = !col.MbIntra[_mbAddr] && col.ColZero[Block(cx, cy)];
                    colZero8 |= colZero ? 1 << quarter : 0;
                }
            }

            for (int list = 0; list < 2; list++)
            {
                _directRefBlock[(list * 16) + blk] = (sbyte)refIdx[list];
                _directMv[(list * 16) + blk] = refIdx[list] < 0 || (refIdx[list] == 0 && colZero) ? 0 : mvp[list];
            }
        }

        if (mask == 15 && inferred)
        {
            // The motion differs between 8x8 blocks only through colZero, in a list of refIdx 0 with a vector.
            bool split = colZero8 is not 0 and not 15 && ((refIdx[0] == 0 && mvp[0] != 0) || (refIdx[1] == 0 && mvp[1] != 0));
            _directUniform = split ? 0 : 1;
            _directQuarters = true;
        }
    }

    /// <summary>
    /// Spatial direct prediction when the co-located picture is coded otherwise than the current one (a frame against a field
    /// pair, or an MBAFF frame): each 4x4 block's colZeroFlag from the block <see cref="Colocated"/> finds.
    /// </summary>
    private void SpatialDirectMapped(int mask, Span<int> refIdx, Span<int> mvp, bool zero, bool colShortTerm)
    {
        for (int blk = 0; blk < 16; blk++)
        {
            int bx = blk & 3, by = blk >> 2, quarter = ((by >> 1) * 2) + (bx >> 1);
            if ((mask & (1 << quarter)) == 0)
            {
                continue;
            }

            bool colZero = false;
            if (colShortTerm && !zero)
            {
                // Worked out here: ColZero holds only the corner blocks, and a frame and a field read other rows.
                Colocated(bx, by, out H264Picture colPic, out int b, out int mb, out _);
                int list = colPic.RefIdx[0][b] >= 0 ? 0 : 1;
                int mvCol = colPic.Mv[list][b];
                colZero = !colPic.MbIntra[mb] && colPic.RefIdx[list][b] == 0 && (uint)(MvX(mvCol) + 1) <= 2 && (uint)(MvY(mvCol) + 1) <= 2;
            }

            for (int list = 0; list < 2; list++)
            {
                _directRefBlock[(list * 16) + blk] = (sbyte)refIdx[list];
                _directMv[(list * 16) + blk] = refIdx[list] < 0 || (refIdx[list] == 0 && colZero) ? 0 : mvp[list];
            }
        }
    }

    /// <summary>
    /// Works out how the co-located picture's motion is found for this slice (8.4.1.2.1): directly at the current macroblock's
    /// blocks when it is coded as the current picture is, else through <see cref="Colocated"/>.
    /// </summary>
    private void SetColocated()
    {
        _colMapped = false;
        if (!_h.IsB || _list1.Length == 0)
        {
            return;
        }

        _colShortTerm = !_list1[0].IsLongTermReference;

        H264Picture first = _list1[0];
        H264Coding coded = first.Frame.CodedAs;
        _colMapped = _h.Mbaff || (_h.FieldPic ? coded != H264Coding.Fields : coded != H264Coding.Frame);
    }

    /// <summary>
    /// The co-located 4x4 block (8.4.1.2.1, Tables 8-6 and 8-8) of block (bx, by) of the current macroblock, with
    /// direct_8x8_inference: the picture whose motion arrays hold it, the block's index there, the macroblock's index for its
    /// intra flag, and vertMvScale (0 One_To_One, 1 Frm_To_Fld, 2 Fld_To_Frm).
    /// </summary>
    private void Colocated(int bx, int by, out H264Picture colPic, out int block, out int mb, out int vertScale)
    {
        int w = _sps.WidthInMbs;
        int xCol = (bx >> 1) * 12, yCol = (by >> 1) * 12;
        H264Picture first = _list1[0];
        H264Picture g = first.Frame;
        int yM;
        int curr;
        if (_h.FieldPic)
        {
            curr = (_mbY * w) + _mbX;
            int pairTop = (2 * w * (curr / w)) + (curr % w);
            if (g.CodedAs == H264Coding.Fields)
            {
                colPic = first;
                mb = curr;
                yM = yCol;
                vertScale = 0;
            }
            else if (g.CodedAs == H264Coding.Mbaff && IsFieldPair(g, pairTop))
            {
                colPic = g;
                mb = pairTop + (_h.BottomField ? w : 0);
                yM = yCol;
                vertScale = 0;
            }
            else
            {
                colPic = g;
                mb = pairTop + (w * (yCol / 8));
                yM = (2 * yCol) % 16;
                vertScale = 1;
            }
        }
        else if (!_h.Mbaff)
        {
            curr = (_mbY * w) + _mbX;
            if (g.CodedAs == H264Coding.Fields)
            {
                colPic = g.Field(CloserField(g));
                mb = (w * (curr / (2 * w))) + (curr % w);
                yM = (8 * ((curr / w) % 2)) + (4 * (yCol / 8));
                vertScale = 2;
            }
            else
            {
                colPic = g;
                mb = curr;
                yM = yCol;
                vertScale = 0;
            }
        }
        else
        {
            int pair = _mbaffAddr >> 1, pairTop = _pairTop;
            int bottom = _mbaffAddr & 1;
            if (g.CodedAs == H264Coding.Fields)
            {
                colPic = g.Field(_fieldMb ? bottom : CloserField(g));
                mb = pair;
                yM = _fieldMb ? yCol : (8 * bottom) + (4 * (yCol / 8));
                vertScale = _fieldMb ? 0 : 2;
            }
            else
            {
                colPic = g;
                bool fieldX = g.CodedAs == H264Coding.Mbaff && IsFieldPair(g, pairTop);
                if (!_fieldMb && !fieldX)
                {
                    mb = pairTop + (w * bottom);
                    yM = yCol;
                    vertScale = 0;
                }
                else if (!_fieldMb)
                {
                    mb = pairTop + (w * CloserField(g));
                    yM = (8 * bottom) + (4 * (yCol / 8));
                    vertScale = 2;
                }
                else if (!fieldX)
                {
                    mb = pairTop + (w * (yCol / 8));
                    yM = (2 * yCol) % 16;
                    vertScale = 1;
                }
                else
                {
                    mb = pairTop + (w * bottom);
                    yM = yCol;
                    vertScale = 0;
                }
            }
        }

        int cw4 = colPic.WidthMbs * 4;
        block = ((((mb / w) * 4) + (yM >> 2)) * cw4) + ((mb % w) * 4) + (xCol >> 2);
    }

    /// <summary>Gets whether the pair whose top macroblock is at a raster index of an MBAFF frame was coded as fields.</summary>
    private static bool IsFieldPair(H264Picture frame, int pairTop) => frame.FieldPairs is { } pairs && pairs[pairTop];

    /// <summary>
    /// The field of a co-located field pair a frame macroblock reads (Table 8-6): the one nearer the current frame in order
    /// count, the bottom on a tie.
    /// </summary>
    private int CloserField(H264Picture g)
    {
        int curPoc = _mbaff ? _frame.Poc : _pic.Poc;
        return Math.Abs(g.TopPoc - curPoc) < Math.Abs(g.BottomPoc - curPoc) ? 0 : 1;
    }

    /// <summary>Works out <see cref="H264Picture.ColZero"/> for the current macroblock's blocks.</summary>
    private void StoreColZero()
    {
        if (_sps.Direct8x8Inference)
        {
            // Only the corner blocks are read as co-located blocks.
            int w4 = _w4, b0 = Block(0, 0);
            StoreColZero(b0);
            StoreColZero(b0 + 3);
            StoreColZero(b0 + (3 * w4));
            StoreColZero(b0 + (3 * w4) + 3);
            return;
        }

        for (int by = 0; by < 4; by++)
        {
            for (int bx = 0; bx < 4; bx++)
            {
                StoreColZero(Block(bx, by));
            }
        }
    }

    private void StoreColZero(int b)
    {
        int r = _refIdx0[b], mv;
        if (r >= 0)
        {
            mv = _mv0[b];
        }
        else
        {
            r = _refIdx1[b];
            mv = _mv1[b];
        }

        _mo.ColZero[b] = r == 0 && (uint)(MvX(mv) + 1) <= 2 && (uint)(MvY(mv) + 1) <= 2;
    }

    /// <summary>Neighbours A, B and C (D when C is missing) of the whole macroblock in a list.</summary>
    private DirectNeighbors ReadDirectNeighbors(int list)
    {
        DirectNeighbors n;
        n.AvailableA = Neighbor(list, -1, 0, out n.RefA, out n.MvA);
        n.AvailableB = Neighbor(list, 0, -1, out n.RefB, out n.MvB);
        n.AvailableC = Neighbor(list, 4, -1, out n.RefC, out n.MvC) || Neighbor(list, -1, -1, out n.RefC, out n.MvC);
        return n;
    }

    private struct DirectNeighbors
    {
        public bool AvailableA, AvailableB, AvailableC;
        public int RefA, RefB, RefC, MvA, MvB, MvC;
    }

    private static int MinPositive(int x, int y) => x >= 0 && y >= 0 ? Math.Min(x, y) : Math.Max(x, y);

    /// <summary>
    /// The co-located 4x4 block's motion (8.4.1.2.1) for block (bx, by): with direct_8x8_inference the corner block of its 8x8.
    /// Gives refIdxCol (-1 for intra), mvCol and the Id of the picture refIdxCol names.
    /// </summary>
    private void ColocatedMotion(int bx, int by, out int refCol, out int mvCol, out int refPicId)
    {
        if (_colMapped)
        {
            ColocatedMotionMapped(bx, by, out refCol, out mvCol, out refPicId);
            return;
        }

        H264Picture col = _list1[0];
        if (_sps.Direct8x8Inference)
        {
            bx = (bx >> 1) * 3;
            by = (by >> 1) * 3;
        }

        int b = Block(bx, by);
        if (col.MbIntra[_mbAddr])
        {
            refCol = -1;
            mvCol = 0;
            refPicId = -1;
            return;
        }

        int list = col.RefIdx[0][b] >= 0 ? 0 : 1;
        refCol = col.RefIdx[list][b];
        mvCol = col.Mv[list][b];
        refPicId = col.RefPicId[list][b];
    }

    /// <summary>
    /// The co-located motion through <see cref="Colocated"/>, in the current macroblock's units: the vertical vector halved
    /// from a frame to a field or doubled from a field to a frame (8.4.1.2.3), and refPicId the picture the current macroblock
    /// refers to: the field of the co-located frame reference with the current parity, or the frame of a co-located field one.
    /// </summary>
    private void ColocatedMotionMapped(int bx, int by, out int refCol, out int mvCol, out int refPicId)
    {
        Colocated(bx, by, out H264Picture col, out int b, out int mb, out int vertScale);
        if (col.MbIntra[mb])
        {
            refCol = -1;
            mvCol = 0;
            refPicId = -1;
            return;
        }

        int list = col.RefIdx[0][b] >= 0 ? 0 : 1;
        refCol = col.RefIdx[list][b];
        mvCol = col.Mv[list][b];
        refPicId = col.RefPicId[list][b];
        if (vertScale == 1)
        {
            mvCol = PackMv(MvX(mvCol), MvY(mvCol) / 2);
            refPicId = (refPicId & ~3) + 1 + _pic.Parity;
        }
        else if (vertScale == 2)
        {
            mvCol = PackMv(MvX(mvCol), MvY(mvCol) * 2);
            refPicId &= ~3;
        }
    }

    private void PredictTemporalDirect(int mask)
    {
        H264Picture pic1 = _list1[0];
        int currPoc = _pic.Poc;
        for (int blk = 0; blk < 16; blk++)
        {
            int bx = blk & 3, by = blk >> 2;
            if ((mask & (1 << (((by >> 1) * 2) + (bx >> 1)))) == 0)
            {
                continue;
            }

            ColocatedMotion(bx, by, out int refCol, out int mvCol, out int refPicId);
            int refIdxL0 = 0;
            if (refCol >= 0)
            {
                refIdxL0 = -1;
                for (int i = 0; i < _list0.Length && i < _refCount0; i++)
                {
                    if (_list0[i].Id == refPicId)
                    {
                        refIdxL0 = i;
                        break;
                    }
                }

                if (refIdxL0 < 0)
                {
                    throw H264Errors.Corrupt("The co-located block of a temporal direct block refers to a picture not in list 0.");
                }
            }

            H264Picture pic0 = _list0[refIdxL0];
            int mvL0, mvL1;
            int tb = Math.Clamp(currPoc - pic0.Poc, -128, 127);
            int td = Math.Clamp(pic1.Poc - pic0.Poc, -128, 127);
            if (pic0.IsLongTermReference || td == 0)
            {
                mvL0 = mvCol;
                mvL1 = 0;
            }
            else
            {
                int tx = (16384 + Math.Abs(td / 2)) / td;
                int scale = Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
                int x = ((scale * MvX(mvCol)) + 128) >> 8, y = ((scale * MvY(mvCol)) + 128) >> 8;
                mvL0 = PackMv(x, y);
                mvL1 = PackMv(x - MvX(mvCol), y - MvY(mvCol));
            }

            _directRefBlock[blk] = (sbyte)refIdxL0;
            _directRefBlock[16 + blk] = 0;
            _directMv[blk] = mvL0;
            _directMv[16 + blk] = mvL1;
        }
    }

    /// <summary>Writes the direct prediction of the 8x8 blocks in the mask, both lists, to the picture (B_Skip, B_Direct_16x16).</summary>
    private void WriteDirect(int mask)
    {
        int w4 = _w4, b0 = Block(0, 0);
        if (mask == 15 && _directQuarters)
        {
            WriteDirectRows(w4, b0);
            return;
        }
        for (int list = 0; list < 2; list++)
        {
            H264Picture[] refs = list == 0 ? _list0 : _list1;
            sbyte[] refIdxs = list == 0 ? _refIdx0 : _refIdx1;
            int[] ids = _mo.RefPicId[list], mvs = _mo.Mv[list];
            byte[] mvds = _mb.Mvd[list];
            for (int blk = 0; blk < 16; blk++)
            {
                int x = blk & 3, y = blk >> 2;
                if ((mask & (1 << (((y >> 1) * 2) + (x >> 1)))) == 0)
                {
                    continue;
                }

                int r = _directRefBlock[(list * 16) + blk];
                int b = b0 + (y * w4) + x;
                refIdxs[b] = (sbyte)r;
                ids[b] = r >= 0 && r < refs.Length ? refs[r].Id : -1;
                mvs[b] = r >= 0 ? _directMv[(list * 16) + blk] : 0;
                mvds[b * 2] = 0;
                mvds[(b * 2) + 1] = 0;
            }
        }

        for (int p = 0; p < 4; p++)
        {
            if ((mask & (1 << p)) != 0)
            {
                _decodedMask |= 0x33 << (((p & 1) * 2) + ((p >> 1) * 8));
            }
        }
    }

    /// <summary>
    /// Writes a whole macroblock of spatial direct prediction with 8x8 inference: one reference per list, and a vector per 8x8
    /// block, so each row of four blocks is two pairs of equal vectors.
    /// </summary>
    private void WriteDirectRows(int w4, int b0)
    {
        if (b0 + (3 * w4) + 4 > _mo.Mv[0].Length)
        {
            throw H264Errors.Corrupt("A macroblock lies outside the picture.");
        }

        for (int list = 0; list < 2; list++)
        {
            H264Picture[] refs = list == 0 ? _list0 : _list1;
            int r = _directRefBlock[list * 16];
            int id = r >= 0 && r < refs.Length ? refs[r].Id : -1;
            ref sbyte refIdxs = ref MemoryMarshal.GetArrayDataReference(_mo.RefIdx[list]);
            ref int ids = ref MemoryMarshal.GetArrayDataReference(_mo.RefPicId[list]);
            ref int mvs = ref MemoryMarshal.GetArrayDataReference(_mo.Mv[list]);
            ref byte mvds = ref MemoryMarshal.GetArrayDataReference(_mb.Mvd[list]);
            uint refRow = (byte)r * 0x01010101u;
            Vector128<int> idRow = Vector128.Create(id);
            for (int y = 0; y < 4; y++)
            {
                int b = b0 + (y * w4);
                int top = (list * 16) + ((y >> 1) * 8);
                int left = r >= 0 ? _directMv[top] : 0, right = r >= 0 ? _directMv[top + 2] : 0;
                Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref refIdxs, b)), refRow);
                idRow.StoreUnsafe(ref ids, (nuint)b);
                Vector128.Create(left, left, right, right).StoreUnsafe(ref mvs, (nuint)b);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref mvds, b * 2), 0ul);
            }
        }

        _decodedMask = 0xFFFF;
    }

    /// <summary>
    /// Where block (x, y)'s direct reference or vector is kept: the whole-macroblock 8x8 form of PredictSpatialDirect keeps one
    /// reference per list, in block 0, and one vector per 8x8 block, in its first block.
    /// </summary>
    private int DirectSource(int list, int x, int y, bool reference) =>
        (list * 16) + (!_directQuarters ? (y * 4) + x : reference ? 0 : ((y >> 1) * 8) + ((x >> 1) * 2));

    private void WriteDirectRefs(int list, int bx, int by)
    {
        H264Picture[] refs = list == 0 ? _list0 : _list1;
        sbyte[] refIdxs = list == 0 ? _refIdx0 : _refIdx1;
        int[] ids = _mo.RefPicId[list];
        for (int y = by; y < by + 2; y++)
        {
            for (int x = bx; x < bx + 2; x++)
            {
                int r = _directRefBlock[DirectSource(list, x, y, true)];
                int b = Block(x, y);
                refIdxs[b] = (sbyte)r;
                ids[b] = r >= 0 && r < refs.Length ? refs[r].Id : -1;
            }
        }
    }

    private void WriteDirectMvs(int list, int bx, int by)
    {
        int[] mvs = list == 0 ? _mv0 : _mv1;
        byte[] mvds = _mb.Mvd[list];
        for (int y = by; y < by + 2; y++)
        {
            int b = Block(bx, y);
            for (int x = bx; x < bx + 2; x++, b++)
            {
                bool used = _directRefBlock[DirectSource(list, x, y, true)] >= 0;
                mvs[b] = used ? _directMv[DirectSource(list, x, y, false)] : 0;
            }

            mvds.AsSpan(Block(bx, y) * 2, 4).Clear();
            _decodedMask |= 3 << (bx + (y * 4));
        }
    }
}
