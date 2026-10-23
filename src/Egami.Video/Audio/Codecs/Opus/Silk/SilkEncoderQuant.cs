// Translated from libopus 1.5.2 silk/gain_quant.c (the encoder's parts), A2NLSF.c, interpolate.c, sort.c, NLSF_encode.c,
// NLSF_VQ.c, NLSF_del_dec_quant.c, NLSF_VQ_weights_laroia.c, quant_LTP_gains.c and VQ_WMat_EC.c.
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>The quantizers of SILK's encoder: gains, NLSFs and LTP coefficients.</summary>
internal static class SilkEncoderQuant
{
    private const int GainOffset = ((MinQGainDb * 128) / 6) + (16 * 128);
    private const int GainScaleQ16 = (65536 * (NLevelsQGain - 1)) / (((MaxQGainDb - MinQGainDb) * 128) / 6);
    private const int GainInvScaleQ16 = (65536 * (((MaxQGainDb - MinQGainDb) * 128) / 6)) / (NLevelsQGain - 1);
    private const int BinDivStepsA2NlsfFix = 3;
    private const int MaxIterationsA2NlsfFix = 16;
    private const int LsfCosTabSzFix = 128;

    /// <summary>SILK_FIX_CONST.</summary>
    public static int FixConst(double c, int q) => (int)((c * (1L << q)) + 0.5);

    /// <summary>silk_gains_quant: gain scalar quantization with hysteresis, uniform on a log scale.</summary>
    public static void GainsQuant(Span<sbyte> ind, Span<int> gainQ16, ref sbyte prevInd, bool conditional, int nbSubfr)
    {
        for (int k = 0; k < nbSubfr; k++)
        {
            ind[k] = (sbyte)Smulwb(GainScaleQ16, SilkSigProc.Lin2Log(gainQ16[k]) - GainOffset);
            if (ind[k] < prevInd)
            {
                ind[k]++;
            }

            ind[k] = (sbyte)Limit(ind[k], 0, NLevelsQGain - 1);
            if (k == 0 && !conditional)
            {
                ind[k] = (sbyte)Limit(ind[k], prevInd + MinDeltaGainQuant, NLevelsQGain - 1);
                prevInd = ind[k];
            }
            else
            {
                ind[k] = (sbyte)(ind[k] - prevInd);
                int doubleStepSizeThreshold = (2 * MaxDeltaGainQuant) - NLevelsQGain + prevInd;
                if (ind[k] > doubleStepSizeThreshold)
                {
                    ind[k] = (sbyte)(doubleStepSizeThreshold + ((ind[k] - doubleStepSizeThreshold + 1) >> 1));
                }

                ind[k] = (sbyte)Limit(ind[k], MinDeltaGainQuant, MaxDeltaGainQuant);
                if (ind[k] > doubleStepSizeThreshold)
                {
                    prevInd = (sbyte)(prevInd + (ind[k] << 1) - doubleStepSizeThreshold);
                    prevInd = (sbyte)Math.Min((int)prevInd, NLevelsQGain - 1);
                }
                else
                {
                    prevInd = (sbyte)(prevInd + ind[k]);
                }

                ind[k] = (sbyte)(ind[k] - MinDeltaGainQuant);
            }

            gainQ16[k] = SilkSigProc.Log2Lin(Math.Min(Smulwb(GainInvScaleQ16, prevInd) + GainOffset, 3967));
        }
    }

    /// <summary>silk_gains_ID: a unique identifier of a gain index vector.</summary>
    public static int GainsId(ReadOnlySpan<sbyte> ind, int nbSubfr)
    {
        int gainsId = 0;
        for (int k = 0; k < nbSubfr; k++)
        {
            gainsId = unchecked(ind[k] + (gainsId << 8));
        }

        return gainsId;
    }

    /// <summary>silk_interpolate: interpolates two vectors with a Q2 factor.</summary>
    public static void Interpolate(Span<short> xi, ReadOnlySpan<short> x0, ReadOnlySpan<short> x1, int ifactQ2, int d)
    {
        for (int i = 0; i < d; i++)
        {
            xi[i] = (short)(x0[i] + (Smulbb(x1[i] - x0[i], ifactQ2) >> 2));
        }
    }

