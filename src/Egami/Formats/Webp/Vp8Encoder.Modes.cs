using System.Numerics;

namespace Egami.Formats.Webp;

/// <summary>The macroblock loop: prediction, mode choice, quantization and reconstruction.</summary>
internal sealed partial class Vp8Encoder
{
    private const int RdDistoMult = 256;
    private const int FlatnessPenalty = 140;

    // The 16 × 16 and chroma modes in libwebp's search order (DC, TrueMotion, vertical, horizontal), which breaks ties.
    private static readonly int[] BlockModeOrder = [0, 3, 1, 2];

    // The 4 × 4 modes in libwebp's search order.
    private static readonly int[] SubModeOrder = [Vp8Dsp.BDc, Vp8Dsp.BTm, Vp8Dsp.BVe, Vp8Dsp.BHe, Vp8Dsp.BRd, Vp8Dsp.BVr, Vp8Dsp.BLd, Vp8Dsp.BVl, Vp8Dsp.BHd, Vp8Dsp.BHu];

    private static ReadOnlySpan<byte> TrellisWeights => [30, 27, 19, 11, 27, 24, 17, 10, 19, 17, 12, 8, 11, 10, 8, 6];

    // The macroblock's source and reconstructions: luma at column 0, U at 16 and V at 24, rows of the work stride.
    private readonly byte[] _yuvIn = new byte[16 * S];
    private byte[] _yuvOut = new byte[16 * S];
    private byte[] _yuvOut2 = new byte[16 * S];

    // Neighbours in the decoder's layout (origin C0), the predictions of each mode, and the 4 × 4 work area.
    private readonly byte[] _ctxY = new byte[17 * S];
    private readonly byte[] _ctxU = new byte[9 * S];
    private readonly byte[] _ctxV = new byte[9 * S];
    private readonly byte[][] _predY16 = [new byte[16 * S], new byte[16 * S], new byte[16 * S], new byte[16 * S]];
    private readonly byte[][] _predUv = [new byte[8 * S], new byte[8 * S], new byte[8 * S], new byte[8 * S]];
    private readonly byte[] _i4Work = new byte[17 * S];
    private readonly byte[] _predI4 = new byte[8 * S];
    private byte[] _i4Tmp = new byte[4 * S];
    private byte[] _i4Best = new byte[4 * S];

    // The ten 4 × 4 trials of a block, made two at a time: their levels one after the other, their reconstructions in pairs side
    // by side (see TrialAt).
    private readonly short[] _trialLevels = new short[160];
    private readonly byte[] _trialRecon = new byte[8 * S];

    // The reconstructed neighbours of the next macroblocks (unfiltered, as intra prediction sees them).
    private readonly byte[] _topY;
    private readonly byte[] _topU;
    private readonly byte[] _topV;
    private readonly byte[] _leftY = new byte[16];
    private readonly byte[] _leftU = new byte[8];
    private readonly byte[] _leftV = new byte[8];
    private readonly byte[] _corner = new byte[3];

    /// <summary>Gets or sets planes that receive the unfiltered reconstruction, for tests.</summary>
    internal Vp8Planes? Reconstruction { get; set; }

    // Non-zero contexts (4 luma, 2 U, 2 V, Y2): committed by token recording, and a working copy for rate estimates.
    private readonly byte[] _topNz;
    private readonly byte[] _leftNz = new byte[9];
    private readonly byte[] _nzTop = new byte[9];
    private readonly byte[] _nzLeft = new byte[9];

    private readonly byte[] _modesAbove;
    private readonly byte[] _modesLeft = new byte[4];

    // Chroma DC error diffusion: [x × 4 + channel × 2 + k] above and [channel × 2 + k] to the left.
    private readonly sbyte[] _topDerr;
    private readonly sbyte[] _leftDerr = new sbyte[4];

    private readonly ModeScore _rd = new();
    private readonly ModeScore _rdTmp = new();
    private readonly ModeScore _rdI4 = new();
    private readonly short[] _tmpLevels = new short[16];
    private readonly Vp8LevelCosts _costs = new();
    private bool _doTrellis;

    private void MainLoop()
    {
        Array.Clear(_topNz);
        Array.Clear(_modesAbove);
        Array.Clear(_topDerr);
        Array.Clear(_zero);
        foreach (Segment m in _dqm)
        {
            m.MaxEdge = 0;
        }

        ResetTokens();
        _costs.Update(Vp8Tables.DefaultCoeffProbs);
        int maxCount = Math.Max((_mbw * _mbh) >> 3, 96);
        int count = maxCount;
        bool adapt = Effort >= 4;
        bool errorDiffusion = _options.Quality <= 98;
        for (int mby = 0; mby < _mbh; mby++)
        {
            Array.Clear(_leftNz);
            Array.Clear(_modesLeft);
            Array.Clear(_leftDerr);
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                int mb = (mby * _mbw) + mbx;
                if (adapt && --count < 0)
                {
                    RunningProbabilities();
                    count = maxCount;
                }

                Import(mbx, mby);
                ReconstructedContext(mbx, mby);
                MakePredictions(mbx, mby);
                _topNz.AsSpan(mbx * 9, 9).CopyTo(_nzTop);
                _leftNz.CopyTo(_nzLeft);
                Decimate(mbx, mby, mb, errorDiffusion);
                RecordMacroblock(mbx, mb);
                SaveBoundary(mbx, mby, mb);
            }
        }

