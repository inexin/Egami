using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Audio.Transforms;

/// <summary>
/// An in-place radix-2 complex inverse FFT of at least 4 points, z[n] = sum Z[k] e^(+2 pi j k n / N), unscaled. The input goes
/// in bit-reversed order (<see cref="Reverse"/>), which callers write directly from their pre-twiddles; the stages past the
/// second run four butterflies at a time on SIMD hardware. A forward FFT is the conjugate of the inverse of the conjugate.
/// </summary>
internal sealed class ComplexFft
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