    /// <summary>silk_insertion_sort_increasing: sorts the first K of L values in increasing order, with their indices.</summary>
    public static void InsertionSortIncreasing(Span<int> a, Span<int> idx, int l, int k)
    {
        int i, j, value;
        for (i = 0; i < k; i++)
        {
            idx[i] = i;
        }

        for (i = 1; i < k; i++)
        {
            value = a[i];
            for (j = i - 1; j >= 0 && value < a[j]; j--)
            {
                a[j + 1] = a[j];
                idx[j + 1] = idx[j];
            }

            a[j + 1] = value;
            idx[j + 1] = i;
        }

        for (i = k; i < l; i++)
        {
            value = a[i];
            if (value < a[k - 1])
            {
                for (j = k - 2; j >= 0 && value < a[j]; j--)
                {
                    a[j + 1] = a[j];
                    idx[j + 1] = idx[j];
                }

                a[j + 1] = value;
                idx[j + 1] = i;
            }
        }
    }

    /// <summary>silk_A2NLSF: NLSFs from monic whitening filter coefficients, bandwidth expanding <paramref name="aQ16"/> until all roots are found.</summary>
    public static void A2Nlsf(Span<short> nlsf, Span<int> aQ16, int d)
    {
        Span<int> p = stackalloc int[(SilkSigProc.MaxOrderLpc / 2) + 1];
        Span<int> q = stackalloc int[(SilkSigProc.MaxOrderLpc / 2) + 1];
        ReadOnlySpan<short> cosTab = SilkTables.LsfCosTabFixQ12;
        int dd = d >> 1;
        A2NlsfInit(aQ16, p, q, dd);
        Span<int> poly = p;
        int xlo = cosTab[0];
        int ylo = A2NlsfEvalPoly(poly, xlo, dd);
        int rootIx;
        if (ylo < 0)
        {
            nlsf[0] = 0;
            poly = q;
            ylo = A2NlsfEvalPoly(poly, xlo, dd);
            rootIx = 1;
        }
        else
        {
            rootIx = 0;
        }

        int k = 1;
        int i = 0;
        int thr = 0;
        while (true)
        {
            int xhi = cosTab[k];
            int yhi = A2NlsfEvalPoly(poly, xhi, dd);
            if ((ylo <= 0 && yhi >= thr) || (ylo >= 0 && yhi <= -thr))
            {
                thr = yhi == 0 ? 1 : 0;
                int ffrac = -256;
                for (int m = 0; m < BinDivStepsA2NlsfFix; m++)
                {
                    int xmid = RShiftRound(xlo + xhi, 1);
                    int ymid = A2NlsfEvalPoly(poly, xmid, dd);
                    if ((ylo <= 0 && ymid >= 0) || (ylo >= 0 && ymid <= 0))
                    {
                        xhi = xmid;
                        yhi = ymid;
                    }
                    else
                    {
                        xlo = xmid;
                        ylo = ymid;
                        ffrac += 128 >> m;
                    }
                }

                if (Math.Abs(ylo) < 65536)
                {
                    int den = ylo - yhi;
                    int nom = (ylo << (8 - BinDivStepsA2NlsfFix)) + (den >> 1);
                    if (den != 0)
                    {
                        ffrac += nom / den;
                    }
                }
                else
                {
                    ffrac += ylo / ((ylo - yhi) >> (8 - BinDivStepsA2NlsfFix));
                }

                nlsf[rootIx] = (short)Math.Min((k << 8) + ffrac, short.MaxValue);
                rootIx++;
                if (rootIx >= d)
                {
                    break;
                }

                poly = (rootIx & 1) == 0 ? p : q;
                xlo = cosTab[k - 1];
                ylo = (1 - (rootIx & 2)) << 12;
            }
            else
            {
                k++;
                xlo = xhi;
                ylo = yhi;
                thr = 0;
                if (k > LsfCosTabSzFix)
                {
                    i++;
                    if (i > MaxIterationsA2NlsfFix)
                    {
                        nlsf[0] = (short)((1 << 15) / (d + 1));
                        for (k = 1; k < d; k++)
                        {
                            nlsf[k] = (short)(nlsf[k - 1] + nlsf[0]);
                        }

                        return;
                    }

                    SilkSigProc.BwExpander32(aQ16, d, 65536 - (1 << i));
                    A2NlsfInit(aQ16, p, q, dd);
                    poly = p;
                    xlo = cosTab[0];
                    ylo = A2NlsfEvalPoly(poly, xlo, dd);
                    if (ylo < 0)
                    {
                        nlsf[0] = 0;
                        poly = q;
                        ylo = A2NlsfEvalPoly(poly, xlo, dd);
                        rootIx = 1;
                    }
                    else
                    {
                        rootIx = 0;
                    }

                    k = 1;
                }
            }
        }
    }