        _tokenStart[_mbw * _mbh] = _tokenCount;
    }

    /// <summary>Copies the macroblock's source samples into <see cref="_yuvIn"/>.</summary>
    private void Import(int mbx, int mby)
    {
        int ys = _src.Stride, cs = _src.ChromaStride;
        for (int j = 0; j < 16; j++)
        {
            _src.Y.AsSpan(((mby * 16) + j) * ys + (mbx * 16), 16).CopyTo(_yuvIn.AsSpan(j * S, 16));
        }

        for (int j = 0; j < 8; j++)
        {
            _src.U.AsSpan(((mby * 8) + j) * cs + (mbx * 8), 8).CopyTo(_yuvIn.AsSpan((j * S) + UOff, 8));
            _src.V.AsSpan(((mby * 8) + j) * cs + (mbx * 8), 8).CopyTo(_yuvIn.AsSpan((j * S) + VOff, 8));
        }
    }

    /// <summary>The neighbours for analysis: the source's own samples, with the decoder's edge values outside the frame.</summary>
    private void SourceContext(int mbx, int mby)
    {
        int ys = _src.Stride, cs = _src.ChromaStride;
        Span<byte> top = stackalloc byte[20];
        Span<byte> left = stackalloc byte[16];
        int x0 = mbx * 16, y0 = mby * 16;
        if (mby > 0)
        {
            _src.Y.AsSpan(((y0 - 1) * ys) + x0, 16).CopyTo(top);
        }

        if (mbx > 0)
        {
            for (int j = 0; j < 16; j++)
            {
                left[j] = _src.Y[((y0 + j) * ys) + x0 - 1];
            }
        }

        byte corner = mbx == 0 ? (byte)(mby > 0 ? 129 : 127) : mby == 0 ? (byte)127 : _src.Y[((y0 - 1) * ys) + x0 - 1];
        FillContext(_ctxY, 16, top, mby > 0, left, mbx > 0, corner);
        for (int plane = 0; plane < 2; plane++)
        {
            byte[] src = plane == 0 ? _src.U : _src.V;
            int cx = mbx * 8, cy = mby * 8;
            if (mby > 0)
            {
                src.AsSpan(((cy - 1) * cs) + cx, 8).CopyTo(top);
            }

            if (mbx > 0)
            {
                for (int j = 0; j < 8; j++)
                {
                    left[j] = src[((cy + j) * cs) + cx - 1];
                }
            }

            corner = mbx == 0 ? (byte)(mby > 0 ? 129 : 127) : mby == 0 ? (byte)127 : src[((cy - 1) * cs) + cx - 1];
            FillContext(plane == 0 ? _ctxU : _ctxV, 8, top, mby > 0, left, mbx > 0, corner);
        }
    }

    /// <summary>The neighbours for coding: the reconstruction, exactly as the decoder will see it.</summary>
    private void ReconstructedContext(int mbx, int mby)
    {
        Span<byte> top = stackalloc byte[20];
        if (mby > 0)
        {
            _topY.AsSpan(mbx * 16, 16).CopyTo(top);
            if (mbx < _mbw - 1)
            {
                _topY.AsSpan((mbx + 1) * 16, 4).CopyTo(top[16..]);
            }
            else
            {
                top.Slice(16, 4).Fill(_topY[(mbx * 16) + 15]);
            }
        }

        byte corner = mbx == 0 ? (byte)(mby > 0 ? 129 : 127) : mby == 0 ? (byte)127 : _corner[0];
        FillContext(_ctxY, 16, top, mby > 0, _leftY, mbx > 0, corner);
        if (mby > 0)
        {
            _topU.AsSpan(mbx * 8, 8).CopyTo(top);
        }

        corner = mbx == 0 ? (byte)(mby > 0 ? 129 : 127) : mby == 0 ? (byte)127 : _corner[1];
        FillContext(_ctxU, 8, top, mby > 0, _leftU, mbx > 0, corner);
        if (mby > 0)
        {
            _topV.AsSpan(mbx * 8, 8).CopyTo(top);
        }

        corner = mbx == 0 ? (byte)(mby > 0 ? 129 : 127) : mby == 0 ? (byte)127 : _corner[2];
        FillContext(_ctxV, 8, top, mby > 0, _leftV, mbx > 0, corner);
    }

    /// <summary>Lays out one plane's neighbours as the decoder does: 127 above the frame, 129 left of it.</summary>
    private static void FillContext(Span<byte> ctx, int size, ReadOnlySpan<byte> top, bool hasTop, ReadOnlySpan<byte> left, bool hasLeft, byte corner)
    {
        int extra = size == 16 ? 4 : 0;
        Span<byte> row = ctx.Slice(C0 - S, size + extra);
        if (hasTop)
        {
            top[..(size + extra)].CopyTo(row);
        }
        else
        {
            row.Fill(127);
        }

        ctx[C0 - S - 1] = corner;
        for (int j = 0; j < size; j++)
        {
            ctx[C0 + (j * S) - 1] = hasLeft ? left[j] : (byte)129;
        }
    }

    private void MakePredictions(int mbx, int mby)
    {
        for (int mode = 0; mode < 4; mode++)
        {
            Vp8Dsp.BlockMode edge = Vp8Decoder.EdgeMode(mode, mbx, mby);
            Vp8Dsp.PredictBlock(edge, _ctxY, C0, 16);
            Vp8EncDsp.Copy(_ctxY, C0, _predY16[mode], 0, 16, 16);
            Vp8Dsp.PredictBlock(edge, _ctxU, C0, 8);
            Vp8EncDsp.Copy(_ctxU, C0, _predUv[mode], UOff, 8, 8);
            Vp8Dsp.PredictBlock(edge, _ctxV, C0, 8);
            Vp8EncDsp.Copy(_ctxV, C0, _predUv[mode], VOff, 8, 8);
        }
    }

    /// <summary>Keeps the macroblock's modes and reconstruction for its neighbours.</summary>
    private void SaveBoundary(int mbx, int mby, int mb)
    {
        byte[] o = _yuvOut;

        // The row above's last samples are the next macroblock's corner, before this row's samples replace them.
        _corner[0] = _topY[(mbx * 16) + 15];
        _corner[1] = _topU[(mbx * 8) + 7];
        _corner[2] = _topV[(mbx * 8) + 7];
        if (Reconstruction is { } recon)
        {
            for (int j = 0; j < 16; j++)
            {
                o.AsSpan(j * S, 16).CopyTo(recon.Y.AsSpan((((mby * 16) + j) * recon.Stride) + (mbx * 16), 16));
            }

            for (int j = 0; j < 8; j++)
            {
                o.AsSpan((j * S) + UOff, 8).CopyTo(recon.U.AsSpan((((mby * 8) + j) * recon.ChromaStride) + (mbx * 8), 8));
                o.AsSpan((j * S) + VOff, 8).CopyTo(recon.V.AsSpan((((mby * 8) + j) * recon.ChromaStride) + (mbx * 8), 8));
            }
        }

        if (mby < _mbh - 1)
        {
            o.AsSpan(15 * S, 16).CopyTo(_topY.AsSpan(mbx * 16, 16));
            o.AsSpan((7 * S) + UOff, 8).CopyTo(_topU.AsSpan(mbx * 8, 8));
            o.AsSpan((7 * S) + VOff, 8).CopyTo(_topV.AsSpan(mbx * 8, 8));
        }

        for (int j = 0; j < 16; j++)
        {
            _leftY[j] = o[(j * S) + 15];
        }

        for (int j = 0; j < 8; j++)
        {
            _leftU[j] = o[(j * S) + UOff + 7];
            _leftV[j] = o[(j * S) + VOff + 7];
        }

        Span<byte> above = _modesAbove.AsSpan(mbx * 4, 4);
        if (_isI16[mb])
        {
            byte implied = ImpliedSubMode(_yMode[mb]);
            above.Fill(implied);
            _modesLeft.AsSpan().Fill(implied);
        }
        else
        {
            ReadOnlySpan<byte> modes = _modes.AsSpan(mb * 16, 16);
            modes.Slice(12, 4).CopyTo(above);
            for (int y = 0; y < 4; y++)
            {
                _modesLeft[y] = modes[(y * 4) + 3];
            }
        }
    }

    // --- Mode choice -------------------------------------------------------------------------------------------------------

    private void Decimate(int mbx, int mby, int mb, bool errorDiffusion)
    {
        ModeScore rd = _rd;
        rd.Init();
        Segment dqm = _dqm[_segment[mb]];
        _useDerr = errorDiffusion;
        if (Effort >= 4)
        {
            _doTrellis = Effort >= 6;
            PickBestIntra16(dqm, rd);
            bool i4 = _maxI4HeaderBits > 0 && PickBestIntra4(mbx, mb, dqm, rd);
            _isI16[mb] = !i4;
            PickBestUv(mbx, dqm, rd);
            if (Effort == 5)
            {
                _doTrellis = true;
                QuantizeLumaAgain(mbx, mb, dqm, rd);
            }
        }
        else
        {
            RefineUsingDistortion(mbx, mby, mb, dqm, rd, tryBoth: Effort >= 2);
        }

        if (_isI16[mb])
        {
            _yMode[mb] = (byte)rd.ModeI16;
        }
        else
        {
            rd.ModesI4.CopyTo(_modes.AsSpan(mb * 16, 16));
        }

        _uvMode[mb] = (byte)rd.ModeUv;
        _zero[mb] = rd.Nz == 0;
    }

    private static void SetScore(int lambda, ModeScore rd) => rd.Score = ((rd.R + rd.H) * lambda) + (RdDistoMult * (rd.D + rd.SD));

    private void PickBestIntra16(Segment dqm, ModeScore rd)
    {
        int lambda = dqm.LambdaI16;
        int tlambda = dqm.TLambda;
        bool isFlat = Vp8EncDsp.IsFlatSource16(_yuvIn);
        Span<int> sourceSpectrum = stackalloc int[16];
        if (tlambda != 0)
        {
            Vp8EncDsp.HadamardWeighted16x16(_yuvIn, 0, sourceSpectrum);
        }

        ModeScore cur = _rdTmp;
        bool first = true;
        foreach (int mode in BlockModeOrder)
        {
            cur.Init();
            cur.ModeI16 = mode;
            int coded = 0;
            if (!_doTrellis && Vp8TransformSimd.IsSupported)
            {
                cur.Nz = ReconstructIntra16Measured(dqm, cur, _yuvOut2, mode, sourceSpectrum, out long sse, out int disto, out coded);
                cur.D = sse;
                cur.SD = tlambda != 0 ? Mult8(tlambda, disto) : 0;
            }
            else
            {
                cur.Nz = ReconstructIntra16(dqm, cur, _yuvOut2, mode);
                cur.D = Vp8EncDsp.Sse(_yuvIn, _yuvOut2, 0, 16, 16);
                cur.SD = tlambda != 0 ? Mult8(tlambda, Vp8EncDsp.Disto16x16(sourceSpectrum, _yuvOut2, 0)) : 0;
            }

            cur.H = Vp8Costs.Intra16[mode];
            if (isFlat)
            {
                isFlat = Vp8EncDsp.IsFlat(cur.YAc, 16, 0);
                if (isFlat)
                {
                    cur.D *= 2;
                    cur.SD *= 2;
                }
            }

            // Each coded AC level costs at least its sign bit: a mode this bound already rules out needs no rate.
            if (!first && ((cur.H + (Vp8Costs.LevelFixed[1] * (long)coded)) * lambda) + (RdDistoMult * (cur.D + cur.SD)) >= rd.Score)
            {
                continue;
            }

            cur.R = CostLuma16(cur);
            SetScore(lambda, cur);
            if (first || cur.Score < rd.Score)
            {
                rd.CopyLuma16From(cur);
                (_yuvOut, _yuvOut2) = (_yuvOut2, _yuvOut);
                first = false;
            }
        }

        SetScore(dqm.LambdaMode, rd);
        if ((rd.Nz & 0x100FFFF) == 0x1000000 && rd.D > dqm.MinDisto)
        {
            int v = Math.Max(Math.Abs((int)rd.YDc[1]), Math.Max(Math.Abs((int)rd.YDc[2]), Math.Abs((int)rd.YDc[4])));
            dqm.MaxEdge = Math.Max(dqm.MaxEdge, v);
        }
    }

    private static long Mult8(int a, int b) => ((long)a * b + 128) >> 8;

    /// <summary>Tries 4 × 4 modes block by block; returns whether they beat the 16 × 16 choice already in <paramref name="rd"/>.</summary>
    private bool PickBestIntra4(int mbx, int mb, Segment dqm, ModeScore rd)
    {
        int lambda = dqm.LambdaI4;
        int tlambda = dqm.TLambda;
        ModeScore best = _rdI4;
        best.Init();
        best.H = Vp8Costs.Intra4Flag;
        SetScore(dqm.LambdaMode, best);
        StartI4();
        int headerBits = 0;
        Span<byte> modes = best.ModesI4;
        bool fused = !_doTrellis && Vp8TransformSimd.IsSupported;
        bool paired = fused && Vp8PairSimd.IsSupported;
        Span<int> trialMask = stackalloc int[10];
        Span<int> trialSse = stackalloc int[10];
        Span<int> trialSpectrum = stackalloc int[10];
        for (int n = 0; n < 16; n++)
        {
            int x = n & 3, y = n >> 2;
            int above = y == 0 ? _modesAbove[(mbx * 4) + x] : modes[n - 4];
            int left = x == 0 ? _modesLeft[y] : modes[n - 1];
            int costAt = (above * 100) + (left * 10);
            int src = ScanY[n];
            MakeI4Predictions(n);
            int sourceSpectrum = tlambda != 0 ? Vp8EncDsp.HadamardWeighted(_yuvIn, src) : 0;
            long bestScore = long.MaxValue;
            long bestD = 0, bestSd = 0, bestR = 0, bestH = 0;
            int bestMode = -1;
            bool bestNz = false;
            if (paired)
            {
                // All ten trials first, two at a time; the winner's results are copied once it is known.
                for (int k = 0; k < SubModeOrder.Length; k += 2)
                {
                    int mask = Vp8PairSimd.ReconstructTrials(_yuvIn, src, _predI4, I4PredAt(SubModeOrder[k]), I4PredAt(SubModeOrder[k + 1]), dqm.Y1, _trialLevels.AsSpan(k * 16, 32), _trialRecon, TrialAt(k), out trialSse[k], out trialSse[k + 1], out trialSpectrum[k], out trialSpectrum[k + 1]);
                    trialMask[k] = mask & 0xFFFF;
                    trialMask[k + 1] = mask >>> 16;
                }
            }

            int bestK = 0;
            for (int k = 0; k < SubModeOrder.Length; k++)
            {
                int mode = SubModeOrder[k];
                bool nz, flat;
                long d, sd;
                int coded = 0;
                Span<short> levels = _tmpLevels;
                if (paired)
                {
                    int mask = trialMask[k];
                    coded = BitOperations.PopCount((uint)mask);
                    nz = mask != 0;
                    flat = BitOperations.PopCount((uint)mask & ~1u) <= 3;
                    d = trialSse[k];
                    sd = tlambda != 0 ? Mult8(tlambda, Math.Abs(trialSpectrum[k] - sourceSpectrum) >> 5) : 0;
                    levels = _trialLevels.AsSpan(k * 16, 16);
                }
                else if (fused)
                {
                    int mask = Vp8TransformSimd.ReconstructBlock(_yuvIn, src, _predI4, I4PredAt(mode), dqm.Y1, _tmpLevels, _i4Tmp, 0, out int sse, out int spectrum);
                    coded = BitOperations.PopCount((uint)mask);
                    nz = mask != 0;
                    flat = BitOperations.PopCount((uint)mask & ~1u) <= 3;
                    d = sse;
                    sd = tlambda != 0 ? Mult8(tlambda, Math.Abs(spectrum - sourceSpectrum) >> 5) : 0;
                }
                else
                {
                    nz = ReconstructIntra4(dqm, n, mode, _tmpLevels, _i4Tmp, 0);
                    flat = Vp8EncDsp.IsFlat(_tmpLevels, 1, 3);
                    d = Vp8EncDsp.Sse4x4(_yuvIn, src, _i4Tmp, 0);
                    sd = tlambda != 0 ? Mult8(tlambda, Math.Abs(Vp8EncDsp.HadamardWeighted(_i4Tmp, 0) - sourceSpectrum) >> 5) : 0;
                }

                long h = Vp8Costs.Intra4[costAt + mode];
                long r = mode != Vp8Dsp.BDc && flat ? FlatnessPenalty : 0;
                long score = ((r + h) * lambda) + (RdDistoMult * (d + sd));

                // Each coded level costs at least its sign bit, so a mode this bound already rules out needs no residual cost.
                if (bestMode >= 0 && score + (Vp8Costs.LevelFixed[1] * (long)coded * lambda) >= bestScore)
                {
                    continue;
                }

                r += _costs.Residual(3, 0, _nzTop[x] + _nzLeft[y], levels);
                score = ((r + h) * lambda) + (RdDistoMult * (d + sd));
                if (bestMode < 0 || score < bestScore)
                {
                    bestScore = score;
                    (bestD, bestSd, bestR, bestH) = (d, sd, r, h);
                    bestMode = mode;
                    bestNz = nz;
                    bestK = k;
                    if (!paired)
                    {
                        (_i4Tmp, _i4Best) = (_i4Best, _i4Tmp);
                        levels.CopyTo(best.YAc.AsSpan(n * 16, 16));
                    }
                }
            }

            if (paired)
            {
                _trialLevels.AsSpan(bestK * 16, 16).CopyTo(best.YAc.AsSpan(n * 16, 16));
                Vp8EncDsp.Copy(_trialRecon, TrialAt(bestK) + ((bestK & 1) * 4), _i4Best, 0, 4, 4);
            }

            best.D += bestD;
            best.SD += bestSd;
            best.R += bestR;
            best.H += bestH;
            best.Score += ((bestR + bestH) * dqm.LambdaMode) + (RdDistoMult * (bestD + bestSd));
            if (bestNz)
            {
                best.Nz |= 1 << n;
            }

            if (best.Score >= rd.Score)
            {
                return false;
            }

            headerBits += (int)bestH;
            if (headerBits > _maxI4HeaderBits)
            {
                return false;
            }

            modes[n] = (byte)bestMode;
            CommitI4Block(n, _i4Best);
            _nzTop[x] = _nzLeft[y] = (byte)(bestNz ? 1 : 0);
        }

        rd.D = best.D;
        rd.SD = best.SD;
        rd.R = best.R;
        rd.H = best.H;
        rd.Nz = best.Nz;
        rd.Score = best.Score;
        best.ModesI4.CopyTo(rd.ModesI4, 0);
        best.YAc.CopyTo(rd.YAc, 0);
        (_yuvOut, _yuvOut2) = (_yuvOut2, _yuvOut);
        return true;
    }

    private void PickBestUv(int mbx, Segment dqm, ModeScore rd)
    {
        int lambda = dqm.LambdaUv;
        ModeScore cur = _rdTmp;
        long bestScore = long.MaxValue;
        long bestD = 0, bestR = 0, bestH = 0;
        int bestNz = 0;
        byte[] dst = _yuvOut, scratch = _yuvOut2;
        foreach (int mode in BlockModeOrder)
        {
            cur.Init();
            int nz = ReconstructUv(mbx, dqm, cur, scratch, mode, out long d, out int coded);
            if (d < 0)
            {
                d = Vp8EncDsp.Sse(_yuvIn, scratch, UOff, 16, 8);
            }

            long h = Vp8Costs.Chroma[mode];
            long r = mode != 0 && Vp8EncDsp.IsFlat(cur.UvLevels, 8, 2) ? FlatnessPenalty * 8 : 0;

            // Each coded level costs at least its sign bit: a mode this bound already rules out needs no rate.
            if (mode != 0 && ((r + h + (Vp8Costs.LevelFixed[1] * (long)coded)) * lambda) + (RdDistoMult * d) >= bestScore)
            {
                continue;
            }

            r += CostUv(cur);
            long score = ((r + h) * lambda) + (RdDistoMult * d);
            if (mode == 0 || score < bestScore)
            {
                bestScore = score;
                (bestD, bestR, bestH, bestNz) = (d, r, h, nz);
                rd.ModeUv = mode;
                cur.UvLevels.CopyTo(rd.UvLevels, 0);
                cur.Derr.CopyTo(rd.Derr, 0);
                (dst, scratch) = (scratch, dst);
            }
        }

        if (dst != _yuvOut)
        {
            Vp8EncDsp.Copy(dst, UOff, _yuvOut, UOff, 16, 8);
        }

        rd.D += bestD;
        rd.R += bestR;
        rd.H += bestH;
        rd.Nz |= bestNz;
        rd.Score += bestScore;
        if (_useDerr)
        {
            StoreDiffusionErrors(mbx, rd);
        }
    }

    /// <summary>At effort 5, quantizes the chosen luma modes again with the trellis.</summary>
    private void QuantizeLumaAgain(int mbx, int mb, Segment dqm, ModeScore rd)
    {
        _topNz.AsSpan(mbx * 9, 9).CopyTo(_nzTop);
        _leftNz.CopyTo(_nzLeft);
        int nz = rd.Nz & 0xFF0000;
        if (_isI16[mb])
        {
            nz |= ReconstructIntra16(dqm, rd, _yuvOut, rd.ModeI16);
        }
        else
        {
            StartI4();
            for (int n = 0; n < 16; n++)
            {
                MakeI4Predictions(n);
                bool any = ReconstructIntra4(dqm, n, rd.ModesI4[n], rd.YAc.AsSpan(n * 16, 16), _i4Tmp, 0);
                CommitI4Block(n, _i4Tmp);
                _nzTop[n & 3] = _nzLeft[n >> 2] = (byte)(any ? 1 : 0);
                if (any)
                {
                    nz |= 1 << n;
                }
            }

            (_yuvOut, _yuvOut2) = (_yuvOut2, _yuvOut);
            Vp8EncDsp.Copy(_yuvOut2, UOff, _yuvOut, UOff, 16, 8);
        }

        rd.Nz = nz;
    }

    /// <summary>Chooses modes by prediction error and fixed mode costs, without coding them (efforts 0 to 3).</summary>
    private void RefineUsingDistortion(int mbx, int mby, int mb, Segment dqm, ModeScore rd, bool tryBoth)
    {
        const int LambdaI16 = 106, LambdaI4 = 11, LambdaUv = 120;
        long bestScore = long.MaxValue;
        long bitLimit = tryBoth ? _mbHeaderLimit : long.MaxValue;
        int bestMode = 0;
        foreach (int mode in BlockModeOrder)
        {
            long score = ((long)Vp8EncDsp.Sse(_yuvIn, _predY16[mode], 0, 16, 16) * RdDistoMult) + (Vp8Costs.Intra16[mode] * LambdaI16);
            if (mode != 0 && Vp8Costs.Intra16[mode] > bitLimit)
            {
                continue;
            }

            if (score < bestScore)
            {
                bestMode = mode;
                bestScore = score;
            }
        }

        if ((mbx == 0 || mby == 0) && Vp8EncDsp.IsFlatSource16(_yuvIn))
        {
            // Avoids starting a checkerboard resonance from the border.
            bestMode = mbx == 0 ? 0 : 1;
            tryBoth = false;
        }

        rd.ModeI16 = bestMode;
        bool isI16 = true;
        int nz = 0;
        if (tryBoth && _maxI4HeaderBits > 0)
        {
            isI16 = false;
            long scoreI4 = dqm.I4Penalty;
            long bitSum = 0;
            StartI4();
            for (int n = 0; n < 16; n++)
            {
                int x = n & 3, y = n >> 2;
                int above = y == 0 ? _modesAbove[(mbx * 4) + x] : rd.ModesI4[n - 4];
                int left = x == 0 ? _modesLeft[y] : rd.ModesI4[n - 1];
                int costAt = (above * 100) + (left * 10);
                int src = ScanY[n];
                MakeI4Predictions(n);
                int best = -1;
                long bestI4 = long.MaxValue;
                foreach (int mode in SubModeOrder)
                {
                    long score = ((long)Vp8EncDsp.Sse4x4(_yuvIn, src, _predI4, I4PredAt(mode)) * RdDistoMult) + (Vp8Costs.Intra4[costAt + mode] * LambdaI4);
                    if (score < bestI4)
                    {
                        best = mode;
                        bestI4 = score;
                    }
                }

                bitSum += Vp8Costs.Intra4[costAt + best];
                rd.ModesI4[n] = (byte)best;
                scoreI4 += bestI4;
                if (scoreI4 >= bestScore || bitSum > bitLimit)
                {
                    isI16 = true;
                    break;
                }

                if (ReconstructIntra4(dqm, n, best, rd.YAc.AsSpan(n * 16, 16), _i4Tmp, 0))
                {
                    nz |= 1 << n;
                }

                CommitI4Block(n, _i4Tmp);
            }

            if (!isI16)
            {
                (_yuvOut, _yuvOut2) = (_yuvOut2, _yuvOut);
                bestScore = scoreI4;
            }
        }

        _isI16[mb] = isI16;
        if (isI16)
        {
            nz = ReconstructIntra16(dqm, rd, _yuvOut, rd.ModeI16);
        }

        int bestUv = 0;
        long bestUvScore = long.MaxValue;
        foreach (int mode in BlockModeOrder)
        {
            long score = ((long)Vp8EncDsp.Sse(_yuvIn, _predUv[mode], UOff, 16, 8) * RdDistoMult) + (Vp8Costs.Chroma[mode] * LambdaUv);
            if (score < bestUvScore)
            {
                bestUv = mode;
                bestUvScore = score;
            }
        }

        rd.ModeUv = bestUv;
        nz |= ReconstructUv(mbx, dqm, rd, _yuvOut, bestUv, out _, out _);
        rd.Nz = nz;
        rd.Score = bestScore;
    }

    // --- Reconstruction ----------------------------------------------------------------------------------------------------

    /// <summary>Predicts, transforms, quantizes and reconstructs the luma in <paramref name="mode"/>; returns the non-zero bits.</summary>
    private int ReconstructIntra16(Segment dqm, ModeScore rd, byte[] output, int mode)
    {
        byte[] pred = _predY16[mode];
        Span<short> tmp = stackalloc short[256];
        Span<short> dc = stackalloc short[16];
        for (int n = 0; n < 16; n++)
        {
            Vp8EncDsp.ForwardTransform(_yuvIn, ScanY[n], pred, ScanY[n], tmp.Slice(n * 16, 16));
        }

        Vp8EncDsp.ForwardWalshHadamard(tmp, dc);
        int nz = Vp8EncDsp.Quantize(dc, rd.YDc, dqm.Y2) ? 1 << 24 : 0;
        if (_doTrellis)
        {
            Span<byte> t = stackalloc byte[4];
            Span<byte> l = stackalloc byte[4];
            _nzTop.AsSpan(0, 4).CopyTo(t);
            _nzLeft.AsSpan(0, 4).CopyTo(l);
            for (int n = 0; n < 16; n++)
            {
                int x = n & 3, y = n >> 2;
                bool any = TrellisQuantize(tmp.Slice(n * 16, 16), rd.YAc.AsSpan(n * 16, 16), t[x] + l[y], 0, dqm.Y1, dqm.LambdaTrellisI16);
                t[x] = l[y] = (byte)(any ? 1 : 0);
                if (any)
                {
                    nz |= 1 << n;
                }
            }
        }
        else
        {
            for (int n = 0; n < 16; n++)
            {
                tmp[n * 16] = 0;
                if (Vp8EncDsp.Quantize(tmp.Slice(n * 16, 16), rd.YAc.AsSpan(n * 16, 16), dqm.Y1, 1))
                {
                    nz |= 1 << n;
                }
            }
        }

        Vp8Dsp.InverseWalshHadamard(dc, tmp);
        Vp8EncDsp.Copy(pred, 0, output, 0, 16, 16);
        for (int n = 0; n < 16; n++)
        {
            Vp8Dsp.InverseTransformAdd(tmp.Slice(n * 16, 16), output, ScanY[n]);
        }

        return nz;
    }

    /// <summary>
    /// <see cref="ReconstructIntra16"/> without the trellis, block by block in registers; also returns the squared error of the
    /// reconstruction and its spectral distortion against the source's magnitudes <paramref name="sourceSpectrum"/>.
    /// </summary>
    private int ReconstructIntra16Measured(Segment dqm, ModeScore rd, byte[] output, int mode, ReadOnlySpan<int> sourceSpectrum, out long sse, out int disto, out int coded)
    {
        coded = 0;
        byte[] pred = _predY16[mode];
        Span<short> tmp = stackalloc short[256];
        Span<short> dc = stackalloc short[16];
        bool paired = Vp8PairSimd.IsSupported;
        for (int n = 0; n < 16; n += paired ? 2 : 1)
        {
            if (paired)
            {
                Vp8PairSimd.ForwardTransform(_yuvIn, ScanY[n], pred, ScanY[n], tmp.Slice(n * 16, 32));
            }
            else
            {
                Vp8EncDsp.ForwardTransform(_yuvIn, ScanY[n], pred, ScanY[n], tmp.Slice(n * 16, 16));
            }
        }

        Vp8EncDsp.ForwardWalshHadamard(tmp, dc);
        int nz = Vp8EncDsp.Quantize(dc, rd.YDc, dqm.Y2) ? 1 << 24 : 0;

        // Each block's coefficient 0 becomes its reconstructed DC.
        Vp8Dsp.InverseWalshHadamard(dc, tmp);
        sse = 0;
        disto = 0;
        if (paired)
        {
            // Blocks n and n + 1 side by side.
            for (int n = 0; n < 16; n += 2)
            {
                int at = ScanY[n];
                int mask = Vp8PairSimd.ReconstructCoefficients(tmp.Slice(n * 16, 32), true, _yuvIn, at, pred, at, dqm.Y1, rd.YAc.AsSpan(n * 16, 32), output, at, out int sseA, out int sseB, out int spectrumA, out int spectrumB);
                nz |= ((mask & 0xFFFF) != 0 ? 1 << n : 0) | ((mask >>> 16) != 0 ? 2 << n : 0);
                coded += BitOperations.PopCount((uint)mask);
                sse += sseA + sseB;
                disto += (Math.Abs(spectrumA - sourceSpectrum[n]) >> 5) + (Math.Abs(spectrumB - sourceSpectrum[n + 1]) >> 5);
            }

            return nz;
        }

        for (int n = 0; n < 16; n++)
        {
            int at = ScanY[n];
            int mask = Vp8TransformSimd.ReconstructAcBlock(tmp.Slice(n * 16, 16), _yuvIn, at, pred, at, dqm.Y1, rd.YAc.AsSpan(n * 16, 16), output, at, out int blockSse, out int spectrum);
            coded += BitOperations.PopCount((uint)mask);
            if (mask != 0)
            {
                nz |= 1 << n;
            }

            sse += blockSse;
            disto += Math.Abs(spectrum - sourceSpectrum[n]) >> 5;
        }

        return nz;
    }

    /// <summary>Reconstructs 4 × 4 block <paramref name="n"/> in <paramref name="mode"/> into <paramref name="output"/> at <paramref name="at"/>.</summary>
    private bool ReconstructIntra4(Segment dqm, int n, int mode, Span<short> levels, byte[] output, int at)
    {
        Span<short> tmp = stackalloc short[16];
        int p = I4PredAt(mode);
        Vp8EncDsp.ForwardTransform(_yuvIn, ScanY[n], _predI4, p, tmp);
        bool nz = _doTrellis
            ? TrellisQuantize(tmp, levels, _nzTop[n & 3] + _nzLeft[n >> 2], 3, dqm.Y1, dqm.LambdaTrellisI4)
            : Vp8EncDsp.Quantize(tmp, levels, dqm.Y1);
        Vp8EncDsp.Copy(_predI4, p, output, at, 4, 4);
        Vp8Dsp.InverseTransformAdd(tmp, output, at);
        return nz;
    }

    /// <summary>Reconstructs the chroma in <paramref name="mode"/>; returns the non-zero bits, and the squared error and the count of non-zero levels when they come for free (else −1 and 0).</summary>
    private int ReconstructUv(int mbx, Segment dqm, ModeScore rd, byte[] output, int mode, out long sse, out int coded)
    {
        coded = 0;
        byte[] pred = _predUv[mode];
        Span<short> tmp = stackalloc short[128];
        bool paired = Vp8PairSimd.IsSupported;
        for (int n = 0; n < 8; n += paired ? 2 : 1)
        {
            if (paired)
            {
                Vp8PairSimd.ForwardTransform(_yuvIn, ScanUv[n], pred, ScanUv[n], tmp.Slice(n * 16, 32));
            }
            else
            {
                Vp8EncDsp.ForwardTransform(_yuvIn, ScanUv[n], pred, ScanUv[n], tmp.Slice(n * 16, 16));
            }
        }

        if (_useDerr)
        {
            CorrectDcValues(mbx, dqm.Uv, tmp, rd);
        }

        int nz = 0;
        if (paired)
        {
            // Blocks n and n + 1 side by side.
            sse = 0;
            for (int n = 0; n < 8; n += 2)
            {
                int at = ScanUv[n];
                int mask = Vp8PairSimd.ReconstructCoefficients(tmp.Slice(n * 16, 32), false, _yuvIn, at, pred, at, dqm.Uv, rd.UvLevels.AsSpan(n * 16, 32), output, at, out int sseA, out int sseB, out _, out _);
                nz |= ((mask & 0xFFFF) != 0 ? 1 << n : 0) | ((mask >>> 16) != 0 ? 2 << n : 0);
                coded += BitOperations.PopCount((uint)mask);
                sse += sseA + sseB;
            }

            return nz << 16;
        }

        sse = -1;
        for (int n = 0; n < 8; n++)
        {
            if (Vp8EncDsp.Quantize(tmp.Slice(n * 16, 16), rd.UvLevels.AsSpan(n * 16, 16), dqm.Uv))
            {
                nz |= 1 << n;
            }
        }

        Vp8EncDsp.Copy(pred, UOff, output, UOff, 16, 8);
        for (int n = 0; n < 8; n++)
        {
            Vp8Dsp.InverseTransformAdd(tmp.Slice(n * 16, 16), output, ScanUv[n]);
        }

        return nz << 16;
    }

    private bool _useDerr;

    /// <summary>
    /// Diffuses each chroma block's DC quantization error into its right and lower neighbours (7/16 down, 8/16 right), which keeps
    /// flat colored areas from turning into blocks of slightly different colors.
    /// </summary>
    private void CorrectDcValues(int mbx, Vp8Matrix m, Span<short> tmp, ModeScore rd)
    {
        for (int ch = 0; ch <= 1; ch++)
        {
            int topAt = (mbx * 4) + (ch * 2);
            int top0 = _topDerr[topAt], top1 = _topDerr[topAt + 1];
            int left0 = _leftDerr[ch * 2], left1 = _leftDerr[(ch * 2) + 1];
            Span<short> c = tmp.Slice(ch * 64, 64);
            c[0] += (short)(((7 * top0) + (8 * left0)) >> 3);
            int err0 = QuantizeSingle(ref c[0], m);
            c[16] += (short)(((7 * top1) + (8 * err0)) >> 3);
            int err1 = QuantizeSingle(ref c[16], m);
            c[32] += (short)(((7 * err0) + (8 * left1)) >> 3);
            int err2 = QuantizeSingle(ref c[32], m);
            c[48] += (short)(((7 * err1) + (8 * err2)) >> 3);
            int err3 = QuantizeSingle(ref c[48], m);
            rd.Derr[(ch * 3) + 0] = (sbyte)err1;
            rd.Derr[(ch * 3) + 1] = (sbyte)err2;
            rd.Derr[(ch * 3) + 2] = (sbyte)err3;
        }
    }

    private static int QuantizeSingle(ref short v, Vp8Matrix m)
    {
        int value = v;
        int sign = value >> 31;
        value = (value ^ sign) - sign;
        if (value > m.ZThresh[0])
        {
            int qv = (int)(((uint)value * m.IQ[0] + m.Bias[0]) >> Vp8EncDsp.QFix) * m.Q[0];
            int err = value - qv;
            v = (short)((qv ^ sign) - sign);
            return ((err ^ sign) - sign) >> 1;
        }

        v = 0;
        return ((value ^ sign) - sign) >> 1;
    }

    private void StoreDiffusionErrors(int mbx, ModeScore rd)
    {
        for (int ch = 0; ch <= 1; ch++)
        {
            int topAt = (mbx * 4) + (ch * 2);
            int err1 = rd.Derr[ch * 3], err2 = rd.Derr[(ch * 3) + 1], err3 = rd.Derr[(ch * 3) + 2];
            _leftDerr[ch * 2] = (sbyte)err1;
            _leftDerr[(ch * 2) + 1] = (sbyte)((3 * err3) >> 2);
            _topDerr[topAt] = (sbyte)err2;
            _topDerr[topAt + 1] = (sbyte)(err3 - _leftDerr[(ch * 2) + 1]);
        }
    }

    // --- 4 × 4 work area ---------------------------------------------------------------------------------------------------

    /// <summary>Where the pair of trials from <paramref name="k"/> (even) goes in <see cref="_trialRecon"/>: four pairs a block row.</summary>
    private static int TrialAt(int k) => ((k >> 1 & 3) * 8) + ((k >> 3) * 4 * S);

    private static int I4PredAt(int mode) => ((mode & 7) * 4) + ((mode >> 3) * 4 * S);

    /// <summary>Lays out the macroblock's neighbours for 4 × 4 prediction, with the above-right samples repeated down the right edge.</summary>
    private void StartI4()
    {
        _ctxY.CopyTo(_i4Work, 0);
        Span<byte> topRight = _i4Work.AsSpan(C0 - S + 16, 4);
        for (int k = 1; k <= 3; k++)
        {
            topRight.CopyTo(_i4Work.AsSpan(C0 + (((k * 4) - 1) * S) + 16, 4));
        }
    }

    private void MakeI4Predictions(int n)
    {
        Vp8EncDsp.PredictSubblocks(_i4Work, C0 + ((n >> 2) * 4 * S) + ((n & 3) * 4), _predI4);
    }

    /// <summary>Puts block <paramref name="n"/>'s chosen reconstruction into the work area (for the next blocks) and into <see cref="_yuvOut2"/>.</summary>
    private void CommitI4Block(int n, byte[] block)
    {
        Vp8EncDsp.Copy(block, 0, _i4Work, C0 + ((n >> 2) * 4 * S) + ((n & 3) * 4), 4, 4);
        Vp8EncDsp.Copy(block, 0, _yuvOut2, ScanY[n], 4, 4);
    }

    // --- Rate estimates ----------------------------------------------------------------------------------------------------

    private long CostLuma16(ModeScore rd)
    {
        long r = _costs.Residual(1, 0, _nzTop[8] + _nzLeft[8], rd.YDc);
        Span<byte> t = stackalloc byte[4];
        Span<byte> l = stackalloc byte[4];
        _nzTop.AsSpan(0, 4).CopyTo(t);
        _nzLeft.AsSpan(0, 4).CopyTo(l);
        for (int n = 0; n < 16; n++)
        {
            int x = n & 3, y = n >> 2;
            ReadOnlySpan<short> levels = rd.YAc.AsSpan(n * 16, 16);
            r += _costs.Residual(0, 1, t[x] + l[y], levels);
            t[x] = l[y] = (byte)(levels[1..].ContainsAnyExcept((short)0) ? 1 : 0);
        }

        return r;
    }

    private long CostUv(ModeScore rd)
    {
        long r = 0;
        Span<byte> t = stackalloc byte[4];
        Span<byte> l = stackalloc byte[4];
        _nzTop.AsSpan(4, 4).CopyTo(t);
        _nzLeft.AsSpan(4, 4).CopyTo(l);
        for (int ch = 0; ch <= 2; ch += 2)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    ReadOnlySpan<short> levels = rd.UvLevels.AsSpan(((ch * 2) + (y * 2) + x) * 16, 16);
                    r += _costs.Residual(2, 0, t[ch + x] + l[ch + y], levels);
                    t[ch + x] = l[ch + y] = (byte)(levels.ContainsAnyExcept((short)0) ? 1 : 0);
                }
            }
        }

        return r;
    }

    // --- Trellis -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Quantizes a block by dynamic programming over levels: each coefficient may take its rounded-down level or one more, and
    /// the path with the lowest rate × lambda + weighted distortion wins, including where to end the block.
    /// </summary>
    private bool TrellisQuantize(Span<short> coefficients, Span<short> levels, int ctx0, int type, Vp8Matrix m, int lambda)
    {
        const int MaxDelta = 1, Nodes = MaxDelta + 1;
        int first = type == 0 ? 1 : 0;
        ReadOnlySpan<byte> zigzag = Vp8Decoder.Zigzag;
        byte[] probs = _costs.Probabilities;
        ushort[] table = _costs.Table;
        Span<sbyte> nodePrev = stackalloc sbyte[16 * Nodes];
        Span<short> nodeLevel = stackalloc short[16 * Nodes];
        Span<bool> nodeSign = stackalloc bool[16 * Nodes];
        Span<long> scoreCur = stackalloc long[Nodes];
        Span<long> scorePrev = stackalloc long[Nodes];
        Span<int> costCur = stackalloc int[Nodes];
        Span<int> costPrev = stackalloc int[Nodes];

        int thresh = m.Q[1] * m.Q[1] / 4;
        int lastProba = probs[((type * 8) + Vp8Decoder.Bands[first]) * 33 + (ctx0 * 11)];
        int last = first - 1;
        for (int n = 15; n >= first; n--)
        {
            int c = coefficients[zigzag[n]];
            if (c * c > thresh)
            {
                last = n;
                break;
            }
        }

        if (last < 15)
        {
            last++;
        }

        long bestScore = (long)Vp8Costs.Bit(0, lastProba) * lambda;
        int bestEnd = -1, bestNode = -1, bestPrev = -1;
        for (int k = 0; k < Nodes; k++)
        {
            scoreCur[k] = ctx0 == 0 ? (long)Vp8Costs.Bit(1, lastProba) * lambda : 0;
            costCur[k] = Vp8LevelCosts.At(type, first, ctx0);
        }

        for (int n = first; n <= last; n++)
        {
            int j = zigzag[n];
            int q = m.Q[j == 0 ? 0 : 1];
            uint iq = m.IQ[j == 0 ? 0 : 1];
            int c0 = coefficients[j];
            bool sign = c0 < 0;
            int coeff0 = Math.Abs(c0) + m.Sharpen[j];
            int level0 = Math.Min((int)(((uint)coeff0 * iq) >> Vp8EncDsp.QFix), Vp8Costs.MaxLevel);
            int threshLevel = Math.Min((int)(((uint)coeff0 * iq + (0x80u << (Vp8EncDsp.QFix - 8))) >> Vp8EncDsp.QFix), Vp8Costs.MaxLevel);
            scoreCur.CopyTo(scorePrev);
            costCur.CopyTo(costPrev);
            for (int k = 0; k < Nodes; k++)
            {
                int level = level0 + k;
                int ctx = level > 2 ? 2 : level;
                costCur[k] = n < 15 ? Vp8LevelCosts.At(type, n + 1, ctx) : 0;
                if (level > threshLevel)
                {
                    scoreCur[k] = long.MaxValue;
                    continue;
                }

                int newError = coeff0 - (level * q);
                long deltaError = TrellisWeights[j] * (((long)newError * newError) - ((long)coeff0 * coeff0));
                long baseScore = RdDistoMult * deltaError;

                long bestCur = long.MaxValue;
                int bestP = 0;
                for (int p = 0; p < Nodes; p++)
                {
                    if (scorePrev[p] == long.MaxValue)
                    {
                        continue;
                    }

                    long score = scorePrev[p] + ((long)Vp8Costs.Level(table, costPrev[p], level) * lambda);
                    if (score < bestCur)
                    {
                        bestCur = score;
                        bestP = p;
                    }
                }

                if (bestCur == long.MaxValue)
                {
                    scoreCur[k] = long.MaxValue;
                    continue;
                }

                bestCur += baseScore;
                int at = (n * Nodes) + k;
                nodeSign[at] = sign;
                nodeLevel[at] = (short)level;
                nodePrev[at] = (sbyte)bestP;
                scoreCur[k] = bestCur;
                if (level != 0 && bestCur < bestScore)
                {
                    long endCost = n < 15 ? Vp8Costs.Bit(0, probs[((type * 8) + Vp8Decoder.Bands[n + 1]) * 33 + (ctx * 11)]) : 0;
                    long score = bestCur + (endCost * lambda);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestEnd = n;
                        bestNode = k;
                        bestPrev = bestP;
                    }
                }
            }
        }

        if (type == 0)
        {
            coefficients[1..].Clear();
            levels[1..].Clear();
        }
        else
        {
            coefficients.Clear();
            levels.Clear();
        }

        if (bestEnd < 0)
        {
            return false;
        }

        bool nz = false;
        int node = bestNode;
        nodePrev[(bestEnd * Nodes) + bestNode] = (sbyte)bestPrev;
        for (int n = bestEnd; n >= first; n--)
        {
            int at = (n * Nodes) + node;
            int level = nodeLevel[at];
            int j = zigzag[n];
            levels[n] = (short)(nodeSign[at] ? -level : level);
            nz |= level != 0;
            coefficients[j] = (short)(levels[n] * m.Q[j == 0 ? 0 : 1]);
            node = nodePrev[at];
        }

        return nz;
    }

    /// <summary>A macroblock's choice and what it costs: distortion, spectral distortion, residual and header rate, levels.</summary>
    private sealed class ModeScore
    {
        internal long D { get; set; }

        internal long SD { get; set; }

        internal long R { get; set; }

        internal long H { get; set; }

        internal long Score { get; set; }

        internal int Nz { get; set; }

        internal int ModeI16 { get; set; }

        internal int ModeUv { get; set; }

        internal byte[] ModesI4 { get; } = new byte[16];

        internal short[] YDc { get; } = new short[16];

        internal short[] YAc { get; } = new short[256];

        internal short[] UvLevels { get; } = new short[128];

        internal sbyte[] Derr { get; } = new sbyte[6];

        internal void Init()
        {
            D = SD = R = H = 0;
            Nz = 0;
            Score = long.MaxValue;
        }

        internal void CopyLuma16From(ModeScore other)
        {
            D = other.D;
            SD = other.SD;
            R = other.R;
            H = other.H;
            Nz = other.Nz;
            Score = other.Score;
            ModeI16 = other.ModeI16;
            other.YDc.CopyTo(YDc, 0);
            other.YAc.CopyTo(YAc, 0);
        }
    }
}
