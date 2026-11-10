namespace Egami.Video.Codecs.H264;

/// <summary>The P and B macroblock syntax (7.3.5.1, 7.3.5.2) and motion vector prediction (8.4.1), as the decoder reads and derives them.</summary>
internal sealed partial class H264MbSyntax
{
    /// <summary>
    /// Lists the macroblock's motion partitions in the order their mvd are coded, as (bx, by, w, h) in 4x4 blocks; returns how
    /// many there are.
    /// </summary>
    internal static int Parts(H264MbCode code, Span<int> parts, bool bSlice = false)
    {
        switch (code.Partition)
        {
            case 0:
                parts[0] = Part(0, 0, 4, 4);
                return 1;
            case 1:
                parts[0] = Part(0, 0, 4, 2);
                parts[1] = Part(0, 2, 4, 2);
                return 2;
            case 2:
                parts[0] = Part(0, 0, 2, 4);
                parts[1] = Part(2, 0, 2, 4);
                return 2;
        }

        int n = 0;
        for (int i8 = 0; i8 < 4; i8++)
        {
            int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2;
            n += SubParts(bSlice ? 0 : code.SubTypes[i8], bx, by, parts[n..]);
        }

        return n;
    }

    /// <summary>The sub-macroblock partitions of an 8x8 block at (bx, by) of a sub_mb_type, in coding order.</summary>
    internal static int SubParts(int subType, int bx, int by, Span<int> parts)
    {
        switch (subType)
        {
            case 0:
                parts[0] = Part(bx, by, 2, 2);
                return 1;
            case 1:
                parts[0] = Part(bx, by, 2, 1);
                parts[1] = Part(bx, by + 1, 2, 1);
                return 2;
            case 2:
                parts[0] = Part(bx, by, 1, 2);
                parts[1] = Part(bx + 1, by, 1, 2);
                return 2;
            default:
                parts[0] = Part(bx, by, 1, 1);
                parts[1] = Part(bx + 1, by, 1, 1);
                parts[2] = Part(bx, by + 1, 1, 1);
                parts[3] = Part(bx + 1, by + 1, 1, 1);
                return 4;
        }
    }

    /// <summary>Packs a partition's position and size.</summary>
    internal static int Part(int bx, int by, int w, int h) => bx | (by << 4) | (w << 8) | (h << 12);

    /// <summary>The number of ref_idx a macroblock codes: one per partition, four for P_8x8.</summary>
    private static int RefParts(H264MbCode code) => code.Partition switch { 0 => 1, 3 => 4, _ => 2 };

    /// <summary>The top-left 4x4 block of the macroblock's ref_idx partition i.</summary>
    private static (int Bx, int By) RefPart(H264MbCode code, int i) => code.Partition switch
    {
        0 => (0, 0),
        1 => (0, i * 2),
        2 => (i * 2, 0),
        _ => ((i & 1) * 2, (i >> 1) * 2),
    };

    /// <summary>
    /// Whether a macroblock splits no 8x8 block further, so it may use the 8x8 transform: the encoder's B sub-macroblocks are
    /// all 8x8, and direct ones are too with direct_8x8_inference_flag.
    /// </summary>
    internal static bool NoSub8x8(H264MbCode code, bool bSlice = false) =>
        bSlice || code.Partition != 3 || (code.SubTypes[0] | code.SubTypes[1] | code.SubTypes[2] | code.SubTypes[3]) == 0;

    /// <summary>The prediction lists of a B partition (or sub-macroblock) i: bit 0 list 0, bit 1 list 1.</summary>
    internal static int PredOf(H264MbCode code, int i) => (code.RefIdx[i] >= 0 ? 1 : 0) | (code.RefIdx1[i] >= 0 ? 2 : 0);

    /// <summary>The mb_type of an inter macroblock of a B slice (Table 7-14).</summary>
    internal static int BType(H264MbCode code)
    {
        if (code.Direct)
        {
            return 0;
        }

        switch (code.Partition)
        {
            case 0:
                return PredOf(code, 0);
            case 3:
                return 22;
        }

        int pred0 = PredOf(code, 0), pred1 = PredOf(code, 1);
        ReadOnlySpan<byte> pairs = H264Tables.BPartPred;
        for (int k = 0; k < 9; k++)
        {
            if (pairs[2 * k] == pred0 && pairs[(2 * k) + 1] == pred1)
            {
                return 4 + (2 * k) + (code.Partition == 2 ? 1 : 0);
            }
        }

        throw new InvalidOperationException("A B partition predicts from no list.");
    }

    // ---- CABAC ----

