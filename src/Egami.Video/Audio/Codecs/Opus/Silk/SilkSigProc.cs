// Translated from libopus 1.5.2 silk/NLSF2A.c, LPC_fit.c, LPC_inv_pred_gain.c, bwexpander.c, bwexpander_32.c,
// NLSF_stabilize.c, sort.c, NLSF_decode.c, NLSF_unpack.c, decode_pitch.c, gain_quant.c, lin2log.c, log2lin.c,
// sum_sqr_shift.c and LPC_analysis_filter.c (the decoder's parts).
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's signal processing helpers used by the decoder: LPC and NLSF conversion, gains and pitch lags.</summary>
internal static class SilkSigProc
{
    public const int MaxOrderLpc = 24;
    private const int MaxLpcStabilizeIterations = 16;
    private const int NlsfQuantMaxAmplitude = 4;

    // QA = 16 in NLSF2A.c.
    private const int NlsfQa = 16;

    // QA = 24, A_LIMIT = SILK_FIX_CONST( 0.99975, QA ) and SILK_FIX_CONST( 1.0f / MAX_PREDICTION_POWER_GAIN, 30 ) in
    // LPC_inv_pred_gain.c.
    private const int InvGainQa = 24;
    private const int ALimit = 16773022;
    private const int MinInvGainQ30 = 107374;

    private static ReadOnlySpan<byte> Ordering16 => [0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1];

    private static ReadOnlySpan<byte> Ordering10 => [0, 9, 6, 3, 4, 5, 8, 1, 2, 7];

    /// <summary>silk_NLSF2A_find_poly.</summary>
    private static void Nlsf2AFindPoly(Span<int> output, ReadOnlySpan<int> cLsf, int dd)
    {
        unchecked
        {
            output[0] = 1 << NlsfQa;
            output[1] = -cLsf[0];
            for (int k = 1; k < dd; k++)
            {
                int ftmp = cLsf[2 * k];
                output[k + 1] = LShift(output[k - 1], 1) - (int)RShiftRound64(Smull(ftmp, output[k]), NlsfQa);
                for (int n = k; n > 1; n--)
                {
                    output[n] += output[n - 2] - (int)RShiftRound64(Smull(ftmp, output[n - 1]), NlsfQa);
                }

                output[1] -= ftmp;
            }
        }
    }

    /// <summary>silk_NLSF2A: whitening filter coefficients in Q12 from normalized line spectral frequencies in Q15.</summary>
    public static void Nlsf2A(Span<short> aQ12, ReadOnlySpan<short> nlsf, int d)
    {
        ReadOnlySpan<byte> ordering = d == 16 ? Ordering16 : Ordering10;
        Span<int> cosLsfQa = stackalloc int[MaxOrderLpc];
        Span<int> p = stackalloc int[(MaxOrderLpc / 2) + 1];
        Span<int> q = stackalloc int[(MaxOrderLpc / 2) + 1];
        Span<int> a32Qa1 = stackalloc int[MaxOrderLpc];
        ReadOnlySpan<short> cosTab = SilkTables.LsfCosTabFixQ12;
        unchecked
        {
            for (int k = 0; k < d; k++)
            {
                int fInt = nlsf[k] >> (15 - 7);
                int fFrac = nlsf[k] - LShift(fInt, 15 - 7);
                int cosVal = cosTab[fInt];
                int delta = cosTab[fInt + 1] - cosVal;
                cosLsfQa[ordering[k]] = RShiftRound(LShift(cosVal, 8) + (delta * fFrac), 20 - NlsfQa);
            }

            int dd = d >> 1;
            Nlsf2AFindPoly(p, cosLsfQa, dd);
            Nlsf2AFindPoly(q, cosLsfQa[1..], dd);
            for (int k = 0; k < dd; k++)
            {
                int ptmp = p[k + 1] + p[k];
                int qtmp = q[k + 1] - q[k];
                a32Qa1[k] = -qtmp - ptmp;
                a32Qa1[d - k - 1] = qtmp - ptmp;
            }

            LpcFit(aQ12, a32Qa1, 12, NlsfQa + 1, d);
            for (int i = 0; LpcInversePredGain(aQ12, d) == 0 && i < MaxLpcStabilizeIterations; i++)
            {
                BwExpander32(a32Qa1, d, 65536 - LShift(2, i));
                for (int k = 0; k < d; k++)
                {
                    aQ12[k] = (short)RShiftRound(a32Qa1[k], NlsfQa + 1 - 12);
                }
            }
        }
    }