    private static void A2NlsfTransPoly(Span<int> p, int dd)
    {
        for (int k = 2; k <= dd; k++)
        {
            for (int n = dd; n > k; n--)
            {
                p[n - 2] -= p[n];
            }

            p[k - 2] -= p[k] << 1;
        }
    }

    private static int A2NlsfEvalPoly(ReadOnlySpan<int> p, int x, int dd)
    {
        int y32 = p[dd];
        int xQ16 = x << 4;
        for (int n = dd - 1; n >= 0; n--)
        {
            y32 = Smlaww(p[n], y32, xQ16);
        }

        return y32;
    }

    private static void A2NlsfInit(ReadOnlySpan<int> aQ16, Span<int> p, Span<int> q, int dd)
    {
        p[dd] = 1 << 16;
        q[dd] = 1 << 16;
        for (int k = 0; k < dd; k++)
        {
            p[k] = -aQ16[dd - k - 1] - aQ16[dd + k];
            q[k] = -aQ16[dd - k - 1] + aQ16[dd + k];
        }

        for (int k = dd; k > 0; k--)
        {
            p[k - 1] -= p[k];
            q[k - 1] += q[k];
        }

        A2NlsfTransPoly(p, dd);
        A2NlsfTransPoly(q, dd);
    }

    /// <summary>silk_NLSF_VQ_weights_laroia: Laroia's low complexity NLSF weights.</summary>
    public static void NlsfVqWeightsLaroia(Span<short> pNlsfwQOut, ReadOnlySpan<short> pNlsfQ15, int d)
    {
        int tmp1Int = Math.Max((int)pNlsfQ15[0], 1);
        tmp1Int = (1 << (15 + NlsfWQ)) / tmp1Int;
        int tmp2Int = Math.Max(pNlsfQ15[1] - pNlsfQ15[0], 1);
        tmp2Int = (1 << (15 + NlsfWQ)) / tmp2Int;
        pNlsfwQOut[0] = (short)Math.Min(tmp1Int + tmp2Int, short.MaxValue);
        for (int k = 1; k < d - 1; k += 2)
        {
            tmp1Int = Math.Max(pNlsfQ15[k + 1] - pNlsfQ15[k], 1);
            tmp1Int = (1 << (15 + NlsfWQ)) / tmp1Int;
            pNlsfwQOut[k] = (short)Math.Min(tmp1Int + tmp2Int, short.MaxValue);
            tmp2Int = Math.Max(pNlsfQ15[k + 2] - pNlsfQ15[k + 1], 1);
            tmp2Int = (1 << (15 + NlsfWQ)) / tmp2Int;
            pNlsfwQOut[k + 1] = (short)Math.Min(tmp1Int + tmp2Int, short.MaxValue);
        }

        tmp1Int = Math.Max((1 << 15) - pNlsfQ15[d - 1], 1);
        tmp1Int = (1 << (15 + NlsfWQ)) / tmp1Int;
        pNlsfwQOut[d - 1] = (short)Math.Min(tmp1Int + tmp2Int, short.MaxValue);
    }

    /// <summary>silk_NLSF_VQ: the weighted quantization errors of an input vector for each codebook vector.</summary>
    private static void NlsfVq(Span<int> errQ24, ReadOnlySpan<short> inQ15, ReadOnlySpan<byte> pCbQ8, ReadOnlySpan<short> pWghtQ9, int k, int lpcOrder)
    {
        int cb = 0;
        for (int i = 0; i < k; i++)
        {
            int sumErrorQ24 = 0;
            int predQ24 = 0;
            for (int m = lpcOrder - 2; m >= 0; m -= 2)
            {
                int diffQ15 = inQ15[m + 1] - (pCbQ8[cb + m + 1] << 7);
                int diffwQ24 = Smulbb(diffQ15, pWghtQ9[cb + m + 1]);
                sumErrorQ24 = unchecked(sumErrorQ24 + Abs(diffwQ24 - (predQ24 >> 1)));
                predQ24 = diffwQ24;

                diffQ15 = inQ15[m] - (pCbQ8[cb + m] << 7);
                diffwQ24 = Smulbb(diffQ15, pWghtQ9[cb + m]);
                sumErrorQ24 = unchecked(sumErrorQ24 + Abs(diffwQ24 - (predQ24 >> 1)));
                predQ24 = diffwQ24;
            }

            errQ24[i] = sumErrorQ24;
            cb += lpcOrder;
        }
    }

