// Translated from libopus 1.5.2 celt/quant_bands.c (encoder side, float build).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>CELT's band energy encoding: coarse energy with the intra/inter decision, fine energy and the final bits.</summary>
internal static partial class CeltEnergy
{
    /// <summary>
    /// quant_coarse_energy: codes the coarse energies of <paramref name="eBands"/> against <paramref name="oldEBands"/>, which it
    /// updates to the quantized values, and leaves the quantization error in <paramref name="error"/>. With <paramref name="twoPass"/>
    /// it tries intra and inter prediction and keeps the better one; <paramref name="delayedIntra"/> carries the decision state.
    /// </summary>
    public static void QuantCoarseEnergy(int start, int end, int effEnd, ReadOnlySpan<float> eBands, Span<float> oldEBands, uint budget,
        Span<float> error, OpusRangeEncoder enc, int c, int lm, int nbAvailableBytes, bool forceIntra, ref float delayedIntra, bool twoPass,
        int lossRate, bool lfe)
    {
        const int Nb = CeltTables.NbEBands;
        bool intra = forceIntra || (!twoPass && delayedIntra > 2 * c * (end - start) && nbAvailableBytes > (end - start) * c);
        int intraBias = (int)(budget * delayedIntra * lossRate / (c * 512));
        float newDistortion = LossDistortion(eBands, oldEBands, start, effEnd, Nb, c);

        uint tell = (uint)enc.Tell();
        if (tell + 3 > budget)
        {
            twoPass = intra = false;
        }

        float maxDecay = 16f;
        if (end - start > 10)
        {
            maxDecay = MathF.Min(maxDecay, .125f * nbAvailableBytes);
        }

        if (lfe)
        {
            maxDecay = 3f;
        }

        OpusRangeEncoder.EncoderState encStartState = enc.State;
        Span<float> oldEBandsIntra = stackalloc float[c * Nb];
        Span<float> errorIntra = stackalloc float[c * Nb];
        oldEBands[..(c * Nb)].CopyTo(oldEBandsIntra);
        int badness1 = 0;
        if (twoPass || intra)
        {
            badness1 = QuantCoarseEnergyImpl(start, end, eBands, oldEBandsIntra, (int)budget, (int)tell, ProbModel(lm, true), errorIntra, enc, c, lm,
                true, maxDecay, lfe);
        }

        if (!intra)
        {
            int tellIntra = (int)enc.TellFrac();
            OpusRangeEncoder.EncoderState encIntraState = enc.State;
            int nstartBytes = (int)encStartState.Offset;
            int nintraBytes = (int)encIntraState.Offset;
            Span<byte> intraBuf = enc.Buffer.AsSpan(enc.Start + nstartBytes, nintraBytes - nstartBytes);
            // Copy bits from intra bit-stream.
            Span<byte> intraBits = stackalloc byte[nintraBytes - nstartBytes];
            intraBuf.CopyTo(intraBits);
            enc.State = encStartState;
            int badness2 = QuantCoarseEnergyImpl(start, end, eBands, oldEBands, (int)budget, (int)tell, ProbModel(lm, false), error, enc, c, lm, false,
                maxDecay, lfe);
            if (twoPass && (badness1 < badness2 || (badness1 == badness2 && (int)enc.TellFrac() + intraBias > tellIntra)))
            {
                enc.State = encIntraState;
                // Copy intra bits to bit-stream.
                intraBits.CopyTo(intraBuf);
                oldEBandsIntra.CopyTo(oldEBands);
                errorIntra.CopyTo(error);
                intra = true;
            }
        }
        else
        {
            oldEBandsIntra.CopyTo(oldEBands);
            errorIntra.CopyTo(error);
        }

        if (intra)
        {
            delayedIntra = newDistortion;
        }
        else
        {
            delayedIntra = (PredCoef[lm] * PredCoef[lm] * delayedIntra) + newDistortion;
        }
    }

    /// <summary>quant_fine_energy.</summary>
    public static void QuantFineEnergy(int start, int end, Span<float> oldEBands, Span<float> error, ReadOnlySpan<int> fineQuant, OpusRangeEncoder enc,
        int c)
    {
        // Encode finer resolution.
        for (int i = start; i < end; i++)
        {
            int frac = (short)(1 << fineQuant[i]);
            if (fineQuant[i] <= 0)
            {
                continue;
            }

            int ch = 0;
            do
            {
                int idx = i + (ch * CeltTables.NbEBands);
                int q2 = (int)MathF.Floor((error[idx] + .5f) * frac);
                if (q2 > frac - 1)
                {
                    q2 = frac - 1;
                }

                if (q2 < 0)
                {
                    q2 = 0;
                }

                enc.EncodeBits((uint)q2, fineQuant[i]);
                float offset = ((q2 + .5f) * (1 << (14 - fineQuant[i])) * (1f / 16384)) - .5f;
                oldEBands[idx] += offset;
                error[idx] -= offset;
            }
            while (++ch < c);
        }
    }

