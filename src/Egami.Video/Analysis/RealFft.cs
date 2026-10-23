namespace Egami.Video.Analysis;

/// <summary>
/// A radix-2 FFT for real input of a power-of-two length: the samples are packed as a complex sequence of half the length,
/// transformed in place, and split into the real spectrum's bins. Built once per length; the instance holds its tables and
/// scratch, so it is not thread-safe.
/// </summary>
internal sealed class RealFft
{
    private readonly int _size;
    private readonly int _half;
    private readonly int[] _reverse;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly double[] _re;
    private readonly double[] _im;

    /// <summary>Creates a transform for <paramref name="size"/> real samples, a power of two from 4.</summary>
    public RealFft(int size)
    {
        if (size < 4 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The FFT size must be a power of two from 4.");
        }

        _size = size;
        _half = size / 2;
        _re = new double[_half];
        _im = new double[_half];
        _reverse = new int[_half];
        int bits = System.Numerics.BitOperations.Log2((uint)_half);
        for (int i = 0; i < _half; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = r;
        }

        // Twiddles e^(-2 pi i k / size) for k below size / 2 serve both the half-length transform (every other one) and the split.
        _cos = new double[_half];
        _sin = new double[_half];
        for (int k = 0; k < _half; k++)
        {
            _cos[k] = Math.Cos(2 * Math.PI * k / size);
            _sin[k] = -Math.Sin(2 * Math.PI * k / size);
        }
    }

    /// <summary>Gets the number of real samples the transform takes.</summary>
    public int Size => _size;

    /// <summary>Gets the number of bins it returns: from 0 to the Nyquist frequency, size / 2 + 1.</summary>
    public int Bins => _half + 1;

    /// <summary>Transforms <paramref name="input"/> and writes each bin's magnitude to <paramref name="magnitudes"/>.</summary>
    public void Magnitudes(ReadOnlySpan<float> input, Span<float> magnitudes)
    {
        Transform(input);
        for (int k = 0; k <= _half; k++)
        {
            Bin(k, out double re, out double im);
            magnitudes[k] = (float)Math.Sqrt((re * re) + (im * im));
        }
    }

    /// <summary>Transforms <paramref name="input"/> and writes the complex bins, for tests.</summary>
    public void Spectrum(ReadOnlySpan<float> input, Span<double> real, Span<double> imaginary)
    {
        Transform(input);
        for (int k = 0; k <= _half; k++)
        {
            Bin(k, out real[k], out imaginary[k]);
        }
    }

    private void Transform(ReadOnlySpan<float> input)
    {
        for (int i = 0; i < _half; i++)
        {
            int r = _reverse[i];
            _re[r] = input[2 * i];
            _im[r] = input[(2 * i) + 1];
        }

        for (int length = 2; length <= _half; length <<= 1)
        {
            int span = length / 2;
            int stride = _size / length;
            for (int start = 0; start < _half; start += length)
            {
                for (int j = 0; j < span; j++)
                {
                    double wr = _cos[j * stride], wi = _sin[j * stride];
                    int a = start + j, b = a + span;
                    double tr = (_re[b] * wr) - (_im[b] * wi);
                    double ti = (_re[b] * wi) + (_im[b] * wr);
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
            }
        }
    }

    /// <summary>Splits bin <paramref name="k"/> of the real spectrum out of the half-length complex result.</summary>
    private void Bin(int k, out double re, out double im)
    {
        int a = k % _half;
        int b = (_half - k) % _half;

        // Even and odd samples' spectra: E = (Z[k] + conj Z[N/2 - k]) / 2, O = (Z[k] - conj Z[N/2 - k]) / 2i.
        double er = (_re[a] + _re[b]) / 2, ei = (_im[a] - _im[b]) / 2;
        double or = (_im[a] + _im[b]) / 2, oi = -(_re[a] - _re[b]) / 2;
        double wr = k == _half ? -1 : _cos[k], wi = k == _half ? 0 : _sin[k];
        re = er + ((or * wr) - (oi * wi));
        im = ei + ((or * wi) + (oi * wr));
    }
}