    /// <summary>silk_NLSF_del_dec_quant: a delayed-decision quantizer for NLSF residuals; returns the RD value in Q25.</summary>
    private static int NlsfDelDecQuant(Span<sbyte> indices, ReadOnlySpan<short> xQ10, ReadOnlySpan<short> wQ5, ReadOnlySpan<byte> predCoefQ8,
        ReadOnlySpan<short> ecIx, ReadOnlySpan<byte> ecRatesQ5, int quantStepSizeQ16, short invQuantStepSizeQ6, int muQ20, int order)
    {
        const int States = NlsfQuantDelDecStates;
        const int AmpExt = NlsfQuantMaxAmplitudeExt;
        const int Amp = NlsfQuantMaxAmplitude;
        int levelAdjQ10 = FixConst(NlsfQuantLevelAdj, 10);
        Span<int> indSort = stackalloc int[States];
        Span<sbyte> ind = stackalloc sbyte[States * SilkChannelDecoder.MaxLpcOrder];
        Span<short> prevOutQ10 = stackalloc short[2 * States];
        Span<int> rdQ25 = stackalloc int[2 * States];
        Span<int> rdMinQ25 = stackalloc int[States];
        Span<int> rdMaxQ25 = stackalloc int[States];
        Span<int> out0Q10Table = stackalloc int[2 * AmpExt];
        Span<int> out1Q10Table = stackalloc int[2 * AmpExt];
        short out0Q10, out1Q10;
        for (int i = -AmpExt; i <= AmpExt - 1; i++)
        {
            out0Q10 = (short)(i << 10);
            out1Q10 = (short)(out0Q10 + 1024);
            if (i > 0)
            {
                out0Q10 = (short)(out0Q10 - levelAdjQ10);
                out1Q10 = (short)(out1Q10 - levelAdjQ10);
            }
            else if (i == 0)
            {
                out1Q10 = (short)(out1Q10 - levelAdjQ10);
            }
            else if (i == -1)
            {
                out0Q10 = (short)(out0Q10 + levelAdjQ10);
            }
            else
            {
                out0Q10 = (short)(out0Q10 + levelAdjQ10);
                out1Q10 = (short)(out1Q10 + levelAdjQ10);
            }

            out0Q10Table[i + AmpExt] = Smulbb(out0Q10, quantStepSizeQ16) >> 16;
            out1Q10Table[i + AmpExt] = Smulbb(out1Q10, quantStepSizeQ16) >> 16;
        }

        int nStates = 1;
        rdQ25[0] = 0;
        prevOutQ10[0] = 0;
        int indTmp, j;
        for (int i = order - 1; i >= 0; i--)
        {
            ReadOnlySpan<byte> ratesQ5 = ecRatesQ5[ecIx[i]..];
            int inQ10 = xQ10[i];
            for (j = 0; j < nStates; j++)
            {
                int predQ10 = Smulbb((short)predCoefQ8[i], prevOutQ10[j]) >> 8;
                int resQ10 = inQ10 - predQ10;
                indTmp = Smulbb(invQuantStepSizeQ6, resQ10) >> 16;
                indTmp = Limit(indTmp, -AmpExt, AmpExt - 1);
                ind[(j * SilkChannelDecoder.MaxLpcOrder) + i] = (sbyte)indTmp;
                out0Q10 = (short)out0Q10Table[indTmp + AmpExt];
                out1Q10 = (short)out1Q10Table[indTmp + AmpExt];
                out0Q10 = (short)(out0Q10 + predQ10);
                out1Q10 = (short)(out1Q10 + predQ10);
                prevOutQ10[j] = out0Q10;
                prevOutQ10[j + nStates] = out1Q10;
                int rate0Q5, rate1Q5;
                if (indTmp + 1 >= Amp)
                {
                    if (indTmp + 1 == Amp)
                    {
                        rate0Q5 = ratesQ5[indTmp + Amp];
                        rate1Q5 = 280;
                    }
                    else
                    {
                        rate0Q5 = Smlabb(280 - (43 * Amp), 43, indTmp);
                        rate1Q5 = rate0Q5 + 43;
                    }
                }
                else if (indTmp <= -Amp)
                {
                    if (indTmp == -Amp)
                    {
                        rate0Q5 = 280;
                        rate1Q5 = ratesQ5[indTmp + 1 + Amp];
                    }
                    else
                    {
                        rate0Q5 = Smlabb(280 - (43 * Amp), -43, indTmp);
                        rate1Q5 = rate0Q5 - 43;
                    }
                }
                else
                {
                    rate0Q5 = ratesQ5[indTmp + Amp];
                    rate1Q5 = ratesQ5[indTmp + 1 + Amp];
                }

                int rdTmpQ25 = rdQ25[j];
                int diffQ10 = inQ10 - out0Q10;
                rdQ25[j] = Smlabb(Mla(rdTmpQ25, Smulbb(diffQ10, diffQ10), wQ5[i]), muQ20, rate0Q5);
                diffQ10 = inQ10 - out1Q10;
                rdQ25[j + nStates] = Smlabb(Mla(rdTmpQ25, Smulbb(diffQ10, diffQ10), wQ5[i]), muQ20, rate1Q5);
            }

            if (nStates <= States / 2)
            {
                for (j = 0; j < nStates; j++)
                {
                    ind[((j + nStates) * SilkChannelDecoder.MaxLpcOrder) + i] = (sbyte)(ind[(j * SilkChannelDecoder.MaxLpcOrder) + i] + 1);
                }

                nStates <<= 1;
                for (j = nStates; j < States; j++)
                {
                    ind[(j * SilkChannelDecoder.MaxLpcOrder) + i] = ind[((j - nStates) * SilkChannelDecoder.MaxLpcOrder) + i];
                }
            }
            else
            {
                for (j = 0; j < States; j++)
                {
                    if (rdQ25[j] > rdQ25[j + States])
                    {
                        rdMaxQ25[j] = rdQ25[j];
                        rdMinQ25[j] = rdQ25[j + States];
                        rdQ25[j] = rdMinQ25[j];
                        rdQ25[j + States] = rdMaxQ25[j];
                        out0Q10 = prevOutQ10[j];
                        prevOutQ10[j] = prevOutQ10[j + States];
                        prevOutQ10[j + States] = out0Q10;
                        indSort[j] = j + States;
                    }
                    else
                    {
                        rdMinQ25[j] = rdQ25[j];
                        rdMaxQ25[j] = rdQ25[j + States];
                        indSort[j] = j;
                    }
                }

                while (true)
                {
                    int minMaxQ25 = int.MaxValue;
                    int maxMinQ25 = 0;
                    int indMinMax = 0;
                    int indMaxMin = 0;
                    for (j = 0; j < States; j++)
                    {
                        if (minMaxQ25 > rdMaxQ25[j])
                        {
                            minMaxQ25 = rdMaxQ25[j];
                            indMinMax = j;
                        }

                        if (maxMinQ25 < rdMinQ25[j])
                        {
                            maxMinQ25 = rdMinQ25[j];
                            indMaxMin = j;
                        }
                    }

                    if (minMaxQ25 >= maxMinQ25)
                    {
                        break;
                    }

                    indSort[indMaxMin] = indSort[indMinMax] ^ States;
                    rdQ25[indMaxMin] = rdQ25[indMinMax + States];
                    prevOutQ10[indMaxMin] = prevOutQ10[indMinMax + States];
                    rdMinQ25[indMaxMin] = 0;
                    rdMaxQ25[indMinMax] = int.MaxValue;
                    ind.Slice(indMinMax * SilkChannelDecoder.MaxLpcOrder, SilkChannelDecoder.MaxLpcOrder)
                        .CopyTo(ind.Slice(indMaxMin * SilkChannelDecoder.MaxLpcOrder, SilkChannelDecoder.MaxLpcOrder));
                }

                for (j = 0; j < States; j++)
                {
                    ind[(j * SilkChannelDecoder.MaxLpcOrder) + i] += (sbyte)(indSort[j] >> NlsfQuantDelDecStatesLog2);
                }
            }
        }

        indTmp = 0;
        int minQ25 = int.MaxValue;
        for (j = 0; j < 2 * States; j++)
        {
            if (minQ25 > rdQ25[j])
            {
                minQ25 = rdQ25[j];
                indTmp = j;
            }
        }

        for (j = 0; j < order; j++)
        {
            indices[j] = ind[((indTmp & (States - 1)) * SilkChannelDecoder.MaxLpcOrder) + j];
        }

        indices[0] += (sbyte)(indTmp >> NlsfQuantDelDecStatesLog2);
        return minQ25;
    }