    /// <summary>silk_LPC_fit: converts int32 coefficients to int16 without wrap-around.</summary>
    public static void LpcFit(Span<short> aQOut, Span<int> aQIn, int qOut, int qIn, int d)
    {
        unchecked
        {
            int i;
            int idx = 0;
            for (i = 0; i < 10; i++)
            {
                int maxabs = 0;
                for (int k = 0; k < d; k++)
                {
                    int absval = Abs(aQIn[k]);
                    if (absval > maxabs)
                    {
                        maxabs = absval;
                        idx = k;
                    }
                }

                maxabs = RShiftRound(maxabs, qIn - qOut);
                if (maxabs > short.MaxValue)
                {
                    maxabs = Math.Min(maxabs, 163838);
                    int chirpQ16 = 65470 - (LShift(maxabs - short.MaxValue, 14) / ((maxabs * (idx + 1)) >> 2));
                    BwExpander32(aQIn, d, chirpQ16);
                }
                else
                {
                    break;
                }
            }

            if (i == 10)
            {
                for (int k = 0; k < d; k++)
                {
                    aQOut[k] = (short)Sat16(RShiftRound(aQIn[k], qIn - qOut));
                    aQIn[k] = LShift(aQOut[k], qIn - qOut);
                }
            }
            else
            {
                for (int k = 0; k < d; k++)
                {
                    aQOut[k] = (short)RShiftRound(aQIn[k], qIn - qOut);
                }
            }
        }
    }

    /// <summary>silk_bwexpander: chirps an int16 AR filter.</summary>
    public static void BwExpander(Span<short> ar, int d, int chirpQ16)
    {
        unchecked
        {
            int chirpMinusOneQ16 = chirpQ16 - 65536;
            for (int i = 0; i < d - 1; i++)
            {
                ar[i] = (short)RShiftRound(chirpQ16 * ar[i], 16);
                chirpQ16 += RShiftRound(chirpQ16 * chirpMinusOneQ16, 16);
            }

            ar[d - 1] = (short)RShiftRound(chirpQ16 * ar[d - 1], 16);
        }
    }

    /// <summary>silk_bwexpander_32: chirps an int32 AR filter.</summary>
    public static void BwExpander32(Span<int> ar, int d, int chirpQ16)
    {
        unchecked
        {
            int chirpMinusOneQ16 = chirpQ16 - 65536;
            for (int i = 0; i < d - 1; i++)
            {
                ar[i] = Smulww(chirpQ16, ar[i]);
                chirpQ16 += RShiftRound(chirpQ16 * chirpMinusOneQ16, 16);
            }

            ar[d - 1] = Smulww(chirpQ16, ar[d - 1]);
        }
    }

    private static int MulFracQ(int a32, int b32, int q) => (int)RShiftRound64(Smull(a32, b32), q);

