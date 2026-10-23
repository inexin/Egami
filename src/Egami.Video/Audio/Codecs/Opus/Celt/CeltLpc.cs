// Translated from libopus 1.5.2 celt/celt_lpc.c and celt/pitch.c (float build; the decoder's packet loss concealment and the encoder's prefilter use them).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>The LPC analysis and filters and the pitch search of libopus's CELT, for the concealment and the encoder's prefilter.</summary>
internal static class CeltLpc
{
    /// <summary>CELT_LPC_ORDER.</summary>
    public const int Order = 24;

    /// <summary>_celt_lpc: Levinson-Durbin recursion from <paramref name="p"/> + 1 autocorrelation values.</summary>
    public static void Lpc(Span<float> lpc, ReadOnlySpan<float> ac, int p)
    {
        float error = ac[0];
        lpc[..p].Clear();
        if (ac[0] > 1e-10f)
        {
            for (int i = 0; i < p; i++)
            {
                // Sum up this iteration's reflection coefficient.
                float rr = 0;
                for (int j = 0; j < i; j++)
                {
                    rr += lpc[j] * ac[i - j];
                }

                rr += ac[i + 1];
                float r = -(rr / error);
                // Update LPC coefficients and total error.
                lpc[i] = r;
                for (int j = 0; j < (i + 1) >> 1; j++)
                {
                    float tmp1 = lpc[j];
                    float tmp2 = lpc[i - 1 - j];
                    lpc[j] = tmp1 + (r * tmp2);
                    lpc[i - 1 - j] = tmp2 + (r * tmp1);
                }

                error -= r * r * error;
                // Bail out once we get 30 dB gain.
                if (error <= .001f * ac[0])
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// celt_fir: y[i] = x[i] + sum of num[j] x[i - j - 1], where x starts at <paramref name="xOffset"/> in
    /// <paramref name="buffer"/> and has <paramref name="ord"/> samples of history before it.
    /// </summary>
    public static void Fir(ReadOnlySpan<float> buffer, int xOffset, ReadOnlySpan<float> num, Span<float> y, int n, int ord)
    {
        Span<float> rnum = stackalloc float[ord];
        for (int i = 0; i < ord; i++)
        {
            rnum[i] = num[ord - i - 1];
        }

        Span<float> sum = stackalloc float[4];
        int k = 0;
        for (; k < n - 3; k += 4)
        {
            int x = xOffset + k;
            sum[0] = buffer[x];
            sum[1] = buffer[x + 1];
            sum[2] = buffer[x + 2];
            sum[3] = buffer[x + 3];
            CeltMath.XcorrKernel(rnum, buffer[(x - ord)..], sum, ord);
            y[k] = sum[0];
            y[k + 1] = sum[1];
            y[k + 2] = sum[2];
            y[k + 3] = sum[3];
        }

        for (; k < n; k++)
        {
            int x = xOffset + k;
            float s = buffer[x];
            for (int j = 0; j < ord; j++)
            {
                s += rnum[j] * buffer[x + j - ord];
            }

            y[k] = s;
        }
    }

    /// <summary>celt_iir: the all-pole filter 1/(1 + sum den[j] z^-(j+1)); <paramref name="x"/> and <paramref name="y"/> may be the same.</summary>
    public static void Iir(ReadOnlySpan<float> x, ReadOnlySpan<float> den, Span<float> y, int n, int ord, Span<float> mem)
    {
        Span<float> rden = stackalloc float[ord];
        float[] yy = new float[n + ord];
        for (int i = 0; i < ord; i++)
        {
            rden[i] = den[ord - i - 1];
        }

        for (int i = 0; i < ord; i++)
        {
            yy[i] = -mem[ord - i - 1];
        }

        Span<float> sum = stackalloc float[4];
        int k = 0;
        for (; k < n - 3; k += 4)
        {
            // Unroll by 4 as if it were an FIR filter.
            sum[0] = x[k];
            sum[1] = x[k + 1];
            sum[2] = x[k + 2];
            sum[3] = x[k + 3];
            CeltMath.XcorrKernel(rden, yy.AsSpan(k), sum, ord);
            // Patch up the result to compensate for the fact that this is an IIR.
            yy[k + ord] = -sum[0];
            y[k] = sum[0];
            sum[1] += yy[k + ord] * den[0];
            yy[k + ord + 1] = -sum[1];
            y[k + 1] = sum[1];
            sum[2] += yy[k + ord + 1] * den[0];
            sum[2] += yy[k + ord] * den[1];
            yy[k + ord + 2] = -sum[2];
            y[k + 2] = sum[2];
            sum[3] += yy[k + ord + 2] * den[0];
            sum[3] += yy[k + ord + 1] * den[1];
            sum[3] += yy[k + ord] * den[2];
            yy[k + ord + 3] = -sum[3];
            y[k + 3] = sum[3];
        }

        for (; k < n; k++)
        {
            float s = x[k];
            for (int j = 0; j < ord; j++)
            {
                s -= rden[j] * yy[k + j];
            }

            yy[k + ord] = s;
            y[k] = s;
        }

        for (int i = 0; i < ord; i++)
        {
            mem[i] = y[n - i - 1];
        }
    }

    /// <summary>_celt_autocorr: <paramref name="lag"/> + 1 autocorrelation values of n samples, windowed over the overlap.</summary>
    public static void Autocorr(ReadOnlySpan<float> x, Span<float> ac, ReadOnlySpan<float> window, int overlap, int lag, int n)
    {
        int fastN = n - lag;
        ReadOnlySpan<float> xptr = x;
        if (overlap != 0)
        {
            float[] xx = new float[n];
            x[..n].CopyTo(xx);
            for (int i = 0; i < overlap; i++)
            {
                xx[i] = x[i] * window[i];
                xx[n - i - 1] = x[n - i - 1] * window[i];
            }

            xptr = xx;
        }

        CeltMath.PitchXcorr(xptr, xptr, ac, fastN, lag + 1);
        for (int k = 0; k <= lag; k++)
        {
            float d = 0;
            for (int i = k + fastN; i < n; i++)
            {
                d += xptr[i] * xptr[i - k];
            }

            ac[k] += d;
        }
    }

    /// <summary>pitch_downsample: low-passes and halves the sample rate of one or two channels into <paramref name="xLp"/>.</summary>
    public static void PitchDownsample(ReadOnlySpan<float> x0, ReadOnlySpan<float> x1, Span<float> xLp, int len, int c)
    {
        Span<float> ac = stackalloc float[5];
        Span<float> lpc = stackalloc float[4];
        Span<float> lpc2 = stackalloc float[5];
        float tmp = 1f;
        const float C1 = .8f;
        for (int i = 1; i < len >> 1; i++)
        {
            xLp[i] = (.25f * x0[(2 * i) - 1]) + (.25f * x0[(2 * i) + 1]) + (.5f * x0[2 * i]);
        }

        xLp[0] = (.25f * x0[1]) + (.5f * x0[0]);
        if (c == 2)
        {
            for (int i = 1; i < len >> 1; i++)
            {
                xLp[i] += (.25f * x1[(2 * i) - 1]) + (.25f * x1[(2 * i) + 1]) + (.5f * x1[2 * i]);
            }

            xLp[0] += (.25f * x1[1]) + (.5f * x1[0]);
        }

        Autocorr(xLp, ac, default, 0, 4, len >> 1);
        // Noise floor -40 dB.
        ac[0] *= 1.0001f;
        // Lag windowing.
        for (int i = 1; i <= 4; i++)
        {
            ac[i] -= ac[i] * (.008f * i) * (.008f * i);
        }

        Lpc(lpc, ac, 4);
        for (int i = 0; i < 4; i++)
        {
            tmp = .9f * tmp;
            lpc[i] *= tmp;
        }

        // Add a zero.
        lpc2[0] = lpc[0] + .8f;
        lpc2[1] = lpc[1] + (C1 * lpc[0]);
        lpc2[2] = lpc[2] + (C1 * lpc[1]);
        lpc2[3] = lpc[3] + (C1 * lpc[2]);
        lpc2[4] = C1 * lpc[3];
        Fir5(xLp, lpc2, len >> 1);
    }

    /// <summary>pitch_search: the lag in [0, maxPitch) that best matches <paramref name="xLp"/> in <paramref name="y"/>.</summary>
    public static int PitchSearch(ReadOnlySpan<float> xLp, ReadOnlySpan<float> y, int len, int maxPitch)
    {
        Span<int> bestPitch = [0, 0];
        int lag = len + maxPitch;
        Span<float> xLp4 = stackalloc float[len >> 2];
        Span<float> yLp4 = stackalloc float[lag >> 2];
        Span<float> xcorr = stackalloc float[maxPitch >> 1];

        // Downsample by 2 again.
        for (int j = 0; j < len >> 2; j++)
        {
            xLp4[j] = xLp[2 * j];
        }

        for (int j = 0; j < lag >> 2; j++)
        {
            yLp4[j] = y[2 * j];
        }

        // Coarse search with 4x decimation.
        CeltMath.PitchXcorr(xLp4, yLp4, xcorr, len >> 2, maxPitch >> 2);
        FindBestPitch(xcorr, yLp4, len >> 2, maxPitch >> 2, bestPitch);

        // Finer search with 2x decimation.
        for (int i = 0; i < maxPitch >> 1; i++)
        {
            xcorr[i] = 0;
            if (Math.Abs(i - (2 * bestPitch[0])) > 2 && Math.Abs(i - (2 * bestPitch[1])) > 2)
            {
                continue;
            }

            float sum = CeltMath.InnerProduct(xLp, y[i..], len >> 1);
            xcorr[i] = MathF.Max(-1, sum);
        }

        FindBestPitch(xcorr, y, len >> 1, maxPitch >> 1, bestPitch);

        // Refine by pseudo-interpolation.
        int offset;
        if (bestPitch[0] > 0 && bestPitch[0] < (maxPitch >> 1) - 1)
        {
            float a = xcorr[bestPitch[0] - 1];
            float b = xcorr[bestPitch[0]];
            float c = xcorr[bestPitch[0] + 1];
            if ((c - a) > (.7f * (b - a)))
            {
                offset = 1;
            }
            else if ((a - c) > (.7f * (b - c)))
            {
                offset = -1;
            }
            else
            {
                offset = 0;
            }
        }
        else
        {
            offset = 0;
        }

        return (2 * bestPitch[0]) - offset;
    }

    /// <summary>
    /// remove_doubling: checks the pitch <paramref name="t0"/> found on the half-rate <paramref name="x"/> (which holds
    /// <paramref name="maxPeriod"/>/2 samples of history before the <paramref name="n"/>/2 current ones) against its submultiples, so
    /// that the period is not a multiple of the true one; updates <paramref name="t0"/> and returns the pitch gain.
    /// </summary>
    public static float RemoveDoubling(ReadOnlySpan<float> x, int maxPeriod, int minPeriod, int n, ref int t0, int prevPeriod, float prevGain)
    {
        ReadOnlySpan<int> secondCheck = [0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2];
        int minPeriod0 = minPeriod;
        maxPeriod /= 2;
        minPeriod /= 2;
        t0 /= 2;
        prevPeriod /= 2;
        n /= 2;
        int xo = maxPeriod;
        if (t0 >= maxPeriod)
        {
            t0 = maxPeriod - 1;
        }

        int t = t0;
        int tt0 = t0;
        Span<float> yyLookup = stackalloc float[maxPeriod + 1];
        ReadOnlySpan<float> xs = x[xo..];
        CeltMath.DualInnerProduct(xs, xs, x[(xo - tt0)..], n, out float xx, out float xy);
        yyLookup[0] = xx;
        float yy = xx;
        for (int i = 1; i <= maxPeriod; i++)
        {
            yy = yy + (x[xo - i] * x[xo - i]) - (x[xo + n - i] * x[xo + n - i]);
            yyLookup[i] = MathF.Max(0, yy);
        }

        yy = yyLookup[tt0];
        float bestXy = xy;
        float bestYy = yy;
        float g0 = ComputePitchGain(xy, xx, yy);
        float g = g0;
        // Look for any pitch at T/k.
        for (int k = 2; k <= 15; k++)
        {
            int t1 = (int)((uint)((2 * tt0) + k) / (uint)(2 * k));
            if (t1 < minPeriod)
            {
                break;
            }

            // Look for another strong correlation at T1b.
            int t1b;
            if (k == 2)
            {
                t1b = t1 + tt0 > maxPeriod ? tt0 : tt0 + t1;
            }
            else
            {
                t1b = (int)((uint)((2 * secondCheck[k] * tt0) + k) / (uint)(2 * k));
            }

            CeltMath.DualInnerProduct(xs, x[(xo - t1)..], x[(xo - t1b)..], n, out xy, out float xy2);
            xy = .5f * (xy + xy2);
            yy = .5f * (yyLookup[t1] + yyLookup[t1b]);
            float g1 = ComputePitchGain(xy, xx, yy);
            float cont;
            if (Math.Abs(t1 - prevPeriod) <= 1)
            {
                cont = prevGain;
            }
            else if (Math.Abs(t1 - prevPeriod) <= 2 && 5 * k * k < tt0)
            {
                cont = .5f * prevGain;
            }
            else
            {
                cont = 0;
            }

            float thresh = MathF.Max(.3f, (.7f * g0) - cont);
            // Bias against very high pitch (very short period) to avoid false-positives due to short-term correlation.
            if (t1 < 3 * minPeriod)
            {
                thresh = MathF.Max(.4f, (.85f * g0) - cont);
            }
            else if (t1 < 2 * minPeriod)
            {
                thresh = MathF.Max(.5f, (.9f * g0) - cont);
            }

            if (g1 > thresh)
            {
                bestXy = xy;
                bestYy = yy;
                t = t1;
                g = g1;
            }
        }

        bestXy = MathF.Max(0, bestXy);
        float pg = bestYy <= bestXy ? 1f : bestXy / (bestYy + 1);
        Span<float> xcorr = stackalloc float[3];
        for (int k = 0; k < 3; k++)
        {
            xcorr[k] = CeltMath.InnerProduct(xs, x[(xo - (t + k - 1))..], n);
        }

        int offset;
        if (xcorr[2] - xcorr[0] > .7f * (xcorr[1] - xcorr[0]))
        {
            offset = 1;
        }
        else if (xcorr[0] - xcorr[2] > .7f * (xcorr[1] - xcorr[2]))
        {
            offset = -1;
        }
        else
        {
            offset = 0;
        }

        if (pg > g)
        {
            pg = g;
        }

        t0 = (2 * t) + offset;
        if (t0 < minPeriod0)
        {
            t0 = minPeriod0;
        }

        return pg;
    }

    private static float ComputePitchGain(float xy, float xx, float yy) => xy / CeltMath.Sqrt(1 + (xx * yy));

    private static void FindBestPitch(ReadOnlySpan<float> xcorr, ReadOnlySpan<float> y, int len, int maxPitch, Span<int> bestPitch)
    {
        float syy = 1;
        float bestNum0 = -1, bestNum1 = -1;
        float bestDen0 = 0, bestDen1 = 0;
        bestPitch[0] = 0;
        bestPitch[1] = 1;
        for (int j = 0; j < len; j++)
        {
            syy += y[j] * y[j];
        }

        for (int i = 0; i < maxPitch; i++)
        {
            if (xcorr[i] > 0)
            {
                // Considering the range of xcorr16, this avoids both underflows and overflows when squaring it.
                float xcorr16 = xcorr[i] * 1e-12f;
                float num = xcorr16 * xcorr16;
                if (num * bestDen1 > bestNum1 * syy)
                {
                    if (num * bestDen0 > bestNum0 * syy)
                    {
                        bestNum1 = bestNum0;
                        bestDen1 = bestDen0;
                        bestPitch[1] = bestPitch[0];
                        bestNum0 = num;
                        bestDen0 = syy;
                        bestPitch[0] = i;
                    }
                    else
                    {
                        bestNum1 = num;
                        bestDen1 = syy;
                        bestPitch[1] = i;
                    }
                }
            }

            syy += (y[i + len] * y[i + len]) - (y[i] * y[i]);
            syy = MathF.Max(1, syy);
        }
    }

    private static void Fir5(Span<float> x, ReadOnlySpan<float> num, int n)
    {
        float num0 = num[0], num1 = num[1], num2 = num[2], num3 = num[3], num4 = num[4];
        float mem0 = 0, mem1 = 0, mem2 = 0, mem3 = 0, mem4 = 0;
        for (int i = 0; i < n; i++)
        {
            float sum = x[i];
            sum += num0 * mem0;
            sum += num1 * mem1;
            sum += num2 * mem2;
            sum += num3 * mem3;
            sum += num4 * mem4;
            mem4 = mem3;
            mem3 = mem2;
            mem2 = mem1;
            mem1 = mem0;
            mem0 = x[i];
            x[i] = sum;
        }
    }
}
