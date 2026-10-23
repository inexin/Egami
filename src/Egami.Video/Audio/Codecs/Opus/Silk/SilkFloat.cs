// Translated from libopus 1.5.2 silk/float/SigProc_FLP.h, inner_product_FLP.c (the order of x86/inner_product_FLP_avx2.c),
// energy_FLP.c, autocorrelation_FLP.c, warped_autocorrelation_FLP.c, schur_FLP.c, k2a_FLP.c, bwexpander_FLP.c,
// burg_modified_FLP.c, LPC_analysis_filter_FLP.c, LPC_inv_pred_gain_FLP.c, apply_sine_window_FLP.c, corrMatrix_FLP.c,
// scale_vector_FLP.c, scale_copy_vector_FLP.c, sort_FLP.c, LTP_analysis_filter_FLP.c, residual_energy_FLP.c and
// wrappers_FLP.c (float build).
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// The float signal processing of SILK's encoder. Float expressions round like the C (each float operation rounds to float,
/// double literals and casts widen); <see cref="InnerProduct"/> adds in the order of libopus's AVX2 version, which the x86-64
/// reference build picks at run time, like <see cref="CeltMath.PitchXcorr"/>.
/// </summary>
internal static class SilkFloat
{
    /// <summary>The PI of SigProc_FLP.h.</summary>
    public const float Pi = 3.1415926536f;

    /// <summary>silk_float2int: rounds to the nearest integer, ties to even (lrintf).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Float2Int(float x) => (int)MathF.Round(x);

    /// <summary>silk_sigmoid.</summary>
    public static float Sigmoid(float x) => (float)(1.0 / (1.0 + Math.Exp(-x)));

    /// <summary>silk_log2.</summary>
    public static float Log2(double x) => (float)(3.32192809488736 * Math.Log10(x));

    /// <summary>silk_float2short_array.</summary>
    public static void Float2ShortArray(Span<short> output, ReadOnlySpan<float> input, int length)
    {
        for (int k = length - 1; k >= 0; k--)
        {
            output[k] = (short)SilkMath.Sat16(Float2Int(input[k]));
        }
    }

    /// <summary>silk_short2float_array.</summary>
    public static void Short2FloatArray(Span<float> output, ReadOnlySpan<short> input, int length)
    {
        for (int k = length - 1; k >= 0; k--)
        {
            output[k] = input[k];
        }
    }

