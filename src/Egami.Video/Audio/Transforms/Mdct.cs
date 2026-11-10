using System.Numerics;

namespace Egami.Video.Audio.Transforms;

/// <summary>
/// The modified discrete cosine transform of one block length N (a power of two, at least 16): N samples to N/2 coefficients and
/// back, as Princen and Bradley's time-domain aliasing cancellation defines it (IEEE Trans. ASSP 34(5), 1986; ICASSP 1987):
/// <code>
/// X[k] = 2 sum over n &lt; N of x[n] cos(2 pi / N (n + n0) (k + 1/2)),    n0 = (N/2 + 1) / 2
/// y[n] = 2/N sum over k &lt; N/2 of X[k] cos(2 pi / N (n + n0) (k + 1/2))
/// </code>
/// Windowing each block with a window that meets the Princen-Bradley condition (w[n]^2 + w[n + N/2]^2 = 1, such as
/// <see cref="MdctWindows.Sine"/> and <see cref="MdctWindows.KaiserBesselDerived"/>) before <see cref="Forward"/> and after
/// <see cref="Inverse"/>, and adding each block's second half to the next one's first, gives the input back: the factors 2 and
/// 2/N make the pair exact. Both directions fold
/// the block into a DCT-IV of N/2 points, which runs as an N/4-point complex FFT between two twiddles.
/// </summary>
internal sealed class Mdct
{
    private readonly int _half;
    private readonly int _quarter;
    private readonly ComplexFft _fft;
    private readonly float[] _preCos;
    private readonly float[] _preSin;
    private readonly float[] _postCos;
    private readonly float[] _postSin;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _folded;

    /// <summary>Initializes the transform for blocks of <paramref name="length"/> samples.</summary>
    /// <param name="length">N, the block length: a power of two of at least 16.</param>
    public Mdct(int length)
    {
        if (length < 16 || !BitOperations.IsPow2(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The MDCT length must be a power of two of at least 16.");
        }

        Length = length;
        _half = length / 2;
        _quarter = length / 4;
        _fft = new ComplexFft(BitOperations.Log2((uint)_quarter));
        _preCos = new float[_quarter];
        _preSin = new float[_quarter];
        _postCos = new float[_quarter];
        _postSin = new float[_quarter];
        for (int i = 0; i < _quarter; i++)
        {
            // DCT-IV of M = N/2 points through an M/2-point FFT: v[i] = (u[2i] + j u[M-1-2i]) e^(-j pi (i + 1/4) / M), then
            // V[k] e^(-j pi k / M) gives Y[2k] (real part) and -Y[M-1-2k] (imaginary part).
            double pre = -Math.PI * (i + 0.25) / _half;
            _preCos[i] = (float)Math.Cos(pre);
            _preSin[i] = (float)Math.Sin(pre);
            double post = -Math.PI * i / _half;
            _postCos[i] = (float)Math.Cos(post);
            _postSin[i] = (float)Math.Sin(post);
        }

        _re = new float[_quarter];
        _im = new float[_quarter];
        _folded = new float[_half];
    }

    /// <summary>Gets N, the block length in samples.</summary>
    public int Length { get; }

    /// <summary>Transforms a block of N (windowed) samples into its N/2 coefficients.</summary>
    /// <param name="samples">The N samples.</param>
    /// <param name="coefficients">The N/2 coefficients.</param>
    public void Forward(ReadOnlySpan<float> samples, Span<float> coefficients)
    {
        int m = _half, h = _quarter;
        ReadOnlySpan<float> x = samples[..Length];
        Span<float> u = _folded;

        // Fold the block's four quarters a, b, c, d into M samples: (-c reversed - d, a - b reversed).
        int threeQuarters = m + h;
        for (int i = 0; i < h; i++)
        {
            u[i] = -x[threeQuarters + i] - x[threeQuarters - 1 - i];
            u[h + i] = x[i] - x[m - 1 - i];
        }

        DctIV(u, coefficients[..m], 2f);
    }

    /// <summary>Transforms N/2 coefficients into the N samples of the block, aliased, before windowing and overlap-add.</summary>
    /// <param name="coefficients">The N/2 coefficients.</param>
    /// <param name="samples">The N samples.</param>
    public void Inverse(ReadOnlySpan<float> coefficients, Span<float> samples)
    {
        int m = _half, h = _quarter;
        Span<float> v = _folded;
        DctIV(coefficients[..m], v, 2f / Length);

        // Unfold: the first quarter is the second half of v, then its mirror with the sign flipped, then the first half's.
        Span<float> y = samples[..Length];
        for (int i = 0; i < h; i++)
        {
            float first = v[i];
            float second = v[h + i];
            y[i] = second;
            y[m - 1 - i] = -second;
            y[m + h - 1 - i] = -first;
            y[m + h + i] = -first;
        }
    }

    /// <summary>
    /// The inverse transform with its windowing and overlap-add: the block's first half, windowed by <paramref name="rising"/>, is
    /// added to <paramref name="overlap"/> to give the N/2 output samples, and its second half, windowed by
    /// <paramref name="falling"/>, becomes the next block's overlap.
    /// </summary>
    /// <param name="coefficients">The N/2 coefficients.</param>
    /// <param name="rising">The window's first half, N/2 values from near 0 to near 1.</param>
    /// <param name="falling">The window's second half read backwards: N/2 values from near 0 to near 1, so a symmetric window passes the same table twice.</param>
    /// <param name="overlap">The previous block's windowed second half, replaced by this block's.</param>
    /// <param name="output">The N/2 finished samples.</param>
    /// <param name="block">Scratch for the N aliased samples.</param>
    public void InverseOverlap(ReadOnlySpan<float> coefficients, ReadOnlySpan<float> rising, ReadOnlySpan<float> falling, Span<float> overlap, Span<float> output, Span<float> block)
    {
        int m = _half;
        Inverse(coefficients, block);
        ReadOnlySpan<float> up = rising[..m];
        ReadOnlySpan<float> down = falling[..m];
        Span<float> o = overlap[..m];
        Span<float> outSpan = output[..m];
        ReadOnlySpan<float> first = block[..m];
        ReadOnlySpan<float> second = block.Slice(m, m);
        for (int i = 0; i < m; i++)
        {
            outSpan[i] = o[i] + (first[i] * up[i]);
            o[i] = second[i] * down[m - 1 - i];
        }
    }

    /// <summary>The DCT-IV of M = N/2 points, y[k] = scale sum over n of x[n] cos(pi / M (n + 1/2) (k + 1/2)).</summary>
    private void DctIV(ReadOnlySpan<float> x, Span<float> y, float scale)
    {
        int m = _half, q = _quarter;
        float[] re = _re, im = _im;
        int[] reverse = _fft.Reverse;

        // The forward FFT runs as the conjugate of the inverse one: the pre-twiddled input goes in conjugated...
        for (int i = 0; i < q; i++)
        {
            float a = x[2 * i];
            float b = x[m - 1 - (2 * i)];
            float c = _preCos[i];
            float s = _preSin[i];
            int at = reverse[i];
            re[at] = (a * c) - (b * s);
            im[at] = -((a * s) + (b * c));
        }

        _fft.Inverse(re, im);

        // ... and comes out conjugated.
        for (int k = 0; k < q; k++)
        {
            float vr = re[k];
            float vi = -im[k];
            float c = _postCos[k];
            float s = _postSin[k];
            y[2 * k] = scale * ((vr * c) - (vi * s));
            y[m - 1 - (2 * k)] = -scale * ((vr * s) + (vi * c));
        }
    }
}