    /// <summary>LPC_inverse_pred_gain_QA_c.</summary>
    private static int LpcInversePredGainQa(Span<int> aQa, int order)
    {
        unchecked
        {
            int invGainQ30 = 1 << 30;
            int rcQ31;
            int rcMult1Q30;
            int k;
            for (k = order - 1; k > 0; k--)
            {
                if (aQa[k] > ALimit || aQa[k] < -ALimit)
                {
                    return 0;
                }

                rcQ31 = -LShift(aQa[k], 31 - InvGainQa);
                rcMult1Q30 = (1 << 30) - Smmul(rcQ31, rcQ31);
                invGainQ30 = LShift(Smmul(invGainQ30, rcMult1Q30), 2);
                if (invGainQ30 < MinInvGainQ30)
                {
                    return 0;
                }

                int mult2Q = 32 - Clz32(Abs(rcMult1Q30));
                int rcMult2 = Inverse32VarQ(rcMult1Q30, mult2Q + 30);
                for (int n = 0; n < (k + 1) >> 1; n++)
                {
                    int tmp1 = aQa[n];
                    int tmp2 = aQa[k - n - 1];
                    long tmp64 = RShiftRound64(Smull(SubSat32(tmp1, MulFracQ(tmp2, rcQ31, 31)), rcMult2), mult2Q);
                    if (tmp64 > int.MaxValue || tmp64 < int.MinValue)
                    {
                        return 0;
                    }

                    aQa[n] = (int)tmp64;
                    tmp64 = RShiftRound64(Smull(SubSat32(tmp2, MulFracQ(tmp1, rcQ31, 31)), rcMult2), mult2Q);
                    if (tmp64 > int.MaxValue || tmp64 < int.MinValue)
                    {
                        return 0;
                    }

                    aQa[k - n - 1] = (int)tmp64;
                }
            }

            if (aQa[k] > ALimit || aQa[k] < -ALimit)
            {
                return 0;
            }

            rcQ31 = -LShift(aQa[0], 31 - InvGainQa);
            rcMult1Q30 = (1 << 30) - Smmul(rcQ31, rcQ31);
            invGainQ30 = LShift(Smmul(invGainQ30, rcMult1Q30), 2);
            if (invGainQ30 < MinInvGainQ30)
            {
                return 0;
            }

            return invGainQ30;
        }
    }

    /// <summary>silk_LPC_inverse_pred_gain_c: the inverse prediction gain in Q30, or 0 if the filter is unstable.</summary>
    public static int LpcInversePredGain(ReadOnlySpan<short> aQ12, int order)
    {
        Span<int> atmpQa = stackalloc int[MaxOrderLpc];
        int dcResp = 0;
        for (int k = 0; k < order; k++)
        {
            dcResp += aQ12[k];
            atmpQa[k] = LShift(aQ12[k], InvGainQa - 12);
        }

        if (dcResp >= 4096)
        {
            return 0;
        }

        return LpcInversePredGainQa(atmpQa, order);
    }

    /// <summary>silk_insertion_sort_increasing_all_values_int16.</summary>
    public static void InsertionSortIncreasingAllValues(Span<short> a, int length)
    {
        for (int i = 1; i < length; i++)
        {
            int value = a[i];
            int j;
            for (j = i - 1; j >= 0 && value < a[j]; j--)
            {
                a[j + 1] = a[j];
            }

            a[j + 1] = (short)value;
        }
    }