    /// <summary>silk_inner_product_FLP: the inner product of two float vectors as a double.</summary>
    public static double InnerProduct(ReadOnlySpan<float> data1, ReadOnlySpan<float> data2, int dataSize)
    {
        int i = 0;
        Vector256<double> accum1 = Vector256<double>.Zero;
        Vector256<double> accum2 = Vector256<double>.Zero;
        for (; i < dataSize - 7; i += 8)
        {
            accum1 = Vector256.FusedMultiplyAdd(Widen(data1, i), Widen(data2, i), accum1);
            accum2 = Vector256.FusedMultiplyAdd(Widen(data1, i + 4), Widen(data2, i + 4), accum2);
        }

        for (; i < dataSize - 3; i += 4)
        {
            accum1 = Vector256.FusedMultiplyAdd(Widen(data1, i), Widen(data2, i), accum1);
        }

        accum1 += accum2;
        double result = (accum1.GetElement(0) + accum1.GetElement(2)) + (accum1.GetElement(1) + accum1.GetElement(3));
        for (; i < dataSize; i++)
        {
            result = Math.FusedMultiplyAdd(data1[i], (double)data2[i], result);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Widen(ReadOnlySpan<float> data, int i)
    {
        Vector128<float> v = Vector128.Create(data.Slice(i, 4));
        return Avx.IsSupported ? Avx.ConvertToVector256Double(v) : Vector256.Create((double)v[0], v[1], v[2], v[3]);
    }

    /// <summary>silk_energy_FLP: the sum of squares as a double.</summary>
    public static double Energy(ReadOnlySpan<float> data, int dataSize)
    {
        double result = 0.0;
        int i;
        for (i = 0; i < dataSize - 3; i += 4)
        {
            result += (data[i] * (double)data[i]) + (data[i + 1] * (double)data[i + 1]) +
                      (data[i + 2] * (double)data[i + 2]) + (data[i + 3] * (double)data[i + 3]);
        }

        for (; i < dataSize; i++)
        {
            result += data[i] * (double)data[i];
        }

        return result;
    }

    /// <summary>silk_autocorrelation_FLP.</summary>
    public static void Autocorrelation(Span<float> results, ReadOnlySpan<float> inputData, int inputDataSize, int correlationCount)
    {
        if (correlationCount > inputDataSize)
        {
            correlationCount = inputDataSize;
        }

        for (int i = 0; i < correlationCount; i++)
        {
            results[i] = (float)InnerProduct(inputData, inputData[i..], inputDataSize - i);
        }
    }

    /// <summary>silk_warped_autocorrelation_FLP.</summary>
    public static void WarpedAutocorrelation(Span<float> corr, ReadOnlySpan<float> input, float warping, int length, int order)
    {
        Span<double> state = stackalloc double[SilkEncoderConstants.MaxShapeLpcOrder + 1];
        Span<double> c = stackalloc double[SilkEncoderConstants.MaxShapeLpcOrder + 1];
        state.Clear();
        c.Clear();
        for (int n = 0; n < length; n++)
        {
            double tmp1 = input[n];
            double tmp2;
            for (int i = 0; i < order; i += 2)
            {
                tmp2 = state[i] + (warping * state[i + 1]) - (warping * tmp1);
                state[i] = tmp1;
                c[i] += state[0] * tmp1;
                tmp1 = state[i + 1] + (warping * state[i + 2]) - (warping * tmp2);
                state[i + 1] = tmp2;
                c[i + 1] += state[0] * tmp2;
            }

            state[order] = tmp1;
            c[order] += state[0] * tmp1;
        }

        for (int i = 0; i < order + 1; i++)
        {
            corr[i] = (float)c[i];
        }
    }

    /// <summary>silk_schur_FLP: reflection coefficients from autocorrelations; returns the residual energy.</summary>
    public static float Schur(Span<float> reflCoef, ReadOnlySpan<float> autoCorr, int order)
    {
        Span<double> c = stackalloc double[2 * (SilkSigProc.MaxOrderLpc + 1)];
        int k = 0;
        do
        {
            c[2 * k] = c[(2 * k) + 1] = autoCorr[k];
        }
        while (++k <= order);

        for (k = 0; k < order; k++)
        {
            double rcTmp = -c[2 * (k + 1)] / Math.Max(c[1], 1e-9f);
            reflCoef[k] = (float)rcTmp;
            for (int n = 0; n < order - k; n++)
            {
                double ctmp1 = c[2 * (n + k + 1)];
                double ctmp2 = c[(2 * n) + 1];
                c[2 * (n + k + 1)] = ctmp1 + (ctmp2 * rcTmp);
                c[(2 * n) + 1] = ctmp2 + (ctmp1 * rcTmp);
            }
        }

        return (float)c[1];
    }

    /// <summary>silk_k2a_FLP: reflection to prediction coefficients.</summary>
    public static void K2A(Span<float> a, ReadOnlySpan<float> rc, int order)
    {
        for (int k = 0; k < order; k++)
        {
            float rck = rc[k];
            for (int n = 0; n < (k + 1) >> 1; n++)
            {
                float tmp1 = a[n];
                float tmp2 = a[k - n - 1];
                a[n] = tmp1 + (tmp2 * rck);
                a[k - n - 1] = tmp2 + (tmp1 * rck);
            }

            a[k] = -rck;
        }
    }

    /// <summary>silk_bwexpander_FLP: chirps an AR filter.</summary>
    public static void BwExpander(Span<float> ar, int d, float chirp)
    {
        float cfac = chirp;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] *= cfac;
            cfac *= chirp;
        }

        ar[d - 1] *= cfac;
    }

