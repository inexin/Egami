// Translated from libopus 1.5.2 silk/float/encode_frame_FLP.c, find_pitch_lags_FLP.c, noise_shape_analysis_FLP.c,
// find_pred_coefs_FLP.c, find_LPC_FLP.c, find_LTP_FLP.c, LTP_scale_ctrl_FLP.c, process_gains_FLP.c, wrappers_FLP.c and
// silk/process_NLSFs.c (float build).
using static Egami.Video.Audio.Codecs.Opus.SilkChannelDecoder;
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

internal sealed partial class SilkChannelEncoder
{
    private const int MaxRangeCopyBytes = 1275;

    private readonly SilkEncoderControl _control = new();
    private readonly SilkNsqState _nsqCopy = new();
    private readonly SilkNsqState _nsqCopy2 = new();
    private readonly SilkNsqState _nsqLbrr = new();
    private readonly byte[] _ecBufCopy = new byte[MaxRangeCopyBytes];

    /// <summary>silk_encode_do_VAD_FLP: runs the VAD on the buffered frame and sets the signal type, VAD and DTX flags.</summary>
    public void EncodeDoVad(int activity)
    {
        int activityThreshold = SilkEncoderQuant.FixConst(SpeechActivityDtxThres, 8);
        SpeechActivityQ8 = SilkVad.GetSpeechActivityQ8(SVad, InputBuf.AsSpan(1), FrameLength, FsKhz, out InputTiltQ15, InputQualityBandsQ15);
        if (activity == VadNoActivity && SpeechActivityQ8 >= activityThreshold)
        {
            SpeechActivityQ8 = activityThreshold - 1;
        }

        if (SpeechActivityQ8 < activityThreshold)
        {
            Indices.SignalType = TypeNoVoiceActivity;
            NoSpeechCounter++;
            if (NoSpeechCounter <= NbSpeechFramesBeforeDtx)
            {
                InDtx = 0;
            }
            else if (NoSpeechCounter > MaxConsecutiveDtx + NbSpeechFramesBeforeDtx)
            {
                NoSpeechCounter = NbSpeechFramesBeforeDtx;
                InDtx = 0;
            }

            VadFlags[NFramesEncoded] = 0;
        }
        else
        {
            NoSpeechCounter = 0;
            InDtx = 0;
            Indices.SignalType = TypeUnvoiced;
            VadFlags[NFramesEncoded] = 1;
        }
    }

