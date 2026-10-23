// Translated from libopus 1.5.2 silk/NSQ.c, NSQ.h and NSQ_del_dec.c.
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's noise shaping quantizer: the excitation pulses of one frame, plain or with delayed decision.</summary>
internal static class SilkNsq
{
    private const int LtpOrder = SilkChannelDecoder.LtpOrder;
    private const int MaxLpcOrder = SilkChannelDecoder.MaxLpcOrder;

    // NSQ_del_dec_struct as a flat int array: the replace step copies a state from an int offset onwards, like the C.
    private const int DdSLpc = 0;
    private const int DdRandState = DdSLpc + SilkChannelDecoder.MaxSubFrameLength + NsqLpcBufLength;
    private const int DdQ = DdRandState + DecisionDelay;
    private const int DdXq = DdQ + DecisionDelay;
    private const int DdPred = DdXq + DecisionDelay;
    private const int DdShape = DdPred + DecisionDelay;
    private const int DdSAr2 = DdShape + DecisionDelay;
    private const int DdLfAr = DdSAr2 + MaxShapeLpcOrder;
    private const int DdDiff = DdLfAr + 1;
    private const int DdSeed = DdDiff + 1;
    private const int DdSeedInit = DdSeed + 1;
    private const int DdRd = DdSeedInit + 1;
    private const int DdSize = DdRd + 1;

    // NSQ_sample_struct as 7 ints.
    private const int SsQ = 0;
    private const int SsRd = 1;
    private const int SsXq = 2;
    private const int SsLfAr = 3;
    private const int SsDiff = 4;
    private const int SsSLtpShp = 5;
    private const int SsLpcExc = 6;
    private const int SsSize = 7;

    /// <summary>The frame parameters the quantizer reads from the encoder state.</summary>
    public readonly record struct Frame(int LtpMemLength, int FrameLength, int SubfrLength, int NbSubfr, int PredictLpcOrder, int ShapingLpcOrder,
        int WarpingQ16, int NStatesDelayedDecision);

    /// <summary>silk_NSQ.</summary>
    public static void Quantize(in Frame f, SilkNsqState nsq, SilkSideInfoIndices indices, ReadOnlySpan<short> x16, Span<sbyte> pulses,
        ReadOnlySpan<short> predCoefQ12, ReadOnlySpan<short> ltpCoefQ14, ReadOnlySpan<short> arQ13, ReadOnlySpan<int> harmShapeGainQ14,
        ReadOnlySpan<int> tiltQ14, ReadOnlySpan<int> lfShpQ14, ReadOnlySpan<int> gainsQ16, ReadOnlySpan<int> pitchL, int lambdaQ10, int ltpScaleQ14)
    {
        nsq.RandSeed = indices.Seed;
        int lag = nsq.LagPrev;
        int offsetQ10 = SilkTables.QuantizationOffsetsQ10[((indices.SignalType >> 1) * 2) + indices.QuantOffsetType];
        int lsfInterpolationFlag = indices.NlsfInterpCoefQ2 == 4 ? 0 : 1;
        Span<int> sLtpQ15 = stackalloc int[f.LtpMemLength + f.FrameLength];
        Span<short> sLtp = stackalloc short[f.LtpMemLength + f.FrameLength];
        Span<int> xScQ10 = stackalloc int[f.SubfrLength];
        nsq.SLtpShpBufIdx = f.LtpMemLength;
        nsq.SLtpBufIdx = f.LtpMemLength;
        int pxq = f.LtpMemLength;
        for (int k = 0; k < f.NbSubfr; k++)
        {
            ReadOnlySpan<short> aQ12 = predCoefQ12.Slice(((k >> 1) | (1 - lsfInterpolationFlag)) * MaxLpcOrder, MaxLpcOrder);
            ReadOnlySpan<short> bQ14 = ltpCoefQ14.Slice(k * LtpOrder, LtpOrder);
            ReadOnlySpan<short> arShpQ13 = arQ13.Slice(k * MaxShapeLpcOrder, MaxShapeLpcOrder);
            int harmShapeFirPackedQ14 = harmShapeGainQ14[k] >> 2;
            harmShapeFirPackedQ14 |= (harmShapeGainQ14[k] >> 1) << 16;
            nsq.RewhiteFlag = 0;
            if (indices.SignalType == SilkChannelDecoder.TypeVoiced)
            {
                lag = pitchL[k];
                if ((k & (3 - (lsfInterpolationFlag << 1))) == 0)
                {
                    int startIdx = f.LtpMemLength - lag - f.PredictLpcOrder - (LtpOrder / 2);
                    SilkSigProc.LpcAnalysisFilter(sLtp[startIdx..], nsq.Xq.AsSpan(startIdx + (k * f.SubfrLength)), aQ12, f.LtpMemLength - startIdx, f.PredictLpcOrder);
                    nsq.RewhiteFlag = 1;
                    nsq.SLtpBufIdx = f.LtpMemLength;
                }
            }

            ScaleStates(f, nsq, x16.Slice(k * f.SubfrLength), xScQ10, sLtp, sLtpQ15, k, ltpScaleQ14, gainsQ16, pitchL, indices.SignalType);
            NoiseShapeQuantizer(nsq, indices.SignalType, xScQ10, pulses.Slice(k * f.SubfrLength), nsq.Xq.AsSpan(pxq), sLtpQ15, aQ12, bQ14, arShpQ13, lag,
                harmShapeFirPackedQ14, tiltQ14[k], lfShpQ14[k], gainsQ16[k], lambdaQ10, offsetQ10, f.SubfrLength, f.ShapingLpcOrder, f.PredictLpcOrder);
            pxq += f.SubfrLength;
        }

        nsq.LagPrev = pitchL[f.NbSubfr - 1];
        nsq.Xq.AsSpan(f.FrameLength, f.LtpMemLength).CopyTo(nsq.Xq);
        nsq.SLtpShpQ14.AsSpan(f.FrameLength, f.LtpMemLength).CopyTo(nsq.SLtpShpQ14);
    }