    /// <summary>silk_NLSF_stabilize: moves NLSFs apart and away from the borders.</summary>
    public static void NlsfStabilize(Span<short> nlsfQ15, ReadOnlySpan<short> nDeltaMinQ15, int length)
    {
        const int MaxLoops = 20;
        int loops;
        unchecked
        {
            for (loops = 0; loops < MaxLoops; loops++)
            {
                int minDiffQ15 = nlsfQ15[0] - nDeltaMinQ15[0];
                int index = 0;
                int diffQ15;
                for (int i = 1; i <= length - 1; i++)
                {
                    diffQ15 = nlsfQ15[i] - (nlsfQ15[i - 1] + nDeltaMinQ15[i]);
                    if (diffQ15 < minDiffQ15)
                    {
                        minDiffQ15 = diffQ15;
                        index = i;
                    }
                }

                diffQ15 = (1 << 15) - (nlsfQ15[length - 1] + nDeltaMinQ15[length]);
                if (diffQ15 < minDiffQ15)
                {
                    minDiffQ15 = diffQ15;
                    index = length;
                }

                if (minDiffQ15 >= 0)
                {
                    return;
                }

                if (index == 0)
                {
                    nlsfQ15[0] = nDeltaMinQ15[0];
                }
                else if (index == length)
                {
                    nlsfQ15[length - 1] = (short)((1 << 15) - nDeltaMinQ15[length]);
                }
                else
                {
                    int minCenterQ15 = 0;
                    for (int k = 0; k < index; k++)
                    {
                        minCenterQ15 += nDeltaMinQ15[k];
                    }

                    minCenterQ15 += nDeltaMinQ15[index] >> 1;
                    int maxCenterQ15 = 1 << 15;
                    for (int k = length; k > index; k--)
                    {
                        maxCenterQ15 -= nDeltaMinQ15[k];
                    }

                    maxCenterQ15 -= nDeltaMinQ15[index] >> 1;
                    short centerFreqQ15 = (short)Limit(RShiftRound(nlsfQ15[index - 1] + nlsfQ15[index], 1), minCenterQ15, maxCenterQ15);
                    nlsfQ15[index - 1] = (short)(centerFreqQ15 - (nDeltaMinQ15[index] >> 1));
                    nlsfQ15[index] = (short)(nlsfQ15[index - 1] + nDeltaMinQ15[index]);
                }
            }

            if (loops == MaxLoops)
            {
                InsertionSortIncreasingAllValues(nlsfQ15, length);
                nlsfQ15[0] = (short)Math.Max(nlsfQ15[0], nDeltaMinQ15[0]);
                for (int i = 1; i < length; i++)
                {
                    nlsfQ15[i] = (short)Math.Max(nlsfQ15[i], AddSat16(nlsfQ15[i - 1], nDeltaMinQ15[i]));
                }

                nlsfQ15[length - 1] = (short)Math.Min(nlsfQ15[length - 1], (1 << 15) - nDeltaMinQ15[length]);
                for (int i = length - 2; i >= 0; i--)
                {
                    nlsfQ15[i] = (short)Math.Min(nlsfQ15[i], nlsfQ15[i + 1] - nDeltaMinQ15[i + 1]);
                }
            }
        }
    }

    /// <summary>silk_NLSF_unpack: entropy table indices and predictor for a first-stage index.</summary>
    public static void NlsfUnpack(Span<short> ecIx, Span<byte> predQ8, SilkNlsfCodebook codebook, int cb1Index)
    {
        ReadOnlySpan<byte> ecSel = codebook.EcSel;
        ReadOnlySpan<byte> pred = codebook.PredQ8;
        int order = codebook.Order;
        int sel = cb1Index * order / 2;
        for (int i = 0; i < order; i += 2)
        {
            int entry = ecSel[sel++];
            ecIx[i] = (short)Smulbb((entry >> 1) & 7, (2 * NlsfQuantMaxAmplitude) + 1);
            predQ8[i] = pred[i + ((entry & 1) * (order - 1))];
            ecIx[i + 1] = (short)Smulbb((entry >> 5) & 7, (2 * NlsfQuantMaxAmplitude) + 1);
            predQ8[i + 1] = pred[i + (((entry >> 4) & 1) * (order - 1)) + 1];
        }
    }

    /// <summary>silk_NLSF_residual_dequant.</summary>
    private static void NlsfResidualDequant(Span<short> xQ10, ReadOnlySpan<sbyte> indices, ReadOnlySpan<byte> predCoefQ8, int quantStepSizeQ16, int order)
    {
        unchecked
        {
            int outQ10 = 0;
            for (int i = order - 1; i >= 0; i--)
            {
                int predQ10 = Smulbb(outQ10, predCoefQ8[i]) >> 8;
                outQ10 = LShift(indices[i], 10);
                if (outQ10 > 0)
                {
                    outQ10 -= 102;
                }
                else if (outQ10 < 0)
                {
                    outQ10 += 102;
                }

                outQ10 = Smlawb(predQ10, outQ10, quantStepSizeQ16);
                xQ10[i] = (short)outQ10;
            }
        }
    }

