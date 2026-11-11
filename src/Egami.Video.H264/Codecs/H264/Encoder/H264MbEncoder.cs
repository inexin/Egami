using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Chooses and codes intra macroblocks: chroma's mode by SATD, then the luma candidates (Intra16x16 in its best mode, Intra4x4
/// and, with the 8x8 transform, Intra8x8, their blocks' modes by SATD) quantized, reconstructed and weighed by squared error plus
/// λ times their CABAC or CAVLC bits. Reconstruction runs the decoder's own scaling and inverse transforms on the levels coded, so
/// the picture built here is the one a decoder builds.
/// </summary>
internal sealed partial class H264MbEncoder
{
    /// <summary>The rounding offset of intra levels, in 1/256 of a step.</summary>
    internal const int IntraRound = 85;

    private readonly H264MbState _mb;
    private readonly H264MbSyntax _syntax;
    private byte[] _y;
    private byte[] _cb;
    private byte[] _cr;
    private readonly byte[] _srcY;
    private readonly byte[] _srcCb;
    private readonly byte[] _srcCr;
    private readonly int _stride;
    private readonly int _cstride;
    private readonly int _origin;
    private readonly int _corigin;
    private readonly int[] _scale4;
    private readonly int[] _scale8;
    private readonly bool _allow8x8;
    private readonly int _effort;
    private readonly H264MbCode _chroma = new();
    private readonly H264MbCode[] _candidates = [new(), new(), new()];
    private readonly byte[][] _recon = [new byte[256], new byte[256], new byte[256]];
    private readonly byte[] _pred = new byte[256];
    private readonly H264BlockPricer _pricer = new();
    private readonly CabacUndoLog _undo = new(460);
    private readonly int[] _rdoqRows = new int[64];
    private readonly int[] _t = new int[33];
    private readonly int[] _l = new int[17];
    private readonly int[] _w = new int[256];
    private readonly int[] _d = new int[64];
    private readonly int[] _dc = new int[16];
    private int _mbX;
    private int _mbY;
    private int _qp;
    private int _qpc;
    private double _lambda;
    private double _sadLambda;
    private bool _left;
    private bool _top;
    private bool _topLeft;
    private bool _topRight;
    private int _lumaAt;
    private int _chromaAt;

    // The rounding offset of levels without RDOQ, in 1/256 of a step: intra's or inter's.
    private int _round = IntraRound;

    // The squared error of the intra chroma last coded.
    private long _chromaSse;

    public H264MbEncoder(H264Picture pic, H264MbState mb, H264MbSyntax syntax, byte[] srcY, byte[] srcCb, byte[] srcCr, bool allow8x8, int effort)
    {
        _mb = mb;
        _syntax = syntax;
        _y = pic.Y<byte>();
        _cb = pic.Cb<byte>();
        _cr = pic.Cr<byte>();
        _srcY = srcY;
        _srcCb = srcCb;
        _srcCr = srcCr;
        _stride = pic.Stride;
        _cstride = pic.ChromaStride;
        _origin = pic.Origin;
        _corigin = pic.ChromaOrigin;
        H264Dequant dequant = new(H264ScalingLists.Flat);
        _scale4 = dequant.Scale4x4[0];
        _scale8 = dequant.Scale8x8[0];
        _allow8x8 = allow8x8;
        _effort = effort;
    }

    /// <summary>Gets the macroblock chosen by the last <see cref="Encode"/> or <see cref="EncodeP"/>, its reconstruction in the picture.</summary>
    public H264MbCode Best { get; private set; } = new();

    /// <summary>Points the encoder at the picture to reconstruct into, laid out as the one it was made with.</summary>
    public void Target(H264Picture pic)
    {
        _y = pic.Y<byte>();
        _cb = pic.Cb<byte>();
        _cr = pic.Cr<byte>();
    }

    /// <summary>
    /// Chooses, codes and reconstructs the intra macroblock at (mbX, mbY) at <paramref name="qp"/>; returns its cost, squared
    /// error plus λ times its bits.
    /// </summary>
    public double Encode(int mbX, int mbY, int qp)
    {
        H264EncodeStats.Add(H264EncodeStat.IntraPasses);
        Setup(mbX, mbY, qp);
        _round = IntraRound;
        CodeChroma();

        int count = _allow8x8 && _effort >= 1 ? 3 : 2;
        int best = 0;
        double bestCost = double.MaxValue;
        for (int c = 0; c < count; c++)
        {
            H264MbCode code = _candidates[c];
            code.CopyFrom(_chroma);
            long sse = c switch
            {
                0 => CodeI16(code),
                1 => CodeI4(code),
                _ => CodeI8(code),
            };
            Save(_recon[c]);
            double cost = sse + (_lambda * Rate(code) / CabacCost.Bit);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = c;
            }
        }