    private static int ShortPrediction(ReadOnlySpan<int> buf32, int p, ReadOnlySpan<short> coef16, int order)
    {
        int output = order >> 1;
        for (int j = 0; j < order; j++)
        {
            output = Smlawb(output, buf32[p - j], coef16[j]);
        }

        return output;
    }

    private static int NoiseShapeFeedbackLoop(int data0, Span<int> data1, ReadOnlySpan<short> coef, int order)
    {
        int tmp2 = data0;
        int tmp1 = data1[0];
        data1[0] = tmp2;
        int output = order >> 1;
        output = Smlawb(output, tmp2, coef[0]);
        for (int j = 2; j < order; j += 2)
        {
            tmp2 = data1[j - 1];
            data1[j - 1] = tmp1;
            output = Smlawb(output, tmp1, coef[j - 1]);
            tmp1 = data1[j];
            data1[j] = tmp2;
            output = Smlawb(output, tmp2, coef[j]);
        }

        data1[order - 1] = tmp1;
        output = Smlawb(output, tmp1, coef[order - 1]);
        return LShift(output, 1);
    }

    private static void NoiseShapeQuantizer(SilkNsqState nsq, int signalType, ReadOnlySpan<int> xScQ10, Span<sbyte> pulses, Span<short> xq, Span<int> sLtpQ15,
        ReadOnlySpan<short> aQ12, ReadOnlySpan<short> bQ14, ReadOnlySpan<short> arShpQ13, int lag, int harmShapeFirPackedQ14, int tiltQ14, int lfShpQ14,
        int gainQ16, int lambdaQ10, int offsetQ10, int length, int shapingLpcOrder, int predictLpcOrder)
    {
        unchecked
        {
            int[] sLtpShp = nsq.SLtpShpQ14;
            int[] sLpc = nsq.SLpcQ14;
            int shpLag = nsq.SLtpShpBufIdx - lag + (HarmShapeFirTaps / 2);
            int predLag = nsq.SLtpBufIdx - lag + (LtpOrder / 2);
            int gainQ10 = gainQ16 >> 6;
            int psLpc = NsqLpcBufLength - 1;
            for (int i = 0; i < length; i++)
            {
                nsq.RandSeed = Rand(nsq.RandSeed);
                int lpcPredQ10 = ShortPrediction(sLpc, psLpc, aQ12, predictLpcOrder);
                int ltpPredQ13;
                if (signalType == SilkChannelDecoder.TypeVoiced)
                {
                    ltpPredQ13 = 2;
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag], bQ14[0]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 1], bQ14[1]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 2], bQ14[2]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 3], bQ14[3]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 4], bQ14[4]);
                    predLag++;
                }
                else
                {
                    ltpPredQ13 = 0;
                }

                int nArQ12 = NoiseShapeFeedbackLoop(nsq.SDiffShpQ14, nsq.SAr2Q14, arShpQ13, shapingLpcOrder);
                nArQ12 = Smlawb(nArQ12, nsq.SLfArShpQ14, tiltQ14);
                int nLfQ12 = Smulwb(sLtpShp[nsq.SLtpShpBufIdx - 1], lfShpQ14);
                nLfQ12 = Smlawt(nLfQ12, nsq.SLfArShpQ14, lfShpQ14);
                int tmp1 = SubOvflw(LShift(lpcPredQ10, 2), nArQ12);
                tmp1 = SubOvflw(tmp1, nLfQ12);
                if (lag > 0)
                {
                    int nLtpQ13 = Smulwb(AddSat32(sLtpShp[shpLag], sLtpShp[shpLag - 2]), harmShapeFirPackedQ14);
                    nLtpQ13 = Smlawt(nLtpQ13, sLtpShp[shpLag - 1], harmShapeFirPackedQ14);
                    nLtpQ13 = LShift(nLtpQ13, 1);
                    shpLag++;
                    int tmp2 = ltpPredQ13 - nLtpQ13;
                    tmp1 = AddOvflw(tmp2, LShift(tmp1, 1));
                    tmp1 = RShiftRound(tmp1, 3);
                }
                else
                {
                    tmp1 = RShiftRound(tmp1, 2);
                }

                int rQ10 = xScQ10[i] - tmp1;
                if (nsq.RandSeed < 0)
                {
                    rQ10 = -rQ10;
                }

                rQ10 = Limit(rQ10, -(31 << 10), 30 << 10);
                Quantize(rQ10, offsetQ10, lambdaQ10, out int q1Q10, out int q2Q10, out int rd1Q20, out int rd2Q20);
                int rrQ10 = rQ10 - q1Q10;
                rd1Q20 = Smlabb(rd1Q20, rrQ10, rrQ10);
                rrQ10 = rQ10 - q2Q10;
                rd2Q20 = Smlabb(rd2Q20, rrQ10, rrQ10);
                if (rd2Q20 < rd1Q20)
                {
                    q1Q10 = q2Q10;
                }

                pulses[i] = (sbyte)RShiftRound(q1Q10, 10);
                int excQ14 = LShift(q1Q10, 4);
                if (nsq.RandSeed < 0)
                {
                    excQ14 = -excQ14;
                }

                int lpcExcQ14 = AddLShift32(excQ14, ltpPredQ13, 1);
                int xqQ14 = AddOvflw(lpcExcQ14, LShift(lpcPredQ10, 4));
                xq[i] = (short)Sat16(RShiftRound(Smulww(xqQ14, gainQ10), 8));
                psLpc++;
                sLpc[psLpc] = xqQ14;
                nsq.SDiffShpQ14 = xqQ14 - LShift(xScQ10[i], 4);
                int sLfArShpQ14 = SubOvflw(nsq.SDiffShpQ14, LShift(nArQ12, 2));
                nsq.SLfArShpQ14 = sLfArShpQ14;
                sLtpShp[nsq.SLtpShpBufIdx] = SubOvflw(sLfArShpQ14, LShift(nLfQ12, 2));
                sLtpQ15[nsq.SLtpBufIdx] = LShift(lpcExcQ14, 1);
                nsq.SLtpShpBufIdx++;
                nsq.SLtpBufIdx++;
                nsq.RandSeed = AddOvflw(nsq.RandSeed, pulses[i]);
            }

            sLpc.AsSpan(length, NsqLpcBufLength).CopyTo(sLpc);
        }
    }

    /// <summary>The two quantization level candidates and their rate terms.</summary>
    private static void Quantize(int rQ10, int offsetQ10, int lambdaQ10, out int q1Q10, out int q2Q10, out int rd1, out int rd2)
    {
        q1Q10 = rQ10 - offsetQ10;
        int q1Q0 = q1Q10 >> 10;
        if (lambdaQ10 > 2048)
        {
            int rdoOffset = (lambdaQ10 / 2) - 512;
            if (q1Q10 > rdoOffset)
            {
                q1Q0 = (q1Q10 - rdoOffset) >> 10;
            }
            else if (q1Q10 < -rdoOffset)
            {
                q1Q0 = (q1Q10 + rdoOffset) >> 10;
            }
            else if (q1Q10 < 0)
            {
                q1Q0 = -1;
            }
            else
            {
                q1Q0 = 0;
            }
        }

        if (q1Q0 > 0)
        {
            q1Q10 = (q1Q0 << 10) - QuantLevelAdjustQ10;
            q1Q10 += offsetQ10;
            q2Q10 = q1Q10 + 1024;
            rd1 = Smulbb(q1Q10, lambdaQ10);
            rd2 = Smulbb(q2Q10, lambdaQ10);
        }
        else if (q1Q0 == 0)
        {
            q1Q10 = offsetQ10;
            q2Q10 = q1Q10 + (1024 - QuantLevelAdjustQ10);
            rd1 = Smulbb(q1Q10, lambdaQ10);
            rd2 = Smulbb(q2Q10, lambdaQ10);
        }
        else if (q1Q0 == -1)
        {
            q2Q10 = offsetQ10;
            q1Q10 = q2Q10 - (1024 - QuantLevelAdjustQ10);
            rd1 = Smulbb(-q1Q10, lambdaQ10);
            rd2 = Smulbb(q2Q10, lambdaQ10);
        }
        else
        {
            q1Q10 = (q1Q0 << 10) + QuantLevelAdjustQ10;
            q1Q10 += offsetQ10;
            q2Q10 = q1Q10 + 1024;
            rd1 = Smulbb(-q1Q10, lambdaQ10);
            rd2 = Smulbb(-q2Q10, lambdaQ10);
        }
    }

    private static void ScaleStates(in Frame f, SilkNsqState nsq, ReadOnlySpan<short> x16, Span<int> xScQ10, ReadOnlySpan<short> sLtp, Span<int> sLtpQ15,
        int subfr, int ltpScaleQ14, ReadOnlySpan<int> gainsQ16, ReadOnlySpan<int> pitchL, int signalType)
    {
        int lag = pitchL[subfr];
        int invGainQ31 = Inverse32VarQ(Math.Max(gainsQ16[subfr], 1), 47);
        int invGainQ26 = RShiftRound(invGainQ31, 5);
        for (int i = 0; i < f.SubfrLength; i++)
        {
            xScQ10[i] = Smulww(x16[i], invGainQ26);
        }

        if (nsq.RewhiteFlag != 0)
        {
            if (subfr == 0)
            {
                invGainQ31 = LShift(Smulwb(invGainQ31, ltpScaleQ14), 2);
            }

            for (int i = nsq.SLtpBufIdx - lag - (LtpOrder / 2); i < nsq.SLtpBufIdx; i++)
            {
                sLtpQ15[i] = Smulwb(invGainQ31, sLtp[i]);
            }
        }

        if (gainsQ16[subfr] != nsq.PrevGainQ16)
        {
            int gainAdjQ16 = Div32VarQ(nsq.PrevGainQ16, gainsQ16[subfr], 16);
            for (int i = nsq.SLtpShpBufIdx - f.LtpMemLength; i < nsq.SLtpShpBufIdx; i++)
            {
                nsq.SLtpShpQ14[i] = Smulww(gainAdjQ16, nsq.SLtpShpQ14[i]);
            }

            if (signalType == SilkChannelDecoder.TypeVoiced && nsq.RewhiteFlag == 0)
            {
                for (int i = nsq.SLtpBufIdx - lag - (LtpOrder / 2); i < nsq.SLtpBufIdx; i++)
                {
                    sLtpQ15[i] = Smulww(gainAdjQ16, sLtpQ15[i]);
                }
            }

            nsq.SLfArShpQ14 = Smulww(gainAdjQ16, nsq.SLfArShpQ14);
            nsq.SDiffShpQ14 = Smulww(gainAdjQ16, nsq.SDiffShpQ14);
            for (int i = 0; i < NsqLpcBufLength; i++)
            {
                nsq.SLpcQ14[i] = Smulww(gainAdjQ16, nsq.SLpcQ14[i]);
            }

            for (int i = 0; i < MaxShapeLpcOrder; i++)
            {
                nsq.SAr2Q14[i] = Smulww(gainAdjQ16, nsq.SAr2Q14[i]);
            }

            nsq.PrevGainQ16 = gainsQ16[subfr];
        }
    }

    /// <summary>silk_NSQ_del_dec: the noise shaping quantizer with delayed decision.</summary>
    public static void QuantizeDelDec(in Frame f, SilkNsqState nsq, SilkSideInfoIndices indices, ReadOnlySpan<short> x16, Span<sbyte> pulses,
        ReadOnlySpan<short> predCoefQ12, ReadOnlySpan<short> ltpCoefQ14, ReadOnlySpan<short> arQ13, ReadOnlySpan<int> harmShapeGainQ14,
        ReadOnlySpan<int> tiltQ14, ReadOnlySpan<int> lfShpQ14, ReadOnlySpan<int> gainsQ16, ReadOnlySpan<int> pitchL, int lambdaQ10, int ltpScaleQ14)
    {
        int nStates = f.NStatesDelayedDecision;
        int lag = nsq.LagPrev;
        Span<int> delDec = stackalloc int[nStates * DdSize];
        delDec.Clear();
        for (int k = 0; k < nStates; k++)
        {
            Span<int> dd = delDec.Slice(k * DdSize, DdSize);
            dd[DdSeed] = (k + indices.Seed) & 3;
            dd[DdSeedInit] = dd[DdSeed];
            dd[DdRd] = 0;
            dd[DdLfAr] = nsq.SLfArShpQ14;
            dd[DdDiff] = nsq.SDiffShpQ14;
            dd[DdShape] = nsq.SLtpShpQ14[f.LtpMemLength - 1];
            nsq.SLpcQ14.AsSpan(0, NsqLpcBufLength).CopyTo(dd[DdSLpc..]);
            nsq.SAr2Q14.CopyTo(dd[DdSAr2..]);
        }

        int offsetQ10 = SilkTables.QuantizationOffsetsQ10[((indices.SignalType >> 1) * 2) + indices.QuantOffsetType];
        int smplBufIdx = 0;
        int decisionDelay = Math.Min(DecisionDelay, f.SubfrLength);
        if (indices.SignalType == SilkChannelDecoder.TypeVoiced)
        {
            for (int k = 0; k < f.NbSubfr; k++)
            {
                decisionDelay = Math.Min(decisionDelay, pitchL[k] - (LtpOrder / 2) - 1);
            }
        }
        else if (lag > 0)
        {
            decisionDelay = Math.Min(decisionDelay, lag - (LtpOrder / 2) - 1);
        }

        int lsfInterpolationFlag = indices.NlsfInterpCoefQ2 == 4 ? 0 : 1;
        Span<int> sLtpQ15 = stackalloc int[f.LtpMemLength + f.FrameLength];
        Span<short> sLtp = stackalloc short[f.LtpMemLength + f.FrameLength];
        Span<int> xScQ10 = stackalloc int[f.SubfrLength];
        Span<int> delayedGainQ10 = stackalloc int[DecisionDelay];
        Span<int> sampleState = stackalloc int[nStates * 2 * SsSize];
        int pxq = f.LtpMemLength;
        nsq.SLtpShpBufIdx = f.LtpMemLength;
        nsq.SLtpBufIdx = f.LtpMemLength;
        int subfr = 0;
        int rdMinQ10, winnerInd, lastSmpleIdx;
        for (int k = 0; k < f.NbSubfr; k++)
        {
            ReadOnlySpan<short> aQ12 = predCoefQ12.Slice(((k >> 1) | (1 - lsfInterpolationFlag)) * MaxLpcOrder, MaxLpcOrder);
            ReadOnlySpan<short> bQ14 = ltpCoefQ14.Slice(k * LtpOrder, LtpOrder);
            ReadOnlySpan<short> arShpQ13 = arQ13.Slice(k * MaxShapeLpcOrder, MaxShapeLpcOrder);
            int harmShapeFirPackedQ14 = harmShapeGainQ14[k] >> 2;
            harmShapeFirPackedQ14 |= (harmShapeGainQ14[k] >> 1) << 16;
            nsq.RewhiteFlag = 0;
            if (indices.SignalType == SilkChannelDecoder.TypeVoiced)
            {
                lag = pitchL[k];
                if ((k & (3 - (lsfInterpolationFlag << 1))) == 0)
                {
                    if (k == 2)
                    {
                        // Reset the delayed decisions: find the winner and copy the final part of its signals.
                        rdMinQ10 = delDec[DdRd];
                        winnerInd = 0;
                        for (int i = 1; i < nStates; i++)
                        {
                            if (delDec[(i * DdSize) + DdRd] < rdMinQ10)
                            {
                                rdMinQ10 = delDec[(i * DdSize) + DdRd];
                                winnerInd = i;
                            }
                        }

                        for (int i = 0; i < nStates; i++)
                        {
                            if (i != winnerInd)
                            {
                                delDec[(i * DdSize) + DdRd] = unchecked(delDec[(i * DdSize) + DdRd] + (int.MaxValue >> 4));
                            }
                        }

                        Span<int> winner = delDec.Slice(winnerInd * DdSize, DdSize);
                        lastSmpleIdx = smplBufIdx + decisionDelay;
                        int pulsesBase = k * f.SubfrLength;
                        for (int i = 0; i < decisionDelay; i++)
                        {
                            lastSmpleIdx = (lastSmpleIdx - 1) % DecisionDelay;
                            if (lastSmpleIdx < 0)
                            {
                                lastSmpleIdx += DecisionDelay;
                            }

                            pulses[pulsesBase + i - decisionDelay] = (sbyte)RShiftRound(winner[DdQ + lastSmpleIdx], 10);
                            nsq.Xq[pxq + i - decisionDelay] = (short)Sat16(RShiftRound(Smulww(winner[DdXq + lastSmpleIdx], gainsQ16[1]), 14));
                            nsq.SLtpShpQ14[nsq.SLtpShpBufIdx - decisionDelay + i] = winner[DdShape + lastSmpleIdx];
                        }

                        subfr = 0;
                    }

                    int startIdx = f.LtpMemLength - lag - f.PredictLpcOrder - (LtpOrder / 2);
                    SilkSigProc.LpcAnalysisFilter(sLtp[startIdx..], nsq.Xq.AsSpan(startIdx + (k * f.SubfrLength)), aQ12, f.LtpMemLength - startIdx, f.PredictLpcOrder);
                    nsq.SLtpBufIdx = f.LtpMemLength;
                    nsq.RewhiteFlag = 1;
                }
            }

            DelDecScaleStates(f, nsq, delDec, nStates, x16.Slice(k * f.SubfrLength), xScQ10, sLtp, sLtpQ15, k, ltpScaleQ14, gainsQ16, pitchL,
                indices.SignalType, decisionDelay);
            NoiseShapeQuantizerDelDec(nsq, delDec, sampleState, indices.SignalType, xScQ10, pulses, k * f.SubfrLength, pxq, sLtpQ15, delayedGainQ10,
                aQ12, bQ14, arShpQ13, lag, harmShapeFirPackedQ14, tiltQ14[k], lfShpQ14[k], gainsQ16[k], lambdaQ10, offsetQ10, f.SubfrLength, subfr++,
                f.ShapingLpcOrder, f.PredictLpcOrder, f.WarpingQ16, nStates, ref smplBufIdx, decisionDelay);
            pxq += f.SubfrLength;
        }

        rdMinQ10 = delDec[DdRd];
        winnerInd = 0;
        for (int k = 1; k < nStates; k++)
        {
            if (delDec[(k * DdSize) + DdRd] < rdMinQ10)
            {
                rdMinQ10 = delDec[(k * DdSize) + DdRd];
                winnerInd = k;
            }
        }

        Span<int> psDd = delDec.Slice(winnerInd * DdSize, DdSize);
        indices.Seed = (sbyte)psDd[DdSeedInit];
        lastSmpleIdx = smplBufIdx + decisionDelay;
        int gainQ10 = gainsQ16[f.NbSubfr - 1] >> 6;
        int pulsesEnd = f.NbSubfr * f.SubfrLength;
        for (int i = 0; i < decisionDelay; i++)
        {
            lastSmpleIdx = (lastSmpleIdx - 1) % DecisionDelay;
            if (lastSmpleIdx < 0)
            {
                lastSmpleIdx += DecisionDelay;
            }

            pulses[pulsesEnd + i - decisionDelay] = (sbyte)RShiftRound(psDd[DdQ + lastSmpleIdx], 10);
            nsq.Xq[pxq + i - decisionDelay] = (short)Sat16(RShiftRound(Smulww(psDd[DdXq + lastSmpleIdx], gainQ10), 8));
            nsq.SLtpShpQ14[nsq.SLtpShpBufIdx - decisionDelay + i] = psDd[DdShape + lastSmpleIdx];
        }

        psDd.Slice(DdSLpc + f.SubfrLength, NsqLpcBufLength).CopyTo(nsq.SLpcQ14);
        psDd.Slice(DdSAr2, MaxShapeLpcOrder).CopyTo(nsq.SAr2Q14);
        nsq.SLfArShpQ14 = psDd[DdLfAr];
        nsq.SDiffShpQ14 = psDd[DdDiff];
        nsq.LagPrev = pitchL[f.NbSubfr - 1];
        nsq.Xq.AsSpan(f.FrameLength, f.LtpMemLength).CopyTo(nsq.Xq);
        nsq.SLtpShpQ14.AsSpan(f.FrameLength, f.LtpMemLength).CopyTo(nsq.SLtpShpQ14);
    }

    private static void NoiseShapeQuantizerDelDec(SilkNsqState nsq, Span<int> delDec, Span<int> sampleState, int signalType, ReadOnlySpan<int> xQ10,
        Span<sbyte> pulses, int pulsesBase, int xqBase, Span<int> sLtpQ15, Span<int> delayedGainQ10, ReadOnlySpan<short> aQ12, ReadOnlySpan<short> bQ14,
        ReadOnlySpan<short> arShpQ13, int lag, int harmShapeFirPackedQ14, int tiltQ14, int lfShpQ14, int gainQ16, int lambdaQ10, int offsetQ10, int length,
        int subfr, int shapingLpcOrder, int predictLpcOrder, int warpingQ16, int nStates, ref int smplBufIdx, int decisionDelay)
    {
        unchecked
        {
            int[] sLtpShp = nsq.SLtpShpQ14;
            short[] xq = nsq.Xq;
            int shpLag = nsq.SLtpShpBufIdx - lag + (HarmShapeFirTaps / 2);
            int predLag = nsq.SLtpBufIdx - lag + (LtpOrder / 2);
            int gainQ10 = gainQ16 >> 6;
            for (int i = 0; i < length; i++)
            {
                int ltpPredQ14;
                if (signalType == SilkChannelDecoder.TypeVoiced)
                {
                    ltpPredQ14 = 2;
                    ltpPredQ14 = Smlawb(ltpPredQ14, sLtpQ15[predLag], bQ14[0]);
                    ltpPredQ14 = Smlawb(ltpPredQ14, sLtpQ15[predLag - 1], bQ14[1]);
                    ltpPredQ14 = Smlawb(ltpPredQ14, sLtpQ15[predLag - 2], bQ14[2]);
                    ltpPredQ14 = Smlawb(ltpPredQ14, sLtpQ15[predLag - 3], bQ14[3]);
                    ltpPredQ14 = Smlawb(ltpPredQ14, sLtpQ15[predLag - 4], bQ14[4]);
                    ltpPredQ14 = LShift(ltpPredQ14, 1);
                    predLag++;
                }
                else
                {
                    ltpPredQ14 = 0;
                }

                int nLtpQ14;
                if (lag > 0)
                {
                    nLtpQ14 = Smulwb(AddSat32(sLtpShp[shpLag], sLtpShp[shpLag - 2]), harmShapeFirPackedQ14);
                    nLtpQ14 = Smlawt(nLtpQ14, sLtpShp[shpLag - 1], harmShapeFirPackedQ14);
                    nLtpQ14 = ltpPredQ14 - LShift(nLtpQ14, 2);
                    shpLag++;
                }
                else
                {
                    nLtpQ14 = 0;
                }

                for (int k = 0; k < nStates; k++)
                {
                    Span<int> dd = delDec.Slice(k * DdSize, DdSize);
                    Span<int> ss = sampleState.Slice(k * 2 * SsSize, 2 * SsSize);
                    dd[DdSeed] = Rand(dd[DdSeed]);
                    int lpcPredQ14 = ShortPrediction(dd, DdSLpc + NsqLpcBufLength - 1 + i, aQ12, predictLpcOrder);
                    lpcPredQ14 = LShift(lpcPredQ14, 4);

                    // Noise shape feedback through the warped allpass sections.
                    Span<int> sAr2 = dd.Slice(DdSAr2, MaxShapeLpcOrder);
                    int tmp2 = Smlawb(dd[DdDiff], sAr2[0], warpingQ16);
                    int tmp1 = Smlawb(sAr2[0], SubOvflw(sAr2[1], tmp2), warpingQ16);
                    sAr2[0] = tmp2;
                    int nArQ14 = shapingLpcOrder >> 1;
                    nArQ14 = Smlawb(nArQ14, tmp2, arShpQ13[0]);
                    for (int j = 2; j < shapingLpcOrder; j += 2)
                    {
                        tmp2 = Smlawb(sAr2[j - 1], SubOvflw(sAr2[j], tmp1), warpingQ16);
                        sAr2[j - 1] = tmp1;
                        nArQ14 = Smlawb(nArQ14, tmp1, arShpQ13[j - 1]);
                        tmp1 = Smlawb(sAr2[j], SubOvflw(sAr2[j + 1], tmp2), warpingQ16);
                        sAr2[j] = tmp2;
                        nArQ14 = Smlawb(nArQ14, tmp2, arShpQ13[j]);
                    }

                    sAr2[shapingLpcOrder - 1] = tmp1;
                    nArQ14 = Smlawb(nArQ14, tmp1, arShpQ13[shapingLpcOrder - 1]);
                    nArQ14 = LShift(nArQ14, 1);
                    nArQ14 = Smlawb(nArQ14, dd[DdLfAr], tiltQ14);
                    nArQ14 = LShift(nArQ14, 2);
                    int nLfQ14 = Smulwb(dd[DdShape + smplBufIdx], lfShpQ14);
                    nLfQ14 = Smlawt(nLfQ14, dd[DdLfAr], lfShpQ14);
                    nLfQ14 = LShift(nLfQ14, 2);

                    tmp1 = AddSat32(nArQ14, nLfQ14);
                    tmp2 = AddOvflw(nLtpQ14, lpcPredQ14);
                    tmp1 = SubSat32(tmp2, tmp1);
                    tmp1 = RShiftRound(tmp1, 4);
                    int rQ10 = xQ10[i] - tmp1;
                    if (dd[DdSeed] < 0)
                    {
                        rQ10 = -rQ10;
                    }

                    rQ10 = Limit(rQ10, -(31 << 10), 30 << 10);
                    Quantize(rQ10, offsetQ10, lambdaQ10, out int q1Q10, out int q2Q10, out int rd1Q10, out int rd2Q10);
                    int rrQ10 = rQ10 - q1Q10;
                    rd1Q10 = Smlabb(rd1Q10, rrQ10, rrQ10) >> 10;
                    rrQ10 = rQ10 - q2Q10;
                    rd2Q10 = Smlabb(rd2Q10, rrQ10, rrQ10) >> 10;
                    if (rd1Q10 < rd2Q10)
                    {
                        ss[SsRd] = dd[DdRd] + rd1Q10;
                        ss[SsSize + SsRd] = dd[DdRd] + rd2Q10;
                        ss[SsQ] = q1Q10;
                        ss[SsSize + SsQ] = q2Q10;
                    }
                    else
                    {
                        ss[SsRd] = dd[DdRd] + rd2Q10;
                        ss[SsSize + SsRd] = dd[DdRd] + rd1Q10;
                        ss[SsQ] = q2Q10;
                        ss[SsSize + SsQ] = q1Q10;
                    }

                    for (int c = 0; c < 2; c++)
                    {
                        int o = c * SsSize;
                        int excQ14 = LShift(ss[o + SsQ], 4);
                        if (dd[DdSeed] < 0)
                        {
                            excQ14 = -excQ14;
                        }

                        int lpcExcQ14 = excQ14 + ltpPredQ14;
                        int xqQ14 = AddOvflw(lpcExcQ14, lpcPredQ14);
                        ss[o + SsDiff] = SubOvflw(xqQ14, LShift(xQ10[i], 4));
                        int sLfArShpQ14 = SubOvflw(ss[o + SsDiff], nArQ14);
                        ss[o + SsSLtpShp] = SubSat32(sLfArShpQ14, nLfQ14);
                        ss[o + SsLfAr] = sLfArShpQ14;
                        ss[o + SsLpcExc] = lpcExcQ14;
                        ss[o + SsXq] = xqQ14;
                    }
                }

                smplBufIdx = (smplBufIdx - 1) % DecisionDelay;
                if (smplBufIdx < 0)
                {
                    smplBufIdx += DecisionDelay;
                }

                int lastSmpleIdx = (smplBufIdx + decisionDelay) % DecisionDelay;

                int rdMinQ10 = sampleState[SsRd];
                int winnerInd = 0;
                for (int k = 1; k < nStates; k++)
                {
                    if (sampleState[(k * 2 * SsSize) + SsRd] < rdMinQ10)
                    {
                        rdMinQ10 = sampleState[(k * 2 * SsSize) + SsRd];
                        winnerInd = k;
                    }
                }

                int winnerRandState = delDec[(winnerInd * DdSize) + DdRandState + lastSmpleIdx];
                for (int k = 0; k < nStates; k++)
                {
                    if (delDec[(k * DdSize) + DdRandState + lastSmpleIdx] != winnerRandState)
                    {
                        sampleState[(k * 2 * SsSize) + SsRd] += int.MaxValue >> 4;
                        sampleState[(k * 2 * SsSize) + SsSize + SsRd] += int.MaxValue >> 4;
                    }
                }

                int rdMaxQ10 = sampleState[SsRd];
                rdMinQ10 = sampleState[SsSize + SsRd];
                int rdMaxInd = 0;
                int rdMinInd = 0;
                for (int k = 1; k < nStates; k++)
                {
                    if (sampleState[(k * 2 * SsSize) + SsRd] > rdMaxQ10)
                    {
                        rdMaxQ10 = sampleState[(k * 2 * SsSize) + SsRd];
                        rdMaxInd = k;
                    }

                    if (sampleState[(k * 2 * SsSize) + SsSize + SsRd] < rdMinQ10)
                    {
                        rdMinQ10 = sampleState[(k * 2 * SsSize) + SsSize + SsRd];
                        rdMinInd = k;
                    }
                }

                if (rdMinQ10 < rdMaxQ10)
                {
                    delDec.Slice((rdMinInd * DdSize) + i, DdSize - i).CopyTo(delDec.Slice((rdMaxInd * DdSize) + i, DdSize - i));
                    sampleState.Slice((rdMinInd * 2 * SsSize) + SsSize, SsSize).CopyTo(sampleState.Slice(rdMaxInd * 2 * SsSize, SsSize));
                }

                Span<int> psDd = delDec.Slice(winnerInd * DdSize, DdSize);
                if (subfr > 0 || i >= decisionDelay)
                {
                    pulses[pulsesBase + i - decisionDelay] = (sbyte)RShiftRound(psDd[DdQ + lastSmpleIdx], 10);
                    xq[xqBase + i - decisionDelay] = (short)Sat16(RShiftRound(Smulww(psDd[DdXq + lastSmpleIdx], delayedGainQ10[lastSmpleIdx]), 8));
                    sLtpShp[nsq.SLtpShpBufIdx - decisionDelay] = psDd[DdShape + lastSmpleIdx];
                    sLtpQ15[nsq.SLtpBufIdx - decisionDelay] = psDd[DdPred + lastSmpleIdx];
                }

                nsq.SLtpShpBufIdx++;
                nsq.SLtpBufIdx++;

                for (int k = 0; k < nStates; k++)
                {
                    Span<int> dd = delDec.Slice(k * DdSize, DdSize);
                    ReadOnlySpan<int> ss = sampleState.Slice(k * 2 * SsSize, SsSize);
                    dd[DdLfAr] = ss[SsLfAr];
                    dd[DdDiff] = ss[SsDiff];
                    dd[DdSLpc + NsqLpcBufLength + i] = ss[SsXq];
                    dd[DdXq + smplBufIdx] = ss[SsXq];
                    dd[DdQ + smplBufIdx] = ss[SsQ];
                    dd[DdPred + smplBufIdx] = LShift(ss[SsLpcExc], 1);
                    dd[DdShape + smplBufIdx] = ss[SsSLtpShp];
                    dd[DdSeed] = AddOvflw(dd[DdSeed], RShiftRound(ss[SsQ], 10));
                    dd[DdRandState + smplBufIdx] = dd[DdSeed];
                    dd[DdRd] = ss[SsRd];
                }

                delayedGainQ10[smplBufIdx] = gainQ10;
            }

            for (int k = 0; k < nStates; k++)
            {
                Span<int> dd = delDec.Slice(k * DdSize, DdSize);
                dd.Slice(DdSLpc + length, NsqLpcBufLength).CopyTo(dd[DdSLpc..]);
            }
        }
    }

    private static void DelDecScaleStates(in Frame f, SilkNsqState nsq, Span<int> delDec, int nStates, ReadOnlySpan<short> x16, Span<int> xScQ10,
        ReadOnlySpan<short> sLtp, Span<int> sLtpQ15, int subfr, int ltpScaleQ14, ReadOnlySpan<int> gainsQ16, ReadOnlySpan<int> pitchL, int signalType,
        int decisionDelay)
    {
        int lag = pitchL[subfr];
        int invGainQ31 = Inverse32VarQ(Math.Max(gainsQ16[subfr], 1), 47);
        int invGainQ26 = RShiftRound(invGainQ31, 5);
        for (int i = 0; i < f.SubfrLength; i++)
        {
            xScQ10[i] = Smulww(x16[i], invGainQ26);
        }

        if (nsq.RewhiteFlag != 0)
        {
            if (subfr == 0)
            {
                invGainQ31 = LShift(Smulwb(invGainQ31, ltpScaleQ14), 2);
            }

            for (int i = nsq.SLtpBufIdx - lag - (LtpOrder / 2); i < nsq.SLtpBufIdx; i++)
            {
                sLtpQ15[i] = Smulwb(invGainQ31, sLtp[i]);
            }
        }

        if (gainsQ16[subfr] != nsq.PrevGainQ16)
        {
            int gainAdjQ16 = Div32VarQ(nsq.PrevGainQ16, gainsQ16[subfr], 16);
            for (int i = nsq.SLtpShpBufIdx - f.LtpMemLength; i < nsq.SLtpShpBufIdx; i++)
            {
                nsq.SLtpShpQ14[i] = Smulww(gainAdjQ16, nsq.SLtpShpQ14[i]);
            }

            if (signalType == SilkChannelDecoder.TypeVoiced && nsq.RewhiteFlag == 0)
            {
                for (int i = nsq.SLtpBufIdx - lag - (LtpOrder / 2); i < nsq.SLtpBufIdx - decisionDelay; i++)
                {
                    sLtpQ15[i] = Smulww(gainAdjQ16, sLtpQ15[i]);
                }
            }

            for (int k = 0; k < nStates; k++)
            {
                Span<int> dd = delDec.Slice(k * DdSize, DdSize);
                dd[DdLfAr] = Smulww(gainAdjQ16, dd[DdLfAr]);
                dd[DdDiff] = Smulww(gainAdjQ16, dd[DdDiff]);
                for (int i = 0; i < NsqLpcBufLength; i++)
                {
                    dd[DdSLpc + i] = Smulww(gainAdjQ16, dd[DdSLpc + i]);
                }

                for (int i = 0; i < MaxShapeLpcOrder; i++)
                {
                    dd[DdSAr2 + i] = Smulww(gainAdjQ16, dd[DdSAr2 + i]);
                }

                for (int i = 0; i < DecisionDelay; i++)
                {
                    dd[DdPred + i] = Smulww(gainAdjQ16, dd[DdPred + i]);
                    dd[DdShape + i] = Smulww(gainAdjQ16, dd[DdShape + i]);
                }
            }

            nsq.PrevGainQ16 = gainsQ16[subfr];
        }
    }
}
