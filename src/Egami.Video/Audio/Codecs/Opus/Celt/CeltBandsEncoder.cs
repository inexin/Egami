// Translated from libopus 1.5.2 celt/bands.c (encoder side: compute_band_energies, normalise_bands, spreading_decision,
// hysteresis_decision and quant_all_bands with what it calls; float build).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// The encoding of CELT's normalized band shapes: the recursive band splitting with its theta angles, PVQ, the time-frequency
/// changes, intensity and dual stereo, and the rate-distortion choice of the stereo angle at high complexity. One instance per
/// encoder keeps the folding buffer and the band context.
/// </summary>
internal sealed class CeltBandsEncoder
{
    private const int BitRes = CeltTables.BitRes;
    private const int Nb = CeltTables.NbEBands;

    // The size of the last band at LM 3, the largest band any one call works on.
    private const int MaxBand = 8 * 22;

    private readonly float[] _norm = new float[(2 * 8 * 100) + 200];
    private readonly float[] _scratch = new float[8 * 100];
    private readonly float[] _lowbandScratch = new float[MaxBand];
    private readonly float[] _xSave = new float[MaxBand];
    private readonly float[] _ySave = new float[MaxBand];
    private readonly float[] _xSave2 = new float[MaxBand];
    private readonly float[] _ySave2 = new float[MaxBand];
    private readonly float[] _normSave2 = new float[MaxBand];
    private readonly byte[] _bytesSave = new byte[1275];

    private OpusRangeEncoder _enc = null!;
    private float[] _bandE = null!;
    private BandContext _ctx;

    // struct band_ctx, without the fields that never change during a frame.
    private struct BandContext
    {
        public bool Resynth;
        public int Band;
        public int Intensity;
        public int Spread;
        public int TfChange;
        public int RemainingBits;
        public uint Seed;
        public int ThetaRound;
        public bool DisableInv;
        public bool AvoidSplitNoise;
    }

