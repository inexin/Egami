// Translated from libopus 1.5.2 silk/decode_core.c.
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>The inverse noise shaping quantizer: LTP and LPC synthesis.</summary>
internal sealed partial class SilkChannelDecoder
{
    private const int QuantLevelAdjustQ10 = 80;

    /// <summary>silk_decode_core: synthesizes the frame from the excitation pulses.</summary>
    private void DecodeCore(SilkDecoderControl decCtrl, Span<short> xq, ReadOnlySpan<short> pulses)
    {
        Span<short> sLtp = stackalloc short[LtpMemLength];
        Span<int> sLtpQ15 = stackalloc int[LtpMemLength + FrameLength];
        Span<int> resQ14 = stackalloc int[SubfrLength];
        Span<int> sLpcQ14 = stackalloc int[SubfrLength + MaxLpcOrder];
        Span<short> aQ12Tmp = stackalloc short[MaxLpcOrder];
        SilkSideInfoIndices indices = Indices;
        int lag = 0;

        unchecked
        {
            int offsetQ10 = SilkTables.QuantizationOffsetsQ10[((indices.SignalType >> 1) * 2) + indices.QuantOffsetType];
            bool nlsfInterpolationFlag = indices.NlsfInterpCoefQ2 < 1 << 2;

            int randSeed = indices.Seed;
            for (int i = 0; i < FrameLength; i++)
            {
                randSeed = Rand(randSeed);
                int exc = LShift(pulses[i], 14);
                if (exc > 0)
                {
                    exc -= QuantLevelAdjustQ10 << 4;
                }
                else if (exc < 0)
                {
                    exc += QuantLevelAdjustQ10 << 4;
                }

                exc += offsetQ10 << 4;
                if (randSeed < 0)
                {
                    exc = -exc;
                }

                ExcQ14[i] = exc;
                randSeed = AddOvflw(randSeed, pulses[i]);
            }

            SLpcQ14Buf.AsSpan(0, MaxLpcOrder).CopyTo(sLpcQ14);
            int pexc = 0;
            int pxq = 0;
            int sLtpBufIdx = LtpMemLength;

            for (int k = 0; k < NbSubfr; k++)
            {
                Span<short> aQ12 = decCtrl.PredCoef(k >> 1);
                aQ12[..LpcOrder].CopyTo(aQ12Tmp);
                Span<short> bQ14 = decCtrl.LtpCoefQ14.AsSpan(k * LtpOrder, LtpOrder);
                int signalType = indices.SignalType;
                int gainQ10 = decCtrl.GainsQ16[k] >> 6;
                int invGainQ31 = Inverse32VarQ(decCtrl.GainsQ16[k], 47);
                int gainAdjQ16;
                if (decCtrl.GainsQ16[k] != PrevGainQ16)
                {
                    gainAdjQ16 = Div32VarQ(PrevGainQ16, decCtrl.GainsQ16[k], 16);
                    for (int i = 0; i < MaxLpcOrder; i++)
                    {
                        sLpcQ14[i] = Smulww(gainAdjQ16, sLpcQ14[i]);
                    }
                }
                else
                {
                    gainAdjQ16 = 1 << 16;
                }

                PrevGainQ16 = decCtrl.GainsQ16[k];

                if (LossCnt != 0 && PrevSignalType == TypeVoiced && indices.SignalType != TypeVoiced && k < MaxNbSubfr / 2)
                {
                    bQ14.Clear();
                    bQ14[LtpOrder / 2] = 4096;
                    signalType = TypeVoiced;
                    decCtrl.PitchL[k] = LagPrev;
                }

                if (signalType == TypeVoiced)
                {
                    lag = decCtrl.PitchL[k];
                    if (k == 0 || (k == 2 && nlsfInterpolationFlag))
                    {
                        int startIdx = LtpMemLength - lag - LpcOrder - (LtpOrder / 2);
                        if (k == 2)
                        {
                            xq[..(2 * SubfrLength)].CopyTo(OutBuf.AsSpan(LtpMemLength));
                        }

                        SilkSigProc.LpcAnalysisFilter(sLtp[startIdx..], OutBuf.AsSpan(startIdx + (k * SubfrLength)), aQ12, LtpMemLength - startIdx, LpcOrder);
                        if (k == 0)
                        {
                            invGainQ31 = LShift(Smulwb(invGainQ31, decCtrl.LtpScaleQ14), 2);
                        }

                        for (int i = 0; i < lag + (LtpOrder / 2); i++)
                        {
                            sLtpQ15[sLtpBufIdx - i - 1] = Smulwb(invGainQ31, sLtp[LtpMemLength - i - 1]);
                        }
                    }
                    else if (gainAdjQ16 != 1 << 16)
                    {
                        for (int i = 0; i < lag + (LtpOrder / 2); i++)
                        {
                            sLtpQ15[sLtpBufIdx - i - 1] = Smulww(gainAdjQ16, sLtpQ15[sLtpBufIdx - i - 1]);
                        }
                    }
                }

                Span<int> presQ14 = resQ14;
                if (signalType == TypeVoiced)
                {
                    int predLag = sLtpBufIdx - lag + (LtpOrder / 2);
                    for (int i = 0; i < SubfrLength; i++)
                    {
                        int ltpPredQ13 = 2;
                        ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag], bQ14[0]);
                        ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 1], bQ14[1]);
                        ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 2], bQ14[2]);
                        ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 3], bQ14[3]);
                        ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 4], bQ14[4]);
                        predLag++;

                        resQ14[i] = AddLShift32(ExcQ14[pexc + i], ltpPredQ13, 1);
                        sLtpQ15[sLtpBufIdx] = LShift(resQ14[i], 1);
                        sLtpBufIdx++;
                    }
                }
                else
                {
                    presQ14 = ExcQ14.AsSpan(pexc, SubfrLength);
                }

                for (int i = 0; i < SubfrLength; i++)
                {
                    int lpcPredQ10 = LpcOrder >> 1;
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 1], aQ12Tmp[0]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 2], aQ12Tmp[1]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 3], aQ12Tmp[2]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 4], aQ12Tmp[3]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 5], aQ12Tmp[4]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 6], aQ12Tmp[5]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 7], aQ12Tmp[6]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 8], aQ12Tmp[7]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 9], aQ12Tmp[8]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 10], aQ12Tmp[9]);
                    if (LpcOrder == 16)
                    {
                        lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 11], aQ12Tmp[10]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 12], aQ12Tmp[11]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 13], aQ12Tmp[12]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 14], aQ12Tmp[13]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 15], aQ12Tmp[14]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 16], aQ12Tmp[15]);
                    }

                    sLpcQ14[MaxLpcOrder + i] = AddSat32(presQ14[i], LShiftSat32(lpcPredQ10, 4));
                    xq[pxq + i] = (short)Sat16(RShiftRound(Smulww(sLpcQ14[MaxLpcOrder + i], gainQ10), 8));
                }

                sLpcQ14.Slice(SubfrLength, MaxLpcOrder).CopyTo(sLpcQ14);
                pexc += SubfrLength;
                pxq += SubfrLength;
            }

            sLpcQ14[..MaxLpcOrder].CopyTo(SLpcQ14Buf);
        }
    }
}