    /// <summary>quant_energy_finalise: spends the bits left over on one more fine energy bit per band, by priority.</summary>
    public static void QuantEnergyFinalise(int start, int end, Span<float> oldEBands, Span<float> error, ReadOnlySpan<int> fineQuant,
        ReadOnlySpan<int> finePriority, int bitsLeft, OpusRangeEncoder enc, int c)
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
                    int idx = i + (ch * CeltTables.NbEBands);
                    int q2 = error[idx] < 0 ? 0 : 1;
                    enc.EncodeBits((uint)q2, 1);
                    float offset = (q2 - .5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384);
                    oldEBands[idx] += offset;
                    error[idx] -= offset;
                    bitsLeft--;
                }
                while (++ch < c);
            }
        }
    }

    /// <summary>amp2Log2: the band amplitudes as log2 energies relative to the band means.</summary>
    public static void Amp2Log2(int effEnd, int end, ReadOnlySpan<float> bandE, Span<float> bandLogE, int c)
    {
        ReadOnlySpan<float> eMeans = CeltTables.EMeans;
        int ch = 0;
        do
        {
            for (int i = 0; i < effEnd; i++)
            {
                int idx = i + (ch * CeltTables.NbEBands);
                bandLogE[idx] = Log2(bandE[idx]) - eMeans[i];
            }

            for (int i = effEnd; i < end; i++)
            {
                bandLogE[(ch * CeltTables.NbEBands) + i] = -14f;
            }
        }
        while (++ch < c);
    }

    /// <summary>celt_log2 without FLOAT_APPROX.</summary>
    public static float Log2(float x) => (float)(1.442695040888963387 * Math.Log(x));

    private static ReadOnlySpan<byte> ProbModel(int lm, bool intra) => CeltTables.EProbModel.Slice(((lm * 2) + (intra ? 1 : 0)) * 42, 42);

    private static float LossDistortion(ReadOnlySpan<float> eBands, ReadOnlySpan<float> oldEBands, int start, int end, int len, int c)
    {
        float dist = 0;
        int ch = 0;
        do
        {
            for (int i = start; i < end; i++)
            {
                float d = eBands[i + (ch * len)] - oldEBands[i + (ch * len)];
                dist += d * d;
            }
        }
        while (++ch < c);
        return MathF.Min(200, dist);
    }

    private static int QuantCoarseEnergyImpl(int start, int end, ReadOnlySpan<float> eBands, Span<float> oldEBands, int budget, int tell,
        ReadOnlySpan<byte> probModel, Span<float> error, OpusRangeEncoder enc, int c, int lm, bool intra, float maxDecay, bool lfe)
    {
        const int Nb = CeltTables.NbEBands;
        int badness = 0;
        Span<float> prev = [0, 0];
        if (tell + 3 <= budget)
        {
            enc.EncodeBitLogp(intra, 3);
        }

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

        // Encode at a fixed coarse resolution.
        for (int i = start; i < end; i++)
        {
            int ch = 0;
            do
            {
                int idx = i + (ch * Nb);
                float x = eBands[idx];
                float oldE = MathF.Max(-9f, oldEBands[idx]);
                float f = x - (coef * oldE) - prev[ch];
                // Rounding to nearest integer here is really important!
                int qi = (int)MathF.Floor(.5f + f);
                float decayBound = MathF.Max(-28f, oldEBands[idx]) - maxDecay;
                // Prevent the energy from going down too quickly (e.g. for bands that have just one bin).
                if (qi < 0 && x < decayBound)
                {
                    qi += (int)(decayBound - x);
                    if (qi > 0)
                    {
                        qi = 0;
                    }
                }

                int qi0 = qi;
                // If we don't have enough bits to encode all the energy, just assume something safe.
                tell = enc.Tell();
                int bitsLeft = budget - tell - (3 * c * (end - i));
                if (i != start && bitsLeft < 30)
                {
                    if (bitsLeft < 24)
                    {
                        qi = Math.Min(1, qi);
                    }

                    if (bitsLeft < 16)
                    {
                        qi = Math.Max(-1, qi);
                    }
                }

                if (lfe && i >= 2)
                {
                    qi = Math.Min(qi, 0);
                }

                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    enc.LaplaceEncode(ref qi, (uint)probModel[pi] << 7, probModel[pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = Math.Max(-1, Math.Min(qi, 1));
                    enc.EncodeIcdf((2 * qi) ^ -(qi < 0 ? 1 : 0), SmallEnergyIcdf, 2);
                }
                else if (budget - tell >= 1)
                {
                    qi = Math.Min(0, qi);
                    enc.EncodeBitLogp(qi != 0, 1);
                }
                else
                {
                    qi = -1;
                }

                error[idx] = f - qi;
                badness += Math.Abs(qi0 - qi);
                float q = qi;
                float tmp = (coef * oldE) + prev[ch] + q;
                oldEBands[idx] = tmp;
                prev[ch] = prev[ch] + q - (beta * q);
            }
            while (++ch < c);
        }

        return lfe ? 0 : badness;
    }
}