    /// <summary>silk_NLSF_encode: quantizes an NLSF vector with the two-stage codebook; returns the RD value in Q25.</summary>
    public static int NlsfEncode(Span<sbyte> nlsfIndices, Span<short> pNlsfQ15, SilkNlsfCodebook cb, ReadOnlySpan<short> pWQ2, int nlsfMuQ20, int nSurvivors, int signalType)
    {
        int order = cb.Order;
        Span<int> errQ24 = stackalloc int[cb.NVectors];
        Span<int> tempIndices1 = stackalloc int[nSurvivors];
        Span<int> rdQ25 = stackalloc int[nSurvivors];
        Span<sbyte> tempIndices2 = stackalloc sbyte[nSurvivors * SilkChannelDecoder.MaxLpcOrder];
        Span<short> resQ10 = stackalloc short[SilkChannelDecoder.MaxLpcOrder];
        Span<short> wAdjQ5 = stackalloc short[SilkChannelDecoder.MaxLpcOrder];
        Span<byte> predQ8 = stackalloc byte[SilkChannelDecoder.MaxLpcOrder];
        Span<short> ecIx = stackalloc short[SilkChannelDecoder.MaxLpcOrder];
        SilkSigProc.NlsfStabilize(pNlsfQ15, cb.DeltaMinQ15, order);
        NlsfVq(errQ24, pNlsfQ15, cb.Cb1NlsfQ8, cb.Cb1WghtQ9, cb.NVectors, order);
        InsertionSortIncreasing(errQ24, tempIndices1, cb.NVectors, nSurvivors);
        ReadOnlySpan<byte> iCdf = cb.Cb1Icdf[((signalType >> 1) * cb.NVectors)..];
        for (int s = 0; s < nSurvivors; s++)
        {
            int ind1 = tempIndices1[s];
            ReadOnlySpan<byte> cbElement = cb.Cb1NlsfQ8.Slice(ind1 * order, order);
            ReadOnlySpan<short> cbWghtQ9 = cb.Cb1WghtQ9.Slice(ind1 * order, order);
            for (int i = 0; i < order; i++)
            {
                int nlsfTmpQ15 = (short)(cbElement[i] << 7);
                int wTmpQ9 = cbWghtQ9[i];
                resQ10[i] = (short)(Smulbb(pNlsfQ15[i] - nlsfTmpQ15, wTmpQ9) >> 14);
                wAdjQ5[i] = (short)Div32VarQ(pWQ2[i], Smulbb(wTmpQ9, wTmpQ9), 21);
            }

            SilkSigProc.NlsfUnpack(ecIx, predQ8, cb, ind1);
            rdQ25[s] = NlsfDelDecQuant(tempIndices2.Slice(s * SilkChannelDecoder.MaxLpcOrder, SilkChannelDecoder.MaxLpcOrder), resQ10, wAdjQ5, predQ8, ecIx,
                cb.EcRatesQ5, cb.QuantStepSizeQ16, (short)cb.InvQuantStepSizeQ6, nlsfMuQ20, order);
            int probQ8 = ind1 == 0 ? 256 - iCdf[ind1] : iCdf[ind1 - 1] - iCdf[ind1];
            int bitsQ7 = (8 << 7) - SilkSigProc.Lin2Log(probQ8);
            rdQ25[s] = Smlabb(rdQ25[s], bitsQ7, nlsfMuQ20 >> 2);
        }

        Span<int> bestIndex = stackalloc int[1];
        InsertionSortIncreasing(rdQ25, bestIndex, nSurvivors, 1);
        nlsfIndices[0] = (sbyte)tempIndices1[bestIndex[0]];
        tempIndices2.Slice(bestIndex[0] * SilkChannelDecoder.MaxLpcOrder, order).CopyTo(nlsfIndices[1..]);
        SilkSigProc.NlsfDecode(pNlsfQ15, nlsfIndices, cb);
        return rdQ25[0];
    }

