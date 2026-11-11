// Parts translated to C# from oxideav-aac (MIT), modified: the window-sequence state machine, the analysis windows, the short-window
// grouping rule (decide_short_grouping), the intensity-stereo test (is_decide), the noise-likeness test (is_noise_like) and the
// raw data block layout of its src/encoder.rs. The psychoacoustic model, the attack detector with one block of look-ahead, the
// mid/side choice by perceptual entropy, the per-band scalefactors from the masking thresholds and the rate control are
// Egami's own. Checked against ISO/IEC 14496-3 4.4.2, 4.5.2, 4.6.2 and 4.6.11.

using Egami.Video.Audio.Transforms;

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// Encodes planar float audio into AAC LC raw data blocks (ISO/IEC 14496-3 4.4.2.1), 1024 samples per channel each. Every block
/// is windowed and transformed with block switching (a long window, or eight short ones around an attack), its bands get
/// masking thresholds from <see cref="AacPsychoacoustics"/>, and TNS, mid/side, intensity stereo and noise substitution are
/// chosen per window and band. The quantizer gives each band the scalefactor at which its quantization noise meets the band's
/// threshold times one factor for the whole block; the rate control searches that factor so that the block fits the bits its
/// perceptual entropy and the bit reservoir allow (average bit rate), or holds it fixed (variable bit rate).
/// The encoder looks one block ahead for attacks, so a block comes out after the block that follows it is pushed.
/// </summary>
internal sealed class AacFrameEncoder
{
    /// <summary>The samples per channel in a block.</summary>
    public const int FrameLength = 1024;

    private const int OnlyLong = 0;
    private const int LongStart = 1;
    private const int EightShort = 2;
    private const int LongStop = 3;
    private const int MaxBands = 8 * 16;
    private const int LfeLines = 12;
    private const float AttackRatio = 10;
    private const float AttackFloor = 128f * 100 * 100;
    private const double GroupMergeDistance = 1.386;
    private const float RoundingOffset = 0.4054f;

    /// <summary>The fixed lambda of each variable-bit-rate quality level, 1 to 5.</summary>
    private static ReadOnlySpan<int> QualityLambda => [0, 22, 17, 12, 7, 1];
    private const int MinLambda = -120;
    private const int MaxLambda = 160;

    private static readonly byte[] s_sfLengths = AacTables.ScaleFactorLengths.ToArray();
    private static readonly uint[] s_sfCodes = AacTables.ScaleFactorCodes.ToArray();

    private readonly int _rateIndex;
    private readonly int _channelConfiguration;
    private readonly (int Type, int First, int Second)[] _elements;
    private readonly ChannelState[] _channels;
    private readonly AacPsychoacoustics _psy;
    private readonly short[] _longOffsets;
    private readonly short[] _shortOffsets;
    private readonly int _longBandsTotal;
    private readonly int _shortBandsTotal;
    private readonly int _longBands;
    private readonly int _shortBands;
    private readonly int _lfeBands;
    private readonly int _cutoff;
    private readonly int _cutoffShort;
    private readonly int _pnsLine;
    private readonly int _intensityLine;
    private readonly bool _pns;
    private readonly bool _intensity;
    private readonly Mdct _longMdct = new(2 * FrameLength);
    private readonly Mdct _shortMdct = new(256);
    private readonly float[] _longRise = MdctWindows.Sine(2 * FrameLength);
    private readonly float[] _shortRise = MdctWindows.Sine(256);
    private readonly float[] _block = new float[2 * FrameLength];
    private readonly int[] _sectionScratch = new int[13 * (MaxBands + 1)];
    private readonly float[] _windowEnergy = new float[16];
    private readonly float[] _windowThreshold = new float[16];
    private readonly AacBitWriter _writer;
    private readonly byte[] _groupLength = new byte[8];

    private readonly double _averageBits;
    private readonly int _maxBits;
    private readonly double _reservoirSize;
    private readonly int? _fixedLambda;
    private double _reservoir;
    private double _meanEntropy;
    private int _lambdaHint;

    private int _sequence;
    private int _previousSequence;
    private int _groups;
    private int _pushed;

    /// <summary>Initializes an encoder.</summary>
    /// <param name="sampleRate">The sampling rate, one of the 13 AAC rates.</param>
    /// <param name="channelConfiguration">The channel configuration, 1 to 7; the input planes come in Egami.Video's order for it.</param>
    /// <param name="bitrate">The average bit rate in bit/s for all channels, used when <paramref name="quality"/> is null.</param>
    /// <param name="quality">The variable-bit-rate quality, 1 to 5, or <see langword="null"/> for the average bit rate.</param>
    /// <param name="bandwidth">The highest frequency coded in hertz.</param>
    public AacFrameEncoder(int sampleRate, int channelConfiguration, int bitrate, int? quality, int bandwidth)
    {
        _rateIndex = AacConfig.RateIndex(sampleRate);
        if (_rateIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "AAC has no band tables for this rate.");
        }

