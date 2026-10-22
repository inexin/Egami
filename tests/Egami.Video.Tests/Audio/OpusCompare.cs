// Translated from libopus 1.5.2 src/opus_compare.c (the RFC 6716 conformance measure), at 48 kHz only.
namespace Egami.Video.Tests.Audio;

/// <summary>Opus's quality measure between a reference decode and a decoder's output: a vector passes when the quality is 0 or more.</summary>
internal static class OpusCompare
{
    private const int Bands = 21;
    private const int Freqs = 240;
    private const int WindowSize = 480;
    private const int WindowStep = 120;
    private static readonly int[] s_bands = [0, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 68, 80, 96, 120, 156, 200];

    /// <summary>
    /// Returns the quality in percent, below 0 for a failing vector. <paramref name="reference"/> is the stereo .dec file
    /// (downmixed for a mono comparison, as opus_compare does); <paramref name="test"/> has <paramref name="channels"/> channels.
    /// </summary>
    public static double Quality(ReadOnlySpan<short> reference, ReadOnlySpan<short> test, int channels)
    {
        int xlength = reference.Length / 2;
        float[] x = new float[xlength * channels];
        for (int i = 0; i < xlength; i++)
        {
            if (channels == 1)
            {
                x[i] = 0.5f * (reference[2 * i] + reference[(2 * i) + 1]);
            }
            else
            {
                x[2 * i] = reference[2 * i];
                x[(2 * i) + 1] = reference[(2 * i) + 1];
            }
        }

        int ylength = test.Length / channels;
        if (xlength != ylength)
        {
            throw new InvalidOperationException($"Sample counts do not match ({xlength} != {ylength}).");
        }

        float[] y = new float[ylength * channels];
        for (int i = 0; i < y.Length; i++)
        {
            y[i] = test[i];
        }

        int nframes = (xlength - WindowSize + WindowStep) / WindowStep;
        float[] xb = new float[nframes * Bands * channels];
        float[] xs = new float[nframes * Freqs * channels];
        float[] ys = new float[nframes * Freqs * channels];
        BandEnergy(xb, xs, x, channels, nframes);
        BandEnergy(null, ys, y, channels, nframes);
        for (int xi = 0; xi < nframes; xi++)
        {
            for (int bi = 1; bi < Bands; bi++)
            {
                for (int ci = 0; ci < channels; ci++)
                {
                    xb[(((xi * Bands) + bi) * channels) + ci] += 0.1f * xb[(((xi * Bands) + bi - 1) * channels) + ci];
                }
            }

            for (int bi = Bands - 1; bi-- > 0;)
            {
                for (int ci = 0; ci < channels; ci++)
                {
                    xb[(((xi * Bands) + bi) * channels) + ci] += 0.03f * xb[(((xi * Bands) + bi + 1) * channels) + ci];
                }
            }

            if (xi > 0)
            {
                for (int bi = 0; bi < Bands; bi++)
                {
                    for (int ci = 0; ci < channels; ci++)
                    {
                        xb[(((xi * Bands) + bi) * channels) + ci] += 0.5f * xb[((((xi - 1) * Bands) + bi) * channels) + ci];
                    }
                }
            }

            if (channels == 2)
            {
                for (int bi = 0; bi < Bands; bi++)
                {
                    float l = xb[(((xi * Bands) + bi) * channels) + 0];
                    float r = xb[(((xi * Bands) + bi) * channels) + 1];
                    xb[(((xi * Bands) + bi) * channels) + 0] += 0.01f * r;
                    xb[(((xi * Bands) + bi) * channels) + 1] += 0.01f * l;
                }
            }

            for (int bi = 0; bi < Bands; bi++)
            {
                for (int xj = s_bands[bi]; xj < s_bands[bi + 1]; xj++)
                {
                    for (int ci = 0; ci < channels; ci++)
                    {
                        xs[(((xi * Freqs) + xj) * channels) + ci] += 0.1f * xb[(((xi * Bands) + bi) * channels) + ci];
                        ys[(((xi * Freqs) + xj) * channels) + ci] += 0.1f * xb[(((xi * Bands) + bi) * channels) + ci];
                    }
                }
            }
        }

        for (int bi = 0; bi < Bands; bi++)
        {
            for (int xj = s_bands[bi]; xj < s_bands[bi + 1]; xj++)
            {
                for (int ci = 0; ci < channels; ci++)
                {
                    float xtmp = xs[(xj * channels) + ci];
                    float ytmp = ys[(xj * channels) + ci];
                    for (int xi = 1; xi < nframes; xi++)
                    {
                        float xtmp2 = xs[(((xi * Freqs) + xj) * channels) + ci];
                        float ytmp2 = ys[(((xi * Freqs) + xj) * channels) + ci];
                        xs[(((xi * Freqs) + xj) * channels) + ci] += xtmp;
                        ys[(((xi * Freqs) + xj) * channels) + ci] += ytmp;
                        xtmp = xtmp2;
                        ytmp = ytmp2;
                    }
                }
            }
        }

        double err = 0;
        for (int xi = 0; xi < nframes; xi++)
        {
            double ef = 0;
            for (int bi = 0; bi < Bands; bi++)
            {
                double eb = 0;
                for (int xj = s_bands[bi]; xj < s_bands[bi + 1]; xj++)
                {
                    for (int ci = 0; ci < channels; ci++)
                    {
                        float re = ys[(((xi * Freqs) + xj) * channels) + ci] / xs[(((xi * Freqs) + xj) * channels) + ci];
                        float im = (float)(re - Math.Log(re) - 1);
                        if (xj is >= 79 and <= 81)
                        {
                            im *= 0.1f;
                        }

                        if (xj == 80)
                        {
                            im *= 0.1f;
                        }

                        eb += im;
                    }
                }

                eb /= (s_bands[bi + 1] - s_bands[bi]) * channels;
                ef += eb * eb;
            }

            ef /= Bands;
            ef *= ef;
            err += ef * ef;
        }

        err = Math.Pow(err / nframes, 1.0 / 16);
        return 100 * (1 - (0.5 * Math.Log(1 + err) / Math.Log(1.13)));
    }