    /// <summary>compute_band_energies: the amplitude (square root of the energy) of each band.</summary>
    public static void ComputeBandEnergies(ReadOnlySpan<float> x, Span<float> bandE, int end, int c, int lm)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int n = CeltTables.ShortMdctSize << lm;
        int ch = 0;
        do
        {
            for (int i = 0; i < end; i++)
            {
                ReadOnlySpan<float> band = x[((ch * n) + (eBands[i] << lm))..];
                float sum = 1e-27f + CeltMath.InnerProduct(band, band, (eBands[i + 1] - eBands[i]) << lm);
                bandE[i + (ch * Nb)] = CeltMath.Sqrt(sum);
            }
        }
        while (++ch < c);
    }

    /// <summary>normalise_bands: scales each band to unit energy.</summary>
    public static void NormaliseBands(ReadOnlySpan<float> freq, Span<float> x, ReadOnlySpan<float> bandE, int end, int c, int m)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int n = m * CeltTables.ShortMdctSize;
        int ch = 0;
        do
        {
            for (int i = 0; i < end; i++)
            {
                float g = 1f / (1e-27f + bandE[i + (ch * Nb)]);
                for (int j = m * eBands[i]; j < m * eBands[i + 1]; j++)
                {
                    x[j + (ch * n)] = freq[j + (ch * n)] * g;
                }
            }
        }
        while (++ch < c);
    }

    /// <summary>hysteresis_decision.</summary>
    public static int HysteresisDecision(float val, ReadOnlySpan<float> thresholds, ReadOnlySpan<float> hysteresis, int n, int prev)
    {
        int i;
        for (i = 0; i < n; i++)
        {
            if (val < thresholds[i])
            {
                break;
            }
        }

        if (i > prev && val < thresholds[prev] + hysteresis[prev])
        {
            i = prev;
        }

        if (i < prev && val > thresholds[prev - 1] - hysteresis[prev - 1])
        {
            i = prev;
        }

        return i;
    }

    /// <summary>spreading_decision: decides whether to spread the pulses in the current frame, and updates the tapset decision.</summary>
    public static int SpreadingDecision(ReadOnlySpan<float> x, ref int average, int lastDecision, ref int hfAverage, ref int tapsetDecision, bool updateHf,
        int end, int c, int m, ReadOnlySpan<int> spreadWeight)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int sum = 0;
        int nbBands = 0;
        int hfSum = 0;
        int n0 = m * CeltTables.ShortMdctSize;
        if (m * (eBands[end] - eBands[end - 1]) <= 8)
        {
            return CeltTables.SpreadNone;
        }

        int ch = 0;
        do
        {
            for (int i = 0; i < end; i++)
            {
                int n = m * (eBands[i + 1] - eBands[i]);
                if (n <= 8)
                {
                    continue;
                }

                ReadOnlySpan<float> xb = x.Slice((m * eBands[i]) + (ch * n0), n);
                int t0 = 0, t1 = 0, t2 = 0;
                // Compute rough CDF of |x[j]|.
                for (int j = 0; j < n; j++)
                {
                    float x2N = xb[j] * xb[j] * n;
                    if (x2N < .25f)
                    {
                        t0++;
                    }

                    if (x2N < .0625f)
                    {
                        t1++;
                    }

                    if (x2N < .015625f)
                    {
                        t2++;
                    }
                }

                // Only include four last bands (8 kHz and up).
                if (i > Nb - 4)
                {
                    hfSum += (int)((uint)(32 * (t1 + t0)) / (uint)n);
                }

                int tmp = (2 * t2 >= n ? 1 : 0) + (2 * t1 >= n ? 1 : 0) + (2 * t0 >= n ? 1 : 0);
                sum += tmp * spreadWeight[i];
                nbBands += spreadWeight[i];
            }
        }
        while (++ch < c);

        if (updateHf)
        {
            if (hfSum != 0)
            {
                hfSum = (int)((uint)hfSum / (uint)(c * (4 - Nb + end)));
            }

            hfAverage = (hfAverage + hfSum) >> 1;
            hfSum = hfAverage;
            if (tapsetDecision == 2)
            {
                hfSum += 4;
            }
            else if (tapsetDecision == 0)
            {
                hfSum -= 4;
            }

            if (hfSum > 22)
            {
                tapsetDecision = 2;
            }
            else if (hfSum > 18)
            {
                tapsetDecision = 1;
            }
            else
            {
                tapsetDecision = 0;
            }
        }

        sum = (int)((uint)(sum << 8) / (uint)nbBands);
        // Recursive averaging.
        sum = (sum + average) >> 1;
        average = sum;
        // Hysteresis.
        sum = ((3 * sum) + (((3 - lastDecision) << 7) + 64) + 2) >> 2;
        if (sum < 80)
        {
            return CeltTables.SpreadAggressive;
        }
        else if (sum < 256)
        {
            return CeltTables.SpreadNormal;
        }
        else if (sum < 384)
        {
            return CeltTables.SpreadLight;
        }

        return CeltTables.SpreadNone;
    }

    /// <summary>
    /// quant_all_bands for the encoder: codes the shapes of bands [start, end) of <paramref name="x"/> (and <paramref name="y"/> for
    /// stereo, empty for mono); <paramref name="bandE"/> holds the band amplitudes for intensity stereo and <paramref name="seed"/> the
    /// folding noise seed.
    /// </summary>
    public void QuantAllBands(int start, int end, Span<float> x, Span<float> y, bool stereo, Span<byte> collapseMasks, float[] bandE, ReadOnlySpan<int> pulses,
        bool shortBlocks, int spread, bool dualStereo, int intensity, ReadOnlySpan<int> tfRes, int totalBits, int balance, OpusRangeEncoder enc, int lm,
        int codedBands, ref uint seed, int complexity, bool disableInv)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << lm;
        int b = shortBlocks ? m : 1;
        int c = stereo ? 2 : 1;
        bool thetaRdo = stereo && !dualStereo && complexity >= 8;
        bool resynth = thetaRdo;
        int normOffset = m * eBands[start];
        // No need to allocate norm for the last band because we don't need an output in that band.
        Span<float> normBuf = _norm;
        int norm2 = (m * eBands[Nb - 1]) - normOffset;
        normBuf[..(c * norm2)].Clear();
        Span<float> lowbandScratch = resynth ? _lowbandScratch : default;
        int lowbandOffset = 0;
        bool updateLowband = true;
        _enc = enc;
        _bandE = bandE;
        _ctx = default;
        _ctx.Intensity = intensity;
        _ctx.Seed = seed;
        _ctx.Spread = spread;
        _ctx.DisableInv = disableInv;
        _ctx.Resynth = resynth;
        _ctx.ThetaRound = 0;
        // Avoid injecting noise in the first band on transients.
        _ctx.AvoidSplitNoise = b > 1;
        for (int i = start; i < end; i++)
        {
            _ctx.Band = i;
            bool last = i == end - 1;
            int bandStart = m * eBands[i];
            int n = (m * eBands[i + 1]) - (m * eBands[i]);
            Span<float> xb = x.Slice(bandStart, n);
            Span<float> yb = stereo ? y.Slice(bandStart, n) : default;
            int tell = (int)enc.TellFrac();

            // Compute how many bits we want to allocate to this band.
            if (i != start)
            {
                balance -= tell;
            }

            int remainingBits = totalBits - tell - 1;
            _ctx.RemainingBits = remainingBits;
            int bits;
            if (i <= codedBands - 1)
            {
                int currBalance = balance / Math.Min(3, codedBands - i);
                bits = Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, pulses[i] + currBalance)));
            }
            else
            {
                bits = 0;
            }

            if (resynth && ((m * eBands[i]) - n >= m * eBands[start] || i == start + 1) && (updateLowband || lowbandOffset == 0))
            {
                lowbandOffset = i;
            }

            if (i == start + 1)
            {
                CeltBands.SpecialHybridFolding(normBuf, norm2, start, m, dualStereo);
            }

            int tfChange = tfRes[i];
            _ctx.TfChange = tfChange;
            if (last && !thetaRdo)
            {
                lowbandScratch = default;
            }

            // Get a conservative estimate of the collapse masks of the bands we're going to be folding from.
            int effectiveLowband = -1;
            uint xcm;
            uint ycm;
            if (lowbandOffset != 0 && (spread != CeltTables.SpreadAggressive || b > 1 || tfChange < 0))
            {
                // This ensures we never repeat spectral content within one band.
                effectiveLowband = Math.Max(0, (m * eBands[lowbandOffset]) - normOffset - n);
                int foldStart = lowbandOffset;
                while (m * eBands[--foldStart] > effectiveLowband + normOffset)
                {
                }

                int foldEnd = lowbandOffset - 1;
                while (++foldEnd < i && m * eBands[foldEnd] < effectiveLowband + normOffset + n)
                {
                }

                xcm = ycm = 0;
                int foldI = foldStart;
                do
                {
                    xcm |= collapseMasks[foldI * c];
                    ycm |= collapseMasks[(foldI * c) + c - 1];
                }
                while (++foldI < foldEnd);
            }
            else
            {
                // Otherwise, we'll be using the LCG to fold, so all blocks will (almost always) be non-zero.
                xcm = ycm = (1u << b) - 1;
            }

            if (dualStereo && i == intensity)
            {
                // Switch off dual stereo to do intensity.
                dualStereo = false;
                if (resynth)
                {
                    for (int j = 0; j < (m * eBands[i]) - normOffset; j++)
                    {
                        normBuf[j] = .5f * (normBuf[j] + normBuf[norm2 + j]);
                    }
                }
            }

            Span<float> lowband = effectiveLowband != -1 ? normBuf.Slice(effectiveLowband, n) : default;
            Span<float> lowbandOut = last ? default : normBuf.Slice((m * eBands[i]) - normOffset, n);
            if (dualStereo)
            {
                Span<float> lowband2 = effectiveLowband != -1 ? normBuf.Slice(norm2 + effectiveLowband, n) : default;
                Span<float> lowbandOut2 = last ? default : normBuf.Slice(norm2 + (m * eBands[i]) - normOffset, n);
                xcm = QuantBand(xb, n, bits / 2, b, lowband, lm, lowbandOut, 1f, lowbandScratch, (int)xcm);
                ycm = QuantBand(yb, n, bits / 2, b, lowband2, lm, lowbandOut2, 1f, lowbandScratch, (int)ycm);
            }
            else
            {
                if (stereo)
                {
                    if (thetaRdo && i < intensity)
                    {
                        xcm = QuantBandStereoRdo(xb, yb, n, bits, b, lowband, lm, lowbandOut, lowbandScratch, xcm | ycm, i, start, last, normBuf,
                            norm2, m, dualStereo);
                    }
                    else
                    {
                        _ctx.ThetaRound = 0;
                        xcm = QuantBandStereo(xb, yb, n, bits, b, lowband, lm, lowbandOut, lowbandScratch, (int)(xcm | ycm));
                    }
                }
                else
                {
                    xcm = QuantBand(xb, n, bits, b, lowband, lm, lowbandOut, 1f, lowbandScratch, (int)(xcm | ycm));
                }

                ycm = xcm;
            }

            collapseMasks[i * c] = (byte)xcm;
            collapseMasks[(i * c) + c - 1] = (byte)ycm;
            balance += pulses[i] + tell;

            // Update the folding position only as long as we have 1 bit/sample depth.
            updateLowband = bits > n << BitRes;
            // We only need to avoid noise on a split for the first band. After that, we have folding.
            _ctx.AvoidSplitNoise = false;
        }

        seed = _ctx.Seed;
    }

    // compute_channel_weights: weights for the stereo distortion from the band amplitudes.
    private static void ComputeChannelWeights(float ex, float ey, out float w0, out float w1)
    {
        float minE = MathF.Min(ex, ey);
        // Adjustment to make the weights a bit more conservative.
        w0 = ex + (minE / 3);
        w1 = ey + (minE / 3);
    }

    // The theta_rdo path of quant_all_bands: codes the band with theta rounded down and up and keeps the one with less distortion.
    private uint QuantBandStereoRdo(Span<float> x, Span<float> y, int n, int bits, int b, Span<float> lowband, int lm, Span<float> lowbandOut,
        Span<float> lowbandScratch, uint cm, int i, int start, bool last, Span<float> normBuf, int norm2, int m, bool dualStereo)
    {
        OpusRangeEncoder enc = _enc;
        ComputeChannelWeights(_bandE[i], _bandE[i + Nb], out float w0, out float w1);
        Span<float> xSave = _xSave.AsSpan(0, n);
        Span<float> ySave = _ySave.AsSpan(0, n);
        Span<float> xSave2 = _xSave2.AsSpan(0, n);
        Span<float> ySave2 = _ySave2.AsSpan(0, n);
        Span<float> normSave2 = _normSave2.AsSpan(0, n);

        // Make a copy.
        OpusRangeEncoder.EncoderState ecSave = enc.State;
        BandContext ctxSave = _ctx;
        x.CopyTo(xSave);
        y.CopyTo(ySave);

        // Encode and round down.
        _ctx.ThetaRound = -1;
        uint xcm = QuantBandStereo(x, y, n, bits, b, lowband, lm, lowbandOut, lowbandScratch, (int)cm);
        float dist0 = (w0 * CeltMath.InnerProduct(xSave, x, n)) + (w1 * CeltMath.InnerProduct(ySave, y, n));

        // Save first result.
        uint cm2 = xcm;
        OpusRangeEncoder.EncoderState ecSave2 = enc.State;
        BandContext ctxSave2 = _ctx;
        x.CopyTo(xSave2);
        y.CopyTo(ySave2);
        if (!last)
        {
            lowbandOut.CopyTo(normSave2);
        }

        int nstartBytes = (int)ecSave.Offset;
        int nendBytes = (int)ecSave.Storage;
        Span<byte> bytesBuf = enc.Buffer.AsSpan(enc.Start + nstartBytes, nendBytes - nstartBytes);
        Span<byte> bytesSave = _bytesSave.AsSpan(0, nendBytes - nstartBytes);
        bytesBuf.CopyTo(bytesSave);

        // Restore.
        enc.State = ecSave;
        _ctx = ctxSave;
        xSave.CopyTo(x);
        ySave.CopyTo(y);
        if (i == start + 1)
        {
            CeltBands.SpecialHybridFolding(normBuf, norm2, start, m, dualStereo);
        }

        // Encode and round up.
        _ctx.ThetaRound = 1;
        xcm = QuantBandStereo(x, y, n, bits, b, lowband, lm, lowbandOut, lowbandScratch, (int)cm);
        float dist1 = (w0 * CeltMath.InnerProduct(xSave, x, n)) + (w1 * CeltMath.InnerProduct(ySave, y, n));
        if (dist0 >= dist1)
        {
            xcm = cm2;
            enc.State = ecSave2;
            _ctx = ctxSave2;
            xSave2.CopyTo(x);
            ySave2.CopyTo(y);
            if (!last)
            {
                normSave2.CopyTo(lowbandOut);
            }

            bytesSave.CopyTo(bytesBuf);
        }

        return xcm;
    }

    // intensity_stereo: replaces X with the energy-weighted sum of X and Y; the side is not coded.
    private static void IntensityStereo(Span<float> x, ReadOnlySpan<float> y, float[] bandE, int i, int n)
    {
        float left = bandE[i];
        float right = bandE[i + Nb];
        float norm = 1e-15f + CeltMath.Sqrt(1e-15f + (left * left) + (right * right));
        float a1 = left / norm;
        float a2 = right / norm;
        for (int j = 0; j < n; j++)
        {
            x[j] = (a1 * x[j]) + (a2 * y[j]);
        }
    }

    // stereo_split: X and Y to mid and side.
    private static void StereoSplit(Span<float> x, Span<float> y, int n)
    {
        for (int j = 0; j < n; j++)
        {
            float l = .70710678f * x[j];
            float r = .70710678f * y[j];
            x[j] = l + r;
            y[j] = r - l;
        }
    }

    // compute_theta, encoder side.
    private void ComputeTheta(Span<float> x, Span<float> y, out bool inv, out int imid, out int iside, out int delta, out int itheta, out int qalloc, int n,
        ref int b, int bBlocks, int b0, int lm, bool stereo, ref int fill)
    {
        OpusRangeEncoder enc = _enc;
        int i = _ctx.Band;
        inv = false;

        // Decide on the resolution to give to the split parameter theta.
        int pulseCap = CeltTables.LogN400[i] + (lm * (1 << BitRes));
        int offset = (pulseCap >> 1) - (stereo && n == 2 ? CeltTables.QThetaOffsetTwoPhase : CeltTables.QThetaOffset);
        int qn = CeltBands.ComputeQn(n, b, offset, pulseCap, stereo);
        if (stereo && i >= _ctx.Intensity)
        {
            qn = 1;
        }

        // theta is the atan() of the ratio between the (normalized) side and mid. With just that parameter, we can re-scale both
        // mid and side because we know that 1) they have unit norm and 2) they are orthogonal.
        itheta = CeltVq.StereoItheta(x, y, stereo, n);
        int tell = (int)enc.TellFrac();
        if (qn != 1)
        {
            if (!stereo || _ctx.ThetaRound == 0)
            {
                itheta = ((itheta * qn) + 8192) >> 14;
                if (!stereo && _ctx.AvoidSplitNoise && itheta > 0 && itheta < qn)
                {
                    // Check if the selected value of theta will cause the bit allocation to inject noise on one side. If so, make sure
                    // the energy of that side is zero.
                    int unquantized = (int)((uint)(itheta * 16384) / (uint)qn);
                    int qimid = CeltMath.BitexactCos((short)unquantized);
                    int qiside = CeltMath.BitexactCos((short)(16384 - unquantized));
                    int qdelta = CeltMath.FracMul16((n - 1) << 7, CeltMath.BitexactLog2Tan(qiside, qimid));
                    if (qdelta > b)
                    {
                        itheta = qn;
                    }
                    else if (qdelta < -b)
                    {
                        itheta = 0;
                    }
                }
            }
            else
            {
                // Bias quantization towards itheta=0 and itheta=16384.
                int bias = itheta > 8192 ? 32767 / qn : -32767 / qn;
                int down = Math.Min(qn - 1, Math.Max(0, ((itheta * qn) + bias) >> 14));
                itheta = _ctx.ThetaRound < 0 ? down : down + 1;
            }

            // Entropy coding of the angle: uniform pdf for the time split, a step for stereo, triangular for the rest.
            if (stereo && n > 2)
            {
                const int P0 = 3;
                int xx = itheta;
                int x0 = qn / 2;
                int ft = (P0 * (x0 + 1)) + x0;
                // Use a probability of p0 up to itheta=8192 and then use 1 after.
                enc.Encode((uint)(xx <= x0 ? P0 * xx : (xx - 1 - x0) + ((x0 + 1) * P0)), (uint)(xx <= x0 ? P0 * (xx + 1) : (xx - x0) + ((x0 + 1) * P0)),
                    (uint)ft);
            }
            else if (b0 > 1 || stereo)
            {
                // Uniform pdf.
                enc.EncodeUint((uint)itheta, (uint)(qn + 1));
            }
            else
            {
                // Triangular pdf.
                int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                int fs = itheta <= qn >> 1 ? itheta + 1 : qn + 1 - itheta;
                int fl = itheta <= qn >> 1 ? itheta * (itheta + 1) >> 1 : ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                enc.Encode((uint)fl, (uint)(fl + fs), (uint)ft);
            }

            itheta = (int)((uint)(itheta * 16384) / (uint)qn);
            if (stereo)
            {
                if (itheta == 0)
                {
                    IntensityStereo(x, y, _bandE, i, n);
                }
                else
                {
                    StereoSplit(x, y, n);
                }
            }
        }
        else if (stereo)
        {
            inv = itheta > 8192 && !_ctx.DisableInv;
            if (inv)
            {
                for (int j = 0; j < n; j++)
                {
                    y[j] = -y[j];
                }
            }

            IntensityStereo(x, y, _bandE, i, n);
            if (b > 2 << BitRes && _ctx.RemainingBits > 2 << BitRes)
            {
                enc.EncodeBitLogp(inv, 2);
            }
            else
            {
                inv = false;
            }

            // inv flag override to avoid problems with downmixing.
            if (_ctx.DisableInv)
            {
                inv = false;
            }

            itheta = 0;
        }

        qalloc = (int)enc.TellFrac() - tell;
        b -= qalloc;

        if (itheta == 0)
        {
            imid = 32767;
            iside = 0;
            fill &= (1 << bBlocks) - 1;
            delta = -16384;
        }
        else if (itheta == 16384)
        {
            imid = 0;
            iside = 32767;
            fill &= ((1 << bBlocks) - 1) << bBlocks;
            delta = 16384;
        }
        else
        {
            imid = CeltMath.BitexactCos((short)itheta);
            iside = CeltMath.BitexactCos((short)(16384 - itheta));
            // This is the mid vs side allocation that minimizes squared error in that band.
            delta = CeltMath.FracMul16((n - 1) << 7, CeltMath.BitexactLog2Tan(iside, imid));
        }
    }

    // quant_band_n1, encoder side.
    private uint QuantBandN1(Span<float> x, Span<float> y, bool stereo, Span<float> lowbandOut)
    {
        Span<float> v = x;
        int c = 0;
        do
        {
            bool sign = false;
            if (_ctx.RemainingBits >= 1 << BitRes)
            {
                sign = v[0] < 0;
                _enc.EncodeBits(sign ? 1u : 0u, 1);
                _ctx.RemainingBits -= 1 << BitRes;
            }

            if (_ctx.Resynth)
            {
                v[0] = sign ? -1f : 1f;
            }

            v = y;
        }
        while (++c < (stereo ? 2 : 1));

        if (!lowbandOut.IsEmpty)
        {
            lowbandOut[0] = x[0];
        }

        return 1;
    }

    // quant_partition, encoder side.
    private uint QuantPartition(Span<float> x, int n, int b, int bBlocks, Span<float> lowband, int lm, float gain, int fill)
    {
        int b0 = bBlocks;
        uint cm = 0;
        int i = _ctx.Band;

        // If we need 1.5 more bit than we can produce, split the band in two.
        if (lm != -1 && n > 2)
        {
            ReadOnlySpan<byte> cache = CeltTables.Cache(i, lm);
            if (b > cache[cache[0]] + 12)
            {
                n >>= 1;
                Span<float> y = x.Slice(n, n);
                lm -= 1;
                if (bBlocks == 1)
                {
                    fill = (fill & 1) | (fill << 1);
                }

                bBlocks = (bBlocks + 1) >> 1;
                ComputeTheta(x[..n], y, out _, out int imid, out int iside, out int delta, out int itheta, out int qalloc, n, ref b, bBlocks, b0, lm, false,
                    ref fill);
                float mid = (1f / 32768) * imid;
                float side = (1f / 32768) * iside;

                // Give more bits to low-energy MDCTs than they would otherwise deserve.
                if (b0 > 1 && (itheta & 0x3fff) != 0)
                {
                    if (itheta > 8192)
                    {
                        // Rough approximation for pre-echo masking.
                        delta -= delta >> (4 - lm);
                    }
                    else
                    {
                        // Corresponds to a forward-masking slope of 1.5 dB per 10 ms.
                        delta = Math.Min(0, delta + (n << BitRes >> (5 - lm)));
                    }
                }

                int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
                int sbits = b - mbits;
                _ctx.RemainingBits -= qalloc;
                Span<float> nextLowband2 = lowband.IsEmpty ? default : lowband.Slice(n, n);
                Span<float> lowband1 = lowband.IsEmpty ? default : lowband[..n];
                int rebalance = _ctx.RemainingBits;
                if (mbits >= sbits)
                {
                    cm = QuantPartition(x[..n], n, mbits, bBlocks, lowband1, lm, gain * mid, fill);
                    rebalance = mbits - (rebalance - _ctx.RemainingBits);
                    if (rebalance > 3 << BitRes && itheta != 0)
                    {
                        sbits += rebalance - (3 << BitRes);
                    }

                    cm |= QuantPartition(y, n, sbits, bBlocks, nextLowband2, lm, gain * side, fill >> bBlocks) << (b0 >> 1);
                }
                else
                {
                    cm = QuantPartition(y, n, sbits, bBlocks, nextLowband2, lm, gain * side, fill >> bBlocks) << (b0 >> 1);
                    rebalance = sbits - (rebalance - _ctx.RemainingBits);
                    if (rebalance > 3 << BitRes && itheta != 16384)
                    {
                        mbits += rebalance - (3 << BitRes);
                    }

                    cm |= QuantPartition(x[..n], n, mbits, bBlocks, lowband1, lm, gain * mid, fill);
                }

                return cm;
            }
        }

        // This is the basic no-split case.
        int q = CeltTables.Bits2Pulses(i, lm, b);
        int currBits = CeltTables.Pulses2Bits(i, lm, q);
        _ctx.RemainingBits -= currBits;

        // Ensures we can never bust the budget.
        while (_ctx.RemainingBits < 0 && q > 0)
        {
            _ctx.RemainingBits += currBits;
            q--;
            currBits = CeltTables.Pulses2Bits(i, lm, q);
            _ctx.RemainingBits -= currBits;
        }

        if (q != 0)
        {
            int k = CeltTables.GetPulses(q);
            // Finally do the actual quantization.
            cm = CeltVq.AlgQuant(x, n, k, _ctx.Spread, bBlocks, _enc, gain, _ctx.Resynth);
        }
        else if (_ctx.Resynth)
        {
            // If there's no pulse, fill the band anyway.
            uint cmMask = (uint)((1UL << bBlocks) - 1);
            fill &= (int)cmMask;
            if (fill == 0)
            {
                x[..n].Clear();
            }
            else
            {
                if (lowband.IsEmpty)
                {
                    // Noise.
                    for (int j = 0; j < n; j++)
                    {
                        _ctx.Seed = CeltMath.LcgRand(_ctx.Seed);
                        x[j] = (int)_ctx.Seed >> 20;
                    }

                    cm = cmMask;
                }
                else
                {
                    // Folded spectrum.
                    for (int j = 0; j < n; j++)
                    {
                        _ctx.Seed = CeltMath.LcgRand(_ctx.Seed);
                        // About 48 dB below the "normal" folding level.
                        float tmp = (_ctx.Seed & 0x8000) != 0 ? 1.0f / 256 : -1.0f / 256;
                        x[j] = lowband[j] + tmp;
                    }

                    cm = (uint)fill;
                }

                CeltVq.RenormaliseVector(x, n, gain);
            }
        }

        return cm;
    }

    // quant_band, encoder side (mono, or one channel of dual stereo, or the mid and side of a stereo split).
    private uint QuantBand(Span<float> x, int n, int b, int bBlocks, Span<float> lowband, int lm, Span<float> lowbandOut, float gain, Span<float> lowbandScratch,
        int fill)
    {
        int n0 = n;
        int b0 = bBlocks;
        int timeDivide = 0;
        int recombine = 0;
        bool longBlocks = b0 == 1;
        int tfChange = _ctx.TfChange;
        int nB = (int)((uint)n / (uint)bBlocks);

        // Special case for one sample.
        if (n == 1)
        {
            return QuantBandN1(x, default, false, lowbandOut);
        }

        if (tfChange > 0)
        {
            recombine = tfChange;
        }

        // Band recombining to increase frequency resolution.
        if (!lowbandScratch.IsEmpty && !lowband.IsEmpty && (recombine != 0 || ((nB & 1) == 0 && tfChange < 0) || b0 > 1))
        {
            lowband[..n].CopyTo(lowbandScratch);
            lowband = lowbandScratch[..n];
        }

        for (int k = 0; k < recombine; k++)
        {
            CeltBands.Haar1(x, n >> k, 1 << k);
            if (!lowband.IsEmpty)
            {
                CeltBands.Haar1(lowband, n >> k, 1 << k);
            }

            fill = CeltBands.BitInterleaveTable[fill & 0xF] | (CeltBands.BitInterleaveTable[fill >> 4] << 2);
        }

        bBlocks >>= recombine;
        nB <<= recombine;

        // Increasing the time resolution.
        while ((nB & 1) == 0 && tfChange < 0)
        {
            CeltBands.Haar1(x, nB, bBlocks);
            if (!lowband.IsEmpty)
            {
                CeltBands.Haar1(lowband, nB, bBlocks);
            }

            fill |= fill << bBlocks;
            bBlocks <<= 1;
            nB >>= 1;
            timeDivide++;
            tfChange++;
        }

        b0 = bBlocks;
        int nB0 = nB;

        // Reorganize the samples in time order instead of frequency order.
        if (b0 > 1)
        {
            CeltBands.DeinterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks, _scratch);
            if (!lowband.IsEmpty)
            {
                CeltBands.DeinterleaveHadamard(lowband, nB >> recombine, b0 << recombine, longBlocks, _scratch);
            }
        }

        uint cm = QuantPartition(x, n, b, bBlocks, lowband, lm, gain, fill);

        // This code is used by the resynthesis-enabled encoder.
        if (_ctx.Resynth)
        {
            // Undo the sample reorganization going from time order to frequency order.
            if (b0 > 1)
            {
                CeltBands.InterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks, _scratch);
            }

            // Undo time-freq changes that we did earlier.
            nB = nB0;
            bBlocks = b0;
            for (int k = 0; k < timeDivide; k++)
            {
                bBlocks >>= 1;
                nB <<= 1;
                cm |= cm >> bBlocks;
                CeltBands.Haar1(x, nB, bBlocks);
            }

            for (int k = 0; k < recombine; k++)
            {
                cm = CeltBands.BitDeinterleaveTable[(int)cm];
                CeltBands.Haar1(x, n0 >> k, 1 << k);
            }

            bBlocks <<= recombine;

            // Scale output for later folding.
            if (!lowbandOut.IsEmpty)
            {
                float nn = CeltMath.Sqrt(n0);
                for (int j = 0; j < n0; j++)
                {
                    lowbandOut[j] = nn * x[j];
                }
            }

            cm &= (uint)((1 << bBlocks) - 1);
        }

        return cm;
    }

    // quant_band_stereo, encoder side.
    private uint QuantBandStereo(Span<float> x, Span<float> y, int n, int b, int bBlocks, Span<float> lowband, int lm, Span<float> lowbandOut,
        Span<float> lowbandScratch, int fill)
    {
        uint cm;

        // Special case for one sample.
        if (n == 1)
        {
            return QuantBandN1(x, y, true, lowbandOut);
        }

        int origFill = fill;
        ComputeTheta(x, y, out bool inv, out int imid, out int iside, out int delta, out int itheta, out int qalloc, n, ref b, bBlocks, bBlocks, lm, true,
            ref fill);
        float mid = (1f / 32768) * imid;
        float side = (1f / 32768) * iside;
        int mbits;
        int sbits;

        // A special case for N=2 that only works for stereo and takes advantage of the fact that mid and side are orthogonal to
        // encode the side with just one bit.
        if (n == 2)
        {
            int sign = 0;
            mbits = b;
            sbits = 0;
            // Only need one bit for the side.
            if (itheta != 0 && itheta != 16384)
            {
                sbits = 1 << BitRes;
            }

            mbits -= sbits;
            bool c = itheta > 8192;
            _ctx.RemainingBits -= qalloc + sbits;

            Span<float> x2 = c ? y : x;
            Span<float> y2 = c ? x : y;
            if (sbits != 0)
            {
                // Here we only need to encode a sign for the side.
                sign = (x2[0] * y2[1]) - (x2[1] * y2[0]) < 0 ? 1 : 0;
                _enc.EncodeBits((uint)sign, 1);
            }

            sign = 1 - (2 * sign);
            // We use origFill here because we want to fold the side, but if itheta==16384, we'll have cleared the low bits of fill.
            cm = QuantBand(x2, n, mbits, bBlocks, lowband, lm, lowbandOut, 1f, lowbandScratch, origFill);
            // We don't split N=2 bands, so cm is either 1 or 0 (for a fold-collapse), and there's no need to worry about mixing with
            // the other channel.
            y2[0] = -sign * x2[1];
            y2[1] = sign * x2[0];
            if (_ctx.Resynth)
            {
                x[0] = mid * x[0];
                x[1] = mid * x[1];
                y[0] = side * y[0];
                y[1] = side * y[1];
                float tmp = x[0];
                x[0] = tmp - y[0];
                y[0] = tmp + y[0];
                tmp = x[1];
                x[1] = tmp - y[1];
                y[1] = tmp + y[1];
            }
        }
        else
        {
            // "Normal" split code.
            mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            sbits = b - mbits;
            _ctx.RemainingBits -= qalloc;

            int rebalance = _ctx.RemainingBits;
            if (mbits >= sbits)
            {
                // In stereo mode, we do not apply a scaling to the mid because we need the normalized mid for folding later.
                cm = QuantBand(x, n, mbits, bBlocks, lowband, lm, lowbandOut, 1f, lowbandScratch, fill);
                rebalance = mbits - (rebalance - _ctx.RemainingBits);
                if (rebalance > 3 << BitRes && itheta != 0)
                {
                    sbits += rebalance - (3 << BitRes);
                }

                // For a stereo split, the high bits of fill are always zero, so no folding will be done to the side.
                cm |= QuantBand(y, n, sbits, bBlocks, default, lm, default, side, default, fill >> bBlocks);
            }
            else
            {
                // For a stereo split, the high bits of fill are always zero, so no folding will be done to the side.
                cm = QuantBand(y, n, sbits, bBlocks, default, lm, default, side, default, fill >> bBlocks);
                rebalance = sbits - (rebalance - _ctx.RemainingBits);
                if (rebalance > 3 << BitRes && itheta != 16384)
                {
                    mbits += rebalance - (3 << BitRes);
                }

                // In stereo mode, we do not apply a scaling to the mid because we need the normalized mid for folding later.
                cm |= QuantBand(x, n, mbits, bBlocks, lowband, lm, lowbandOut, 1f, lowbandScratch, fill);
            }
        }

        if (_ctx.Resynth)
        {
            if (n != 2)
            {
                CeltBands.StereoMerge(x, y, mid, n);
            }

            if (inv)
            {
                for (int j = 0; j < n; j++)
                {
                    y[j] = -y[j];
                }
            }
        }

        return cm;
    }
}