    /// <summary>silk_apply_sine_window_FLP: window type 1 rises from 0 to pi/2, type 2 falls from pi/2 to pi.</summary>
    public static void ApplySineWindow(Span<float> pxWin, ReadOnlySpan<float> px, int winType, int length)
    {
        float freq = Pi / (length + 1);
        float c = 2.0f - (freq * freq);
        float s0, s1;
        if (winType < 2)
        {
            s0 = 0.0f;
            s1 = freq;
        }
        else
        {
            s0 = 1.0f;
            s1 = 0.5f * c;
        }

        for (int k = 0; k < length; k += 4)
        {
            pxWin[k] = px[k] * 0.5f * (s0 + s1);
            pxWin[k + 1] = px[k + 1] * s1;
            s0 = (c * s1) - s0;
            pxWin[k + 2] = px[k + 2] * 0.5f * (s1 + s0);
            pxWin[k + 3] = px[k + 3] * s0;
            s1 = (c * s0) - s1;
        }
    }

    /// <summary>
    /// silk_LPC_analysis_filter_FLP: the residual of <paramref name="s"/> starting at <paramref name="sOffset"/>; the first
    /// <paramref name="order"/> outputs are zero.
    /// </summary>
    public static void LpcAnalysisFilter(Span<float> rLpc, ReadOnlySpan<float> predCoef, ReadOnlySpan<float> s, int sOffset, int length, int order)
    {
        for (int ix = order; ix < length; ix++)
        {
            int p = sOffset + ix - 1;
            float lpcPred = s[p] * predCoef[0];
            for (int j = 1; j < order; j++)
            {
                lpcPred += s[p - j] * predCoef[j];
            }

            rLpc[ix] = s[p + 1] - lpcPred;
        }

        rLpc[..order].Clear();
    }

    /// <summary>silk_LPC_inverse_pred_gain_FLP: the inverse prediction gain, or 0 for an unstable filter.</summary>
    public static float LpcInversePredGain(ReadOnlySpan<float> a, int order)
    {
        Span<float> atmp = stackalloc float[SilkSigProc.MaxOrderLpc];
        a[..order].CopyTo(atmp);
        double invGain = 1.0;
        double rc, rcMult1;
        for (int k = order - 1; k > 0; k--)
        {
            rc = -atmp[k];
            rcMult1 = 1.0f - (rc * rc);
            invGain *= rcMult1;
            if (invGain * SilkEncoderConstants.MaxPredictionPowerGain < 1.0f)
            {
                return 0.0f;
            }

            double rcMult2 = 1.0f / rcMult1;
            for (int n = 0; n < (k + 1) >> 1; n++)
            {
                double tmp1 = atmp[n];
                double tmp2 = atmp[k - n - 1];
                atmp[n] = (float)((tmp1 - (tmp2 * rc)) * rcMult2);
                atmp[k - n - 1] = (float)((tmp2 - (tmp1 * rc)) * rcMult2);
            }
        }

        rc = -atmp[0];
        rcMult1 = 1.0f - (rc * rc);
        invGain *= rcMult1;
        if (invGain * SilkEncoderConstants.MaxPredictionPowerGain < 1.0f)
        {
            return 0.0f;
        }

        return (float)invGain;
    }

