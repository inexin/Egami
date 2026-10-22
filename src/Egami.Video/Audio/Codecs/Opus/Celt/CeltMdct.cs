// Translated from libopus 1.5.2 celt/kiss_fft.c, celt/mdct.c and the FFT states of celt/static_modes_float.h (float build).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// libopus's mixed-radix FFT (opus_fft_impl) for the four static FFT sizes of the 48 kHz mode (480, 240, 120 and 60) and the
/// inverse MDCT built on it (clt_mdct_backward). Complex values are interleaved: real at 2k, imaginary at 2k + 1.
/// </summary>
internal static class CeltMdct
{
    // The factors of fft_state48000_960_0..3: radix, then the remaining length, per stage.
    private static ReadOnlySpan<short> Factors =>
    [
        5, 96, 3, 32, 4, 8, 2, 4, 4, 1, 0, 0,
        5, 48, 3, 16, 4, 4, 4, 1, 0, 0, 0, 0,
        5, 24, 3, 8, 2, 4, 4, 1, 0, 0, 0, 0,
        5, 12, 3, 4, 4, 1, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>The bit-reverse table of the FFT that the MDCT of <paramref name="shift"/> uses.</summary>
    private static ReadOnlySpan<short> Bitrev(int shift) => shift switch
    {
        0 => CeltTables.FftBitrev480,
        1 => CeltTables.FftBitrev240,
        2 => CeltTables.FftBitrev120,
        _ => CeltTables.FftBitrev60,
    };

    /// <summary>
    /// opus_fft_impl for the static FFT state <paramref name="state"/> (0 to 3: 480, 240, 120 or 60 points), in place on
    /// <paramref name="fout"/>, which already holds the input in bit-reversed order. Unscaled.
    /// </summary>
    public static void Fft(int state, Span<float> fout)
    {
        ReadOnlySpan<short> factors = Factors.Slice(state * 12, 12);
        int shift = state > 0 ? state : 0;
        Span<int> fstride = stackalloc int[8];
        fstride[0] = 1;
        int l = 0;
        int m;
        do
        {
            int p = factors[2 * l];
            m = factors[(2 * l) + 1];
            fstride[l + 1] = fstride[l] * p;
            l++;
        }
        while (m != 1);
        m = factors[(2 * l) - 1];
        for (int i = l - 1; i >= 0; i--)
        {
            int m2 = i != 0 ? factors[(2 * i) - 1] : 1;
            switch (factors[2 * i])
            {
                case 2:
                    Bfly2(fout, fstride[i]);
                    break;
                case 4:
                    Bfly4(fout, fstride[i] << shift, m, fstride[i], m2);
                    break;
                case 3:
                    Bfly3(fout, fstride[i] << shift, m, fstride[i], m2);
                    break;
                case 5:
                    Bfly5(fout, fstride[i] << shift, m, fstride[i], m2);
                    break;
            }

            m = m2;
        }
    }

    /// <summary>
    /// clt_mdct_backward: the inverse MDCT of size 1920 &gt;&gt; <paramref name="shift"/>, reading every
    /// <paramref name="stride"/>-th coefficient of <paramref name="input"/> and writing N/2 + overlap samples to
    /// <paramref name="output"/> with the TDAC mirroring of the window over the first <paramref name="overlap"/> samples.
    /// </summary>
    public static void Backward(ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<float> window, int overlap, int shift, int stride)
    {
        ReadOnlySpan<float> trig = CeltTables.MdctTwiddles960;
        int n = CeltTables.MdctSize;
        int trigOffset = 0;
        for (int i = 0; i < shift; i++)
        {
            n >>= 1;
            trigOffset += n;
        }

        trig = trig[trigOffset..];
        int n2 = n >> 1;
        int n4 = n >> 2;

        // Pre-rotate, storing in bit-reversed order.
        Span<float> yp = output.Slice(overlap >> 1, n2);
        ReadOnlySpan<short> bitrev = Bitrev(shift);
        int xp1 = 0;
        int xp2 = stride * (n2 - 1);
        for (int i = 0; i < n4; i++)
        {
            int rev = bitrev[i];
            float yr = (input[xp2] * trig[i]) + (input[xp1] * trig[n4 + i]);
            float yi = (input[xp1] * trig[i]) - (input[xp2] * trig[n4 + i]);
            // Real and imaginary swap because this uses an FFT instead of an IFFT.
            yp[(2 * rev) + 1] = yr;
            yp[2 * rev] = yi;
            xp1 += 2 * stride;
            xp2 -= 2 * stride;
        }

        Fft(shift, yp);

        // Post-rotate and de-shuffle from both ends at once to work in place.
        int yp0 = 0;
        int yp1 = n2 - 2;
        for (int i = 0; i < (n4 + 1) >> 1; i++)
        {
            float re = yp[yp0 + 1];
            float im = yp[yp0];
            float t0 = trig[i];
            float t1 = trig[n4 + i];
            float yr = (re * t0) + (im * t1);
            float yi = (re * t1) - (im * t0);
            re = yp[yp1 + 1];
            im = yp[yp1];
            yp[yp0] = yr;
            yp[yp1 + 1] = yi;
            t0 = trig[n4 - i - 1];
            t1 = trig[n2 - i - 1];
            yr = (re * t0) + (im * t1);
            yi = (re * t1) - (im * t0);
            yp[yp1] = yr;
            yp[yp0 + 1] = yi;
            yp0 += 2;
            yp1 -= 2;
        }

        // Mirror on both sides for TDAC.
        int x1p = overlap - 1;
        int y1p = 0;
        int wp1 = 0;
        int wp2 = overlap - 1;
        for (int i = 0; i < overlap / 2; i++)
        {
            float x1 = output[x1p];
            float x2 = output[y1p];
            output[y1p++] = (window[wp2] * x2) - (window[wp1] * x1);
            output[x1p--] = (window[wp1] * x2) + (window[wp2] * x1);
            wp1++;
            wp2--;
        }
    }

    private static void Bfly2(Span<float> f, int n)
    {
        // m is 4 here: the radix-2 stage always follows a radix-4 one.
        const float Tw = 0.7071067812f;
        for (int i = 0; i < n; i++)
        {
            int a = 16 * i;
            int b = a + 8;
            float tr = f[b];
            float ti = f[b + 1];
            f[b] = f[a] - tr;
            f[b + 1] = f[a + 1] - ti;
            f[a] += tr;
            f[a + 1] += ti;

            tr = (f[b + 2] + f[b + 3]) * Tw;
            ti = (f[b + 3] - f[b + 2]) * Tw;
            f[b + 2] = f[a + 2] - tr;
            f[b + 3] = f[a + 3] - ti;
            f[a + 2] += tr;
            f[a + 3] += ti;

            tr = f[b + 5];
            ti = -f[b + 4];
            f[b + 4] = f[a + 4] - tr;
            f[b + 5] = f[a + 5] - ti;
            f[a + 4] += tr;
            f[a + 5] += ti;

            tr = (f[b + 7] - f[b + 6]) * Tw;
            ti = -(f[b + 7] + f[b + 6]) * Tw;
            f[b + 6] = f[a + 6] - tr;
            f[b + 7] = f[a + 7] - ti;
            f[a + 6] += tr;
            f[a + 7] += ti;
        }
    }

    private static void Bfly4(Span<float> f, int fstride, int m, int n, int mm)
    {
        if (m == 1)
        {
            // All twiddles are 1.
            for (int i = 0; i < n; i++)
            {
                int p = 8 * i;
                float s0r = f[p] - f[p + 4];
                float s0i = f[p + 1] - f[p + 5];
                f[p] += f[p + 4];
                f[p + 1] += f[p + 5];
                float s1r = f[p + 2] + f[p + 6];
                float s1i = f[p + 3] + f[p + 7];
                f[p + 4] = f[p] - s1r;
                f[p + 5] = f[p + 1] - s1i;
                f[p] += s1r;
                f[p + 1] += s1i;
                s1r = f[p + 2] - f[p + 6];
                s1i = f[p + 3] - f[p + 7];
                f[p + 2] = s0r + s1i;
                f[p + 3] = s0i - s1r;
                f[p + 6] = s0r - s1i;
                f[p + 7] = s0i + s1r;
            }

            return;
        }

        ReadOnlySpan<float> tw = CeltTables.FftTwiddles48000_960;
        int m2 = 2 * m;
        int m3 = 3 * m;
        for (int i = 0; i < n; i++)
        {
            int p = 2 * i * mm;
            int tw1 = 0, tw2 = 0, tw3 = 0;
            for (int j = 0; j < m; j++, p += 2)
            {
                int q1 = p + (2 * m);
                int q2 = p + (2 * m2);
                int q3 = p + (2 * m3);
                float s0r = (f[q1] * tw[2 * tw1]) - (f[q1 + 1] * tw[(2 * tw1) + 1]);
                float s0i = (f[q1] * tw[(2 * tw1) + 1]) + (f[q1 + 1] * tw[2 * tw1]);
                float s1r = (f[q2] * tw[2 * tw2]) - (f[q2 + 1] * tw[(2 * tw2) + 1]);
                float s1i = (f[q2] * tw[(2 * tw2) + 1]) + (f[q2 + 1] * tw[2 * tw2]);
                float s2r = (f[q3] * tw[2 * tw3]) - (f[q3 + 1] * tw[(2 * tw3) + 1]);
                float s2i = (f[q3] * tw[(2 * tw3) + 1]) + (f[q3 + 1] * tw[2 * tw3]);
                float s5r = f[p] - s1r;
                float s5i = f[p + 1] - s1i;
                f[p] += s1r;
                f[p + 1] += s1i;
                float s3r = s0r + s2r;
                float s3i = s0i + s2i;
                float s4r = s0r - s2r;
                float s4i = s0i - s2i;
                f[q2] = f[p] - s3r;
                f[q2 + 1] = f[p + 1] - s3i;
                tw1 += fstride;
                tw2 += fstride * 2;
                tw3 += fstride * 3;
                f[p] += s3r;
                f[p + 1] += s3i;
                f[q1] = s5r + s4i;
                f[q1 + 1] = s5i - s4r;
                f[q3] = s5r - s4i;
                f[q3 + 1] = s5i + s4r;
            }
        }
    }

    private static void Bfly3(Span<float> f, int fstride, int m, int n, int mm)
    {
        ReadOnlySpan<float> tw = CeltTables.FftTwiddles48000_960;
        int m2 = 2 * m;
        float epi3i = tw[(2 * fstride * m) + 1];
        for (int i = 0; i < n; i++)
        {
            int p = 2 * i * mm;
            int tw1 = 0, tw2 = 0;
            for (int k = m; k > 0; k--, p += 2)
            {
                int q1 = p + (2 * m);
                int q2 = p + (2 * m2);
                float s1r = (f[q1] * tw[2 * tw1]) - (f[q1 + 1] * tw[(2 * tw1) + 1]);
                float s1i = (f[q1] * tw[(2 * tw1) + 1]) + (f[q1 + 1] * tw[2 * tw1]);
                float s2r = (f[q2] * tw[2 * tw2]) - (f[q2 + 1] * tw[(2 * tw2) + 1]);
                float s2i = (f[q2] * tw[(2 * tw2) + 1]) + (f[q2 + 1] * tw[2 * tw2]);
                float s3r = s1r + s2r;
                float s3i = s1i + s2i;
                float s0r = s1r - s2r;
                float s0i = s1i - s2i;
                tw1 += fstride;
                tw2 += fstride * 2;
                f[q1] = f[p] - (s3r * 0.5f);
                f[q1 + 1] = f[p + 1] - (s3i * 0.5f);
                s0r *= epi3i;
                s0i *= epi3i;
                f[p] += s3r;
                f[p + 1] += s3i;
                f[q2] = f[q1] + s0i;
                f[q2 + 1] = f[q1 + 1] - s0r;
                f[q1] -= s0i;
                f[q1 + 1] += s0r;
            }
        }
    }

    private static void Bfly5(Span<float> f, int fstride, int m, int n, int mm)
    {
        ReadOnlySpan<float> tw = CeltTables.FftTwiddles48000_960;
        float yar = tw[2 * fstride * m];
        float yai = tw[(2 * fstride * m) + 1];
        float ybr = tw[2 * fstride * 2 * m];
        float ybi = tw[(2 * fstride * 2 * m) + 1];
        for (int i = 0; i < n; i++)
        {
            int f0 = 2 * i * mm;
            int f1 = f0 + (2 * m);
            int f2 = f0 + (4 * m);
            int f3 = f0 + (6 * m);
            int f4 = f0 + (8 * m);
            for (int u = 0; u < m; u++, f0 += 2, f1 += 2, f2 += 2, f3 += 2, f4 += 2)
            {
                float s0r = f[f0];
                float s0i = f[f0 + 1];
                int t1 = 2 * u * fstride;
                int t2 = 4 * u * fstride;
                int t3 = 6 * u * fstride;
                int t4 = 8 * u * fstride;
                float s1r = (f[f1] * tw[t1]) - (f[f1 + 1] * tw[t1 + 1]);
                float s1i = (f[f1] * tw[t1 + 1]) + (f[f1 + 1] * tw[t1]);
                float s2r = (f[f2] * tw[t2]) - (f[f2 + 1] * tw[t2 + 1]);
                float s2i = (f[f2] * tw[t2 + 1]) + (f[f2 + 1] * tw[t2]);
                float s3r = (f[f3] * tw[t3]) - (f[f3 + 1] * tw[t3 + 1]);
                float s3i = (f[f3] * tw[t3 + 1]) + (f[f3 + 1] * tw[t3]);
                float s4r = (f[f4] * tw[t4]) - (f[f4 + 1] * tw[t4 + 1]);
                float s4i = (f[f4] * tw[t4 + 1]) + (f[f4 + 1] * tw[t4]);
                float s7r = s1r + s4r, s7i = s1i + s4i;
                float s10r = s1r - s4r, s10i = s1i - s4i;
                float s8r = s2r + s3r, s8i = s2i + s3i;
                float s9r = s2r - s3r, s9i = s2i - s3i;
                f[f0] = s0r + (s7r + s8r);
                f[f0 + 1] = s0i + (s7i + s8i);
                float s5r = s0r + ((s7r * yar) + (s8r * ybr));
                float s5i = s0i + ((s7i * yar) + (s8i * ybr));
                float s6r = (s10i * yai) + (s9i * ybi);
                float s6i = -((s10r * yai) + (s9r * ybi));
                f[f1] = s5r - s6r;
                f[f1 + 1] = s5i - s6i;
                f[f4] = s5r + s6r;
                f[f4 + 1] = s5i + s6i;
                float s11r = s0r + ((s7r * ybr) + (s8r * yar));
                float s11i = s0i + ((s7i * ybr) + (s8i * yar));
                float s12r = (s9i * yai) - (s10i * ybi);
                float s12i = (s10r * ybi) - (s9r * yai);
                f[f2] = s11r + s12r;
                f[f2 + 1] = s11i + s12i;
                f[f3] = s11r - s12r;
                f[f3 + 1] = s11i - s12i;
            }
        }
    }
}