    /// <summary>
    /// silk_quant_LTP_gains: picks the LTP codebook and vectors with the lowest rate-distortion. <paramref name="xxQ17"/> and
    /// <paramref name="xXQ17"/> are the correlation matrices and vectors of the subframes.
    /// </summary>
    public static void QuantLtpGains(Span<short> bQ14, Span<sbyte> cbkIndex, out sbyte periodicityIndex, ref int sumLogGainQ7, out int predGainDbQ7,
        ReadOnlySpan<int> xxQ17, ReadOnlySpan<int> xXQ17, int subfrLen, int nbSubfr)
    {
        Span<sbyte> tempIdx = stackalloc sbyte[SilkChannelDecoder.MaxNbSubfr];
        int minRateDistQ7 = int.MaxValue;
        int bestSumLogGainQ7 = 0;
        int resNrgQ15 = 0;
        periodicityIndex = 0;
        int maxSumLogGainQ7 = FixConst(MaxSumLogGainDb / 6.0, 7);
        int gainQ7 = 0;
        for (int k = 0; k < 3; k++)
        {
            int gainSafety = FixConst(0.4, 7);
            ReadOnlySpan<byte> clQ5 = LtpGainBitsQ5(k);
            ReadOnlySpan<sbyte> cbkQ7 = LtpVqQ7(k);
            ReadOnlySpan<byte> cbkGainQ7 = LtpVqGainQ7(k);
            int cbkSize = SilkTables.LtpVqSizes[k];
            resNrgQ15 = 0;
            int rateDistQ7 = 0;
            int sumLogGainTmpQ7 = sumLogGainQ7;
            for (int j = 0; j < nbSubfr; j++)
            {
                int maxGainQ7 = SilkSigProc.Log2Lin(maxSumLogGainQ7 - sumLogGainTmpQ7 + FixConst(7, 7)) - gainSafety;
                VqWMatEc(out tempIdx[j], out int resNrgQ15Subfr, out int rateDistQ7Subfr, ref gainQ7,
                    xxQ17.Slice(j * SilkChannelDecoder.LtpOrder * SilkChannelDecoder.LtpOrder), xXQ17.Slice(j * SilkChannelDecoder.LtpOrder),
                    cbkQ7, cbkGainQ7, clQ5, subfrLen, maxGainQ7, cbkSize);
                resNrgQ15 = AddPosSat32(resNrgQ15, resNrgQ15Subfr);
                rateDistQ7 = AddPosSat32(rateDistQ7, rateDistQ7Subfr);
                sumLogGainTmpQ7 = Math.Max(0, sumLogGainTmpQ7 + SilkSigProc.Lin2Log(gainSafety + gainQ7) - FixConst(7, 7));
            }

            if (rateDistQ7 <= minRateDistQ7)
            {
                minRateDistQ7 = rateDistQ7;
                periodicityIndex = (sbyte)k;
                tempIdx[..nbSubfr].CopyTo(cbkIndex);
                bestSumLogGainQ7 = sumLogGainTmpQ7;
            }
        }

        ReadOnlySpan<sbyte> cbk = LtpVqQ7(periodicityIndex);
        for (int j = 0; j < nbSubfr; j++)
        {
            for (int k = 0; k < SilkChannelDecoder.LtpOrder; k++)
            {
                bQ14[(j * SilkChannelDecoder.LtpOrder) + k] = (short)(cbk[(cbkIndex[j] * SilkChannelDecoder.LtpOrder) + k] << 7);
            }
        }

        resNrgQ15 = nbSubfr == 2 ? resNrgQ15 >> 1 : resNrgQ15 >> 2;
        sumLogGainQ7 = bestSumLogGainQ7;
        predGainDbQ7 = Smulbb(-3, SilkSigProc.Lin2Log(resNrgQ15) - (15 << 7));
    }

