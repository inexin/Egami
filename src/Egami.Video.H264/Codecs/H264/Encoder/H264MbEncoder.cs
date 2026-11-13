using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    private readonly byte[] _edges = new byte[H264IntraPredict.EdgeSize];
    private readonly int[] _modeSatd = new int[9];

    // The SATD-scale cost of an Intra4x4 or Intra8x8 mode's bits: the predicted mode (1 bit) and any other (4).
    private long _modeBits1, _modeBits4;
    private readonly H264BlockPricer _pricer = new();
    private readonly CabacUndoLog _undo = new(460);
    private readonly int[] _rdoqRows = new int[64];
    private readonly double[] _rdoqGains = new double[64];
    private readonly int[] _quant = new int[64];
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
        Shortcuts = DefaultShortcuts(effort);
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
    [SkipLocalsInit]
    public double Encode(int mbX, int mbY, int qp)
    {
        H264EncodeStats.Add(H264EncodeStat.IntraPasses);

        // The 16x16 SATDs TryIntra worked out just before, for this macroblock, still hold: its neighbours have not changed.
        if (!_i16Pending)
        {
            _i16Satd.AsSpan().Fill(-1);
        }

        _i16Pending = false;
        Setup(mbX, mbY, qp);
        _round = IntraRound;
        CodeChroma();

        int count = _allow8x8 && _effort >= 1 ? 3 : 2;

        // Task 8: rank I16, I4 and I8 by SATD and mode bits, and code the best (and the next when it comes close). I16 is
        // estimated first, then I8, then I4; an estimate stops once it is past the margin of the best so far, as it can then be
        // neither the best nor the second coded (its partial sum stands in, already past that margin of the final best too).
        Span<long> estimate = stackalloc long[3];
        Span<int> order = [0, 1, 2];
        estimate[0] = EstimateI16();
        long least = estimate[0];
        for (int c = count - 1; c >= 1; c--)
        {
            estimate[c] = EstimateNxN(c == 1 ? 4 : 8, least);
            least = Math.Min(least, estimate[c]);
        }

        for (int i = 1; i < count; i++)
        {
            for (int j = i; j > 0 && estimate[order[j]] < estimate[order[j - 1]]; j--)
            {
                (order[j], order[j - 1]) = (order[j - 1], order[j]);
            }
        }

        int coded = _effort >= 10 ? count : estimate[order[1]] - estimate[order[0]] <= estimate[order[0]] * IntraRankMargin ? 2 : 1;
        int best = order[0];
        double bestCost = double.MaxValue;
        for (int o = 0; o < coded; o++)
        {
            int c = order[o];
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
    /// coding as intra too: always from effort 7, else when intra 16x16 prediction comes within <paramref name="margin"/>
    /// sixteenths of it by SATD.
    /// </summary>
    private bool TryIntra(long interSatd, int margin)
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
        _i16Satd.AsSpan().Fill(-1);
        long limit = interSatd * margin / 16;
        for (int mode = 0; mode < 4; mode++)
        {
            if (H264IntraPredict.Allowed16x16(mode, _top, _left, _topLeft))
            {
                H264IntraPredict.Predict16x16(_pred, 16, mode, t, l, _top, _left);
                long satd = _i16Satd[mode] = Satd(_srcY, _lumaAt, _stride, _pred, 0, 16, 16, 16);
                if (satd < limit)
                {
                    // Encode follows at once for this macroblock and takes these SATDs over.
                    _i16Pending = true;
                    return true;
                }
            }
        }

        return false;
    }

    // The SATD of each Intra16x16 mode's prediction of this macroblock (-1 until worked out), and its best mode by SATD and mode
    // bits; worked out once for TryIntra, EstimateI16 and CodeI16. Pending: TryIntra filled them and Encode follows.
    private readonly long[] _i16Satd = [-1, -1, -1, -1];
    private bool _i16Pending;
    private int _i16Mode;

    // Task 8: the second-best intra kind by SATD and mode bits is RD-coded too when it comes within this share of the best; at
    // effort 10 every kind is.
    private const double IntraRankMargin = 0.25;

    /// <summary>How far above the inter cost, in sixteenths, an intra 16x16 SATD still tries intra.</summary>
    internal static int IntraMargin { get; set; } = 24;

    /// <summary>
    /// <see cref="IntraMargin"/> in B pictures at effort 4: intra only below the inter cost, where B pictures rarely pick it.
    /// </summary>
    private const int BIntraMargin = 16;

    private void Setup(int mbX, int mbY, int qp)
    {
        _mbX = mbX;
        _mbY = mbY;
        _qp = qp;
        _pricer.Invalidate();
        _qpc = H264Tables.ChromaQp[Math.Clamp(qp + _syntax.ChromaQpOffset, 0, 51)];
        _lambda = H264Forward.Lambda(qp);
        _sadLambda = Math.Sqrt(_lambda);
        (_modeBits1, _modeBits4) = ((long)(_sadLambda * 1), (long)(_sadLambda * 4));
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

    // The SATD and mode bits of the best 16x16 prediction, as CodeI16 chooses it.
    private long EstimateI16()
    {
        int[] t = _t, l = _l;
        bool gathered = false;
        long bestCost = long.MaxValue;
        int bestMode = 2;
        for (int mode = 0; mode < 4; mode++)
        {
            if (H264IntraPredict.Allowed16x16(mode, _top, _left, _topLeft))
            {
                long satd = _i16Satd[mode];
                if (satd < 0)
                {
                    if (!gathered)
                    {
                        H264IntraPredict.Gather(_y, _lumaAt, _stride, 16, _top, false, _left, _topLeft, t, l);
                        gathered = true;
                    }

                    H264IntraPredict.Predict16x16(_pred, 16, mode, t, l, _top, _left);
                    satd = _i16Satd[mode] = Satd(_srcY, _lumaAt, _stride, _pred, 0, 16, 16, 16);
                }

                long cost = satd + (long)(_sadLambda * (mode == 2 ? 1 : 3));
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                }
            }
        }

        _i16Mode = bestMode;
        return bestCost;
    }

    // The SATD and mode bits of the best n by n predictions (4 or 8) as CodeI4 and CodeI8 choose them, predicted from the
    // source's samples instead of the reconstruction's, so nothing has to be coded first.
    private long EstimateNxN(int n, long least)
    {
        int[] t = _t, l = _l;
        long total = 0;
        int blocks = n == 4 ? 16 : 4;
        for (int i = 0; i < blocks; i++)
        {
            int bx, by, blk;
            if (n == 4)
            {
                int xy = H264Tables.BlockXY[i];
                (bx, by, blk) = (xy & 3, xy >> 2, i);
            }
            else
            {
                (bx, by, blk) = ((i & 1) * 2, (i >> 1) * 2, i * 4);
            }

            int at = _lumaAt + (by * 4 * _stride) + (bx * 4);
            (bool top, bool left, bool corner, bool topRight) = Neighbors(bx * 4, by * 4, n, blk);
            H264IntraPredict.Gather(_srcY, at, _stride, n, top, topRight, left, corner, t, l);
            if (n == 8)
            {
                H264IntraPredict.Filter8x8(t, l, top, left, corner);
            }

            H264IntraPredict.Edges(n, t, l, top, left, _edges);
            Span<int> satd = ModeSatds(n, at);
            int predicted = _syntax.PredictedIntraMode(bx, by);
            long bestCost = long.MaxValue;
            for (int mode = 0; mode < 9; mode++)
            {
                if (H264IntraPredict.NxNAllowed(mode, top, left, corner))
                {
                    bestCost = Math.Min(bestCost, satd[mode] + (mode == predicted ? _modeBits1 : _modeBits4));
                }
            }

            total += bestCost;
            if (total - least > least * IntraRankMargin)
            {
                return total;
            }
        }

        return total;
    }

    [SkipLocalsInit]
    private long CodeI16(H264MbCode code)
    {
        code.Kind = H264MbKind.I16x16;
        code.Qp = _qp;
        int[] t = _t, l = _l;
        H264IntraPredict.Gather(_y, _lumaAt, _stride, 16, _top, false, _left, _topLeft, t, l);

        // The mode EstimateI16 chose for this macroblock (Encode always estimates before it codes).
        int bestMode = _i16Mode;
        code.I16Mode = bestMode;
        H264IntraPredict.Predict16x16(_pred, 16, bestMode, t, l, _top, _left);

        // Forward transforms of the 16 blocks (in raster order of blocks); their DCs go through the Hadamard.
        int m = _qp % 6, q6 = _qp / 6;
        H264Forward.ResidualTransform4x4(_srcY, _lumaAt, _stride, _pred, 0, 16, 16, 16, _w);
        for (int r = 0; r < 16; r++)
        {
            _dc[r] = _w[r * 16];
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
            int xy = H264Tables.BlockXY[blk];
            int n = QuantizeBlock(_w.AsSpan((((xy >> 2) * 4) + (xy & 3)) * 16, 16), code.Luma, blk * 16, 1, 15, false, _qp, 1);
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
    [SkipLocalsInit]
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

    [SkipLocalsInit]
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
            H264IntraPredict.Edges(4, t, l, top, left, _edges);
            Span<int> satd = ModeSatds(4, at);
            int predicted = _syntax.PredictedIntraMode(bx, by);
            int bestMode = 2;
            long bestCost = long.MaxValue;
            for (int mode = 0; mode < 9; mode++)
            {
                if (!H264IntraPredict.NxNAllowed(mode, top, left, corner))
                {
                    continue;
                }

                long cost = satd[mode] + (mode == predicted ? _modeBits1 : _modeBits4);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                }
            }

            code.Modes[blk] = bestMode;
            _mb.IntraModes[(((_mbY * 4) + by) * _mb.W4) + (_mbX * 4) + bx] = (sbyte)bestMode;
            H264IntraPredict.PredictNxN(_pred, 4, 4, bestMode, _edges);
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

    [SkipLocalsInit]
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
            H264IntraPredict.Edges(8, t, l, top, left, _edges);
            Span<int> satd = ModeSatds(8, at);
            int predicted = _syntax.PredictedIntraMode(bx, by);
            int bestMode = 2;
            long bestCost = long.MaxValue;
            for (int mode = 0; mode < 9; mode++)
            {
                if (!H264IntraPredict.NxNAllowed(mode, top, left, corner))
                {
                    continue;
                }

                long cost = satd[mode] + (mode == predicted ? _modeBits1 : _modeBits4);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                }
            }

            code.Modes[i8] = bestMode;
            int b = (((_mbY * 4) + by) * _mb.W4) + (_mbX * 4) + bx;
            _mb.IntraModes[b] = _mb.IntraModes[b + 1] = _mb.IntraModes[b + _mb.W4] = _mb.IntraModes[b + _mb.W4 + 1] = (sbyte)bestMode;
            H264IntraPredict.PredictNxN(_pred, 8, 8, bestMode, _edges);
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
    [SkipLocalsInit]
    private (bool Dc, bool Ac) CodeChromaPlane(H264MbCode code, int c, byte[] src, byte[] plane)
    {
        int m = _qpc % 6, q6 = _qpc / 6;
        Span<int> w = _w.AsSpan(0, 64);
        Span<int> dc = stackalloc int[4];
        H264Forward.ResidualTransform4x4(src, _chromaAt, _cstride, _pred, 0, 8, 8, 8, w);
        for (int blk = 0; blk < 4; blk++)
        {
            dc[blk] = w[blk * 16];
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

        // Every coefficient quantized at once (raster order), then read out in scan order.
        int[] q = _quant;
        if (eight)
        {
            Quantize8(w, H264Forward.Mf8.AsSpan(m * 64, 64), qbits, round, q);
        }
        else
        {
            Quantize4(w, H264Forward.Mf4.AsSpan(m * 16, 16), qbits, round, q);
        }

        int n = 0;
        int[] rows = _rdoqRows;
        Span<int> dst = levels.AsSpan(at, max);
        if (Avx2.IsSupported && max == (eight ? 64 : 16) - first && (!eight || first == 0))
        {
            // The levels in scan order and a mask of the non-zero ones, whose positions are RDOQ's list.
            ulong mask = eight ? Scan8(q, dst) : Scan4(q, first, dst);
            while (mask != 0)
            {
                rows[n++] = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
            }
        }
        else
        {
            ref byte scanAt = ref MemoryMarshal.GetArrayDataReference(scan);
            ref int qAt = ref MemoryMarshal.GetArrayDataReference(q);
            for (int k = 0; k < dst.Length; k++)
            {
                int level = Unsafe.Add(ref qAt, Unsafe.Add(ref scanAt, k + first));
                dst[k] = level;

                // RDOQ's list of the non-zero levels' scan positions, made without a branch: which levels are 0 is hard to predict.
                rows[n] = k;
                n += (level | -level) >>> 31;
            }
        }

        return rdoq && n > 0 ? Rdoq(w, levels, at, first, max, eight, qp, cat, n) : n;
    }

    // The 4x4 zigzag scan as lane permutations of the two halves of a raster block: the lanes of each half of the scan, and
    // which of them come from the second half of the block.
    private static readonly Vector256<int> ScanLow = Vector256.Create(0, 1, 4, 0, 5, 2, 3, 6);
    private static readonly Vector256<int> ScanLowHigh = Vector256.Create(0, 0, 0, -1, 0, 0, 0, 0);
    private static readonly Vector256<int> ScanHigh = Vector256.Create(1, 4, 5, 2, 7, 3, 6, 7);
    private static readonly Vector256<int> ScanHighHigh = Vector256.Create(-1, -1, -1, -1, 0, -1, -1, -1);

    // The 8x8 zigzag scan's raster positions.
    private static readonly int[] Scan8x8 = Array.ConvertAll(H264ScalingLists.Zigzag8x8, p => (int)p);

    /// <summary>
    /// Puts a 4x4 block's quantized levels (raster order) into <paramref name="dst"/> in scan order from <paramref name="first"/>
    /// on, and returns the mask of the non-zero ones by position in <paramref name="dst"/>.
    /// </summary>
    private static ulong Scan4(int[] q, int first, Span<int> dst)
    {
        Vector256<int> low = Vector256.Create(q, 0), high = Vector256.Create(q, 8);
        Vector256<int> a = Vector256.ConditionalSelect(ScanLowHigh, Avx2.PermuteVar8x32(high, ScanLow), Avx2.PermuteVar8x32(low, ScanLow));
        Vector256<int> b = Vector256.ConditionalSelect(ScanHighHigh, Avx2.PermuteVar8x32(high, ScanHigh), Avx2.PermuteVar8x32(low, ScanHigh));
        uint mask = ~(Vector256.Equals(a, Vector256<int>.Zero).ExtractMostSignificantBits() | (Vector256.Equals(b, Vector256<int>.Zero).ExtractMostSignificantBits() << 8)) & 0xFFFF;
        if (first == 0)
        {
            a.CopyTo(dst);
            b.CopyTo(dst[8..]);
            return mask;
        }

        Span<int> all = stackalloc int[16];
        a.CopyTo(all);
        b.CopyTo(all[8..]);
        all[first..].CopyTo(dst);
        return mask >> first;
    }

    /// <summary>Puts an 8x8 block's quantized levels (raster order) into <paramref name="dst"/> in scan order and returns the mask of the non-zero ones.</summary>
    private static unsafe ulong Scan8(int[] q, Span<int> dst)
    {
        ulong mask = 0;
        fixed (int* qAt = q)
        {
            for (int i = 0; i < 64; i += 8)
            {
                Vector256<int> v = Avx2.GatherVector256(qAt, Vector256.Create(Scan8x8, i), 4);
                v.CopyTo(dst[i..]);
                mask |= (ulong)(~Vector256.Equals(v, Vector256<int>.Zero).ExtractMostSignificantBits() & 0xFF) << i;
            }
        }

        return mask;
    }

    private int Rdoq(ReadOnlySpan<int> w, int[] levels, int at, int first, int max, bool eight, int qp, int cat, int n)
    {
        H264EncodeStats.Add(H264EncodeStat.RdoqBlocks);
        ReadOnlySpan<byte> scan = (eight ? H264ScalingLists.Zigzag8x8 : H264ScalingLists.Zigzag4x4).AsSpan(first);
        ReadOnlySpan<double> steps = eight ? H264Forward.Steps8At(qp) : H264Forward.Steps4At(qp);
        double lambda = _lambda / CabacCost.Bit;
        // The error of the block as zeros, then as quantized: a level of 0 leaves its coefficient's error as it is.
        double[] weights = eight ? H264Forward.Weight8 : H264Forward.Weight4;
        double zero = ZeroError(w, weights, first);
        double distortion = zero;
        int[] rows = _rdoqRows;
        double[] gains = _rdoqGains;
        int count = n;
        for (int row = 0; row < count; row++)
        {
            // The error as quantized, and how it changes when the level is lowered one step (which the other levels' choices
            // leave as it is).
            int k = rows[row];
            int pos = scan[k];
            int level = levels[at + k];
            double step = steps[pos];
            double weight = weights[pos];
            int lower = level - ((level >> 31) | 1);
            double before = w[pos] - (level * step), after = w[pos] - (lower * step);
            distortion += ((before * before) - ((double)w[pos] * w[pos])) * weight;
            gains[row] = ((after * after) - (before * before)) * weight;
        }

        long rate;
        if (!_syntax.Cabac)
        {
            rate = BlockRate(levels, at, max, cat, n);
            H264EncodeStats.Add(H264EncodeStat.RdoqRateCounts);
            for (int row = count - 1; row >= 0; row--)
            {
                int k = rows[row];
                int level = levels[at + k];
                int lower = level - ((level >> 31) | 1);
                int n2 = lower == 0 ? n - 1 : n;
                double dd = gains[row];
                levels[at + k] = lower;
                long cavlc = BlockRate(levels, at, max, cat, n2);
                H264EncodeStats.Add(H264EncodeStat.RdoqRateCounts, n2 == 0 ? 0 : 1);
                if (dd + (lambda * (cavlc - rate)) < 0)
                {
                    distortion += dd;
                    rate = cavlc;
                    n = n2;
                }
                else
                {
                    levels[at + k] = level;
                }
            }
        }
        else
        {
            H264BlockPricer pricer = _pricer;
            rate = pricer.Begin(_syntax.Contexts, cat, max, levels, at, rows, count);
            H264EncodeStats.Add(H264EncodeStat.RdoqRateCounts);
            ref int levelAt = ref levels[at];
            for (int row = count - 1; row >= 0; row--)
            {
                // The choice taken without a branch, as whether RDOQ lowers a level is hard to predict.
                int k = rows[row];
                ref int slot = ref Unsafe.Add(ref levelAt, k);
                int level = slot;
                int lower = level - ((level >> 31) | 1);
                int n2 = lower == 0 ? n - 1 : n;
                double dd = gains[row];
                slot = lower;
                long rate2 = n2 == 0 ? 0 : pricer.Price(k, level);
                H264EncodeStats.Add(H264EncodeStat.RdoqRateCounts, n2 == 0 ? 0 : 1);
                bool take = dd + (lambda * (rate2 - rate)) < 0;
                int taken = take ? 1 : 0;
                distortion += dd * taken;
                rate = take ? rate2 : rate;
                n = take ? n2 : n;
                slot = take ? lower : level;
                pricer.Accept(take);
                pricer.Advance(k);
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
        if (Avx.IsSupported)
        {
            // The same four sums, one per lane, each term rounded as below.
            ref int wAt = ref MemoryMarshal.GetReference(w);
            ref double weightAt = ref MemoryMarshal.GetArrayDataReference(weights);
            Vector256<double> sum = Vector256<double>.Zero;
            for (int i = 0; i < weights.Length; i += 4)
            {
                Vector256<double> v = Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref wAt, (nuint)i));
                sum += v * v * Vector256.LoadUnsafe(ref weightAt, (nuint)i);
            }

            double whole = (sum.GetElement(0) + sum.GetElement(1)) + (sum.GetElement(2) + sum.GetElement(3));
            return first == 0 ? whole : whole - ((double)w[0] * w[0] * weights[0]);
        }

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
        // Every position the scan reaches is written (a level of 0 scales to 0 either way), so only the rest is cleared.
        if (first + max < d.Length || first > 0)
        {
            d.Clear();
        }

        ReadOnlySpan<byte> scan = (eight ? H264ScalingLists.Zigzag8x8 : H264ScalingLists.Zigzag4x4).AsSpan(first, max);
        ReadOnlySpan<int> levelsAt = levels.AsSpan(at, max);
        int shift = qp / 6;
        int fraction = eight ? 6 : 4;
        ReadOnlySpan<int> scale = eight ? _scale8.AsSpan((qp % 6) * 64, 64) : _scale4.AsSpan((qp % 6) * 16, 16);
        if (shift >= fraction)
        {
            int up = shift - fraction;
            for (int k = 0; k < levelsAt.Length; k++)
            {
                int pos = scan[k];
                d[pos] = (levelsAt[k] * scale[pos]) << up;
            }
        }
        else
        {
            int down = fraction - shift, half = 1 << (down - 1);
            for (int k = 0; k < levelsAt.Length; k++)
            {
                int pos = scan[k];
                d[pos] = ((levelsAt[k] * scale[pos]) + half) >> down;
            }
        }
    }

    // ---- Shared ----

    /// <summary>
    /// <see cref="H264Forward.Quantize"/> of the 16 coefficients of a 4x4 block (raster order) into <paramref name="q"/>. A 4x4
    /// coefficient is at most 36 · 255 and an MF at most 13107, so the products and the rounding offset fit an int.
    /// </summary>
    internal static void Quantize4(ReadOnlySpan<int> w, ReadOnlySpan<int> mf, int qbits, int round, int[] q)
    {
        int offset = round << (qbits - 8);
        if (Vector256.IsHardwareAccelerated)
        {
            ref int wAt = ref MemoryMarshal.GetReference(w);
            ref int mfAt = ref MemoryMarshal.GetReference(mf);
            ref int qAt = ref MemoryMarshal.GetArrayDataReference(q);
            Vector256<int> add = Vector256.Create(offset);
            for (int i = 0; i < 16; i += 8)
            {
                Vector256<int> v = Vector256.LoadUnsafe(ref wAt, (nuint)i);
                Vector256<int> level = Vector256.ShiftRightLogical((Vector256.Abs(v) * Vector256.LoadUnsafe(ref mfAt, (nuint)i)) + add, qbits);
                Vector256<int> sign = Vector256.ShiftRightArithmetic(v, 31);
                ((level ^ sign) - sign).StoreUnsafe(ref qAt, (nuint)i);
            }

            return;
        }

        for (int i = 0; i < 16; i++)
        {
            q[i] = H264Forward.Quantize(w[i], mf[i], qbits, round);
        }
    }

    /// <summary><see cref="H264Forward.Quantize"/> of the 64 coefficients of an 8x8 block (raster order), whose products need 64 bits.</summary>
    internal static void Quantize8(ReadOnlySpan<int> w, ReadOnlySpan<int> mf, int qbits, int round, int[] q)
    {
        if (Avx2.IsSupported)
        {
            ref int wAt = ref MemoryMarshal.GetReference(w);
            ref int mfAt = ref MemoryMarshal.GetReference(mf);
            ref int qAt = ref MemoryMarshal.GetArrayDataReference(q);
            Vector256<ulong> add = Vector256.Create((ulong)round << (qbits - 8));
            for (int i = 0; i < 64; i += 8)
            {
                // The even lanes' products, then the odd lanes', each in 64 bits; the levels fit the low halves.
                Vector256<int> v = Vector256.LoadUnsafe(ref wAt, (nuint)i);
                Vector256<uint> a = Vector256.Abs(v).AsUInt32(), f = Vector256.LoadUnsafe(ref mfAt, (nuint)i).AsUInt32();
                Vector256<ulong> even = (Avx2.Multiply(a, f) + add) >>> qbits;
                Vector256<ulong> odd = (Avx2.Multiply((a.AsUInt64() >>> 32).AsUInt32(), (f.AsUInt64() >>> 32).AsUInt32()) + add) >>> qbits;
                Vector256<int> level = even.AsInt32() | (odd << 32).AsInt32();
                Vector256<int> sign = Vector256.ShiftRightArithmetic(v, 31);
                ((level ^ sign) - sign).StoreUnsafe(ref qAt, (nuint)i);
            }

            return;
        }

        for (int i = 0; i < 64; i++)
        {
            q[i] = H264Forward.Quantize(w[i], mf[i], qbits, round);
        }
    }

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

    /// <summary>The SATDs of the nine Intra4x4 or Intra8x8 modes' predictions from the lines in _edges of the n by n block at <paramref name="at"/>.</summary>
    private Span<int> ModeSatds(int n, int at)
    {
        H264EncodeStats.Add(H264EncodeStat.SatdCalls, 9);
        H264EncodeStats.Add(H264EncodeStat.SatdSamples, 9 * n * n);
        H264IntraPredict.SatdNxN(n, _srcY, at, _stride, _edges, _pred, _modeSatd);
        return _modeSatd;
    }

    /// <summary>The SATD of a block against a prediction (<see cref="H264EncodeSimd.Satd"/>).</summary>
    internal static long Satd(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int width, int height)
    {
        H264EncodeStats.Add(H264EncodeStat.SatdCalls);
        H264EncodeStats.Add(H264EncodeStat.SatdSamples, width * height);
        return H264EncodeSimd.Satd(src, at, stride, pred, predAt, predStride, width, height);
    }

    /// <summary>The SA8D of a block against a prediction (<see cref="H264EncodeSimd.Sa8d"/>).</summary>
    internal static long Sa8d(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int width, int height)
    {
        H264EncodeStats.Add(H264EncodeStat.Sa8dCalls);
        return H264EncodeSimd.Sa8d(src, at, stride, pred, predAt, predStride, width, height);
    }

    /// <summary>The sum of squared differences of two planes over an area.</summary>
    internal static long Sse(byte[] a, byte[] b, int at, int stride, int width, int height) => H264EncodeSimd.Sse(a, b, at, stride, width, height);
}