    /// <summary>
    /// silk_encode_frame_FLP: analyses and encodes the buffered frame, searching the gains for a size within
    /// <paramref name="maxBits"/>. Returns the payload bytes so far.
    /// </summary>
    public int EncodeFrame(OpusRangeEncoder enc, int condCoding, int maxBits, bool useCbr)
    {
        SilkEncoderControl ctrl = _control;
        ctrl.Clear();
        int bitsMargin = useCbr ? 5 : maxBits / 4;
        sbyte lastGainIndexCopy2 = 0;
        int nBitsLower = 0;
        int nBitsUpper = 0;
        int gainMultLower = 0;
        int gainMultUpper = 0;
        Indices.Seed = (sbyte)(FrameCounter++ & 3);

        int xFrame = LtpMemLength;
        Span<float> resPitch = stackalloc float[(2 * MaxFrameLength) + LaPitchMax];
        int resPitchFrame = LtpMemLength;

        LpVariableCutoff(InputBuf.AsSpan(1), FrameLength);
        int inStart = xFrame + (LaShapeMs * FsKhz);
        SilkFloat.Short2FloatArray(XBuf.AsSpan(inStart), InputBuf.AsSpan(1), FrameLength);
        for (int i = 0; i < 8; i++)
        {
            XBuf[inStart + (i * (FrameLength >> 3))] += (1 - (i & 2)) * 1e-6f;
        }

        if (PrefillFlag == 0)
        {
            FindPitchLags(ctrl, resPitch);
            NoiseShapeAnalysis(ctrl, resPitch[resPitchFrame..], xFrame);
            FindPredCoefs(ctrl, resPitch, resPitchFrame, xFrame, condCoding);
            ProcessGains(ctrl, condCoding);
            LbrrEncode(ctrl, xFrame, condCoding);

            const int MaxIter = 6;
            short gainMultQ8 = 1 << 8;
            bool foundLower = false;
            bool foundUpper = false;
            int gainsId = SilkEncoderQuant.GainsId(Indices.GainsIndices, NbSubfr);
            int gainsIdLower = -1;
            int gainsIdUpper = -1;
            OpusRangeEncoder.EncoderState rangeCopy = enc.State;
            OpusRangeEncoder.EncoderState rangeCopy2 = default;
            _nsqCopy.CopyFrom(SNsq);
            sbyte seedCopy = Indices.Seed;
            short ecPrevLagIndexCopy = EcPrevLagIndex;
            int ecPrevSignalTypeCopy = EcPrevSignalType;
            Span<int> gainLock = stackalloc int[MaxNbSubfr];
            gainLock.Clear();
            Span<short> bestGainMult = stackalloc short[MaxNbSubfr];
            Span<int> bestSum = stackalloc int[MaxNbSubfr];
            Span<int> pGainsQ16 = stackalloc int[MaxNbSubfr];
            for (int iter = 0; ; iter++)
            {
                int nBits;
                if (gainsId == gainsIdLower)
                {
                    nBits = nBitsLower;
                }
                else if (gainsId == gainsIdUpper)
                {
                    nBits = nBitsUpper;
                }
                else
                {
                    if (iter > 0)
                    {
                        enc.State = rangeCopy;
                        SNsq.CopyFrom(_nsqCopy);
                        Indices.Seed = seedCopy;
                        EcPrevLagIndex = ecPrevLagIndexCopy;
                        EcPrevSignalType = ecPrevSignalTypeCopy;
                    }

                    NsqWrapper(ctrl, Indices, SNsq, Pulses, xFrame);
                    if (iter == MaxIter && !foundLower)
                    {
                        rangeCopy2 = enc.State;
                    }

                    EncodeIndices(enc, NFramesEncoded, false, condCoding);
                    SilkEntropyEncoder.EncodePulses(enc, Indices.SignalType, Indices.QuantOffsetType, Pulses, FrameLength);
                    nBits = enc.Tell();

                    if (iter == MaxIter && !foundLower && nBits > maxBits)
                    {
                        enc.State = rangeCopy2;
                        ShapeLastGainIndex = ctrl.LastGainIndexPrev;
                        for (int i = 0; i < NbSubfr; i++)
                        {
                            Indices.GainsIndices[i] = 4;
                        }

                        if (condCoding != CodeConditionally)
                        {
                            Indices.GainsIndices[0] = ctrl.LastGainIndexPrev;
                        }

                        EcPrevLagIndex = ecPrevLagIndexCopy;
                        EcPrevSignalType = ecPrevSignalTypeCopy;
                        Array.Clear(Pulses, 0, FrameLength);
                        EncodeIndices(enc, NFramesEncoded, false, condCoding);
                        SilkEntropyEncoder.EncodePulses(enc, Indices.SignalType, Indices.QuantOffsetType, Pulses, FrameLength);
                        nBits = enc.Tell();
                    }

                    if (!useCbr && iter == 0 && nBits <= maxBits)
                    {
                        break;
                    }
                }

                if (iter == MaxIter)
                {
                    if (foundLower && (gainsId == gainsIdLower || nBits > maxBits))
                    {
                        enc.State = rangeCopy2;
                        _ecBufCopy.AsSpan(0, (int)rangeCopy2.Offset).CopyTo(enc.Buffer.AsSpan(enc.Start));
                        SNsq.CopyFrom(_nsqCopy2);
                        ShapeLastGainIndex = lastGainIndexCopy2;
                    }

                    break;
                }

                if (nBits > maxBits)
                {
                    if (!foundLower && iter >= 2)
                    {
                        ctrl.Lambda = Math.Max(ctrl.Lambda * 1.5f, 1.5f);
                        Indices.QuantOffsetType = 0;
                        foundUpper = false;
                        gainsIdUpper = -1;
                    }
                    else
                    {
                        foundUpper = true;
                        nBitsUpper = nBits;
                        gainMultUpper = gainMultQ8;
                        gainsIdUpper = gainsId;
                    }
                }
                else if (nBits < maxBits - bitsMargin)
                {
                    foundLower = true;
                    nBitsLower = nBits;
                    gainMultLower = gainMultQ8;
                    if (gainsId != gainsIdLower)
                    {
                        gainsIdLower = gainsId;
                        rangeCopy2 = enc.State;
                        enc.Buffer.AsSpan(enc.Start, enc.RangeBytes).CopyTo(_ecBufCopy);
                        _nsqCopy2.CopyFrom(SNsq);
                        lastGainIndexCopy2 = ShapeLastGainIndex;
                    }
                }
                else
                {
                    break;
                }

                if (!foundLower && nBits > maxBits)
                {
                    for (int i = 0; i < NbSubfr; i++)
                    {
                        int sum = 0;
                        for (int j = i * SubfrLength; j < (i + 1) * SubfrLength; j++)
                        {
                            sum += Math.Abs((int)Pulses[j]);
                        }

                        if (iter == 0 || (sum < bestSum[i] && gainLock[i] == 0))
                        {
                            bestSum[i] = sum;
                            bestGainMult[i] = gainMultQ8;
                        }
                        else
                        {
                            gainLock[i] = 1;
                        }
                    }
                }

                if (!(foundLower && foundUpper))
                {
                    gainMultQ8 = nBits > maxBits
                        ? (short)Math.Min(1024, gainMultQ8 * 3 / 2)
                        : (short)Math.Max(64, gainMultQ8 * 4 / 5);
                }
                else
                {
                    gainMultQ8 = (short)(gainMultLower + ((gainMultUpper - gainMultLower) * (maxBits - nBitsLower) / (nBitsUpper - nBitsLower)));
                    if (gainMultQ8 > gainMultLower + ((gainMultUpper - gainMultLower) >> 2))
                    {
                        gainMultQ8 = (short)(gainMultLower + ((gainMultUpper - gainMultLower) >> 2));
                    }
                    else if (gainMultQ8 < gainMultUpper - ((gainMultUpper - gainMultLower) >> 2))
                    {
                        gainMultQ8 = (short)(gainMultUpper - ((gainMultUpper - gainMultLower) >> 2));
                    }
                }

                for (int i = 0; i < NbSubfr; i++)
                {
                    short tmp = gainLock[i] != 0 ? bestGainMult[i] : gainMultQ8;
                    pGainsQ16[i] = LShiftSat32(Smulwb(ctrl.GainsUnqQ16[i], tmp), 8);
                }

                ShapeLastGainIndex = ctrl.LastGainIndexPrev;
                SilkEncoderQuant.GainsQuant(Indices.GainsIndices, pGainsQ16, ref ShapeLastGainIndex, condCoding == CodeConditionally, NbSubfr);
                gainsId = SilkEncoderQuant.GainsId(Indices.GainsIndices, NbSubfr);
                for (int i = 0; i < NbSubfr; i++)
                {
                    ctrl.Gains[i] = pGainsQ16[i] / 65536.0f;
                }
            }
        }

        XBuf.AsSpan(FrameLength, LtpMemLength + (LaShapeMs * FsKhz)).CopyTo(XBuf);
        if (PrefillFlag != 0)
        {
            return 0;
        }

        PrevLag = ctrl.PitchL[NbSubfr - 1];
        PrevSignalType = Indices.SignalType;
        FirstFrameAfterReset = 0;
        return (enc.Tell() + 7) >> 3;
    }

