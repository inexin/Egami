// Translated from libopus 1.5.2 celt/vq.c (op_pvq_search_c, alg_quant, stereo_itheta), celt/cwrs.c (icwrs, encode_pulses) and
// fast_atan2f of celt/mathops.h (float build, SMALL_FOOTPRINT off).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>CELT's pyramid vector quantizer on the encoder side: the pulse search, codeword encoding and the stereo angle.</summary>
internal static partial class CeltVq
{
    /// <summary>
    /// alg_quant: quantizes <paramref name="x"/> (unit norm) to <paramref name="k"/> pulses over <paramref name="n"/> values and writes
    /// the codeword; with <paramref name="resynth"/>, replaces <paramref name="x"/> with the decoded shape at norm <paramref name="gain"/>.
    /// Returns the collapse mask.
    /// </summary>
    public static uint AlgQuant(Span<float> x, int n, int k, int spread, int b, OpusRangeEncoder enc, float gain, bool resynth)
    {
        // Covers vectorization by up to 4.
        Span<int> iy = n + 3 <= 256 ? stackalloc int[n + 3] : new int[n + 3];
        ExpRotation(x, n, 1, b, k, spread);
        float yy = PvqSearch(x, iy, k, n);
        EncodePulses(iy, n, k, enc);
        if (resynth)
        {
            NormaliseResidual(iy, x, n, yy, gain);
            ExpRotation(x, n, -1, b, k, spread);
        }

        return ExtractCollapseMask(iy, n, b);
    }

    /// <summary>
    /// op_pvq_search_c: the pulse vector with <paramref name="k"/> pulses closest in angle to <paramref name="x"/>, into
    /// <paramref name="iy"/>; returns its squared norm. <paramref name="x"/> is left holding its absolute values. libopus's x86 builds
    /// use an SSE2 version whose reciprocal approximations differ between processors; this is the portable C search.
    /// </summary>
    public static float PvqSearch(Span<float> x, Span<int> iy, int k, int n)
    {
        Span<float> y = n <= 256 ? stackalloc float[n] : new float[n];
        Span<bool> signx = n <= 256 ? stackalloc bool[n] : new bool[n];

        // Get rid of the sign.
        float sum = 0;
        int j = 0;
        do
        {
            signx[j] = x[j] < 0;
            x[j] = MathF.Abs(x[j]);
            iy[j] = 0;
            y[j] = 0;
        }
        while (++j < n);

        float xy = 0;
        float yy = 0;
        int pulsesLeft = k;

        // Do a pre-search by projecting on the pyramid.
        if (k > (n >> 1))
        {
            j = 0;
            do
            {
                sum += x[j];
            }
            while (++j < n);

            // If X is too small, just replace it with a pulse at 0. This also prevents infinities and NaNs from causing too many
            // pulses to be allocated; 64 is an approximation of infinity here.
            if (!(sum > 1e-15f && sum < 64))
            {
                x[0] = 1f;
                j = 1;
                do
                {
                    x[j] = 0;
                }
                while (++j < n);
                sum = 1f;
            }

            // Using K+e with e < 1 guarantees we cannot get more than K pulses.
            float rcp = (k + 0.8f) * (1f / sum);
            j = 0;
            do
            {
                iy[j] = (int)MathF.Floor(rcp * x[j]);
                y[j] = iy[j];
                yy += y[j] * y[j];
                xy += x[j] * y[j];
                y[j] *= 2;
                pulsesLeft -= iy[j];
            }
            while (++j < n);
        }

        // This should never happen, but just in case it does (e.g. on silence) we fill the first bin with pulses.
        if (pulsesLeft > n + 3)
        {
            float tmp = pulsesLeft;
            yy += tmp * tmp;
            yy += tmp * y[0];
            iy[0] += pulsesLeft;
            pulsesLeft = 0;
        }

        for (int i = 0; i < pulsesLeft; i++)
        {
            int bestId = 0;
            // The squared magnitude term gets added anyway, so we might as well add it outside the loop.
            yy += 1;
            // Calculations for position 0 are out of the loop.
            float rxy = xy + x[0];
            // We're multiplying y[j] by two so we don't have to do it here.
            float ryy = yy + y[0];
            // Approximate score: we maximise Rxy/sqrt(Ryy).
            rxy *= rxy;
            float bestDen = ryy;
            float bestNum = rxy;
            j = 1;
            do
            {
                rxy = xy + x[j];
                ryy = yy + y[j];
                rxy *= rxy;
                // Check num/den >= best_num/best_den without a division.
                if (bestDen * rxy > ryy * bestNum)
                {
                    bestDen = ryy;
                    bestNum = rxy;
                    bestId = j;
                }
            }
            while (++j < n);

            // Updating the sums of the new pulse(s).
            xy += x[bestId];
            yy += y[bestId];
            // Only now that we've made the final choice, update y/iy.
            y[bestId] += 2;
            iy[bestId]++;
        }

        // Put the original sign back.
        j = 0;
        do
        {
            if (signx[j])
            {
                iy[j] = -iy[j];
            }
        }
        while (++j < n);
        return yy;
    }

