// Translated from libopus 1.5.2 celt/celt_decoder.c and celt/celt.c (float build, static 48 kHz mode, no DRED or deep PLC).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// libopus's CELT decoder (celt_decode_with_ec) for the static 48 kHz, 960-sample mode, with its packet loss concealment.
/// Produces float samples in [-1, 1] scale, as opus_decode_float does.
/// </summary>
internal sealed class CeltDecoder
{
    private const int DecodeBufferSize = 2048;
    private const int PlcPitchLagMax = 720;
    private const int PlcPitchLagMin = 100;
    private const int Overlap = CeltTables.Overlap;
    private const int Nb = CeltTables.NbEBands;
    private const int BitRes = CeltTables.BitRes;
    private const int ChannelStride = DecodeBufferSize + Overlap;
    private const float VerySmall = 1e-30f;
    private const int OpusBadArg = -1;
    private const int OpusInternalError = -3;

    private readonly int _channels;
    private readonly int _downsample = 1;
    private readonly OpusRangeDecoder _ownDecoder = new();
    private readonly CeltBands _bands = new();
    private readonly float[] _x = new float[2 * 960];
    private readonly float[] _freq = new float[960];

    // Everything below is cleared on a reset.
    private uint _rng;
    private bool _error;
    private int _lastPitchIndex;
    private int _lossDuration;
    private bool _skipPlc;
    private int _postfilterPeriod;
    private int _postfilterPeriodOld;
    private float _postfilterGain;
    private float _postfilterGainOld;
    private int _postfilterTapset;
    private int _postfilterTapsetOld;
    private bool _prefilterAndFold;
    private readonly float[] _preemphMemD = new float[2];
    private readonly float[] _decodeMem;
    private readonly float[] _lpc;
    private readonly float[] _oldBandE = new float[2 * Nb];
    private readonly float[] _oldLogE = new float[2 * Nb];
    private readonly float[] _oldLogE2 = new float[2 * Nb];
    private readonly float[] _backgroundLogE = new float[2 * Nb];