    // silk_LBRR_encode_FLP: quantizes the frame again with higher gains for the in-band redundancy of the next packet.
    private void LbrrEncode(SilkEncoderControl ctrl, int xFrame, int condCoding)
    {
        if (LbrrEnabled != 0 && SpeechActivityQ8 > SilkEncoderQuant.FixConst(LbrrSpeechActivityThres, 8))
        {
            SilkSideInfoIndices indicesLbrr = IndicesLbrr[NFramesEncoded];
            LbrrFlags[NFramesEncoded] = 1;
            _nsqLbrr.CopyFrom(SNsq);
            indicesLbrr.CopyFrom(Indices);
            Span<float> tempGains = stackalloc float[MaxNbSubfr];
            ctrl.Gains.AsSpan(0, NbSubfr).CopyTo(tempGains);
            if (NFramesEncoded == 0 || LbrrFlags[NFramesEncoded - 1] == 0)
            {
                LbrrPrevLastGainIndex = ShapeLastGainIndex;
                indicesLbrr.GainsIndices[0] = (sbyte)(indicesLbrr.GainsIndices[0] + LbrrGainIncreases);
                indicesLbrr.GainsIndices[0] = (sbyte)Math.Min((int)indicesLbrr.GainsIndices[0], NLevelsQGain - 1);
            }

            Span<int> gainsQ16 = stackalloc int[MaxNbSubfr];
            SilkSigProc.GainsDequant(gainsQ16, indicesLbrr.GainsIndices, ref LbrrPrevLastGainIndex, condCoding == CodeConditionally, NbSubfr);
            for (int k = 0; k < NbSubfr; k++)
            {
                ctrl.Gains[k] = gainsQ16[k] * (1.0f / 65536.0f);
            }

            NsqWrapper(ctrl, indicesLbrr, _nsqLbrr, PulsesLbrr.AsSpan(NFramesEncoded * MaxFrameLength, MaxFrameLength), xFrame);
            tempGains[..NbSubfr].CopyTo(ctrl.Gains);
        }
    }

    // silk_NSQ_wrapper_FLP: converts the float parameters to the fixed-point quantizer's and runs it.
    private void NsqWrapper(SilkEncoderControl ctrl, SilkSideInfoIndices indices, SilkNsqState nsq, Span<sbyte> pulses, int xOffset)
    {
        Span<short> x16 = stackalloc short[MaxFrameLength];
        Span<int> gainsQ16 = stackalloc int[MaxNbSubfr];
        Span<short> predCoefQ12 = stackalloc short[2 * MaxLpcOrder];
        Span<short> ltpCoefQ14 = stackalloc short[LtpOrder * MaxNbSubfr];
        Span<short> arQ13 = stackalloc short[MaxNbSubfr * MaxShapeLpcOrder];
        Span<int> lfShpQ14 = stackalloc int[MaxNbSubfr];
        Span<int> tiltQ14 = stackalloc int[MaxNbSubfr];
        Span<int> harmShapeGainQ14 = stackalloc int[MaxNbSubfr];
        predCoefQ12.Clear();
        arQ13.Clear();
        for (int i = 0; i < NbSubfr; i++)
        {
            for (int j = 0; j < ShapingLpcOrder; j++)
            {
                arQ13[(i * MaxShapeLpcOrder) + j] = (short)SilkFloat.Float2Int(ctrl.Ar[(i * MaxShapeLpcOrder) + j] * 8192.0f);
            }
        }

        for (int i = 0; i < NbSubfr; i++)
        {
            lfShpQ14[i] = LShift(SilkFloat.Float2Int(ctrl.LfArShp[i] * 16384.0f), 16) | (ushort)SilkFloat.Float2Int(ctrl.LfMaShp[i] * 16384.0f);
            tiltQ14[i] = SilkFloat.Float2Int(ctrl.Tilt[i] * 16384.0f);
            harmShapeGainQ14[i] = SilkFloat.Float2Int(ctrl.HarmShapeGain[i] * 16384.0f);
        }

        int lambdaQ10 = SilkFloat.Float2Int(ctrl.Lambda * 1024.0f);
        for (int i = 0; i < NbSubfr * LtpOrder; i++)
        {
            ltpCoefQ14[i] = (short)SilkFloat.Float2Int(ctrl.LtpCoef[i] * 16384.0f);
        }

        for (int j = 0; j < 2; j++)
        {
            for (int i = 0; i < PredictLpcOrder; i++)
            {
                predCoefQ12[(j * MaxLpcOrder) + i] = (short)SilkFloat.Float2Int(ctrl.PredCoef[(j * MaxLpcOrder) + i] * 4096.0f);
            }
        }

        for (int i = 0; i < NbSubfr; i++)
        {
            gainsQ16[i] = SilkFloat.Float2Int(ctrl.Gains[i] * 65536.0f);
        }

        int ltpScaleQ14 = indices.SignalType == TypeVoiced ? SilkTables.LtpScalesTableQ14[indices.LtpScaleIndex] : 0;
        for (int i = 0; i < FrameLength; i++)
        {
            x16[i] = (short)SilkFloat.Float2Int(XBuf[xOffset + i]);
        }

        SilkNsq.Frame frame = new(LtpMemLength, FrameLength, SubfrLength, NbSubfr, PredictLpcOrder, ShapingLpcOrder, WarpingQ16, NStatesDelayedDecision);
        if (NStatesDelayedDecision > 1 || WarpingQ16 > 0)
        {
            SilkNsq.QuantizeDelDec(frame, nsq, indices, x16, pulses, predCoefQ12, ltpCoefQ14, arQ13, harmShapeGainQ14, tiltQ14, lfShpQ14, gainsQ16,
                ctrl.PitchL, lambdaQ10, ltpScaleQ14);
        }
        else
        {
            SilkNsq.Quantize(frame, nsq, indices, x16, pulses, predCoefQ12, ltpCoefQ14, arQ13, harmShapeGainQ14, tiltQ14, lfShpQ14, gainsQ16,
                ctrl.PitchL, lambdaQ10, ltpScaleQ14);
        }
    }

