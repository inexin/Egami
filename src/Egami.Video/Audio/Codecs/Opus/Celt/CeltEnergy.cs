// Translated from libopus 1.5.2 celt/quant_bands.c and celt/laplace.c (decoder side, float build).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>CELT's band energy decoding: the coarse energy with its Laplace model and prediction, then the fine energy bits.</summary>
internal static class CeltEnergy
{
    private const int LaplaceLogMinP = 0;
    private const int LaplaceMinP = 1 << LaplaceLogMinP;
    private const int LaplaceNMin = 16;

    // pred_coef, beta_coef and beta_intra of quant_bands.c (prediction coefficients 0.9, 0.8, 0.65 and 0.5).
    private static ReadOnlySpan<float> PredCoef => [29440 / 32768f, 26112 / 32768f, 21248 / 32768f, 16384 / 32768f];

    private static ReadOnlySpan<float> BetaCoef => [30147 / 32768f, 22282 / 32768f, 12124 / 32768f, 6554 / 32768f];

    private const float BetaIntra = 4915 / 32768f;

    private static ReadOnlySpan<byte> SmallEnergyIcdf => [2, 1, 0];

    /// <summary>ec_laplace_decode: a Laplace-like symbol with probability of zero <paramref name="fs"/>/32768 and the given decay.</summary>
    public static int LaplaceDecode(OpusRangeDecoder dec, uint fs, int decay)
    {
        int val = 0;
        uint fm = dec.DecodeBin(15);
        uint fl = 0;
        if (fm >= fs)
        {
            val++;
            fl = fs;
            fs = GetFreq1(fs, decay) + LaplaceMinP;
            // Search the decaying part of the PDF.
            while (fs > LaplaceMinP && fm >= fl + (2 * fs))
            {
                fs *= 2;
                fl += fs;
                fs = (uint)((int)(fs - (2 * LaplaceMinP)) * decay) >> 15;
                fs += LaplaceMinP;
                val++;
            }

            // Everything beyond that has probability LAPLACE_MINP.
            if (fs <= LaplaceMinP)
            {
                int di = (int)((fm - fl) >> (LaplaceLogMinP + 1));
                val += di;
                fl += (uint)(2 * di * LaplaceMinP);
            }

            if (fm < fl + fs)
            {
                val = -val;
            }
            else
            {
                fl += fs;
            }
        }

        dec.Update(fl, Math.Min(fl + fs, 32768u), 32768);
        return val;
    }

    /// <summary>unquant_coarse_energy.</summary>
    public static void UnquantCoarseEnergy(int start, int end, Span<float> oldEBands, bool intra, OpusRangeDecoder dec, int c, int lm)
    {
        ReadOnlySpan<byte> probModel = CeltTables.EProbModel.Slice(((lm * 2) + (intra ? 1 : 0)) * 42, 42);
        Span<float> prev = [0, 0];
        float coef;
        float beta;
        if (intra)
        {
            coef = 0;
            beta = BetaIntra;
        }
        else
        {
            beta = BetaCoef[lm];
            coef = PredCoef[lm];
        }

        int budget = (int)dec.Storage * 8;
        // Decode at a fixed coarse resolution.
        for (int i = start; i < end; i++)
        {
            int ch = 0;
            do
            {
                int qi;
                int tell = dec.Tell();
                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    qi = LaplaceDecode(dec, (uint)probModel[pi] << 7, probModel[pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = dec.DecodeIcdf(SmallEnergyIcdf, 2);
                    qi = (qi >> 1) ^ -(qi & 1);
                }
                else if (budget - tell >= 1)
                {
                    qi = dec.DecodeBitLogp(1) ? -1 : 0;
                }
                else
                {
                    qi = -1;
                }

                float q = qi;
                int idx = i + (ch * CeltTables.NbEBands);
                oldEBands[idx] = MathF.Max(-9f, oldEBands[idx]);
                float tmp = (coef * oldEBands[idx]) + prev[ch] + q;
                oldEBands[idx] = tmp;
                prev[ch] = prev[ch] + q - (beta * q);
            }
            while (++ch < c);
        }
    }

    /// <summary>unquant_fine_energy.</summary>
    public static void UnquantFineEnergy(int start, int end, Span<float> oldEBands, ReadOnlySpan<int> fineQuant, OpusRangeDecoder dec, int c)
    {
        // Decode finer resolution.
        for (int i = start; i < end; i++)
        {
            if (fineQuant[i] <= 0)
            {
                continue;
            }

            int ch = 0;
            do
            {
                int q2 = (int)dec.DecodeBits(fineQuant[i]);
                float offset = ((q2 + .5f) * (1 << (14 - fineQuant[i])) * (1f / 16384)) - .5f;
                oldEBands[i + (ch * CeltTables.NbEBands)] += offset;
            }
            while (++ch < c);
        }
    }

    /// <summary>unquant_energy_finalise: spends the bits left over on one more fine energy bit per band, by priority.</summary>
    public static void UnquantEnergyFinalise(int start, int end, Span<float> oldEBands, ReadOnlySpan<int> fineQuant, ReadOnlySpan<int> finePriority,
        int bitsLeft, OpusRangeDecoder dec, int c)
    {
        // Use up the remaining bits.
        for (int prio = 0; prio < 2; prio++)
        {
            for (int i = start; i < end && bitsLeft >= c; i++)
            {
                if (fineQuant[i] >= CeltTables.MaxFineBits || finePriority[i] != prio)
                {
                    continue;
                }

                int ch = 0;
                do
                {
                    int q2 = (int)dec.DecodeBits(1);
                    float offset = (q2 - .5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384);
                    oldEBands[i + (ch * CeltTables.NbEBands)] += offset;
                    bitsLeft--;
                }
                while (++ch < c);
            }
        }
    }

    private static uint GetFreq1(uint fs0, int decay)
    {
        uint ft = 32768 - (LaplaceMinP * (2 * LaplaceNMin)) - fs0;
        return (ft * (uint)(16384 - decay)) >> 15;
    }
}
