namespace Egami.Formats.Jxl;

/// <summary>
/// The lossy encoder's chroma-from-luma search (Phase 2.9.7, effort 5 and up): libjxl's CfLHeuristics (enc_chroma_from_luma,
/// see THIRD-PARTY-NOTICES). For each 64×64 tile, the factor that best predicts X and B's HF coefficients from Y's, in steps of
/// 1/84, either by least squares (fast) or by Newton steps on a cost that grows with the residual's magnitude.
/// </summary>
internal static class JxlChromaFromLuma
{
    private const float InvColorFactor = 1.0f / 84;
    private const float DistanceMultiplierAc = 1e-9f;

    /// <summary>
    /// Finds a tile's two factors from its varblocks' coefficients: Y's and X's (or Y's and B's), each already scaled by the
    /// quantization step and inverse weights. <paramref name="baseCorrelation"/> is the factor the decoder adds before the
    /// tile's own (0 for X, 1 for B).
    /// </summary>
    internal static int FindBestMultiplier(ReadOnlySpan<float> valuesM, ReadOnlySpan<float> valuesS, float baseCorrelation, bool fast)
    {
        int num = valuesM.Length;
        if (num == 0)
        {
            return 0;
        }

        float x;
        if (fast)
        {
            float ca = 0;
            float cb = 0;
            for (int i = 0; i < num; i++)
            {
                float a = InvColorFactor * valuesM[i];
                float b = (baseCorrelation * valuesM[i]) - valuesS[i];
                ca = MathF.FusedMultiplyAdd(a, a, ca);
                cb = MathF.FusedMultiplyAdd(a, b, cb);
            }

            x = -cb / (ca + (num * DistanceMultiplierAc * 0.5f));
        }
        else
        {
            const float Eps = 100;
            const float Clamp = 20.0f;
            x = 0;
            for (int i = 0; i < 20; i++)
            {
                float d = Derivative(valuesM, valuesS, baseCorrelation, x, Eps, out float dpeps, out float dmeps);
                float ddf = (dpeps - dmeps) / (2 * Eps);
                const float Stabilizer = 0.85f;
                float step = d / (ddf + Stabilizer);
                x -= Math.Clamp(step, -Clamp, Clamp);
                if (MathF.Abs(step) < 3e-3f)
                {
                    break;
                }
            }
        }

        // Pulled towards zero, which keeps large transforms from red-green oscillations.
        const float TowardsZero = 2.6f;
        x = x >= TowardsZero ? x - TowardsZero : x <= -TowardsZero ? x + TowardsZero : 0;
        return (int)Math.Clamp(MathF.Round(x, MidpointRounding.AwayFromZero), -128f, 127f);
    }

    /// <summary>CFLFunction::Compute: f'(x) of 1/3 Σ((|residual| + 1)² − 1) + distance_mul · x² · n, and f' at x ± eps.</summary>
    private static float Derivative(ReadOnlySpan<float> valuesM, ReadOnlySpan<float> valuesS, float baseCorrelation, float x, float eps, out float fpeps, out float fmeps)
    {
        const float Coeff = 1f / 3;
        const float Thres = 100.0f;
        int num = valuesM.Length;
        float first = 2 * DistanceMultiplierAc * num * x;
        float firstPeps = 2 * DistanceMultiplierAc * num * (x + eps);
        float firstMeps = 2 * DistanceMultiplierAc * num * (x - eps);
        float fd = 0, fdpe = 0, fdme = 0;
        for (int i = 0; i < num; i++)
        {
            float a = InvColorFactor * valuesM[i];
            float b = (baseCorrelation * valuesM[i]) - valuesS[i];
            float v = MathF.FusedMultiplyAdd(a, x, b);
            float vpe = MathF.FusedMultiplyAdd(a, x + eps, b);
            float vme = MathF.FusedMultiplyAdd(a, x - eps, b);
            float av = MathF.Abs(v);
            float acoeffx2 = Coeff * 2.0f * a;
            float d = acoeffx2 * (av + 1);
            float dpe = acoeffx2 * (MathF.Abs(vpe) + 1);
            float dme = acoeffx2 * (MathF.Abs(vme) + 1);
            d = v < 0 ? -d : d;
            dpe = vpe < 0 ? -dpe : dpe;
            dme = vme < 0 ? -dme : dme;
            if (av < Thres)
            {
                fd += d;
                fdpe += dpe;
                fdme += dme;
            }
        }

        fpeps = firstPeps + fdpe;
        fmeps = firstMeps + fdme;
        return first + fd;
    }
}