    // silk_find_pitch_lags_FLP: LPC whitening of the pitch analysis window and the pitch search.
    private void FindPitchLags(SilkEncoderControl ctrl, Span<float> res)
    {
        Span<float> autoCorr = stackalloc float[MaxFindPitchLpcOrder + 1];
        Span<float> a = stackalloc float[MaxFindPitchLpcOrder];
        Span<float> reflCoef = stackalloc float[MaxFindPitchLpcOrder];
        Span<float> wsig = stackalloc float[FindPitchLpcWinMax];
        int bufLen = LaPitch + FrameLength + LtpMemLength;
        int xBufPtr = bufLen - PitchLpcWinLength;
        int wsigPtr = 0;
        SilkFloat.ApplySineWindow(wsig, XBuf.AsSpan(xBufPtr), 1, LaPitch);
        wsigPtr += LaPitch;
        xBufPtr += LaPitch;
        int middle = PitchLpcWinLength - (LaPitch << 1);
        XBuf.AsSpan(xBufPtr, middle).CopyTo(wsig[wsigPtr..]);
        wsigPtr += middle;
        xBufPtr += middle;
        SilkFloat.ApplySineWindow(wsig[wsigPtr..], XBuf.AsSpan(xBufPtr), 2, LaPitch);

        SilkFloat.Autocorrelation(autoCorr, wsig, PitchLpcWinLength, PitchEstimationLpcOrder + 1);
        autoCorr[0] += (autoCorr[0] * FindPitchWhiteNoiseFraction) + 1;
        float resNrg = SilkFloat.Schur(reflCoef, autoCorr, PitchEstimationLpcOrder);
        ctrl.PredGain = autoCorr[0] / (resNrg > 1.0f ? resNrg : 1.0f);
        SilkFloat.K2A(a, reflCoef, PitchEstimationLpcOrder);
        SilkFloat.BwExpander(a, PitchEstimationLpcOrder, FindPitchBandwidthExpansion);
        SilkFloat.LpcAnalysisFilter(res, a, XBuf, 0, bufLen, PitchEstimationLpcOrder);

        if (Indices.SignalType != TypeNoVoiceActivity && FirstFrameAfterReset == 0)
        {
            float thrhld = 0.6f;
            thrhld -= 0.004f * PitchEstimationLpcOrder;
            thrhld -= 0.1f * SpeechActivityQ8 * (1.0f / 256.0f);
            thrhld -= 0.15f * (PrevSignalType >> 1);
            thrhld -= 0.1f * InputTiltQ15 * (1.0f / 32768.0f);
            int voiced = SilkPitchAnalysis.Analyze(res, ctrl.PitchL, out Indices.LagIndex, out Indices.ContourIndex, ref LtpCorr, PrevLag,
                PitchEstimationThresholdQ16 / 65536.0f, thrhld, FsKhz, PitchEstimationComplexity, NbSubfr);
            Indices.SignalType = voiced == 0 ? (sbyte)TypeVoiced : (sbyte)TypeUnvoiced;
        }
        else
        {
            Array.Clear(ctrl.PitchL);
            Indices.LagIndex = 0;
            Indices.ContourIndex = 0;
            LtpCorr = 0;
        }
    }

