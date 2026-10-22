// Translated from libopus 1.5.2 silk/PLC.c, PLC.h and CNG.c (deep PLC off).
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>Packet loss concealment and comfort noise generation.</summary>
internal sealed partial class SilkChannelDecoder
{
    private const int NbAtt = 2;
    private const int BweCoefQ16 = 64881;
    private const int VPitchGainStartMinQ14 = 11469;
    private const int VPitchGainStartMaxQ14 = 15565;
    private const int MaxPitchLagMs = 18;
    private const int RandBufSize = 128;
    private const int RandBufMask = RandBufSize - 1;
    private const int Log2InvLpcGainHighThres = 3;
    private const int Log2InvLpcGainLowThres = 8;
    private const int PitchDriftFacQ16 = 655;
    private const int CngBufMaskMax = 255;
    private const int CngGainSmthQ16 = 4634;
    private const int CngGainSmthThresholdQ16 = 46396;
    private const int CngNlsfSmthQ16 = 16348;

    // silk_PLC_struct.
    private int _plcPitchLQ8;
    private readonly short[] _plcLtpCoefQ14 = new short[LtpOrder];
    private readonly short[] _plcPrevLpcQ12 = new short[MaxLpcOrder];
    private int _plcLastFrameLost;
    private int _plcRandSeed;
    private short _plcRandScaleQ14;
    private int _plcConcEnergy;
    private int _plcConcEnergyShift;
    private short _plcPrevLtpScaleQ14;
    private readonly int[] _plcPrevGainQ16 = new int[2];
    private int _plcFsKhz;
    private int _plcNbSubfr;
    private int _plcSubfrLength;

    // silk_CNG_struct.
    private readonly int[] _cngExcBufQ14 = new int[MaxFrameLength];
    private readonly short[] _cngSmthNlsfQ15 = new short[MaxLpcOrder];
    private readonly int[] _cngSynthState = new int[MaxLpcOrder];
    private int _cngSmthGainQ16;
    private int _cngRandSeed;
    private int _cngFsKhz;

    private static ReadOnlySpan<short> HarmAttQ15 => [32440, 31130];

    private static ReadOnlySpan<short> PlcRandAttenuateVQ15 => [31130, 26214];

    private static ReadOnlySpan<short> PlcRandAttenuateUvQ15 => [32440, 29491];

    private void ClearPlc()
    {
        _plcPitchLQ8 = 0;
        Array.Clear(_plcLtpCoefQ14);
        Array.Clear(_plcPrevLpcQ12);
        _plcLastFrameLost = 0;
        _plcRandSeed = 0;
        _plcRandScaleQ14 = 0;
        _plcConcEnergy = 0;
        _plcConcEnergyShift = 0;
        _plcPrevLtpScaleQ14 = 0;
        Array.Clear(_plcPrevGainQ16);
        _plcFsKhz = 0;
        _plcNbSubfr = 0;
        _plcSubfrLength = 0;
    }

    private void ClearCng()
    {
        Array.Clear(_cngExcBufQ14);
        Array.Clear(_cngSmthNlsfQ15);
        Array.Clear(_cngSynthState);
        _cngSmthGainQ16 = 0;
        _cngRandSeed = 0;
        _cngFsKhz = 0;
    }

    /// <summary>silk_PLC_Reset.</summary>
    private void PlcReset()
    {
        _plcPitchLQ8 = LShift(FrameLength, 8 - 1);
        _plcPrevGainQ16[0] = 1 << 16;
        _plcPrevGainQ16[1] = 1 << 16;
        _plcSubfrLength = 20;
        _plcNbSubfr = 2;
    }

    /// <summary>silk_PLC: conceals a lost frame, or updates the concealment state from a good one.</summary>
    private void Plc(SilkDecoderControl decCtrl, Span<short> frame, bool lost)
    {
        if (FsKhz != _plcFsKhz)
        {
            PlcReset();
            _plcFsKhz = FsKhz;
        }

        if (lost)
        {
            PlcConceal(decCtrl, frame);
            LossCnt++;
        }
        else
        {
            PlcUpdate(decCtrl);
        }
    }