        Restore(_recon[best]);
        Best = _candidates[best];
        return bestCost + _chromaSse;
    }

    /// <summary>
    /// Whether an inter macroblock whose best partitioning costs <paramref name="interSatd"/> (SATD and vector bits) is worth
    /// coding as intra too: always from effort 7, else when intra 16x16 prediction comes near it by SATD.
    /// </summary>
    private bool TryIntra(long interSatd)
    {
        if (_effort >= 7)
        {
            return true;
        }

        if (_effort == 0)
        {
            return interSatd > 16 * 16 * 4;
        }

        int[] t = _t, l = _l;
        H264IntraPredict.Gather(_y, _lumaAt, _stride, 16, _top, false, _left, _topLeft, t, l);
        long limit = interSatd * IntraMargin / 16;
        for (int mode = 0; mode < 4; mode++)
        {
            if (H264IntraPredict.Allowed16x16(mode, _top, _left, _topLeft))
            {
                H264IntraPredict.Predict16x16(_pred, 16, mode, t, l, _top, _left);
                if (Satd(_srcY, _lumaAt, _stride, _pred, 0, 16, 16, 16) < limit)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>How far above the inter cost, in sixteenths, an intra 16x16 SATD still tries intra.</summary>
    internal static int IntraMargin { get; set; } = 24;

    private void Setup(int mbX, int mbY, int qp)
    {
        _mbX = mbX;
        _mbY = mbY;
        _qp = qp;
        _qpc = H264Tables.ChromaQp[Math.Clamp(qp + _syntax.ChromaQpOffset, 0, 51)];
        _lambda = H264Forward.Lambda(qp);
        _sadLambda = Math.Sqrt(_lambda);
        int slice = _syntax.Slice, w = _mb.WidthMbs, addr = (mbY * w) + mbX;
        _left = mbX > 0 && _mb.Slice[addr - 1] == slice;
        _top = mbY > 0 && _mb.Slice[addr - w] == slice;
        _topLeft = mbX > 0 && mbY > 0 && _mb.Slice[addr - w - 1] == slice;
        _topRight = mbX < w - 1 && mbY > 0 && _mb.Slice[addr - w + 1] == slice;
        _lumaAt = _origin + (mbY * 16 * _stride) + (mbX * 16);
        _chromaAt = _corigin + (mbY * 8 * _cstride) + (mbX * 8);
        _syntax.Locate(mbX, mbY);
    }

    /// <summary>The bits of a macroblock (in 1/32768 bit) as the syntax writer would code it, the CABAC states left as they were.</summary>
    private long Rate(H264MbCode code)
    {
        H264EncodeStats.Add(H264EncodeStat.RdCodings);
        bool skippable = _syntax.SliceType != H264SliceType.I;
        _syntax.Locate(_mbX, _mbY);
        if (_syntax.Cabac)
        {
            // Counted exactly; only the states the count moved are put back, not all 460 copied in and out.
            _undo.Begin(_syntax.Contexts);
            CabacUndoSink sink = new(_undo);
            if (skippable)
            {
                // mb_skip_flag's context reads the neighbours only, so it is counted before Begin marks this macroblock.
                _syntax.SkipFlagCabac(ref sink, H264MbState.IsSkip(code.Kind));
            }

            _syntax.Begin(_mbX, _mbY, code);
            if (!H264MbState.IsSkip(code.Kind))
            {
                _syntax.WriteCabac(code, ref sink);
            }

            _undo.Restore();
            return sink.Cost;
        }

        _syntax.Begin(_mbX, _mbY, code);
        if (H264MbState.IsSkip(code.Kind))
        {
            return 0;
        }

        BitCountSink bits = default;
        _syntax.WriteCavlc(code, ref bits);

        // The mb_skip_run before a coded macroblock of a P slice: one bit when it is 0.
        return (bits.Bits + (skippable ? 1 : 0)) * CabacCost.Bit;
    }

    private void Save(byte[] recon)
    {
        for (int y = 0; y < 16; y++)
        {
            _y.AsSpan(_lumaAt + (y * _stride), 16).CopyTo(recon.AsSpan(y * 16, 16));
        }
    }

    private void Restore(byte[] recon)
    {
        for (int y = 0; y < 16; y++)
        {
            recon.AsSpan(y * 16, 16).CopyTo(_y.AsSpan(_lumaAt + (y * _stride), 16));
        }
    }

    // ---- Luma ----

    private long CodeI16(H264MbCode code)
    {
        code.Kind = H264MbKind.I16x16;
        code.Qp = _qp;
        int[] t = _t, l = _l;
        H264IntraPredict.Gather(_y, _lumaAt, _stride, 16, _top, false, _left, _topLeft, t, l);
        int bestMode = 2;
        long bestCost = long.MaxValue;
        for (int mode = 0; mode < 4; mode++)
        {
            if (!H264IntraPredict.Allowed16x16(mode, _top, _left, _topLeft))
            {
                continue;
            }

            H264IntraPredict.Predict16x16(_pred, 16, mode, t, l, _top, _left);
            long cost = Satd(_srcY, _lumaAt, _stride, _pred, 0, 16, 16, 16) + (long)(_sadLambda * (mode == 2 ? 1 : 3));
            if (cost < bestCost)
            {
                bestCost = cost;
                bestMode = mode;
            }
        }

        code.I16Mode = bestMode;
        H264IntraPredict.Predict16x16(_pred, 16, bestMode, t, l, _top, _left);

        // Forward transforms of the 16 blocks; their DCs go through the Hadamard.
        int m = _qp % 6, q6 = _qp / 6;
        Span<int> block = stackalloc int[16];
        for (int blk = 0; blk < 16; blk++)
        {
            int xy = H264Tables.BlockXY[blk];
            int bx = xy & 3, by = xy >> 2;
            Residual(_srcY, _lumaAt + (by * 4 * _stride) + (bx * 4), _stride, _pred, (by * 64) + (bx * 4), 16, 4, block);
            H264Forward.Transform4x4(block);
            block.CopyTo(_w.AsSpan(blk * 16, 16));
            _dc[(by * 4) + bx] = block[0];
        }

        H264Forward.Hadamard4x4(_dc);
        int dcCount = 0;
        Span<int> dcLevels = stackalloc int[16];
        for (int k = 0; k < 16; k++)
        {
            int pos = H264ScalingLists.Zigzag4x4[k];
            int level = H264Forward.Quantize(_dc[pos], H264Forward.Mf4[m * 16], H264Forward.Shift4 + 2 + q6, IntraRound);
            code.LumaDc[k] = level;
            dcLevels[pos] = level;
            dcCount += level != 0 ? 1 : 0;
        }

        code.LumaDcCount = dcCount;
        bool anyAc = false;
        for (int blk = 0; blk < 16; blk++)
        {
            int n = QuantizeBlock(_w.AsSpan(blk * 16, 16), code.Luma, blk * 16, 1, 15, false, _qp, 1);
            code.Luma[(blk * 16) + 15] = 0;
            code.LumaCount[blk] = n;
            anyAc |= n > 0;
        }

        code.Cbp = (code.Cbp & 0x30) | (anyAc ? 15 : 0);

        // Reconstruction: the decoder's DC scaling (8.5.10) and each block's AC.
        Span<int> dcY = stackalloc int[16];
        if (dcCount > 0)
        {
            InverseLumaDc(dcLevels, dcY);
        }
        else
        {
            dcY.Clear();
        }

        for (int y = 0; y < 16; y++)
        {
            _pred.AsSpan(y * 16, 16).CopyTo(_y.AsSpan(_lumaAt + (y * _stride), 16));
        }

        Span<int> d = _d.AsSpan(0, 16);
        for (int blk = 0; blk < 16; blk++)
        {
            int xy = H264Tables.BlockXY[blk];
            int bx = xy & 3, by = xy >> 2;
            d.Clear();
            d[0] = dcY[(by * 4) + bx];
            if (anyAc)
            {
                for (int k = 1; k < 16; k++)
                {
                    int level = code.Luma[(blk * 16) + k - 1];
                    if (level != 0)
                    {
                        int pos = H264ScalingLists.Zigzag4x4[k];
                        d[pos] = Scale4x4(level, _qp, _scale4, pos);
                    }
                }
            }

            H264Transform.Add4x4(d, _y, _lumaAt + (by * 4 * _stride) + (bx * 4), _stride, 255);
        }

        return Sse(_srcY, _y, _lumaAt, _stride, 16, 16);
    }

    /// <summary>The Intra16x16 DC levels (raster, row by) scaled into each block's coefficient 0 (raster by block position).</summary>
    private void InverseLumaDc(ReadOnlySpan<int> c, Span<int> dcY)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = c[(i * 4) + 0], b = c[(i * 4) + 1], d = c[(i * 4) + 2], e = c[(i * 4) + 3];
            t[(i * 4) + 0] = a + b + d + e;
            t[(i * 4) + 1] = a + b - d - e;
            t[(i * 4) + 2] = a - b - d + e;
            t[(i * 4) + 3] = a - b + d - e;
        }

        int ls = _scale4[(_qp % 6) * 16];
        int shift = _qp / 6;
        for (int j = 0; j < 4; j++)
        {
            int a = t[j], b = t[4 + j], d = t[8 + j], e = t[12 + j];
            Span<int> f = [a + b + d + e, a + b - d - e, a - b - d + e, a - b + d - e];
            for (int i = 0; i < 4; i++)
            {
                dcY[(i * 4) + j] = shift >= 6 ? (f[i] * ls) << (shift - 6) : ((f[i] * ls) + (1 << (5 - shift))) >> (6 - shift);
            }
        }
    }

    private long CodeI4(H264MbCode code)
    {
        code.Kind = H264MbKind.I4x4;
        code.Qp = _qp;
        int m = _qp % 6, q6 = _qp / 6;
        int[] t = _t, l = _l;
        Span<int> block = stackalloc int[16];
        int cbp = 0;
        for (int blk = 0; blk < 16; blk++)
        {
            int xy = H264Tables.BlockXY[blk];
            int bx = xy & 3, by = xy >> 2;
            int at = _lumaAt + (by * 4 * _stride) + (bx * 4);
            (bool top, bool left, bool corner, bool topRight) = Neighbors(bx * 4, by * 4, 4, blk);
            H264IntraPredict.Gather(_y, at, _stride, 4, top, topRight, left, corner, t, l);
            int predicted = _syntax.PredictedIntraMode(bx, by);
            int bestMode = 2;
            long bestCost = long.MaxValue;
            for (int mode = 0; mode < 9; mode++)
            {
                if (!H264IntraPredict.NxNAllowed(mode, top, left, corner))
                {
                    continue;
                }

                H264IntraPredict.PredictNxN(_pred, 4, 4, mode, t, l, top, left);
                long cost = Satd(_srcY, at, _stride, _pred, 0, 4, 4, 4) + (long)(_sadLambda * (mode == predicted ? 1 : 4));
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                }
            }

            code.Modes[blk] = bestMode;
            _mb.IntraModes[(((_mbY * 4) + by) * _mb.W4) + (_mbX * 4) + bx] = (sbyte)bestMode;
            H264IntraPredict.PredictNxN(_pred, 4, 4, bestMode, t, l, top, left);
            Residual(_srcY, at, _stride, _pred, 0, 4, 4, block);
            H264Forward.Transform4x4(block);
            int n = QuantizeBlock(block, code.Luma, blk * 16, 0, 16, false, _qp, 2);
            Span<int> d = _d.AsSpan(0, 16);
            Dequantize(code.Luma, blk * 16, 0, 16, false, _qp, d);

            code.LumaCount[blk] = n;
            cbp |= n > 0 ? 1 << (blk >> 2) : 0;
            for (int y = 0; y < 4; y++)
            {
                _pred.AsSpan(y * 4, 4).CopyTo(_y.AsSpan(at + (y * _stride), 4));
            }

            H264Transform.Add4x4(d, _y, at, _stride, 255);
        }

        code.Cbp = (code.Cbp & 0x30) | cbp;
        return Sse(_srcY, _y, _lumaAt, _stride, 16, 16);
    }

    private long CodeI8(H264MbCode code)
    {
        code.Kind = H264MbKind.I8x8;
        code.Qp = _qp;
        int m = _qp % 6, q6 = _qp / 6;
        int[] t = _t, l = _l;
        Span<int> block = stackalloc int[64];
        int cbp = 0;
        for (int i8 = 0; i8 < 4; i8++)
        {
            int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2;
            int at = _lumaAt + (by * 4 * _stride) + (bx * 4);
            (bool top, bool left, bool corner, bool topRight) = Neighbors(bx * 4, by * 4, 8, i8 * 4);
            H264IntraPredict.Gather(_y, at, _stride, 8, top, topRight, left, corner, t, l);
            H264IntraPredict.Filter8x8(t, l, top, left, corner);
            int predicted = _syntax.PredictedIntraMode(bx, by);
            int bestMode = 2;
            long bestCost = long.MaxValue;
            for (int mode = 0; mode < 9; mode++)
            {
                if (!H264IntraPredict.NxNAllowed(mode, top, left, corner))
                {
                    continue;
                }

                H264IntraPredict.PredictNxN(_pred, 8, 8, mode, t, l, top, left);
                long cost = Satd(_srcY, at, _stride, _pred, 0, 8, 8, 8) + (long)(_sadLambda * (mode == predicted ? 1 : 4));
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                }
            }

            code.Modes[i8] = bestMode;
            int b = (((_mbY * 4) + by) * _mb.W4) + (_mbX * 4) + bx;
            _mb.IntraModes[b] = _mb.IntraModes[b + 1] = _mb.IntraModes[b + _mb.W4] = _mb.IntraModes[b + _mb.W4 + 1] = (sbyte)bestMode;
            H264IntraPredict.PredictNxN(_pred, 8, 8, bestMode, t, l, top, left);
            Residual(_srcY, at, _stride, _pred, 0, 8, 8, block);
            H264Forward.Transform8x8(block);
            int n = QuantizeBlock(block, code.Luma8, i8 * 64, 0, 64, true, _qp, 5);
            Span<int> d = _d.AsSpan(0, 64);
            Dequantize(code.Luma8, i8 * 64, 0, 64, true, _qp, d);

            code.Luma8Count[i8] = n;
            cbp |= n > 0 ? 1 << i8 : 0;
            for (int y = 0; y < 8; y++)
            {
                _pred.AsSpan(y * 8, 8).CopyTo(_y.AsSpan(at + (y * _stride), 8));
            }

            H264Transform.Add8x8(d, _y, at, _stride, 255);
        }

        code.Cbp = (code.Cbp & 0x30) | cbp;
        return Sse(_srcY, _y, _lumaAt, _stride, 16, 16);
    }

    /// <summary>
    /// Which neighbours a luma block at (px, py) of size n has (8.3.1.2): inside the macroblock, the blocks coded before it;
    /// outside, the neighbouring macroblocks of the slice. <paramref name="blk"/> is the block's first luma4x4BlkIdx.
    /// </summary>
    private (bool Top, bool Left, bool Corner, bool TopRight) Neighbors(int px, int py, int n, int blk)
    {
        bool top = py > 0 || _top;
        bool left = px > 0 || _left;
        bool corner = px > 0 && py > 0 ? true : py > 0 ? _left : px > 0 ? _top : _topLeft;
        int bx = px >> 2, by = py >> 2, step = n >> 2;
        bool topRight = by == 0
            ? (bx + step < 4 ? _top : _topRight)
            : bx + step < 4 && BlockIndex(bx + step, by - 1) < blk;
        return (top, left, corner, topRight);
    }

    private static int BlockIndex(int bx, int by) => ((by >> 1) * 8) + ((bx >> 1) * 4) + ((by & 1) * 2) + (bx & 1);

    // ---- Chroma ----

    private void CodeChroma()
    {
        H264MbCode code = _chroma;
        int[] t = _t, l = _l;
        int bestMode = 0;
        long bestCost = long.MaxValue;
        Span<int> tCr = stackalloc int[33];
        Span<int> lCr = stackalloc int[17];
        H264IntraPredict.Gather(_cb, _chromaAt, _cstride, 8, _top, false, _left, _topLeft, t, l);
        H264IntraPredict.Gather(_cr, _chromaAt, _cstride, 8, _top, false, _left, _topLeft, tCr, lCr);
        for (int mode = 0; mode < 4; mode++)
        {
            if (!H264IntraPredict.AllowedChroma(mode, _top, _left, _topLeft))
            {
                continue;
            }

            H264IntraPredict.PredictChroma(_pred, 8, mode, t, l, _top, _left);
            long cost = Satd(_srcCb, _chromaAt, _cstride, _pred, 0, 8, 8, 8);
            H264IntraPredict.PredictChroma(_pred, 8, mode, tCr, lCr, _top, _left);
            cost += Satd(_srcCr, _chromaAt, _cstride, _pred, 0, 8, 8, 8) + (long)(_sadLambda * (mode == 0 ? 1 : 3));
            if (cost < bestCost)
            {
                bestCost = cost;
                bestMode = mode;
            }
        }

        code.ChromaMode = bestMode;
        bool dc = false, ac = false;
        for (int c = 0; c < 2; c++)
        {
            byte[] src = c == 0 ? _srcCb : _srcCr, plane = c == 0 ? _cb : _cr;
            if (c == 0)
            {
                H264IntraPredict.PredictChroma(_pred, 8, bestMode, t, l, _top, _left);
            }
            else
            {
                H264IntraPredict.PredictChroma(_pred, 8, bestMode, tCr, lCr, _top, _left);
            }

            (bool hasDc, bool hasAc) = CodeChromaPlane(code, c, src, plane);
            dc |= hasDc;
            ac |= hasAc;
        }

        code.Cbp = ac ? 0x20 : dc ? 0x10 : 0;
        _chromaSse = Sse(_srcCb, _cb, _chromaAt, _cstride, 8, 8) + Sse(_srcCr, _cr, _chromaAt, _cstride, 8, 8);
    }

    /// <summary>Codes one chroma plane's residual against the prediction in <see cref="_pred"/> and reconstructs it.</summary>
    private (bool Dc, bool Ac) CodeChromaPlane(H264MbCode code, int c, byte[] src, byte[] plane)
    {
        int m = _qpc % 6, q6 = _qpc / 6;
        Span<int> block = stackalloc int[16];
        Span<int> w = _w.AsSpan(0, 64);
        Span<int> dc = stackalloc int[4];
        for (int blk = 0; blk < 4; blk++)
        {
            int bx = blk & 1, by = blk >> 1;
            Residual(src, _chromaAt + (by * 4 * _cstride) + (bx * 4), _cstride, _pred, (by * 32) + (bx * 4), 8, 4, block);
            H264Forward.Transform4x4(block);
            block.CopyTo(w.Slice(blk * 16, 16));
            dc[blk] = block[0];
        }

        // The 2x2 Hadamard of the DCs, quantized one bit finer than AC (8.5.11).
        Span<int> f = [dc[0] + dc[1] + dc[2] + dc[3], dc[0] - dc[1] + dc[2] - dc[3], dc[0] + dc[1] - dc[2] - dc[3], dc[0] - dc[1] - dc[2] + dc[3]];
        int dcCount = 0;
        for (int k = 0; k < 4; k++)
        {
            int level = H264Forward.Quantize(f[k], H264Forward.Mf4[m * 16], H264Forward.Shift4 + 1 + q6, _round);
            code.ChromaDc[(c * 4) + k] = level;
            dcCount += level != 0 ? 1 : 0;
        }

        code.ChromaDcCount[c] = dcCount;
        bool anyAc = false;
        for (int blk = 0; blk < 4; blk++)
        {
            int at = ((c * 4) + blk) * 16;
            int n = QuantizeBlock(w.Slice(blk * 16, 16), code.ChromaAc, at, 1, 15, false, _qpc, 4);

            code.ChromaAc[at + 15] = 0;
            code.ChromaAcCount[(c * 4) + blk] = n;
            anyAc |= n > 0;
        }

        // Reconstruction as the decoder's: DC (8.5.11.2), then AC; AC levels count only when some block has one.
        Span<int> dcC = stackalloc int[4];
        {
            int a = code.ChromaDc[c * 4], b = code.ChromaDc[(c * 4) + 1], e = code.ChromaDc[(c * 4) + 2], g = code.ChromaDc[(c * 4) + 3];
            Span<int> inv = [a + b + e + g, a - b + e - g, a + b - e - g, a - b - e + g];
            int ls = _scale4[m * 16];
            for (int i = 0; i < 4; i++)
            {
                dcC[i] = ((inv[i] * ls) << q6) >> 5;
            }
        }

        for (int y = 0; y < 8; y++)
        {
            _pred.AsSpan(y * 8, 8).CopyTo(plane.AsSpan(_chromaAt + (y * _cstride), 8));
        }

        Span<int> d = _d.AsSpan(0, 16);
        for (int blk = 0; blk < 4; blk++)
        {
            int bx = blk & 1, by = blk >> 1;
            d.Clear();
            d[0] = dcC[blk];
            if (anyAc)
            {
                int at = ((c * 4) + blk) * 16;
                for (int k = 1; k < 16; k++)
                {
                    int level = code.ChromaAc[at + k - 1];
                    if (level != 0)
                    {
                        int pos = H264ScalingLists.Zigzag4x4[k];
                        d[pos] = Scale4x4(level, _qpc, _scale4, pos);
                    }
                }
            }

            H264Transform.Add4x4(d, plane, _chromaAt + (by * 4 * _cstride) + (bx * 4), _cstride, 255);
        }

        return (dcCount > 0, anyAc);
    }

    // ---- Quantization ----

    /// <summary>
    /// Quantizes a block's transform coefficients (raster order) into <paramref name="max"/> levels in scan order from scan
    /// position <paramref name="first"/>, then, from effort 3 on, lowers each level by one or to zero where the squared error
    /// it adds is less than λ times the bits it saves (RDOQ, greedy from the last level back); returns the non-zero levels.
    /// </summary>
    private int QuantizeBlock(ReadOnlySpan<int> w, int[] levels, int at, int first, int max, bool eight, int qp, int cat)
    {
        int m = qp % 6, q6 = qp / 6;
        byte[] scan = eight ? H264ScalingLists.Zigzag8x8 : H264ScalingLists.Zigzag4x4;
        bool rdoq = _effort >= 3;
        int round = rdoq ? 128 : _round;

        // Most blocks quantize to nothing: when the largest coefficient does, every one does.
        int qbits = (eight ? H264Forward.Shift8 : H264Forward.Shift4) + q6;
        long largest = (long)H264EncodeSimd.MaxAbs(first == 0 ? w : w[1..]) * (eight ? H264Forward.MaxMf8[m] : H264Forward.MaxMf4[m]);
        if (largest + ((long)round << (qbits - 8)) < 1L << qbits)
        {
            levels.AsSpan(at, max).Clear();
            return 0;
        }

        int n = 0;
        int[] rows = _rdoqRows;
        for (int k = 0; k < max; k++)
        {
            int pos = scan[k + first];
            int level = eight
                ? H264Forward.Quantize(w[pos], H264Forward.Mf8[(m * 64) + pos], H264Forward.Shift8 + q6, round)
                : H264Forward.Quantize(w[pos], H264Forward.Mf4[(m * 16) + pos], H264Forward.Shift4 + q6, round);
            levels[at + k] = level;

            // RDOQ's list of the non-zero levels' scan positions, made without a branch: which levels are 0 is hard to predict.
            rows[n] = k;
            n += (level | -level) >>> 31;
        }

        return rdoq && n > 0 ? Rdoq(w, levels, at, first, max, eight, qp, cat, n) : n;
    }

    private int Rdoq(ReadOnlySpan<int> w, int[] levels, int at, int first, int max, bool eight, int qp, int cat, int n)
    {
        H264EncodeStats.Add(H264EncodeStat.RdoqBlocks);
        byte[] scan = eight ? H264ScalingLists.Zigzag8x8 : H264ScalingLists.Zigzag4x4;
        double lambda = _lambda / CabacCost.Bit;
        // The error of the block as zeros, then as quantized: a level of 0 leaves its coefficient's error as it is.
        double[] weights = eight ? H264Forward.Weight8 : H264Forward.Weight4;
        double zero = ZeroError(w, weights, first);
        double distortion = zero;
        int[] rows = _rdoqRows;
        int count = n;
        for (int row = 0; row < count; row++)
        {
            int k = rows[row];
            int pos = scan[k + first];
            double step = eight ? H264Forward.Step8(qp, pos) : H264Forward.Step4(qp, pos);
            double e = w[pos] - (levels[at + k] * step);
            distortion += ((e * e) - ((double)w[pos] * w[pos])) * weights[pos];
        }

        bool cabac = _syntax.Cabac;
        long rate = cabac ? _pricer.Begin(_syntax.Contexts, cat, max, levels, at, rows, count) : BlockRate(levels, at, max, cat, n);
        H264EncodeStats.Add(H264EncodeStat.RdoqRateCounts);
        for (int row = count - 1; row >= 0; row--)
        {
            int k = rows[row];
            int level = levels[at + k];
            int pos = scan[k + first];
            double step = eight ? H264Forward.Step8(qp, pos) : H264Forward.Step4(qp, pos);
            double weight = weights[pos];
            int lower = level - Math.Sign(level);
            double before = w[pos] - (level * step), after = w[pos] - (lower * step);
            double dd = ((after * after) - (before * before)) * weight;
            levels[at + k] = lower;
            int n2 = lower == 0 ? n - 1 : n;
            long rate2 = !cabac ? BlockRate(levels, at, max, cat, n2) : n2 == 0 ? 0 : _pricer.Price(k, level);
            H264EncodeStats.Add(H264EncodeStat.RdoqRateCounts, n2 == 0 ? 0 : 1);
            if (dd + (lambda * (rate2 - rate)) < 0)
            {
                distortion += dd;
                rate = rate2;
                n = n2;
                if (cabac)
                {
                    _pricer.Accept();
                }
            }
            else
            {
                levels[at + k] = level;
            }

            if (cabac)
            {
                _pricer.Advance(k);
            }
        }

        // The whole block as zeros: coded_block_flag 0 instead of the levels.
        if (n > 0 && zero <= distortion + (lambda * rate))
        {
            levels.AsSpan(at, max).Clear();
            n = 0;
        }

        return n;
    }

    // The weighted squared error of a block's coefficients (raster order) from first on, all quantized to 0. The scan order
    // reaches every position from the second on, so only the DC's place depends on first; four sums keep the adds apart.
    private static double ZeroError(ReadOnlySpan<int> w, double[] weights, int first)
    {
        double a = 0, b = 0, c = 0, d = 0;
        for (int i = 0; i < weights.Length; i += 4)
        {
            a += (double)w[i] * w[i] * weights[i];
            b += (double)w[i + 1] * w[i + 1] * weights[i + 1];
            c += (double)w[i + 2] * w[i + 2] * weights[i + 2];
            d += (double)w[i + 3] * w[i + 3] * weights[i + 3];
        }

        double all = (a + b) + (c + d);
        return first == 0 ? all : all - ((double)w[0] * w[0] * weights[0]);
    }

    /// <summary>The CAVLC bits of a block's levels with nC 0, in 1/32768 bit (CABAC blocks go to <see cref="H264BlockPricer"/>).</summary>
    private static long BlockRate(int[] levels, int at, int max, int cat, int n)
    {
        if (n == 0)
        {
            return 0;
        }

        BitCountSink bits = default;
        if (cat == 5)
        {
            Span<int> list = stackalloc int[16];
            for (int i4 = 0; i4 < 4; i4++)
            {
                for (int k = 0; k < 16; k++)
                {
                    list[k] = levels[at + (4 * k) + i4];
                }

                H264MbSyntax.BlockCavlc(ref bits, 0, 16, list);
            }
        }
        else
        {
            H264MbSyntax.BlockCavlc(ref bits, 0, max, levels.AsSpan(at, max));
        }

        return bits.Bits * CabacCost.Bit;
    }

    /// <summary>Scales a block's levels (scan order from <paramref name="first"/>) into raster coefficients as the decoder does.</summary>
    private void Dequantize(int[] levels, int at, int first, int max, bool eight, int qp, Span<int> d)
    {
        d.Clear();
        byte[] scan = eight ? H264ScalingLists.Zigzag8x8 : H264ScalingLists.Zigzag4x4;
        for (int k = 0; k < max; k++)
        {
            int level = levels[at + k];
            if (level != 0)
            {
                int pos = scan[k + first];
                d[pos] = eight ? Scale8x8(level, qp, _scale8, pos) : Scale4x4(level, qp, _scale4, pos);
            }
        }
    }

    // ---- Shared ----

    /// <summary>LevelScale scaling of a 4x4 level (8.5.12.1), as the decoder's.</summary>
    internal static int Scale4x4(int level, int qp, int[] scale, int pos)
    {
        int ls = scale[((qp % 6) * 16) + pos];
        int shift = qp / 6;
        return shift >= 4 ? (level * ls) << (shift - 4) : ((level * ls) + (1 << (3 - shift))) >> (4 - shift);
    }

    /// <summary>LevelScale scaling of an 8x8 level, as the decoder's.</summary>
    internal static int Scale8x8(int level, int qp, int[] scale, int pos)
    {
        int ls = scale[((qp % 6) * 64) + pos];
        int shift = qp / 6;
        return shift >= 6 ? (level * ls) << (shift - 6) : ((level * ls) + (1 << (5 - shift))) >> (6 - shift);
    }

    /// <summary>The residual of an n by n block: source minus prediction, raster order.</summary>
    internal static void Residual(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int n, Span<int> r)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                r[(y * n) + x] = src[at + (y * stride) + x] - pred[predAt + (y * predStride) + x];
            }
        }
    }

    /// <summary>The SATD of a block against a prediction (<see cref="H264EncodeSimd.Satd"/>).</summary>
    internal static long Satd(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int width, int height)
    {
        H264EncodeStats.Add(H264EncodeStat.SatdCalls);
        H264EncodeStats.Add(H264EncodeStat.SatdSamples, width * height);
        return H264EncodeSimd.Satd(src, at, stride, pred, predAt, predStride, width, height);
    }

    /// <summary>The sum of squared differences of two planes over an area.</summary>
    internal static long Sse(byte[] a, byte[] b, int at, int stride, int width, int height) => H264EncodeSimd.Sse(a, b, at, stride, width, height);
}
