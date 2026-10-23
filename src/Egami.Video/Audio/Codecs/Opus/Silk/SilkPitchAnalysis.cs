// Translated from libopus 1.5.2 silk/float/pitch_analysis_core_FLP.c and silk/pitch_est_defines.h.
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's pitch estimator: a coarse search at 4 kHz, a refinement at 8 kHz and a final search at the internal rate.</summary>
internal static class SilkPitchAnalysis
{
    public const int PeMinComplex = 0;
    public const int PeMidComplex = 1;
    public const int PeMaxComplex = 2;
    private const int PeMaxNbSubfr = 4;
    private const int PeSubfrLengthMs = 5;
    private const int PeLtpMemLengthMs = 4 * PeSubfrLengthMs;
    private const int PeMaxFrameLengthMs = PeLtpMemLengthMs + (PeMaxNbSubfr * PeSubfrLengthMs);
    private const int PeMaxLagMs = 18;
    private const int PeMinLagMs = 2;
    private const int PeMaxLag = PeMaxLagMs * 16;
    private const int PeDSrchLength = 24;
    private const int PeNbStage3Lags = 5;
    private const int PeNbCbksStage2 = 3;
    private const int PeNbCbksStage2Ext = 11;
    private const int PeNbCbksStage3Max = 34;
    private const int PeNbCbksStage310Ms = 12;
    private const int PeNbCbksStage210Ms = 3;
    private const float PeShortlagBias = 0.2f;
    private const float PePrevlagBias = 0.2f;
    private const float PeFlatcontourBias = 0.05f;
    private const int ScratchSize = 22;
    private const int CRow = (PeMaxLag >> 1) + 5;
    private const int St3Size = PeMaxNbSubfr * PeNbCbksStage3Max * PeNbStage3Lags;