        _channelConfiguration = channelConfiguration;
        _elements = channelConfiguration switch
        {
            1 => [(0, 0, 0)],
            2 => [(1, 0, 1)],
            3 => [(0, 2, 0), (1, 0, 1)],
            4 => [(0, 2, 0), (1, 0, 1), (0, 3, 0)],
            5 => [(0, 2, 0), (1, 0, 1), (1, 3, 4)],
            6 => [(0, 2, 0), (1, 0, 1), (1, 4, 5), (3, 3, 0)],
            7 => [(0, 2, 0), (1, 0, 1), (1, 6, 7), (1, 4, 5), (3, 3, 0)],
            _ => throw new ArgumentOutOfRangeException(nameof(channelConfiguration)),
        };

        ChannelCount = channelConfiguration == 7 ? 8 : channelConfiguration < 6 ? channelConfiguration : 6;
        _channels = new ChannelState[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            _channels[c] = new ChannelState();
        }

        foreach ((int type, int first, int _) in _elements)
        {
            if (type == 3)
            {
                _channels[first].IsLfe = true;
            }
        }

        _psy = new AacPsychoacoustics(_rateIndex, sampleRate);
        _longOffsets = _psy.LongOffsets.ToArray();
        _shortOffsets = _psy.ShortOffsets.ToArray();
        _longBandsTotal = _longOffsets.Length - 1;
        _shortBandsTotal = _shortOffsets.Length - 1;

        double nyquist = sampleRate / 2.0;
        _cutoff = (int)Math.Min(FrameLength, Math.Ceiling(Math.Min(bandwidth, nyquist) / nyquist * FrameLength));
        _cutoffShort = (int)Math.Min(128, Math.Ceiling(_cutoff / 8.0));
        _longBands = BandsBelow(_longOffsets, _cutoff);
        _shortBands = BandsBelow(_shortOffsets, _cutoffShort);
        _lfeBands = BandsBelow(_longOffsets, LfeLines);

        // Per full-bandwidth channel: noise substitution below about 48 kbit/s from 5 kHz, intensity stereo below about 40 from 6.
        int perChannel = bitrate / Math.Max(1, ChannelCount - (channelConfiguration >= 6 ? 1 : 0));
        _pns = quality is null ? perChannel < 48_000 : quality <= 2;
        _intensity = quality is null && perChannel < 40_000;
        _pnsLine = (int)(5000 / nyquist * FrameLength);
        _intensityLine = (int)(6000 / nyquist * FrameLength);

        _averageBits = (double)bitrate * FrameLength / sampleRate;
        _maxBits = 6144 * ChannelCount;
        _reservoirSize = Math.Max(0, _maxBits - _averageBits);
        if (quality is int q)
        {
            _fixedLambda = QualityLambda[Math.Clamp(q, 1, 5)];
        }