    private static ReadOnlySpan<byte> LtpGainBitsQ5(int k) => k switch
    {
        0 => SilkTables.LtpGainBitsQ50,
        1 => SilkTables.LtpGainBitsQ51,
        _ => SilkTables.LtpGainBitsQ52,
    };

    private static ReadOnlySpan<sbyte> LtpVqQ7(int k) => k switch
    {
        0 => SilkTables.LtpGainVq0,
        1 => SilkTables.LtpGainVq1,
        _ => SilkTables.LtpGainVq2,
    };

    private static ReadOnlySpan<byte> LtpVqGainQ7(int k) => k switch
    {
        0 => SilkTables.LtpGainVq0Gain,
        1 => SilkTables.LtpGainVq1Gain,
        _ => SilkTables.LtpGainVq2Gain,
    };

    /// <summary>silk_VQ_WMat_EC: entropy-constrained matrix-weighted VQ of one 5-element vector.</summary>
    private static void VqWMatEc(out sbyte ind, out int resNrgQ15, out int rateDistQ8, ref int gainQ7, ReadOnlySpan<int> xxQ17, ReadOnlySpan<int> xXQ17,
        ReadOnlySpan<sbyte> cbQ7, ReadOnlySpan<byte> cbGainQ7, ReadOnlySpan<byte> clQ5, int subfrLen, int maxGainQ7, int l)
    {
        unchecked
        {
            Span<int> negXxQ24 = stackalloc int[5];
            for (int i = 0; i < 5; i++)
            {
                negXxQ24[i] = -(xXQ17[i] << 7);
            }

            rateDistQ8 = int.MaxValue;
            resNrgQ15 = int.MaxValue;
            ind = 0;
            int row = 0;
            for (int k = 0; k < l; k++)
            {
                int gainTmpQ7 = cbGainQ7[k];
                int sum1Q15 = FixConst(1.001, 15);
                int penalty = Math.Max(gainTmpQ7 - maxGainQ7, 0) << 11;
                ReadOnlySpan<sbyte> cb = cbQ7.Slice(row, 5);

                int sum2Q24 = Mla(negXxQ24[0], xxQ17[1], cb[1]);
                sum2Q24 = Mla(sum2Q24, xxQ17[2], cb[2]);
                sum2Q24 = Mla(sum2Q24, xxQ17[3], cb[3]);
                sum2Q24 = Mla(sum2Q24, xxQ17[4], cb[4]);
                sum2Q24 <<= 1;
                sum2Q24 = Mla(sum2Q24, xxQ17[0], cb[0]);
                sum1Q15 = Smlawb(sum1Q15, sum2Q24, cb[0]);

                sum2Q24 = Mla(negXxQ24[1], xxQ17[7], cb[2]);
                sum2Q24 = Mla(sum2Q24, xxQ17[8], cb[3]);
                sum2Q24 = Mla(sum2Q24, xxQ17[9], cb[4]);
                sum2Q24 <<= 1;
                sum2Q24 = Mla(sum2Q24, xxQ17[6], cb[1]);
                sum1Q15 = Smlawb(sum1Q15, sum2Q24, cb[1]);

                sum2Q24 = Mla(negXxQ24[2], xxQ17[13], cb[3]);
                sum2Q24 = Mla(sum2Q24, xxQ17[14], cb[4]);
                sum2Q24 <<= 1;
                sum2Q24 = Mla(sum2Q24, xxQ17[12], cb[2]);
                sum1Q15 = Smlawb(sum1Q15, sum2Q24, cb[2]);

                sum2Q24 = Mla(negXxQ24[3], xxQ17[19], cb[4]);
                sum2Q24 <<= 1;
                sum2Q24 = Mla(sum2Q24, xxQ17[18], cb[3]);
                sum1Q15 = Smlawb(sum1Q15, sum2Q24, cb[3]);

                sum2Q24 = negXxQ24[4] << 1;
                sum2Q24 = Mla(sum2Q24, xxQ17[24], cb[4]);
                sum1Q15 = Smlawb(sum1Q15, sum2Q24, cb[4]);

                if (sum1Q15 >= 0)
                {
                    int bitsResQ8 = Smulbb(subfrLen, SilkSigProc.Lin2Log(sum1Q15 + penalty) - (15 << 7));
                    int bitsTotQ8 = bitsResQ8 + (clQ5[k] << (3 - 1));
                    if (bitsTotQ8 <= rateDistQ8)
                    {
                        rateDistQ8 = bitsTotQ8;
                        resNrgQ15 = sum1Q15 + penalty;
                        ind = (sbyte)k;
                        gainQ7 = gainTmpQ7;
                    }
                }

                row += SilkChannelDecoder.LtpOrder;
            }
        }
    }
}
