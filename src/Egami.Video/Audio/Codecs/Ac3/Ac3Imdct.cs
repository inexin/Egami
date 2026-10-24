using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// The AC-3 inverse transform (ATSC A/52:2018 section 7.9.4): a 512-point IMDCT through a 128-point complex IFFT, or two
/// 256-point ones through 64-point IFFTs for a block-switched block, then the KBD window and the overlap-add with the second
/// half of the previous block.
/// </summary>
internal sealed class Ac3Imdct
{
    private readonly float[] _cos1 = new float[128];
    private readonly float[] _sin1 = new float[128];
    private readonly float[] _cos2 = new float[64];
    private readonly float[] _sin2 = new float[64];
    private readonly float[] _re = new float[128];
    private readonly float[] _im = new float[128];
    private readonly float[] _re2 = new float[64];
    private readonly float[] _im2 = new float[64];
    private readonly ComplexFft _fft128 = new(7);
    private readonly ComplexFft _fft64 = new(6);

    public Ac3Imdct()
    {
        for (int k = 0; k < 128; k++)
        {
            double a = 2 * Math.PI * ((8 * k) + 1) / (8 * 512.0);
            _cos1[k] = (float)-Math.Cos(a);
            _sin1[k] = (float)-Math.Sin(a);
        }

        for (int k = 0; k < 64; k++)
        {
            double a = 2 * Math.PI * ((8 * k) + 1) / (4 * 512.0);
            _cos2[k] = (float)-Math.Cos(a);
            _sin2[k] = (float)-Math.Sin(a);
        }
    }

    /// <summary>Transforms 256 coefficients into 256 output samples, updating the 256-sample delay line.</summary>
    /// <param name="coefficients">The block's transform coefficients (interleaved pairs for a block-switched block).</param>
    /// <param name="shortBlocks">Whether the block was split into two 256-sample transforms (blksw).</param>
    /// <param name="delay">The second half of the previous block's windowed output, replaced by this block's.</param>
    /// <param name="output">The 256 samples.</param>
    public void Transform(ReadOnlySpan<float> coefficients, bool shortBlocks, Span<float> delay, Span<float> output)
    {
        ReadOnlySpan<float> x = coefficients[..256];
        Span<float> d = delay[..256];
        Span<float> pcm = output[..256];
        ReadOnlySpan<float> w = Ac3Tables.Window;
        float[] re = _re;
        float[] im = _im;
        if (!shortBlocks)
        {
            float[] c1 = _cos1;
            float[] s1 = _sin1;
            int[] rev = _fft128.Reverse;
            for (int k = 0; k < 128; k++)
            {
                float a = x[255 - (2 * k)];
                float b = x[2 * k];
                int at = rev[k];
                re[at] = (a * c1[k]) - (b * s1[k]);
                im[at] = (b * c1[k]) + (a * s1[k]);
            }

            _fft128.Inverse(re, im);
            for (int n = 0; n < 128; n++)
            {
                float zr = re[n];
                float zi = im[n];
                re[n] = (zr * c1[n]) - (zi * s1[n]);
                im[n] = (zi * c1[n]) + (zr * s1[n]);
            }

            for (int n = 0; n < 64; n++)
            {
                int e = 2 * n;
                pcm[e] = 2 * ((-im[64 + n] * w[e]) + d[e]);
                pcm[e + 1] = 2 * ((re[63 - n] * w[e + 1]) + d[e + 1]);
                pcm[128 + e] = 2 * ((-re[n] * w[128 + e]) + d[128 + e]);
                pcm[129 + e] = 2 * ((im[127 - n] * w[129 + e]) + d[129 + e]);
                d[e] = -re[64 + n] * w[255 - e];
                d[e + 1] = im[63 - n] * w[254 - e];
                d[128 + e] = im[n] * w[127 - e];
                d[129 + e] = -re[127 - n] * w[126 - e];
            }

            return;
        }

        float[] re2 = _re2;
        float[] im2 = _im2;
        float[] c2 = _cos2;
        float[] s2 = _sin2;
        int[] rev64 = _fft64.Reverse;
        for (int k = 0; k < 64; k++)
        {
            // X1[k] = X[2k] and X2[k] = X[2k + 1]: X1[127 - 2k] is X[254 - 4k], X1[2k] is X[4k].
            float a1 = x[254 - (4 * k)];
            float b1 = x[4 * k];
            float a2 = x[255 - (4 * k)];
            float b2 = x[(4 * k) + 1];
            int at = rev64[k];
            re[at] = (a1 * c2[k]) - (b1 * s2[k]);
            im[at] = (b1 * c2[k]) + (a1 * s2[k]);
            re2[at] = (a2 * c2[k]) - (b2 * s2[k]);
            im2[at] = (b2 * c2[k]) + (a2 * s2[k]);
        }

        _fft64.Inverse(re, im);
        _fft64.Inverse(re2, im2);
        for (int n = 0; n < 64; n++)
        {
            float zr = re[n];
            float zi = im[n];
            re[n] = (zr * c2[n]) - (zi * s2[n]);
            im[n] = (zi * c2[n]) + (zr * s2[n]);
            zr = re2[n];
            zi = im2[n];
            re2[n] = (zr * c2[n]) - (zi * s2[n]);
            im2[n] = (zi * c2[n]) + (zr * s2[n]);
        }

        for (int n = 0; n < 64; n++)
        {
            int e = 2 * n;
            pcm[e] = 2 * ((-im[n] * w[e]) + d[e]);
            pcm[e + 1] = 2 * ((re[63 - n] * w[e + 1]) + d[e + 1]);
            pcm[128 + e] = 2 * ((-re[n] * w[128 + e]) + d[128 + e]);
            pcm[129 + e] = 2 * ((im[63 - n] * w[129 + e]) + d[129 + e]);
            d[e] = -re2[n] * w[255 - e];
            d[e + 1] = im2[63 - n] * w[254 - e];
            d[128 + e] = im2[n] * w[127 - e];
            d[129 + e] = -re2[63 - n] * w[126 - e];
        }
    }

