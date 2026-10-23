// Translated from libopus 1.5.2 celt/mdct.c (clt_mdct_forward_c, float build) and the FFT scales of celt/static_modes_float.h.
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>The forward MDCT of CELT's encoder.</summary>
internal static partial class CeltMdct
{
    // The scale of fft_state48000_960_0..3.
    private static ReadOnlySpan<float> FftScales => [0.002083333f, 0.004166667f, 0.008333333f, 0.016666667f];

    /// <summary>
    /// clt_mdct_forward: the MDCT of size 1920 &gt;&gt; <paramref name="shift"/> of N/2 + overlap samples of <paramref name="input"/>,
    /// windowed over <paramref name="overlap"/> samples at each end, writing N/2 coefficients to every <paramref name="stride"/>-th value
    /// of <paramref name="output"/>.
    /// </summary>
    public static void Forward(ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<float> window, int overlap, int shift, int stride)
    {
        ReadOnlySpan<float> trig = CeltTables.MdctTwiddles960;
        float scale = FftScales[shift];
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
        Span<float> f = stackalloc float[n2];
        Span<float> f2 = stackalloc float[2 * n4];

        // Consider the input to be composed of four blocks: [a, b, c, d]. Window, shuffle, fold.
        {
            int xp1 = overlap >> 1;
            int xp2 = n2 - 1 + (overlap >> 1);
            int yp = 0;
            int wp1 = overlap >> 1;
            int wp2 = (overlap >> 1) - 1;
            int i;
            for (i = 0; i < (overlap + 3) >> 2; i++)
            {
                // Real part arranged as -d-cR, Imag part arranged as -b+aR.
                f[yp++] = (window[wp2] * input[xp1 + n2]) + (window[wp1] * input[xp2]);
                f[yp++] = (window[wp1] * input[xp1]) - (window[wp2] * input[xp2 - n2]);
                xp1 += 2;
                xp2 -= 2;
                wp1 += 2;
                wp2 -= 2;
            }

            wp1 = 0;
            wp2 = overlap - 1;
            for (; i < n4 - ((overlap + 3) >> 2); i++)
            {
                // Real part arranged as a-bR, Imag part arranged as -c-dR.
                f[yp++] = input[xp2];
                f[yp++] = input[xp1];
                xp1 += 2;
                xp2 -= 2;
            }

            for (; i < n4; i++)
            {
                // Real part arranged as a-bR, Imag part arranged as -c-dR.
                f[yp++] = -(window[wp1] * input[xp1 - n2]) + (window[wp2] * input[xp2]);
                f[yp++] = (window[wp2] * input[xp1]) + (window[wp1] * input[xp2 + n2]);
                xp1 += 2;
                xp2 -= 2;
                wp1 += 2;
                wp2 -= 2;
            }
        }

        // Pre-rotation.
        ReadOnlySpan<short> bitrev = Bitrev(shift);
        for (int i = 0; i < n4; i++)
        {
            float t0 = trig[i];
            float t1 = trig[n4 + i];
            float re = f[2 * i];
            float im = f[(2 * i) + 1];
            float yr = (re * t0) - (im * t1);
            float yi = (im * t0) + (re * t1);
            int rev = bitrev[i];
            f2[2 * rev] = scale * yr;
            f2[(2 * rev) + 1] = scale * yi;
        }

        // N/4 complex FFT, does not downscale anymore.
        Fft(shift, f2);

        // Post-rotate.
        int yp1 = 0;
        int yp2 = stride * (n2 - 1);
        for (int i = 0; i < n4; i++)
        {
            float fr = f2[2 * i];
            float fi = f2[(2 * i) + 1];
            float yr = (fi * trig[n4 + i]) - (fr * trig[i]);
            float yi = (fr * trig[n4 + i]) + (fi * trig[i]);
            output[yp1] = yr;
            output[yp2] = yi;
            yp1 += 2 * stride;
            yp2 -= 2 * stride;
        }
    }
}
