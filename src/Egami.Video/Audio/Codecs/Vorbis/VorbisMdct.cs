// Follows the Vorbis I specification (Xiph.Org), section 4.3.7 "inverse MDCT" (and 1.3.2): the inverse MDCT of an n/2 value
// spectrum, y[k] = sum over j of X[j] cos(pi / (n/2) (k + 1/2 + n/4) (j + 1/2)). It is computed as a DCT-IV of size n/2 through a
// complex FFT of size n/4 with pre- and post-twiddles; the decoder unfolds the DCT-IV into the n outputs by its symmetries.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>The DCT-IV at the heart of the inverse MDCT for one block size.</summary>
internal sealed class VorbisMdct
{
    private readonly int _m;
    private readonly int _q;
    private readonly int[] _bitReverse;
    private readonly float[] _preCos;
    private readonly float[] _preSin;
    private readonly float[] _postCos;
    private readonly float[] _postSin;

    // Twiddles for the butterfly stage of half-size h at [h, 2h): e^(-2 pi i k / 2h).
    private readonly float[] _twCos;
    private readonly float[] _twSin;
    private readonly float[] _re;
    private readonly float[] _im;

    public VorbisMdct(int n)
    {
        _m = n / 2;
        _q = n / 4;
        int bits = BitOperations.Log2((uint)_q);
        _bitReverse = new int[_q];
        for (int i = 0; i < _q; i++)
        {
            _bitReverse[i] = (int)(ReverseBits((uint)i) >> (32 - bits));
        }

        _preCos = new float[_q];
        _preSin = new float[_q];
        _postCos = new float[_q];
        _postSin = new float[_q];
        for (int j = 0; j < _q; j++)
        {
            double a = -Math.PI * (j + 0.25) / _m;
            _preCos[j] = (float)Math.Cos(a);
            _preSin[j] = (float)Math.Sin(a);
            double b = -Math.PI * j / _m;
            _postCos[j] = (float)Math.Cos(b);
            _postSin[j] = (float)Math.Sin(b);
        }

        _twCos = new float[Math.Max(_q, 2)];
        _twSin = new float[Math.Max(_q, 2)];
        for (int h = 1; h < _q; h <<= 1)
        {
            for (int k = 0; k < h; k++)
            {
                double a = -Math.PI * k / h;
                _twCos[h + k] = (float)Math.Cos(a);
                _twSin[h + k] = (float)Math.Sin(a);
            }
        }

        _re = new float[_q];
        _im = new float[_q];
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
        v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
        v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
        v = ((v >> 8) & 0x00FF00FF) | ((v & 0x00FF00FF) << 8);
        return (v >> 16) | (v << 16);
    }

    /// <summary>u[k] = sum over j of x[j] cos(pi / m (k + 1/2)(j + 1/2)), for the m = n/2 values of <paramref name="x"/>.</summary>
    public void Dct4(ReadOnlySpan<float> x, Span<float> u)
    {
        int m = _m;
        int q = _q;
        Span<float> re = _re;
        Span<float> im = _im;
        ReadOnlySpan<int> rev = _bitReverse;
        ReadOnlySpan<float> pc = _preCos;
        ReadOnlySpan<float> ps = _preSin;
        x = x[..m];
        for (int j = 0; j < q; j++)
        {
            float a = x[2 * j];
            float b = x[m - 1 - (2 * j)];
            int t = rev[j];
            re[t] = (a * pc[j]) - (b * ps[j]);
            im[t] = (a * ps[j]) + (b * pc[j]);
        }

        Fft(re, im);

        ReadOnlySpan<float> qc = _postCos;
        ReadOnlySpan<float> qs = _postSin;
        u = u[..m];
        for (int j = 0; j < q; j++)
        {
            float a = re[j];
            float b = im[j];
            u[2 * j] = (a * qc[j]) - (b * qs[j]);
            u[m - 1 - (2 * j)] = -((a * qs[j]) + (b * qc[j]));
        }
    }

    private void Fft(Span<float> re, Span<float> im)
    {
        int q = _q;

        // Radix-4 first: the stages of half-size 1 and 2 together (twiddles 1 and -i).
        for (int g = 0; g + 4 <= q; g += 4)
        {
            float r0 = re[g] + re[g + 1];
            float i0 = im[g] + im[g + 1];
            float r1 = re[g] - re[g + 1];
            float i1 = im[g] - im[g + 1];
            float r2 = re[g + 2] + re[g + 3];
            float i2 = im[g + 2] + im[g + 3];
            float r3 = re[g + 2] - re[g + 3];
            float i3 = im[g + 2] - im[g + 3];
            re[g] = r0 + r2;
            im[g] = i0 + i2;
            re[g + 2] = r0 - r2;
            im[g + 2] = i0 - i2;

            // (r3 + i i3) * -i = i3 - i r3.
            re[g + 1] = r1 + i3;
            im[g + 1] = i1 - r3;
            re[g + 3] = r1 - i3;
            im[g + 3] = i1 + r3;
        }

        ReadOnlySpan<float> twc = _twCos;
        ReadOnlySpan<float> tws = _twSin;
        int width = Vector<float>.Count;
        for (int h = 4; h < q; h <<= 1)
        {
            int s = 2 * h;
            if (Vector.IsHardwareAccelerated && h >= width)
            {
                ref float rr = ref MemoryMarshal.GetReference(re);
                ref float ri = ref MemoryMarshal.GetReference(im);
                ref float rc = ref MemoryMarshal.GetReference(twc);
                ref float rs = ref MemoryMarshal.GetReference(tws);
                for (int g = 0; g < q; g += s)
                {
                    for (int k = 0; k < h; k += width)
                    {
                        Vector<float> wc = Vector.LoadUnsafe(ref rc, (nuint)(h + k));
                        Vector<float> ws = Vector.LoadUnsafe(ref rs, (nuint)(h + k));
                        nuint a = (nuint)(g + k);
                        nuint b = (nuint)(g + k + h);
                        Vector<float> ar = Vector.LoadUnsafe(ref rr, a);
                        Vector<float> ai = Vector.LoadUnsafe(ref ri, a);
                        Vector<float> br = Vector.LoadUnsafe(ref rr, b);
                        Vector<float> bi = Vector.LoadUnsafe(ref ri, b);
                        Vector<float> tr = (br * wc) - (bi * ws);
                        Vector<float> ti = (br * ws) + (bi * wc);
                        (ar + tr).StoreUnsafe(ref rr, a);
                        (ai + ti).StoreUnsafe(ref ri, a);
                        (ar - tr).StoreUnsafe(ref rr, b);
                        (ai - ti).StoreUnsafe(ref ri, b);
                    }
                }

                continue;
            }

            for (int g = 0; g < q; g += s)
            {
                for (int k = 0; k < h; k++)
                {
                    Butterfly(re, im, g + k, g + k + h, twc[h + k], tws[h + k]);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(Span<float> re, Span<float> im, int a, int b, float wc, float ws)
    {
        float tr = (re[b] * wc) - (im[b] * ws);
        float ti = (re[b] * ws) + (im[b] * wc);
        float ar = re[a];
        float ai = im[a];
        re[a] = ar + tr;
        im[a] = ai + ti;
        re[b] = ar - tr;
        im[b] = ai - ti;
    }
}
