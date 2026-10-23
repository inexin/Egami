// Translated from libopus 1.5.2 silk/stereo_LR_to_MS.c, stereo_find_predictor.c, stereo_quant_pred.c and inner_prod_aligned.c.
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's stereo coding: left/right to mid/side with a predictor of side from mid, and the split of the bitrate.</summary>
internal static class SilkStereoEncoder
{
    /// <summary>
    /// silk_stereo_LR_to_MS. <paramref name="x1"/> and <paramref name="x2"/> are the two channels' input buffers, whose
    /// samples start at index 2; the mid signal replaces x1 from index 0 and the side residual is written to x2 from index 1.
    /// </summary>
    public static void LrToMs(SilkStereoEncState state, Span<short> x1, Span<short> x2, Span<sbyte> ix, out sbyte midOnlyFlag, Span<int> midSideRatesBps,
        int totalRateBps, int prevSpeechActQ8, int toMono, int fsKhz, int frameLength)
    {
        Span<short> mid = x1;
        Span<short> side = stackalloc short[SilkChannelDecoder.MaxFrameLength + 2];
        for (int n = 0; n < frameLength + 2; n++)
        {
            int sum = x1[n] + x2[n];
            int diff = x1[n] - x2[n];
            mid[n] = (short)RShiftRound(sum, 1);
            side[n] = (short)Sat16(RShiftRound(diff, 1));
        }

        state.SMid.CopyTo(mid);
        state.SSide.CopyTo(side);
        mid.Slice(frameLength, 2).CopyTo(state.SMid);
        side.Slice(frameLength, 2).CopyTo(state.SSide);

        Span<short> lpMid = stackalloc short[SilkChannelDecoder.MaxFrameLength];
        Span<short> hpMid = stackalloc short[SilkChannelDecoder.MaxFrameLength];
        for (int n = 0; n < frameLength; n++)
        {
            int sum = RShiftRound(AddLShift32(mid[n] + mid[n + 2], mid[n + 1], 1), 2);
            lpMid[n] = (short)sum;
            hpMid[n] = (short)(mid[n + 1] - sum);
        }

        Span<short> lpSide = stackalloc short[SilkChannelDecoder.MaxFrameLength];
        Span<short> hpSide = stackalloc short[SilkChannelDecoder.MaxFrameLength];
        for (int n = 0; n < frameLength; n++)
        {
            int sum = RShiftRound(AddLShift32(side[n] + side[n + 2], side[n + 1], 1), 2);
            lpSide[n] = (short)sum;
            hpSide[n] = (short)(side[n + 1] - sum);
        }

        bool is10MsFrame = frameLength == 10 * fsKhz;
        int smoothCoefQ16 = is10MsFrame
            ? SilkEncoderQuant.FixConst(StereoRatioSmoothCoef / 2, 16)
            : SilkEncoderQuant.FixConst(StereoRatioSmoothCoef, 16);
        smoothCoefQ16 = Smulwb(Smulbb(prevSpeechActQ8, prevSpeechActQ8), smoothCoefQ16);

        Span<int> predQ13 = stackalloc int[2];
        predQ13[0] = FindPredictor(out int lpRatioQ14, lpMid, lpSide, state.MidSideAmpQ0.AsSpan(0, 2), frameLength, smoothCoefQ16);
        predQ13[1] = FindPredictor(out int hpRatioQ14, hpMid, hpSide, state.MidSideAmpQ0.AsSpan(2, 2), frameLength, smoothCoefQ16);
        int fracQ16 = Smlabb(hpRatioQ14, lpRatioQ14, 3);
        fracQ16 = Math.Min(fracQ16, 1 << 16);

        totalRateBps -= is10MsFrame ? 1200 : 600;
        if (totalRateBps < 1)
        {
            totalRateBps = 1;
        }

        int minMidRateBps = Smlabb(2000, fsKhz, 600);
        int frac3Q16 = Mul(3, fracQ16);
        int widthQ14;
        midSideRatesBps[0] = Div32VarQ(totalRateBps, (13 << 16) + frac3Q16, 16 + 3);
        if (midSideRatesBps[0] < minMidRateBps)
        {
            midSideRatesBps[0] = minMidRateBps;
            midSideRatesBps[1] = totalRateBps - midSideRatesBps[0];
            widthQ14 = Div32VarQ(LShift(midSideRatesBps[1], 1) - minMidRateBps, Smulwb((1 << 16) + frac3Q16, minMidRateBps), 14 + 2);
            widthQ14 = Limit(widthQ14, 0, 1 << 14);
        }
        else
        {
            midSideRatesBps[1] = totalRateBps - midSideRatesBps[0];
            widthQ14 = 1 << 14;
        }

        state.SmthWidthQ14 = (short)Smlawb(state.SmthWidthQ14, widthQ14 - state.SmthWidthQ14, smoothCoefQ16);

        midOnlyFlag = 0;
        if (toMono != 0)
        {
            widthQ14 = 0;
            predQ13[0] = 0;
            predQ13[1] = 0;
            QuantPred(predQ13, ix);
        }
        else if (state.WidthPrevQ14 == 0 &&
            (8 * totalRateBps < 13 * minMidRateBps || Smulwb(fracQ16, state.SmthWidthQ14) < SilkEncoderQuant.FixConst(0.05, 14)))
        {
            predQ13[0] = Smulbb(state.SmthWidthQ14, predQ13[0]) >> 14;
            predQ13[1] = Smulbb(state.SmthWidthQ14, predQ13[1]) >> 14;
            QuantPred(predQ13, ix);
            widthQ14 = 0;
            predQ13[0] = 0;
            predQ13[1] = 0;
            midSideRatesBps[0] = totalRateBps;
            midSideRatesBps[1] = 0;
            midOnlyFlag = 1;
        }
        else if (state.WidthPrevQ14 != 0 &&
            (8 * totalRateBps < 11 * minMidRateBps || Smulwb(fracQ16, state.SmthWidthQ14) < SilkEncoderQuant.FixConst(0.02, 14)))
        {
            predQ13[0] = Smulbb(state.SmthWidthQ14, predQ13[0]) >> 14;
            predQ13[1] = Smulbb(state.SmthWidthQ14, predQ13[1]) >> 14;
            QuantPred(predQ13, ix);
            widthQ14 = 0;
            predQ13[0] = 0;
            predQ13[1] = 0;
        }
        else if (state.SmthWidthQ14 > SilkEncoderQuant.FixConst(0.95, 14))
        {
            QuantPred(predQ13, ix);
            widthQ14 = 1 << 14;
        }
        else
        {
            predQ13[0] = Smulbb(state.SmthWidthQ14, predQ13[0]) >> 14;
            predQ13[1] = Smulbb(state.SmthWidthQ14, predQ13[1]) >> 14;
            QuantPred(predQ13, ix);
            widthQ14 = state.SmthWidthQ14;
        }

        if (midOnlyFlag == 1)
        {
            state.SilentSideLen += (short)(frameLength - (StereoInterpLenMs * fsKhz));
            if (state.SilentSideLen < LaShapeMs * fsKhz)
            {
                midOnlyFlag = 0;
            }
            else
            {
                state.SilentSideLen = 10000;
            }
        }
        else
        {
            state.SilentSideLen = 0;
        }

        if (midOnlyFlag == 0 && midSideRatesBps[1] < 1)
        {
            midSideRatesBps[1] = 1;
            midSideRatesBps[0] = Math.Max(1, totalRateBps - midSideRatesBps[1]);
        }

        int pred0Q13 = -state.PredPrevQ13[0];
        int pred1Q13 = -state.PredPrevQ13[1];
        int wQ24 = LShift(state.WidthPrevQ14, 10);
        int denomQ16 = (1 << 16) / (StereoInterpLenMs * fsKhz);
        int delta0Q13 = -RShiftRound(Smulbb(predQ13[0] - state.PredPrevQ13[0], denomQ16), 16);
        int delta1Q13 = -RShiftRound(Smulbb(predQ13[1] - state.PredPrevQ13[1], denomQ16), 16);
        int deltawQ24 = LShift(Smulwb(widthQ14 - state.WidthPrevQ14, denomQ16), 10);
        for (int n = 0; n < StereoInterpLenMs * fsKhz; n++)
        {
            pred0Q13 += delta0Q13;
            pred1Q13 += delta1Q13;
            wQ24 += deltawQ24;
            int sum = LShift(AddLShift32(mid[n] + mid[n + 2], mid[n + 1], 1), 9);
            sum = Smlawb(Smulwb(wQ24, side[n + 1]), sum, pred0Q13);
            sum = Smlawb(sum, LShift(mid[n + 1], 11), pred1Q13);
            x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }

        pred0Q13 = -predQ13[0];
        pred1Q13 = -predQ13[1];
        wQ24 = LShift(widthQ14, 10);
        for (int n = StereoInterpLenMs * fsKhz; n < frameLength; n++)
        {
            int sum = LShift(AddLShift32(mid[n] + mid[n + 2], mid[n + 1], 1), 9);
            sum = Smlawb(Smulwb(wQ24, side[n + 1]), sum, pred0Q13);
            sum = Smlawb(sum, LShift(mid[n + 1], 11), pred1Q13);
            x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }

        state.PredPrevQ13[0] = (short)predQ13[0];
        state.PredPrevQ13[1] = (short)predQ13[1];
        state.WidthPrevQ14 = (short)widthQ14;
    }

