// Translated from libopus 1.5.2 celt/bands.c (decoder side: quant_all_bands and what it calls, anti_collapse, denormalise_bands).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// The decoding of CELT's normalized band shapes: the recursive band splitting with its theta angles, PVQ, folding, the
/// time-frequency changes, intensity and dual stereo. One instance per decoder keeps the folding buffer and the band context.
/// </summary>
internal sealed class CeltBands
{
    private const int BitRes = CeltTables.BitRes;

    // Indexing table for converting from natural Hadamard to ordery Hadamard, for N = 2, 4, 8 and 16.
    private static ReadOnlySpan<byte> OrderyTable =>
    [
        1, 0,
        3, 0, 2, 1,
        7, 0, 4, 3, 6, 1, 5, 2,
        15, 0, 8, 7, 12, 3, 11, 4, 14, 1, 9, 6, 13, 2, 10, 5,
    ];

    internal static ReadOnlySpan<byte> BitInterleaveTable => [0, 1, 1, 1, 2, 3, 3, 3, 2, 3, 3, 3, 2, 3, 3, 3];

    internal static ReadOnlySpan<byte> BitDeinterleaveTable =>
    [
        0x00, 0x03, 0x0C, 0x0F, 0x30, 0x33, 0x3C, 0x3F, 0xC0, 0xC3, 0xCC, 0xCF, 0xF0, 0xF3, 0xFC, 0xFF,
    ];

    private static ReadOnlySpan<short> Exp2Table8 => [16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048];

    // The folding buffer (norm, then norm2), with slack so that a fold window that runs past norm2 stays in bounds.
    private readonly float[] _norm = new float[(2 * 8 * 100) + 200];
    private readonly float[] _scratch = new float[8 * 100];

    // struct band_ctx.
    private OpusRangeDecoder _dec = null!;
    private int _band;
    private int _intensity;
    private int _spread;
    private int _tfChange;
    private int _remainingBits;
    private uint _seed;
    private bool _disableInv;
    private bool _avoidSplitNoise;

    /// <summary>
    /// quant_all_bands for the decoder: decodes the shapes of bands [start, end) into <paramref name="x"/> (and
    /// <paramref name="y"/> for stereo, empty for mono) and their collapse masks; <paramref name="seed"/> is the folding noise seed.
    /// </summary>
    public void QuantAllBands(int start, int end, Span<float> x, Span<float> y, bool stereo, Span<byte> collapseMasks, ReadOnlySpan<int> pulses,
        bool shortBlocks, int spread, bool dualStereo, int intensity, ReadOnlySpan<int> tfRes, int totalBits, int balance, OpusRangeDecoder dec, int lm,
        int codedBands, ref uint seed, bool disableInv)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << lm;
        int b = shortBlocks ? m : 1;
        int c = stereo ? 2 : 1;
        int normOffset = m * eBands[start];
        // No need to allocate norm for the last band because we don't need an output in that band.
        Span<float> normBuf = _norm;
        int norm2 = (m * eBands[CeltTables.NbEBands - 1]) - normOffset;
        normBuf[..(c * norm2)].Clear();

        // For decoding, the last band serves as scratch space: its data isn't needed until it is decoded.
        int scratchOffset = m * eBands[CeltTables.EffEBands - 1];
        bool useScratch;
        int lowbandOffset = 0;
        bool updateLowband = true;
        _dec = dec;
        _intensity = intensity;
        _seed = seed;
        _spread = spread;
        _disableInv = disableInv;
        // Avoid injecting noise in the first band on transients.
        _avoidSplitNoise = b > 1;
        for (int i = start; i < end; i++)
        {
            _band = i;
            bool last = i == end - 1;
            int bandStart = m * eBands[i];
            int n = (m * eBands[i + 1]) - (m * eBands[i]);
            Span<float> xb = x.Slice(bandStart, n);
            Span<float> yb = stereo ? y.Slice(bandStart, n) : default;
            int tell = (int)dec.TellFrac();

            // Compute how many bits we want to allocate to this band.
            if (i != start)
            {
                balance -= tell;
            }

            int remainingBits = totalBits - tell - 1;
            _remainingBits = remainingBits;
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

            if (((m * eBands[i]) - n >= m * eBands[start] || i == start + 1) && (updateLowband || lowbandOffset == 0))
            {
                lowbandOffset = i;
            }

            if (i == start + 1)
            {
                SpecialHybridFolding(normBuf, norm2, start, m, dualStereo);
            }

            int tfChange = tfRes[i];
            _tfChange = tfChange;
            useScratch = !last;

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
                for (int j = 0; j < (m * eBands[i]) - normOffset; j++)
                {
                    normBuf[j] = .5f * (normBuf[j] + normBuf[norm2 + j]);
                }
            }