    // silk_noise_shape_analysis_FLP: the noise shaping filters, gains, tilt, low-frequency and harmonic shaping of each subframe.
    private void NoiseShapeAnalysis(SilkEncoderControl ctrl, ReadOnlySpan<float> pitchRes, int xFrame)
    {
        Span<float> xWindowed = stackalloc float[ShapeLpcWinMax];
        Span<float> autoCorr = stackalloc float[MaxShapeLpcOrder + 1];
        Span<float> rc = stackalloc float[MaxShapeLpcOrder + 1];
        int xPtr = xFrame - LaShape;

        float snrAdjDb = SnrDbQ7 * (1 / 128.0f);
        ctrl.InputQuality = 0.5f * (InputQualityBandsQ15[0] + InputQualityBandsQ15[1]) * (1.0f / 32768.0f);
        ctrl.CodingQuality = SilkFloat.Sigmoid(0.25f * (snrAdjDb - 20.0f));
        if (UseCbr == 0)
        {
            float b0 = 1.0f - (SpeechActivityQ8 * (1.0f / 256.0f));
            snrAdjDb -= BgSnrDecrDb * ctrl.CodingQuality * (0.5f + (0.5f * ctrl.InputQuality)) * b0 * b0;
        }

        if (Indices.SignalType == TypeVoiced)
        {
            snrAdjDb += HarmSnrIncrDb * LtpCorr;
        }
        else
        {
            snrAdjDb += ((-0.4f * SnrDbQ7 * (1 / 128.0f)) + 6.0f) * (1.0f - ctrl.InputQuality);
        }

        if (Indices.SignalType == TypeVoiced)
        {
            Indices.QuantOffsetType = 0;
        }
        else
        {
            int nSamples = 2 * FsKhz;
            float energyVariation = 0.0f;
            float logEnergyPrev = 0.0f;
            int pitchResPtr = 0;
            int nSegs = Smulbb(SubFrameLengthMs, NbSubfr) / 2;
            for (int k = 0; k < nSegs; k++)
            {
                float nrg = nSamples + (float)SilkFloat.Energy(pitchRes[pitchResPtr..], nSamples);
                float logEnergy = SilkFloat.Log2(nrg);
                if (k > 0)
                {
                    energyVariation += MathF.Abs(logEnergy - logEnergyPrev);
                }

                logEnergyPrev = logEnergy;
                pitchResPtr += nSamples;
            }

            Indices.QuantOffsetType = energyVariation > EnergyVariationThresholdQntOffset * (nSegs - 1) ? (sbyte)0 : (sbyte)1;
        }

        float strength = FindPitchWhiteNoiseFraction * ctrl.PredGain;
        float bwExp = BandwidthExpansion / (1.0f + (strength * strength));
        float warping = (WarpingQ16 / 65536.0f) + (0.01f * ctrl.CodingQuality);

        for (int k = 0; k < NbSubfr; k++)
        {
            int flatPart = FsKhz * 3;
            int slopePart = (ShapeWinLength - flatPart) / 2;
            SilkFloat.ApplySineWindow(xWindowed, XBuf.AsSpan(xPtr), 1, slopePart);
            int shift = slopePart;
            XBuf.AsSpan(xPtr + shift, flatPart).CopyTo(xWindowed[shift..]);
            shift += flatPart;
            SilkFloat.ApplySineWindow(xWindowed[shift..], XBuf.AsSpan(xPtr + shift), 2, slopePart);
            xPtr += SubfrLength;

            if (WarpingQ16 > 0)
            {
                SilkFloat.WarpedAutocorrelation(autoCorr, xWindowed, warping, ShapeWinLength, ShapingLpcOrder);
            }
            else
            {
                SilkFloat.Autocorrelation(autoCorr, xWindowed, ShapeWinLength, ShapingLpcOrder + 1);
            }

            autoCorr[0] += (autoCorr[0] * ShapeWhiteNoiseFraction) + 1.0f;
            float nrg = SilkFloat.Schur(rc, autoCorr, ShapingLpcOrder);
            Span<float> ar = ctrl.Ar.AsSpan(k * MaxShapeLpcOrder, MaxShapeLpcOrder);
            SilkFloat.K2A(ar, rc, ShapingLpcOrder);
            ctrl.Gains[k] = (float)Math.Sqrt(nrg);
            if (WarpingQ16 > 0)
            {
                ctrl.Gains[k] *= WarpedGain(ar, warping, ShapingLpcOrder);
            }

            SilkFloat.BwExpander(ar, ShapingLpcOrder, bwExp);
            if (WarpingQ16 > 0)
            {
                WarpedTrue2MonicCoefs(ar, warping, 3.999f, ShapingLpcOrder);
            }
            else
            {
                LimitCoefs(ar, 3.999f, ShapingLpcOrder);
            }
        }

        float gainMult = (float)Math.Pow(2.0, -0.16f * snrAdjDb);
        float gainAdd = (float)Math.Pow(2.0, 0.16f * MinQGainDb);
        for (int k = 0; k < NbSubfr; k++)
        {
            ctrl.Gains[k] *= gainMult;
            ctrl.Gains[k] += gainAdd;
        }

        strength = LowFreqShaping * (1.0f + (LowQualityLowFreqShapingDecr * ((InputQualityBandsQ15[0] * (1.0f / 32768.0f)) - 1.0f)));
        strength *= SpeechActivityQ8 * (1.0f / 256.0f);
        float tilt;
        if (Indices.SignalType == TypeVoiced)
        {
            for (int k = 0; k < NbSubfr; k++)
            {
                float b = (0.2f / FsKhz) + (3.0f / ctrl.PitchL[k]);
                ctrl.LfMaShp[k] = -1.0f + b;
                ctrl.LfArShp[k] = 1.0f - b - (b * strength);
            }

            tilt = -HpNoiseCoef - ((1 - HpNoiseCoef) * HarmHpNoiseCoef * SpeechActivityQ8 * (1.0f / 256.0f));
        }
        else
        {
            float b = 1.3f / FsKhz;
            ctrl.LfMaShp[0] = -1.0f + b;
            ctrl.LfArShp[0] = 1.0f - b - (b * strength * 0.6f);
            for (int k = 1; k < NbSubfr; k++)
            {
                ctrl.LfMaShp[k] = ctrl.LfMaShp[0];
                ctrl.LfArShp[k] = ctrl.LfArShp[0];
            }

            tilt = -HpNoiseCoef;
        }

        float harmShapeGain;
        if (Indices.SignalType == TypeVoiced)
        {
            harmShapeGain = HarmonicShaping;
            harmShapeGain += HighRateOrLowQualityHarmonicShaping * (1.0f - ((1.0f - ctrl.CodingQuality) * ctrl.InputQuality));
            harmShapeGain *= (float)Math.Sqrt(LtpCorr);
        }
        else
        {
            harmShapeGain = 0.0f;
        }

        for (int k = 0; k < NbSubfr; k++)
        {
            HarmShapeGainSmth += SubfrSmthCoef * (harmShapeGain - HarmShapeGainSmth);
            ctrl.HarmShapeGain[k] = HarmShapeGainSmth;
            TiltSmth += SubfrSmthCoef * (tilt - TiltSmth);
            ctrl.Tilt[k] = TiltSmth;
        }
    }

    private static float WarpedGain(ReadOnlySpan<float> coefs, float lambda, int order)
    {
        lambda = -lambda;
        float gain = coefs[order - 1];
        for (int i = order - 2; i >= 0; i--)
        {
            gain = (lambda * gain) + coefs[i];
        }

        return 1.0f / (1.0f - (lambda * gain));
    }