    // silk_stereo_find_predictor: the least-squares predictor of y from x, and the smoothed residual amplitudes.
    private static int FindPredictor(out int ratioQ14, ReadOnlySpan<short> x, ReadOnlySpan<short> y, Span<int> midResAmpQ0, int length, int smoothCoefQ16)
    {
        SilkSigProc.SumSqrShift(out int nrgx, out int scale1, x, length);
        SilkSigProc.SumSqrShift(out int nrgy, out int scale2, y, length);
        int scale = Math.Max(scale1, scale2);
        scale += scale & 1;
        nrgy >>= scale - scale2;
        nrgx >>= scale - scale1;
        nrgx = Math.Max(nrgx, 1);
        int corr = InnerProdAlignedScale(x, y, scale, length);
        int predQ13 = Div32VarQ(corr, nrgx, 13);
        predQ13 = Limit(predQ13, -(1 << 14), 1 << 14);
        int pred2Q10 = Smulwb(predQ13, predQ13);
        smoothCoefQ16 = Math.Max(smoothCoefQ16, Abs(pred2Q10));
        scale >>= 1;
        midResAmpQ0[0] = Smlawb(midResAmpQ0[0], LShift(SqrtApprox(nrgx), scale) - midResAmpQ0[0], smoothCoefQ16);
        nrgy -= LShift(Smulwb(corr, predQ13), 3 + 1);
        nrgy += LShift(Smulwb(nrgx, pred2Q10), 6);
        midResAmpQ0[1] = Smlawb(midResAmpQ0[1], LShift(SqrtApprox(nrgy), scale) - midResAmpQ0[1], smoothCoefQ16);
        ratioQ14 = Div32VarQ(midResAmpQ0[1], Math.Max(midResAmpQ0[0], 1), 14);
        ratioQ14 = Limit(ratioQ14, 0, 32767);
        return predQ13;
    }