            Span<float> scratch = useScratch ? x.Slice(scratchOffset, n) : default;
            Span<float> lowband = effectiveLowband != -1 ? normBuf.Slice(effectiveLowband, n) : default;
            Span<float> lowbandOut = last ? default : normBuf.Slice((m * eBands[i]) - normOffset, n);
            if (dualStereo)
            {
                Span<float> lowband2 = effectiveLowband != -1 ? normBuf.Slice(norm2 + effectiveLowband, n) : default;
                Span<float> lowbandOut2 = last ? default : normBuf.Slice(norm2 + (m * eBands[i]) - normOffset, n);
                xcm = QuantBand(xb, n, bits / 2, b, lowband, lm, lowbandOut, 1f, scratch, (int)xcm);
                ycm = QuantBand(yb, n, bits / 2, b, lowband2, lm, lowbandOut2, 1f, scratch, (int)ycm);
            }
            else
            {
                if (stereo)
                {
                    xcm = QuantBandStereo(xb, yb, n, bits, b, lowband, lm, lowbandOut, scratch, (int)(xcm | ycm));
                }
                else
                {
                    xcm = QuantBand(xb, n, bits, b, lowband, lm, lowbandOut, 1f, scratch, (int)(xcm | ycm));
                }

                ycm = xcm;
            }

            collapseMasks[i * c] = (byte)xcm;
            collapseMasks[(i * c) + c - 1] = (byte)ycm;
            balance += pulses[i] + tell;