    private static void WarpedTrue2MonicCoefs(Span<float> coefs, float lambda, float limit, int order)
    {
        int ind = 0;
        for (int i = order - 1; i > 0; i--)
        {
            coefs[i - 1] -= lambda * coefs[i];
        }

        float gain = (1.0f - (lambda * lambda)) / (1.0f + (lambda * coefs[0]));
        for (int i = 0; i < order; i++)
        {
            coefs[i] *= gain;
        }

        for (int iter = 0; iter < 10; iter++)
        {
            float maxabs = -1.0f;
            for (int i = 0; i < order; i++)
            {
                float tmp = MathF.Abs(coefs[i]);
                if (tmp > maxabs)
                {
                    maxabs = tmp;
                    ind = i;
                }
            }

            if (maxabs <= limit)
            {
                return;
            }

            for (int i = 1; i < order; i++)
            {
                coefs[i - 1] += lambda * coefs[i];
            }

            gain = 1.0f / gain;
            for (int i = 0; i < order; i++)
            {
                coefs[i] *= gain;
            }

            float chirp = 0.99f - ((0.8f + (0.1f * iter)) * (maxabs - limit) / (maxabs * (ind + 1)));
            SilkFloat.BwExpander(coefs, order, chirp);
            for (int i = order - 1; i > 0; i--)
            {
                coefs[i - 1] -= lambda * coefs[i];
            }

            gain = (1.0f - (lambda * lambda)) / (1.0f + (lambda * coefs[0]));
            for (int i = 0; i < order; i++)
            {
                coefs[i] *= gain;
            }
        }
    }

    private static void LimitCoefs(Span<float> coefs, float limit, int order)
    {
        int ind = 0;
        for (int iter = 0; iter < 10; iter++)
        {
            float maxabs = -1.0f;
            for (int i = 0; i < order; i++)
            {
                float tmp = MathF.Abs(coefs[i]);
                if (tmp > maxabs)
                {
                    maxabs = tmp;
                    ind = i;
                }
            }

            if (maxabs <= limit)
            {
                return;
            }

            float chirp = 0.99f - ((0.8f + (0.1f * iter)) * (maxabs - limit) / (maxabs * (ind + 1)));
            SilkFloat.BwExpander(coefs, order, chirp);
        }
    }

    // silk_find_pred_coefs_FLP: LTP and LPC coefficients, their quantization, and the residual energies.
    private void FindPredCoefs(SilkEncoderControl ctrl, ReadOnlySpan<float> resPitch, int resPitchFrame, int xFrame, int condCoding)
    {
        Span<float> xxLtp = stackalloc float[MaxNbSubfr * LtpOrder * LtpOrder];
        Span<float> xXLtp = stackalloc float[MaxNbSubfr * LtpOrder];
        Span<float> invGains = stackalloc float[MaxNbSubfr];
        Span<short> nlsfQ15 = stackalloc short[MaxLpcOrder];
        Span<float> lpcInPre = stackalloc float[(MaxNbSubfr * MaxLpcOrder) + MaxFrameLength];
        nlsfQ15.Clear();
        for (int i = 0; i < NbSubfr; i++)
        {
            invGains[i] = 1.0f / ctrl.Gains[i];
        }

        if (Indices.SignalType == TypeVoiced)
        {
            FindLtp(xxLtp, xXLtp, resPitch, resPitchFrame, ctrl.PitchL, SubfrLength, NbSubfr);
            QuantLtpGains(ctrl.LtpCoef, Indices.LtpIndex, out Indices.PerIndex, ref SumLogGainQ7, out ctrl.LtpPredCodGain, xxLtp, xXLtp, SubfrLength, NbSubfr);
            LtpScaleCtrl(ctrl, condCoding);
            SilkFloat.LtpAnalysisFilter(lpcInPre, XBuf, xFrame - PredictLpcOrder, ctrl.LtpCoef, ctrl.PitchL, invGains, SubfrLength, NbSubfr, PredictLpcOrder);
        }
        else
        {
            int xPtr = xFrame - PredictLpcOrder;
            int xPrePtr = 0;
            for (int i = 0; i < NbSubfr; i++)
            {
                SilkFloat.ScaleCopyVector(lpcInPre[xPrePtr..], XBuf.AsSpan(xPtr), invGains[i], SubfrLength + PredictLpcOrder);
                xPrePtr += SubfrLength + PredictLpcOrder;
                xPtr += SubfrLength;
            }

            Array.Clear(ctrl.LtpCoef, 0, NbSubfr * LtpOrder);
            ctrl.LtpPredCodGain = 0.0f;
            SumLogGainQ7 = 0;
        }

        float minInvGain;
        if (FirstFrameAfterReset != 0)
        {
            minInvGain = 1.0f / MaxPredictionPowerGainAfterReset;
        }
        else
        {
            minInvGain = (float)Math.Pow(2, ctrl.LtpPredCodGain / 3) / MaxPredictionPowerGain;
            minInvGain /= 0.25f + (0.75f * ctrl.CodingQuality);
        }

        FindLpc(nlsfQ15, lpcInPre, minInvGain);
        ProcessNlsfs(ctrl.PredCoef, nlsfQ15, PrevNlsfqQ15);
        SilkFloat.ResidualEnergy(ctrl.ResNrg, lpcInPre, ctrl.PredCoefHalf(0), ctrl.PredCoefHalf(1), ctrl.Gains, SubfrLength, NbSubfr, PredictLpcOrder);
        nlsfQ15.CopyTo(PrevNlsfqQ15);
    }