    /// <summary>silk_pitch_analysis_core_FLP: returns 0 for voiced and 1 for unvoiced.</summary>
    public static int Analyze(ReadOnlySpan<float> frame, Span<int> pitchOut, out short lagIndex, out sbyte contourIndex, ref float ltpCorr, int prevLag,
        float searchThres1, float searchThres2, int fsKhz, int complexity, int nbSubfr)
    {
        Span<float> frame8kHz = stackalloc float[PeMaxFrameLengthMs * 8];
        Span<float> frame4kHz = stackalloc float[PeMaxFrameLengthMs * 4];
        Span<short> frame8Fix = stackalloc short[PeMaxFrameLengthMs * 8];
        Span<short> frame4Fix = stackalloc short[PeMaxFrameLengthMs * 4];
        Span<int> filtState = stackalloc int[6];
        Span<float> c = stackalloc float[PeMaxNbSubfr * CRow];
        Span<float> xcorr = stackalloc float[(PeMaxLagMs * 4) - (PeMinLagMs * 4) + 1];
        Span<float> cc = stackalloc float[PeNbCbksStage2Ext];
        Span<int> dSrch = stackalloc int[PeDSrchLength];
        Span<short> dComp = stackalloc short[CRow];

        int frameLength = (PeLtpMemLengthMs + (nbSubfr * PeSubfrLengthMs)) * fsKhz;
        int frameLength4kHz = (PeLtpMemLengthMs + (nbSubfr * PeSubfrLengthMs)) * 4;
        int frameLength8kHz = (PeLtpMemLengthMs + (nbSubfr * PeSubfrLengthMs)) * 8;
        int sfLength = PeSubfrLengthMs * fsKhz;
        int sfLength4kHz = PeSubfrLengthMs * 4;
        int sfLength8kHz = PeSubfrLengthMs * 8;
        int minLag = PeMinLagMs * fsKhz;
        int minLag4kHz = PeMinLagMs * 4;
        int minLag8kHz = PeMinLagMs * 8;
        int maxLag = (PeMaxLagMs * fsKhz) - 1;
        int maxLag4kHz = PeMaxLagMs * 4;
        int maxLag8kHz = (PeMaxLagMs * 8) - 1;

        // Resample to 8 kHz.
        if (fsKhz == 16)
        {
            Span<short> frame16Fix = stackalloc short[16 * PeMaxFrameLengthMs];
            SilkFloat.Float2ShortArray(frame16Fix, frame, frameLength);
            filtState[..2].Clear();
            SilkResampler.Down2(filtState, frame8Fix, frame16Fix, frameLength);
            SilkFloat.Short2FloatArray(frame8kHz, frame8Fix, frameLength8kHz);
        }
        else if (fsKhz == 12)
        {
            Span<short> frame12Fix = stackalloc short[12 * PeMaxFrameLengthMs];
            SilkFloat.Float2ShortArray(frame12Fix, frame, frameLength);
            filtState.Clear();
            SilkResampler.Down23(filtState, frame8Fix, frame12Fix, frameLength);
            SilkFloat.Short2FloatArray(frame8kHz, frame8Fix, frameLength8kHz);
        }
        else
        {
            SilkFloat.Float2ShortArray(frame8Fix, frame, frameLength8kHz);
        }

        // Decimate again to 4 kHz and low-pass filter.
        filtState[..2].Clear();
        SilkResampler.Down2(filtState, frame4Fix, frame8Fix, frameLength8kHz);
        SilkFloat.Short2FloatArray(frame4kHz, frame4Fix, frameLength4kHz);
        for (int i = frameLength4kHz - 1; i > 0; i--)
        {
            frame4kHz[i] = AddSat16((int)frame4kHz[i], (int)frame4kHz[i - 1]);
        }

        // First stage, at 4 kHz.
        c[..(nbSubfr * CRow)].Clear();
        int target = sfLength4kHz << 2;
        double crossCorr, normalizer, energy, energyTmp;
        for (int k = 0; k < nbSubfr >> 1; k++)
        {
            int basis = target - minLag4kHz;
            CeltMath.PitchXcorr(frame4kHz[target..], frame4kHz[(target - maxLag4kHz)..], xcorr, sfLength8kHz, maxLag4kHz - minLag4kHz + 1);
            crossCorr = xcorr[maxLag4kHz - minLag4kHz];
            normalizer = SilkFloat.Energy(frame4kHz[target..], sfLength8kHz) + SilkFloat.Energy(frame4kHz[basis..], sfLength8kHz) + (sfLength8kHz * 4000.0f);
            c[minLag4kHz] += (float)(2 * crossCorr / normalizer);
            for (int d = minLag4kHz + 1; d <= maxLag4kHz; d++)
            {
                basis--;
                crossCorr = xcorr[maxLag4kHz - d];
                normalizer += (frame4kHz[basis] * (double)frame4kHz[basis]) - (frame4kHz[basis + sfLength8kHz] * (double)frame4kHz[basis + sfLength8kHz]);
                c[d] += (float)(2 * crossCorr / normalizer);
            }

            target += sfLength8kHz;
        }

        // Short-lag bias.
        for (int i = maxLag4kHz; i >= minLag4kHz; i--)
        {
            c[i] -= c[i] * i / 4096.0f;
        }

        int lengthDSrch = 4 + (2 * complexity);
        SilkFloat.InsertionSortDecreasing(c[minLag4kHz..], dSrch, maxLag4kHz - minLag4kHz + 1, lengthDSrch);
        float cmax = c[minLag4kHz];
        if (cmax < 0.2f)
        {
            pitchOut[..nbSubfr].Clear();
            ltpCorr = 0.0f;
            lagIndex = 0;
            contourIndex = 0;
            return 1;
        }

        float threshold = searchThres1 * cmax;
        for (int i = 0; i < lengthDSrch; i++)
        {
            if (c[minLag4kHz + i] > threshold)
            {
                dSrch[i] = (dSrch[i] + minLag4kHz) << 1;
            }
            else
            {
                lengthDSrch = i;
                break;
            }
        }

        for (int i = minLag8kHz - 5; i < maxLag8kHz + 5; i++)
        {
            dComp[i] = 0;
        }

        for (int i = 0; i < lengthDSrch; i++)
        {
            dComp[dSrch[i]] = 1;
        }

        for (int i = maxLag8kHz + 3; i >= minLag8kHz; i--)
        {
            dComp[i] += (short)(dComp[i - 1] + dComp[i - 2]);
        }

        lengthDSrch = 0;
        for (int i = minLag8kHz; i < maxLag8kHz + 1; i++)
        {
            if (dComp[i + 1] > 0)
            {
                dSrch[lengthDSrch] = i;
                lengthDSrch++;
            }
        }

        for (int i = maxLag8kHz + 3; i >= minLag8kHz; i--)
        {
            dComp[i] += (short)(dComp[i - 1] + dComp[i - 2] + dComp[i - 3]);
        }

        int lengthDComp = 0;
        for (int i = minLag8kHz; i < maxLag8kHz + 4; i++)
        {
            if (dComp[i] > 0)
            {
                dComp[lengthDComp] = (short)(i - 2);
                lengthDComp++;
            }
        }

        // Second stage, at 8 kHz, on the lag sections with high correlation.
        c.Clear();
        ReadOnlySpan<float> src8 = fsKhz == 8 ? frame : frame8kHz;
        target = PeLtpMemLengthMs * 8;
        for (int k = 0; k < nbSubfr; k++)
        {
            energyTmp = SilkFloat.Energy(src8[target..], sfLength8kHz) + 1.0;
            for (int j = 0; j < lengthDComp; j++)
            {
                int d = dComp[j];
                int basis = target - d;
                crossCorr = SilkFloat.InnerProduct(src8[basis..], src8[target..], sfLength8kHz);
                if (crossCorr > 0.0f)
                {
                    energy = SilkFloat.Energy(src8[basis..], sfLength8kHz);
                    c[(k * CRow) + d] = (float)(2 * crossCorr / (energy + energyTmp));
                }
                else
                {
                    c[(k * CRow) + d] = 0.0f;
                }
            }

            target += sfLength8kHz;
        }

        float ccmax = 0.0f;
        float ccmaxB = -1000.0f;
        int cbImax = 0;
        int lag = -1;
        float prevLagLog2;
        if (prevLag > 0)
        {
            if (fsKhz == 12)
            {
                prevLag = (prevLag << 1) / 3;
            }
            else if (fsKhz == 16)
            {
                prevLag >>= 1;
            }

            prevLagLog2 = SilkFloat.Log2((float)prevLag);
        }
        else
        {
            prevLagLog2 = 0;
        }

        int cbkSize, nbCbkSearch;
        ReadOnlySpan<sbyte> lagCb;
        if (nbSubfr == PeMaxNbSubfr)
        {
            cbkSize = PeNbCbksStage2Ext;
            lagCb = SilkTables.CbLagsStage2;
            nbCbkSearch = fsKhz == 8 && complexity > PeMinComplex ? PeNbCbksStage2Ext : PeNbCbksStage2;
        }
        else
        {
            cbkSize = PeNbCbksStage210Ms;
            lagCb = SilkTables.CbLagsStage210Ms;
            nbCbkSearch = PeNbCbksStage210Ms;
        }

        for (int k = 0; k < lengthDSrch; k++)
        {
            int d = dSrch[k];
            for (int j = 0; j < nbCbkSearch; j++)
            {
                cc[j] = 0.0f;
                for (int i = 0; i < nbSubfr; i++)
                {
                    cc[j] += c[(i * CRow) + d + lagCb[(i * cbkSize) + j]];
                }
            }

            float ccmaxNew = -1000.0f;
            int cbImaxNew = 0;
            for (int i = 0; i < nbCbkSearch; i++)
            {
                if (cc[i] > ccmaxNew)
                {
                    ccmaxNew = cc[i];
                    cbImaxNew = i;
                }
            }

            float lagLog2 = SilkFloat.Log2((float)d);
            float ccmaxNewB = ccmaxNew - (PeShortlagBias * nbSubfr * lagLog2);
            if (prevLag > 0)
            {
                float deltaLagLog2Sqr = lagLog2 - prevLagLog2;
                deltaLagLog2Sqr *= deltaLagLog2Sqr;
                ccmaxNewB -= PePrevlagBias * nbSubfr * ltpCorr * deltaLagLog2Sqr / (deltaLagLog2Sqr + 0.5f);
            }

            if (ccmaxNewB > ccmaxB && ccmaxNew > nbSubfr * searchThres2)
            {
                ccmaxB = ccmaxNewB;
                ccmax = ccmaxNew;
                lag = d;
                cbImax = cbImaxNew;
            }
        }

        if (lag == -1)
        {
            pitchOut[..PeMaxNbSubfr].Clear();
            ltpCorr = 0.0f;
            lagIndex = 0;
            contourIndex = 0;
            return 1;
        }

        ltpCorr = ccmax / nbSubfr;
        if (fsKhz > 8)
        {
            // Search in the original signal.
            lag = fsKhz == 12 ? RShiftRound(Smulbb(lag, 3), 1) : lag << 1;
            lag = Limit(lag, minLag, maxLag);
            int startLag = Math.Max(lag - 2, minLag);
            int endLag = Math.Min(lag + 2, maxLag);
            int lagNew = lag;
            cbImax = 0;
            ccmax = -1000.0f;
            Span<float> crossCorrSt3 = stackalloc float[St3Size];
            Span<float> energiesSt3 = stackalloc float[St3Size];
            CalcCorrSt3(crossCorrSt3, frame, startLag, sfLength, nbSubfr, complexity);
            CalcEnergySt3(energiesSt3, frame, startLag, sfLength, nbSubfr, complexity);
            int lagCounter = 0;
            float contourBias = PeFlatcontourBias / lag;
            if (nbSubfr == PeMaxNbSubfr)
            {
                nbCbkSearch = SilkTables.NbCbkSearchsStage3[complexity];
                cbkSize = PeNbCbksStage3Max;
                lagCb = SilkTables.CbLagsStage3;
            }
            else
            {
                nbCbkSearch = PeNbCbksStage310Ms;
                cbkSize = PeNbCbksStage310Ms;
                lagCb = SilkTables.CbLagsStage310Ms;
            }

            target = PeLtpMemLengthMs * fsKhz;
            energyTmp = SilkFloat.Energy(frame[target..], nbSubfr * sfLength) + 1.0;
            for (int d = startLag; d <= endLag; d++)
            {
                for (int j = 0; j < nbCbkSearch; j++)
                {
                    crossCorr = 0.0;
                    energy = energyTmp;
                    for (int k = 0; k < nbSubfr; k++)
                    {
                        int ix = (((k * PeNbCbksStage3Max) + j) * PeNbStage3Lags) + lagCounter;
                        crossCorr += crossCorrSt3[ix];
                        energy += energiesSt3[ix];
                    }

                    float ccmaxNew;
                    if (crossCorr > 0.0)
                    {
                        ccmaxNew = (float)(2 * crossCorr / energy);
                        ccmaxNew *= 1.0f - (contourBias * j);
                    }
                    else
                    {
                        ccmaxNew = 0.0f;
                    }

                    if (ccmaxNew > ccmax && d + SilkTables.CbLagsStage3[j] <= maxLag)
                    {
                        ccmax = ccmaxNew;
                        lagNew = d;
                        cbImax = j;
                    }
                }

                lagCounter++;
            }

            for (int k = 0; k < nbSubfr; k++)
            {
                pitchOut[k] = lagNew + lagCb[(k * cbkSize) + cbImax];
                pitchOut[k] = Limit(pitchOut[k], minLag, PeMaxLagMs * fsKhz);
            }

            lagIndex = (short)(lagNew - minLag);
            contourIndex = (sbyte)cbImax;
        }
        else
        {
            for (int k = 0; k < nbSubfr; k++)
            {
                pitchOut[k] = lag + lagCb[(k * cbkSize) + cbImax];
                pitchOut[k] = Limit(pitchOut[k], minLag8kHz, PeMaxLagMs * 8);
            }

            lagIndex = (short)(lag - minLag8kHz);
            contourIndex = (sbyte)cbImax;
        }

        return 0;
    }