    /// <summary>encode_pulses: writes the codeword of pulse vector <paramref name="y"/>.</summary>
    public static void EncodePulses(ReadOnlySpan<int> y, int n, int k, OpusRangeEncoder enc)
    {
        enc.EncodeUint(Icwrs(n, y), PvqV(n, k));
    }

    /// <summary>icwrs: the index of pulse vector <paramref name="y"/>.</summary>
    public static uint Icwrs(int n, ReadOnlySpan<int> y)
    {
        int j = n - 1;
        uint i = y[j] < 0 ? 1u : 0u;
        int k = Math.Abs(y[j]);
        do
        {
            j--;
            i += PvqU(n - j, k);
            k += Math.Abs(y[j]);
            if (y[j] < 0)
            {
                i += PvqU(n - j, k + 1);
            }
        }
        while (j > 0);
        return i;
    }

    /// <summary>
    /// stereo_itheta: the angle (0 to 16384 for 0 to pi/2) between the energies of mid and side (<paramref name="stereo"/>) or of
    /// <paramref name="x"/> and <paramref name="y"/>.
    /// </summary>
    public static int StereoItheta(ReadOnlySpan<float> x, ReadOnlySpan<float> y, bool stereo, int n)
    {
        float emid = 1e-15f;
        float eside = 1e-15f;
        if (stereo)
        {
            for (int i = 0; i < n; i++)
            {
                float m = x[i] + y[i];
                float s = x[i] - y[i];
                emid += m * m;
                eside += s * s;
            }
        }
        else
        {
            emid += CeltMath.InnerProduct(x, x, n);
            eside += CeltMath.InnerProduct(y, y, n);
        }

        float mid = CeltMath.Sqrt(emid);
        float side = CeltMath.Sqrt(eside);
        return (int)MathF.Floor(.5f + (16384 * 0.63662f * FastAtan2(side, mid)));
    }

    /// <summary>fast_atan2f: a rational approximation of atan2.</summary>
    public static float FastAtan2(float y, float x)
    {
        const float A = 0.43157974f;
        const float B = 0.67848403f;
        const float C = 0.08595542f;
        const float E = CeltMath.Pi / 2;
        float x2 = x * x;
        float y2 = y * y;
        // For very small values, we don't care about the answer, so we can just return 0.
        if (x2 + y2 < 1e-18f)
        {
            return 0;
        }

        if (x2 < y2)
        {
            float den = (y2 + (B * x2)) * (y2 + (C * x2));
            return (-x * y * (y2 + (A * x2)) / den) + (y < 0 ? -E : E);
        }
        else
        {
            float den = (x2 + (B * y2)) * (x2 + (C * y2));
            return (x * y * (x2 + (A * y2)) / den) + (y < 0 ? -E : E) - (x * y < 0 ? -E : E);
        }
    }
}
