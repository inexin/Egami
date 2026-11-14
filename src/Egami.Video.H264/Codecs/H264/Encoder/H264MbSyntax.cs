using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Writes macroblock_layer() (7.3.5) of intra and P macroblocks with CABAC (9.3) or CAVLC (9.2), the inverse of the decoder's reading:
/// context selection reads the same <see cref="H264MbState"/> fields, which <see cref="Begin"/> fills for the macroblock first,
/// so a pass with a counting sink gives a candidate's rate and a pass with the writers codes it.
/// </summary>
internal sealed partial class H264MbSyntax
{
    private static readonly byte[] CbfOffset = [0, 4, 8, 12, 16];
    private static readonly byte[] SigOffset = [0, 15, 29, 44, 47];
    private static readonly byte[] AbsOffset = [0, 10, 20, 30, 39];

    /// <summary>codeNum of each intra coded_block_pattern (the inverse of Table 9-4's intra column).</summary>
    private static readonly byte[] CbpIntraCode = Invert(H264Tables.CbpIntra);

    /// <summary>codeNum of each inter coded_block_pattern.</summary>
    private static readonly byte[] CbpInterCode = Invert(H264Tables.CbpInter);

    private readonly H264MbState _mb;
    private readonly int _w4;
    private int _addr;
    private int _mbX;
    private int _mbY;
    private int _b0;
    private int _left;
    private int _top;
    private bool _availableLeft;
    private bool _availableTop;
    private bool _availableTopRight;
    private bool _availableTopLeft;
    private int _slice;
    private bool _intra;

    public H264MbSyntax(H264MbState mb)
    {
        _mb = mb;
        _w4 = mb.W4;
    }

    /// <summary>The CABAC contexts of an H.264 slice (ctxIdx 0 to 459).</summary>
    private const int ContextCount = 460;

    /// <summary>
    /// The cost of a coeff_abs_level_minus1 prefix after its first bin by (state &lt;&lt; 4) | n, with n = Min(abs - 1, 14): n - 1
    /// bins of 1 then a 0 when n &lt; 14, all in one context; 0 for n = 0, the level 1 that has no more bins.
    /// </summary>
    private static readonly int[] PrefixCost = new int[128 * 16];

    /// <summary>The context's state after the prefix bins of <see cref="PrefixCost"/>.</summary>
    private static readonly byte[] PrefixNext = new byte[128 * 16];

    static H264MbSyntax()
    {
        for (int state = 0; state < 128; state++)
        {
            for (int n = 0; n <= 14; n++)
            {
                byte s = (byte)state;
                int cost = 0;
                for (int p = 1; p < n; p++)
                {
                    cost += CabacCost.Update(ref s, 1);
                }

                if (n is > 0 and < 14)
                {
                    cost += CabacCost.Update(ref s, 0);
                }

                PrefixCost[(state << 4) | n] = cost;
                PrefixNext[(state << 4) | n] = s;
            }
        }
    }

    /// <summary>Gets the CABAC contexts of the slice being coded (padded to a multiple of 8 for <see cref="CabacUndoLog"/>).</summary>
    public byte[] Contexts { get; } = new byte[(ContextCount + 7) & ~7];

    /// <summary>Gets or sets whether the slice is coded with CABAC.</summary>
    public bool Cabac { get; set; }

    /// <summary>Gets or sets whether the picture parameter set allows the 8x8 transform (transform_size_8x8_flag is coded).</summary>
    public bool Transform8x8Mode { get; set; }

    /// <summary>Gets or sets the slice type.</summary>
    public H264SliceType SliceType { get; set; }

    /// <summary>Gets or sets whether the last macroblock coded had a non-zero mb_qp_delta (its CABAC context).</summary>
    public bool PrevQpDeltaNonZero { get; set; }

    /// <summary>Gets or sets QPY of the last macroblock coded in the slice, from which mb_qp_delta counts.</summary>
    public int LastQp { get; set; }

    /// <summary>Gets or sets chroma_qp_index_offset.</summary>
    public int ChromaQpOffset { get; set; }

    /// <summary>Gets or sets the picture whose motion arrays <see cref="Begin"/> fills, which motion vector prediction reads.</summary>
    public H264Picture? Motion { get; set; }

    /// <summary>Gets or sets the <see cref="H264Picture.Id"/> of each reference in list 0, which the deblocking filter compares.</summary>
    public int[] RefIds { get; set; } = [];

    /// <summary>Gets or sets num_ref_idx_l0_active: with more than one, ref_idx_l0 is coded.</summary>
    public int RefCount { get; set; } = 1;

    /// <summary>Gets or sets the <see cref="H264Picture.Id"/> of each reference in list 1.</summary>
    public int[] RefIds1 { get; set; } = [];

    /// <summary>Gets or sets num_ref_idx_l1_active.</summary>
    public int RefCount1 { get; set; } = 1;

    /// <summary>Starts a slice at <paramref name="qp"/> (SliceQPY): CABAC contexts initialised for it (9.3.1.1).</summary>
    public void StartSlice(int slice, H264SliceType type, int qp, int cabacInitIdc)
    {
        _slice = slice;
        SliceType = type;
        LastQp = qp;
        PrevQpDeltaNonZero = false;
        int column = type == H264SliceType.I ? 0 : 1 + cabacInitIdc;
        ReadOnlySpan<sbyte> init = H264Tables.CabacInit;
        byte[] ctx = Contexts;
        for (int i = 0; i < ContextCount; i++)
        {
            int m = init[(i * 8) + (column * 2)], n = init[(i * 8) + (column * 2) + 1];
            int pre = Math.Clamp(((m * qp) >> 4) + n, 1, 126);
            ctx[i] = (byte)(pre <= 63 ? (63 - pre) << 1 : ((pre - 64) << 1) | 1);
        }
    }

    /// <summary>Gets the number of the slice being coded.</summary>
    public int Slice => _slice;

    /// <summary>
    /// Moves to the macroblock at (mbX, mbY): its neighbours' availability, which <see cref="PredictedIntraMode"/> reads with the
    /// modes of the macroblock's earlier blocks in <see cref="H264MbState.IntraModes"/>.
    /// </summary>
    public void Locate(int mbX, int mbY)
    {
        _mbX = mbX;
        _mbY = mbY;
        _addr = (mbY * _mb.WidthMbs) + mbX;
        _b0 = (mbY * 4 * _w4) + (mbX * 4);
        _left = _addr - 1;
        _top = _addr - _mb.WidthMbs;
        _availableLeft = mbX > 0 && _mb.Slice[_left] == _slice;
        _availableTop = mbY > 0 && _mb.Slice[_top] == _slice;
        _availableTopRight = mbY > 0 && mbX < _mb.WidthMbs - 1 && _mb.Slice[_top + 1] == _slice;
        _availableTopLeft = mbY > 0 && mbX > 0 && _mb.Slice[_top - 1] == _slice;
    }