    private void WriteInterCabac<T>(H264MbCode code, ref T sink)
        where T : struct, ICabacSink
    {
        if (SliceType == H264SliceType.B)
        {
            WriteBCabac(code, ref sink);
            return;
        }

        byte[] ctx = Contexts;

        // mb_type (Table 9-37, P): 0 0 0 16x16, 0 1 1 16x8, 0 1 0 8x16, 0 0 1 8x8.
        sink.Encode(ref ctx[14], 0);
        switch (code.Partition)
        {
            case 0:
                sink.Encode(ref ctx[15], 0);
                sink.Encode(ref ctx[16], 0);
                break;
            case 1:
                sink.Encode(ref ctx[15], 1);
                sink.Encode(ref ctx[17], 1);
                break;
            case 2:
                sink.Encode(ref ctx[15], 1);
                sink.Encode(ref ctx[17], 0);
                break;
            default:
                sink.Encode(ref ctx[15], 0);
                sink.Encode(ref ctx[16], 1);
                for (int i = 0; i < 4; i++)
                {
                    // sub_mb_type: 1 8x8, 0 0 8x4, 0 1 1 4x8, 0 1 0 4x4.
                    int sub = code.SubTypes[i];
                    sink.Encode(ref ctx[21], sub == 0 ? 1 : 0);
                    if (sub != 0)
                    {
                        sink.Encode(ref ctx[22], sub == 1 ? 0 : 1);
                        if (sub != 1)
                        {
                            sink.Encode(ref ctx[23], sub == 2 ? 1 : 0);
                        }
                    }
                }

                break;
        }

        if (RefCount > 1)
        {
            for (int i = 0; i < RefParts(code); i++)
            {
                (int bx, int by) = RefPart(code, i);
                RefIdxCabac(ref sink, 0, bx, by, code.RefIdx[i]);
            }
        }

        Span<int> parts = stackalloc int[16];
        int n = Parts(code, parts);
        for (int i = 0; i < n; i++)
        {
            int bx = parts[i] & 15, by = (parts[i] >> 4) & 15;
            int mvd = code.Mvd[(by * 4) + bx];
            MvdCabac(ref sink, 0, bx, by, 0, H264SliceDecoder.MvX(mvd));
            MvdCabac(ref sink, 0, bx, by, 1, H264SliceDecoder.MvY(mvd));
        }

        ResidualPartCabac(code, ref sink);
    }

    /// <summary>coded_block_pattern, transform_size_8x8_flag, mb_qp_delta and the residual of an inter macroblock, with CABAC.</summary>
    private void ResidualPartCabac<T>(H264MbCode code, ref T sink)
        where T : struct, ICabacSink
    {
        byte[] ctx = Contexts;
        CbpCabac(ref sink, code.Cbp);
        if ((code.Cbp & 15) != 0 && Transform8x8Mode && NoSub8x8(code, SliceType == H264SliceType.B))
        {
            int inc = (_availableLeft && _mb.Transform8x8[_left] ? 1 : 0) + (_availableTop && _mb.Transform8x8[_top] ? 1 : 0);
            sink.Encode(ref ctx[399 + inc], code.Transform8x8 ? 1 : 0);
        }

        if (code.Cbp != 0)
        {
            QpDeltaCabac(ref sink, code.Qp - LastQp);
            ResidualCabac(code, ref sink);
        }
    }