        _writer = new AacBitWriter((_maxBits / 8) + 64);
        AudioSpecificConfig = AacConfig.Create(AacConfig.LowComplexity, _rateIndex, channelConfiguration);
    }

    /// <summary>Gets the channels the encoder takes.</summary>
    public int ChannelCount { get; }

    /// <summary>Gets the AudioSpecificConfig of the stream.</summary>
    public byte[] AudioSpecificConfig { get; }

    /// <summary>
    /// Pushes the next 1024 samples of every channel (silence after the end) and, once a block has its look-ahead, encodes it.
    /// </summary>
    /// <param name="planes">One plane per channel, scaled to plus or minus 1, or <see langword="null"/> for silence.</param>
    /// <param name="samples">The samples in each plane, at most 1024; the rest of the block is silence.</param>
    /// <param name="block">The encoded raw data block, valid until the next call, or empty while the look-ahead fills.</param>
    /// <returns>Whether a block was encoded.</returns>
    public bool Push(float[][]? planes, int samples, out ReadOnlySpan<byte> block)
    {
        for (int c = 0; c < ChannelCount; c++)
        {
            ChannelState ch = _channels[c];
            Span<float> buffer = ch.Samples;
            buffer[FrameLength..].CopyTo(buffer);
            Span<float> next = buffer[(2 * FrameLength)..];
            ReadOnlySpan<float> input = planes is null ? default : planes[c];
            int n = Math.Min(Math.Min(input.Length, samples), FrameLength);
            for (int i = 0; i < n; i++)
            {
                next[i] = input[i] * 32768f;
            }

            next[n..].Clear();
            ch.AttackPrevious = ch.AttackCurrent;
            ch.AttackCurrent = ch.AttackNext;
            ch.AttackNext = ch.IsLfe ? 0 : DetectAttacks(ch, next);
        }

        if (++_pushed < 2)
        {
            block = default;
            return false;
        }

        block = EncodeBlock();
        return true;
    }

    private static int BandsBelow(ReadOnlySpan<short> offsets, int line)
    {
        int bands = 0;
        while (bands < offsets.Length - 1 && offsets[bands] < line)
        {
            bands++;
        }

        return bands;
    }

    /// <summary>The 128-sample parts of a new block whose high-passed energy jumps tenfold over the eight parts before it.</summary>
    private static int DetectAttacks(ChannelState ch, ReadOnlySpan<float> next)
    {
        int mask = 0;
        float last = ch.LastSample;
        for (int s = 0; s < 8; s++)
        {
            float e = 0;
            for (int i = s * 128; i < (s + 1) * 128; i++)
            {
                float d = next[i] - last;
                last = next[i];
                e += d * d;
            }

            float before = 0;
            foreach (float h in ch.PartEnergy)
            {
                before = Math.Max(before, h);
            }

            if (e > AttackFloor && e > AttackRatio * before)
            {
                mask |= 1 << s;
            }

            ch.PartEnergy[ch.PartIndex] = e;
            ch.PartIndex = (ch.PartIndex + 1) & 7;
        }

        ch.LastSample = last;
        return mask;
    }

    private ReadOnlySpan<byte> EncodeBlock()
    {
        ChooseSequence();
        bool shortWindows = _sequence == EightShort;

        float entropy = 0;
        foreach (ChannelState ch in _channels)
        {
            Transform(ch);
        }

        if (shortWindows)
        {
            Group();
        }
        else
        {
            _groups = 1;
            _groupLength[0] = 1;
        }

        foreach (ChannelState ch in _channels)
        {
            entropy += Analyze(ch);
        }

        foreach ((int type, int first, int second) in _elements)
        {
            if (type == 1)
            {
                ChooseStereo(_channels[first], _channels[second]);
            }
        }

        foreach (ChannelState ch in _channels)
        {
            Measure(ch);
        }

        int lambda;
        if (_fixedLambda is int fixedLambda)
        {
            lambda = Quantize(fixedLambda) <= _maxBits ? fixedLambda : Search(fixedLambda + 1, _maxBits);
        }
        else
        {
            _meanEntropy = _meanEntropy <= 0 ? entropy : (0.9 * _meanEntropy) + (0.1 * entropy);
            double ratio = Math.Clamp((entropy + 200) / (_meanEntropy + 200), 0.5, 2.5);
            double desired = (_averageBits * Math.Pow(ratio, 0.6)) + (0.2 * (_reservoir - (0.5 * _reservoirSize)));
            double high = Math.Min(_averageBits + _reservoir, _maxBits);
            int budget = (int)Math.Clamp(desired, Math.Min(0.5 * _averageBits, high), high);
            lambda = Search(_lambdaHint, budget);
            _lambdaHint = lambda;
        }

        int bits = Quantize(lambda);
        _reservoir = Math.Clamp(_reservoir + _averageBits - bits, 0, _reservoirSize);
        _previousSequence = _sequence;
        return Write();
    }

    /// <summary>
    /// The window sequence of the block that spans the last two pushed blocks of input but one (4.6.11.3.2): eight short windows
    /// when an attack falls where they sit, a start or stop window on either side of them, else one long window.
    /// </summary>
    private void ChooseSequence()
    {
        // The short windows of a block cover the last 576 samples of its first half and the first 576 of its second.
        bool now = false, next = false;
        foreach (ChannelState ch in _channels)
        {
            now |= (ch.AttackCurrent & 0x1F) != 0 || (ch.AttackPrevious & 0xF0) != 0;
            next |= (ch.AttackNext & 0x1F) != 0 || (ch.AttackCurrent & 0xF0) != 0;
        }

        bool previousShort = _previousSequence == EightShort;
        if (now || _previousSequence == LongStart || (previousShort && next))
        {
            _sequence = EightShort;
        }
        else if (next)
        {
            _sequence = LongStart;
        }
        else
        {
            _sequence = previousShort ? LongStop : OnlyLong;
        }
    }

    /// <summary>Windows and transforms a channel's block into its spectrum, scaled so that the decoder's 1/32768 gives it back.</summary>
    private void Transform(ChannelState ch)
    {
        int sequence = ch.IsLfe ? OnlyLong : _sequence;
        ch.Sequence = sequence;
        ReadOnlySpan<float> x = ch.Samples.AsSpan(0, 2 * FrameLength);
        Span<float> spectrum = ch.Spectrum;
        if (sequence == EightShort)
        {
            Span<float> z = _block.AsSpan(0, 256);
            for (int w = 0; w < 8; w++)
            {
                ReadOnlySpan<float> part = x.Slice(448 + (128 * w), 256);
                for (int n = 0; n < 128; n++)
                {
                    z[n] = part[n] * _shortRise[n];
                    z[128 + n] = part[128 + n] * _shortRise[127 - n];
                }

                _shortMdct.Forward(z, spectrum.Slice(128 * w, 128));
                spectrum.Slice((128 * w) + _cutoffShort, 128 - _cutoffShort).Clear();
            }

            return;
        }

        Span<float> block = _block;
        for (int n = 0; n < FrameLength; n++)
        {
            float rise = sequence == LongStop ? (n < 448 ? 0 : n < 576 ? _shortRise[n - 448] : 1) : _longRise[n];
            float fall = sequence == LongStart ? (n < 448 ? 1 : n < 576 ? _shortRise[575 - n] : 0) : _longRise[FrameLength - 1 - n];
            block[n] = x[n] * rise;
            block[FrameLength + n] = x[FrameLength + n] * fall;
        }

        _longMdct.Forward(block, spectrum);
        spectrum[(ch.IsLfe ? LfeLines : _cutoff)..].Clear();
    }

    /// <summary>
    /// Groups the eight short windows (4.5.2.3.4): a window joins the group before it when its band energies differ from the
    /// previous window's by at most a factor of four on average (the mean absolute log ratio up to ln 4).
    /// </summary>
    private void Group()
    {
        Span<double> energy = stackalloc double[8 * 16];
        energy.Clear();
        foreach (ChannelState ch in _channels)
        {
            if (ch.IsLfe)
            {
                continue;
            }

            for (int w = 0; w < 8; w++)
            {
                for (int b = 0; b < _shortBandsTotal; b++)
                {
                    double e = 0;
                    for (int k = _shortOffsets[b]; k < _shortOffsets[b + 1]; k++)
                    {
                        double v = ch.Spectrum[(128 * w) + k];
                        e += v * v;
                    }

                    energy[(w * 16) + b] += e;
                }
            }
        }

        _groups = 1;
        _groupLength[0] = 1;
        for (int w = 1; w < 8; w++)
        {
            double distance = 0;
            for (int b = 0; b < _shortBandsTotal; b++)
            {
                distance += Math.Abs(Math.Log((energy[((w - 1) * 16) + b] + 1) / (energy[(w * 16) + b] + 1)));
            }

            if (distance / _shortBandsTotal <= GroupMergeDistance)
            {
                _groupLength[_groups - 1]++;
            }
            else
            {
                _groupLength[_groups++] = 1;
            }
        }
    }

    /// <summary>
    /// The psychoacoustic thresholds of a channel's bands, TNS, and the spectrum in coding order (4.6.3.3: within a group, band
    /// by band, each window's lines of the band in turn). Returns the channel's perceptual entropy.
    /// </summary>
    private float Analyze(ChannelState ch)
    {
        bool shortWindows = ch.Sequence == EightShort;
        int groups = shortWindows ? _groups : 1;
        int bands = ch.IsLfe ? _lfeBands : shortWindows ? _shortBands : _longBands;
        ch.MaxSfb = bands;
        ch.Groups = groups;
        ch.BandCount = groups * bands;
        ch.Partner = null;
        Array.Clear(ch.Special);
        Array.Clear(ch.MsUsed);
        float entropy = 0;
        ch.Tns.Clear();
        if (!shortWindows)
        {
            entropy = _psy.Analyze(ch.Spectrum, bands, ch.Energy, ch.Threshold, ch.PreviousThreshold.AsSpan(0, bands));
            if (!ch.IsLfe && AacTnsEncoder.Permits(ch.Samples.AsSpan(0, 2 * FrameLength)))
            {
                int top = Math.Min(AacTables.TnsMaxBandsLong[_rateIndex], bands);
                ch.Tns.Analyze(0, ch.Spectrum, _longOffsets[top], _longBandsTotal, shortWindow: false);
            }

            ch.Spectrum.AsSpan(0, FrameLength).CopyTo(ch.Coded);
            for (int b = 0; b <= bands; b++)
            {
                ch.BandStart[b] = _longOffsets[b];
            }

            return entropy;
        }

        // Short windows: thresholds per window, a group band holding its quietest window's threshold once per window.
        Array.Clear(ch.PreviousThreshold);
        int window = 0, at = 0, index = 0;
        int tnsTop = _shortOffsets[Math.Min(AacTables.TnsMaxBandsShort[_rateIndex], bands)];
        for (int g = 0; g < groups; g++)
        {
            int length = _groupLength[g];
            for (int b = 0; b < bands; b++)
            {
                ch.Threshold[(g * bands) + b] = float.MaxValue;
            }

            for (int w = window; w < window + length; w++)
            {
                Span<float> spectrum = ch.Spectrum.AsSpan(128 * w, 128);
                entropy += _psy.Analyze(spectrum, bands, _windowEnergy, _windowThreshold, default);
                for (int b = 0; b < bands; b++)
                {
                    ref float t = ref ch.Threshold[(g * bands) + b];
                    t = Math.Min(t, _windowThreshold[b]);
                }

                if (AacTnsEncoder.Permits(ch.Samples.AsSpan(448 + (128 * w), 256)))
                {
                    ch.Tns.Analyze(w, spectrum, tnsTop, _shortBandsTotal, shortWindow: true);
                }
            }

            for (int b = 0; b < bands; b++)
            {
                ch.Threshold[(g * bands) + b] *= length;
                ch.BandStart[index++] = at;
                for (int w = window; w < window + length; w++)
                {
                    int width = _shortOffsets[b + 1] - _shortOffsets[b];
                    ch.Spectrum.AsSpan((128 * w) + _shortOffsets[b], width).CopyTo(ch.Coded.AsSpan(at));
                    at += width;
                }
            }

            // The group's lines above max_sfb are zero and not coded.
            at += length * (128 - _shortOffsets[bands]);
            window += length;
        }

        ch.BandStart[index] = at;
        return entropy;
    }

    /// <summary>
    /// Mid/side and intensity stereo for a channel pair (4.6.8): a band is coded as mid and side when that lowers its perceptual
    /// entropy at thresholds that keep the decoded left and right under theirs; at low rates a band high up whose channels are
    /// almost fully correlated sends one channel and the level ratio instead.
    /// </summary>
    private void ChooseStereo(ChannelState left, ChannelState right)
    {
        bool longWindows = left.Sequence != EightShort;
        left.Partner = right;
        right.Partner = left;
        for (int i = 0; i < left.BandCount; i++)
        {
            left.MsUsed[i] = false;
            int start = left.BandStart[i], end = BandEnd(left, i);
            if (end <= start)
            {
                continue;
            }

            double el = 0, er = 0, em = 0, es = 0, dot = 0;
            for (int k = start; k < end; k++)
            {
                double l = left.Coded[k], r = right.Coded[k];
                el += l * l;
                er += r * r;
                dot += l * r;
                double m = 0.5 * (l + r), s = 0.5 * (l - r);
                em += m * m;
                es += s * s;
            }

            if (el <= 0 && er <= 0)
            {
                continue;
            }

            double tl = Math.Max(left.Threshold[i], 1e-9), tr = Math.Max(right.Threshold[i], 1e-9);
            if (_intensity && longWindows && start >= _intensityLine && el > 0 && er > 0
                && Math.Abs(dot) >= 0.95 * Math.Sqrt(el * er) && left.Tns.Order[0] == 0 && right.Tns.Order[0] == 0)
            {
                double position = Math.Round(2 * Math.Log2(el / er));
                if (Math.Abs(position) <= 80)
                {
                    right.Special[i] = dot >= 0 ? AacSpectrumCoder.IntensityBook : AacSpectrumCoder.IntensityBook2;
                    right.Position[i] = (int)position;
                    continue;
                }
            }

            double tms = 0.5 * Math.Min(tl, tr);
            double width = end - start;
            double lr = width * (Math.Log2(1 + (el / tl)) + Math.Log2(1 + (er / tr)));
            double ms = width * (Math.Log2(1 + (em / tms)) + Math.Log2(1 + (es / tms)));
            if (ms < lr)
            {
                left.MsUsed[i] = true;
                left.Threshold[i] = (float)tms;
                right.Threshold[i] = (float)tms;
                for (int k = start; k < end; k++)
                {
                    float l = left.Coded[k], r = right.Coded[k];
                    left.Coded[k] = 0.5f * (l + r);
                    right.Coded[k] = 0.5f * (l - r);
                }
            }
        }
    }

    /// <summary>Per band of the coded spectrum: energy, peak, the sum of square roots that sets its scalefactor, and |x|^(3/4).</summary>
    private void Measure(ChannelState ch)
    {
        bool longWindows = ch.Sequence != EightShort;
        for (int i = 0; i < ch.BandCount; i++)
        {
            int start = ch.BandStart[i], end = BandEnd(ch, i);

            float e = 0, peak = 0, roots = 0, l1 = 0;
            for (int k = start; k < end; k++)
            {
                float a = Math.Abs(ch.Coded[k]);
                float root = MathF.Sqrt(a);
                ch.Pow[k] = MathF.Sqrt(a * root);
                e += a * a;
                l1 += a;
                peak = Math.Max(peak, a);
                roots += root;
            }

            ch.Energy[i] = e;
            ch.Peak[i] = peak;
            ch.Roots[i] = roots;
            ch.MinSf[i] = peak > 0 ? (int)Math.Ceiling(100 + (4 * Math.Log2(peak)) - 69.33) : 0;

            // Noise substitution needs a long block, a wide band high enough up, no TNS, no mid/side, and noise-like lines.
            int width = end - start;
            ch.NoiseLike[i] = _pns && longWindows && !ch.IsLfe && start >= _pnsLine && width >= 8 && !ch.Tns.Present
                && ch.Special[i] == 0 && !(ch.Partner is { } partner && (ch.MsUsed[i] || partner.MsUsed[i])) && e > 0 && l1 * l1 > 0.5f * width * e;
        }
    }

    /// <summary>The end of band <paramref name="i"/> in coding order, which in a short block is the start of the next band only within a group.</summary>
    private int BandEnd(ChannelState ch, int i)
    {
        if (ch.Sequence != EightShort)
        {
            return ch.BandStart[i + 1];
        }

        int g = i / ch.MaxSfb, b = i % ch.MaxSfb;
        int length = _groupLength[g];
        return ch.BandStart[i] + (length * (_shortOffsets[b + 1] - _shortOffsets[b]));
    }

    /// <summary>
    /// The least threshold factor whose block fits <paramref name="budget"/> bits, searched from <paramref name="hint"/> by
    /// galloping until the boundary is bracketed and then bisecting; bits fall as the factor rises.
    /// </summary>
    private int Search(int hint, int budget)
    {
        int start = Math.Clamp(hint, MinLambda, MaxLambda);
        int? fit = null, fail = null;
        if (Quantize(start) <= budget)
        {
            fit = start;
            for (int step = 1; fit > MinLambda; step *= 2)
            {
                int probe = Math.Max(fit.Value - step, MinLambda);
                if (Quantize(probe) <= budget)
                {
                    fit = probe;
                }
                else
                {
                    fail = probe;
                    break;
                }
            }
        }
        else
        {
            fail = start;
            for (int step = 1; fail < MaxLambda; step *= 2)
            {
                int probe = Math.Min(fail.Value + step, MaxLambda);
                if (Quantize(probe) <= budget)
                {
                    fit = probe;
                    break;
                }

                fail = probe;
            }
        }

        while (fit is int lo && fail is int hi && lo - hi > 1)
        {
            int mid = hi + ((lo - hi) / 2);
            if (Quantize(mid) <= budget)
            {
                fit = mid;
            }
            else
            {
                fail = mid;
            }
        }

        return fit ?? MaxLambda;
    }

    /// <summary>Quantizes every channel at threshold factor 2^(lambda / 4) and returns the block's bits.</summary>
    private int Quantize(int lambda)
    {
        float factor = MathF.Pow(2, lambda / 4f);
        int bits = 3; // END
        foreach ((int type, int first, int second) in _elements)
        {
            bits += 3 + 4;
            if (type == 1)
            {
                ChannelState left = _channels[first], right = _channels[second];
                bits += 1 + IcsInfoBits(left) + 2;
                if (MsMaskPresent(left) == 1)
                {
                    bits += left.BandCount;
                }

                bits += QuantizeChannel(left, factor) + QuantizeChannel(right, factor);
            }
            else
            {
                ChannelState ch = _channels[first];
                bits += IcsInfoBits(ch) + QuantizeChannel(ch, factor);
            }
        }

        return (bits + 7) & ~7;
    }

    private static int IcsInfoBits(ChannelState ch) => ch.Sequence == EightShort ? 15 : 11;

    private static int MsMaskPresent(ChannelState left)
    {
        bool any = false, all = true;
        for (int i = 0; i < left.BandCount; i++)
        {
            any |= left.MsUsed[i];
            all &= left.MsUsed[i];
        }

        return all && left.BandCount > 0 ? 2 : any ? 1 : 0;
    }

    /// <summary>
    /// Quantizes one channel: each band gets the scalefactor at which its noise meets its threshold times
    /// <paramref name="factor"/>, from the noise of rounding |x|^(3/4) to integers, about (4/27) g^(3/2) sum |x|^(1/2) for a
    /// step g = 2^((sf - 100) / 4); bands under the threshold are left out, and noise-like ones may become noise. Returns the
    /// bits of the channel stream without its ics_info.
    /// </summary>
    private int QuantizeChannel(ChannelState ch, float factor)
    {
        int count = ch.BandCount;
        Span<byte> books = ch.Books.AsSpan(0, count);
        Span<int> factors = ch.Factors.AsSpan(0, count);
        Array.Clear(ch.Quant);
        for (int i = 0; i < count; i++)
        {
            ch.WasZero[i] = false;
            if (ch.Special[i] != 0)
            {
                books[i] = (byte)ch.Special[i];
                continue;
            }

            float e = ch.Energy[i];
            float t = ch.Threshold[i] * factor;
            if (e <= t || e <= 0)
            {
                books[i] = 0;
                continue;
            }

            if (ch.NoiseLike[i] && e < 4 * t)
            {
                books[i] = AacSpectrumCoder.NoiseBook;
                factors[i] = (int)Math.Round(2 * Math.Log2(e));
                continue;
            }

            int sf = (int)Math.Round(100 + (8.0 / 3 * Math.Log2(27 * t / (4 * Math.Max(ch.Roots[i], 1e-9f)))));
            factors[i] = Math.Clamp(Math.Max(sf, ch.MinSf[i]), 0, 255);
            books[i] = 1;
        }

        // Keep neighbouring scalefactors within 60 of each other (4.6.2.3.2): a step down is held to 60, which only refines.
        int previous = -1;
        for (int i = 0; i < count; i++)
        {
            if (books[i] is 0 or >= AacSpectrumCoder.NoiseBook)
            {
                continue;
            }

            if (previous >= 0)
            {
                factors[i] = Math.Clamp(factors[i], previous - 60, previous + 60);
                factors[i] = Math.Max(factors[i], Math.Min(ch.MinSf[i], previous + 60));
            }

            previous = factors[i];
        }

        // Quantize; a band that rounds to zeros drops out unless the scalefactor chain needs it.
        for (int i = 0; i < count; i++)
        {
            if (books[i] is 0 or >= AacSpectrumCoder.NoiseBook)
            {
                continue;
            }

            int start = ch.BandStart[i], end = BandEnd(ch, i);
            float inverse = MathF.Pow(2, -0.1875f * (factors[i] - 100));
            int peak = 0;
            for (int k = start; k < end; k++)
            {
                float v = (ch.Pow[k] * inverse) + RoundingOffset;
                int q = v < 8191 ? (int)v : 8191;
                peak |= q;
                ch.Quant[k] = ch.Coded[k] < 0 ? -q : q;
            }

            if (peak == 0)
            {
                ch.WasZero[i] = true;
            }
        }

        previous = -1;
        for (int i = 0; i < count; i++)
        {
            if (books[i] is 0 or >= AacSpectrumCoder.NoiseBook)
            {
                continue;
            }

            if (ch.WasZero[i])
            {
                int nextSf = -1;
                for (int j = i + 1; j < count; j++)
                {
                    if (books[j] is not (0 or >= AacSpectrumCoder.NoiseBook) && !ch.WasZero[j])
                    {
                        nextSf = factors[j];
                        break;
                    }
                }

                if (previous < 0 || nextSf < 0 || Math.Abs(nextSf - previous) <= 60)
                {
                    books[i] = ch.NoiseLike[i] ? (byte)AacSpectrumCoder.NoiseBook : (byte)0;
                    if (books[i] == AacSpectrumCoder.NoiseBook)
                    {
                        factors[i] = (int)Math.Round(2 * Math.Log2(ch.Energy[i]));
                    }

                    continue;
                }

                ch.WasZero[i] = false; // kept as a coded band of zeros
            }

            previous = factors[i];
        }

        // Sections; zero bands may join coded sections, at the scalefactor before them.
        int bits = 8 + 1 + 1 + 1; // global_gain, pulse, tns and gain-control flags
        if (ch.Tns.Present)
        {
            bits += ch.Tns.Bits(ch.Sequence == EightShort);
        }

        bool shortWindows = ch.Sequence == EightShort;
        for (int i = 0; i < count; i++)
        {
            ch.Joined[i] = books[i] == 0;
        }

        Span<int> starts = stackalloc int[ch.MaxSfb + 1];
        for (int g = 0; g < ch.Groups; g++)
        {
            int first = g * ch.MaxSfb;
            for (int b = 0; b < ch.MaxSfb; b++)
            {
                starts[b] = ch.BandStart[first + b];
            }

            starts[ch.MaxSfb] = ch.MaxSfb > 0 ? BandEnd(ch, first + ch.MaxSfb - 1) : 0;
            bits += ChooseSections(ch, first, starts, shortWindows);
        }

        for (int i = 0; i < count; i++)
        {
            ch.Joined[i] &= books[i] != 0;
        }

        return bits + ScaleFactorBits(ch, write: null);
    }

    private int ChooseSections(ChannelState ch, int first, ReadOnlySpan<int> starts, bool shortWindows)
    {
        if (ch.MaxSfb == 0)
        {
            return 0;
        }

        // ChooseSections takes lines relative to the group's first band.
        int origin = starts[0];
        Span<int> relative = stackalloc int[starts.Length];
        for (int b = 0; b < starts.Length; b++)
        {
            relative[b] = starts[b] - origin;
        }

        return AacSpectrumCoder.ChooseSections(
            ch.Quant.AsSpan(origin, relative[^1]),
            relative,
            ch.Books.AsSpan(first, ch.MaxSfb),
            shortWindows,
            _sectionScratch);
    }

    /// <summary>The bits of scale_factor_data (Table 4.53, 4.6.2.3.2), and when a writer is given, the data itself.</summary>
    private static int ScaleFactorBits(ChannelState ch, AacBitWriter? write)
    {
        int count = ch.BandCount;
        int globalGain = GlobalGain(ch);
        int factor = globalGain, noise = globalGain - 90 - 256, position = 0;
        bool firstNoise = true;
        int bits = 0;
        for (int i = 0; i < count; i++)
        {
            int cb = ch.Books[i];
            int delta;
            if (cb == 0)
            {
                continue;
            }
            else if (cb >= AacSpectrumCoder.IntensityBook2)
            {
                delta = Math.Clamp(ch.Position[i] - position, -60, 60);
                position += delta;
            }
            else if (cb == AacSpectrumCoder.NoiseBook)
            {
                if (firstNoise)
                {
                    firstNoise = false;
                    int v = Math.Clamp(ch.Factors[i] - noise, 0, 511);
                    noise += v;
                    bits += 9;
                    write?.Write((uint)v, 9);
                    continue;
                }

                delta = Math.Clamp(ch.Factors[i] - noise, -60, 60);
                noise += delta;
            }
            else
            {
                if (ch.Joined[i])
                {
                    ch.Factors[i] = factor;
                }

                delta = ch.Factors[i] - factor;
                factor = ch.Factors[i];
            }

            bits += s_sfLengths[delta + 60];
            write?.Write(s_sfCodes[delta + 60], s_sfLengths[delta + 60]);
        }

        return bits;
    }

    /// <summary>The global gain: the first coded band's scalefactor, or 100 when no band is coded.</summary>
    private static int GlobalGain(ChannelState ch)
    {
        for (int i = 0; i < ch.BandCount; i++)
        {
            if (ch.Books[i] is > 0 and < AacSpectrumCoder.NoiseBook && !ch.Joined[i])
            {
                return ch.Factors[i];
            }
        }

        return 100;
    }

    private ReadOnlySpan<byte> Write()
    {
        AacBitWriter w = _writer;
        w.Reset();
        Span<int> tags = stackalloc int[4];
        foreach ((int type, int first, int second) in _elements)
        {
            w.Write((uint)type, 3);
            w.Write((uint)tags[type]++, 4);
            if (type == 1)
            {
                ChannelState left = _channels[first], right = _channels[second];
                w.Write(1, 1); // common_window
                WriteIcsInfo(w, left);
                int mask = MsMaskPresent(left);
                w.Write((uint)mask, 2);
                if (mask == 1)
                {
                    for (int i = 0; i < left.BandCount; i++)
                    {
                        w.WriteBit(left.MsUsed[i]);
                    }
                }

                WriteChannel(w, left, commonWindow: true);
                WriteChannel(w, right, commonWindow: true);
            }
            else
            {
                WriteChannel(w, _channels[first], commonWindow: false);
            }
        }

        w.Write(7, 3); // END
        return w.Finish();
    }

    private void WriteIcsInfo(AacBitWriter w, ChannelState ch)
    {
        w.Write(0, 1); // ics_reserved_bit
        w.Write((uint)ch.Sequence, 2);
        w.Write(0, 1); // window_shape: sine
        if (ch.Sequence == EightShort)
        {
            w.Write((uint)ch.MaxSfb, 4);
            int grouping = 0, window = 0;
            for (int g = 0; g < _groups; g++)
            {
                for (int j = 0; j < _groupLength[g]; j++, window++)
                {
                    if (j > 0)
                    {
                        grouping |= 1 << (7 - window);
                    }
                }
            }

            w.Write((uint)grouping, 7);
        }
        else
        {
            w.Write((uint)ch.MaxSfb, 6);
            w.Write(0, 1); // predictor_data_present
        }
    }

    private void WriteChannel(AacBitWriter w, ChannelState ch, bool commonWindow)
    {
        bool shortWindows = ch.Sequence == EightShort;
        w.Write((uint)GlobalGain(ch), 8);
        if (!commonWindow)
        {
            WriteIcsInfo(w, ch);
        }

        for (int g = 0; g < ch.Groups; g++)
        {
            AacSpectrumCoder.WriteSections(w, ch.Books.AsSpan(g * ch.MaxSfb, ch.MaxSfb), shortWindows);
        }

        ScaleFactorBits(ch, w);
        w.Write(0, 1); // pulse_data_present
        w.WriteBit(ch.Tns.Present);
        if (ch.Tns.Present)
        {
            ch.Tns.Write(w, shortWindows);
        }

        w.Write(0, 1); // gain_control_data_present
        for (int i = 0; i < ch.BandCount; i++)
        {
            int cb = ch.Books[i];
            if (cb is > 0 and < AacSpectrumCoder.NoiseBook)
            {
                AacSpectrumCoder.Write(w, cb, ch.Quant.AsSpan(ch.BandStart[i], BandEnd(ch, i) - ch.BandStart[i]));
            }
        }
    }

    /// <summary>One channel's input, analysis and quantization state.</summary>
    private sealed class ChannelState
    {
        public readonly float[] Samples = new float[3 * FrameLength];
        public readonly float[] PartEnergy = new float[8];
        public readonly float[] Spectrum = new float[FrameLength];
        public readonly float[] Coded = new float[FrameLength];
        public readonly float[] Pow = new float[FrameLength];
        public readonly int[] Quant = new int[FrameLength];
        public readonly float[] PreviousThreshold = new float[64];
        public readonly int[] BandStart = new int[MaxBands + 1];
        public readonly float[] Energy = new float[MaxBands];
        public readonly float[] Threshold = new float[MaxBands];
        public readonly float[] Peak = new float[MaxBands];
        public readonly float[] Roots = new float[MaxBands];
        public readonly int[] MinSf = new int[MaxBands];
        public readonly bool[] NoiseLike = new bool[MaxBands];
        public readonly bool[] WasZero = new bool[MaxBands];
        public readonly bool[] Joined = new bool[MaxBands];
        public readonly int[] Special = new int[MaxBands];
        public readonly bool[] MsUsed = new bool[MaxBands];
        public readonly int[] Position = new int[MaxBands];
        public readonly byte[] Books = new byte[MaxBands];
        public readonly int[] Factors = new int[MaxBands];
        public readonly AacTnsEncoder Tns = new();
        public bool IsLfe;
        public int AttackPrevious;
        public int AttackCurrent;
        public int AttackNext;
        public int PartIndex;
        public float LastSample;
        public int Sequence;
        public int Groups;
        public int MaxSfb;
        public int BandCount;
        public ChannelState? Partner;
    }
}