    /// <summary>
    /// Fills the macroblock state for <paramref name="code"/> at (mbX, mbY) as the decoder will have it: kind, modes, coded block
    /// pattern, counts and QPs. Call before writing or counting, and again for each candidate.
    /// </summary>
    public void Begin(int mbX, int mbY, H264MbCode code)
    {
        Locate(mbX, mbY);
        _intra = H264MbState.IsIntra(code.Kind);
        int addr = _addr;
        _mb.Slice[addr] = _slice;
        _mb.Kind[addr] = code.Kind;
        _mb.Transform8x8[addr] = code.Eight;
        _mb.Cbp[addr] = (byte)code.Cbp;
        _mb.ChromaPredMode[addr] = (byte)(_intra ? code.ChromaMode : 0);
        _mb.Direct8x8[addr] = (byte)DirectMask(code);
        if (!HasQpDelta(code))
        {
            code.Qp = LastQp;
        }

        _mb.Qp[addr] = (sbyte)code.Qp;
        _mb.QpC[0][addr] = (sbyte)H264Tables.ChromaQp[Math.Clamp(code.Qp + ChromaQpOffset, 0, 51)];
        _mb.QpC[1][addr] = _mb.QpC[0][addr];
        _mb.QpDeltaNonZero[addr] = HasQpDelta(code) && code.Qp != LastQp;
        _mb.CbfDc[addr] = (byte)((code.Kind == H264MbKind.I16x16 && code.LumaDcCount > 0 ? 1 : 0)
            | ((code.Cbp & 0x30) != 0 && code.ChromaDcCount[0] > 0 ? 2 : 0)
            | ((code.Cbp & 0x30) != 0 && code.ChromaDcCount[1] > 0 ? 4 : 0));
        // Speed round 4: a row of four blocks at a time, filling what is the same for every block (no intra modes, no vector
        // differences, no luma levels) and working out the rest a block at a time; BlockXY is its own inverse.
        bool modes = code.Kind is H264MbKind.I4x4 or H264MbKind.I8x8, levels = (code.Cbp & 15) != 0;
        for (int by = 0; by < 4; by++)
        {
            int row = _b0 + (by * _w4);
            if (!modes)
            {
                _mb.IntraModes.AsSpan(row, 4).Fill(-1);
            }

            if (_intra)
            {
                _mb.Mvd[0].AsSpan(2 * row, 8).Clear();
                _mb.Mvd[1].AsSpan(2 * row, 8).Clear();
            }

            if (!levels)
            {
                _mb.NzLuma.AsSpan(row, 4).Clear();
                _mb.Nonzero.AsSpan(row, 4).Clear();
            }

            if (!modes && _intra && !levels)
            {
                continue;
            }

            for (int bx = 0; bx < 4; bx++)
            {
                int xy = (by * 4) + bx, blk = H264Tables.BlockXY[xy], i8 = blk >> 2, b = row + bx;
                if (modes)
                {
                    _mb.IntraModes[b] = (sbyte)(code.Kind == H264MbKind.I4x4 ? code.Modes[blk] : code.Modes[i8]);
                }

                if (!_intra)
                {
                    int mvd = code.Mvd[xy], mvd1 = code.Mvd1[xy];
                    _mb.Mvd[0][2 * b] = (byte)Math.Min(Math.Abs(H264SliceDecoder.MvX(mvd)), 255);
                    _mb.Mvd[0][(2 * b) + 1] = (byte)Math.Min(Math.Abs(H264SliceDecoder.MvY(mvd)), 255);
                    _mb.Mvd[1][2 * b] = (byte)Math.Min(Math.Abs(H264SliceDecoder.MvX(mvd1)), 255);
                    _mb.Mvd[1][(2 * b) + 1] = (byte)Math.Min(Math.Abs(H264SliceDecoder.MvY(mvd1)), 255);
                }

                if (levels)
                {
                    int count = (code.Cbp & (1 << i8)) == 0 ? 0 : code.Eight ? code.Luma8Count[i8] : code.LumaCount[blk];
                    _mb.NzLuma[b] = (byte)count;
                    _mb.Nonzero[b] = (byte)(count > 0 ? 1 : 0);
                }
            }
        }

        int cw = _mb.ChromaW4;
        int c0 = (mbY * 2 * cw) + (mbX * 2);
        for (int blk = 0; blk < 4; blk++)
        {
            int c = c0 + ((blk >> 1) * cw) + (blk & 1);
            bool ac = (code.Cbp >> 4) == 2;
            _mb.NzCb[c] = (byte)(ac ? code.ChromaAcCount[blk] : 0);
            _mb.NzCr[c] = (byte)(ac ? code.ChromaAcCount[4 + blk] : 0);
        }

        _mb.FlatInside[addr] = false;
        SetCavlc8x8Counts(code);
        if (Motion is { } pic)
        {
            SetMotion(pic, code);
        }
    }

    /// <summary>Writes the macroblock's references and vectors to the motion arrays, as the decoder will have them.</summary>
    private void SetMotion(H264Picture pic, H264MbCode code)
    {
        if (_intra || ((code.Kind == H264MbKind.PSkip || (code.Partition == 0 && !code.Direct))
            && (BlockRef(code, 0, 0, 0) < 0 || code.Mv.AsSpan(0, 16).IndexOfAnyExcept(code.Mv[0]) < 0)
            && (BlockRef(code, 1, 0, 0) < 0 || code.Mv1.AsSpan(0, 16).IndexOfAnyExcept(code.Mv1[0]) < 0)))
        {
            // Speed round 4: one reference and one vector a list for the whole macroblock (intra, P_Skip, 16x16), written a row
            // at a time; the same values the loop below writes.
            SetMotionWhole(pic, code);
            return;
        }

        bool flat = !_intra;
        for (int xy = 0; xy < 16; xy++)
        {
            int bx = xy & 3, by = xy >> 2;
            int b = _b0 + (by * _w4) + bx;
            int refIdx = _intra ? -1 : BlockRef(code, 0, bx, by), refIdx1 = _intra ? -1 : BlockRef(code, 1, bx, by);
            pic.RefIdx[0][b] = (sbyte)refIdx;
            pic.RefPicId[0][b] = refIdx >= 0 ? RefIds[refIdx] : -1;
            pic.Mv[0][b] = refIdx >= 0 ? code.Mv[xy] : 0;
            pic.RefIdx[1][b] = (sbyte)refIdx1;
            pic.RefPicId[1][b] = refIdx1 >= 0 ? RefIds1[refIdx1] : -1;
            pic.Mv[1][b] = refIdx1 >= 0 ? code.Mv1[xy] : 0;
            flat &= _mb.Nonzero[b] == 0 && pic.RefPicId[0][b] == pic.RefPicId[0][_b0] && pic.Mv[0][b] == pic.Mv[0][_b0]
                && pic.RefPicId[1][b] == pic.RefPicId[1][_b0] && pic.Mv[1][b] == pic.Mv[1][_b0];
        }

        pic.MbIntra[_addr] = _intra;
        _mb.FlatInside[_addr] = flat;
    }

    /// <summary>SetMotion for a macroblock whose sixteen blocks share each list's reference and vector.</summary>
    private void SetMotionWhole(H264Picture pic, H264MbCode code)
    {
        int refIdx = _intra ? -1 : BlockRef(code, 0, 0, 0), refIdx1 = _intra ? -1 : BlockRef(code, 1, 0, 0);
        int id = refIdx >= 0 ? RefIds[refIdx] : -1, id1 = refIdx1 >= 0 ? RefIds1[refIdx1] : -1;
        int mv = refIdx >= 0 ? code.Mv[0] : 0, mv1 = refIdx1 >= 0 ? code.Mv1[0] : 0;
        bool flat = !_intra;
        for (int by = 0; by < 4; by++)
        {
            int b = _b0 + (by * _w4);
            pic.RefIdx[0].AsSpan(b, 4).Fill((sbyte)refIdx);
            pic.RefPicId[0].AsSpan(b, 4).Fill(id);
            pic.Mv[0].AsSpan(b, 4).Fill(mv);
            pic.RefIdx[1].AsSpan(b, 4).Fill((sbyte)refIdx1);
            pic.RefPicId[1].AsSpan(b, 4).Fill(id1);
            pic.Mv[1].AsSpan(b, 4).Fill(mv1);
            flat &= _mb.Nonzero.AsSpan(b, 4).IndexOfAnyExcept((byte)0) < 0;
        }

        pic.MbIntra[_addr] = _intra;
        _mb.FlatInside[_addr] = flat;
    }