    /// <summary>silk_NLSF_decode: the quantized NLSF vector from its codebook path.</summary>
    public static void NlsfDecode(Span<short> nlsfQ15, ReadOnlySpan<sbyte> nlsfIndices, SilkNlsfCodebook codebook)
    {
        Span<byte> predQ8 = stackalloc byte[16];
        Span<short> ecIx = stackalloc short[16];
        Span<short> resQ10 = stackalloc short[16];
        int order = codebook.Order;
        NlsfUnpack(ecIx, predQ8, codebook, nlsfIndices[0]);
        NlsfResidualDequant(resQ10, nlsfIndices[1..], predQ8, codebook.QuantStepSizeQ16, order);
        ReadOnlySpan<byte> cbElement = codebook.Cb1NlsfQ8.Slice(nlsfIndices[0] * order, order);
        ReadOnlySpan<short> cbWghtQ9 = codebook.Cb1WghtQ9.Slice(nlsfIndices[0] * order, order);
        unchecked
        {
            for (int i = 0; i < order; i++)
            {
                int nlsfQ15Tmp = AddLShift32(LShift(resQ10[i], 14) / cbWghtQ9[i], cbElement[i], 7);
                nlsfQ15[i] = (short)Limit(nlsfQ15Tmp, 0, 32767);
            }
        }

        NlsfStabilize(nlsfQ15, codebook.DeltaMinQ15, order);
    }

    /// <summary>silk_decode_pitch: the pitch lags of the subframes.</summary>
    public static void DecodePitch(int lagIndex, int contourIndex, Span<int> pitchLags, int fsKhz, int nbSubfr)
    {
        ReadOnlySpan<sbyte> lagCb;
        int cbkSize;
        if (fsKhz == 8)
        {
            if (nbSubfr == 4)
            {
                lagCb = SilkTables.CbLagsStage2;
                cbkSize = 11;
            }
            else
            {
                lagCb = SilkTables.CbLagsStage210Ms;
                cbkSize = 3;
            }
        }
        else if (nbSubfr == 4)
        {
            lagCb = SilkTables.CbLagsStage3;
            cbkSize = 34;
        }
        else
        {
            lagCb = SilkTables.CbLagsStage310Ms;
            cbkSize = 12;
        }

        int minLag = Smulbb(2, fsKhz);
        int maxLag = Smulbb(18, fsKhz);
        int lag = minLag + lagIndex;
        for (int k = 0; k < nbSubfr; k++)
        {
            pitchLags[k] = Limit(lag + lagCb[(k * cbkSize) + contourIndex], minLag, maxLag);
        }
    }

    /// <summary>silk_gains_dequant: gains in Q16 from gain indices, uniform on a log scale.</summary>
    public static void GainsDequant(Span<int> gainQ16, ReadOnlySpan<sbyte> ind, ref sbyte prevInd, bool conditional, int nbSubfr)
    {
        const int NLevelsQGain = 64;
        const int MaxDeltaGainQuant = 36;
        const int MinDeltaGainQuant = -4;
        const int Offset = ((2 * 128) / 6) + (16 * 128);
        const int InvScaleQ16 = (65536 * (((88 - 2) * 128) / 6)) / (NLevelsQGain - 1);
        unchecked
        {
            for (int k = 0; k < nbSubfr; k++)
            {
                if (k == 0 && !conditional)
                {
                    prevInd = (sbyte)Math.Max(ind[k], prevInd - 16);
                }
                else
                {
                    int indTmp = ind[k] + MinDeltaGainQuant;
                    int doubleStepSizeThreshold = (2 * MaxDeltaGainQuant) - NLevelsQGain + prevInd;
                    if (indTmp > doubleStepSizeThreshold)
                    {
                        prevInd = (sbyte)(prevInd + (LShift(indTmp, 1) - doubleStepSizeThreshold));
                    }
                    else
                    {
                        prevInd = (sbyte)(prevInd + indTmp);
                    }
                }

                prevInd = (sbyte)Limit(prevInd, 0, NLevelsQGain - 1);
                gainQ16[k] = Log2Lin(Math.Min(Smulwb(InvScaleQ16, prevInd) + Offset, 3967));
            }
        }
    }

