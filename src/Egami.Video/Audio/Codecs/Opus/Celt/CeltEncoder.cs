// Translated from libopus 1.5.2 celt/celt_encoder.c and celt/celt.c (float build, static 48 kHz mode, no custom modes).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// libopus's CELT encoder (celt_encode_with_ec) for the static 48 kHz, 960-sample mode: pre-emphasis, the pitch pre-filter, transient
/// detection, the MDCTs, band energies, time-frequency resolution, dynamic allocation, spreading, VBR and the band shapes. Takes float
/// samples in [-1, 1] scale, as opus_encode_float does.
/// </summary>
internal sealed class CeltEncoder
{
    /// <summary>OPUS_BITRATE_MAX.</summary>
    public const int BitrateMax = -1;

    private const int Overlap = CeltTables.Overlap;
    private const int Nb = CeltTables.NbEBands;
    private const int BitRes = CeltTables.BitRes;
    private const int MaxPeriod = CeltTables.MaxPeriod;
    private const int MinPeriod = CeltTables.CombFilterMinPeriod;
    private const int OpusBadArg = -1;
    private const int OpusInternalError = -3;

    // Table of 6*64/x, trained on real data to minimize the average error.
    private static ReadOnlySpan<byte> InvTable =>
    [
        255, 255, 156, 110, 86, 70, 59, 51, 45, 40, 37, 33, 31, 28, 26, 25,
        23, 22, 21, 20, 19, 18, 17, 16, 16, 15, 15, 14, 13, 13, 12, 12,
        12, 12, 11, 11, 11, 10, 10, 10, 9, 9, 9, 9, 9, 9, 8, 8,
        8, 8, 8, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2,
    ];

    private static ReadOnlySpan<float> IntensityThresholds => [1, 2, 3, 4, 5, 6, 7, 8, 16, 24, 36, 44, 50, 56, 62, 67, 72, 79, 88, 106, 134];

    private static ReadOnlySpan<float> IntensityHysteresis => [1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 3, 3, 4, 5, 6, 8, 8];

    private readonly int _channels;
    private readonly int _upsample;
    private readonly OpusRangeEncoder _ownEncoder = new();
    private readonly CeltBandsEncoder _bands = new();

    // Scratch buffers for one frame.
    private readonly float[] _in;
    private readonly float[] _freq;
    private readonly float[] _x = new float[2 * 960];
    private readonly float[] _bandE = new float[2 * Nb];
    private readonly float[] _bandLogE = new float[2 * Nb];
    private readonly float[] _bandLogE2 = new float[2 * Nb];
    private readonly float[] _error = new float[2 * Nb];
    private readonly float[] _surroundDynalloc = new float[2 * Nb];
    private readonly float[] _pre;
    private readonly float[] _pitchBuf = new float[(MaxPeriod + 960) >> 1];
    private readonly float[] _tmp = new float[960 + Overlap];
    private readonly int[] _fineQuant = new int[Nb];
    private readonly int[] _pulses = new int[Nb];
    private readonly int[] _cap = new int[Nb];
    private readonly int[] _offsets = new int[Nb];
    private readonly int[] _importance = new int[Nb];
    private readonly int[] _spreadWeight = new int[Nb];
    private readonly int[] _finePriority = new int[Nb];
    private readonly int[] _tfRes = new int[Nb];
    private readonly byte[] _collapseMasks = new byte[2 * Nb];

    // Everything below is cleared on a reset.
    private uint _rng;
    private int _spreadDecision;
    private float _delayedIntra;
    private int _tonalAverage;
    private int _lastCodedBands;
    private int _hfAverage;
    private int _tapsetDecision;
    private int _prefilterPeriod;
    private float _prefilterGain;
    private int _prefilterTapset;
    private int _consecTransient;
    private OpusAnalysisInfo _analysis;
    private OpusSilkInfo _silkInfo;
    private readonly float[] _preemphMemE = new float[2];
    private int _vbrReservoir;
    private int _vbrDrift;
    private int _vbrOffset;
    private int _vbrCount;
    private float _overlapMax;
    private float _stereoSaving;
    private int _intensity;
    private float[]? _energyMask;
    private float _specAvg;
    private readonly float[] _inMem;
    private readonly float[] _prefilterMem;
    private readonly float[] _oldBandE;
    private readonly float[] _oldLogE;
    private readonly float[] _oldLogE2;
    private readonly float[] _energyError;