    /// <summary>The list 0 reference index of the 4x4 block at (bx, by) of an inter macroblock.</summary>
    internal static int BlockRef(H264MbCode code, int bx, int by) => BlockRef(code, 0, bx, by);

    /// <summary>The reference index in a list of the 4x4 block at (bx, by) of an inter macroblock, -1 where the list is unused.</summary>
    internal static int BlockRef(H264MbCode code, int list, int bx, int by)
    {
        if (code.Kind == H264MbKind.PSkip)
        {
            return list == 0 ? 0 : -1;
        }

        int[] refs = list == 0 ? code.RefIdx : code.RefIdx1;
        return code.Partition switch
        {
            0 => refs[0],
            1 => refs[by >> 1],
            2 => refs[bx >> 1],
            _ => refs[((by >> 1) * 2) + (bx >> 1)],
        };
    }

    /// <summary>The 8x8 blocks of a B macroblock whose motion is direct prediction, bit i8 each.</summary>
    private int DirectMask(H264MbCode code)
    {
        if (code.Direct)
        {
            return 15;
        }

        if (SliceType != H264SliceType.B || code.Kind != H264MbKind.Inter || code.Partition != 3)
        {
            return 0;
        }

        return (code.SubTypes[0] == 0 ? 1 : 0) | (code.SubTypes[1] == 0 ? 2 : 0) | (code.SubTypes[2] == 0 ? 4 : 0) | (code.SubTypes[3] == 0 ? 8 : 0);
    }

    private static bool HasQpDelta(H264MbCode code) => code.Cbp != 0 || code.Kind == H264MbKind.I16x16;

    /// <summary>Records the macroblock as coded: its mb_qp_delta becomes the next one's context and QP prediction.</summary>
    public void Commit(H264MbCode code)
    {
        if (HasQpDelta(code))
        {
            PrevQpDeltaNonZero = code.Qp != LastQp;
            LastQp = code.Qp;
        }
        else
        {
            PrevQpDeltaNonZero = false;
        }
    }

    /// <summary>The mb_type of an intra macroblock in an I slice (Table 7-11).</summary>
    private static int IntraType(H264MbCode code) => code.Kind switch
    {
        H264MbKind.I16x16 => 1 + code.I16Mode + (4 * (code.Cbp >> 4)) + ((code.Cbp & 15) != 0 ? 12 : 0),
        _ => 0,
    };

    // ---- CABAC ----

    /// <summary>Writes mb_skip_flag of the macroblock at the last <see cref="Locate"/> (9.3.3.1.1.1).</summary>
    public void SkipFlagCabac<T>(ref T sink, bool skip)
        where T : struct, ICabacSink
    {
        int inc = (_availableLeft && !H264MbState.IsSkip(_mb.Kind[_left]) ? 1 : 0) + (_availableTop && !H264MbState.IsSkip(_mb.Kind[_top]) ? 1 : 0);
        sink.Encode(ref Contexts[(SliceType == H264SliceType.B ? 24 : 11) + inc], skip ? 1 : 0);
    }

    /// <summary>Writes a macroblock_layer with CABAC (a skipped macroblock has only its mb_skip_flag).</summary>
    public void WriteCabac<T>(H264MbCode code, ref T sink)
        where T : struct, ICabacSink
    {
        if (code.Kind is H264MbKind.Inter or H264MbKind.BDirect)
        {
            WriteInterCabac(code, ref sink);
            return;
        }

        byte[] ctx = Contexts;
        int type = IntraType(code);
        switch (SliceType)
        {
            case H264SliceType.I:
                {
                    int inc = (_availableLeft && _mb.Kind[_left] is not (H264MbKind.I4x4 or H264MbKind.I8x8) ? 1 : 0)
                        + (_availableTop && _mb.Kind[_top] is not (H264MbKind.I4x4 or H264MbKind.I8x8) ? 1 : 0);
                    sink.Encode(ref ctx[3 + inc], type == 0 ? 0 : 1);
                    if (type != 0)
                    {
                        IntraSuffixCabac(ref sink, type, 3 + 3, 3 + 4, 3 + 5, 3 + 6, 3 + 7);
                    }

                    break;
                }

            case H264SliceType.P:
                sink.Encode(ref ctx[14], 1);
                IntraPrefixedCabac(ref sink, type, 17);
                break;
            default:
                {
                    int inc = (_availableLeft && _mb.Kind[_left] is not (H264MbKind.BSkip or H264MbKind.BDirect) ? 1 : 0)
                        + (_availableTop && _mb.Kind[_top] is not (H264MbKind.BSkip or H264MbKind.BDirect) ? 1 : 0);
                    sink.Encode(ref ctx[27 + inc], 1);
                    sink.Encode(ref ctx[27 + 3], 1);
                    sink.Encode(ref ctx[27 + 4], 1);
                    sink.Encode(ref ctx[27 + 5], 1);
                    sink.Encode(ref ctx[27 + 5], 0);
                    sink.Encode(ref ctx[27 + 5], 1);
                    IntraPrefixedCabac(ref sink, type, 32);
                    break;
                }
        }

        if (code.Kind != H264MbKind.I16x16)
        {
            if (Transform8x8Mode)
            {
                int inc = (_availableLeft && _mb.Transform8x8[_left] ? 1 : 0) + (_availableTop && _mb.Transform8x8[_top] ? 1 : 0);
                sink.Encode(ref ctx[399 + inc], code.Kind == H264MbKind.I8x8 ? 1 : 0);
            }

            WriteIntraModes(code, ref sink, default(NoBits));
        }

        int chromaInc = (ChromaModeCond(_availableLeft, _left) ? 1 : 0) + (ChromaModeCond(_availableTop, _top) ? 1 : 0);
        int mode = code.ChromaMode;
        sink.Encode(ref ctx[64 + chromaInc], mode == 0 ? 0 : 1);
        if (mode > 0)
        {
            sink.Encode(ref ctx[67], mode == 1 ? 0 : 1);
            if (mode > 1)
            {
                sink.Encode(ref ctx[67], mode == 2 ? 0 : 1);
            }
        }

        if (code.Kind != H264MbKind.I16x16)
        {
            CbpCabac(ref sink, code.Cbp);
        }

        if (HasQpDelta(code))
        {
            QpDeltaCabac(ref sink, code.Qp - LastQp);
            ResidualCabac(code, ref sink);
        }
    }

    private void IntraPrefixedCabac<T>(ref T sink, int type, int offset)
        where T : struct, ICabacSink
    {
        byte[] ctx = Contexts;
        sink.Encode(ref ctx[offset], type == 0 ? 0 : 1);
        if (type != 0)
        {
            IntraSuffixCabac(ref sink, type, offset + 1, offset + 2, offset + 2, offset + 3, offset + 3);
        }
    }