    /// <summary>silk_burg_modified_FLP: LPC by the modified Burg method over stacked subframes; returns the residual energy.</summary>
    public static float BurgModified(Span<float> a, ReadOnlySpan<float> x, float minInvGain, int subfrLength, int nbSubfr, int d)
    {
        const int MaxOrder = SilkSigProc.MaxOrderLpc;
        Span<double> cFirstRow = stackalloc double[MaxOrder];
        Span<double> cLastRow = stackalloc double[MaxOrder];
        Span<double> caf = stackalloc double[MaxOrder + 1];
        Span<double> cab = stackalloc double[MaxOrder + 1];
        Span<double> af = stackalloc double[MaxOrder];
        double c0 = Energy(x, nbSubfr * subfrLength);
        cFirstRow.Clear();
        for (int s = 0; s < nbSubfr; s++)
        {
            ReadOnlySpan<float> xPtr = x[(s * subfrLength)..];
            for (int n = 1; n < d + 1; n++)
            {
                cFirstRow[n - 1] += InnerProduct(xPtr, xPtr[n..], subfrLength - n);
            }
        }

        cFirstRow.CopyTo(cLastRow);
        cab[0] = caf[0] = c0 + (SilkEncoderConstants.FindLpcCondFac * c0) + 1e-9f;
        double invGain = 1.0f;
        bool reachedMaxGain = false;
        double tmp1, tmp2, atmp, rc;
        for (int n = 0; n < d; n++)
        {
            for (int s = 0; s < nbSubfr; s++)
            {
                ReadOnlySpan<float> xPtr = x[(s * subfrLength)..];
                tmp1 = xPtr[n];
                tmp2 = xPtr[subfrLength - n - 1];
                for (int k = 0; k < n; k++)
                {
                    cFirstRow[k] -= xPtr[n] * xPtr[n - k - 1];
                    cLastRow[k] -= xPtr[subfrLength - n - 1] * xPtr[subfrLength - n + k];
                    atmp = af[k];
                    tmp1 += xPtr[n - k - 1] * atmp;
                    tmp2 += xPtr[subfrLength - n + k] * atmp;
                }

                for (int k = 0; k <= n; k++)
                {
                    caf[k] -= tmp1 * xPtr[n - k];
                    cab[k] -= tmp2 * xPtr[subfrLength - n + k - 1];
                }
            }

            tmp1 = cFirstRow[n];
            tmp2 = cLastRow[n];
            for (int k = 0; k < n; k++)
            {
                atmp = af[k];
                tmp1 += cLastRow[n - k - 1] * atmp;
                tmp2 += cFirstRow[n - k - 1] * atmp;
            }

            caf[n + 1] = tmp1;
            cab[n + 1] = tmp2;
            double num = cab[n + 1];
            double nrgB = cab[0];
            double nrgF = caf[0];
            for (int k = 0; k < n; k++)
            {
                atmp = af[k];
                num += cab[n - k] * atmp;
                nrgB += cab[k + 1] * atmp;
                nrgF += caf[k + 1] * atmp;
            }

            rc = -2.0 * num / (nrgF + nrgB);
            tmp1 = invGain * (1.0 - (rc * rc));
            if (tmp1 <= minInvGain)
            {
                rc = Math.Sqrt(1.0 - (minInvGain / invGain));
                if (num > 0)
                {
                    rc = -rc;
                }

                invGain = minInvGain;
                reachedMaxGain = true;
            }
            else
            {
                invGain = tmp1;
            }

            for (int k = 0; k < (n + 1) >> 1; k++)
            {
                tmp1 = af[k];
                tmp2 = af[n - k - 1];
                af[k] = tmp1 + (rc * tmp2);
                af[n - k - 1] = tmp2 + (rc * tmp1);
            }

            af[n] = rc;
            if (reachedMaxGain)
            {
                for (int k = n + 1; k < d; k++)
                {
                    af[k] = 0.0;
                }

                break;
            }

            for (int k = 0; k <= n + 1; k++)
            {
                tmp1 = caf[k];
                caf[k] += rc * cab[n - k + 1];
                cab[n - k + 1] += rc * tmp1;
            }
        }

        double nrgFOut;
        if (reachedMaxGain)
        {
            for (int k = 0; k < d; k++)
            {
                a[k] = (float)(-af[k]);
            }

            for (int s = 0; s < nbSubfr; s++)
            {
                c0 -= Energy(x[(s * subfrLength)..], d);
            }

            nrgFOut = c0 * invGain;
        }
        else
        {
            nrgFOut = caf[0];
            tmp1 = 1.0;
            for (int k = 0; k < d; k++)
            {
                atmp = af[k];
                nrgFOut += caf[k + 1] * atmp;
                tmp1 += atmp * atmp;
                a[k] = (float)(-atmp);
            }

            nrgFOut -= SilkEncoderConstants.FindLpcCondFac * c0 * tmp1;
        }

        return (float)nrgFOut;
    }