    private static void Stage3Tables(int nbSubfr, int complexity, out ReadOnlySpan<sbyte> lagRange, out ReadOnlySpan<sbyte> lagCb, out int nbCbkSearch, out int cbkSize)
    {
        if (nbSubfr == PeMaxNbSubfr)
        {
            lagRange = SilkTables.LagRangeStage3.Slice(complexity * PeMaxNbSubfr * 2, PeMaxNbSubfr * 2);
            lagCb = SilkTables.CbLagsStage3;
            nbCbkSearch = SilkTables.NbCbkSearchsStage3[complexity];
            cbkSize = PeNbCbksStage3Max;
        }
        else
        {
            lagRange = SilkTables.LagRangeStage310Ms;
            lagCb = SilkTables.CbLagsStage310Ms;
            nbCbkSearch = PeNbCbksStage310Ms;
            cbkSize = PeNbCbksStage310Ms;
        }
    }

    private static void CalcCorrSt3(Span<float> crossCorrSt3, ReadOnlySpan<float> frame, int startLag, int sfLength, int nbSubfr, int complexity)
    {
        Stage3Tables(nbSubfr, complexity, out ReadOnlySpan<sbyte> lagRange, out ReadOnlySpan<sbyte> lagCb, out int nbCbkSearch, out int cbkSize);
        Span<float> scratchMem = stackalloc float[ScratchSize];
        Span<float> xcorr = stackalloc float[ScratchSize];
        int target = sfLength << 2;
        for (int k = 0; k < nbSubfr; k++)
        {
            int lagCounter = 0;
            int lagLow = lagRange[k * 2];
            int lagHigh = lagRange[(k * 2) + 1];
            CeltMath.PitchXcorr(frame[target..], frame[(target - startLag - lagHigh)..], xcorr, sfLength, lagHigh - lagLow + 1);
            for (int j = lagLow; j <= lagHigh; j++)
            {
                scratchMem[lagCounter] = xcorr[lagHigh - j];
                lagCounter++;
            }

            int delta = lagRange[k * 2];
            for (int i = 0; i < nbCbkSearch; i++)
            {
                int idx = lagCb[(k * cbkSize) + i] - delta;
                for (int j = 0; j < PeNbStage3Lags; j++)
                {
                    crossCorrSt3[(((k * PeNbCbksStage3Max) + i) * PeNbStage3Lags) + j] = scratchMem[idx + j];
                }
            }

            target += sfLength;
        }
    }