    private void IntraSuffixCabac<T>(ref T sink, int type, int lumaCtx, int chromaCtx, int chroma2Ctx, int modeCtx, int mode2Ctx)
        where T : struct, ICabacSink
    {
        byte[] ctx = Contexts;
        sink.Terminate(0);
        int t = type - 1;
        int luma = t / 12, chroma = (t / 4) % 3, mode = t % 4;
        sink.Encode(ref ctx[lumaCtx], luma);
        sink.Encode(ref ctx[chromaCtx], chroma == 0 ? 0 : 1);
        if (chroma != 0)
        {
            sink.Encode(ref ctx[chroma2Ctx], chroma == 1 ? 0 : 1);
        }

        sink.Encode(ref ctx[modeCtx], mode >> 1);
        sink.Encode(ref ctx[mode2Ctx], mode & 1);
    }

    private bool ChromaModeCond(bool available, int addr) =>
        available && H264MbState.IsIntra(_mb.Kind[addr]) && _mb.Kind[addr] != H264MbKind.IPcm && _mb.ChromaPredMode[addr] != 0;

    /// <summary>A sink that ignores CAVLC bits, for the CABAC path of the shared intra mode writer.</summary>
    private struct NoBits : IBitSink
    {
        public readonly void U(int n, uint value)
        {
        }
    }

    /// <summary>A sink that ignores CABAC bins, for the CAVLC path of the shared intra mode writer.</summary>
    private struct NoBins : ICabacSink
    {
        public readonly void Encode(ref byte state, int bin)
        {
        }

        public readonly void Bypass(int bin)
        {
        }

        public readonly void Terminate(int bin)
        {
        }
    }

    /// <summary>Writes prev_intra_pred_mode_flag and rem_intra_pred_mode of each 4x4 or 8x8 block, with CABAC or CAVLC.</summary>
    private void WriteIntraModes<TC, TB>(H264MbCode code, ref TC bins, TB bits)
        where TC : struct, ICabacSink
        where TB : struct, IBitSink
    {
        bool t8 = code.Kind == H264MbKind.I8x8;
        int step = t8 ? 4 : 1;
        byte[] ctx = Contexts;
        for (int blk = 0; blk < 16; blk += step)
        {
            int xy = H264Tables.BlockXY[blk];
            int predicted = PredictedIntraMode(xy & 3, xy >> 2);
            int mode = t8 ? code.Modes[blk >> 2] : code.Modes[blk];
            int rem = mode < predicted ? mode : mode - 1;
            if (Cabac)
            {
                bins.Encode(ref ctx[68], mode == predicted ? 1 : 0);
                if (mode != predicted)
                {
                    bins.Encode(ref ctx[69], rem & 1);
                    bins.Encode(ref ctx[69], (rem >> 1) & 1);
                    bins.Encode(ref ctx[69], rem >> 2);
                }
            }
            else
            {
                bits.U(1, mode == predicted ? 1u : 0u);
                if (mode != predicted)
                {
                    bits.U(3, (uint)rem);
                }
            }
        }
    }

    /// <summary>predIntra4x4PredMode / predIntra8x8PredMode of the block at (bx, by) (8.3.1.1).</summary>
    public int PredictedIntraMode(int bx, int by)
    {
        int a = NeighborIntraMode(bx - 1, by, out bool availableA);
        int b = NeighborIntraMode(bx, by - 1, out bool availableB);
        return !availableA || !availableB ? 2 : Math.Min(a, b);
    }

    private int NeighborIntraMode(int bx, int by, out bool available)
    {
        bool inside = bx >= 0 && by >= 0;
        int addr = bx < 0 ? _left : by < 0 ? _top : _addr;
        available = inside || (bx < 0 ? _availableLeft : _availableTop);
        if (!available)
        {
            return 2;
        }

        if (!inside)
        {
            H264MbKind kind = _mb.Kind[addr];
            if (!H264MbState.IsIntra(kind) || kind is not (H264MbKind.I4x4 or H264MbKind.I8x8))
            {
                return 2;
            }
        }

        return _mb.IntraModes[_b0 + (by * _w4) + bx];
    }

    private void CbpCabac<T>(ref T sink, int cbp)
        where T : struct, ICabacSink
    {
        byte[] ctx = Contexts;
        for (int b8 = 0; b8 < 4; b8++)
        {
            int bx = b8 & 1, by = b8 >> 1;
            int condA = bx > 0 ? (((cbp >> (b8 - 1)) & 1) == 0 ? 1 : 0)
                : _availableLeft ? (((_mb.Cbp[_left] >> (b8 + 1)) & 1) == 0 ? 1 : 0) : 0;
            int condB = by > 0 ? (((cbp >> (b8 - 2)) & 1) == 0 ? 1 : 0)
                : _availableTop ? (((_mb.Cbp[_top] >> (b8 + 2)) & 1) == 0 ? 1 : 0) : 0;
            sink.Encode(ref ctx[73 + condA + (2 * condB)], (cbp >> b8) & 1);
        }

        int chroma = cbp >> 4;
        int chromaA = _availableLeft ? _mb.Cbp[_left] >> 4 : 0, chromaB = _availableTop ? _mb.Cbp[_top] >> 4 : 0;
        sink.Encode(ref ctx[77 + (chromaA != 0 ? 1 : 0) + (chromaB != 0 ? 2 : 0)], chroma == 0 ? 0 : 1);
        if (chroma != 0)
        {
            sink.Encode(ref ctx[77 + 4 + (chromaA == 2 ? 1 : 0) + (chromaB == 2 ? 2 : 0)], chroma == 2 ? 1 : 0);
        }
    }

    private void QpDeltaCabac<T>(ref T sink, int delta)
        where T : struct, ICabacSink
    {
        H264EncodeStats.Part(H264BitPart.QpDelta);
        byte[] ctx = Contexts;
        sink.Encode(ref ctx[60 + (PrevQpDeltaNonZero ? 1 : 0)], delta == 0 ? 0 : 1);
        if (delta == 0)
        {
            return;
        }

        int k = delta > 0 ? (2 * delta) - 1 : -2 * delta;
        for (int j = 1; j < k; j++)
        {
            sink.Encode(ref ctx[j == 1 ? 62 : 63], 1);
        }

        sink.Encode(ref ctx[k == 1 ? 62 : 63], 0);
    }

    private void ResidualCabac<T>(H264MbCode code, ref T sink)
        where T : struct, ICabacSink
    {
        H264EncodeStats.Part(H264BitPart.Residual);
        if (typeof(T) == typeof(CabacUndoSink))
        {
            CountResidual(code, ref Unsafe.As<T, CabacUndoSink>(ref sink));
            return;
        }

        if (code.Kind == H264MbKind.I16x16)
        {
            BlockCabac(ref sink, 0, CbfInc(DcCond(_availableLeft, _left, 1), DcCond(_availableTop, _top, 1)), 16, code.LumaDc, 0, code.LumaDcCount);
        }

        for (int i8 = 0; i8 < 4; i8++)
        {
            if ((code.Cbp & (1 << i8)) == 0)
            {
                continue;
            }

            if (code.Eight)
            {
                BlockCabac(ref sink, 5, -1, 64, code.Luma8, i8 * 64, code.Luma8Count[i8]);
                continue;
            }

            for (int i4 = 0; i4 < 4; i4++)
            {
                int blk = (i8 * 4) + i4;
                int xy = H264Tables.BlockXY[blk];
                int bx = xy & 3, by = xy >> 2;
                bool ac = code.Kind == H264MbKind.I16x16;
                BlockCabac(ref sink, ac ? 1 : 2, CbfInc(LumaCbfCond(bx - 1, by), LumaCbfCond(bx, by - 1)), ac ? 15 : 16, code.Luma, blk * 16, code.LumaCount[blk]);
            }
        }

        if ((code.Cbp & 0x30) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                BlockCabac(ref sink, 3, CbfInc(DcCond(_availableLeft, _left, 2 << c), DcCond(_availableTop, _top, 2 << c)), 4, code.ChromaDc, c * 4, code.ChromaDcCount[c]);
            }
        }