    /// <summary>silk_corrVector_FLP: X'*t, where the columns of X are <paramref name="x"/> shifted back by 0 to order - 1.</summary>
    public static void CorrVector(ReadOnlySpan<float> x, ReadOnlySpan<float> t, int l, int order, Span<float> xt)
    {
        int ptr1 = order - 1;
        for (int lag = 0; lag < order; lag++)
        {
            xt[lag] = (float)InnerProduct(x[ptr1..], t, l);
            ptr1--;
        }
    }

    /// <summary>silk_corrMatrix_FLP: X'*X.</summary>
    public static void CorrMatrix(ReadOnlySpan<float> x, int l, int order, Span<float> xx)
    {
        int ptr1 = order - 1;
        double energy = Energy(x[ptr1..], l);
        xx[0] = (float)energy;
        for (int j = 1; j < order; j++)
        {
            energy += (x[ptr1 - j] * x[ptr1 - j]) - (x[ptr1 + l - j] * x[ptr1 + l - j]);
            xx[(j * order) + j] = (float)energy;
        }

        int ptr2 = order - 2;
        for (int lag = 1; lag < order; lag++)
        {
            energy = InnerProduct(x[ptr1..], x[ptr2..], l);
            xx[(lag * order) + 0] = (float)energy;
            xx[lag] = (float)energy;
            for (int j = 1; j < order - lag; j++)
            {
                energy += (x[ptr1 - j] * x[ptr2 - j]) - (x[ptr1 + l - j] * x[ptr2 + l - j]);
                xx[((lag + j) * order) + j] = (float)energy;
                xx[(j * order) + lag + j] = (float)energy;
            }

            ptr2--;
        }
    }

    /// <summary>silk_scale_vector_FLP.</summary>
    public static void ScaleVector(Span<float> data, float gain, int dataSize)
    {
        for (int i = 0; i < dataSize; i++)
        {
            data[i] *= gain;
        }
    }

    /// <summary>silk_scale_copy_vector_FLP.</summary>
    public static void ScaleCopyVector(Span<float> dataOut, ReadOnlySpan<float> dataIn, float gain, int dataSize)
    {
        for (int i = 0; i < dataSize; i++)
        {
            dataOut[i] = gain * dataIn[i];
        }
    }

    /// <summary>silk_insertion_sort_decreasing_FLP: sorts the first K of L values in decreasing order, with their indices.</summary>
    public static void InsertionSortDecreasing(Span<float> a, Span<int> idx, int l, int k)
    {
        int i, j;
        float value;
        for (i = 0; i < k; i++)
        {
            idx[i] = i;
        }

        for (i = 1; i < k; i++)
        {
            value = a[i];
            for (j = i - 1; j >= 0 && value > a[j]; j--)
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
            if (value > a[k - 1])
            {
                for (j = k - 2; j >= 0 && value > a[j]; j--)
                {
                    a[j + 1] = a[j];
                    idx[j + 1] = idx[j];
                }

                a[j + 1] = value;
                idx[j + 1] = i;
            }
        }
    }