    // silk_find_LPC_FLP: Burg's LPC of the frame, or of its second half interpolated from the previous frame's NLSFs.
    private void FindLpc(Span<short> nlsfQ15, ReadOnlySpan<float> x, float minInvGain)
    {
        Span<float> a = stackalloc float[MaxLpcOrder];
        Span<short> nlsf0Q15 = stackalloc short[MaxLpcOrder];
        Span<float> aTmp = stackalloc float[MaxLpcOrder];
        Span<float> lpcRes = stackalloc float[MaxFrameLength + (MaxNbSubfr * MaxLpcOrder)];
        int subfrLength = SubfrLength + PredictLpcOrder;
        Indices.NlsfInterpCoefQ2 = 4;
        float resNrg = SilkFloat.BurgModified(a, x, minInvGain, subfrLength, NbSubfr, PredictLpcOrder);
        if (UseInterpolatedNlsfs != 0 && FirstFrameAfterReset == 0 && NbSubfr == MaxNbSubfr)
        {
            resNrg -= SilkFloat.BurgModified(aTmp, x[((MaxNbSubfr / 2) * subfrLength)..], minInvGain, subfrLength, MaxNbSubfr / 2, PredictLpcOrder);
            SilkFloat.A2Nlsf(nlsfQ15, aTmp, PredictLpcOrder);
            float resNrg2nd = float.MaxValue;
            for (int k = 3; k >= 0; k--)
            {
                SilkEncoderQuant.Interpolate(nlsf0Q15, PrevNlsfqQ15, nlsfQ15, k, PredictLpcOrder);
                SilkFloat.Nlsf2A(aTmp, nlsf0Q15, PredictLpcOrder);
                SilkFloat.LpcAnalysisFilter(lpcRes, aTmp, x, 0, 2 * subfrLength, PredictLpcOrder);
                float resNrgInterp = (float)(SilkFloat.Energy(lpcRes[PredictLpcOrder..], subfrLength - PredictLpcOrder) +
                    SilkFloat.Energy(lpcRes[(PredictLpcOrder + subfrLength)..], subfrLength - PredictLpcOrder));
                if (resNrgInterp < resNrg)
                {
                    resNrg = resNrgInterp;
                    Indices.NlsfInterpCoefQ2 = (sbyte)k;
                }
                else if (resNrgInterp > resNrg2nd)
                {
                    break;
                }

                resNrg2nd = resNrgInterp;
            }
        }

        if (Indices.NlsfInterpCoefQ2 == 4)
        {
            SilkFloat.A2Nlsf(nlsfQ15, a, PredictLpcOrder);
        }
    }

    // silk_find_LTP_FLP: the normalized correlation matrices and vectors of the LTP analysis.
    private static void FindLtp(Span<float> xx, Span<float> xX, ReadOnlySpan<float> r, int rPtr, ReadOnlySpan<int> lag, int subfrLength, int nbSubfr)
    {
        int xXPtr = 0;
        int xxPtr = 0;
        for (int k = 0; k < nbSubfr; k++)
        {
            ReadOnlySpan<float> lagSpan = r[(rPtr - (lag[k] + (LtpOrder / 2)))..];
            Span<float> xxK = xx.Slice(xxPtr, LtpOrder * LtpOrder);
            Span<float> xXK = xX.Slice(xXPtr, LtpOrder);
            SilkFloat.CorrMatrix(lagSpan, subfrLength, LtpOrder, xxK);
            SilkFloat.CorrVector(lagSpan, r[rPtr..], subfrLength, LtpOrder, xXK);
            float energy = (float)SilkFloat.Energy(r[rPtr..], subfrLength + LtpOrder);
            float floor = (LtpCorrInvMax * 0.5f * (xxK[0] + xxK[24])) + 1.0f;
            float temp = 1.0f / (energy > floor ? energy : floor);
            SilkFloat.ScaleVector(xxK, temp, LtpOrder * LtpOrder);
            SilkFloat.ScaleVector(xXK, temp, LtpOrder);
            rPtr += subfrLength;
            xxPtr += LtpOrder * LtpOrder;
            xXPtr += LtpOrder;
        }
    }

    // silk_quant_LTP_gains_FLP.
    private static void QuantLtpGains(Span<float> b, Span<sbyte> cbkIndex, out sbyte periodicityIndex, ref int sumLogGainQ7, out float predGainDb,
        ReadOnlySpan<float> xx, ReadOnlySpan<float> xX, int subfrLen, int nbSubfr)
    {
        Span<short> bQ14 = stackalloc short[MaxNbSubfr * LtpOrder];
        Span<int> xxQ17 = stackalloc int[MaxNbSubfr * LtpOrder * LtpOrder];
        Span<int> xXQ17 = stackalloc int[MaxNbSubfr * LtpOrder];
        for (int i = 0; i < nbSubfr * LtpOrder * LtpOrder; i++)
        {
            xxQ17[i] = SilkFloat.Float2Int(xx[i] * 131072.0f);
        }

        for (int i = 0; i < nbSubfr * LtpOrder; i++)
        {
            xXQ17[i] = SilkFloat.Float2Int(xX[i] * 131072.0f);
        }

        SilkEncoderQuant.QuantLtpGains(bQ14, cbkIndex, out periodicityIndex, ref sumLogGainQ7, out int predGainDbQ7, xxQ17, xXQ17, subfrLen, nbSubfr);
        for (int i = 0; i < nbSubfr * LtpOrder; i++)
        {
            b[i] = bQ14[i] * (1.0f / 16384.0f);
        }

        predGainDb = predGainDbQ7 * (1.0f / 128.0f);
    }

    // silk_LTP_scale_ctrl_FLP: how much to scale down the LTP state at the start of an independently coded frame.
    private void LtpScaleCtrl(SilkEncoderControl ctrl, int condCoding)
    {
        if (condCoding == CodeIndependently)
        {
            int roundLoss = PacketLossPerc * NFramesPerPacket;
            if (LbrrFlag != 0)
            {
                roundLoss = 2 + (Smulbb(roundLoss, roundLoss) / 100);
            }

            // silk_SMULBB of the float gain truncates it to an integer first, like the C conversion.
            int gain = (int)ctrl.LtpPredCodGain;
            Indices.LtpScaleIndex = (sbyte)(Smulbb(gain, roundLoss) > SilkSigProc.Log2Lin(2900 - SnrDbQ7) ? 1 : 0);
            Indices.LtpScaleIndex += (sbyte)(Smulbb(gain, roundLoss) > SilkSigProc.Log2Lin(3900 - SnrDbQ7) ? 1 : 0);
        }
        else
        {
            Indices.LtpScaleIndex = 0;
        }

        ctrl.LtpScale = SilkTables.LtpScalesTableQ14[Indices.LtpScaleIndex] / 16384.0f;
    }