    /// <summary>celt_encoder_init: a CELT encoder for <paramref name="channels"/> channels at <paramref name="sampleRate"/> (8, 12, 16, 24 or 48 kHz).</summary>
    public CeltEncoder(int sampleRate, int channels)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        _upsample = sampleRate switch
        {
            48000 => 1,
            24000 => 2,
            16000 => 3,
            12000 => 4,
            8000 => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate)),
        };
        _channels = channels;
        _in = new float[channels * (960 + Overlap)];
        _freq = new float[channels * 960];
        _pre = new float[channels * (960 + MaxPeriod)];
        _inMem = new float[channels * Overlap];
        _prefilterMem = new float[channels * MaxPeriod];
        _oldBandE = new float[channels * Nb];
        _oldLogE = new float[channels * Nb];
        _oldLogE2 = new float[channels * Nb];
        _energyError = new float[channels * Nb];
        StreamChannels = channels;
        StartBand = 0;
        EndBand = CeltTables.EffEBands;
        Bitrate = BitrateMax;
        Complexity = 5;
        ConstrainedVbr = true;
        LsbDepth = 24;
        Reset();
    }

    /// <summary>Gets or sets the complexity, 0 to 10 (OPUS_SET_COMPLEXITY).</summary>
    public int Complexity { get; set; }

    /// <summary>Gets or sets the first coded band (CELT_SET_START_BAND): 17 in hybrid mode.</summary>
    public int StartBand { get; set; }

    /// <summary>Gets or sets the end band (CELT_SET_END_BAND), which sets the bandwidth.</summary>
    public int EndBand { get; set; }

    /// <summary>Gets or sets the coded channels (CELT_SET_CHANNELS): 1 codes a stereo input as mono.</summary>
    public int StreamChannels { get; set; }

    /// <summary>Gets or sets the bitrate in bits per second, or <see cref="BitrateMax"/>.</summary>
    public int Bitrate { get; set; }

    /// <summary>Gets or sets whether the rate is variable.</summary>
    public bool Vbr { get; set; }

    /// <summary>Gets or sets whether VBR is constrained.</summary>
    public bool ConstrainedVbr { get; set; }

    /// <summary>Gets or sets the expected packet loss in percent.</summary>
    public int LossRate { get; set; }

    /// <summary>Gets or sets the depth of the input in bits, 8 to 24.</summary>
    public int LsbDepth { get; set; }

    /// <summary>Gets or sets whether this is an LFE channel.</summary>
    public bool Lfe { get; set; }

    /// <summary>Gets or sets whether stereo phase inversion is disabled.</summary>
    public bool DisableInv { get; set; }

    /// <summary>Gets or sets whether the pitch pre-filter is disabled (CELT_SET_PREDICTION 0 or 1).</summary>
    public bool DisablePrefilter { get; set; }

    /// <summary>Gets or sets whether every frame is coded intra (CELT_SET_PREDICTION 0).</summary>
    public bool ForceIntra { get; set; }

    /// <summary>Gets the final range of the last frame.</summary>
    public uint FinalRange => _rng;

    /// <summary>Sets the analysis of the next frame (CELT_SET_ANALYSIS).</summary>
    public void SetAnalysis(in OpusAnalysisInfo info) => _analysis = info;

    /// <summary>Sets what the SILK layer found in the next hybrid frame (CELT_SET_SILK_INFO).</summary>
    public void SetSilkInfo(in OpusSilkInfo info) => _silkInfo = info;

    /// <summary>Sets the surround masking of the next frame (OPUS_SET_ENERGY_MASK), or null.</summary>
    public void SetEnergyMask(float[]? mask) => _energyMask = mask;

    /// <summary>OPUS_RESET_STATE.</summary>
    public void Reset()
    {
        _rng = 0;
        _lastCodedBands = 0;
        _prefilterPeriod = 0;
        _prefilterGain = 0;
        _prefilterTapset = 0;
        _consecTransient = 0;
        _analysis = default;
        _silkInfo = default;
        Array.Clear(_preemphMemE);
        _vbrReservoir = 0;
        _vbrDrift = 0;
        _vbrCount = 0;
        _overlapMax = 0;
        _stereoSaving = 0;
        _intensity = 0;
        _energyMask = null;
        _specAvg = 0;
        Array.Clear(_inMem);
        Array.Clear(_prefilterMem);
        Array.Clear(_oldBandE);
        Array.Clear(_energyError);
        Array.Fill(_oldLogE, -28f);
        Array.Fill(_oldLogE2, -28f);
        _vbrOffset = 0;
        _delayedIntra = 1;
        _spreadDecision = CeltTables.SpreadNormal;
        _tonalAverage = 256;
        _hfAverage = 0;
        _tapsetDecision = 0;
    }

    /// <summary>
    /// celt_encode_with_ec: encodes <paramref name="frameSize"/> samples per channel of interleaved <paramref name="pcm"/> into at most
    /// <paramref name="nbCompressedBytes"/> bytes. With <paramref name="enc"/>, continues that range coder (hybrid frames and the CELT
    /// layer of a SILK-capable packet); otherwise writes into <paramref name="compressed"/> from <paramref name="offset"/>. Returns the
    /// size or a negative error.
    /// </summary>
    public int Encode(ReadOnlySpan<float> pcm, int frameSize, byte[]? compressed, int offset, int nbCompressedBytes, OpusRangeEncoder? enc)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int cc = _channels;
        int c = StreamChannels;
        int start = StartBand;
        int end = EndBand;
        bool hybrid = start != 0;
        float tfEstimate = 0;
        if (nbCompressedBytes < 2 || pcm.IsEmpty)
        {
            return OpusBadArg;
        }

        frameSize *= _upsample;
        int lm;
        for (lm = 0; lm <= CeltTables.MaxLM; lm++)
        {
            if (CeltTables.ShortMdctSize << lm == frameSize)
            {
                break;
            }
        }

        if (lm > CeltTables.MaxLM)
        {
            return OpusBadArg;
        }

        int m = 1 << lm;
        int n = m * CeltTables.ShortMdctSize;
        int tell0Frac;
        int tell;
        int nbFilledBytes;
        if (enc == null)
        {
            tell0Frac = tell = 1;
            nbFilledBytes = 0;
        }
        else
        {
            tell0Frac = (int)enc.TellFrac();
            tell = enc.Tell();
            nbFilledBytes = (tell + 4) >> 3;
        }

        // Can't produce more than 1275 output bytes.
        nbCompressedBytes = Math.Min(nbCompressedBytes, 1275);
        int nbAvailableBytes = nbCompressedBytes - nbFilledBytes;
        int vbrRate;
        int effectiveBytes;
        if (Vbr && Bitrate != BitrateMax)
        {
            int den = 48000 >> BitRes;
            vbrRate = ((Bitrate * frameSize) + (den >> 1)) / den;
            effectiveBytes = vbrRate >> (3 + BitRes);
        }
        else
        {
            vbrRate = 0;
            int tmp = Bitrate * frameSize;
            if (tell > 1)
            {
                tmp += tell * 48000;
            }

            if (Bitrate != BitrateMax)
            {
                nbCompressedBytes = Math.Max(2, Math.Min(nbCompressedBytes, (tmp + (4 * 48000)) / (8 * 48000)));
                enc?.Shrink((uint)nbCompressedBytes);
            }

            effectiveBytes = nbCompressedBytes - nbFilledBytes;
        }

        int equivRate = ((nbCompressedBytes * 8 * 50) << (3 - lm)) - (((40 * c) + 20) * ((400 >> lm) - 50));
        if (Bitrate != BitrateMax)
        {
            equivRate = Math.Min(equivRate, Bitrate - (((40 * c) + 20) * ((400 >> lm) - 50)));
        }

        if (enc == null)
        {
            _ownEncoder.Init(compressed!, offset, nbCompressedBytes);
            enc = _ownEncoder;
        }

        if (vbrRate > 0)
        {
            // Computes the max bit-rate allowed in VBR mode to avoid violating the target rate and buffering. We must do this up front
            // so that bust-prevention logic triggers correctly if we don't have enough bits.
            if (ConstrainedVbr)
            {
                // We could use any multiple of vbr_rate as bound (depending on the delay). This is clamped to ensure we use at least
                // two bytes if the encoder was entirely empty, but to allow 0 in hybrid mode.
                int vbrBound = vbrRate;
                int maxAllowed = Math.Min(Math.Max(tell == 1 ? 2 : 0, (vbrRate + vbrBound - _vbrReservoir) >> (BitRes + 3)), nbAvailableBytes);
                if (maxAllowed < nbAvailableBytes)
                {
                    nbCompressedBytes = nbFilledBytes + maxAllowed;
                    nbAvailableBytes = maxAllowed;
                    enc.Shrink((uint)nbCompressedBytes);
                }
            }
        }

        int totalBits = nbCompressedBytes * 8;
        int effEnd = end;
        if (effEnd > CeltTables.EffEBands)
        {
            effEnd = CeltTables.EffEBands;
        }

        Span<float> input = _in.AsSpan(0, cc * (n + Overlap));
        float sampleMax = MathF.Max(_overlapMax, MaxAbs(pcm[..(c * (n - Overlap) / _upsample)]));
        _overlapMax = MaxAbs(pcm.Slice(c * (n - Overlap) / _upsample, c * Overlap / _upsample));
        sampleMax = MathF.Max(sampleMax, _overlapMax);
        bool silence = sampleMax <= 1f / (1 << LsbDepth);
        if (tell == 1)
        {
            enc.EncodeBitLogp(silence, 15);
        }
        else
        {
            silence = false;
        }

        if (silence)
        {
            // In VBR mode there is no need to send more than the minimum.
            if (vbrRate > 0)
            {
                effectiveBytes = nbCompressedBytes = Math.Min(nbCompressedBytes, nbFilledBytes + 2);
                totalBits = nbCompressedBytes * 8;
                nbAvailableBytes = 2;
                enc.Shrink((uint)nbCompressedBytes);
            }

            // Pretend we've filled all the remaining bits with zeros (that's what the initialiser did anyway).
            tell = nbCompressedBytes * 8;
            enc.AddUsedBits(tell - enc.Tell());
        }

        int ch = 0;
        do
        {
            bool needClip = sampleMax > 65536f;
            Preemphasis(pcm[ch..], input[((ch * (n + Overlap)) + Overlap)..], n, cc, _upsample, ref _preemphMemE[ch], needClip);
        }
        while (++ch < cc);

        // Find pitch period and gain.
        int pitchIndex;
        float gain1;
        bool pfOn;
        bool pitchChange = false;
        int prefilterTapset;
        {
            bool enabled = ((Lfe && nbAvailableBytes > 3) || nbAvailableBytes > 12 * c) && !hybrid && !silence && !DisablePrefilter && Complexity >= 5;
            prefilterTapset = _tapsetDecision;
            pfOn = RunPrefilter(input, cc, n, prefilterTapset, out pitchIndex, out gain1, out int qg, enabled, nbAvailableBytes);
            if ((gain1 > .4f || _prefilterGain > .4f) && (!_analysis.Valid || _analysis.Tonality > .3)
                && (pitchIndex > 1.26 * _prefilterPeriod || pitchIndex < .79 * _prefilterPeriod))
            {
                pitchChange = true;
            }

            if (!pfOn)
            {
                if (!hybrid && tell + 16 <= totalBits)
                {
                    enc.EncodeBitLogp(false, 1);
                }
            }
            else
            {
                // This block is not gated by a total bits check only because of the nbAvailableBytes check above.
                enc.EncodeBitLogp(true, 1);
                pitchIndex += 1;
                int octave = OpusRangeDecoder.Ilog((uint)pitchIndex) - 5;
                enc.EncodeUint((uint)octave, 6);
                enc.EncodeBits((uint)(pitchIndex - (16 << octave)), 4 + octave);
                pitchIndex -= 1;
                enc.EncodeBits((uint)qg, 3);
                enc.EncodeIcdf(prefilterTapset, CeltTables.TapsetIcdf, 2);
            }
        }

        bool isTransient = false;
        int shortBlocks = 0;
        int tfChan = 0;
        bool weakTransient = false;
        bool transientGotDisabled = false;
        if (Complexity >= 1 && !Lfe)
        {
            // Reduces the likelihood of energy instability on fricatives at low bitrate in hybrid mode. It seems like we still want to
            // have real transients on vowels though (small SILK quantization offset value).
            bool allowWeakTransients = hybrid && effectiveBytes < 15 && _silkInfo.SignalType != 2;
            isTransient = TransientAnalysis(input, n + Overlap, cc, out tfEstimate, ref tfChan, allowWeakTransients, out weakTransient);
        }

        if (lm > 0 && enc.Tell() + 3 <= totalBits)
        {
            if (isTransient)
            {
                shortBlocks = m;
            }
        }
        else
        {
            isTransient = false;
            transientGotDisabled = true;
        }

        Span<float> freq = _freq.AsSpan(0, cc * n);
        Span<float> bandE = _bandE;
        Span<float> bandLogE = _bandLogE;
        Span<float> bandLogE2 = _bandLogE2;
        bool secondMdct = shortBlocks != 0 && Complexity >= 8;
        if (secondMdct)
        {
            ComputeMdcts(0, input, freq, c, cc, lm, _upsample);
            CeltBandsEncoder.ComputeBandEnergies(freq, bandE, effEnd, c, lm);
            CeltEnergy.Amp2Log2(effEnd, end, bandE, bandLogE2, c);
            for (ch = 0; ch < c; ch++)
            {
                for (int i = 0; i < end; i++)
                {
                    bandLogE2[(Nb * ch) + i] += .5f * lm;
                }
            }
        }

        ComputeMdcts(shortBlocks, input, freq, c, cc, lm, _upsample);
        if (cc == 2 && c == 1)
        {
            tfChan = 0;
        }

        CeltBandsEncoder.ComputeBandEnergies(freq, bandE, effEnd, c, lm);
        if (Lfe)
        {
            for (int i = 2; i < end; i++)
            {
                bandE[i] = MathF.Min(bandE[i], 1e-4f * bandE[0]);
                bandE[i] = MathF.Max(bandE[i], 1e-15f);
            }
        }

        CeltEnergy.Amp2Log2(effEnd, end, bandE, bandLogE, c);

        Span<float> surroundDynalloc = _surroundDynalloc;
        surroundDynalloc[..end].Clear();
        float surroundMasking = 0;
        float surroundTrim = 0;
        // This computes how much masking takes place between surround channels.
        if (!hybrid && _energyMask != null && !Lfe)
        {
            SurroundMasking(c, out surroundMasking, out surroundTrim);
        }

        // Temporal VBR (but not for LFE).
        float temporalVbr = 0;
        if (!Lfe)
        {
            float follow = -10f;
            float frameAvg = 0;
            float offsetTv = shortBlocks != 0 ? .5f * lm : 0;
            for (int i = start; i < end; i++)
            {
                follow = MathF.Max(follow - 1f, bandLogE[i] - offsetTv);
                if (c == 2)
                {
                    follow = MathF.Max(follow, bandLogE[i + Nb] - offsetTv);
                }

                frameAvg += follow;
            }

            frameAvg /= end - start;
            temporalVbr = frameAvg - _specAvg;
            temporalVbr = MathF.Min(3f, MathF.Max(-1.5f, temporalVbr));
            _specAvg += .02f * temporalVbr;
        }

        if (!secondMdct)
        {
            bandLogE[..(c * Nb)].CopyTo(bandLogE2);
        }

        // Last chance to catch any transient we might have missed in the time-domain analysis.
        if (lm > 0 && enc.Tell() + 3 <= totalBits && !isTransient && Complexity >= 5 && !Lfe && !hybrid)
        {
            if (PatchTransientDecision(bandLogE, _oldBandE, start, end, c))
            {
                isTransient = true;
                shortBlocks = m;
                ComputeMdcts(shortBlocks, input, freq, c, cc, lm, _upsample);
                CeltBandsEncoder.ComputeBandEnergies(freq, bandE, effEnd, c, lm);
                CeltEnergy.Amp2Log2(effEnd, end, bandE, bandLogE, c);
                // Compensate for the scaling of short vs long mdcts.
                for (ch = 0; ch < c; ch++)
                {
                    for (int i = 0; i < end; i++)
                    {
                        bandLogE2[(Nb * ch) + i] += .5f * lm;
                    }
                }

                tfEstimate = .2f;
            }
        }

        if (lm > 0 && enc.Tell() + 3 <= totalBits)
        {
            enc.EncodeBitLogp(isTransient, 3);
        }

        // Band normalisation.
        Span<float> x = _x.AsSpan(0, c * n);
        CeltBandsEncoder.NormaliseBands(freq, x, bandE, effEnd, c, m);

        bool enableTfAnalysis = effectiveBytes >= 15 * c && !hybrid && Complexity >= 2 && !Lfe;
        Span<int> offsets = _offsets;
        Span<int> importance = _importance;
        Span<int> spreadWeight = _spreadWeight;
        float maxDepth = DynallocAnalysis(bandLogE, bandLogE2, _oldBandE, start, end, c, offsets, LsbDepth, isTransient, Vbr, ConstrainedVbr, lm,
            effectiveBytes, out int totBoost, Lfe, surroundDynalloc, importance, spreadWeight);

        Span<int> tfRes = _tfRes;
        int tfSelect;
        // Disable variable tf resolution for hybrid and at very low bitrate.
        if (enableTfAnalysis)
        {
            int lambda = Math.Max(80, (20480 / effectiveBytes) + 2);
            tfSelect = TfAnalysis(effEnd, isTransient, tfRes, lambda, x, n, lm, tfEstimate, tfChan, importance);
            for (int i = effEnd; i < end; i++)
            {
                tfRes[i] = tfRes[effEnd - 1];
            }
        }
        else if (hybrid && weakTransient)
        {
            // For weak transients, we rely on the fact that improving time resolution using TF on a long window is imperfect and will
            // not result in an energy collapse at low bitrate.
            for (int i = 0; i < end; i++)
            {
                tfRes[i] = 1;
            }

            tfSelect = 0;
        }
        else if (hybrid && effectiveBytes < 15 && _silkInfo.SignalType != 2)
        {
            // For low bitrate hybrid, we force temporal resolution to 5 ms rather than 2.5 ms.
            for (int i = 0; i < end; i++)
            {
                tfRes[i] = 0;
            }

            tfSelect = isTransient ? 1 : 0;
        }
        else
        {
            for (int i = 0; i < end; i++)
            {
                tfRes[i] = isTransient ? 1 : 0;
            }

            tfSelect = 0;
        }

        Span<float> error = _error;
        ch = 0;
        do
        {
            for (int i = start; i < end; i++)
            {
                int idx = i + (ch * Nb);
                // When the energy is stable, slightly bias energy quantization towards the previous error to make the gain more stable
                // (a constant offset is better than fluctuations).
                if (MathF.Abs(bandLogE[idx] - _oldBandE[idx]) < 2f)
                {
                    bandLogE[idx] -= _energyError[idx] * 0.25f;
                }
            }
        }
        while (++ch < c);
        CeltEnergy.QuantCoarseEnergy(start, end, effEnd, bandLogE, _oldBandE, (uint)totalBits, error, enc, c, lm, nbAvailableBytes, ForceIntra,
            ref _delayedIntra, Complexity >= 4, LossRate, Lfe);

        TfEncode(start, end, isTransient, tfRes, lm, tfSelect, enc);

        if (enc.Tell() + 4 <= totalBits)
        {
            if (Lfe)
            {
                _tapsetDecision = 0;
                _spreadDecision = CeltTables.SpreadNormal;
            }
            else if (hybrid)
            {
                if (Complexity == 0)
                {
                    _spreadDecision = CeltTables.SpreadNone;
                }
                else if (isTransient)
                {
                    _spreadDecision = CeltTables.SpreadNormal;
                }
                else
                {
                    _spreadDecision = CeltTables.SpreadAggressive;
                }
            }
            else if (shortBlocks != 0 || Complexity < 3 || nbAvailableBytes < 10 * c)
            {
                _spreadDecision = Complexity == 0 ? CeltTables.SpreadNone : CeltTables.SpreadNormal;
            }
            else
            {
                _spreadDecision = CeltBandsEncoder.SpreadingDecision(x, ref _tonalAverage, _spreadDecision, ref _hfAverage, ref _tapsetDecision,
                    pfOn && shortBlocks == 0, effEnd, c, m, spreadWeight);
            }

            enc.EncodeIcdf(_spreadDecision, CeltTables.SpreadIcdf, 5);
        }

        // For LFE, everything interesting is in the first band.
        if (Lfe)
        {
            offsets[0] = Math.Min(8, effectiveBytes / 3);
        }

        Span<int> cap = _cap;
        CeltTables.InitCaps(cap, lm, c);

        int dynallocLogp = 6;
        totalBits <<= BitRes;
        int totalBoost = 0;
        tell = (int)enc.TellFrac();
        for (int i = start; i < end; i++)
        {
            int width = c * (eBands[i + 1] - eBands[i]) << lm;
            // quanta is 6 bits, but no more than 1 bit/sample and no less than 1/8 bit/sample.
            int quanta = Math.Min(width << BitRes, Math.Max(6 << BitRes, width));
            int dynallocLoopLogp = dynallocLogp;
            int boost = 0;
            int j;
            for (j = 0; tell + (dynallocLoopLogp << BitRes) < totalBits - totalBoost && boost < cap[i]; j++)
            {
                bool flag = j < offsets[i];
                enc.EncodeBitLogp(flag, dynallocLoopLogp);
                tell = (int)enc.TellFrac();
                if (!flag)
                {
                    break;
                }

                boost += quanta;
                totalBoost += quanta;
                dynallocLoopLogp = 1;
            }

            // Making dynalloc more likely.
            if (j != 0)
            {
                dynallocLogp = Math.Max(2, dynallocLogp - 1);
            }

            offsets[i] = boost;
        }

        bool dualStereo = false;
        if (c == 2)
        {
            // Always use MS for 2.5 ms frames until we can do a better analysis.
            if (lm != 0)
            {
                dualStereo = StereoAnalysis(x, lm, n);
            }

            _intensity = CeltBandsEncoder.HysteresisDecision(equivRate / 1000, IntensityThresholds, IntensityHysteresis, 21, _intensity);
            _intensity = Math.Min(end, Math.Max(start, _intensity));
        }

        int allocTrim = 5;
        if (tell + (6 << BitRes) <= totalBits - totalBoost)
        {
            if (start > 0 || Lfe)
            {
                _stereoSaving = 0;
                allocTrim = 5;
            }
            else
            {
                allocTrim = AllocTrimAnalysis(x, bandLogE, end, lm, c, n, ref _stereoSaving, tfEstimate, _intensity, surroundTrim, equivRate);
            }

            enc.EncodeIcdf(allocTrim, CeltTables.TrimIcdf, 7);
            tell = (int)enc.TellFrac();
        }

        // Variable bitrate.
        if (vbrRate > 0)
        {
            int lmDiff = CeltTables.MaxLM - lm;
            // Don't attempt to use more than 510 kb/s, even for frames smaller than 20 ms. The CELT allocator will just not be able to
            // use more than that anyway.
            nbCompressedBytes = Math.Min(nbCompressedBytes, 1275 >> (3 - lm));
            int baseTarget = !hybrid ? vbrRate - (((40 * c) + 20) << BitRes) : Math.Max(0, vbrRate - (((9 * c) + 4) << BitRes));
            if (ConstrainedVbr)
            {
                baseTarget += _vbrOffset >> lmDiff;
            }

            int target;
            if (!hybrid)
            {
                target = ComputeVbr(baseTarget, lm, equivRate, _lastCodedBands, c, _intensity, ConstrainedVbr, _stereoSaving, totBoost, tfEstimate,
                    pitchChange, maxDepth, Lfe, _energyMask != null, surroundMasking, temporalVbr);
            }
            else
            {
                target = baseTarget;
                // Tonal frames (offset<100) need more bits than noisy (offset>100) ones.
                if (_silkInfo.Offset < 100)
                {
                    target += 12 << BitRes >> (3 - lm);
                }

                if (_silkInfo.Offset > 100)
                {
                    target -= 18 << BitRes >> (3 - lm);
                }

                // Boosting bitrate on transients and vowels with significant temporal spikes.
                target += (int)((tfEstimate - .25f) * (50 << BitRes));
                // If we have a strong transient, let's make sure it has enough bits to code the first two bands, so that it can use
                // folding rather than noise.
                if (tfEstimate > .7f)
                {
                    target = Math.Max(target, 50 << BitRes);
                }
            }

            // The current offset is removed from the target and the space used so far is added.
            target += tell;
            // In VBR mode the frame size must not be reduced so much that it would result in the encoder running out of bits. The
            // margin of 2 bytes ensures that none of the bust-prevention logic in the decoder will have triggered so far.
            int minAllowed = ((tell + totalBoost + (1 << (BitRes + 3)) - 1) >> (BitRes + 3)) + 2;
            // Take into account the 37 bits we need to have left in the packet to signal a redundant frame in hybrid mode. Creating a
            // shorter packet would create an entropy coder desync.
            if (hybrid)
            {
                minAllowed = Math.Max(minAllowed, (tell0Frac + (37 << BitRes) + totalBoost + (1 << (BitRes + 3)) - 1) >> (BitRes + 3));
            }

            nbAvailableBytes = (target + (1 << (BitRes + 2))) >> (BitRes + 3);
            nbAvailableBytes = Math.Max(minAllowed, nbAvailableBytes);
            nbAvailableBytes = Math.Min(nbCompressedBytes, nbAvailableBytes);

            // By how much did we "miss" the target on that frame.
            int delta = target - vbrRate;
            target = nbAvailableBytes << (BitRes + 3);

            // If the frame is silent we don't adjust our drift, otherwise the encoder will shoot to very high rates after hitting a
            // span of silence, but we do allow the bitres to refill. This means that we'll undershoot our target in CVBR/VBR modes on
            // files with lots of silence.
            if (silence)
            {
                nbAvailableBytes = 2;
                target = 2 * 8 << BitRes;
                delta = 0;
            }

            float alpha;
            if (_vbrCount < 970)
            {
                _vbrCount++;
                alpha = 1f / (_vbrCount + 20);
            }
            else
            {
                alpha = .001f;
            }

            // How many bits have we used in excess of what we're allowed.
            if (ConstrainedVbr)
            {
                _vbrReservoir += target - vbrRate;
            }

            // Compute the offset we need to apply in order to reach the target.
            if (ConstrainedVbr)
            {
                _vbrDrift += (int)(alpha * ((delta * (1 << lmDiff)) - _vbrOffset - _vbrDrift));
                _vbrOffset = -_vbrDrift;
            }

            if (ConstrainedVbr && _vbrReservoir < 0)
            {
                // We're under the min value -- increase rate.
                int adjust = -_vbrReservoir / (8 << BitRes);
                // Unless we're just coding silence.
                nbAvailableBytes += silence ? 0 : adjust;
                _vbrReservoir = 0;
            }

            nbCompressedBytes = Math.Min(nbCompressedBytes, nbAvailableBytes);
            // This moves the raw bits to take into account the new compressed size.
            enc.Shrink((uint)nbCompressedBytes);
        }

        // Bit allocation.
        Span<int> fineQuant = _fineQuant;
        Span<int> pulses = _pulses;
        Span<int> finePriority = _finePriority;

        // bits = packet size - where we are - safety.
        int bits = ((nbCompressedBytes * 8) << BitRes) - (int)enc.TellFrac() - 1;
        int antiCollapseRsv = isTransient && lm >= 2 && bits >= (lm + 2) << BitRes ? 1 << BitRes : 0;
        bits -= antiCollapseRsv;
        int signalBandwidth = end - 1;
        if (_analysis.Valid)
        {
            int minBandwidth;
            if (equivRate < 32000 * c)
            {
                minBandwidth = 13;
            }
            else if (equivRate < 48000 * c)
            {
                minBandwidth = 16;
            }
            else if (equivRate < 60000 * c)
            {
                minBandwidth = 18;
            }
            else if (equivRate < 80000 * c)
            {
                minBandwidth = 19;
            }
            else
            {
                minBandwidth = 20;
            }

            signalBandwidth = Math.Max(_analysis.Bandwidth, minBandwidth);
        }

        if (Lfe)
        {
            signalBandwidth = 1;
        }

        int codedBands = CeltRate.ComputeAllocation(start, end, offsets, cap, allocTrim, ref _intensity, ref dualStereo, bits, out int balance, pulses,
            fineQuant, finePriority, c, lm, enc, _lastCodedBands, signalBandwidth);
        if (_lastCodedBands != 0)
        {
            _lastCodedBands = Math.Min(_lastCodedBands + 1, Math.Max(_lastCodedBands - 1, codedBands));
        }
        else
        {
            _lastCodedBands = codedBands;
        }

        CeltEnergy.QuantFineEnergy(start, end, _oldBandE, error, fineQuant, enc, c);

        // Residual quantisation.
        Span<byte> collapseMasks = _collapseMasks;
        _bands.QuantAllBands(start, end, x[..n], c == 2 ? x.Slice(n, n) : default, c == 2, collapseMasks, _bandE, pulses, shortBlocks != 0,
            _spreadDecision, dualStereo, _intensity, tfRes, (nbCompressedBytes * (8 << BitRes)) - antiCollapseRsv, balance, enc, lm, codedBands, ref _rng,
            Complexity, DisableInv);

        if (antiCollapseRsv > 0)
        {
            bool antiCollapseOn = _consecTransient < 2;
            enc.EncodeBits(antiCollapseOn ? 1u : 0u, 1);
        }

        CeltEnergy.QuantEnergyFinalise(start, end, _oldBandE, error, fineQuant, finePriority, (nbCompressedBytes * 8) - enc.Tell(), enc, c);
        Array.Clear(_energyError);
        ch = 0;
        do
        {
            for (int i = start; i < end; i++)
            {
                _energyError[i + (ch * Nb)] = MathF.Max(-0.5f, MathF.Min(0.5f, error[i + (ch * Nb)]));
            }
        }
        while (++ch < c);

        if (silence)
        {
            for (int i = 0; i < c * Nb; i++)
            {
                _oldBandE[i] = -28f;
            }
        }

        _prefilterPeriod = pitchIndex;
        _prefilterGain = gain1;
        _prefilterTapset = prefilterTapset;

        if (cc == 2 && c == 1)
        {
            _oldBandE.AsSpan(0, Nb).CopyTo(_oldBandE.AsSpan(Nb));
        }

        if (!isTransient)
        {
            _oldLogE.AsSpan(0, cc * Nb).CopyTo(_oldLogE2);
            _oldBandE.AsSpan(0, cc * Nb).CopyTo(_oldLogE);
        }
        else
        {
            for (int i = 0; i < cc * Nb; i++)
            {
                _oldLogE[i] = MathF.Min(_oldLogE[i], _oldBandE[i]);
            }
        }

        // In case start or end were to change.
        ch = 0;
        do
        {
            for (int i = 0; i < start; i++)
            {
                _oldBandE[(ch * Nb) + i] = 0;
                _oldLogE[(ch * Nb) + i] = _oldLogE2[(ch * Nb) + i] = -28f;
            }

            for (int i = end; i < Nb; i++)
            {
                _oldBandE[(ch * Nb) + i] = 0;
                _oldLogE[(ch * Nb) + i] = _oldLogE2[(ch * Nb) + i] = -28f;
            }
        }
        while (++ch < cc);

        if (isTransient || transientGotDisabled)
        {
            _consecTransient++;
        }
        else
        {
            _consecTransient = 0;
        }

        _rng = enc.Range;

        // If there's any room left (can only happen for very high rates), it's already filled with zeros.
        enc.Done();
        return enc.Error ? OpusInternalError : nbCompressedBytes;
    }

    // celt_maxabs16.
    private static float MaxAbs(ReadOnlySpan<float> x)
    {
        float maxval = 0;
        float minval = 0;
        for (int i = 0; i < x.Length; i++)
        {
            maxval = maxval > x[i] ? maxval : x[i];
            minval = minval < x[i] ? minval : x[i];
        }

        return maxval > -minval ? maxval : -minval;
    }

    // celt_preemphasis for the 48 kHz mode (coef[1] == 0).
    internal static void Preemphasis(ReadOnlySpan<float> pcm, Span<float> inp, int n, int cc, int upsample, ref float mem, bool clip)
    {
        const float Coef0 = CeltTables.Preemph0;
        float mm = mem;
        int nu = n / upsample;
        if (upsample != 1)
        {
            inp[..n].Clear();
        }

        for (int i = 0; i < nu; i++)
        {
            inp[i * upsample] = pcm[cc * i] * 32768f;
        }

        if (clip)
        {
            // Clip input to avoid encoding non-portable files.
            for (int i = 0; i < nu; i++)
            {
                inp[i * upsample] = MathF.Max(-65536f, MathF.Min(65536f, inp[i * upsample]));
            }
        }

        for (int i = 0; i < n; i++)
        {
            float x = inp[i];
            // Apply pre-emphasis.
            inp[i] = x - mm;
            mm = Coef0 * x;
        }

        mem = mm;
    }

    // compute_mdcts: windows and transforms all sub-frames and channels of a frame.
    private static void ComputeMdcts(int shortBlocks, ReadOnlySpan<float> input, Span<float> output, int c, int cc, int lm, int upsample)
    {
        int b;
        int n;
        int shift;
        if (shortBlocks != 0)
        {
            b = shortBlocks;
            n = CeltTables.ShortMdctSize;
            shift = CeltTables.MaxLM;
        }
        else
        {
            b = 1;
            n = CeltTables.ShortMdctSize << lm;
            shift = CeltTables.MaxLM - lm;
        }

        int ch = 0;
        do
        {
            for (int bi = 0; bi < b; bi++)
            {
                // Interleaving the sub-frames while doing the MDCTs.
                CeltMdct.Forward(input[((ch * ((b * n) + Overlap)) + (bi * n))..], output[(bi + (ch * n * b))..], CeltTables.Window120, Overlap, shift, b);
            }
        }
        while (++ch < cc);

        if (cc == 2 && c == 1)
        {
            for (int i = 0; i < b * n; i++)
            {
                output[i] = (.5f * output[i]) + (.5f * output[(b * n) + i]);
            }
        }

        if (upsample != 1)
        {
            ch = 0;
            do
            {
                int bound = b * n / upsample;
                for (int i = 0; i < bound; i++)
                {
                    output[(ch * b * n) + i] *= upsample;
                }

                output.Slice((ch * b * n) + bound, (b * n) - bound).Clear();
            }
            while (++ch < c);
        }
    }

    // transient_analysis: decides whether the frame needs short blocks, from the masking of a high-passed copy of the input.
    private bool TransientAnalysis(ReadOnlySpan<float> input, int len, int c, out float tfEstimate, ref int tfChan, bool allowWeakTransients,
        out bool weakTransient)
    {
        Span<float> tmp = _tmp.AsSpan(0, len);
        int maskMetric = 0;
        weakTransient = false;
        // Forward masking: 6.7 dB/ms. For lower bitrates, let's be more conservative and have a forward masking decay of 3.3 dB/ms.
        // This avoids having to code transients at very low bitrate (mostly for hybrid), which can result in unstable energy and/or
        // partial collapse.
        float forwardDecay = allowWeakTransients ? .03125f : .0625f;
        int len2 = len / 2;
        for (int ch = 0; ch < c; ch++)
        {
            float mem0 = 0;
            float mem1 = 0;
            // High-pass filter: (1 - 2*z^-1 + z^-2) / (1 - z^-1 + .5*z^-2).
            for (int i = 0; i < len; i++)
            {
                float x = input[i + (ch * len)];
                float y = mem0 + x;
                // Modified code to shorten dependency chains.
                float mem00 = mem0;
                mem0 = mem0 - x + (.5f * mem1);
                mem1 = x - mem00;
                tmp[i] = y;
            }

            // First few samples are bad because we don't propagate the memory.
            tmp[..12].Clear();

            float mean = 0;
            mem0 = 0;
            // Grouping by two to reduce complexity. Forward pass to compute the post-echo threshold.
            for (int i = 0; i < len2; i++)
            {
                float x2 = (tmp[2 * i] * tmp[2 * i]) + (tmp[(2 * i) + 1] * tmp[(2 * i) + 1]);
                mean += x2;
                mem0 = x2 + ((1f - forwardDecay) * mem0);
                tmp[i] = forwardDecay * mem0;
            }

            mem0 = 0;
            float maxE = 0;
            // Backward pass to compute the pre-echo threshold. Backward masking: 13.9 dB/ms.
            for (int i = len2 - 1; i >= 0; i--)
            {
                mem0 = tmp[i] + (0.875f * mem0);
                tmp[i] = 0.125f * mem0;
                maxE = MathF.Max(maxE, 0.125f * mem0);
            }

            // Compute the ratio of the "frame energy" over the harmonic mean of the energy. As a compromise with the old transient
            // detector, frame energy is the geometric mean of the energy and half the max.
            mean = (float)Math.Sqrt(mean * maxE * .5 * len2);
            // Inverse of the mean energy in Q15+6.
            float norm = len2 / (1e-15f + mean);
            // Compute harmonic mean discarding the unreliable boundaries. The data is smooth, so we only take 1/4th of the samples.
            int unmask = 0;
            for (int i = 12; i < len2 - 5; i += 4)
            {
                float v = 64 * norm * (tmp[i] + 1e-15f);
                // Do not round to nearest.
                int id = (int)Math.Max(0, Math.Min(127, Math.Floor(v)));
                unmask += InvTable[id];
            }

            // Normalize, compensate for the 1/4th of the sample and the factor of 6 in the inverse table.
            unmask = 64 * unmask * 4 / (6 * (len2 - 17));
            if (unmask > maskMetric)
            {
                tfChan = ch;
                maskMetric = unmask;
            }
        }

        bool isTransient = maskMetric > 200;
        // For low bitrates, define "weak transients" that need to be handled differently to avoid partial collapse.
        if (allowWeakTransients && isTransient && maskMetric < 600)
        {
            isTransient = false;
            weakTransient = true;
        }

        // Arbitrary metric for VBR boost.
        float tfMax = MathF.Max(0, (float)Math.Sqrt(27 * maskMetric) - 42);
        tfEstimate = (float)Math.Sqrt(Math.Max(0, (0.0069f * MathF.Min(163, tfMax)) - 0.139));
        return isTransient;
    }

    // patch_transient_decision: looks for sudden increases of energy to decide whether we need to patch the transient decision.
    private static bool PatchTransientDecision(ReadOnlySpan<float> newE, ReadOnlySpan<float> oldE, int start, int end, int c)
    {
        Span<float> spreadOld = stackalloc float[26];
        // Apply an aggressive (-6 dB/Bark) spreading function to the old frame to avoid false detection caused by irrelevant bands.
        if (c == 1)
        {
            spreadOld[start] = oldE[start];
            for (int i = start + 1; i < end; i++)
            {
                spreadOld[i] = MathF.Max(spreadOld[i - 1] - 1f, oldE[i]);
            }
        }
        else
        {
            spreadOld[start] = MathF.Max(oldE[start], oldE[start + Nb]);
            for (int i = start + 1; i < end; i++)
            {
                spreadOld[i] = MathF.Max(spreadOld[i - 1] - 1f, MathF.Max(oldE[i], oldE[i + Nb]));
            }
        }

        for (int i = end - 2; i >= start; i--)
        {
            spreadOld[i] = MathF.Max(spreadOld[i], spreadOld[i + 1] - 1f);
        }

        // Compute mean increase.
        float meanDiff = 0;
        int ch = 0;
        do
        {
            for (int i = Math.Max(2, start); i < end - 1; i++)
            {
                float x1 = MathF.Max(0, newE[i + (ch * Nb)]);
                float x2 = MathF.Max(0, spreadOld[i]);
                meanDiff += MathF.Max(0, x1 - x2);
            }
        }
        while (++ch < c);
        meanDiff /= c * (end - 1 - Math.Max(2, start));
        return meanDiff > 1f;
    }

    // l1_metric.
    private static float L1Metric(ReadOnlySpan<float> tmp, int n, int lm, float bias)
    {
        float l1 = 0;
        for (int i = 0; i < n; i++)
        {
            l1 += MathF.Abs(tmp[i]);
        }

        // When in doubt, prefer good freq resolution.
        return l1 + (lm * bias * l1);
    }

    // tf_analysis: picks the time-frequency resolution of each band with a Viterbi search.
    private static int TfAnalysis(int len, bool isTransient, Span<int> tfRes, int lambda, ReadOnlySpan<float> x, int n0, int lm, float tfEstimate, int tfChan,
        ReadOnlySpan<int> importance)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<sbyte> table = CeltTables.TfSelectTable;
        int it = isTransient ? 1 : 0;
        float bias = .04f * MathF.Max(-.25f, .5f - tfEstimate);
        Span<int> metric = stackalloc int[len];
        Span<float> tmp = stackalloc float[(eBands[len] - eBands[len - 1]) << lm];
        Span<float> tmp1 = stackalloc float[(eBands[len] - eBands[len - 1]) << lm];
        Span<int> path0 = stackalloc int[len];
        Span<int> path1 = stackalloc int[len];
        for (int i = 0; i < len; i++)
        {
            int bestLevel = 0;
            int n = (eBands[i + 1] - eBands[i]) << lm;
            // Band is too narrow to be split down to LM=-1.
            bool narrow = eBands[i + 1] - eBands[i] == 1;
            x.Slice((tfChan * n0) + (eBands[i] << lm), n).CopyTo(tmp);
            float l1 = L1Metric(tmp, n, isTransient ? lm : 0, bias);
            float bestL1 = l1;
            // Check the -1 case for transients.
            if (isTransient && !narrow)
            {
                tmp[..n].CopyTo(tmp1);
                CeltBands.Haar1(tmp1, n >> lm, 1 << lm);
                l1 = L1Metric(tmp1, n, lm + 1, bias);
                if (l1 < bestL1)
                {
                    bestL1 = l1;
                    bestLevel = -1;
                }
            }

            for (int k = 0; k < lm + (!(isTransient || narrow) ? 1 : 0); k++)
            {
                int b = isTransient ? lm - k - 1 : k + 1;
                CeltBands.Haar1(tmp, n >> k, 1 << k);
                l1 = L1Metric(tmp, n, b, bias);
                if (l1 < bestL1)
                {
                    bestL1 = l1;
                    bestLevel = k + 1;
                }
            }

            // metric is in Q1 to be able to select the mid-point (-0.5) for narrower bands.
            metric[i] = isTransient ? 2 * bestLevel : -2 * bestLevel;
            // For bands that can't be split to -1, set the metric to the half-way point to avoid biasing the decision.
            if (narrow && (metric[i] == 0 || metric[i] == -2 * lm))
            {
                metric[i] -= 1;
            }
        }

        // Search for the optimal tf resolution, including tf_select.
        int tfSelect = 0;
        Span<int> selcost = stackalloc int[2];
        int row = lm * 8;
        for (int sel = 0; sel < 2; sel++)
        {
            int cost0 = importance[0] * Math.Abs(metric[0] - (2 * table[row + (4 * it) + (2 * sel)]));
            int cost1 = (importance[0] * Math.Abs(metric[0] - (2 * table[row + (4 * it) + (2 * sel) + 1]))) + (isTransient ? 0 : lambda);
            for (int i = 1; i < len; i++)
            {
                int curr0 = Math.Min(cost0, cost1 + lambda);
                int curr1 = Math.Min(cost0 + lambda, cost1);
                cost0 = curr0 + (importance[i] * Math.Abs(metric[i] - (2 * table[row + (4 * it) + (2 * sel)])));
                cost1 = curr1 + (importance[i] * Math.Abs(metric[i] - (2 * table[row + (4 * it) + (2 * sel) + 1])));
            }

            selcost[sel] = Math.Min(cost0, cost1);
        }

        // For now, we're conservative and only allow tf_select=1 for transients. If tests confirm it's useful for non-transients, we
        // could allow it.
        if (selcost[1] < selcost[0] && isTransient)
        {
            tfSelect = 1;
        }

        {
            int cost0 = importance[0] * Math.Abs(metric[0] - (2 * table[row + (4 * it) + (2 * tfSelect)]));
            int cost1 = (importance[0] * Math.Abs(metric[0] - (2 * table[row + (4 * it) + (2 * tfSelect) + 1]))) + (isTransient ? 0 : lambda);
            // Viterbi forward pass.
            for (int i = 1; i < len; i++)
            {
                int curr0;
                int curr1;
                int from0 = cost0;
                int from1 = cost1 + lambda;
                if (from0 < from1)
                {
                    curr0 = from0;
                    path0[i] = 0;
                }
                else
                {
                    curr0 = from1;
                    path0[i] = 1;
                }

                from0 = cost0 + lambda;
                from1 = cost1;
                if (from0 < from1)
                {
                    curr1 = from0;
                    path1[i] = 0;
                }
                else
                {
                    curr1 = from1;
                    path1[i] = 1;
                }

                cost0 = curr0 + (importance[i] * Math.Abs(metric[i] - (2 * table[row + (4 * it) + (2 * tfSelect)])));
                cost1 = curr1 + (importance[i] * Math.Abs(metric[i] - (2 * table[row + (4 * it) + (2 * tfSelect) + 1])));
            }

            tfRes[len - 1] = cost0 < cost1 ? 0 : 1;
        }

        // Viterbi backward pass to check the decisions.
        for (int i = len - 2; i >= 0; i--)
        {
            tfRes[i] = tfRes[i + 1] == 1 ? path1[i + 1] : path0[i + 1];
        }

        return tfSelect;
    }

    // tf_encode.
    private static void TfEncode(int start, int end, bool isTransient, Span<int> tfRes, int lm, int tfSelect, OpusRangeEncoder enc)
    {
        ReadOnlySpan<sbyte> table = CeltTables.TfSelectTable;
        int it = isTransient ? 1 : 0;
        uint budget = enc.Storage * 8;
        uint tell = (uint)enc.Tell();
        int logp = isTransient ? 2 : 4;
        // Reserve space to code the tf_select decision.
        bool tfSelectRsv = lm > 0 && tell + (uint)logp + 1 <= budget;
        budget -= tfSelectRsv ? 1u : 0u;
        int curr = 0;
        int tfChanged = 0;
        for (int i = start; i < end; i++)
        {
            if (tell + (uint)logp <= budget)
            {
                enc.EncodeBitLogp((tfRes[i] ^ curr) != 0, logp);
                tell = (uint)enc.Tell();
                curr = tfRes[i];
                tfChanged |= curr;
            }
            else
            {
                tfRes[i] = curr;
            }

            logp = isTransient ? 4 : 5;
        }

        // Only code tf_select if it would actually make a difference.
        if (tfSelectRsv && table[(lm * 8) + (4 * it) + tfChanged] != table[(lm * 8) + (4 * it) + 2 + tfChanged])
        {
            enc.EncodeBitLogp(tfSelect != 0, 1);
        }
        else
        {
            tfSelect = 0;
        }

        for (int i = start; i < end; i++)
        {
            tfRes[i] = table[(lm * 8) + (4 * it) + (2 * tfSelect) + tfRes[i]];
        }
    }

    // alloc_trim_analysis.
    private int AllocTrimAnalysis(ReadOnlySpan<float> x, ReadOnlySpan<float> bandLogE, int end, int lm, int c, int n0, ref float stereoSaving,
        float tfEstimate, int intensity, float surroundTrim, int equivRate)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        float diff = 0;
        float trim = 5f;
        // At low bitrate, reducing the trim seems to help. At higher bitrates, it's less clear what's best, so we're keeping it as it
        // was before, at least for now.
        if (equivRate < 64000)
        {
            trim = 4f;
        }
        else if (equivRate < 80000)
        {
            int frac = (equivRate - 64000) >> 10;
            trim = 4f + (1f / 16f * frac);
        }

        if (c == 2)
        {
            float sum = 0;
            // Compute inter-channel correlation for low frequencies.
            for (int i = 0; i < 8; i++)
            {
                float partial = CeltMath.InnerProduct(x[(eBands[i] << lm)..], x[(n0 + (eBands[i] << lm))..], (eBands[i + 1] - eBands[i]) << lm);
                sum += partial;
            }

            sum = 1f / 8 * sum;
            sum = MathF.Min(1f, MathF.Abs(sum));
            float minXC = sum;
            for (int i = 8; i < intensity; i++)
            {
                float partial = CeltMath.InnerProduct(x[(eBands[i] << lm)..], x[(n0 + (eBands[i] << lm))..], (eBands[i + 1] - eBands[i]) << lm);
                minXC = MathF.Min(minXC, MathF.Abs(partial));
            }

            minXC = MathF.Min(1f, MathF.Abs(minXC));
            // Mid-side savings estimations based on the LF average.
            float logXC = CeltEnergy.Log2(1.001f - (sum * sum));
            // Mid-side savings estimations based on min correlation.
            float logXC2 = MathF.Max(.5f * logXC, CeltEnergy.Log2(1.001f - (minXC * minXC)));
            trim += MathF.Max(-4f, .75f * logXC);
            stereoSaving = MathF.Min(stereoSaving + 0.25f, -.5f * logXC2);
        }

        // Estimate spectral tilt.
        int ch = 0;
        do
        {
            for (int i = 0; i < end - 1; i++)
            {
                diff += bandLogE[i + (ch * Nb)] * (2 + (2 * i) - end);
            }
        }
        while (++ch < c);
        diff /= c * (end - 1);
        trim -= MathF.Max(-2f, MathF.Min(2f, (diff + 1f) / 6));
        trim -= surroundTrim;
        trim -= 2 * tfEstimate;
        if (_analysis.Valid)
        {
            trim -= MathF.Max(-2f, MathF.Min(2f, 2f * (_analysis.TonalitySlope + .05f)));
        }

        int trimIndex = (int)MathF.Floor(.5f + trim);
        return Math.Max(0, Math.Min(10, trimIndex));
    }

    // stereo_analysis: uses the L1 norm to model the entropy of the L/R signal vs the M/S signal.
    private static bool StereoAnalysis(ReadOnlySpan<float> x, int lm, int n0)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        float sumLR = 1e-15f;
        float sumMS = 1e-15f;
        for (int i = 0; i < 13; i++)
        {
            for (int j = eBands[i] << lm; j < eBands[i + 1] << lm; j++)
            {
                float l = x[j];
                float r = x[n0 + j];
                float mm = l + r;
                float s = l - r;
                sumLR += MathF.Abs(l) + MathF.Abs(r);
                sumMS += MathF.Abs(mm) + MathF.Abs(s);
            }
        }

        sumMS = 0.707107f * sumMS;
        int thetas = 13;
        // We don't need thetas for lower bands with LM<=1.
        if (lm <= 1)
        {
            thetas -= 8;
        }

        return ((eBands[13] << (lm + 1)) + thetas) * sumMS > (eBands[13] << (lm + 1)) * sumLR;
    }

    private static float MedianOf5(ReadOnlySpan<float> x)
    {
        float t0, t1, t3, t4;
        float t2 = x[2];
        if (x[0] > x[1])
        {
            t0 = x[1];
            t1 = x[0];
        }
        else
        {
            t0 = x[0];
            t1 = x[1];
        }

        if (x[3] > x[4])
        {
            t3 = x[4];
            t4 = x[3];
        }
        else
        {
            t3 = x[3];
            t4 = x[4];
        }

        if (t0 > t3)
        {
            (t0, t3) = (t3, t0);
            (t1, t4) = (t4, t1);
        }

        if (t2 > t1)
        {
            return t1 < t3 ? MathF.Min(t2, t3) : MathF.Min(t4, t1);
        }

        return t2 < t3 ? MathF.Min(t1, t3) : MathF.Min(t2, t4);
    }

    private static float MedianOf3(ReadOnlySpan<float> x)
    {
        float t0, t1;
        if (x[0] > x[1])
        {
            t0 = x[1];
            t1 = x[0];
        }
        else
        {
            t0 = x[0];
            t1 = x[1];
        }

        float t2 = x[2];
        if (t1 < t2)
        {
            return t1;
        }

        return t0 < t2 ? t2 : t0;
    }

    // dynalloc_analysis: boosts bands that stick out of a smoothed spectrum, and computes the importance and spreading weights.
    private float DynallocAnalysis(ReadOnlySpan<float> bandLogE, ReadOnlySpan<float> bandLogE2, ReadOnlySpan<float> oldBandE, int start, int end, int c,
        Span<int> offsets, int lsbDepth, bool isTransient, bool vbr, bool constrainedVbr, int lm, int effectiveBytes, out int totBoostOut, bool lfe,
        ReadOnlySpan<float> surroundDynalloc, Span<int> importance, Span<int> spreadWeight)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<short> logN = CeltTables.LogN400;
        ReadOnlySpan<float> eMeans = CeltTables.EMeans;
        int totBoost = 0;
        Span<float> follower = stackalloc float[c * Nb];
        Span<float> noiseFloor = stackalloc float[c * Nb];
        Span<float> bandLogE3 = stackalloc float[Nb];
        offsets[..Nb].Clear();
        // Dynamic allocation code.
        float maxDepth = -31.9f;
        for (int i = 0; i < end; i++)
        {
            // Noise floor must take into account eMeans, the depth, the width of the bands and the preemphasis filter (approx. square
            // of bark band ID).
            noiseFloor[i] = (0.0625f * logN[i]) + .5f + (9 - lsbDepth) - eMeans[i] + (0.0062f * ((i + 5) * (i + 5)));
        }

        int ch = 0;
        do
        {
            for (int i = 0; i < end; i++)
            {
                maxDepth = MathF.Max(maxDepth, bandLogE[(ch * Nb) + i] - noiseFloor[i]);
            }
        }
        while (++ch < c);

        {
            // Compute a really simple masking model to avoid taking into account completely masked bands when computing the spreading
            // decision.
            Span<float> mask = stackalloc float[Nb];
            Span<float> sig = stackalloc float[Nb];
            for (int i = 0; i < end; i++)
            {
                mask[i] = bandLogE[i] - noiseFloor[i];
            }

            if (c == 2)
            {
                for (int i = 0; i < end; i++)
                {
                    mask[i] = MathF.Max(mask[i], bandLogE[Nb + i] - noiseFloor[i]);
                }
            }

            mask[..end].CopyTo(sig);
            for (int i = 1; i < end; i++)
            {
                mask[i] = MathF.Max(mask[i], mask[i - 1] - 2f);
            }

            for (int i = end - 2; i >= 0; i--)
            {
                mask[i] = MathF.Max(mask[i], mask[i + 1] - 3f);
            }

            for (int i = 0; i < end; i++)
            {
                // Compute SMR: Mask is never more than 72 dB below the peak and never below the noise floor.
                float smr = sig[i] - MathF.Max(MathF.Max(0, maxDepth - 12f), mask[i]);
                // Clamp SMR to make sure we're not shifting by something negative or too large.
                int shift = Math.Min(5, Math.Max(0, -(int)MathF.Floor(.5f + smr)));
                spreadWeight[i] = 32 >> shift;
            }
        }

        // Make sure that dynamic allocation can't make us bust the budget. We enable the feature starting at 24 kb/s for 20-ms frames
        // and 96 kb/s for 2.5 ms frames.
        if (effectiveBytes >= 30 + (5 * lm) && !lfe)
        {
            int last = 0;
            ch = 0;
            do
            {
                bandLogE2.Slice(ch * Nb, end).CopyTo(bandLogE3);
                if (lm == 0)
                {
                    // For 2.5 ms frames, the first 8 bands have just one bin, so the energy is highly unreliable (high variance). For
                    // that reason, we take the max with the previous energy so that at least 2 bins are getting used.
                    for (int i = 0; i < Math.Min(8, end); i++)
                    {
                        bandLogE3[i] = MathF.Max(bandLogE2[(ch * Nb) + i], oldBandE[(ch * Nb) + i]);
                    }
                }

                Span<float> f = follower.Slice(ch * Nb, Nb);
                f[0] = bandLogE3[0];
                for (int i = 1; i < end; i++)
                {
                    // The last band to be at least 3 dB higher than the previous one is the last we'll consider. Otherwise, we run into
                    // problems on bandlimited signals.
                    if (bandLogE3[i] > bandLogE3[i - 1] + .5f)
                    {
                        last = i;
                    }

                    f[i] = MathF.Min(f[i - 1] + 1.5f, bandLogE3[i]);
                }

                for (int i = last - 1; i >= 0; i--)
                {
                    f[i] = MathF.Min(f[i], MathF.Min(f[i + 1] + 2f, bandLogE3[i]));
                }

                // Combine with a median filter to avoid dynalloc triggering unnecessarily. The "offset" value controls how conservative
                // we are -- a higher offset reduces the impact of the median filter and makes dynalloc use more bits.
                const float Offset = 1f;
                for (int i = 2; i < end - 2; i++)
                {
                    f[i] = MathF.Max(f[i], MedianOf5(bandLogE3[(i - 2)..]) - Offset);
                }

                float tmp = MedianOf3(bandLogE3) - Offset;
                f[0] = MathF.Max(f[0], tmp);
                f[1] = MathF.Max(f[1], tmp);
                tmp = MedianOf3(bandLogE3[(end - 3)..]) - Offset;
                f[end - 2] = MathF.Max(f[end - 2], tmp);
                f[end - 1] = MathF.Max(f[end - 1], tmp);

                for (int i = 0; i < end; i++)
                {
                    f[i] = MathF.Max(f[i], noiseFloor[i]);
                }
            }
            while (++ch < c);

            if (c == 2)
            {
                for (int i = start; i < end; i++)
                {
                    // Consider 24 dB "cross-talk".
                    follower[Nb + i] = MathF.Max(follower[Nb + i], follower[i] - 4f);
                    follower[i] = MathF.Max(follower[i], follower[Nb + i] - 4f);
                    follower[i] = .5f * (MathF.Max(0, bandLogE[i] - follower[i]) + MathF.Max(0, bandLogE[Nb + i] - follower[Nb + i]));
                }
            }
            else
            {
                for (int i = start; i < end; i++)
                {
                    follower[i] = MathF.Max(0, bandLogE[i] - follower[i]);
                }
            }

            for (int i = start; i < end; i++)
            {
                follower[i] = MathF.Max(follower[i], surroundDynalloc[i]);
            }

            for (int i = start; i < end; i++)
            {
                importance[i] = (int)MathF.Floor(.5f + (13 * CeltMath.Exp2(MathF.Min(follower[i], 4f))));
            }

            // For non-transient CBR/CVBR frames, halve the dynalloc contribution.
            if ((!vbr || constrainedVbr) && !isTransient)
            {
                for (int i = start; i < end; i++)
                {
                    follower[i] = .5f * follower[i];
                }
            }

            for (int i = start; i < end; i++)
            {
                if (i < 8)
                {
                    follower[i] *= 2;
                }

                if (i >= 12)
                {
                    follower[i] = .5f * follower[i];
                }
            }

            if (_analysis.Valid)
            {
                for (int i = start; i < Math.Min(OpusAnalysisInfo.LeakBands, end); i++)
                {
                    follower[i] = follower[i] + (1f / 64f * _analysis.LeakBoost[i]);
                }
            }

            for (int i = start; i < end; i++)
            {
                follower[i] = MathF.Min(follower[i], 4);
                int width = c * (eBands[i + 1] - eBands[i]) << lm;
                int boost;
                int boostBits;
                if (width < 6)
                {
                    boost = (int)follower[i];
                    boostBits = boost * width << BitRes;
                }
                else if (width > 48)
                {
                    boost = (int)(follower[i] * 8);
                    boostBits = (boost * width << BitRes) / 8;
                }
                else
                {
                    boost = (int)(follower[i] * width / 6);
                    boostBits = boost * 6 << BitRes;
                }

                // For CBR and non-transient CVBR frames, limit dynalloc to 2/3 of the bits.
                if ((!vbr || (constrainedVbr && !isTransient)) && (totBoost + boostBits) >> BitRes >> 3 > 2 * effectiveBytes / 3)
                {
                    int cap = (2 * effectiveBytes / 3) << BitRes << 3;
                    offsets[i] = cap - totBoost;
                    totBoost = cap;
                    break;
                }
                else
                {
                    offsets[i] = boost;
                    totBoost += boostBits;
                }
            }
        }
        else
        {
            for (int i = start; i < end; i++)
            {
                importance[i] = 13;
            }
        }

        totBoostOut = totBoost;
        return maxDepth;
    }

    // run_prefilter: finds the pitch and applies the comb pre-filter in place on the input.
    private bool RunPrefilter(Span<float> input, int cc, int n, int prefilterTapset, out int pitch, out float gain, out int qgain, bool enabled,
        int nbAvailableBytes)
    {
        const int Stride = 960 + MaxPeriod;
        int pitchIndex;
        float gain1;
        Span<float> pre = _pre;
        int ch = 0;
        do
        {
            _prefilterMem.AsSpan(ch * MaxPeriod, MaxPeriod).CopyTo(pre[(ch * Stride)..]);
            input.Slice((ch * (n + Overlap)) + Overlap, n).CopyTo(pre[((ch * Stride) + MaxPeriod)..]);
        }
        while (++ch < cc);

        if (enabled)
        {
            Span<float> pitchBuf = _pitchBuf.AsSpan(0, (MaxPeriod + n) >> 1);
            CeltLpc.PitchDownsample(pre[..(MaxPeriod + n)], cc == 2 ? pre.Slice(Stride, MaxPeriod + n) : default, pitchBuf, MaxPeriod + n, cc);
            // Don't search for the fir last 1.5 octave of the range because there's too many false-positives due to short-term
            // correlation.
            pitchIndex = CeltLpc.PitchSearch(pitchBuf[(MaxPeriod >> 1)..], pitchBuf, n, MaxPeriod - (3 * MinPeriod));
            pitchIndex = MaxPeriod - pitchIndex;
            gain1 = CeltLpc.RemoveDoubling(pitchBuf, MaxPeriod, MinPeriod, n, ref pitchIndex, _prefilterPeriod, _prefilterGain);
            if (pitchIndex > MaxPeriod - 2)
            {
                pitchIndex = MaxPeriod - 2;
            }

            gain1 = .7f * gain1;
            if (LossRate > 2)
            {
                gain1 = .5f * gain1;
            }

            if (LossRate > 4)
            {
                gain1 = .5f * gain1;
            }

            if (LossRate > 8)
            {
                gain1 = 0;
            }
        }
        else
        {
            gain1 = 0;
            pitchIndex = MinPeriod;
        }

        if (_analysis.Valid)
        {
            gain1 *= _analysis.MaxPitchRatio;
        }

        // Gain threshold for enabling the prefilter/postfilter.
        float pfThreshold = .2f;
        // Adjusting the threshold based on rate and continuity.
        if (Math.Abs(pitchIndex - _prefilterPeriod) * 10 > pitchIndex)
        {
            pfThreshold += .2f;
        }

        if (nbAvailableBytes < 25)
        {
            pfThreshold += .1f;
        }

        if (nbAvailableBytes < 35)
        {
            pfThreshold += .1f;
        }

        if (_prefilterGain > .4f)
        {
            pfThreshold -= .1f;
        }

        if (_prefilterGain > .55f)
        {
            pfThreshold -= .1f;
        }

        // Hard threshold at 0.2.
        pfThreshold = MathF.Max(pfThreshold, .2f);
        bool pfOn;
        int qg;
        if (gain1 < pfThreshold)
        {
            gain1 = 0;
            pfOn = false;
            qg = 0;
        }
        else
        {
            // This block is not gated by a total bits check only because of the nbAvailableBytes check above.
            if (MathF.Abs(gain1 - _prefilterGain) < .1f)
            {
                gain1 = _prefilterGain;
            }

            qg = (int)MathF.Floor(.5f + (gain1 * 32 / 3)) - 1;
            qg = Math.Max(0, Math.Min(7, qg));
            gain1 = 0.09375f * (qg + 1);
            pfOn = true;
        }

        ch = 0;
        do
        {
            _prefilterPeriod = Math.Max(_prefilterPeriod, MinPeriod);
            _inMem.AsSpan(ch * Overlap, Overlap).CopyTo(input[(ch * (n + Overlap))..]);
            // offset = shortMdctSize - overlap is 0 in the 48 kHz mode, so the constant first part is empty.
            CeltDecoder.CombFilter(input, (ch * (n + Overlap)) + Overlap, pre, (ch * Stride) + MaxPeriod, _prefilterPeriod, pitchIndex, n, -_prefilterGain,
                -gain1, _prefilterTapset, prefilterTapset, CeltTables.Window120, Overlap);
            input.Slice((ch * (n + Overlap)) + n, Overlap).CopyTo(_inMem.AsSpan(ch * Overlap));
            Span<float> mem = _prefilterMem.AsSpan(ch * MaxPeriod, MaxPeriod);
            mem[n..].CopyTo(mem);
            pre.Slice((ch * Stride) + MaxPeriod, n).CopyTo(mem[(MaxPeriod - n)..]);
        }
        while (++ch < cc);

        gain = gain1;
        pitch = pitchIndex;
        qgain = qg;
        return pfOn;
    }

    // compute_vbr: the VBR target of a frame in eighth bits.
    private int ComputeVbr(int baseTarget, int lm, int bitrate, int lastCodedBands, int c, int intensity, bool constrainedVbr, float stereoSaving, int totBoost,
        float tfEstimate, bool pitchChange, float maxDepth, bool lfe, bool hasSurroundMask, float surroundMasking, float temporalVbr)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int codedBands = lastCodedBands != 0 ? lastCodedBands : Nb;
        int codedBins = eBands[codedBands] << lm;
        if (c == 2)
        {
            codedBins += eBands[Math.Min(intensity, codedBands)] << lm;
        }

        int target = baseTarget;
        if (_analysis.Valid && _analysis.Activity < .4)
        {
            target -= (int)((codedBins << BitRes) * (.4f - _analysis.Activity));
        }

        // Stereo savings.
        if (c == 2)
        {
            int codedStereoBands = Math.Min(intensity, codedBands);
            int codedStereoDof = (eBands[codedStereoBands] << lm) - codedStereoBands;
            // Maximum fraction of the bits we can save if the signal is mono.
            float maxFrac = 0.8f * codedStereoDof / codedBins;
            stereoSaving = MathF.Min(stereoSaving, 1f);
            target -= (int)MathF.Min(maxFrac * target, (stereoSaving - 0.1f) * (codedStereoDof << BitRes));
        }

        // Boost the rate according to dynalloc (minus the dynalloc average for calibration).
        target += totBoost - (19 << lm);
        // Apply transient boost, compensating for average boost.
        const float TfCalibration = 0.044f;
        target += (int)((tfEstimate - TfCalibration) * target);

        // Apply tonality boost.
        if (_analysis.Valid && !lfe)
        {
            // Tonality boost (compensating for the average).
            float tonal = MathF.Max(0f, _analysis.Tonality - .15f) - 0.12f;
            int tonalTarget = target + (int)((codedBins << BitRes) * 1.2f * tonal);
            if (pitchChange)
            {
                tonalTarget += (int)((codedBins << BitRes) * .8f);
            }

            target = tonalTarget;
        }

        if (hasSurroundMask && !lfe)
        {
            int surroundTarget = target + (int)(surroundMasking * (codedBins << BitRes));
            target = Math.Max(target / 4, surroundTarget);
        }

        {
            int bins = eBands[Nb - 2] << lm;
            int floorDepth = (int)((c * bins << BitRes) * maxDepth);
            floorDepth = Math.Max(floorDepth, target >> 2);
            target = Math.Min(target, floorDepth);
        }

        // Make VBR less aggressive for constrained VBR because we can't keep a higher bitrate for long. Needs tuning.
        if ((!hasSurroundMask || lfe) && constrainedVbr)
        {
            target = baseTarget + (int)(0.67f * (target - baseTarget));
        }

        if (!hasSurroundMask && tfEstimate < .2f)
        {
            float amount = .0000031f * Math.Max(0, Math.Min(32000, 96000 - bitrate));
            float tvbrFactor = temporalVbr * amount;
            target += (int)(tvbrFactor * target);
        }

        // Don't allow more than doubling the rate.
        return Math.Min(2 * baseTarget, target);
    }

    // The surround masking part of celt_encode_with_ec: how much masking takes place between surround channels.
    private void SurroundMasking(int c, out float surroundMasking, out float surroundTrim)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        float[] energyMask = _energyMask!;
        Span<float> surroundDynalloc = _surroundDynalloc;
        float maskAvg = 0;
        float diff = 0;
        int count = 0;
        int maskEnd = Math.Max(2, _lastCodedBands);
        for (int ch = 0; ch < c; ch++)
        {
            for (int i = 0; i < maskEnd; i++)
            {
                float mask = MathF.Max(MathF.Min(energyMask[(Nb * ch) + i], .25f), -2.0f);
                if (mask > 0)
                {
                    mask = .5f * mask;
                }

                maskAvg += mask * (eBands[i + 1] - eBands[i]);
                count += eBands[i + 1] - eBands[i];
                diff += mask * (1 + (2 * i) - maskEnd);
            }
        }

        maskAvg /= count;
        maskAvg += .2f;
        diff = diff * 6 / (c * (maskEnd - 1) * (maskEnd + 1) * maskEnd);
        // Again, being conservative.
        diff = .5f * diff;
        diff = MathF.Max(MathF.Min(diff, .031f), -.031f);
        // Find the band that's in the middle of the coded spectrum.
        int midband;
        for (midband = 0; eBands[midband + 1] < eBands[maskEnd] / 2; midband++)
        {
        }

        int countDynalloc = 0;
        for (int i = 0; i < maskEnd; i++)
        {
            float lin = maskAvg + (diff * (i - midband));
            float unmask = c == 2 ? MathF.Max(energyMask[i], energyMask[Nb + i]) : energyMask[i];
            unmask = MathF.Min(unmask, .0f);
            unmask -= lin;
            if (unmask > .25f)
            {
                surroundDynalloc[i] = unmask - .25f;
                countDynalloc++;
            }
        }

        if (countDynalloc >= 3)
        {
            // If we need dynalloc in many bands, it's probably because our initial masking rate was too low.
            maskAvg += .25f;
            if (maskAvg > 0)
            {
                // Something went really wrong in the original calculations, disabling masking.
                maskAvg = 0;
                diff = 0;
                surroundDynalloc[..maskEnd].Clear();
            }
            else
            {
                for (int i = 0; i < maskEnd; i++)
                {
                    surroundDynalloc[i] = MathF.Max(0, surroundDynalloc[i] - .25f);
                }
            }
        }

        maskAvg += .2f;
        // Convert to 1/64th units used for the trim.
        surroundTrim = 64 * diff;
        surroundMasking = maskAvg;
    }
}