    /// <summary>celt_decoder_init(st, 48000, channels) with the static mode.</summary>
    public CeltDecoder(int channels)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        _channels = channels;
        StreamChannels = channels;
        StartBand = 0;
        EndBand = CeltTables.EffEBands;
        DisablePhaseInversion = channels == 1;
        _decodeMem = new float[channels * ChannelStride];
        _lpc = new float[channels * CeltLpc.Order];
        Reset();
    }

    /// <summary>Gets the 120-sample MDCT window of the static mode (what celt_mode->window points to).</summary>
    public static ReadOnlySpan<float> Window => CeltTables.Window120;

    /// <summary>Gets or sets the number of coded channels (CELT_SET_CHANNELS).</summary>
    public int StreamChannels { get; set; }

    /// <summary>Gets or sets the first coded band (CELT_SET_START_BAND): 17 in hybrid mode, 0 otherwise.</summary>
    public int StartBand { get; set; }

    /// <summary>Gets or sets the end band (CELT_SET_END_BAND): 13, 17, 19 or 21 by bandwidth.</summary>
    public int EndBand { get; set; }

    /// <summary>Gets or sets whether intensity stereo phase inversion is off (OPUS_SET_PHASE_INVERSION_DISABLED).</summary>
    public bool DisablePhaseInversion { get; set; }

    /// <summary>Gets the range coder state after the last frame (OPUS_GET_FINAL_RANGE).</summary>
    public uint FinalRange => _rng;

    /// <summary>Gets the postfilter period (OPUS_GET_PITCH).</summary>
    public int LastPitch => _postfilterPeriod;

    /// <summary>Gets whether a frame read past its end since the last call, and clears it (CELT_GET_AND_CLEAR_ERROR).</summary>
    public bool GetAndClearError()
    {
        bool error = _error;
        _error = false;
        return error;
    }

    /// <summary>OPUS_RESET_STATE.</summary>
    public void Reset()
    {
        _rng = 0;
        _error = false;
        _lastPitchIndex = 0;
        _lossDuration = 0;
        _postfilterPeriod = 0;
        _postfilterPeriodOld = 0;
        _postfilterGain = 0;
        _postfilterGainOld = 0;
        _postfilterTapset = 0;
        _postfilterTapsetOld = 0;
        _prefilterAndFold = false;
        Array.Clear(_preemphMemD);
        Array.Clear(_decodeMem);
        Array.Clear(_lpc);
        Array.Clear(_oldBandE);
        Array.Clear(_backgroundLogE);
        Array.Fill(_oldLogE, -28f);
        Array.Fill(_oldLogE2, -28f);
        _skipPlc = true;
    }

    /// <summary>
    /// celt_decode_with_ec: decodes one frame of <paramref name="frameSize"/> samples per channel (120, 240, 480 or 960) into
    /// interleaved <paramref name="pcm"/>, or conceals a lost frame when <paramref name="data"/> is null or shorter than 2
    /// bytes. <paramref name="rangeDecoder"/> is the decoder shared with SILK in hybrid mode, or null to start one on the data.
    /// Returns the samples decoded per channel or a negative OPUS_* error code.
    /// </summary>
    public int Decode(byte[]? data, int offset, int length, Span<float> pcm, int frameSize, OpusRangeDecoder? rangeDecoder, bool accumulate)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int cc = _channels;
        int c = StreamChannels;
        int start = StartBand;
        int end = EndBand;
        frameSize *= _downsample;

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
        if (length < 0 || length > 1275 || pcm.Length < frameSize / _downsample * cc)
        {
            return OpusBadArg;
        }

        int n = m * CeltTables.ShortMdctSize;
        int outSyn = DecodeBufferSize - n;
        int effEnd = Math.Min(end, CeltTables.EffEBands);

        if (data is null || length <= 1)
        {
            DecodeLost(n, lm);
            Deemphasis(pcm, n, outSyn, accumulate);
            return frameSize / _downsample;
        }

        // Check if there are at least two packets received consecutively before turning on the pitch-based PLC.
        if (_lossDuration == 0)
        {
            _skipPlc = false;
        }

        OpusRangeDecoder dec = rangeDecoder ?? _ownDecoder;
        if (rangeDecoder is null)
        {
            dec.Init(data, offset, length);
        }

        Span<float> oldBandE = _oldBandE;
        Span<float> oldLogE = _oldLogE;
        Span<float> oldLogE2 = _oldLogE2;
        Span<float> backgroundLogE = _backgroundLogE;
        if (c == 1)
        {
            for (int i = 0; i < Nb; i++)
            {
                oldBandE[i] = Max(oldBandE[i], oldBandE[Nb + i]);
            }
        }

        int totalBits = length * 8;
        int tell = dec.Tell();
        bool silence;
        if (tell >= totalBits)
        {
            silence = true;
        }
        else if (tell == 1)
        {
            silence = dec.DecodeBitLogp(15);
        }
        else
        {
            silence = false;
        }

        if (silence)
        {
            // Pretend we've read all the remaining bits.
            tell = length * 8;
            dec.AddUsedBits(tell - dec.Tell());
        }

        float postfilterGain = 0;
        int postfilterPitch = 0;
        int postfilterTapset = 0;
        if (start == 0 && tell + 16 <= totalBits)
        {
            if (dec.DecodeBitLogp(1))
            {
                int octave = (int)dec.DecodeUint(6);
                postfilterPitch = (16 << octave) + (int)dec.DecodeBits(4 + octave) - 1;
                int qg = (int)dec.DecodeBits(3);
                if (dec.Tell() + 2 <= totalBits)
                {
                    postfilterTapset = dec.DecodeIcdf(CeltTables.TapsetIcdf, 2);
                }

                postfilterGain = .09375f * (qg + 1);
            }

            tell = dec.Tell();
        }

        bool isTransient;
        if (lm > 0 && tell + 3 <= totalBits)
        {
            isTransient = dec.DecodeBitLogp(3);
            tell = dec.Tell();
        }
        else
        {
            isTransient = false;
        }

        bool shortBlocks = isTransient;

        // Decode the global flags (first symbols in the stream).
        bool intraEner = tell + 3 <= totalBits && dec.DecodeBitLogp(3);
        // If recovering from packet loss, make sure the energy prediction is safe to reduce the risk of loud artifacts.
        if (!intraEner && _lossDuration != 0)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                float safety = 0;
                int missing = Math.Min(10, _lossDuration >> lm);
                if (lm == 0)
                {
                    safety = 1.5f;
                }
                else if (lm == 1)
                {
                    safety = .5f;
                }

                for (int i = start; i < end; i++)
                {
                    int k = (ch * Nb) + i;
                    if (oldBandE[k] < Max(oldLogE[k], oldLogE2[k]))
                    {
                        // If energy is going down already, continue the trend.
                        float e0 = oldBandE[k];
                        float e1 = oldLogE[k];
                        float e2 = oldLogE2[k];
                        float slope = Max(e1 - e0, .5f * (e2 - e0));
                        e0 -= Max(0, (1 + missing) * slope);
                        oldBandE[k] = Max(-20f, e0);
                    }
                    else
                    {
                        // Otherwise take the min of the last frames.
                        oldBandE[k] = Min(Min(oldBandE[k], oldLogE[k]), oldLogE2[k]);
                    }

                    // Shorter frames have more natural fluctuations -- play it safe.
                    oldBandE[k] -= safety;
                }
            }
        }

        // Get band energies.
        CeltEnergy.UnquantCoarseEnergy(start, end, oldBandE, intraEner, dec, c, lm);

        Span<int> tfRes = stackalloc int[Nb];
        TfDecode(start, end, isTransient, tfRes, lm, dec);

        tell = dec.Tell();
        int spreadDecision = CeltTables.SpreadNormal;
        if (tell + 4 <= totalBits)
        {
            spreadDecision = dec.DecodeIcdf(CeltTables.SpreadIcdf, 5);
        }

        Span<int> cap = stackalloc int[Nb];
        CeltTables.InitCaps(cap, lm, c);

        Span<int> offsets = stackalloc int[Nb];
        offsets.Clear();
        int dynallocLogp = 6;
        totalBits <<= BitRes;
        tell = (int)dec.TellFrac();
        for (int i = start; i < end; i++)
        {
            int width = c * (eBands[i + 1] - eBands[i]) << lm;
            // quanta is 6 bits, but no more than 1 bit/sample and no less than 1/8 bit/sample.
            int quanta = Math.Min(width << BitRes, Math.Max(6 << BitRes, width));
            int dynallocLoopLogp = dynallocLogp;
            int boost = 0;
            while (tell + (dynallocLoopLogp << BitRes) < totalBits && boost < cap[i])
            {
                bool flag = dec.DecodeBitLogp(dynallocLoopLogp);
                tell = (int)dec.TellFrac();
                if (!flag)
                {
                    break;
                }

                boost += quanta;
                totalBits -= quanta;
                dynallocLoopLogp = 1;
            }

            offsets[i] = boost;
            // Making dynalloc more likely.
            if (boost > 0)
            {
                dynallocLogp = Math.Max(2, dynallocLogp - 1);
            }
        }

        Span<int> fineQuant = stackalloc int[Nb];
        int allocTrim = tell + (6 << BitRes) <= totalBits ? dec.DecodeIcdf(CeltTables.TrimIcdf, 7) : 5;

        int bits = (length * 8 << BitRes) - (int)dec.TellFrac() - 1;
        int antiCollapseRsv = isTransient && lm >= 2 && bits >= (lm + 2) << BitRes ? 1 << BitRes : 0;
        bits -= antiCollapseRsv;

        Span<int> pulses = stackalloc int[Nb];
        Span<int> finePriority = stackalloc int[Nb];
        fineQuant.Clear();
        pulses.Clear();
        finePriority.Clear();
        int codedBands = CeltRate.ComputeAllocation(start, end, offsets, cap, allocTrim, out int intensity, out bool dualStereo, bits, out int balance,
            pulses, fineQuant, finePriority, c, lm, dec);

        CeltEnergy.UnquantFineEnergy(start, end, oldBandE, fineQuant, dec, c);

        for (int ch = 0; ch < cc; ch++)
        {
            Span<float> mem = DecodeMem(ch);
            mem.Slice(n, DecodeBufferSize - n + Overlap).CopyTo(mem);
        }

        // Decode fixed codebook.
        Span<byte> collapseMasks = stackalloc byte[c * Nb];
        collapseMasks.Clear();
        Span<float> x = _x.AsSpan(0, c * n);
        x.Clear();
        _bands.QuantAllBands(start, end, x[..n], c == 2 ? x.Slice(n, n) : default, c == 2, collapseMasks, pulses, shortBlocks, spreadDecision,
            dualStereo, intensity, tfRes, (length * (8 << BitRes)) - antiCollapseRsv, balance, dec, lm, codedBands, ref _rng, DisablePhaseInversion);

        bool antiCollapseOn = false;
        if (antiCollapseRsv > 0)
        {
            antiCollapseOn = dec.DecodeBits(1) != 0;
        }

        CeltEnergy.UnquantEnergyFinalise(start, end, oldBandE, fineQuant, finePriority, (length * 8) - dec.Tell(), dec, c);

        if (antiCollapseOn)
        {
            CeltBands.AntiCollapse(x, collapseMasks, lm, c, n, start, end, oldBandE, oldLogE, oldLogE2, pulses, _rng);
        }

        if (silence)
        {
            oldBandE[..(c * Nb)].Fill(-28f);
        }

        if (_prefilterAndFold)
        {
            PrefilterAndFold(n);
        }

        Synthesis(x, oldBandE, start, effEnd, c, cc, isTransient, lm, silence, n);

        ReadOnlySpan<float> window = CeltTables.Window120;
        for (int ch = 0; ch < cc; ch++)
        {
            _postfilterPeriod = Math.Max(_postfilterPeriod, CeltTables.CombFilterMinPeriod);
            _postfilterPeriodOld = Math.Max(_postfilterPeriodOld, CeltTables.CombFilterMinPeriod);
            Span<float> mem = DecodeMem(ch);
            CombFilter(mem, outSyn, mem, outSyn, _postfilterPeriodOld, _postfilterPeriod, CeltTables.ShortMdctSize, _postfilterGainOld, _postfilterGain,
                _postfilterTapsetOld, _postfilterTapset, window, Overlap);
            if (lm != 0)
            {
                int o = outSyn + CeltTables.ShortMdctSize;
                CombFilter(mem, o, mem, o, _postfilterPeriod, postfilterPitch, n - CeltTables.ShortMdctSize, _postfilterGain, postfilterGain,
                    _postfilterTapset, postfilterTapset, window, Overlap);
            }
        }

        _postfilterPeriodOld = _postfilterPeriod;
        _postfilterGainOld = _postfilterGain;
        _postfilterTapsetOld = _postfilterTapset;
        _postfilterPeriod = postfilterPitch;
        _postfilterGain = postfilterGain;
        _postfilterTapset = postfilterTapset;
        if (lm != 0)
        {
            _postfilterPeriodOld = _postfilterPeriod;
            _postfilterGainOld = _postfilterGain;
            _postfilterTapsetOld = _postfilterTapset;
        }

        if (c == 1)
        {
            oldBandE[..Nb].CopyTo(oldBandE[Nb..]);
        }

        if (!isTransient)
        {
            oldLogE.CopyTo(oldLogE2);
            oldBandE.CopyTo(oldLogE);
        }
        else
        {
            for (int i = 0; i < 2 * Nb; i++)
            {
                oldLogE[i] = Min(oldLogE[i], oldBandE[i]);
            }
        }

        // In normal circumstances, the noise floor may only increase by up to 2.4 dB/second, but in DTX the update packet gets
        // the weight of all the missing packets.
        float maxBackgroundIncrease = Math.Min(160, _lossDuration + m) * 0.001f;
        for (int i = 0; i < 2 * Nb; i++)
        {
            backgroundLogE[i] = Min(backgroundLogE[i] + maxBackgroundIncrease, oldBandE[i]);
        }

        // In case start or end were to change.
        for (int ch = 0; ch < 2; ch++)
        {
            for (int i = 0; i < start; i++)
            {
                oldBandE[(ch * Nb) + i] = 0;
                oldLogE[(ch * Nb) + i] = oldLogE2[(ch * Nb) + i] = -28f;
            }

            for (int i = end; i < Nb; i++)
            {
                oldBandE[(ch * Nb) + i] = 0;
                oldLogE[(ch * Nb) + i] = oldLogE2[(ch * Nb) + i] = -28f;
            }
        }

        _rng = dec.Range;

        Deemphasis(pcm, n, outSyn, accumulate);
        _lossDuration = 0;
        _prefilterAndFold = false;
        if (dec.Tell() > 8 * length)
        {
            return OpusInternalError;
        }

        if (dec.Error)
        {
            _error = true;
        }

        return frameSize / _downsample;
    }

    /// <summary>
    /// comb_filter: the pitch pre/postfilter from <paramref name="x"/> at <paramref name="xOffset"/> (with the history before it)
    /// to <paramref name="y"/> at <paramref name="yOffset"/>, cross-fading from the old filter to the new over the overlap. The
    /// two may be the same buffer, as in the decoder's in-place postfilter.
    /// </summary>
    public static void CombFilter(Span<float> y, int yOffset, ReadOnlySpan<float> x, int xOffset, int t0, int t1, int n, float g0, float g1, int tapset0,
        int tapset1, ReadOnlySpan<float> window, int overlap)
    {
        ReadOnlySpan<float> gains =
        [
            0.3066406250f, 0.2170410156f, 0.1296386719f,
            0.4638671875f, 0.2680664062f, 0f,
            0.7998046875f, 0.1000976562f, 0f,
        ];
        if (g0 == 0 && g1 == 0)
        {
            // In place this copies onto itself.
            x.Slice(xOffset, n).CopyTo(y[yOffset..]);

            return;
        }

        // When the gain is zero, T0 and/or T1 is set to zero. They must be at least 2 to avoid processing garbage data.
        t0 = Math.Max(t0, CeltTables.CombFilterMinPeriod);
        t1 = Math.Max(t1, CeltTables.CombFilterMinPeriod);
        float g00 = g0 * gains[tapset0 * 3];
        float g01 = g0 * gains[(tapset0 * 3) + 1];
        float g02 = g0 * gains[(tapset0 * 3) + 2];
        float g10 = g1 * gains[tapset1 * 3];
        float g11 = g1 * gains[(tapset1 * 3) + 1];
        float g12 = g1 * gains[(tapset1 * 3) + 2];
        float x1 = x[xOffset - t1 + 1];
        float x2 = x[xOffset - t1];
        float x3 = x[xOffset - t1 - 1];
        float x4 = x[xOffset - t1 - 2];
        // If the filter didn't change, we don't need the overlap.
        if (g0 == g1 && t0 == t1 && tapset0 == tapset1)
        {
            overlap = 0;
        }

        int i;
        for (i = 0; i < overlap; i++)
        {
            int xi = xOffset + i;
            float x0 = x[xi - t1 + 2];
            float f = window[i] * window[i];
            y[yOffset + i] = x[xi]
                + ((1f - f) * g00 * x[xi - t0])
                + ((1f - f) * g01 * (x[xi - t0 + 1] + x[xi - t0 - 1]))
                + ((1f - f) * g02 * (x[xi - t0 + 2] + x[xi - t0 - 2]))
                + (f * g10 * x2)
                + (f * g11 * (x1 + x3))
                + (f * g12 * (x0 + x4));
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = x0;
        }

        if (g1 == 0)
        {
            x.Slice(xOffset + overlap, n - overlap).CopyTo(y[(yOffset + overlap)..]);

            return;
        }

        // The part with the constant filter, in the order of comb_filter_const_sse (the x86-64 reference build).
        for (; i < n; i++)
        {
            int xi = xOffset + i;
            float yi = x[xi] + (g10 * x[xi - t1]);
            float yi2 = (g11 * (x[xi - t1 + 1] + x[xi - t1 - 1])) + (g12 * (x[xi - t1 + 2] + x[xi - t1 - 2]));
            y[yOffset + i] = yi + yi2;
        }
    }

    private static float Min(float a, float b) => a < b ? a : b;

    private static float Max(float a, float b) => a > b ? a : b;

    private static void TfDecode(int start, int end, bool isTransient, Span<int> tfRes, int lm, OpusRangeDecoder dec)
    {
        ReadOnlySpan<sbyte> table = CeltTables.TfSelectTable.Slice(lm * 8, 8);
        int t = isTransient ? 1 : 0;
        uint budget = dec.Storage * 8;
        uint tell = (uint)dec.Tell();
        int logp = isTransient ? 2 : 4;
        int tfSelectRsv = lm > 0 && tell + logp + 1 <= budget ? 1 : 0;
        budget -= (uint)tfSelectRsv;
        int tfChanged = 0;
        int curr = 0;
        for (int i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                curr ^= dec.DecodeBitLogp(logp) ? 1 : 0;
                tell = (uint)dec.Tell();
                tfChanged |= curr;
            }

            tfRes[i] = curr;
            logp = isTransient ? 4 : 5;
        }

        int tfSelect = 0;
        if (tfSelectRsv != 0 && table[(4 * t) + tfChanged] != table[(4 * t) + 2 + tfChanged])
        {
            tfSelect = dec.DecodeBitLogp(1) ? 1 : 0;
        }

        for (int i = start; i < end; i++)
        {
            tfRes[i] = table[(4 * t) + (2 * tfSelect) + tfRes[i]];
        }
    }

    private Span<float> DecodeMem(int channel) => _decodeMem.AsSpan(channel * ChannelStride, ChannelStride);

    // deemphasis (downsample 1, no custom modes), from out_syn at outSyn in each channel's decode memory.
    private void Deemphasis(Span<float> pcm, int n, int outSyn, bool accumulate)
    {
        const float Coef0 = CeltTables.Preemph0;
        const float Scale = 1 / 32768f;
        int cc = _channels;
        for (int c = 0; c < cc; c++)
        {
            ReadOnlySpan<float> x = DecodeMem(c).Slice(outSyn, n);
            float m = _preemphMemD[c];
            if (accumulate)
            {
                for (int j = 0; j < n; j++)
                {
                    float tmp = x[j] + VerySmall + m;
                    m = Coef0 * tmp;
                    pcm[(j * cc) + c] += tmp * Scale;
                }
            }
            else
            {
                for (int j = 0; j < n; j++)
                {
                    float tmp = x[j] + VerySmall + m;
                    m = Coef0 * tmp;
                    pcm[(j * cc) + c] = tmp * Scale;
                }
            }

            _preemphMemD[c] = m;
        }
    }

    // celt_synthesis.
    private void Synthesis(ReadOnlySpan<float> x, ReadOnlySpan<float> oldBandE, int start, int effEnd, int c, int cc, bool isTransient, int lm, bool silence,
        int n)
    {
        ReadOnlySpan<float> window = CeltTables.Window120;
        int m = 1 << lm;
        int outSyn = DecodeBufferSize - n;
        Span<float> freq = _freq.AsSpan(0, n);
        int b;
        int nb;
        int shift;
        if (isTransient)
        {
            b = m;
            nb = CeltTables.ShortMdctSize;
            shift = CeltTables.MaxLM;
        }
        else
        {
            b = 1;
            nb = CeltTables.ShortMdctSize << lm;
            shift = CeltTables.MaxLM - lm;
        }

        if (cc == 2 && c == 1)
        {
            // Copying a mono stream to two channels.
            CeltBands.DenormaliseBands(x, freq, oldBandE, start, effEnd, m, _downsample, silence);
            // Store a temporary copy in the output buffer because the IMDCT destroys its input.
            Span<float> freq2 = DecodeMem(1).Slice(outSyn + (Overlap / 2), n);
            freq.CopyTo(freq2);
            Span<float> out0 = DecodeMem(0);
            for (int k = 0; k < b; k++)
            {
                CeltMdct.Backward(freq2[k..], out0[(outSyn + (nb * k))..], window, Overlap, shift, b);
            }

            Span<float> out1 = DecodeMem(1);
            for (int k = 0; k < b; k++)
            {
                CeltMdct.Backward(freq[k..], out1[(outSyn + (nb * k))..], window, Overlap, shift, b);
            }
        }
        else if (cc == 1 && c == 2)
        {
            // Downmixing a stereo stream to mono.
            Span<float> out0 = DecodeMem(0);
            Span<float> freq2 = out0.Slice(outSyn + (Overlap / 2), n);
            CeltBands.DenormaliseBands(x, freq, oldBandE, start, effEnd, m, _downsample, silence);
            // Use the output buffer as temp array before downmixing.
            CeltBands.DenormaliseBands(x[n..], freq2, oldBandE[Nb..], start, effEnd, m, _downsample, silence);
            for (int i = 0; i < n; i++)
            {
                freq[i] = (.5f * freq[i]) + (.5f * freq2[i]);
            }

            for (int k = 0; k < b; k++)
            {
                CeltMdct.Backward(freq[k..], out0[(outSyn + (nb * k))..], window, Overlap, shift, b);
            }
        }
        else
        {
            // Normal case (mono or stereo).
            for (int ch = 0; ch < cc; ch++)
            {
                CeltBands.DenormaliseBands(x[(ch * n)..], freq, oldBandE[(ch * Nb)..], start, effEnd, m, _downsample, silence);
                Span<float> outc = DecodeMem(ch);
                for (int k = 0; k < b; k++)
                {
                    CeltMdct.Backward(freq[k..], outc[(outSyn + (nb * k))..], window, Overlap, shift, b);
                }
            }
        }
    }

    // prefilter_and_fold.
    private void PrefilterAndFold(int n)
    {
        ReadOnlySpan<float> window = CeltTables.Window120;
        Span<float> etmp = stackalloc float[Overlap];
        for (int c = 0; c < _channels; c++)
        {
            Span<float> mem = DecodeMem(c);
            // Apply the pre-filter to the MDCT overlap for the next frame because the post-filter will be re-applied in the
            // decoder after the MDCT overlap.
            CombFilter(etmp, 0, mem, DecodeBufferSize - n, _postfilterPeriodOld, _postfilterPeriod, Overlap, -_postfilterGainOld, -_postfilterGain,
                _postfilterTapsetOld, _postfilterTapset, default, 0);

            // Simulate TDAC on the concealed audio so that it blends with the MDCT of the next frame.
            for (int i = 0; i < Overlap / 2; i++)
            {
                mem[DecodeBufferSize - n + i] = (window[i] * etmp[Overlap - 1 - i]) + (window[Overlap - i - 1] * etmp[i]);
            }
        }
    }

    // celt_plc_pitch_search.
    private int PlcPitchSearch(int c)
    {
        float[] lpPitchBuf = new float[DecodeBufferSize >> 1];
        CeltLpc.PitchDownsample(DecodeMem(0), c == 2 ? DecodeMem(1) : default, lpPitchBuf, DecodeBufferSize, c);
        int pitchIndex = CeltLpc.PitchSearch(lpPitchBuf.AsSpan(PlcPitchLagMax >> 1), lpPitchBuf, DecodeBufferSize - PlcPitchLagMax,
            PlcPitchLagMax - PlcPitchLagMin);
        return PlcPitchLagMax - pitchIndex;
    }

    // celt_decode_lost, without the deep PLC.
    private void DecodeLost(int n, int lm)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int c = _channels;
        int outSyn = DecodeBufferSize - n;
        Span<float> oldBandE = _oldBandE;
        int lossDuration = _lossDuration;
        int start = StartBand;
        bool noiseBased = lossDuration >= 40 || start != 0 || _skipPlc;
        if (noiseBased)
        {
            // Noise-based PLC/CNG.
            int end = EndBand;
            int effEnd = Math.Max(start, Math.Min(end, CeltTables.EffEBands));
            Span<float> xBuffer = _x.AsSpan(0, c * n);
            xBuffer.Clear();
            for (int ch = 0; ch < c; ch++)
            {
                Span<float> mem = DecodeMem(ch);
                mem.Slice(n, DecodeBufferSize - n + Overlap).CopyTo(mem);
            }

            if (_prefilterAndFold)
            {
                PrefilterAndFold(n);
            }

            // Energy decay.
            float decay = lossDuration == 0 ? 1.5f : .5f;
            for (int ch = 0; ch < c; ch++)
            {
                for (int i = start; i < end; i++)
                {
                    int k = (ch * Nb) + i;
                    oldBandE[k] = Max(_backgroundLogE[k], oldBandE[k] - decay);
                }
            }

            uint seed = _rng;
            for (int ch = 0; ch < c; ch++)
            {
                for (int i = start; i < effEnd; i++)
                {
                    int boffs = (n * ch) + (eBands[i] << lm);
                    int blen = (eBands[i + 1] - eBands[i]) << lm;
                    for (int j = 0; j < blen; j++)
                    {
                        seed = CeltMath.LcgRand(seed);
                        xBuffer[boffs + j] = (int)seed >> 20;
                    }

                    CeltVq.RenormaliseVector(xBuffer.Slice(boffs, blen), blen, 1f);
                }
            }

            _rng = seed;

            Synthesis(xBuffer, oldBandE, start, effEnd, c, c, false, lm, false, n);
            _prefilterAndFold = false;
            // Skip regular PLC until we get two consecutive packets.
            _skipPlc = true;
        }
        else
        {
            // Pitch-based PLC.
            ReadOnlySpan<float> window = CeltTables.Window120;
            const int Ord = CeltLpc.Order;
            const int MaxPeriod = CeltTables.MaxPeriod;
            float fade = 1f;
            int pitchIndex;
            if (lossDuration == 0)
            {
                _lastPitchIndex = pitchIndex = PlcPitchSearch(c);
            }
            else
            {
                pitchIndex = _lastPitchIndex;
                fade = .8f;
            }

            // We want the excitation for 2 pitch periods in order to look for a decaying signal, but we can't get more than MAX_PERIOD.
            int excLength = Math.Min(2 * pitchIndex, MaxPeriod);
            float[] excBuffer = new float[MaxPeriod + Ord];
            float[] firTmp = new float[excLength];
            Span<float> exc = excBuffer.AsSpan(Ord);
            Span<float> ac = stackalloc float[Ord + 1];
            Span<float> lpcMem = stackalloc float[Ord];
            for (int ch = 0; ch < c; ch++)
            {
                Span<float> buf = DecodeMem(ch);
                Span<float> lpc = _lpc.AsSpan(ch * Ord, Ord);
                for (int i = 0; i < MaxPeriod + Ord; i++)
                {
                    excBuffer[i] = buf[DecodeBufferSize - MaxPeriod - Ord + i];
                }

                if (lossDuration == 0)
                {
                    // Compute LPC coefficients for the last MAX_PERIOD samples before the first loss so we can work in the
                    // excitation-filter domain.
                    CeltLpc.Autocorr(exc, ac, window, Overlap, Ord, MaxPeriod);
                    // Add a noise floor of -40 dB.
                    ac[0] *= 1.0001f;
                    // Use lag windowing to stabilize the Levinson-Durbin recursion.
                    for (int i = 1; i <= Ord; i++)
                    {
                        ac[i] -= ac[i] * (0.008f * 0.008f) * i * i;
                    }

                    CeltLpc.Lpc(lpc, ac, Ord);
                }

                // Compute the excitation for excLength samples before the loss. The copy is needed because celt_fir() cannot
                // filter in place.
                CeltLpc.Fir(excBuffer, Ord + MaxPeriod - excLength, lpc, firTmp, excLength, Ord);
                firTmp.CopyTo(exc[(MaxPeriod - excLength)..]);

                // Check if the waveform is decaying, and if so how fast, to avoid adding energy when concealing in a segment with
                // decaying energy.
                float decay;
                {
                    float e1 = 1, e2 = 1;
                    int decayLength = excLength >> 1;
                    for (int i = 0; i < decayLength; i++)
                    {
                        float e = exc[MaxPeriod - decayLength + i];
                        e1 += e * e;
                        e = exc[MaxPeriod - (2 * decayLength) + i];
                        e2 += e * e;
                    }

                    e1 = Min(e1, e2);
                    decay = CeltMath.Sqrt(e1 / e2);
                }

                // Move the decoder memory one frame to the left to give us room to add the data for the new frame. The overlap
                // that extends past the end of the buffer is ignored because it isn't used.
                buf.Slice(n, DecodeBufferSize - n).CopyTo(buf);

                // Extrapolate from the end of the excitation with a period of pitchIndex, scaling down each period by an
                // additional factor of decay.
                int extrapolationOffset = MaxPeriod - pitchIndex;
                // Extrapolate enough samples to cover a complete MDCT window (including overlap/2 samples on both sides).
                int extrapolationLen = n + Overlap;
                // We also apply fading if this is not the first loss.
                float attenuation = fade * decay;
                float s1 = 0;
                for (int i = 0, j = 0; i < extrapolationLen; i++, j++)
                {
                    if (j >= pitchIndex)
                    {
                        j -= pitchIndex;
                        attenuation *= decay;
                    }

                    buf[DecodeBufferSize - n + i] = attenuation * exc[extrapolationOffset + j];
                    // Compute the energy of the previously decoded signal whose excitation we're copying.
                    float tmp = buf[DecodeBufferSize - MaxPeriod - n + extrapolationOffset + j];
                    s1 += tmp * tmp;
                }

                // Copy the last decoded samples (prior to the overlap region) to synthesis filter memory so we can have a
                // continuous signal.
                for (int i = 0; i < Ord; i++)
                {
                    lpcMem[i] = buf[DecodeBufferSize - n - 1 - i];
                }

                // Apply the synthesis filter to convert the excitation back into the signal domain.
                Span<float> ext = buf.Slice(DecodeBufferSize - n, extrapolationLen);
                CeltLpc.Iir(ext, lpc, ext, extrapolationLen, Ord, lpcMem);

                // Check if the synthesis energy is higher than expected, which can happen with the signal changes during our
                // window. If so, attenuate.
                float s2 = 0;
                for (int i = 0; i < extrapolationLen; i++)
                {
                    float tmp = ext[i];
                    s2 += tmp * tmp;
                }

                // This checks for an "explosion" in the synthesis, and catches NaNs in the output of the IIR filter.
                if (!(s1 > 0.2f * s2))
                {
                    ext.Clear();
                }
                else if (s1 < s2)
                {
                    float ratio = CeltMath.Sqrt((s1 + 1) / (s2 + 1));
                    for (int i = 0; i < Overlap; i++)
                    {
                        float tmpG = 1f - (window[i] * (1f - ratio));
                        ext[i] = tmpG * ext[i];
                    }

                    for (int i = Overlap; i < extrapolationLen; i++)
                    {
                        ext[i] = ratio * ext[i];
                    }
                }
            }

            _prefilterAndFold = true;
        }

        // Saturate to something large to avoid wrap-around.
        _lossDuration = Math.Min(10000, lossDuration + (1 << lm));
    }
}