    // silk_process_gains_FLP: gain adjustment, quantization, quantizer offset type and rate-distortion lambda.
    private void ProcessGains(SilkEncoderControl ctrl, int condCoding)
    {
        Span<int> pGainsQ16 = stackalloc int[MaxNbSubfr];
        if (Indices.SignalType == TypeVoiced)
        {
            float s = 1.0f - (0.5f * SilkFloat.Sigmoid(0.25f * (ctrl.LtpPredCodGain - 12.0f)));
            for (int k = 0; k < NbSubfr; k++)
            {
                ctrl.Gains[k] *= s;
            }
        }

        float invMaxSqrVal = (float)(Math.Pow(2.0, 0.33f * (21.0f - (SnrDbQ7 * (1 / 128.0f)))) / SubfrLength);
        for (int k = 0; k < NbSubfr; k++)
        {
            float gain = ctrl.Gains[k];
            gain = (float)Math.Sqrt((gain * gain) + (ctrl.ResNrg[k] * invMaxSqrVal));
            ctrl.Gains[k] = gain < 32767.0f ? gain : 32767.0f;
        }

        for (int k = 0; k < NbSubfr; k++)
        {
            pGainsQ16[k] = (int)(ctrl.Gains[k] * 65536.0f);
        }

        pGainsQ16[..NbSubfr].CopyTo(ctrl.GainsUnqQ16);
        ctrl.LastGainIndexPrev = ShapeLastGainIndex;
        SilkEncoderQuant.GainsQuant(Indices.GainsIndices, pGainsQ16, ref ShapeLastGainIndex, condCoding == CodeConditionally, NbSubfr);
        for (int k = 0; k < NbSubfr; k++)
        {
            ctrl.Gains[k] = pGainsQ16[k] / 65536.0f;
        }

        if (Indices.SignalType == TypeVoiced)
        {
            Indices.QuantOffsetType = ctrl.LtpPredCodGain + (InputTiltQ15 * (1.0f / 32768.0f)) > 1.0f ? (sbyte)0 : (sbyte)1;
        }

        float quantOffset = SilkTables.QuantizationOffsetsQ10[((Indices.SignalType >> 1) * 2) + Indices.QuantOffsetType] / 1024.0f;
        ctrl.Lambda = LambdaOffset
            + (LambdaDelayedDecisions * NStatesDelayedDecision)
            + (LambdaSpeechAct * SpeechActivityQ8 * (1.0f / 256.0f))
            + (LambdaInputQuality * ctrl.InputQuality)
            + (LambdaCodingQuality * ctrl.CodingQuality)
            + (LambdaQuantOffset * quantOffset);
    }

    // silk_process_NLSFs and its FLP wrapper: NLSF quantization and the (interpolated) prediction coefficients.
    private void ProcessNlsfs(Span<float> predCoef, Span<short> pNlsfQ15, ReadOnlySpan<short> prevNlsfqQ15)
    {
        Span<short> predCoefQ12 = stackalloc short[2 * MaxLpcOrder];
        Span<short> pNlsf0TempQ15 = stackalloc short[MaxLpcOrder];
        Span<short> pNlsfwQw = stackalloc short[MaxLpcOrder];
        Span<short> pNlsfw0TempQw = stackalloc short[MaxLpcOrder];
        int nlsfMuQ20 = Smlawb(SilkEncoderQuant.FixConst(0.003, 20), SilkEncoderQuant.FixConst(-0.001, 28), SpeechActivityQ8);
        if (NbSubfr == 2)
        {
            nlsfMuQ20 += nlsfMuQ20 >> 1;
        }

        SilkEncoderQuant.NlsfVqWeightsLaroia(pNlsfwQw, pNlsfQ15, PredictLpcOrder);
        bool doInterpolate = UseInterpolatedNlsfs == 1 && Indices.NlsfInterpCoefQ2 < 4;
        if (doInterpolate)
        {
            SilkEncoderQuant.Interpolate(pNlsf0TempQ15, prevNlsfqQ15, pNlsfQ15, Indices.NlsfInterpCoefQ2, PredictLpcOrder);
            SilkEncoderQuant.NlsfVqWeightsLaroia(pNlsfw0TempQw, pNlsf0TempQ15, PredictLpcOrder);
            short iSqrQ15 = (short)LShift(Smulbb(Indices.NlsfInterpCoefQ2, Indices.NlsfInterpCoefQ2), 11);
            for (int i = 0; i < PredictLpcOrder; i++)
            {
                pNlsfwQw[i] = (short)((pNlsfwQw[i] >> 1) + (Smulbb(pNlsfw0TempQw[i], iSqrQ15) >> 16));
            }
        }

        SilkEncoderQuant.NlsfEncode(Indices.NlsfIndices, pNlsfQ15, NlsfCodebook!, pNlsfwQw, nlsfMuQ20, NlsfMsvqSurvivors, Indices.SignalType);
        SilkSigProc.Nlsf2A(predCoefQ12.Slice(MaxLpcOrder, MaxLpcOrder), pNlsfQ15, PredictLpcOrder);
        if (doInterpolate)
        {
            SilkEncoderQuant.Interpolate(pNlsf0TempQ15, prevNlsfqQ15, pNlsfQ15, Indices.NlsfInterpCoefQ2, PredictLpcOrder);
            SilkSigProc.Nlsf2A(predCoefQ12[..MaxLpcOrder], pNlsf0TempQ15, PredictLpcOrder);
        }
        else
        {
            predCoefQ12.Slice(MaxLpcOrder, PredictLpcOrder).CopyTo(predCoefQ12);
        }

        for (int j = 0; j < 2; j++)
        {
            for (int i = 0; i < PredictLpcOrder; i++)
            {
                predCoef[(j * MaxLpcOrder) + i] = predCoefQ12[(j * MaxLpcOrder) + i] * (1.0f / 4096.0f);
            }
        }
    }
}