    /// <summary>silk_LTP_analysis_filter_FLP: the LTP residual of each subframe with its preceding samples, scaled by 1/gain.</summary>
    public static void LtpAnalysisFilter(Span<float> ltpRes, ReadOnlySpan<float> x, int xOffset, ReadOnlySpan<float> b, ReadOnlySpan<int> pitchL,
        ReadOnlySpan<float> invGains, int subfrLength, int nbSubfr, int preLength)
    {
        Span<float> btmp = stackalloc float[SilkChannelDecoder.LtpOrder];
        int xPtr = xOffset;
        int resPtr = 0;
        for (int k = 0; k < nbSubfr; k++)
        {
            int xLagPtr = xPtr - pitchL[k];
            float invGain = invGains[k];
            for (int i = 0; i < SilkChannelDecoder.LtpOrder; i++)
            {
                btmp[i] = b[(k * SilkChannelDecoder.LtpOrder) + i];
            }

            for (int i = 0; i < subfrLength + preLength; i++)
            {
                float r = x[xPtr + i];
                for (int j = 0; j < SilkChannelDecoder.LtpOrder; j++)
                {
                    r -= btmp[j] * x[xLagPtr + (SilkChannelDecoder.LtpOrder / 2) - j];
                }

                ltpRes[resPtr + i] = r * invGain;
                xLagPtr++;
            }

            resPtr += subfrLength + preLength;
            xPtr += subfrLength;
        }
    }

    /// <summary>silk_residual_energy_FLP: the residual energy of each subframe, each with LPC_order preceding samples.</summary>
    public static void ResidualEnergy(Span<float> nrgs, ReadOnlySpan<float> x, ReadOnlySpan<float> a0, ReadOnlySpan<float> a1, ReadOnlySpan<float> gains,
        int subfrLength, int nbSubfr, int lpcOrder)
    {
        Span<float> lpcRes = stackalloc float[(SilkChannelDecoder.MaxFrameLength + (SilkChannelDecoder.MaxNbSubfr * SilkChannelDecoder.MaxLpcOrder)) / 2];
        int shift = lpcOrder + subfrLength;
        LpcAnalysisFilter(lpcRes, a0, x, 0, 2 * shift, lpcOrder);
        nrgs[0] = (float)(gains[0] * gains[0] * Energy(lpcRes[lpcOrder..], subfrLength));
        nrgs[1] = (float)(gains[1] * gains[1] * Energy(lpcRes[(lpcOrder + shift)..], subfrLength));
        if (nbSubfr == SilkChannelDecoder.MaxNbSubfr)
        {
            LpcAnalysisFilter(lpcRes, a1, x, 2 * shift, 2 * shift, lpcOrder);
            nrgs[2] = (float)(gains[2] * gains[2] * Energy(lpcRes[lpcOrder..], subfrLength));
            nrgs[3] = (float)(gains[3] * gains[3] * Energy(lpcRes[(lpcOrder + shift)..], subfrLength));
        }
    }

    /// <summary>silk_A2NLSF_FLP.</summary>
    public static void A2Nlsf(Span<short> nlsfQ15, ReadOnlySpan<float> ar, int lpcOrder)
    {
        Span<int> aFixQ16 = stackalloc int[SilkChannelDecoder.MaxLpcOrder];
        for (int i = 0; i < lpcOrder; i++)
        {
            aFixQ16[i] = Float2Int(ar[i] * 65536.0f);
        }

        SilkEncoderQuant.A2Nlsf(nlsfQ15, aFixQ16, lpcOrder);
    }

    /// <summary>silk_NLSF2A_FLP.</summary>
    public static void Nlsf2A(Span<float> ar, ReadOnlySpan<short> nlsfQ15, int lpcOrder)
    {
        Span<short> aFixQ12 = stackalloc short[SilkChannelDecoder.MaxLpcOrder];
        SilkSigProc.Nlsf2A(aFixQ12, nlsfQ15, lpcOrder);
        for (int i = 0; i < lpcOrder; i++)
        {
            ar[i] = aFixQ12[i] * (1.0f / 4096.0f);
        }
    }
}