    /// <summary>A B macroblock with CABAC: mb_type (Table 9-37, B), sub_mb_type, then each list's ref_idx and mvd.</summary>
    private void WriteBCabac<T>(H264MbCode code, ref T sink)
        where T : struct, ICabacSink
    {
        byte[] ctx = Contexts;
        int type = BType(code);
        int inc = (_availableLeft && _mb.Kind[_left] is not (H264MbKind.BSkip or H264MbKind.BDirect) ? 1 : 0)
            + (_availableTop && _mb.Kind[_top] is not (H264MbKind.BSkip or H264MbKind.BDirect) ? 1 : 0);
        sink.Encode(ref ctx[27 + inc], type == 0 ? 0 : 1);
        if (type != 0)
        {
            if (type <= 2)
            {
                sink.Encode(ref ctx[30], 0);
                sink.Encode(ref ctx[32], type - 1);
            }
            else
            {
                sink.Encode(ref ctx[30], 1);
                int bits = type <= 10 ? type - 3 : type == 11 ? 14 : type == 22 ? 15 : (type + 4) >> 1;
                sink.Encode(ref ctx[31], (bits >> 3) & 1);
                sink.Encode(ref ctx[32], (bits >> 2) & 1);
                sink.Encode(ref ctx[32], (bits >> 1) & 1);
                sink.Encode(ref ctx[32], bits & 1);
                if (type is >= 12 and <= 21)
                {
                    sink.Encode(ref ctx[32], (type + 4) & 1);
                }
            }
        }

        if (type == 22)
        {
            for (int i = 0; i < 4; i++)
            {
                // sub_mb_type (Table 9-38, B): 0 direct, 1 0 0 L0, 1 0 1 L1, 1 1 0 0 0 Bi.
                int sub = code.SubTypes[i];
                sink.Encode(ref ctx[36], sub == 0 ? 0 : 1);
                if (sub == 0)
                {
                    continue;
                }

                sink.Encode(ref ctx[37], sub == 3 ? 1 : 0);
                if (sub < 3)
                {
                    sink.Encode(ref ctx[39], sub - 1);
                }
                else
                {
                    sink.Encode(ref ctx[38], 0);
                    sink.Encode(ref ctx[39], 0);
                    sink.Encode(ref ctx[39], 0);
                }
            }
        }

        if (type != 0)
        {
            for (int list = 0; list < 2; list++)
            {
                int[] refs = list == 0 ? code.RefIdx : code.RefIdx1;
                if ((list == 0 ? RefCount : RefCount1) <= 1)
                {
                    continue;
                }

                for (int i = 0; i < RefParts(code); i++)
                {
                    if (refs[i] >= 0 && !(type == 22 && code.SubTypes[i] == 0))
                    {
                        (int bx, int by) = RefPart(code, i);
                        RefIdxCabac(ref sink, list, bx, by, refs[i]);
                    }
                }
            }

            Span<int> parts = stackalloc int[16];
            int n = Parts(code, parts, bSlice: true);
            for (int list = 0; list < 2; list++)
            {
                int[] mvds = list == 0 ? code.Mvd : code.Mvd1;
                for (int i = 0; i < n; i++)
                {
                    int bx = parts[i] & 15, by = (parts[i] >> 4) & 15;
                    if (UsesList(code, type, list, bx, by))
                    {
                        int mvd = mvds[(by * 4) + bx];
                        MvdCabac(ref sink, list, bx, by, 0, H264SliceDecoder.MvX(mvd));
                        MvdCabac(ref sink, list, bx, by, 1, H264SliceDecoder.MvY(mvd));
                    }
                }
            }
        }

        ResidualPartCabac(code, ref sink);
    }

    /// <summary>Whether the coded (not direct) partition of a B macroblock covering block (bx, by) has an mvd in a list.</summary>
    private static bool UsesList(H264MbCode code, int type, int list, int bx, int by) =>
        !(type == 22 && code.SubTypes[((by >> 1) * 2) + (bx >> 1)] == 0) && BlockRef(code, list, bx, by) >= 0;

    /// <summary>ref_idx_lX (9.3.2.1 unary, 9.3.3.1.1.6 contexts) of the partition whose first 4x4 block is (bx, by).</summary>
    private void RefIdxCabac<T>(ref T sink, int list, int bx, int by, int refIdx)
        where T : struct, ICabacSink
    {
        byte[] ctx = Contexts;
        int inc = (RefIdxCond(list, bx - 1, by) ? 1 : 0) + (RefIdxCond(list, bx, by - 1) ? 2 : 0);
        sink.Encode(ref ctx[54 + inc], refIdx > 0 ? 1 : 0);
        for (int k = 1; k <= refIdx; k++)
        {
            sink.Encode(ref ctx[k == 1 ? 58 : 59], k < refIdx ? 1 : 0);
        }
    }

    private bool RefIdxCond(int list, int bx, int by)
    {
        bool inside = bx >= 0 && by >= 0;
        int addr = bx < 0 ? _left : by < 0 ? _top : _addr;
        if (!inside && !(bx < 0 ? _availableLeft : _availableTop))
        {
            return false;
        }

        H264MbKind kind = _mb.Kind[addr];
        if (H264MbState.IsIntra(kind) || H264MbState.IsSkip(kind) || kind == H264MbKind.BDirect)
        {
            return false;
        }

        int nx = bx & 3, ny = by & 3;
        if ((_mb.Direct8x8[addr] & (1 << (((ny >> 1) * 2) + (nx >> 1)))) != 0)
        {
            return false;
        }

        return Motion!.RefIdx[list][_b0 + (by * _w4) + bx] > 0;
    }