    /// <summary>silk_PLC_update.</summary>
    private void PlcUpdate(SilkDecoderControl decCtrl)
    {
        PrevSignalType = Indices.SignalType;
        int ltpGainQ14 = 0;
        if (Indices.SignalType == TypeVoiced)
        {
            for (int j = 0; j * SubfrLength < decCtrl.PitchL[NbSubfr - 1]; j++)
            {
                if (j == NbSubfr)
                {
                    break;
                }

                int tempLtpGainQ14 = 0;
                for (int i = 0; i < LtpOrder; i++)
                {
                    tempLtpGainQ14 += decCtrl.LtpCoefQ14[((NbSubfr - 1 - j) * LtpOrder) + i];
                }

                if (tempLtpGainQ14 > ltpGainQ14)
                {
                    ltpGainQ14 = tempLtpGainQ14;
                    decCtrl.LtpCoefQ14.AsSpan(Smulbb(NbSubfr - 1 - j, LtpOrder), LtpOrder).CopyTo(_plcLtpCoefQ14);
                    _plcPitchLQ8 = LShift(decCtrl.PitchL[NbSubfr - 1 - j], 8);
                }
            }

            Array.Clear(_plcLtpCoefQ14);
            _plcLtpCoefQ14[LtpOrder / 2] = (short)ltpGainQ14;

            if (ltpGainQ14 < VPitchGainStartMinQ14)
            {
                int tmp = LShift(VPitchGainStartMinQ14, 10);
                int scaleQ10 = tmp / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++)
                {
                    _plcLtpCoefQ14[i] = (short)(Smulbb(_plcLtpCoefQ14[i], scaleQ10) >> 10);
                }
            }
            else if (ltpGainQ14 > VPitchGainStartMaxQ14)
            {
                int tmp = LShift(VPitchGainStartMaxQ14, 14);
                int scaleQ14 = tmp / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++)
                {
                    _plcLtpCoefQ14[i] = (short)(Smulbb(_plcLtpCoefQ14[i], scaleQ14) >> 14);
                }
            }
        }
        else
        {
            _plcPitchLQ8 = LShift(Smulbb(FsKhz, 18), 8);
            Array.Clear(_plcLtpCoefQ14);
        }

        decCtrl.PredCoef(1)[..LpcOrder].CopyTo(_plcPrevLpcQ12);
        _plcPrevLtpScaleQ14 = (short)decCtrl.LtpScaleQ14;
        decCtrl.GainsQ16.AsSpan(NbSubfr - 2, 2).CopyTo(_plcPrevGainQ16);
        _plcSubfrLength = SubfrLength;
        _plcNbSubfr = NbSubfr;
    }

    /// <summary>silk_PLC_energy.</summary>
    private static void PlcEnergy(out int energy1, out int shift1, out int energy2, out int shift2, ReadOnlySpan<int> excQ14, ReadOnlySpan<int> prevGainQ10, int subfrLength, int nbSubfr)
    {
        Span<short> excBuf = stackalloc short[2 * subfrLength];
        int ptr = 0;
        for (int k = 0; k < 2; k++)
        {
            for (int i = 0; i < subfrLength; i++)
            {
                excBuf[ptr + i] = (short)Sat16(Smulww(excQ14[i + ((k + nbSubfr - 2) * subfrLength)], prevGainQ10[k]) >> 8);
            }

            ptr += subfrLength;
        }

        SilkSigProc.SumSqrShift(out energy1, out shift1, excBuf, subfrLength);
        SilkSigProc.SumSqrShift(out energy2, out shift2, excBuf[subfrLength..], subfrLength);
    }

    /// <summary>silk_PLC_conceal: extrapolates a lost frame.</summary>
    private void PlcConceal(SilkDecoderControl decCtrl, Span<short> frame)
    {
        Span<int> sLtpQ14 = stackalloc int[LtpMemLength + FrameLength];
        Span<short> sLtp = stackalloc short[LtpMemLength];
        Span<short> aQ12 = stackalloc short[MaxLpcOrder];
        Span<int> prevGainQ10 = stackalloc int[2];
        unchecked
        {
            prevGainQ10[0] = _plcPrevGainQ16[0] >> 6;
            prevGainQ10[1] = _plcPrevGainQ16[1] >> 6;
            if (FirstFrameAfterReset != 0)
            {
                Array.Clear(_plcPrevLpcQ12);
            }

            PlcEnergy(out int energy1, out int shift1, out int energy2, out int shift2, ExcQ14, prevGainQ10, SubfrLength, NbSubfr);
            int randStart;
            if ((energy1 >> shift2) < (energy2 >> shift1))
            {
                randStart = Math.Max(0, ((_plcNbSubfr - 1) * _plcSubfrLength) - RandBufSize);
            }
            else
            {
                randStart = Math.Max(0, (_plcNbSubfr * _plcSubfrLength) - RandBufSize);
            }

            ReadOnlySpan<int> randPtr = ExcQ14.AsSpan(randStart);
            Span<short> bQ14 = _plcLtpCoefQ14;
            short randScaleQ14 = _plcRandScaleQ14;

            int harmGainQ15 = HarmAttQ15[Math.Min(NbAtt - 1, LossCnt)];
            int randGainQ15 = PrevSignalType == TypeVoiced
                ? PlcRandAttenuateVQ15[Math.Min(NbAtt - 1, LossCnt)]
                : PlcRandAttenuateUvQ15[Math.Min(NbAtt - 1, LossCnt)];

            SilkSigProc.BwExpander(_plcPrevLpcQ12, LpcOrder, BweCoefQ16);
            _plcPrevLpcQ12.AsSpan(0, LpcOrder).CopyTo(aQ12);

            if (LossCnt == 0)
            {
                randScaleQ14 = 1 << 14;
                if (PrevSignalType == TypeVoiced)
                {
                    for (int i = 0; i < LtpOrder; i++)
                    {
                        randScaleQ14 = (short)(randScaleQ14 - bQ14[i]);
                    }

                    randScaleQ14 = Math.Max((short)3277, randScaleQ14);
                    randScaleQ14 = (short)(Smulbb(randScaleQ14, _plcPrevLtpScaleQ14) >> 14);
                }
                else
                {
                    int invGainQ30 = SilkSigProc.LpcInversePredGain(_plcPrevLpcQ12, LpcOrder);
                    int downScaleQ30 = Math.Min((1 << 30) >> Log2InvLpcGainHighThres, invGainQ30);
                    downScaleQ30 = Math.Max((1 << 30) >> Log2InvLpcGainLowThres, downScaleQ30);
                    downScaleQ30 = LShift(downScaleQ30, Log2InvLpcGainHighThres);
                    randGainQ15 = Smulwb(downScaleQ30, randGainQ15) >> 14;
                }
            }

            int randSeed = _plcRandSeed;
            int lag = RShiftRound(_plcPitchLQ8, 8);
            int sLtpBufIdx = LtpMemLength;

            int idx = LtpMemLength - lag - LpcOrder - (LtpOrder / 2);
            SilkSigProc.LpcAnalysisFilter(sLtp[idx..], OutBuf.AsSpan(idx), aQ12, LtpMemLength - idx, LpcOrder);

            int invGainQ30b = Inverse32VarQ(_plcPrevGainQ16[1], 46);
            invGainQ30b = Math.Min(invGainQ30b, int.MaxValue >> 1);
            for (int i = idx + LpcOrder; i < LtpMemLength; i++)
            {
                sLtpQ14[i] = Smulwb(invGainQ30b, sLtp[i]);
            }

            for (int k = 0; k < NbSubfr; k++)
            {
                int predLag = sLtpBufIdx - lag + (LtpOrder / 2);
                for (int i = 0; i < SubfrLength; i++)
                {
                    int ltpPredQ12 = 2;
                    ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag], bQ14[0]);
                    ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 1], bQ14[1]);
                    ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 2], bQ14[2]);
                    ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 3], bQ14[3]);
                    ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 4], bQ14[4]);
                    predLag++;

                    randSeed = Rand(randSeed);
                    idx = (randSeed >> 25) & RandBufMask;
                    sLtpQ14[sLtpBufIdx] = LShift(Smlawb(ltpPredQ12, randPtr[idx], randScaleQ14), 2);
                    sLtpBufIdx++;
                }

                for (int j = 0; j < LtpOrder; j++)
                {
                    bQ14[j] = (short)(Smulbb(harmGainQ15, bQ14[j]) >> 15);
                }

                randScaleQ14 = (short)(Smulbb(randScaleQ14, randGainQ15) >> 15);
                _plcPitchLQ8 = Smlawb(_plcPitchLQ8, _plcPitchLQ8, PitchDriftFacQ16);
                _plcPitchLQ8 = Math.Min(_plcPitchLQ8, LShift(Smulbb(MaxPitchLagMs, FsKhz), 8));
                lag = RShiftRound(_plcPitchLQ8, 8);
            }

            Span<int> sLpcQ14 = sLtpQ14[(LtpMemLength - MaxLpcOrder)..];
            SLpcQ14Buf.AsSpan(0, MaxLpcOrder).CopyTo(sLpcQ14);
            for (int i = 0; i < FrameLength; i++)
            {
                int lpcPredQ10 = LpcOrder >> 1;
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 1], aQ12[0]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 2], aQ12[1]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 3], aQ12[2]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 4], aQ12[3]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 5], aQ12[4]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 6], aQ12[5]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 7], aQ12[6]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 8], aQ12[7]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 9], aQ12[8]);
                lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 10], aQ12[9]);
                for (int j = 10; j < LpcOrder; j++)
                {
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - j - 1], aQ12[j]);
                }

                sLpcQ14[MaxLpcOrder + i] = AddSat32(sLpcQ14[MaxLpcOrder + i], LShiftSat32(lpcPredQ10, 4));
                frame[i] = (short)Sat16(Sat16(RShiftRound(Smulww(sLpcQ14[MaxLpcOrder + i], prevGainQ10[1]), 8)));
            }

            sLpcQ14.Slice(FrameLength, MaxLpcOrder).CopyTo(SLpcQ14Buf);

            _plcRandSeed = randSeed;
            _plcRandScaleQ14 = randScaleQ14;
            for (int i = 0; i < MaxNbSubfr; i++)
            {
                decCtrl.PitchL[i] = lag;
            }
        }
    }

    /// <summary>silk_PLC_glue_frames: smooths the step from concealed frames to good ones.</summary>
    private void PlcGlueFrames(Span<short> frame, int length)
    {
        if (LossCnt != 0)
        {
            SilkSigProc.SumSqrShift(out _plcConcEnergy, out _plcConcEnergyShift, frame, length);
            _plcLastFrameLost = 1;
        }
        else
        {
            if (_plcLastFrameLost != 0)
            {
                SilkSigProc.SumSqrShift(out int energy, out int energyShift, frame, length);
                if (energyShift > _plcConcEnergyShift)
                {
                    _plcConcEnergy >>= energyShift - _plcConcEnergyShift;
                }
                else if (energyShift < _plcConcEnergyShift)
                {
                    energy >>= _plcConcEnergyShift - energyShift;
                }

                if (energy > _plcConcEnergy)
                {
                    unchecked
                    {
                        int lz = Clz32(_plcConcEnergy);
                        lz--;
                        _plcConcEnergy = LShift(_plcConcEnergy, lz);
                        energy >>= Math.Max(24 - lz, 0);
                        int fracQ24 = _plcConcEnergy / Math.Max(energy, 1);
                        int gainQ16 = LShift(SqrtApprox(fracQ24), 4);
                        int slopeQ16 = ((1 << 16) - gainQ16) / length;
                        slopeQ16 = LShift(slopeQ16, 2);
                        for (int i = 0; i < length; i++)
                        {
                            frame[i] = (short)Smulwb(gainQ16, frame[i]);
                            gainQ16 += slopeQ16;
                            if (gainQ16 > 1 << 16)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            _plcLastFrameLost = 0;
        }
    }

    /// <summary>silk_CNG_exc.</summary>
    private static void CngExc(Span<int> excQ14, ReadOnlySpan<int> excBufQ14, int length, ref int randSeed)
    {
        int excMask = CngBufMaskMax;
        while (excMask > length)
        {
            excMask >>= 1;
        }

        int seed = randSeed;
        for (int i = 0; i < length; i++)
        {
            seed = Rand(seed);
            int idx = (seed >> 24) & excMask;
            excQ14[i] = excBufQ14[idx];
        }

        randSeed = seed;
    }

    /// <summary>silk_CNG_Reset.</summary>
    private void CngReset()
    {
        int nlsfStepQ15 = short.MaxValue / (LpcOrder + 1);
        int nlsfAccQ15 = 0;
        for (int i = 0; i < LpcOrder; i++)
        {
            nlsfAccQ15 += nlsfStepQ15;
            _cngSmthNlsfQ15[i] = (short)nlsfAccQ15;
        }

        _cngSmthGainQ16 = 0;
        _cngRandSeed = 3176576;
    }

    /// <summary>silk_CNG: updates the comfort noise estimate and adds it to lost frames.</summary>
    private void Cng(SilkDecoderControl decCtrl, Span<short> frame, int length)
    {
        if (FsKhz != _cngFsKhz)
        {
            CngReset();
            _cngFsKhz = FsKhz;
        }

        unchecked
        {
            if (LossCnt == 0 && PrevSignalType == TypeNoVoiceActivity)
            {
                for (int i = 0; i < LpcOrder; i++)
                {
                    _cngSmthNlsfQ15[i] += (short)Smulwb(PrevNlsfQ15[i] - _cngSmthNlsfQ15[i], CngNlsfSmthQ16);
                }

                int maxGainQ16 = 0;
                int subfr = 0;
                for (int i = 0; i < NbSubfr; i++)
                {
                    if (decCtrl.GainsQ16[i] > maxGainQ16)
                    {
                        maxGainQ16 = decCtrl.GainsQ16[i];
                        subfr = i;
                    }
                }

                _cngExcBufQ14.AsSpan(0, (NbSubfr - 1) * SubfrLength).CopyTo(_cngExcBufQ14.AsSpan(SubfrLength));
                ExcQ14.AsSpan(subfr * SubfrLength, SubfrLength).CopyTo(_cngExcBufQ14);

                for (int i = 0; i < NbSubfr; i++)
                {
                    _cngSmthGainQ16 += Smulwb(decCtrl.GainsQ16[i] - _cngSmthGainQ16, CngGainSmthQ16);
                    if (Smulww(_cngSmthGainQ16, CngGainSmthThresholdQ16) > decCtrl.GainsQ16[i])
                    {
                        _cngSmthGainQ16 = decCtrl.GainsQ16[i];
                    }
                }
            }

            if (LossCnt != 0)
            {
                Span<int> cngSigQ14 = stackalloc int[length + MaxLpcOrder];
                Span<short> aQ12 = stackalloc short[MaxLpcOrder];
                int gainQ16 = Smulww(_plcRandScaleQ14, _plcPrevGainQ16[1]);
                if (gainQ16 >= (1 << 21) || _cngSmthGainQ16 > (1 << 23))
                {
                    gainQ16 = Smultt(gainQ16, gainQ16);
                    gainQ16 = Smultt(_cngSmthGainQ16, _cngSmthGainQ16) - LShift(gainQ16, 5);
                    gainQ16 = LShift(SqrtApprox(gainQ16), 16);
                }
                else
                {
                    gainQ16 = Smulww(gainQ16, gainQ16);
                    gainQ16 = Smulww(_cngSmthGainQ16, _cngSmthGainQ16) - LShift(gainQ16, 5);
                    gainQ16 = LShift(SqrtApprox(gainQ16), 8);
                }

                int gainQ10 = gainQ16 >> 6;
                CngExc(cngSigQ14[MaxLpcOrder..], _cngExcBufQ14, length, ref _cngRandSeed);
                SilkSigProc.Nlsf2A(aQ12, _cngSmthNlsfQ15, LpcOrder);
                _cngSynthState.AsSpan().CopyTo(cngSigQ14);
                for (int i = 0; i < length; i++)
                {
                    int lpcPredQ10 = LpcOrder >> 1;
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 1], aQ12[0]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 2], aQ12[1]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 3], aQ12[2]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 4], aQ12[3]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 5], aQ12[4]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 6], aQ12[5]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 7], aQ12[6]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 8], aQ12[7]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 9], aQ12[8]);
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 10], aQ12[9]);
                    if (LpcOrder == 16)
                    {
                        lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 11], aQ12[10]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 12], aQ12[11]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 13], aQ12[12]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 14], aQ12[13]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 15], aQ12[14]);
                        lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 16], aQ12[15]);
                    }

                    cngSigQ14[MaxLpcOrder + i] = AddSat32(cngSigQ14[MaxLpcOrder + i], LShiftSat32(lpcPredQ10, 4));
                    frame[i] = (short)AddSat16(frame[i], Sat16(RShiftRound(Smulww(cngSigQ14[MaxLpcOrder + i], gainQ10), 8)));
                }

                cngSigQ14.Slice(length, MaxLpcOrder).CopyTo(_cngSynthState);
            }
            else
            {
                _cngSynthState.AsSpan(0, LpcOrder).Clear();
            }
        }
    }
}
