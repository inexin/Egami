// Translated from libopus 1.5.2 celt/vq.c and celt/cwrs.c (decoder side, float build, SMALL_FOOTPRINT off).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>CELT's pyramid vector quantizer on the decoder side: codeword decoding (CWRS), spreading rotation and normalization.</summary>
internal static partial class CeltVq
{
    private static readonly int[] s_spreadFactor = [15, 10, 5];

    /// <summary>CELT_PVQ_U(n, k): the number of codewords helper U(n, k) of cwrs.c.</summary>
    public static uint PvqU(int n, int k)
    {
        return CeltTables.PvqUData[CeltTables.PvqURow[Math.Min(n, k)] + Math.Max(n, k)];
    }

    /// <summary>CELT_PVQ_V(n, k): the number of PVQ codewords for <paramref name="n"/> dimensions and <paramref name="k"/> pulses.</summary>
    public static uint PvqV(int n, int k) => PvqU(n, k) + PvqU(n, k + 1);

    /// <summary>decode_pulses: reads a codeword and writes its pulse vector to <paramref name="y"/>; returns its squared norm.</summary>
    public static float DecodePulses(Span<int> y, int n, int k, OpusRangeDecoder dec)
    {
        return Cwrsi(n, k, dec.DecodeUint(PvqV(n, k)), y);
    }

    /// <summary>cwrsi: the pulse vector of codeword <paramref name="i"/>.</summary>
    public static float Cwrsi(int n, int k, uint i, Span<int> y)
    {
        ReadOnlySpan<uint> data = CeltTables.PvqUData;
        ReadOnlySpan<short> rows = CeltTables.PvqURow;
        float yy = 0;
        int yi = 0;
        uint p;
        int s;
        int k0;
        int val;
        while (n > 2)
        {
            uint q;
            if (k >= n)
            {
                // Lots of pulses case.
                int row = rows[n];
                // Are the pulses in this dimension negative?
                p = data[row + k + 1];
                s = -(i >= p ? 1 : 0);
                i -= p & (uint)s;
                // Count how many pulses were placed in this dimension.
                k0 = k;
                q = data[row + n];
                if (q > i)
                {
                    k = n;
                    do
                    {
                        p = data[rows[--k] + n];
                    }
                    while (p > i);
                }
                else
                {
                    for (p = data[row + k]; p > i; p = data[row + k])
                    {
                        k--;
                    }
                }

                i -= p;
                val = (k0 - k + s) ^ s;
                y[yi++] = val;
                yy += val * val;
            }
            else
            {
                // Lots of dimensions case: are there any pulses in this dimension at all?
                p = data[rows[k] + n];
                q = data[rows[k + 1] + n];
                if (p <= i && i < q)
                {
                    i -= p;
                    y[yi++] = 0;
                }
                else
                {
                    // Are the pulses in this dimension negative?
                    s = -(i >= q ? 1 : 0);
                    i -= q & (uint)s;
                    // Count how many pulses were placed in this dimension.
                    k0 = k;
                    do
                    {
                        p = data[rows[--k] + n];
                    }
                    while (p > i);
                    i -= p;
                    val = (k0 - k + s) ^ s;
                    y[yi++] = val;
                    yy += val * val;
                }
            }

            n--;
        }

        // n == 2.
        p = (uint)((2 * k) + 1);
        s = -(i >= p ? 1 : 0);
        i -= p & (uint)s;
        k0 = k;
        k = (int)((i + 1) >> 1);
        if (k != 0)
        {
            i -= (uint)((2 * k) - 1);
        }

        val = (k0 - k + s) ^ s;
        y[yi++] = val;
        yy += val * val;
        // n == 1.
        s = -(int)i;
        val = (k + s) ^ s;
        y[yi] = val;
        yy += val * val;
        return yy;
    }

    /// <summary>alg_unquant: decodes <paramref name="k"/> pulses over <paramref name="n"/> values; returns the collapse mask.</summary>
    public static uint AlgUnquant(Span<float> x, int n, int k, int spread, int b, OpusRangeDecoder dec, float gain)
    {
        Span<int> iy = n <= 256 ? stackalloc int[n] : new int[n];
        float ryy = DecodePulses(iy, n, k, dec);
        NormaliseResidual(iy, x, n, ryy, gain);
        ExpRotation(x, n, -1, b, k, spread);
        return ExtractCollapseMask(iy, n, b);
    }

    /// <summary>renormalise_vector: scales <paramref name="x"/> to norm <paramref name="gain"/>.</summary>
    public static void RenormaliseVector(Span<float> x, int n, float gain)
    {
        float e = 1e-15f + CeltMath.InnerProduct(x, x, n);
        float g = CeltMath.RSqrt(e) * gain;
        for (int i = 0; i < n; i++)
        {
            x[i] = g * x[i];
        }
    }

    /// <summary>exp_rotation: the spreading rotation, inverse when <paramref name="dir"/> is negative.</summary>
    public static void ExpRotation(Span<float> x, int len, int dir, int stride, int k, int spread)
    {
        int[] spreadFactor = s_spreadFactor;
        if (2 * k >= len || spread == CeltTables.SpreadNone)
        {
            return;
        }

        int factor = spreadFactor[spread - 1];
        float gain = (1f * len) / (len + (factor * k));
        float theta = .5f * (gain * gain);
        float c = CeltMath.CosNorm(theta);
        float s = CeltMath.CosNorm(1f - theta); // sin(theta)
        int stride2 = 0;
        if (len >= 8 * stride)
        {
            stride2 = 1;
            // A simple (equivalent) way of computing sqrt(len/stride) with rounding.
            while (((stride2 * stride2) + stride2) * stride + (stride >> 2) < len)
            {
                stride2++;
            }
        }

        len = (int)((uint)len / (uint)stride);
        for (int i = 0; i < stride; i++)
        {
            Span<float> xi = x.Slice(i * len, len);
            if (dir < 0)
            {
                if (stride2 != 0)
                {
                    ExpRotation1(xi, len, stride2, s, c);
                }

                ExpRotation1(xi, len, 1, c, s);
            }
            else
            {
                ExpRotation1(xi, len, 1, c, -s);
                if (stride2 != 0)
                {
                    ExpRotation1(xi, len, stride2, s, -c);
                }
            }
        }
    }

    private static void ExpRotation1(Span<float> x, int len, int stride, float c, float s)
    {
        float ms = -s;
        for (int i = 0; i < len - stride; i++)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) + (ms * x2);
        }

        for (int i = len - (2 * stride) - 1; i >= 0; i--)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) + (ms * x2);
        }
    }

    private static void NormaliseResidual(ReadOnlySpan<int> iy, Span<float> x, int n, float ryy, float gain)
    {
        float g = CeltMath.RSqrt(ryy) * gain;
        for (int i = 0; i < n; i++)
        {
            x[i] = g * iy[i];
        }
    }

    private static uint ExtractCollapseMask(ReadOnlySpan<int> iy, int n, int b)
    {
        if (b <= 1)
        {
            return 1;
        }

        int n0 = (int)((uint)n / (uint)b);
        uint collapseMask = 0;
        for (int i = 0; i < b; i++)
        {
            int tmp = 0;
            for (int j = 0; j < n0; j++)
            {
                tmp |= iy[(i * n0) + j];
            }

            collapseMask |= (tmp != 0 ? 1u : 0u) << i;
        }

        return collapseMask;
    }
}