    /// <summary>One mvd_lX component (9.3.2.3 UEG3, 9.3.3.1.1.7 contexts) of the partition whose first 4x4 block is (bx, by).</summary>
    private void MvdCabac<T>(ref T sink, int list, int bx, int by, int component, int value)
        where T : struct, ICabacSink
    {
        byte[] mvds = _mb.Mvd[list];
        int at = ((_b0 + (by * _w4) + bx) * 2) + component;
        int sum = (bx > 0 || _availableLeft ? mvds[at - 2] : 0) + (by > 0 || _availableTop ? mvds[at - (2 * _w4)] : 0);
        int ctxBase = component == 0 ? 40 : 47;
        byte[] ctx = Contexts;
        int abs = Math.Abs(value);
        sink.Encode(ref ctx[ctxBase + (sum < 3 ? 0 : sum > 32 ? 2 : 1)], abs > 0 ? 1 : 0);
        if (abs == 0)
        {
            return;
        }

        int prefix = Math.Min(abs, 9);
        int inc = 3;
        for (int k = 1; k < prefix; k++)
        {
            sink.Encode(ref ctx[ctxBase + inc], 1);
            inc = Math.Min(inc + 1, 6);
        }

        if (abs < 9)
        {
            sink.Encode(ref ctx[ctxBase + inc], 0);
        }
        else
        {
            // Exp-Golomb suffix, k = 3, in bypass bins.
            int v = abs - 9, k = 3;
            while (v >= 1 << k)
            {
                sink.Bypass(1);
                v -= 1 << k;
                k++;
            }

            sink.Bypass(0);
            for (int b = k - 1; b >= 0; b--)
            {
                sink.Bypass((v >> b) & 1);
            }
        }

        sink.Bypass(value < 0 ? 1 : 0);
    }

    // ---- CAVLC ----

    private void WriteInterCavlc<T>(H264MbCode code, ref T sink)
        where T : struct, IBitSink
    {
        if (SliceType == H264SliceType.B)
        {
            WriteBCavlc(code, ref sink);
            return;
        }

        BitSinkCodes.Ue(ref sink, (uint)code.Partition);
        if (code.Partition == 3)
        {
            for (int i = 0; i < 4; i++)
            {
                BitSinkCodes.Ue(ref sink, (uint)code.SubTypes[i]);
            }
        }

        if (RefCount > 1)
        {
            for (int i = 0; i < RefParts(code); i++)
            {
                // te(v): one inverted bit when the range is 0 to 1.
                if (RefCount == 2)
                {
                    sink.U(1, code.RefIdx[i] == 0 ? 1u : 0u);
                }
                else
                {
                    BitSinkCodes.Ue(ref sink, (uint)code.RefIdx[i]);
                }
            }
        }

        Span<int> parts = stackalloc int[16];
        int n = Parts(code, parts);
        for (int i = 0; i < n; i++)
        {
            int bx = parts[i] & 15, by = (parts[i] >> 4) & 15;
            int mvd = code.Mvd[(by * 4) + bx];
            BitSinkCodes.Se(ref sink, H264SliceDecoder.MvX(mvd));
            BitSinkCodes.Se(ref sink, H264SliceDecoder.MvY(mvd));
        }

        ResidualPartCavlc(code, ref sink);
    }

    private void ResidualPartCavlc<T>(H264MbCode code, ref T sink)
        where T : struct, IBitSink
    {
        BitSinkCodes.Ue(ref sink, CbpInterCode[code.Cbp]);
        if ((code.Cbp & 15) != 0 && Transform8x8Mode && NoSub8x8(code, SliceType == H264SliceType.B))
        {
            sink.U(1, code.Transform8x8 ? 1u : 0u);
        }

        if (code.Cbp != 0)
        {
            BitSinkCodes.Se(ref sink, code.Qp - LastQp);
            ResidualCavlc(code, ref sink);
        }
    }

