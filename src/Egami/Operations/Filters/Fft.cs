using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Operations.Filters;

/// <summary>
/// A complex float FFT of a power-of-two size, iterative radix-2 (Cooley and Tukey, 1965) on split real and imaginary arrays.
/// The 2D transform runs the 1D transform along every row, then along every column with each butterfly applied to whole rows,
/// so both directions are vector loops. Inverse transforms are not scaled; callers fold 1 / N² into what they multiply by.
/// </summary>
internal sealed class Fft
{
    private static readonly ConcurrentDictionary<int, Fft> Cache = new();

    private readonly int[] _reverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    private Fft(int size)
    {
        Size = size;
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        _reverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = r;
        }

        // The twiddles of every stage, one after another: the stage with half-length h starts at h − 1 and holds
        // e^(−2πik/2h) for k = 0 .. h − 1.
        _cos = new float[size];
        _sin = new float[size];
        for (int half = 1; half < size; half *= 2)
        {
            for (int k = 0; k < half; k++)
            {
                double angle = -Math.PI * k / half;
                _cos[half - 1 + k] = (float)Math.Cos(angle);
                _sin[half - 1 + k] = (float)Math.Sin(angle);
            }
        }
    }

    /// <summary>Gets the transform length.</summary>
    internal int Size { get; }

    /// <summary>Returns the transform for <paramref name="size"/>, a power of two of at least 2.</summary>
    internal static Fft For(int size) => Cache.GetOrAdd(size, n => new Fft(n));

    /// <summary>Transforms one sequence in place; <paramref name="inverse"/> uses e^(+2πi) twiddles.</summary>
    internal void Transform(Span<float> re, Span<float> im, bool inverse)
    {
        int n = Size;
        for (int i = 0; i < n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        float sign = inverse ? -1 : 1;
        ref float r = ref MemoryMarshal.GetReference(re);
        ref float m = ref MemoryMarshal.GetReference(im);
        for (int half = 1; half < n; half *= 2)
        {
            ReadOnlySpan<float> cos = _cos.AsSpan(half - 1, half), sin = _sin.AsSpan(half - 1, half);
            for (int start = 0; start < n; start += 2 * half)
            {
                int k = 0;
                if (Vector256.IsHardwareAccelerated && half >= Vector256<float>.Count)
                {
                    ref float c = ref MemoryMarshal.GetReference(cos);
                    ref float s = ref MemoryMarshal.GetReference(sin);
                    Vector256<float> vsign = Vector256.Create(sign);
                    for (; k < half; k += Vector256<float>.Count)
                    {
                        nuint a = (nuint)(start + k), b = (nuint)(start + k + half);
                        Vector256<float> wr = Vector256.LoadUnsafe(ref c, (nuint)k), wi = Vector256.LoadUnsafe(ref s, (nuint)k) * vsign;
                        Vector256<float> br = Vector256.LoadUnsafe(ref r, b), bi = Vector256.LoadUnsafe(ref m, b);
                        Vector256<float> tr = (br * wr) - (bi * wi), ti = (br * wi) + (bi * wr);
                        Vector256<float> ar = Vector256.LoadUnsafe(ref r, a), ai = Vector256.LoadUnsafe(ref m, a);
                        (ar + tr).StoreUnsafe(ref r, a);
                        (ai + ti).StoreUnsafe(ref m, a);
                        (ar - tr).StoreUnsafe(ref r, b);
                        (ai - ti).StoreUnsafe(ref m, b);
                    }
                }

                for (; k < half; k++)
                {
                    int a = start + k, b = a + half;
                    float wr = cos[k], wi = sin[k] * sign;
                    float tr = (re[b] * wr) - (im[b] * wi), ti = (re[b] * wi) + (im[b] * wr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    /// <summary>Transforms an N × N array, row-major, in place.</summary>
    internal void Transform2D(Span<float> re, Span<float> im, bool inverse)
    {
        int n = Size;
        for (int y = 0; y < n; y++)
        {
            Transform(re.Slice(y * n, n), im.Slice(y * n, n), inverse);
        }

        // Down the columns: the same butterflies, each on a pair of whole rows.
        for (int i = 0; i < n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                SwapRows(re, i, j, n);
                SwapRows(im, i, j, n);
            }
        }

        float sign = inverse ? -1 : 1;
        for (int half = 1; half < n; half *= 2)
        {
            for (int start = 0; start < n; start += 2 * half)
            {
                for (int k = 0; k < half; k++)
                {
                    int a = start + k, b = a + half;
                    float wr = _cos[half - 1 + k], wi = _sin[half - 1 + k] * sign;
                    RowButterfly(re.Slice(a * n, n), im.Slice(a * n, n), re.Slice(b * n, n), im.Slice(b * n, n), wr, wi);
                }
            }
        }
    }

    private static void SwapRows(Span<float> data, int i, int j, int n)
    {
        Span<float> a = data.Slice(i * n, n), b = data.Slice(j * n, n);
        for (int x = 0; x < n; x++)
        {
            (a[x], b[x]) = (b[x], a[x]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RowButterfly(Span<float> ar, Span<float> ai, Span<float> br, Span<float> bi, float wr, float wi)
    {
        int n = ar.Length, x = 0;
        if (ai.Length < n || br.Length < n || bi.Length < n)
        {
            throw new ArgumentException("Rows differ in length.");
        }

        ref float rar = ref MemoryMarshal.GetReference(ar);
        ref float rai = ref MemoryMarshal.GetReference(ai);
        ref float rbr = ref MemoryMarshal.GetReference(br);
        ref float rbi = ref MemoryMarshal.GetReference(bi);
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> vwr = Vector256.Create(wr), vwi = Vector256.Create(wi);
            for (; x <= n - Vector256<float>.Count; x += Vector256<float>.Count)
            {
                nuint at = (nuint)x;
                Vector256<float> pr = Vector256.LoadUnsafe(ref rbr, at), pi = Vector256.LoadUnsafe(ref rbi, at);
                Vector256<float> tr = (pr * vwr) - (pi * vwi), ti = (pr * vwi) + (pi * vwr);
                Vector256<float> qr = Vector256.LoadUnsafe(ref rar, at), qi = Vector256.LoadUnsafe(ref rai, at);
                (qr + tr).StoreUnsafe(ref rar, at);
                (qi + ti).StoreUnsafe(ref rai, at);
                (qr - tr).StoreUnsafe(ref rbr, at);
                (qi - ti).StoreUnsafe(ref rbi, at);
            }
        }

        for (; x < n; x++)
        {
            float tr = (br[x] * wr) - (bi[x] * wi), ti = (br[x] * wi) + (bi[x] * wr);
            br[x] = ar[x] - tr;
            bi[x] = ai[x] - ti;
            ar[x] += tr;
            ai[x] += ti;
        }
    }
}