    /// <summary>The DFT basis opus_compare walks: cos and -sin of 2 pi j k / 480 for each bin j and sample k.</summary>
    private static readonly (float[] Cos, float[] Sin) s_basis = Basis();

    private static (float[] Cos, float[] Sin) Basis()
    {
        float[] c = new float[WindowSize];
        float[] s = new float[WindowSize];
        for (int xj = 0; xj < WindowSize; xj++)
        {
            c[xj] = (float)Math.Cos(2 * 3.14159265f / WindowSize * xj);
            s[xj] = (float)Math.Sin(2 * 3.14159265f / WindowSize * xj);
        }

        // Row j holds the table entries opus_compare's index ti = j * k mod 480 picks, so each bin is two dot products.
        float[] cos = new float[Freqs * WindowSize];
        float[] sin = new float[Freqs * WindowSize];
        for (int j = 0; j < Freqs; j++)
        {
            int ti = 0;
            for (int k = 0; k < WindowSize; k++)
            {
                cos[(j * WindowSize) + k] = c[ti];
                sin[(j * WindowSize) + k] = s[ti];
                ti += j;
                if (ti >= WindowSize)
                {
                    ti -= WindowSize;
                }
            }
        }

        return (cos, sin);
    }

    private static void BandEnergy(float[]? output, float[] ps, float[] input, int channels, int nframes)
    {
        float[] window = new float[WindowSize];
        for (int xj = 0; xj < WindowSize; xj++)
        {
            window[xj] = 0.5f - (0.5f * (float)Math.Cos(2 * 3.14159265f / (WindowSize - 1) * xj));
        }

        (float[] cos, float[] sin) = s_basis;
        int psSize = WindowSize / 2;
        Parallel.For(0, nframes, xi =>
        {
            float[] x = new float[channels * WindowSize];
            for (int ci = 0; ci < channels; ci++)
            {
                for (int xk = 0; xk < WindowSize; xk++)
                {
                    x[(ci * WindowSize) + xk] = window[xk] * input[((((xi * WindowStep) + xk) * channels) + ci)];
                }
            }

            int j = 0;
            for (int bi = 0; bi < Bands; bi++)
            {
                float p0 = 0, p1 = 0;
                for (; j < s_bands[bi + 1]; j++)
                {
                    for (int ci = 0; ci < channels; ci++)
                    {
                        ReadOnlySpan<float> xs = x.AsSpan(ci * WindowSize, WindowSize);
                        float re = Dot(cos.AsSpan(j * WindowSize, WindowSize), xs);
                        float im = -Dot(sin.AsSpan(j * WindowSize, WindowSize), xs);
                        float v = (re * re) + (im * im) + 100000;
                        ps[(((xi * psSize) + j) * channels) + ci] = v;
                        if (ci == 0)
                        {
                            p0 += v;
                        }
                        else
                        {
                            p1 += v;
                        }
                    }
                }

                if (output is not null)
                {
                    output[((xi * Bands) + bi) * channels] = p0 / (s_bands[bi + 1] - s_bands[bi]);
                    if (channels == 2)
                    {
                        output[(((xi * Bands) + bi) * channels) + 1] = p1 / (s_bands[bi + 1] - s_bands[bi]);
                    }
                }
            }
        });
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        System.Numerics.Vector<float> sum = System.Numerics.Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - System.Numerics.Vector<float>.Count; i += System.Numerics.Vector<float>.Count)
        {
            sum += new System.Numerics.Vector<float>(a[i..]) * new System.Numerics.Vector<float>(b[i..]);
        }

        float total = System.Numerics.Vector.Sum(sum);
        for (; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }
}