    private static int InnerProdAlignedScale(ReadOnlySpan<short> inVec1, ReadOnlySpan<short> inVec2, int scale, int len)
    {
        int sum = 0;
        for (int i = 0; i < len; i++)
        {
            sum += Smulbb(inVec1[i], inVec2[i]) >> scale;
        }

        return sum;
    }

    // silk_stereo_quant_pred: quantizes the two predictors to the indices ix[ 2 ][ 3 ] and leaves the quantized values in predQ13.
    private static void QuantPred(Span<int> predQ13, Span<sbyte> ix)
    {
        int quantPredQ13 = 0;
        ReadOnlySpan<short> table = SilkTables.StereoPredQuantQ13;
        int halfStep = SilkEncoderQuant.FixConst(0.5 / StereoQuantSubSteps, 16);
        for (int n = 0; n < 2; n++)
        {
            int errMinQ13 = int.MaxValue;
            for (int i = 0; i < StereoQuantTabSize - 1; i++)
            {
                int lowQ13 = table[i];
                int stepQ13 = Smulwb(table[i + 1] - lowQ13, halfStep);
                bool done = false;
                for (int j = 0; j < StereoQuantSubSteps; j++)
                {
                    int lvlQ13 = Smlabb(lowQ13, stepQ13, (2 * j) + 1);
                    int errQ13 = Abs(predQ13[n] - lvlQ13);
                    if (errQ13 < errMinQ13)
                    {
                        errMinQ13 = errQ13;
                        quantPredQ13 = lvlQ13;
                        ix[(n * 3) + 0] = (sbyte)i;
                        ix[(n * 3) + 1] = (sbyte)j;
                    }
                    else
                    {
                        done = true;
                        break;
                    }
                }

                if (done)
                {
                    break;
                }
            }

            ix[(n * 3) + 2] = (sbyte)(ix[(n * 3) + 0] / 3);
            ix[(n * 3) + 0] -= (sbyte)(ix[(n * 3) + 2] * 3);
            predQ13[n] = quantPredQ13;
        }

        predQ13[0] -= predQ13[1];
    }
}
