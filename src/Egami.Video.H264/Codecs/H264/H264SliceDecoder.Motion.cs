using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.H264;

/// <summary>Motion: reference indices and motion vectors (8.4.1), P_Skip and direct prediction.</summary>
internal sealed partial class H264SliceDecoder
{
    // Which 4x4 blocks of the current macroblock (bit bx + 4 * by) have their motion for the list being derived.
    private int _decodedMask;

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
            int count = list == 0 ? _h.NumRefIdxL0 : _h.NumRefIdxL1;
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

                int refIdx = _pic.RefIdx[list][Block(bx, by)];
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
        int w4 = _mb.W4;
        int b = Block(bx, by);
        sbyte[] refIdxs = _pic.RefIdx[list];
        int[] ids = _pic.RefPicId[list];
        for (int y = 0; y < h; y++, b += w4)
        {
            refIdxs.AsSpan(b, w).Fill((sbyte)refIdx);
            ids.AsSpan(b, w).Fill(id);
        }
    }

    /// <summary>Writes a motion vector (and the absolute mvd for CABAC, x low byte, y high byte) to blocks, marking them decoded.</summary>
    private void FillMv(int list, int bx, int by, int w, int h, int mv, int mvd)
    {
        int w4 = _mb.W4;
        int b = Block(bx, by);
        int[] mvs = _pic.Mv[list];
        byte[] mvds = _mb.Mvd[list];
        int row = ((1 << w) - 1) << (bx + (by * 4));
        for (int y = 0; y < h; y++, b += w4, row <<= 4)
        {
            mvs.AsSpan(b, w).Fill(mv);

            // x in the low byte, y in the high one: a little-endian 16-bit value per block.
            MemoryMarshal.Cast<byte, ushort>(mvds.AsSpan(b * 2, w * 2)).Fill(BitConverter.IsLittleEndian ? (ushort)mvd : (ushort)((mvd >> 8) | (mvd << 8)));
            _decodedMask |= row;
        }
    }

    /// <summary>Gets a neighbouring 4x4 block's motion (6.4.11.7): whether it is available, its refIdx (-1 when the list is unused).</summary>
    private bool Neighbor(int list, int bx, int by, out int refIdx, out int mv)
    {
        refIdx = -1;
        mv = 0;
        int addr;
        if (by < 0)
        {
            if (bx < 0)
            {
                addr = _mbX > 0 && _mbY > 0 ? _mbAddr - _widthMbs - 1 : -1;
            }
            else if (bx > 3)
            {
                addr = _mbX < _widthMbs - 1 && _mbY > 0 ? _mbAddr - _widthMbs + 1 : -1;
            }
            else
            {
                addr = TopMb;
            }
        }
        else if (bx < 0)
        {
            addr = LeftMb;
        }
        else if (bx > 3)
        {
            return false;
        }
        else
        {
            if ((_decodedMask & (1 << (bx + (by * 4)))) == 0)
            {
                return false;
            }

            addr = _mbAddr;
        }

        if (addr != _mbAddr && !Available(addr))
        {
            return false;
        }

        int b = Block(bx, by);
        refIdx = _pic.RefIdx[list][b];
        if (refIdx >= 0)
        {
            mv = _pic.Mv[list][b];
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
            availableC = Neighbor(list, bx - 1, by - 1, out refC, out mvC);
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
        int mv = !availableA || !availableB || (refA == 0 && mvA == 0) || (refB == 0 && mvB == 0) ? 0 : PredictMv(0, 0, 0, 4, 4, 0);
        FillMv(0, 0, 0, 4, 4, mv, 0);
    }

    /// <summary>Derives the direct prediction (8.4.1.2) of the 8x8 blocks in the mask into the direct buffers.</summary>
    private void PredictDirect(int mask)
    {
        if (_list1.Length == 0 || _list0.Length == 0)
        {
            throw H264Errors.Corrupt("A B slice has an empty reference list.");
        }

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
        Span<int> refIdx = stackalloc int[2];
        Span<int> mvp = stackalloc int[2];
        for (int list = 0; list < 2; list++)
        {
            Neighbor(list, -1, 0, out int refA, out _);
            Neighbor(list, 0, -1, out int refB, out _);
            if (!Neighbor(list, 4, -1, out int refC, out _))
            {
                Neighbor(list, -1, -1, out refC, out _);
            }

            refIdx[list] = MinPositive(refA, MinPositive(refB, refC));
        }

        bool zero = refIdx[0] < 0 && refIdx[1] < 0;
        if (zero)
        {
            refIdx[0] = refIdx[1] = 0;
        }

        for (int list = 0; list < 2; list++)
        {
            mvp[list] = !zero && refIdx[list] >= 0 ? PredictMv(list, 0, 0, 4, 4, refIdx[list]) : 0;
        }

        H264Picture col = _list1[0];
        bool colShortTerm = !col.IsLongTermReference;
        bool inferred = _sps.Direct8x8Inference;
        int colZero8 = 0;
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
                    ColocatedMotion(bx, by, out int refCol, out int mvCol, out _);
                    colZero = refCol == 0 && MvX(mvCol) is >= -1 and <= 1 && MvY(mvCol) is >= -1 and <= 1;
                    colZero8 |= colZero ? 1 << quarter : 0;
                }
            }

            for (int list = 0; list < 2; list++)
            {
                _directRefBlock[(list * 16) + blk] = (sbyte)refIdx[list];
                _directMv[(list * 16) + blk] = refIdx[list] < 0 || (refIdx[list] == 0 && colZero) ? 0 : mvp[list];
            }
        }
    }

    private static int MinPositive(int x, int y) => x >= 0 && y >= 0 ? Math.Min(x, y) : Math.Max(x, y);

    /// <summary>
    /// The co-located 4x4 block's motion (8.4.1.2.1) for block (bx, by): with direct_8x8_inference the corner block of its 8x8.
    /// Gives refIdxCol (-1 for intra), mvCol and the Id of the picture refIdxCol names.
    /// </summary>
    private void ColocatedMotion(int bx, int by, out int refCol, out int mvCol, out int refPicId)
    {
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
                for (int i = 0; i < _list0.Length && i < _h.NumRefIdxL0; i++)
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
        int w4 = _mb.W4, b0 = Block(0, 0);
        for (int list = 0; list < 2; list++)
        {
            H264Picture[] refs = list == 0 ? _list0 : _list1;
            sbyte[] refIdxs = _pic.RefIdx[list];
            int[] ids = _pic.RefPicId[list], mvs = _pic.Mv[list];
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

    private void WriteDirectRefs(int list, int bx, int by)
    {
        H264Picture[] refs = list == 0 ? _list0 : _list1;
        sbyte[] refIdxs = _pic.RefIdx[list];
        int[] ids = _pic.RefPicId[list];
        for (int y = by; y < by + 2; y++)
        {
            for (int x = bx; x < bx + 2; x++)
            {
                int r = _directRefBlock[(list * 16) + (y * 4) + x];
                int b = Block(x, y);
                refIdxs[b] = (sbyte)r;
                ids[b] = r >= 0 && r < refs.Length ? refs[r].Id : -1;
            }
        }
    }

    private void WriteDirectMvs(int list, int bx, int by)
    {
        int[] mvs = _pic.Mv[list];
        byte[] mvds = _mb.Mvd[list];
        for (int y = by; y < by + 2; y++)
        {
            int b = Block(bx, y);
            for (int x = bx; x < bx + 2; x++, b++)
            {
                bool used = _directRefBlock[(list * 16) + (y * 4) + x] >= 0;
                mvs[b] = used ? _directMv[(list * 16) + (y * 4) + x] : 0;
            }

            mvds.AsSpan(Block(bx, y) * 2, 4).Clear();
            _decodedMask |= 3 << (bx + (y * 4));
        }
    }
}