    /// <summary>silk_lin2log: about 128 * log2().</summary>
    public static int Lin2Log(int inLin)
    {
        ClzFrac(inLin, out int lz, out int fracQ7);
        return AddLShift32(Smlawb(fracQ7, fracQ7 * (128 - fracQ7), 179), 31 - lz, 7);
    }

    /// <summary>silk_log2lin: about 2^(inLog_Q7 / 128).</summary>
    public static int Log2Lin(int inLogQ7)
    {
        if (inLogQ7 < 0)
        {
            return 0;
        }

        if (inLogQ7 >= 3967)
        {
            return int.MaxValue;
        }

        unchecked
        {
            int output = LShift(1, inLogQ7 >> 7);
            int fracQ7 = inLogQ7 & 0x7F;
            if (inLogQ7 < 2048)
            {
                output += (output * Smlawb(fracQ7, Smulbb(fracQ7, 128 - fracQ7), -174)) >> 7;
            }
            else
            {
                output = Mla(output, output >> 7, Smlawb(fracQ7, Smulbb(fracQ7, 128 - fracQ7), -174));
            }

            return output;
        }
    }

    /// <summary>silk_sum_sqr_shift: the energy of x and the right shift that makes it fit in 32 bits.</summary>
    public static void SumSqrShift(out int energy, out int shift, ReadOnlySpan<short> x, int length)
    {
        unchecked
        {
            int shft = 31 - Clz32(length);
            int nrg = length;
            int i;
            uint nrgTmp;
            for (i = 0; i < length - 1; i += 2)
            {
                nrgTmp = (uint)Smulbb(x[i], x[i]);
                nrgTmp = (uint)SmlabbOvflw((int)nrgTmp, x[i + 1], x[i + 1]);
                nrg = (int)((uint)nrg + (nrgTmp >> shft));
            }

            if (i < length)
            {
                nrgTmp = (uint)Smulbb(x[i], x[i]);
                nrg = (int)((uint)nrg + (nrgTmp >> shft));
            }

            shft = Math.Max(0, shft + 3 - Clz32(nrg));
            nrg = 0;
            for (i = 0; i < length - 1; i += 2)
            {
                nrgTmp = (uint)Smulbb(x[i], x[i]);
                nrgTmp = (uint)SmlabbOvflw((int)nrgTmp, x[i + 1], x[i + 1]);
                nrg = (int)((uint)nrg + (nrgTmp >> shft));
            }

            if (i < length)
            {
                nrgTmp = (uint)Smulbb(x[i], x[i]);
                nrg = (int)((uint)nrg + (nrgTmp >> shft));
            }

            shift = shft;
            energy = nrg;
        }
    }

    /// <summary>silk_LPC_analysis_filter: whitens <paramref name="input"/>; the first d outputs are zero.</summary>
    public static void LpcAnalysisFilter(Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> b, int length, int d)
    {
        unchecked
        {
            for (int ix = d; ix < length; ix++)
            {
                int p = ix - 1;
                int out32Q12 = Smulbb(input[p], b[0]);
                out32Q12 = SmlabbOvflw(out32Q12, input[p - 1], b[1]);
                out32Q12 = SmlabbOvflw(out32Q12, input[p - 2], b[2]);
                out32Q12 = SmlabbOvflw(out32Q12, input[p - 3], b[3]);
                out32Q12 = SmlabbOvflw(out32Q12, input[p - 4], b[4]);
                out32Q12 = SmlabbOvflw(out32Q12, input[p - 5], b[5]);
                for (int j = 6; j < d; j += 2)
                {
                    out32Q12 = SmlabbOvflw(out32Q12, input[p - j], b[j]);
                    out32Q12 = SmlabbOvflw(out32Q12, input[p - j - 1], b[j + 1]);
                }

                out32Q12 = SubOvflw(LShift(input[p + 1], 12), out32Q12);
                output[ix] = (short)Sat16(RShiftRound(out32Q12, 12));
            }

            output[..d].Clear();
        }
    }
}