        if ((code.Cbp & 0x20) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                byte[] nz = c == 0 ? _mb.NzCb : _mb.NzCr;
                for (int blk = 0; blk < 4; blk++)
                {
                    int bx = blk & 1, by = blk >> 1;
                    BlockCabac(ref sink, 4, CbfInc(ChromaCbfCond(nz, bx - 1, by), ChromaCbfCond(nz, bx, by - 1)), 15, code.ChromaAc, ((c * 4) + blk) * 16, code.ChromaAcCount[(c * 4) + blk]);
                }
            }
        }
    }

    /// <summary>
    /// Counts the macroblock's residual as <see cref="ResidualCabac{T}"/> codes it into a <see cref="CabacUndoSink"/> (H.264
    /// Encoder Speed Build Plan, task 19): the coded_block_flag conditions from masks of the coded blocks and their neighbours,
    /// each flag one table step, and each coded block from <see cref="CountLevels"/>.
    /// </summary>
    private void CountResidual(H264MbCode code, ref CabacUndoSink sink)
    {
        byte[] ctx = Contexts;
        CabacUndoLog log = sink.Log;
        log.NoteRange(85, 104);
        ref byte states = ref MemoryMarshal.GetArrayDataReference(ctx);
        ref int costs = ref MemoryMarshal.GetArrayDataReference(CabacCost.Costs);
        ref byte next = ref MemoryMarshal.GetArrayDataReference(CabacCost.Next);
        long cost = 0;
        int cbp = code.Cbp;
        bool i16 = code.Kind == H264MbKind.I16x16;
        if (i16)
        {
            int count = code.LumaDcCount;
            int inc = CbfInc(DcCond(_availableLeft, _left, 1), DcCond(_availableTop, _top, 1));
            cost += Bin(ref Unsafe.Add(ref states, 85 + inc), count > 0 ? 1 : 0, ref costs, ref next);
            if (count > 0)
            {
                cost += CountLevels(ctx, log, 0, 16, code.LumaDc, 0);
            }
        }

        if ((cbp & 15) != 0)
        {
            if (code.Eight)
            {
                for (int i8 = 0; i8 < 4; i8++)
                {
                    if ((cbp & (1 << i8)) != 0 && code.Luma8Count[i8] > 0)
                    {
                        cost += CountLevels(ctx, log, 5, 64, code.Luma8, i8 * 64);
                    }
                }
            }
            else
            {
                // Bit (by + 1) * 5 + bx + 1: whether block (bx, by) has levels, with the left column and top row the neighbours'.
                uint grid = LumaCbfGrid();
                int cat = i16 ? 1 : 2, max = i16 ? 15 : 16;
                ref byte cbf = ref Unsafe.Add(ref states, 85 + CbfOffset[cat]);
                int[] counts = code.LumaCount;
                ReadOnlySpan<byte> blockXY = H264Tables.BlockXY;
                for (int blk = 0; blk < 16; blk++)
                {
                    if ((cbp & (1 << (blk >> 2))) == 0)
                    {
                        blk |= 3;
                        continue;
                    }

                    int xy = blockXY[blk];
                    int bx = xy & 3, by = xy >> 2;
                    int inc = (int)((grid >> (((by + 1) * 5) + bx)) & 1) + (2 * (int)((grid >> ((by * 5) + bx + 1)) & 1));
                    int count = counts[blk];
                    cost += Bin(ref Unsafe.Add(ref cbf, inc), count > 0 ? 1 : 0, ref costs, ref next);
                    if (count > 0)
                    {
                        cost += CountLevels(ctx, log, cat, max, code.Luma, blk * 16);
                    }
                }
            }
        }

        if ((cbp & 0x30) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                int count = code.ChromaDcCount[c];
                int inc = CbfInc(DcCond(_availableLeft, _left, 2 << c), DcCond(_availableTop, _top, 2 << c));
                cost += Bin(ref Unsafe.Add(ref states, 85 + 12 + inc), count > 0 ? 1 : 0, ref costs, ref next);
                if (count > 0)
                {
                    cost += CountLevels(ctx, log, 3, 4, code.ChromaDc, c * 4);
                }
            }
        }

        if ((cbp & 0x20) != 0)
        {
            ref byte cbf = ref Unsafe.Add(ref states, 85 + 16);
            int[] counts = code.ChromaAcCount;
            for (int c = 0; c < 2; c++)
            {
                // Bit (by + 1) * 3 + bx + 1, as the luma grid.
                uint grid = ChromaCbfGrid(c == 0 ? _mb.NzCb : _mb.NzCr);
                for (int blk = 0; blk < 4; blk++)
                {
                    int bx = blk & 1, by = blk >> 1;
                    int inc = (int)((grid >> (((by + 1) * 3) + bx)) & 1) + (2 * (int)((grid >> ((by * 3) + bx + 1)) & 1));
                    int count = counts[(c * 4) + blk];
                    cost += Bin(ref Unsafe.Add(ref cbf, inc), count > 0 ? 1 : 0, ref costs, ref next);
                    if (count > 0)
                    {
                        cost += CountLevels(ctx, log, 4, 15, code.ChromaAc, ((c * 4) + blk) * 16);
                    }
                }
            }
        }

        sink.Cost += cost;
    }

    /// <summary>The coded_block_flag conditions of the luma blocks as <see cref="LumaCbfCond"/> gives them, on a 5x5 grid.</summary>
    private uint LumaCbfGrid()
    {
        byte[] nz = _mb.NzLuma;
        uint unavailable = _intra ? 1u : 0u;
        uint grid = 0;
        for (int by = 0; by < 4; by++)
        {
            int b = _b0 + (by * _w4);
            uint left = _availableLeft ? (nz[b - 1] != 0 ? 1u : 0u) : unavailable;
            uint row = (nz[b] != 0 ? 1u : 0u) | (nz[b + 1] != 0 ? 2u : 0u) | (nz[b + 2] != 0 ? 4u : 0u) | (nz[b + 3] != 0 ? 8u : 0u);
            grid |= (left | (row << 1)) << ((by + 1) * 5);
        }

        int t = _b0 - _w4;
        uint top = _availableTop
            ? (nz[t] != 0 ? 1u : 0u) | (nz[t + 1] != 0 ? 2u : 0u) | (nz[t + 2] != 0 ? 4u : 0u) | (nz[t + 3] != 0 ? 8u : 0u)
            : unavailable * 15;
        return grid | (top << 1);
    }

    /// <summary>The coded_block_flag conditions of a chroma component's AC blocks as <see cref="ChromaCbfCond"/> gives them, on a 3x3 grid.</summary>
    private uint ChromaCbfGrid(byte[] nz)
    {
        uint unavailable = _intra ? 1u : 0u;
        int cw = _mb.ChromaW4, c0 = ChromaBlock(0, 0);
        uint grid = (nz[c0] != 0 ? 1u << 4 : 0) | (nz[c0 + 1] != 0 ? 1u << 5 : 0) | (nz[c0 + cw] != 0 ? 1u << 7 : 0) | (nz[c0 + cw + 1] != 0 ? 1u << 8 : 0);
        uint left = _availableLeft ? (nz[c0 - 1] != 0 ? 1u : 0) | (nz[c0 + cw - 1] != 0 ? 2u : 0) : unavailable * 3;
        uint top = _availableTop ? (nz[c0 - cw] != 0 ? 1u : 0) | (nz[c0 - cw + 1] != 0 ? 2u : 0) : unavailable * 3;
        return grid | ((left & 1) << 3) | ((left >> 1) << 6) | (top << 1);
    }

    private static int CbfInc(int condA, int condB) => condA + (2 * condB);

    private int DcCond(bool available, int addr, int bit)
    {
        if (!available)
        {
            return _intra ? 1 : 0;
        }

        return _mb.Kind[addr] == H264MbKind.IPcm ? 1 : (_mb.CbfDc[addr] & bit) != 0 ? 1 : 0;
    }

    private int LumaCbfCond(int bx, int by)
    {
        bool outside = bx < 0 || by < 0;
        if (outside && !(bx < 0 ? _availableLeft : _availableTop))
        {
            return _intra ? 1 : 0;
        }

        return _mb.NzLuma[_b0 + (by * _w4) + bx] != 0 ? 1 : 0;
    }

    private int ChromaCbfCond(byte[] nz, int bx, int by)
    {
        bool outside = bx < 0 || by < 0;
        if (outside && !(bx < 0 ? _availableLeft : _availableTop))
        {
            return _intra ? 1 : 0;
        }

        return nz[ChromaBlock(bx, by)] != 0 ? 1 : 0;
    }

    private int ChromaBlock(int bx, int by) => (((_mbY * 2) + by) * _mb.ChromaW4) + (_mbX * 2) + bx;

    /// <summary>The first significance, last and level context of a residual block category (frame coding).</summary>
    internal static (int Sig, int Last, int Abs) ResidualContexts(int cat) =>
        cat == 5 ? (402, 417, 426) : (105 + SigOffset[cat], 166 + SigOffset[cat], 227 + AbsOffset[cat]);

    /// <summary>Writes residual_block_cabac (7.3.5.3.3) of a block's levels: coded_block_flag, significance map, then levels.</summary>
    public void BlockCabac<T>(ref T sink, int cat, int cbfInc, int maxNumCoeff, int[] levels, int at, int count)
        where T : struct, ICabacSink => BlockCabac(Contexts, ref sink, cat, cbfInc, maxNumCoeff, levels, at, count);

    /// <summary>Writes residual_block_cabac with the contexts in <paramref name="ctx"/>.</summary>
    public static void BlockCabac<T>(byte[] ctx, ref T sink, int cat, int cbfInc, int maxNumCoeff, int[] levels, int at, int count)
        where T : struct, ICabacSink
    {
        if (cbfInc >= 0)
        {
            sink.Encode(ref ctx[85 + CbfOffset[cat] + cbfInc], count > 0 ? 1 : 0);
        }

        if (count == 0)
        {
            return;
        }

        if (typeof(T) == typeof(CabacUndoSink))
        {
            ref CabacUndoSink counter = ref Unsafe.As<T, CabacUndoSink>(ref sink);
            counter.Cost += CountLevels(ctx, counter.Log, cat, maxNumCoeff, levels, at);
            return;
        }

        int last = maxNumCoeff - 1;
        while (levels[at + last] == 0)
        {
            last--;
        }

        int sigBase, lastBase, absBase;
        if (cat == 5)
        {
            sigBase = 402;
            lastBase = 417;
            absBase = 426;
        }
        else
        {
            sigBase = 105 + SigOffset[cat];
            lastBase = 166 + SigOffset[cat];
            absBase = 227 + AbsOffset[cat];
        }

        ReadOnlySpan<byte> sig8 = H264Tables.Significant8x8, last8 = H264Tables.Last8x8;
        for (int i = 0; i < maxNumCoeff - 1; i++)
        {
            int sigInc = cat == 5 ? sig8[i] : cat == 3 ? Math.Min(i, 2) : i;
            int lastInc = cat == 5 ? last8[i] : sigInc;
            int significant = levels[at + i] != 0 ? 1 : 0;
            sink.Encode(ref ctx[sigBase + sigInc], significant);
            if (significant != 0)
            {
                sink.Encode(ref ctx[lastBase + lastInc], i == last ? 1 : 0);
                if (i == last)
                {
                    break;
                }
            }
        }

        int eq1 = 0, gt1 = 0;
        int maxGt1Inc = cat == 3 ? 3 : 4;
        for (int i = last; i >= 0; i--)
        {
            int level = levels[at + i];
            if (level == 0)
            {
                continue;
            }

            int abs = Math.Abs(level);
            int inc = gt1 != 0 ? 0 : Math.Min(4, 1 + eq1);
            sink.Encode(ref ctx[absBase + inc], abs > 1 ? 1 : 0);
            if (abs > 1)
            {
                ref byte c = ref ctx[absBase + 5 + Math.Min(maxGt1Inc, gt1)];
                int prefix = Math.Min(abs - 1, 14);
                for (int p = 1; p < prefix; p++)
                {
                    sink.Encode(ref c, 1);
                }

                if (prefix < 14)
                {
                    sink.Encode(ref c, 0);
                }
                else
                {
                    // Exp-Golomb suffix, k = 0, in bypass bins.
                    int v = abs - 15, k = 0;
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

                gt1++;
            }
            else
            {
                eq1++;
            }

            sink.Bypass(level < 0 ? 1 : 0);
        }
    }

    /// <summary>
    /// Counts the significance map and levels of a block with non-zero levels exactly as <see cref="BlockCabac{T}(byte[], ref T,
    /// int, int, int, int[], int, int)"/> codes them into a <see cref="CabacUndoSink"/>, moving the same states (H.264 Encoder
    /// Speed Build Plan, task 19): one pass over a mask of the non-zero levels, without a branch on each level's value, and each
    /// level's prefix from <see cref="PrefixCost"/>; the block's context ranges are noted in <paramref name="log"/> once.
    /// </summary>
    private static long CountLevels(byte[] ctx, CabacUndoLog log, int cat, int maxNumCoeff, int[] levels, int at)
    {
        ulong mask = NonZeroMask(levels, at, maxNumCoeff);
        int last = 63 - BitOperations.LeadingZeroCount(mask);
        ref byte states = ref MemoryMarshal.GetArrayDataReference(ctx);
        ref int costs = ref MemoryMarshal.GetArrayDataReference(CabacCost.Costs);
        ref byte next = ref MemoryMarshal.GetArrayDataReference(CabacCost.Next);
        long cost = 0;
        int absBase;
        if (cat == 5)
        {
            // Significance 402-416, last 417-425 and levels 426-435 in one range.
            log.NoteRange(402, 435);
            absBase = 426;
            ref byte sig = ref Unsafe.Add(ref states, 402);
            ref byte lastCtx = ref Unsafe.Add(ref states, 417);
            ref byte sig8 = ref MemoryMarshal.GetReference(H264Tables.Significant8x8);
            ref byte last8 = ref MemoryMarshal.GetReference(H264Tables.Last8x8);
            for (int i = 0; i < last; i++)
            {
                int s = (int)(mask >> i) & 1;
                cost += Significant(ref Unsafe.Add(ref sig, Unsafe.Add(ref sig8, i)), ref Unsafe.Add(ref lastCtx, Unsafe.Add(ref last8, i)), s, ref costs, ref next);
            }

            if (last < 63)
            {
                cost += Bin(ref Unsafe.Add(ref sig, Unsafe.Add(ref sig8, last)), 1, ref costs, ref next)
                    + Bin(ref Unsafe.Add(ref lastCtx, Unsafe.Add(ref last8, last)), 1, ref costs, ref next);
            }
        }
        else
        {
            // Coded positions run to Min(last, maxNumCoeff - 2); chroma DC's increment Min(i, 2) is i at those.
            int sigBase = 105 + SigOffset[cat], lastBase = 166 + SigOffset[cat];
            int top = Math.Min(last, maxNumCoeff - 2);
            log.NoteRange(sigBase, sigBase + top);
            log.NoteRange(lastBase, lastBase + top);
            absBase = 227 + AbsOffset[cat];
            log.NoteRange(absBase, absBase + 9);
            ref byte sig = ref Unsafe.Add(ref states, sigBase);
            ref byte lastCtx = ref Unsafe.Add(ref states, lastBase);
            for (int i = 0; i < last; i++)
            {
                int s = (int)(mask >> i) & 1;
                cost += Significant(ref Unsafe.Add(ref sig, i), ref Unsafe.Add(ref lastCtx, i), s, ref costs, ref next);
            }

            if (last < maxNumCoeff - 1)
            {
                cost += Bin(ref Unsafe.Add(ref sig, last), 1, ref costs, ref next) + Bin(ref Unsafe.Add(ref lastCtx, last), 1, ref costs, ref next);
            }
        }

        // Levels from the last: coeff_abs_level_minus1's first bin, its prefix from the run tables, the escape and the sign.
        ref byte abs0 = ref Unsafe.Add(ref states, absBase);
        ref byte prefix0 = ref Unsafe.Add(ref abs0, 5);
        ref int prefixCost = ref MemoryMarshal.GetArrayDataReference(PrefixCost);
        ref byte prefixNext = ref MemoryMarshal.GetArrayDataReference(PrefixNext);
        ref int level0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(levels), at);
        int maxGt1Inc = cat == 3 ? 3 : 4;
        int eq1 = 0, gt1 = 0;
        long escapes = 0;
        ulong left = mask;
        do
        {
            int i = 63 - BitOperations.LeadingZeroCount(left);
            left ^= 1UL << i;
            int abs = Math.Abs(Unsafe.Add(ref level0, i));
            int g = abs > 1 ? 1 : 0;
            cost += Bin(ref Unsafe.Add(ref abs0, gt1 != 0 ? 0 : Math.Min(4, 1 + eq1)), g, ref costs, ref next);
            ref byte p = ref Unsafe.Add(ref prefix0, Math.Min(maxGt1Inc, gt1));
            int k = (p << 4) | Math.Min(abs - 1, 14);
            cost += Unsafe.Add(ref prefixCost, k);
            p = Unsafe.Add(ref prefixNext, k);
            if (abs >= 15)
            {
                // Exp-Golomb (k = 0) suffix of abs - 15: 2 Log2(abs - 14) + 1 bypass bins.
                escapes += (2 * BitOperations.Log2((uint)(abs - 14))) + 1;
            }

            gt1 += g;
            eq1 += g ^ 1;
        }
        while (left != 0);

        // A bypass sign each.
        return cost + ((escapes + BitOperations.PopCount(mask)) * CabacCost.Bit);
    }

    /// <summary>significant_coeff_flag s, then last_significant_coeff_flag 0 when s is 1, without a branch on s.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Significant(ref byte sig, ref byte last, int s, ref int costs, ref byte next)
    {
        int st = sig;
        int cost = Unsafe.Add(ref costs, st ^ s);
        sig = Unsafe.Add(ref next, (st << 1) | s);
        int lt = last, nl = Unsafe.Add(ref next, lt << 1);
        last = (byte)(lt ^ ((lt ^ nl) & -s));
        return cost + (Unsafe.Add(ref costs, lt) & -s);
    }

    /// <summary>Codes one bin in a context, returning its cost.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bin(ref byte state, int bin, ref int costs, ref byte next)
    {
        int st = state;
        state = Unsafe.Add(ref next, (st << 1) | bin);
        return Unsafe.Add(ref costs, st ^ bin);
    }

    /// <summary>Bit i set for each non-zero level i of the block's first <paramref name="count"/>.</summary>
    private static ulong NonZeroMask(int[] levels, int at, int count)
    {
        ulong mask = 0;
        int groups = (count + 3) >> 2;
        if ((uint)at <= (uint)levels.Length && levels.Length - at >= groups * 4)
        {
            ref int first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(levels), at);
            for (int g = 0; g < groups; g++)
            {
                Vector128<int> v = Vector128.LoadUnsafe(ref first, (nuint)(g * 4));
                mask |= (ulong)(~Vector128.Equals(v, Vector128<int>.Zero)).ExtractMostSignificantBits() << (g * 4);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                mask |= (levels[at + i] != 0 ? 1UL : 0) << i;
            }
        }

        return count == 64 ? mask : mask & ((1UL << count) - 1);
    }

    // ---- CAVLC ----

    /// <summary>Writes a macroblock_layer with CAVLC (skipped macroblocks are counted in mb_skip_run by the slice writer).</summary>
    public void WriteCavlc<T>(H264MbCode code, ref T sink)
        where T : struct, IBitSink
    {
        if (code.Kind is H264MbKind.Inter or H264MbKind.BDirect)
        {
            WriteInterCavlc(code, ref sink);
            return;
        }

        int typeOffset = SliceType switch { H264SliceType.I => 0, H264SliceType.P => 5, _ => 23 };
        BitSinkCodes.Ue(ref sink, (uint)(IntraType(code) + typeOffset));
        if (code.Kind != H264MbKind.I16x16)
        {
            if (Transform8x8Mode)
            {
                sink.U(1, code.Kind == H264MbKind.I8x8 ? 1u : 0u);
            }

            NoBins none = default;
            WriteIntraModes(code, ref none, sink);
        }

        BitSinkCodes.Ue(ref sink, (uint)code.ChromaMode);
        if (code.Kind != H264MbKind.I16x16)
        {
            BitSinkCodes.Ue(ref sink, (_intra ? CbpIntraCode : CbpInterCode)[code.Cbp]);
        }

        if (HasQpDelta(code))
        {
            BitSinkCodes.Se(ref sink, code.Qp - LastQp);
            ResidualCavlc(code, ref sink);
        }
    }

    private void ResidualCavlc<T>(H264MbCode code, ref T sink)
        where T : struct, IBitSink
    {
        Span<int> list = stackalloc int[16];
        if (code.Kind == H264MbKind.I16x16)
        {
            BlockCavlc(ref sink, LumaNc(0, 0), 16, code.LumaDc.AsSpan(0, 16));
        }

        for (int i8 = 0; i8 < 4; i8++)
        {
            if ((code.Cbp & (1 << i8)) == 0)
            {
                continue;
            }

            for (int i4 = 0; i4 < 4; i4++)
            {
                int blk = (i8 * 4) + i4;
                int xy = H264Tables.BlockXY[blk];
                int bx = xy & 3, by = xy >> 2;
                if (code.Eight)
                {
                    // The 8x8 block's levels as four interleaved lists (7.3.5.3.2).
                    for (int k = 0; k < 16; k++)
                    {
                        list[k] = code.Luma8[(i8 * 64) + (4 * k) + i4];
                    }

                    BlockCavlc(ref sink, LumaNc(bx, by), 16, list);
                }
                else
                {
                    bool ac = code.Kind == H264MbKind.I16x16;
                    BlockCavlc(ref sink, LumaNc(bx, by), ac ? 15 : 16, code.Luma.AsSpan(blk * 16, ac ? 15 : 16));
                }
            }
        }

        if ((code.Cbp & 0x30) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                BlockCavlc(ref sink, -1, 4, code.ChromaDc.AsSpan(c * 4, 4));
            }
        }

        if ((code.Cbp & 0x20) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                byte[] nz = c == 0 ? _mb.NzCb : _mb.NzCr;
                for (int blk = 0; blk < 4; blk++)
                {
                    BlockCavlc(ref sink, ChromaNc(nz, blk & 1, blk >> 1), 15, code.ChromaAc.AsSpan(((c * 4) + blk) * 16, 15));
                }
            }
        }
    }

    /// <summary>
    /// The CAVLC counts of an 8x8 block's four interleaved 4x4 lists, which CAVLC's nC reads (the decoder stores each list's
    /// TotalCoeff); CABAC keeps the 8x8 block's total in all four.
    /// </summary>
    private void SetCavlc8x8Counts(H264MbCode code)
    {
        if (Cabac || !code.Eight)
        {
            return;
        }

        for (int i8 = 0; i8 < 4; i8++)
        {
            for (int i4 = 0; i4 < 4; i4++)
            {
                int count = 0;
                if ((code.Cbp & (1 << i8)) != 0)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        count += code.Luma8[(i8 * 64) + (4 * k) + i4] != 0 ? 1 : 0;
                    }
                }

                int xy = H264Tables.BlockXY[(i8 * 4) + i4];
                _mb.NzLuma[_b0 + ((xy >> 2) * _w4) + (xy & 3)] = (byte)count;
            }
        }
    }

    private int LumaNc(int bx, int by)
    {
        bool availableA = bx > 0 || _availableLeft, availableB = by > 0 || _availableTop;
        int nA = availableA ? _mb.NzLuma[_b0 + (by * _w4) + bx - 1] : 0, nB = availableB ? _mb.NzLuma[_b0 + ((by - 1) * _w4) + bx] : 0;
        return availableA && availableB ? (nA + nB + 1) >> 1 : nA + nB;
    }

    private int ChromaNc(byte[] nz, int bx, int by)
    {
        bool availableA = bx > 0 || _availableLeft, availableB = by > 0 || _availableTop;
        int nA = availableA ? nz[ChromaBlock(bx - 1, by)] : 0, nB = availableB ? nz[ChromaBlock(bx, by - 1)] : 0;
        return availableA && availableB ? (nA + nB + 1) >> 1 : nA + nB;
    }

    /// <summary>Writes residual_block_cavlc (9.2) of levels in scan order.</summary>
    public static void BlockCavlc<T>(ref T sink, int nC, int maxNumCoeff, ReadOnlySpan<int> levels)
        where T : struct, IBitSink
    {
        Span<int> values = stackalloc int[16];
        Span<int> runs = stackalloc int[16];
        int total = 0, zeros = 0, run = 0;
        for (int i = 0; i < maxNumCoeff; i++)
        {
            if (levels[i] == 0)
            {
                run++;
                continue;
            }

            // Listed from the lowest frequency; reversed below.
            values[total] = levels[i];
            runs[total] = run;
            zeros += run;
            run = 0;
            total++;
        }

        // TotalZeros counts the zeros before the last non-zero level only.
        int trailingOnes = 0;
        for (int i = total - 1; i >= 0 && trailingOnes < 3 && Math.Abs(values[i]) == 1; i--)
        {
            trailingOnes++;
        }

        ReadOnlySpan<byte> tokens = H264Tables.CoeffToken;
        if (nC >= 8)
        {
            sink.U(6, total == 0 ? 3u : (uint)(((total - 1) << 2) | trailingOnes));
        }
        else
        {
            int table = nC switch { -1 => 4, -2 => 5, < 2 => 0, < 4 => 1, _ => 2 };
            int t = (((table * 17) + total) * 4 + trailingOnes) * 2;
            sink.U(tokens[t + 1], tokens[t]);
        }

        if (total == 0)
        {
            return;
        }

        int suffixLength = total > 10 && trailingOnes < 3 ? 1 : 0;
        for (int k = 0; k < total; k++)
        {
            int level = values[total - 1 - k];
            if (k < trailingOnes)
            {
                sink.U(1, level < 0 ? 1u : 0u);
                continue;
            }

            int levelCode = level > 0 ? (2 * level) - 2 : (-2 * level) - 1;
            if (k == trailingOnes && trailingOnes < 3)
            {
                levelCode -= 2;
            }

            WriteLevel(ref sink, levelCode, suffixLength);
            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(level) > (3 << (suffixLength - 1)) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        if (total < maxNumCoeff)
        {
            ReadOnlySpan<byte> table = maxNumCoeff == 4 && nC == -1 ? H264Tables.TotalZerosChromaDc420 : H264Tables.TotalZeros;
            int width = maxNumCoeff == 4 && nC == -1 ? 4 : 16;
            int z = ((total * width) + zeros) * 2;
            sink.U(table[z + 1], table[z]);
        }

        int zerosLeft = zeros;
        ReadOnlySpan<byte> runBefore = H264Tables.RunBefore;
        for (int k = 0; k < total - 1 && zerosLeft > 0; k++)
        {
            int r = runs[total - 1 - k];
            int at = ((Math.Min(zerosLeft, 7) * 15) + r) * 2;
            sink.U(runBefore[at + 1], runBefore[at]);
            zerosLeft -= r;
        }
    }

    /// <summary>Writes level_prefix and level_suffix of a levelCode (9.2.2.1, inverted).</summary>
    private static void WriteLevel<T>(ref T sink, int levelCode, int suffixLength)
        where T : struct, IBitSink
    {
        int prefix, suffix, size;
        if (suffixLength == 0 && levelCode < 14)
        {
            (prefix, suffix, size) = (levelCode, 0, 0);
        }
        else if (suffixLength == 0 && levelCode < 30)
        {
            (prefix, suffix, size) = (14, levelCode - 14, 4);
        }
        else if (suffixLength > 0 && levelCode < 15 << suffixLength)
        {
            (prefix, suffix, size) = (levelCode >> suffixLength, levelCode & ((1 << suffixLength) - 1), suffixLength);
        }
        else
        {
            // level_prefix 15 and up: the suffix counts from 15 << suffixLength (plus 15 when suffixLength is 0).
            int rest = levelCode - (15 << suffixLength) - (suffixLength == 0 ? 15 : 0);
            prefix = 15;
            while (rest >= 1 << (prefix - 3))
            {
                rest -= 1 << (prefix - 3);
                prefix++;
            }

            (suffix, size) = (rest, prefix - 3);
        }

        sink.U(prefix, 0);
        sink.U(1, 1);
        if (size > 0)
        {
            sink.U(size, (uint)suffix);
        }
    }

    private static byte[] Invert(ReadOnlySpan<byte> table)
    {
        byte[] inverse = new byte[48];
        for (int code = 0; code < table.Length; code++)
        {
            inverse[table[code]] = (byte)code;
        }

        return inverse;
    }
}