    /// <summary>
    /// An in-place radix-2 complex inverse FFT, z[n] = sum Z[k] e^(+2 pi j k n / N), unscaled. The input goes in bit-reversed
    /// order (<see cref="Reverse"/>), which the pre-twiddle writes directly; the stages past the second run four butterflies at a
    /// time on SIMD hardware.
    /// </summary>
    private sealed class ComplexFft
    {
        private readonly int _size;
        private readonly float[] _cos;
        private readonly float[] _sin;

        public ComplexFft(int bits)
        {
            _size = 1 << bits;
            Reverse = new int[_size];
            for (int i = 0; i < _size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++)
                {
                    r |= ((i >> b) & 1) << (bits - 1 - b);
                }

                Reverse[i] = r;
            }

            // Twiddles by stage: the stage of half-size h keeps e^(2 pi j k / 2h) for k below h at index h + k.
            _cos = new float[_size];
            _sin = new float[_size];
            for (int half = 1; half < _size; half <<= 1)
            {
                for (int k = 0; k < half; k++)
                {
                    _cos[half + k] = (float)Math.Cos(Math.PI * k / half);
                    _sin[half + k] = (float)Math.Sin(Math.PI * k / half);
                }
            }
        }

        /// <summary>Gets where each input index goes: the bit-reversed index.</summary>
        public int[] Reverse { get; }

        public void Inverse(float[] re, float[] im)
        {
            int n = _size;
            ref float r = ref MemoryMarshal.GetArrayDataReference(re);
            ref float m = ref MemoryMarshal.GetArrayDataReference(im);

            // The first two stages need no multiplies.
            for (int i = 0; i < n; i += 4)
            {
                float r0 = Unsafe.Add(ref r, i), i0 = Unsafe.Add(ref m, i), r1 = Unsafe.Add(ref r, i + 1), i1 = Unsafe.Add(ref m, i + 1);
                float r2 = Unsafe.Add(ref r, i + 2), i2 = Unsafe.Add(ref m, i + 2), r3 = Unsafe.Add(ref r, i + 3), i3 = Unsafe.Add(ref m, i + 3);
                float ar = r0 + r1, ai = i0 + i1, br = r0 - r1, bi = i0 - i1;
                float cr = r2 + r3, ci = i2 + i3, dr = r2 - r3, di = i2 - i3;
                Unsafe.Add(ref r, i) = ar + cr;
                Unsafe.Add(ref m, i) = ai + ci;
                Unsafe.Add(ref r, i + 2) = ar - cr;
                Unsafe.Add(ref m, i + 2) = ai - ci;

                // d times +j.
                Unsafe.Add(ref r, i + 1) = br - di;
                Unsafe.Add(ref m, i + 1) = bi + dr;
                Unsafe.Add(ref r, i + 3) = br + di;
                Unsafe.Add(ref m, i + 3) = bi - dr;
            }

            ref float c = ref MemoryMarshal.GetArrayDataReference(_cos);
            ref float s = ref MemoryMarshal.GetArrayDataReference(_sin);
            bool simd = Vector128.IsHardwareAccelerated;
            for (int half = 4; half < n; half <<= 1)
            {
                for (int start = 0; start < n; start += 2 * half)
                {
                    int k = 0;
                    if (simd)
                    {
                        for (; k < half; k += 4)
                        {
                            nuint a = (nuint)(start + k);
                            nuint b = a + (nuint)half;
                            Vector128<float> wc = Vector128.LoadUnsafe(ref c, (nuint)(half + k));
                            Vector128<float> ws = Vector128.LoadUnsafe(ref s, (nuint)(half + k));
                            Vector128<float> br = Vector128.LoadUnsafe(ref r, b);
                            Vector128<float> bi = Vector128.LoadUnsafe(ref m, b);
                            Vector128<float> tr = (br * wc) - (bi * ws);
                            Vector128<float> ti = (br * ws) + (bi * wc);
                            Vector128<float> ar = Vector128.LoadUnsafe(ref r, a);
                            Vector128<float> ai = Vector128.LoadUnsafe(ref m, a);
                            (ar - tr).StoreUnsafe(ref r, b);
                            (ai - ti).StoreUnsafe(ref m, b);
                            (ar + tr).StoreUnsafe(ref r, a);
                            (ai + ti).StoreUnsafe(ref m, a);
                        }
                    }

                    for (; k < half; k++)
                    {
                        float wc = Unsafe.Add(ref c, half + k);
                        float ws = Unsafe.Add(ref s, half + k);
                        int a = start + k;
                        int b = a + half;
                        float tr = (Unsafe.Add(ref r, b) * wc) - (Unsafe.Add(ref m, b) * ws);
                        float ti = (Unsafe.Add(ref r, b) * ws) + (Unsafe.Add(ref m, b) * wc);
                        Unsafe.Add(ref r, b) = Unsafe.Add(ref r, a) - tr;
                        Unsafe.Add(ref m, b) = Unsafe.Add(ref m, a) - ti;
                        Unsafe.Add(ref r, a) += tr;
                        Unsafe.Add(ref m, a) += ti;
                    }
                }
            }
        }
    }
}