            // Update the folding position only as long as we have 1 bit/sample depth.
            updateLowband = bits > n << BitRes;
            // We only need to avoid noise on a split for the first band. After that, we have folding.
            _avoidSplitNoise = false;
        }

        seed = _seed;
    }

    /// <summary>anti_collapse: fills the short blocks that got no pulses with noise at the energy of the previous frames.</summary>
    public static void AntiCollapse(Span<float> x, ReadOnlySpan<byte> collapseMasks, int lm, int c, int size, int start, int end, ReadOnlySpan<float> logE,
        ReadOnlySpan<float> prev1LogE, ReadOnlySpan<float> prev2LogE, ReadOnlySpan<int> pulses, uint seed)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        const int Nb = CeltTables.NbEBands;
        for (int i = start; i < end; i++)
        {
            int n0 = eBands[i + 1] - eBands[i];
            // Depth in 1/8 bits.
            int depth = (int)((uint)(1 + pulses[i]) / (uint)n0) >> lm;
            float thresh = .5f * CeltMath.Exp2(-.125f * depth);
            float sqrt1 = CeltMath.RSqrt(n0 << lm);
            int ch = 0;
            do
            {
                float prev1 = prev1LogE[(ch * Nb) + i];
                float prev2 = prev2LogE[(ch * Nb) + i];
                if (c == 1)
                {
                    prev1 = Max(prev1, prev1LogE[Nb + i]);
                    prev2 = Max(prev2, prev2LogE[Nb + i]);
                }

                float ediff = logE[(ch * Nb) + i] - Min(prev1, prev2);
                ediff = Max(0, ediff);
                // r needs to be multiplied by 2 or 2*sqrt(2) depending on LM because short blocks don't have the same energy as long.
                float r = 2f * CeltMath.Exp2(-ediff);
                if (lm == 3)
                {
                    r *= 1.41421356f;
                }

                r = Min(thresh, r);
                r *= sqrt1;
                Span<float> xb = x[((ch * size) + (eBands[i] << lm))..];
                bool renormalize = false;
                for (int k = 0; k < 1 << lm; k++)
                {
                    // Detect collapse.
                    if ((collapseMasks[(i * c) + ch] & (1 << k)) == 0)
                    {
                        // Fill with noise.
                        for (int j = 0; j < n0; j++)
                        {
                            seed = CeltMath.LcgRand(seed);
                            xb[(j << lm) + k] = (seed & 0x8000) != 0 ? r : -r;
                        }

                        renormalize = true;
                    }
                }

                // We just added some energy, so we need to renormalise.
                if (renormalize)
                {
                    CeltVq.RenormaliseVector(xb, n0 << lm, 1f);
                }
            }
            while (++ch < c);
        }
    }

    /// <summary>denormalise_bands: scales the unit-norm bands by their energies into MDCT coefficients.</summary>
    public static void DenormaliseBands(ReadOnlySpan<float> x, Span<float> freq, ReadOnlySpan<float> bandLogE, int start, int end, int m, int downsample, bool silence)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<float> eMeans = CeltTables.EMeans;
        int n = m * CeltTables.ShortMdctSize;
        int bound = m * eBands[end];
        if (downsample != 1)
        {
            bound = Math.Min(bound, n / downsample);
        }

        if (silence)
        {
            bound = 0;
            start = end = 0;
        }

        int f = 0;
        int xi = m * eBands[start];
        for (int i = 0; i < m * eBands[start]; i++)
        {
            freq[f++] = 0;
        }

        for (int i = start; i < end; i++)
        {
            int j = m * eBands[i];
            int bandEnd = m * eBands[i + 1];
            float lg = bandLogE[i] + eMeans[i];
            float g = CeltMath.Exp2(Min(32f, lg));
            do
            {
                freq[f++] = x[xi++] * g;
            }
            while (++j < bandEnd);
        }

        freq[bound..n].Clear();
    }

    /// <summary>haar1.</summary>
    public static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < n0; j++)
            {
                int a = (stride * 2 * j) + i;
                int b = (stride * ((2 * j) + 1)) + i;
                float tmp1 = .70710678f * x[a];
                float tmp2 = .70710678f * x[b];
                x[a] = tmp1 + tmp2;
                x[b] = tmp1 - tmp2;
            }
        }
    }

    private static float Min(float a, float b) => a < b ? a : b;

    private static float Max(float a, float b) => a > b ? a : b;

    internal static void SpecialHybridFolding(Span<float> norm, int norm2, int start, int m, bool dualStereo)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int n1 = m * (eBands[start + 1] - eBands[start]);
        int n2 = m * (eBands[start + 2] - eBands[start + 1]);
        // Duplicate enough of the first band folding data to be able to fold the second band. Copies no data for CELT-only mode.
        if (n2 > n1)
        {
            norm.Slice((2 * n1) - n2, n2 - n1).CopyTo(norm[n1..]);
            if (dualStereo)
            {
                norm.Slice(norm2 + (2 * n1) - n2, n2 - n1).CopyTo(norm[(norm2 + n1)..]);
            }
        }
    }

    internal static void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard, Span<float> tmp)
    {
        int n = n0 * stride;
        if (hadamard)
        {
            ReadOnlySpan<byte> ordery = OrderyTable[(stride - 2)..];
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(ordery[i] * n0) + j] = x[(j * stride) + i];
                }
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(i * n0) + j] = x[(j * stride) + i];
                }
            }
        }

        tmp[..n].CopyTo(x);
    }

    internal static void InterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard, Span<float> tmp)
    {
        int n = n0 * stride;
        if (hadamard)
        {
            ReadOnlySpan<byte> ordery = OrderyTable[(stride - 2)..];
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(j * stride) + i] = x[(ordery[i] * n0) + j];
                }
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(j * stride) + i] = x[(i * n0) + j];
                }
            }
        }

        tmp[..n].CopyTo(x);
    }

    internal static int ComputeQn(int n, int b, int offset, int pulseCap, bool stereo)
    {
        int n2 = (2 * n) - 1;
        if (stereo && n == 2)
        {
            n2--;
        }

        // The upper limit ensures that in a stereo split with itheta==16384, we'll always have enough bits left over to code at
        // least one pulse in the side; otherwise it would collapse, since it doesn't get folded.
        int qb = (b + (n2 * offset)) / n2;
        qb = Math.Min(b - pulseCap - (4 << BitRes), qb);
        qb = Math.Min(8 << BitRes, qb);
        int qn;
        if (qb < (1 << BitRes >> 1))
        {
            qn = 1;
        }
        else
        {
            qn = Exp2Table8[qb & 0x7] >> (14 - (qb >> BitRes));
            qn = (qn + 1) >> 1 << 1;
        }

        return qn;
    }

    internal static void StereoMerge(Span<float> x, Span<float> y, float mid, int n)
    {
        // Compute the norm of X+Y and X-Y as |X|^2 + |Y|^2 +/- sum(xy).
        CeltMath.DualInnerProduct(y, x, y, n, out float xp, out float side);
        // Compensating for the mid normalization.
        xp = mid * xp;
        float mid2 = mid;
        float el = (mid2 * mid2) + side - (2 * xp);
        float er = (mid2 * mid2) + side + (2 * xp);
        if (er < 6e-4f || el < 6e-4f)
        {
            x[..n].CopyTo(y);
            return;
        }

        float lgain = CeltMath.RSqrt(el);
        float rgain = CeltMath.RSqrt(er);
        for (int j = 0; j < n; j++)
        {
            // Apply mid scaling (side is already scaled).
            float l = mid * x[j];
            float r = y[j];
            x[j] = lgain * (l - r);
            y[j] = rgain * (l + r);
        }
    }

    // compute_theta, decoder side.
    private void ComputeTheta(out bool inv, out int imid, out int iside, out int delta, out int itheta, out int qalloc, int n, ref int b, int bBlocks, int b0,
        int lm, bool stereo, ref int fill)
    {
        OpusRangeDecoder dec = _dec;
        int i = _band;
        inv = false;
        itheta = 0;

        // Decide on the resolution to give to the split parameter theta.
        int pulseCap = CeltTables.LogN400[i] + (lm * (1 << BitRes));
        int offset = (pulseCap >> 1) - (stereo && n == 2 ? CeltTables.QThetaOffsetTwoPhase : CeltTables.QThetaOffset);
        int qn = ComputeQn(n, b, offset, pulseCap, stereo);
        if (stereo && i >= _intensity)
        {
            qn = 1;
        }

        int tell = (int)dec.TellFrac();
        if (qn != 1)
        {
            // Entropy coding of the angle: uniform pdf for the time split, a step for stereo, triangular for the rest.
            if (stereo && n > 2)
            {
                const int P0 = 3;
                int x0 = qn / 2;
                int ft = (P0 * (x0 + 1)) + x0;
                int fs = (int)dec.Decode((uint)ft);
                int x = fs < (x0 + 1) * P0 ? fs / P0 : x0 + 1 + (fs - ((x0 + 1) * P0));
                dec.Update((uint)(x <= x0 ? P0 * x : (x - 1 - x0) + ((x0 + 1) * P0)), (uint)(x <= x0 ? P0 * (x + 1) : (x - x0) + ((x0 + 1) * P0)), (uint)ft);
                itheta = x;
            }
            else if (b0 > 1 || stereo)
            {
                // Uniform pdf.
                itheta = (int)dec.DecodeUint((uint)(qn + 1));
            }
            else
            {
                // Triangular pdf.
                int fs;
                int fl;
                int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                int fm = (int)dec.Decode((uint)ft);
                if (fm < ((qn >> 1) * ((qn >> 1) + 1) >> 1))
                {
                    itheta = (int)(CeltMath.Isqrt32((8 * (uint)fm) + 1) - 1) >> 1;
                    fs = itheta + 1;
                    fl = itheta * (itheta + 1) >> 1;
                }
                else
                {
                    itheta = ((2 * (qn + 1)) - (int)CeltMath.Isqrt32((8 * (uint)(ft - fm - 1)) + 1)) >> 1;
                    fs = qn + 1 - itheta;
                    fl = ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                }

                dec.Update((uint)fl, (uint)(fl + fs), (uint)ft);
            }

            itheta = (int)((uint)(itheta * 16384) / (uint)qn);
        }
        else if (stereo)
        {
            inv = b > 2 << BitRes && _remainingBits > 2 << BitRes && dec.DecodeBitLogp(2);
            // inv flag override to avoid problems with downmixing.
            if (_disableInv)
            {
                inv = false;
            }

            itheta = 0;
        }

        qalloc = (int)dec.TellFrac() - tell;
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
            imid = CeltMath.BitexactCos(itheta);
            iside = CeltMath.BitexactCos(16384 - itheta);
            // This is the mid vs side allocation that minimizes squared error in that band.
            delta = CeltMath.FracMul16((n - 1) << 7, CeltMath.BitexactLog2Tan(iside, imid));
        }
    }

    // quant_band_n1, decoder side.
    private uint QuantBandN1(Span<float> x, Span<float> y, bool stereo, Span<float> lowbandOut)
    {
        Span<float> v = x;
        int c = 0;
        do
        {
            bool sign = false;
            if (_remainingBits >= 1 << BitRes)
            {
                sign = _dec.DecodeBits(1) != 0;
                _remainingBits -= 1 << BitRes;
            }

            v[0] = sign ? -1f : 1f;
            v = y;
        }
        while (++c < (stereo ? 2 : 1));

        if (!lowbandOut.IsEmpty)
        {
            lowbandOut[0] = x[0];
        }

        return 1;
    }

    // quant_partition, decoder side.
    private uint QuantPartition(Span<float> x, int n, int b, int bBlocks, Span<float> lowband, int lm, float gain, int fill)
    {
        int b0 = bBlocks;
        uint cm = 0;
        int i = _band;

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
                ComputeTheta(out _, out int imid, out int iside, out int delta, out int itheta, out int qalloc, n, ref b, bBlocks, b0, lm, false, ref fill);
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
                _remainingBits -= qalloc;
                Span<float> nextLowband2 = lowband.IsEmpty ? default : lowband.Slice(n, n);
                Span<float> lowband1 = lowband.IsEmpty ? default : lowband[..n];
                int rebalance = _remainingBits;
                if (mbits >= sbits)
                {
                    cm = QuantPartition(x[..n], n, mbits, bBlocks, lowband1, lm, gain * mid, fill);
                    rebalance = mbits - (rebalance - _remainingBits);
                    if (rebalance > 3 << BitRes && itheta != 0)
                    {
                        sbits += rebalance - (3 << BitRes);
                    }

                    cm |= QuantPartition(y, n, sbits, bBlocks, nextLowband2, lm, gain * side, fill >> bBlocks) << (b0 >> 1);
                }
                else
                {
                    cm = QuantPartition(y, n, sbits, bBlocks, nextLowband2, lm, gain * side, fill >> bBlocks) << (b0 >> 1);
                    rebalance = sbits - (rebalance - _remainingBits);
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
        _remainingBits -= currBits;

        // Ensures we can never bust the budget.
        while (_remainingBits < 0 && q > 0)
        {
            _remainingBits += currBits;
            q--;
            currBits = CeltTables.Pulses2Bits(i, lm, q);
            _remainingBits -= currBits;
        }

        if (q != 0)
        {
            int k = CeltTables.GetPulses(q);
            cm = CeltVq.AlgUnquant(x, n, k, _spread, bBlocks, _dec, gain);
        }
        else
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
                        _seed = CeltMath.LcgRand(_seed);
                        x[j] = (int)_seed >> 20;
                    }

                    cm = cmMask;
                }
                else
                {
                    // Folded spectrum.
                    for (int j = 0; j < n; j++)
                    {
                        _seed = CeltMath.LcgRand(_seed);
                        // About 48 dB below the "normal" folding level.
                        float tmp = (_seed & 0x8000) != 0 ? 1.0f / 256 : -1.0f / 256;
                        x[j] = lowband[j] + tmp;
                    }

                    cm = (uint)fill;
                }

                CeltVq.RenormaliseVector(x, n, gain);
            }
        }

        return cm;
    }

    // quant_band, decoder side (mono, or one channel of dual stereo, or the mid and side of a stereo split).
    private uint QuantBand(Span<float> x, int n, int b, int bBlocks, Span<float> lowband, int lm, Span<float> lowbandOut, float gain, Span<float> lowbandScratch,
        int fill)
    {
        int n0 = n;
        int nB;
        int b0 = bBlocks;
        int timeDivide = 0;
        int recombine = 0;
        bool longBlocks = b0 == 1;
        uint cm;
        int tfChange = _tfChange;

        nB = (int)((uint)n / (uint)bBlocks);

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
            if (!lowband.IsEmpty)
            {
                Haar1(lowband, n >> k, 1 << k);
            }

            fill = BitInterleaveTable[fill & 0xF] | (BitInterleaveTable[fill >> 4] << 2);
        }

        bBlocks >>= recombine;
        nB <<= recombine;

        // Increasing the time resolution.
        while ((nB & 1) == 0 && tfChange < 0)
        {
            if (!lowband.IsEmpty)
            {
                Haar1(lowband, nB, bBlocks);
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
        if (b0 > 1 && !lowband.IsEmpty)
        {
            DeinterleaveHadamard(lowband, nB >> recombine, b0 << recombine, longBlocks, _scratch);
        }

        cm = QuantPartition(x, n, b, bBlocks, lowband, lm, gain, fill);

        // Undo the sample reorganization going from time order to frequency order.
        if (b0 > 1)
        {
            InterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks, _scratch);
        }

        // Undo time-freq changes that we did earlier.
        nB = nB0;
        bBlocks = b0;
        for (int k = 0; k < timeDivide; k++)
        {
            bBlocks >>= 1;
            nB <<= 1;
            cm |= cm >> bBlocks;
            Haar1(x, nB, bBlocks);
        }

        for (int k = 0; k < recombine; k++)
        {
            cm = BitDeinterleaveTable[(int)cm];
            Haar1(x, n0 >> k, 1 << k);
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
        return cm;
    }

    // quant_band_stereo, decoder side.
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
        ComputeTheta(out bool inv, out int imid, out int iside, out int delta, out int itheta, out int qalloc, n, ref b, bBlocks, bBlocks, lm, true, ref fill);
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
            _remainingBits -= qalloc + sbits;

            Span<float> x2 = c ? y : x;
            Span<float> y2 = c ? x : y;
            if (sbits != 0)
            {
                sign = (int)_dec.DecodeBits(1);
            }

            sign = 1 - (2 * sign);
            // We use origFill here because we want to fold the side, but if itheta==16384, we'll have cleared the low bits of fill.
            cm = QuantBand(x2, n, mbits, bBlocks, lowband, lm, lowbandOut, 1f, lowbandScratch, origFill);
            // We don't split N=2 bands, so cm is either 1 or 0 (for a fold-collapse), and there's no need to worry about mixing with
            // the other channel.
            y2[0] = -sign * x2[1];
            y2[1] = sign * x2[0];
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
        else
        {
            // "Normal" split code.
            mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            sbits = b - mbits;
            _remainingBits -= qalloc;

            int rebalance = _remainingBits;
            if (mbits >= sbits)
            {
                // In stereo mode, we do not apply a scaling to the mid because we need the normalized mid for folding later.
                cm = QuantBand(x, n, mbits, bBlocks, lowband, lm, lowbandOut, 1f, lowbandScratch, fill);
                rebalance = mbits - (rebalance - _remainingBits);
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
                rebalance = sbits - (rebalance - _remainingBits);
                if (rebalance > 3 << BitRes && itheta != 16384)
                {
                    mbits += rebalance - (3 << BitRes);
                }

                // In stereo mode, we do not apply a scaling to the mid because we need the normalized mid for folding later.
                cm |= QuantBand(x, n, mbits, bBlocks, lowband, lm, lowbandOut, 1f, lowbandScratch, fill);
            }
        }

        if (n != 2)
        {
            StereoMerge(x, y, mid, n);
        }

        if (inv)
        {
            for (int j = 0; j < n; j++)
            {
                y[j] = -y[j];
            }
        }

        return cm;
    }
}