    private static void CalcEnergySt3(Span<float> energiesSt3, ReadOnlySpan<float> frame, int startLag, int sfLength, int nbSubfr, int complexity)
    {
        Stage3Tables(nbSubfr, complexity, out ReadOnlySpan<sbyte> lagRange, out ReadOnlySpan<sbyte> lagCb, out int nbCbkSearch, out int cbkSize);
        Span<float> scratchMem = stackalloc float[ScratchSize];
        int target = sfLength << 2;
        for (int k = 0; k < nbSubfr; k++)
        {
            int lagCounter = 0;
            int basis = target - (startLag + lagRange[k * 2]);
            double energy = SilkFloat.Energy(frame[basis..], sfLength) + 1e-3;
            scratchMem[lagCounter] = (float)energy;
            lagCounter++;
            int lagDiff = lagRange[(k * 2) + 1] - lagRange[k * 2] + 1;
            for (int i = 1; i < lagDiff; i++)
            {
                energy -= frame[basis + sfLength - i] * (double)frame[basis + sfLength - i];
                energy += frame[basis - i] * (double)frame[basis - i];
                scratchMem[lagCounter] = (float)energy;
                lagCounter++;
            }

            int delta = lagRange[k * 2];
            for (int i = 0; i < nbCbkSearch; i++)
            {
                int idx = lagCb[(k * cbkSize) + i] - delta;
                for (int j = 0; j < PeNbStage3Lags; j++)
                {
                    energiesSt3[(((k * PeNbCbksStage3Max) + i) * PeNbStage3Lags) + j] = scratchMem[idx + j];
                }
            }

            target += sfLength;
        }
    }
}