    private void WriteBCavlc<T>(H264MbCode code, ref T sink)
        where T : struct, IBitSink
    {
        int type = BType(code);
        BitSinkCodes.Ue(ref sink, (uint)type);
        if (type == 22)
        {
            for (int i = 0; i < 4; i++)
            {
                BitSinkCodes.Ue(ref sink, (uint)code.SubTypes[i]);
            }
        }

        if (type != 0)
        {
            for (int list = 0; list < 2; list++)
            {
                int[] refs = list == 0 ? code.RefIdx : code.RefIdx1;
                int count = list == 0 ? RefCount : RefCount1;
                if (count <= 1)
                {
                    continue;
                }

                for (int i = 0; i < RefParts(code); i++)
                {
                    if (refs[i] < 0 || (type == 22 && code.SubTypes[i] == 0))
                    {
                        continue;
                    }

                    if (count == 2)
                    {
                        sink.U(1, refs[i] == 0 ? 1u : 0u);
                    }
                    else
                    {
                        BitSinkCodes.Ue(ref sink, (uint)refs[i]);
                    }
                }
            }

            Span<int> parts = stackalloc int[16];
            int n = Parts(code, parts, bSlice: true);
            for (int list = 0; list < 2; list++)
            {
                int[] mvds = list == 0 ? code.Mvd : code.Mvd1;
                for (int i = 0; i < n; i++)
                {
                    int bx = parts[i] & 15, by = (parts[i] >> 4) & 15;
                    if (UsesList(code, type, list, bx, by))
                    {
                        int mvd = mvds[(by * 4) + bx];
                        BitSinkCodes.Se(ref sink, H264SliceDecoder.MvX(mvd));
                        BitSinkCodes.Se(ref sink, H264SliceDecoder.MvY(mvd));
                    }
                }
            }
        }

        ResidualPartCavlc(code, ref sink);
    }

    // ---- Motion vector prediction ----

    /// <summary>Writes a partition's reference and vector to the motion arrays of the macroblock at the last <see cref="Locate"/>.</summary>
    public void SetBlocks(int bx, int by, int w, int h, int refIdx, int mv) => SetBlocks(0, bx, by, w, h, refIdx, mv);

    /// <summary>Writes a partition's reference and vector in a list (-1 and 0 where it does not use the list).</summary>
    public void SetBlocks(int list, int bx, int by, int w, int h, int refIdx, int mv)
    {
        H264Picture pic = Motion!;
        for (int y = by; y < by + h; y++)
        {
            int b = _b0 + (y * _w4) + bx;
            pic.RefIdx[list].AsSpan(b, w).Fill((sbyte)refIdx);
            pic.Mv[list].AsSpan(b, w).Fill(refIdx >= 0 ? mv : 0);
        }
    }

    /// <summary>
    /// The motion vector predictor (8.4.1.3) of the partition at 4x4 block (bx, by), w by h blocks, with <paramref name="refIdx"/>;
    /// <paramref name="mask"/> has bit bx + 4 by set for the macroblock's blocks whose motion comes before it.
    /// </summary>
    public int PredictMv(int bx, int by, int w, int h, int refIdx, int mask) => PredictMv(0, bx, by, w, h, refIdx, mask);

    /// <summary>The motion vector predictor of a partition in a list.</summary>
    public int PredictMv(int list, int bx, int by, int w, int h, int refIdx, int mask)
    {
        bool availableA = Neighbor(list, bx - 1, by, mask, out int refA, out int mvA);
        bool availableB = Neighbor(list, bx, by - 1, mask, out int refB, out int mvB);
        bool availableC = Neighbor(list, bx + w, by - 1, mask, out int refC, out int mvC);
        if (!availableC)
        {
            availableC = Neighbor(list, bx - 1, by - 1, mask, out refC, out mvC);
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

    /// <summary>The P_Skip motion vector (8.4.1.1) of the macroblock at the last <see cref="Locate"/>.</summary>
    public int SkipMv()
    {
        bool availableA = Neighbor(0, -1, 0, 0, out int refA, out int mvA);
        bool availableB = Neighbor(0, 0, -1, 0, out int refB, out int mvB);
        if (!availableA || !availableB || (refA == 0 && mvA == 0) || (refB == 0 && mvB == 0))
        {
            return 0;
        }

        return PredictMv(0, 0, 4, 4, 0, 0);
    }

    private bool Neighbor(int list, int bx, int by, int mask, out int refIdx, out int mv)
    {
        refIdx = -1;
        mv = 0;
        bool available = by < 0
            ? bx < 0 ? _availableTopLeft : bx > 3 ? _availableTopRight : _availableTop
            : bx < 0 ? _availableLeft : bx <= 3 && (mask & (1 << (bx + (by * 4)))) != 0;
        if (!available)
        {
            return false;
        }

        int b = _b0 + (by * _w4) + bx;
        refIdx = Motion!.RefIdx[list][b];
        if (refIdx >= 0)
        {
            mv = Motion.Mv[list][b];
        }

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

        return H264SliceDecoder.PackMv(Median3(H264SliceDecoder.MvX(mvA), H264SliceDecoder.MvX(mvB), H264SliceDecoder.MvX(mvC)),
            Median3(H264SliceDecoder.MvY(mvA), H264SliceDecoder.MvY(mvB), H264SliceDecoder.MvY(mvC)));
    }

    private static int Median3(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}
