// Translated from libopus 1.5.2 src/opus_encoder.c (float build, without DRED, OSCE or the integer API).
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// OpusEncoder: one Opus stream of one or two channels. For every frame it picks SILK, CELT or hybrid coding, the coded bandwidth
/// and the stream channels from the bitrate and the look-ahead analysis, runs the SILK and CELT encoders on one range coder and
/// writes the redundant CELT frames that smooth a switch between modes.
/// </summary>
internal sealed class OpusEncoder
{
    public const int Auto = -1000;
    public const int BitrateMax = -1;
    public const int ApplicationVoip = 2048;
    public const int ApplicationAudio = 2049;
    public const int ApplicationRestrictedLowDelay = 2051;
    public const int SignalVoice = 3001;
    public const int SignalMusic = 3002;
    public const int BandwidthNarrowband = 1101;
    public const int BandwidthMediumband = 1102;
    public const int BandwidthWideband = 1103;
    public const int BandwidthSuperwideband = 1104;
    public const int BandwidthFullband = 1105;
    public const int FramesizeArg = 5000;
    public const int Framesize2_5Ms = 5001;
    public const int Framesize40Ms = 5005;
    public const int Framesize120Ms = 5009;
    public const int ModeSilkOnly = 1000;
    public const int ModeHybrid = 1001;
    public const int ModeCeltOnly = 1002;

    private const int MaxEncoderBuffer = 480;
    private const float PseudoSnrThreshold = 316.23f;
    private const float VerySmall = 1e-30f;
    private const float Epsilon = 1e-15f;
    private const int Overlap = 120;

    private static ReadOnlySpan<int> MonoVoiceBandwidthThresholds => [9000, 700, 9000, 700, 13500, 1000, 14000, 2000];
    private static ReadOnlySpan<int> MonoMusicBandwidthThresholds => [9000, 700, 9000, 700, 11000, 1000, 12000, 2000];
    private static ReadOnlySpan<int> StereoVoiceBandwidthThresholds => [9000, 700, 9000, 700, 13500, 1000, 14000, 2000];
    private static ReadOnlySpan<int> StereoMusicBandwidthThresholds => [9000, 700, 9000, 700, 11000, 1000, 12000, 2000];
    private const int StereoVoiceThreshold = 19000;
    private const int StereoMusicThreshold = 17000;
    // mode_thresholds[2][2] = {{64000, 10000}, {44000, 10000}}.
    private static ReadOnlySpan<int> ModeThresholds => [64000, 10000, 44000, 10000];
    private static ReadOnlySpan<int> FecThresholds => [12000, 1000, 14000, 1000, 16000, 1000, 20000, 1000, 22000, 1000];

    private static ReadOnlySpan<int> HybridRateTable =>
    [
        0, 0, 0, 0, 0,
        12000, 10000, 10000, 11000, 11000,
        16000, 13500, 13500, 15000, 15000,
        20000, 16000, 16000, 18000, 18000,
        24000, 18000, 18000, 21000, 21000,
        32000, 22000, 22000, 28000, 28000,
        64000, 38000, 38000, 50000, 50000,
    ];

    private readonly SilkEncoder _silk = new();
    private readonly CeltEncoder _celt;
    private readonly SilkEncControl _silkMode = new();
    private readonly SilkEncControl _dummyControl = new();
    private readonly OpusTonalityAnalysis _analysis;
    private readonly int _channels;
    private readonly int _fs;
    private readonly int _delayCompensation;
    private readonly int _encoderBuffer;
    private readonly int _application;
    private int _forceChannels;
    private int _signalType;
    private int _userBandwidth;
    private int _maxBandwidth;
    private int _userForcedMode;
    private int _voiceRatio;
    private int _useVbr;
    private int _vbrConstraint;
    private int _variableDuration;
    private int _bitrateBps;
    private int _userBitrateBps;
    private int _lsbDepth;
    private int _lfe;
    private int _useDtx;
    private int _fecConfig;

    // OPUS_ENCODER_RESET_START.
    private int _streamChannels;
    private short _hybridStereoWidthQ14;
    private int _variableHpSmth2Q15;
    private float _prevHbGain;
    private readonly float[] _hpMem = new float[4];
    private int _mode;
    private int _prevMode;
    private int _prevChannels;
    private int _prevFramesize;
    private int _bandwidth;
    private int _autoBandwidth;
    private int _silkBwSwitch;
    private int _first;
    private float[]? _energyMasking;
    private float _widthXx;
    private float _widthXy;
    private float _widthYy;
    private float _smoothedWidth;
    private float _maxFollower;
    private readonly float[] _delayBuffer = new float[MaxEncoderBuffer * 2];
    private int _detectedBandwidth;
    private int _nbNoActivityMsQ1;
    private float _peakSignalEnergy;
    private int _nonfinalFrame;
    private uint _rangeFinal;

    // Scratch.
    private readonly OpusRangeEncoder _enc = new();
    private readonly OpusRepacketizer _rp = new();
    private readonly OpusRepacketizer _padRp = new();
    private readonly float[] _pcmBuf;
    private readonly short[] _pcmSilk;
    private readonly float[] _tmpPrefill;
    private readonly byte[] _tmpData = new byte[1276 + 6];
    private readonly byte[] _dummy = new byte[2];

    /// <summary>opus_encoder_init: an encoder for <paramref name="channels"/> (1 or 2) channels at <paramref name="sampleRate"/>.</summary>
    public OpusEncoder(int sampleRate, int channels, int application)
    {
        if ((sampleRate != 48000 && sampleRate != 24000 && sampleRate != 16000 && sampleRate != 12000 && sampleRate != 8000) ||
            (channels != 1 && channels != 2) ||
            (application != ApplicationVoip && application != ApplicationAudio && application != ApplicationRestrictedLowDelay))
        {
            throw new ArgumentException("Unsupported Opus encoder configuration.");
        }

        _streamChannels = _channels = channels;
        _fs = sampleRate;
        _silk.Init(_silkMode);
        // Default SILK parameters.
        _silkMode.NChannelsApi = channels;
        _silkMode.NChannelsInternal = channels;
        _silkMode.ApiSampleRate = _fs;
        _silkMode.MaxInternalSampleRate = 16000;
        _silkMode.MinInternalSampleRate = 8000;
        _silkMode.DesiredInternalSampleRate = 16000;
        _silkMode.PayloadSizeMs = 20;
        _silkMode.BitRate = 25000;
        _silkMode.PacketLossPercentage = 0;
        _silkMode.Complexity = 9;
        _silkMode.UseInBandFec = 0;
        _silkMode.UseDtx = 0;
        _silkMode.UseCbr = 0;
        _silkMode.ReducedDependency = 0;

        // Initialize CELT encoder. Initialize SILK encoder.
        _celt = new CeltEncoder(sampleRate, channels) { Complexity = _silkMode.Complexity };

        _useVbr = 1;
        // Makes constrained VBR the default (safer for real-time use).
        _vbrConstraint = 1;
        _userBitrateBps = Auto;
        _bitrateBps = 3000 + (_fs * channels);
        _application = application;
        _signalType = Auto;
        _userBandwidth = Auto;
        _maxBandwidth = BandwidthFullband;
        _forceChannels = Auto;
        _userForcedMode = Auto;
        _voiceRatio = -1;
        _encoderBuffer = _fs / 100;
        _lsbDepth = 24;
        _variableDuration = FramesizeArg;

        // Delay compensation of 4 ms (2.5 ms for SILK's extra look-ahead + 1.5 ms for SILK resamplers and stereo prediction).
        _delayCompensation = _fs / 250;

        _hybridStereoWidthQ14 = 1 << 14;
        _prevHbGain = 1f;
        _variableHpSmth2Q15 = LShift(SilkSigProc.Lin2Log(VariableHpMinCutoffHz), 8);
        _first = 1;
        _mode = ModeHybrid;
        _bandwidth = BandwidthFullband;

        _analysis = new OpusTonalityAnalysis(_fs);

        int maxFrame = 3 * _fs / 50;
        _pcmBuf = new float[((_fs / 250) + maxFrame) * channels];
        _pcmSilk = new short[maxFrame * channels];
        _tmpPrefill = new float[channels * _fs / 400];
    }

    /// <summary>Gets the sample rate.</summary>
    public int SampleRate => _fs;

    /// <summary>Gets the channel count.</summary>
    public int Channels => _channels;

    /// <summary>Gets or sets the bitrate in bit/s, <see cref="Auto"/> or <see cref="BitrateMax"/> (OPUS_SET_BITRATE; clamped to 500 to 300000 per channel).</summary>
    public int Bitrate
    {
        get => UserBitrateToBitrate(_prevFramesize, 1276);
        set
        {
            if (value != Auto && value != BitrateMax)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
                value = value <= 500 ? 500 : Math.Min(value, 300000 * _channels);
            }

            _userBitrateBps = value;
        }
    }

    /// <summary>Gets or sets the complexity, 0 to 10 (OPUS_SET_COMPLEXITY).</summary>
    public int Complexity
    {
        get => _silkMode.Complexity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10);
            _silkMode.Complexity = value;
            _celt.Complexity = value;
        }
    }

    /// <summary>Gets or sets whether the bitrate varies (OPUS_SET_VBR).</summary>
    public bool Vbr
    {
        get => _useVbr != 0;
        set
        {
            _useVbr = value ? 1 : 0;
            _silkMode.UseCbr = 1 - _useVbr;
        }
    }

    /// <summary>Gets or sets whether the variable bitrate is constrained (OPUS_SET_VBR_CONSTRAINT).</summary>
    public bool VbrConstraint
    {
        get => _vbrConstraint != 0;
        set => _vbrConstraint = value ? 1 : 0;
    }

    /// <summary>Gets or sets in-band forward error correction: 0 off, 1 on, 2 on without forcing SILK (OPUS_SET_INBAND_FEC).</summary>
    public int InbandFec
    {
        get => _fecConfig;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 2);
            _fecConfig = value;
            _silkMode.UseInBandFec = value != 0 ? 1 : 0;
        }
    }

    /// <summary>Gets or sets the expected packet loss in percent, 0 to 100 (OPUS_SET_PACKET_LOSS_PERC).</summary>
    public int PacketLossPercentage
    {
        get => _silkMode.PacketLossPercentage;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _silkMode.PacketLossPercentage = value;
            _celt.LossRate = value;
        }
    }

    /// <summary>Gets or sets discontinuous transmission (OPUS_SET_DTX).</summary>
    public bool Dtx
    {
        get => _useDtx != 0;
        set => _useDtx = value ? 1 : 0;
    }

    /// <summary>Gets or sets the widest bandwidth the encoder may pick (OPUS_SET_MAX_BANDWIDTH).</summary>
    public int MaxBandwidth
    {
        get => _maxBandwidth;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, BandwidthNarrowband);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, BandwidthFullband);
            _maxBandwidth = value;
            _silkMode.MaxInternalSampleRate = value == BandwidthNarrowband ? 8000 : value == BandwidthMediumband ? 12000 : 16000;
        }
    }

    /// <summary>Gets or sets a forced bandwidth or <see cref="Auto"/> (OPUS_SET_BANDWIDTH).</summary>
    public int Bandwidth
    {
        get => _bandwidth;
        set
        {
            if ((value < BandwidthNarrowband || value > BandwidthFullband) && value != Auto)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _userBandwidth = value;
            _silkMode.MaxInternalSampleRate = value == BandwidthNarrowband ? 8000 : value == BandwidthMediumband ? 12000 : 16000;
        }
    }

    /// <summary>Gets or sets the signal hint: <see cref="Auto"/>, <see cref="SignalVoice"/> or <see cref="SignalMusic"/> (OPUS_SET_SIGNAL).</summary>
    public int Signal
    {
        get => _signalType;
        set
        {
            if (value != Auto && value != SignalVoice && value != SignalMusic)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _signalType = value;
        }
    }

    /// <summary>Gets or sets forced stream channels or <see cref="Auto"/> (OPUS_SET_FORCE_CHANNELS).</summary>
    public int ForceChannels
    {
        get => _forceChannels;
        set
        {
            if ((value < 1 || value > _channels) && value != Auto)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _forceChannels = value;
        }
    }

    /// <summary>Gets or sets a forced mode or <see cref="Auto"/> (OPUS_SET_FORCE_MODE).</summary>
    public int ForceMode
    {
        get => _userForcedMode;
        set
        {
            if ((value < ModeSilkOnly || value > ModeCeltOnly) && value != Auto)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _userForcedMode = value;
        }
    }

    /// <summary>Gets or sets the frame duration rule, <see cref="FramesizeArg"/> or 5001 to 5009 (OPUS_SET_EXPERT_FRAME_DURATION).</summary>
    public int ExpertFrameDuration
    {
        get => _variableDuration;
        set
        {
            if (value != FramesizeArg && (value < Framesize2_5Ms || value > Framesize120Ms))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _variableDuration = value;
        }
    }

    /// <summary>Gets or sets the depth of the input in bits, 8 to 24 (OPUS_SET_LSB_DEPTH).</summary>
    public int LsbDepth
    {
        get => _lsbDepth;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 8);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 24);
            _lsbDepth = value;
        }
    }

    /// <summary>Gets or sets whether frames are coded without reference to earlier frames (OPUS_SET_PREDICTION_DISABLED).</summary>
    public bool PredictionDisabled
    {
        get => _silkMode.ReducedDependency != 0;
        set => _silkMode.ReducedDependency = value ? 1 : 0;
    }

    /// <summary>Gets or sets whether CELT's stereo phase inversion is off (OPUS_SET_PHASE_INVERSION_DISABLED).</summary>
    public bool PhaseInversionDisabled
    {
        get => _celt.DisableInv;
        set => _celt.DisableInv = value;
    }

    /// <summary>Gets or sets whether this stream carries a low-frequency effects channel (OPUS_SET_LFE).</summary>
    public bool Lfe
    {
        get => _lfe != 0;
        set
        {
            _lfe = value ? 1 : 0;
            _celt.Lfe = value;
        }
    }

    /// <summary>Gets the look-ahead in samples (OPUS_GET_LOOKAHEAD).</summary>
    public int Lookahead => (_fs / 400) + (_application != ApplicationRestrictedLowDelay ? _delayCompensation : 0);

    /// <summary>Gets the final range of the last packet, for conformance checks (OPUS_GET_FINAL_RANGE).</summary>
    public uint FinalRange => _rangeFinal;

    /// <summary>Sets the surround masking of the next frame (OPUS_SET_ENERGY_MASK), or null.</summary>
    public void SetEnergyMask(float[]? mask)
    {
        _energyMasking = mask;
        _celt.SetEnergyMask(mask);
    }

    /// <summary>OPUS_RESET_STATE.</summary>
    public void Reset()
    {
        _analysis.Reset();
        _streamChannels = 0;
        _hybridStereoWidthQ14 = 0;
        _variableHpSmth2Q15 = 0;
        _prevHbGain = 0;
        Array.Clear(_hpMem);
        _mode = 0;
        _prevMode = 0;
        _prevChannels = 0;
        _prevFramesize = 0;
        _bandwidth = 0;
        _autoBandwidth = 0;
        _silkBwSwitch = 0;
        _first = 0;
        _energyMasking = null;
        _widthXx = _widthXy = _widthYy = _smoothedWidth = _maxFollower = 0;
        Array.Clear(_delayBuffer);
        _detectedBandwidth = 0;
        _nbNoActivityMsQ1 = 0;
        _peakSignalEnergy = 0;
        _nonfinalFrame = 0;
        _rangeFinal = 0;

        _celt.Reset();
        _silk.Init(_dummyControl);
        _streamChannels = _channels;
        _hybridStereoWidthQ14 = 1 << 14;
        _prevHbGain = 1f;
        _first = 1;
        _mode = ModeHybrid;
        _bandwidth = BandwidthFullband;
        _variableHpSmth2Q15 = LShift(SilkSigProc.Lin2Log(VariableHpMinCutoffHz), 8);
    }

    /// <summary>
    /// opus_encode_float: encodes <paramref name="analysisFrameSize"/> samples per channel of interleaved <paramref name="pcm"/>
    /// (2.5 to 120 ms) into at most <paramref name="maxDataBytes"/> bytes of <paramref name="data"/> from <paramref name="offset"/>.
    /// Returns the packet size or a negative <see cref="OpusPacket"/> error.
    /// </summary>
    public int Encode(ReadOnlySpan<float> pcm, int analysisFrameSize, byte[] data, int offset, int maxDataBytes)
    {
        int frameSize = FrameSizeSelect(analysisFrameSize, _variableDuration, _fs);
        return EncodeNative(pcm, frameSize, data, offset, maxDataBytes, 24, pcm, analysisFrameSize, 0, -2, _channels, true);
    }

    // frame_size_select.
    internal static int FrameSizeSelect(int frameSize, int variableDuration, int fs)
    {
        if (frameSize < fs / 400)
        {
            return -1;
        }

        int newSize;
        if (variableDuration == FramesizeArg)
        {
            newSize = frameSize;
        }
        else if (variableDuration >= Framesize2_5Ms && variableDuration <= Framesize120Ms)
        {
            newSize = variableDuration <= Framesize40Ms
                ? (fs / 400) << (variableDuration - Framesize2_5Ms)
                : (variableDuration - Framesize2_5Ms - 2) * fs / 50;
        }
        else
        {
            return -1;
        }

        if (newSize > frameSize)
        {
            return -1;
        }

        if (400 * newSize != fs && 200 * newSize != fs && 100 * newSize != fs &&
            50 * newSize != fs && 25 * newSize != fs && 50 * newSize != 3 * fs &&
            50 * newSize != 4 * fs && 50 * newSize != 5 * fs && 50 * newSize != 6 * fs)
        {
            return -1;
        }

        return newSize;
    }

    private int UserBitrateToBitrate(int frameSize, int maxDataBytes)
    {
        if (frameSize == 0)
        {
            frameSize = _fs / 400;
        }

        if (_userBitrateBps == Auto)
        {
            return (60 * _fs / frameSize) + (_fs * _channels);
        }
        else if (_userBitrateBps == BitrateMax)
        {
            return maxDataBytes * 8 * _fs / frameSize;
        }
        else
        {
            return _userBitrateBps;
        }
    }

    /// <summary>
    /// opus_encode_native: encodes one packet of <paramref name="frameSize"/> samples per channel. <paramref name="analysisPcm"/> is
    /// the input the look-ahead analysis reads, <paramref name="c1"/> and <paramref name="c2"/> its downmix (as in libopus) and
    /// <paramref name="analysisChannels"/> its channel count.
    /// </summary>
    public int EncodeNative(ReadOnlySpan<float> pcm, int frameSize, byte[] data, int dataOffset, int outDataBytes, int lsbDepth,
        ReadOnlySpan<float> analysisPcm, int analysisSize, int c1, int c2, int analysisChannels, bool floatApi)
    {
        int ret;
        int prefill = 0;
        int redundancy = 0;
        int celtToSilk = 0;
        int toCelt = 0;
        int cbrBytes = -1;
        int analysisReadPosBak = -1;
        int analysisReadSubframeBak = -1;
        bool isSilence = false;

        int maxDataBytes = Math.Min(1276, outDataBytes);
        _rangeFinal = 0;
        if (frameSize <= 0 || maxDataBytes <= 0)
        {
            return OpusPacket.BadArgument;
        }

        // Cannot encode 100 ms in 1 byte.
        if (maxDataBytes == 1 && _fs == frameSize * 10)
        {
            return OpusPacket.BufferTooSmall;
        }

        lsbDepth = Math.Min(lsbDepth, _lsbDepth);
        OpusAnalysisInfo analysisInfo = default;
        if (_silkMode.Complexity >= 7 && _fs >= 16000)
        {
            isSilence = IsDigitalSilence(pcm, frameSize, _channels, lsbDepth);
            analysisReadPosBak = _analysis.ReadPos;
            analysisReadSubframeBak = _analysis.ReadSubframe;
            _analysis.Run(analysisPcm, analysisSize, frameSize, c1, c2, analysisChannels, lsbDepth, out analysisInfo);

            // Track the peak signal energy.
            if (!isSilence && analysisInfo.ActivityProbability > DtxActivityThreshold)
            {
                _peakSignalEnergy = Max(0.999f * _peakSignalEnergy, ComputeFrameEnergy(pcm, frameSize, _channels));
            }
        }
        else if (_analysis.Initialized)
        {
            _analysis.Reset();
        }

        // Reset voice_ratio if this frame is not silent or if analysis is disabled. Otherwise, preserve voice_ratio from the last
        // non-silent frame.
        if (!isSilence)
        {
            _voiceRatio = -1;
        }

        _detectedBandwidth = 0;
        if (analysisInfo.Valid)
        {
            if (_signalType == Auto)
            {
                float prob;
                if (_prevMode == 0)
                {
                    prob = analysisInfo.MusicProb;
                }
                else if (_prevMode == ModeCeltOnly)
                {
                    prob = analysisInfo.MusicProbMax;
                }
                else
                {
                    prob = analysisInfo.MusicProbMin;
                }

                _voiceRatio = (int)Math.Floor(.5 + (100 * (1 - prob)));
            }

            int analysisBandwidth = analysisInfo.Bandwidth;
            if (analysisBandwidth <= 12)
            {
                _detectedBandwidth = BandwidthNarrowband;
            }
            else if (analysisBandwidth <= 14)
            {
                _detectedBandwidth = BandwidthMediumband;
            }
            else if (analysisBandwidth <= 16)
            {
                _detectedBandwidth = BandwidthWideband;
            }
            else if (analysisBandwidth <= 18)
            {
                _detectedBandwidth = BandwidthSuperwideband;
            }
            else
            {
                _detectedBandwidth = BandwidthFullband;
            }
        }

        float stereoWidth = _channels == 2 && _forceChannels != 1 ? ComputeStereoWidth(pcm, frameSize) : 0;
        _bitrateBps = UserBitrateToBitrate(frameSize, maxDataBytes);

        int frameRate = _fs / frameSize;
        if (_useVbr == 0)
        {
            // Multiply by 12 to make sure the division is exact.
            int frameRate12 = 12 * _fs / frameSize;
            // We need to make sure that "int" values always fit in 16 bits.
            cbrBytes = Math.Min(((12 * _bitrateBps / 8) + (frameRate12 / 2)) / frameRate12, maxDataBytes);
            _bitrateBps = cbrBytes * frameRate12 * 8 / 12;
            // Make sure we provide at least one byte to avoid failing.
            maxDataBytes = Math.Max(1, cbrBytes);
        }

        if (maxDataBytes < 3 || _bitrateBps < 3 * frameRate * 8
            || (frameRate < 50 && (maxDataBytes * frameRate < 300 || _bitrateBps < 2400)))
        {
            // If the space is too low to do something useful, emit 'PLC' frames.
            int tocmode = _mode;
            int bw = _bandwidth == 0 ? BandwidthNarrowband : _bandwidth;
            int packetCode = 0;
            int numMultiframes = 0;

            if (tocmode == 0)
            {
                tocmode = ModeSilkOnly;
            }

            if (frameRate > 100)
            {
                tocmode = ModeCeltOnly;
            }

            // 40 ms -> 2 x 20 ms if in CELT_ONLY or HYBRID mode.
            if (frameRate == 25 && tocmode != ModeSilkOnly)
            {
                frameRate = 50;
                packetCode = 1;
            }

            // >= 60 ms frames.
            if (frameRate <= 16)
            {
                // 1 x 60 ms, 2 x 40 ms, 2 x 60 ms.
                if (outDataBytes == 1 || (tocmode == ModeSilkOnly && frameRate != 10))
                {
                    tocmode = ModeSilkOnly;
                    packetCode = frameRate <= 12 ? 1 : 0;
                    frameRate = frameRate == 12 ? 25 : 16;
                }
                else
                {
                    numMultiframes = 50 / frameRate;
                    frameRate = 50;
                    packetCode = 3;
                }
            }

            if (tocmode == ModeSilkOnly && bw > BandwidthWideband)
            {
                bw = BandwidthWideband;
            }
            else if (tocmode == ModeCeltOnly && bw == BandwidthMediumband)
            {
                bw = BandwidthNarrowband;
            }
            else if (tocmode == ModeHybrid && bw <= BandwidthSuperwideband)
            {
                bw = BandwidthSuperwideband;
            }

            data[dataOffset] = GenToc(tocmode, frameRate, bw, _streamChannels);
            data[dataOffset] |= (byte)packetCode;

            ret = packetCode <= 1 ? 1 : 2;

            maxDataBytes = Math.Max(maxDataBytes, ret);

            if (packetCode == 3)
            {
                data[dataOffset + 1] = (byte)numMultiframes;
            }

            if (_useVbr == 0)
            {
                ret = _padRp.Pad(data.AsSpan(dataOffset), ret, maxDataBytes) == 0 ? maxDataBytes : OpusPacket.InternalError;
            }

            return ret;
        }

        int maxRate = frameRate * maxDataBytes * 8;

        // Equivalent 20-ms rate for mode/channel/bandwidth decisions.
        int equivRate = ComputeEquivRate(_bitrateBps, _channels, _fs / frameSize, _useVbr, 0, _silkMode.Complexity, _silkMode.PacketLossPercentage);

        int voiceEst;
        if (_signalType == SignalVoice)
        {
            voiceEst = 127;
        }
        else if (_signalType == SignalMusic)
        {
            voiceEst = 0;
        }
        else if (_voiceRatio >= 0)
        {
            voiceEst = _voiceRatio * 327 >> 8;
            // For AUDIO, never be more than 90% confident of having speech.
            if (_application == ApplicationAudio)
            {
                voiceEst = Math.Min(voiceEst, 115);
            }
        }
        else if (_application == ApplicationVoip)
        {
            voiceEst = 115;
        }
        else
        {
            voiceEst = 48;
        }

        if (_forceChannels != Auto && _channels == 2)
        {
            _streamChannels = _forceChannels;
        }
        else if (_channels == 2)
        {
            // Rate-dependent mono-stereo decision.
            int stereoThreshold = StereoMusicThreshold + ((voiceEst * voiceEst * (StereoVoiceThreshold - StereoMusicThreshold)) >> 14);
            if (_streamChannels == 2)
            {
                stereoThreshold -= 1000;
            }
            else
            {
                stereoThreshold += 1000;
            }

            _streamChannels = equivRate > stereoThreshold ? 2 : 1;
        }
        else
        {
            _streamChannels = _channels;
        }

        // Update equivalent rate for channels decision.
        equivRate = ComputeEquivRate(_bitrateBps, _streamChannels, _fs / frameSize, _useVbr, 0, _silkMode.Complexity, _silkMode.PacketLossPercentage);

        // Allow SILK DTX if DTX is enabled but the generalized DTX cannot be used, e.g. because of the complexity setting or sample
        // rate.
        _silkMode.UseDtx = _useDtx != 0 && !(analysisInfo.Valid || isSilence) ? 1 : 0;

        // Mode selection depending on application and signal type.
        if (_application == ApplicationRestrictedLowDelay)
        {
            _mode = ModeCeltOnly;
        }
        else if (_userForcedMode == Auto)
        {
            // Interpolate based on stereo width.
            int modeVoice = (int)(((1f - stereoWidth) * ModeThresholds[0]) + (stereoWidth * ModeThresholds[2]));
            int modeMusic = (int)(((1f - stereoWidth) * ModeThresholds[3]) + (stereoWidth * ModeThresholds[3]));
            // Interpolate based on speech/music probability.
            int threshold = modeMusic + ((voiceEst * voiceEst * (modeVoice - modeMusic)) >> 14);
            // Bias towards SILK for VoIP because of some useful features.
            if (_application == ApplicationVoip)
            {
                threshold += 8000;
            }

            // Hysteresis.
            if (_prevMode == ModeCeltOnly)
            {
                threshold -= 4000;
            }
            else if (_prevMode > 0)
            {
                threshold += 4000;
            }

            _mode = equivRate >= threshold ? ModeCeltOnly : ModeSilkOnly;

            // When FEC is enabled and there's enough packet loss, use SILK. Unless the FEC is set to 2, in which case we don't
            // switch to SILK if we're confident we have music.
            if (_silkMode.UseInBandFec != 0 && _silkMode.PacketLossPercentage > (128 - voiceEst) >> 4 && (_fecConfig != 2 || voiceEst > 25))
            {
                _mode = ModeSilkOnly;
            }

            // When encoding voice and DTX is enabled but the generalized DTX cannot be used, use SILK in order to make use of
            // its DTX.
            if (_silkMode.UseDtx != 0 && voiceEst > 100)
            {
                _mode = ModeSilkOnly;
            }

            // If max_data_bytes represents less than 6 kb/s, switch to CELT-only mode.
            if (maxDataBytes < (frameRate > 50 ? 9000 : 6000) * frameSize / (_fs * 8))
            {
                _mode = ModeCeltOnly;
            }
        }
        else
        {
            _mode = _userForcedMode;
        }

        // Override the chosen mode to make sure we meet the requested frame size.
        if (_mode != ModeCeltOnly && frameSize < _fs / 100)
        {
            _mode = ModeCeltOnly;
        }

        if (_lfe != 0)
        {
            _mode = ModeCeltOnly;
        }

        if (_prevMode > 0 &&
            ((_mode != ModeCeltOnly && _prevMode == ModeCeltOnly) ||
             (_mode == ModeCeltOnly && _prevMode != ModeCeltOnly)))
        {
            redundancy = 1;
            celtToSilk = _mode != ModeCeltOnly ? 1 : 0;
            if (celtToSilk == 0)
            {
                // Switch to SILK/hybrid if frame size is 10 ms or more.
                if (frameSize >= _fs / 100)
                {
                    _mode = _prevMode;
                    toCelt = 1;
                }
                else
                {
                    redundancy = 0;
                }
            }
        }

        // When encoding multiframes, we can ask for a switch to CELT only in the last frame. This switch condition will then
        // trigger the CELT redundancy frame. Avoid changing the number of channels during it.
        if (_streamChannels == 1 && _prevChannels == 2 && _silkMode.ToMono == 0
            && _mode != ModeCeltOnly && _prevMode != ModeCeltOnly)
        {
            // Delay stereo->mono transition by two frames so that SILK can do a smooth downmix.
            _silkMode.ToMono = 1;
            _streamChannels = 2;
        }
        else
        {
            _silkMode.ToMono = 0;
        }

        // Update equivalent rate with mode decision.
        equivRate = ComputeEquivRate(_bitrateBps, _streamChannels, _fs / frameSize, _useVbr, _mode, _silkMode.Complexity, _silkMode.PacketLossPercentage);

        if (_mode != ModeCeltOnly && _prevMode == ModeCeltOnly)
        {
            _silk.Init(_dummyControl);
            prefill = 1;
        }

        // Automatic (rate-dependent) bandwidth selection.
        if (_mode == ModeCeltOnly || _first != 0 || _silkMode.AllowBandwidthSwitch != 0)
        {
            ReadOnlySpan<int> voiceBandwidthThresholds;
            ReadOnlySpan<int> musicBandwidthThresholds;
            Span<int> bandwidthThresholds = stackalloc int[8];
            int bandwidth = BandwidthFullband;

            if (_channels == 2 && _forceChannels != 1)
            {
                voiceBandwidthThresholds = StereoVoiceBandwidthThresholds;
                musicBandwidthThresholds = StereoMusicBandwidthThresholds;
            }
            else
            {
                voiceBandwidthThresholds = MonoVoiceBandwidthThresholds;
                musicBandwidthThresholds = MonoMusicBandwidthThresholds;
            }

            // Interpolate bandwidth thresholds depending on voice estimation.
            for (int i = 0; i < 8; i++)
            {
                bandwidthThresholds[i] = musicBandwidthThresholds[i]
                    + ((voiceEst * voiceEst * (voiceBandwidthThresholds[i] - musicBandwidthThresholds[i])) >> 14);
            }

            do
            {
                int threshold = bandwidthThresholds[2 * (bandwidth - BandwidthMediumband)];
                int hysteresis = bandwidthThresholds[(2 * (bandwidth - BandwidthMediumband)) + 1];
                if (_first == 0)
                {
                    if (_autoBandwidth >= bandwidth)
                    {
                        threshold -= hysteresis;
                    }
                    else
                    {
                        threshold += hysteresis;
                    }
                }

                if (equivRate >= threshold)
                {
                    break;
                }
            }
            while (--bandwidth > BandwidthNarrowband);

            // We don't use mediumband anymore, except when explicitly requested or during mode transitions.
            if (bandwidth == BandwidthMediumband)
            {
                bandwidth = BandwidthWideband;
            }

            _bandwidth = _autoBandwidth = bandwidth;
            // Prevents any transition to SWB/FB until the SILK layer has fully switched to WB mode and turned the variable LP
            // filter off.
            if (_first == 0 && _mode != ModeCeltOnly && _silkMode.InWbModeWithoutVariableLp == 0 && _bandwidth > BandwidthWideband)
            {
                _bandwidth = BandwidthWideband;
            }
        }

        if (_bandwidth > _maxBandwidth)
        {
            _bandwidth = _maxBandwidth;
        }

        if (_userBandwidth != Auto)
        {
            _bandwidth = _userBandwidth;
        }

        // This prevents us from using hybrid at unsafe CBR/max rates.
        if (_mode != ModeCeltOnly && maxRate < 15000)
        {
            _bandwidth = Math.Min(_bandwidth, BandwidthWideband);
        }

        // Prevents Opus from wasting bits on frequencies that are above the Nyquist rate of the input signal.
        if (_fs <= 24000 && _bandwidth > BandwidthSuperwideband)
        {
            _bandwidth = BandwidthSuperwideband;
        }

        if (_fs <= 16000 && _bandwidth > BandwidthWideband)
        {
            _bandwidth = BandwidthWideband;
        }

        if (_fs <= 12000 && _bandwidth > BandwidthMediumband)
        {
            _bandwidth = BandwidthMediumband;
        }

        if (_fs <= 8000 && _bandwidth > BandwidthNarrowband)
        {
            _bandwidth = BandwidthNarrowband;
        }

        // Use detected bandwidth to reduce the encoded bandwidth.
        if (_detectedBandwidth != 0 && _userBandwidth == Auto)
        {
            int minDetectedBandwidth;
            // Makes bandwidth detection more conservative just in case the detector gets it wrong when we could have coded a
            // high bandwidth transparently. When operating in SILK/hybrid mode, we don't go below wideband to avoid changing
            // the SILK internal rate.
            if (equivRate <= 18000 * _streamChannels && _mode == ModeCeltOnly)
            {
                minDetectedBandwidth = BandwidthNarrowband;
            }
            else if (equivRate <= 24000 * _streamChannels && _mode == ModeCeltOnly)
            {
                minDetectedBandwidth = BandwidthMediumband;
            }
            else if (equivRate <= 30000 * _streamChannels)
            {
                minDetectedBandwidth = BandwidthWideband;
            }
            else if (equivRate <= 44000 * _streamChannels)
            {
                minDetectedBandwidth = BandwidthSuperwideband;
            }
            else
            {
                minDetectedBandwidth = BandwidthFullband;
            }

            _detectedBandwidth = Math.Max(_detectedBandwidth, minDetectedBandwidth);
            _bandwidth = Math.Min(_bandwidth, _detectedBandwidth);
        }

        _silkMode.LbrrCoded = DecideFec(_silkMode.UseInBandFec, _silkMode.PacketLossPercentage, _silkMode.LbrrCoded, _mode, ref _bandwidth, equivRate);
        _celt.LsbDepth = lsbDepth;

        // CELT mode doesn't support mediumband, use wideband instead.
        if (_mode == ModeCeltOnly && _bandwidth == BandwidthMediumband)
        {
            _bandwidth = BandwidthWideband;
        }

        if (_lfe != 0)
        {
            _bandwidth = BandwidthNarrowband;
        }

        int currBandwidth = _bandwidth;

        // Chooses the appropriate mode for speech. *NEVER* switch to/from CELT-only mode here as this will invalidate some
        // encoding decisions.
        if (_mode == ModeSilkOnly && currBandwidth > BandwidthWideband)
        {
            _mode = ModeHybrid;
        }

        if (_mode == ModeHybrid && currBandwidth <= BandwidthWideband)
        {
            _mode = ModeSilkOnly;
        }

        // Can't support higher than >60 ms frames, and >20 ms when in Hybrid or CELT-only modes, so we split into multiple frames.
        if ((frameSize > _fs / 50 && _mode != ModeSilkOnly) || frameSize > 3 * _fs / 50)
        {
            int encFrameSize;
            int dtxCount = 0;
            int totSize = 0;
            int currData = 0;

            if (_mode == ModeSilkOnly)
            {
                if (frameSize == 2 * _fs / 25)
                {
                    // 80 ms -> 2x 40 ms.
                    encFrameSize = _fs / 25;
                }
                else if (frameSize == 3 * _fs / 25)
                {
                    // 120 ms -> 2x 60 ms.
                    encFrameSize = 3 * _fs / 50;
                }
                else
                {
                    // 100 ms -> 5x 20 ms.
                    encFrameSize = _fs / 50;
                }
            }
            else
            {
                encFrameSize = _fs / 50;
            }

            int nbFrames = frameSize / encFrameSize;

            if (analysisReadPosBak != -1)
            {
                // Reset analysis position to the beginning of the first frame so we can use it one frame at a time.
                _analysis.ReadPos = analysisReadPosBak;
                _analysis.ReadSubframe = analysisReadSubframeBak;
            }

            // Worst cases: 2 frames: code 2 with different compressed sizes; >2 frames: code 3 VBR.
            int maxHeaderBytes = nbFrames == 2 ? 3 : 2 + ((nbFrames - 1) * 2);

            int repacketizeLen;
            if (_useVbr != 0 || _userBitrateBps == BitrateMax)
            {
                repacketizeLen = outDataBytes;
            }
            else
            {
                repacketizeLen = Math.Min(cbrBytes, outDataBytes);
            }

            int maxLenSum = nbFrames + repacketizeLen - maxHeaderBytes;

            byte[] tmpData = _tmpData.Length >= maxLenSum ? _tmpData : new byte[maxLenSum];
            _rp.Init();

            int bakToMono = _silkMode.ToMono;
            if (bakToMono != 0)
            {
                _forceChannels = 1;
            }
            else
            {
                _prevChannels = _streamChannels;
            }

            for (int i = 0; i < nbFrames; i++)
            {
                int firstFrame = i == 0 || i == dtxCount ? 1 : 0;
                _silkMode.ToMono = 0;
                _nonfinalFrame = i < nbFrames - 1 ? 1 : 0;

                // When switching from SILK/Hybrid to CELT, only ask for a switch at the last frame.
                int frameToCelt = toCelt != 0 && i == nbFrames - 1 ? 1 : 0;
                int frameRedundancy = redundancy != 0 && (frameToCelt != 0 || (toCelt == 0 && i == 0)) ? 1 : 0;

                int currMax = Math.Min(3 * _bitrateBps / (3 * 8 * _fs / encFrameSize), maxLenSum / nbFrames);
                currMax = Math.Min(maxLenSum - totSize, currMax);
                if (analysisReadPosBak != -1)
                {
                    isSilence = IsDigitalSilence(pcm, frameSize, _channels, lsbDepth);
                    // Get analysis for current frame.
                    _analysis.GetInfo(out analysisInfo, encFrameSize);
                }

                int tmpLen = EncodeFrameNative(pcm[(i * _channels * encFrameSize)..], encFrameSize, tmpData, currData, currMax, floatApi, firstFrame,
                    ref analysisInfo, isSilence, frameRedundancy, celtToSilk, prefill, equivRate, frameToCelt);
                if (tmpLen < 0)
                {
                    return OpusPacket.InternalError;
                }
                else if (tmpLen == 1)
                {
                    dtxCount++;
                }

                ret = _rp.Cat(tmpData.AsSpan(currData, tmpLen));
                if (ret < 0)
                {
                    return OpusPacket.InternalError;
                }

                totSize += tmpLen;
                currData += tmpLen;
            }

            ret = _rp.Out(0, nbFrames, data.AsSpan(dataOffset), repacketizeLen, false, _useVbr == 0 && dtxCount != nbFrames);
            if (ret < 0)
            {
                ret = OpusPacket.InternalError;
            }

            _silkMode.ToMono = bakToMono;
            return ret;
        }
        else
        {
            return EncodeFrameNative(pcm, frameSize, data, dataOffset, maxDataBytes, floatApi, 1, ref analysisInfo, isSilence, redundancy,
                celtToSilk, prefill, equivRate, toCelt);
        }
    }

    // opus_encode_frame_native: one frame of at most 20 ms (CELT and hybrid) or 60 ms (SILK). data[offset] is the TOC.
    private int EncodeFrameNative(ReadOnlySpan<float> pcm, int frameSize, byte[] data, int offset, int maxDataBytes, bool floatApi, int firstFrame,
        ref OpusAnalysisInfo analysisInfo, bool isSilence, int redundancy, int celtToSilk, int prefill, int equivRate, int toCelt)
    {
        int ret = 0;
        int nBytes = 0;
        int startBand = 0;
        int redundancyBytes = 0;
        uint redundantRng = 0;
        int activity = VadNoDecision;
        _ = firstFrame;

        _rangeFinal = 0;
        int currBandwidth = _bandwidth;
        int delayCompensation = _application == ApplicationRestrictedLowDelay ? 0 : _delayCompensation;
        int totalBuffer = delayCompensation;

        int frameRate = _fs / frameSize;

        if (isSilence)
        {
            activity = 0;
        }
        else if (analysisInfo.Valid)
        {
            activity = analysisInfo.ActivityProbability >= DtxActivityThreshold ? 1 : 0;
            if (activity == 0)
            {
                // Mark as active if this noise frame is sufficiently loud.
                float noiseEnergy = ComputeFrameEnergy(pcm, frameSize, _channels);
                activity = _peakSignalEnergy < PseudoSnrThreshold * noiseEnergy ? 1 : 0;
            }
        }

        if (_silkBwSwitch != 0)
        {
            redundancy = 1;
            celtToSilk = 1;
            _silkBwSwitch = 0;
            // Do a prefill without resetting the sampling rate control.
            prefill = 2;
        }

        // If we decided to go with CELT, make sure redundancy is off, no matter what we decided earlier.
        if (_mode == ModeCeltOnly)
        {
            redundancy = 0;
        }

        if (redundancy != 0)
        {
            redundancyBytes = ComputeRedundancyBytes(maxDataBytes, _bitrateBps, frameRate, _streamChannels);
            if (redundancyBytes == 0)
            {
                redundancy = 0;
            }
        }

        // The SILK and CELT layers start after the TOC byte.
        int bytesTarget = Math.Min(maxDataBytes - redundancyBytes, _bitrateBps * frameSize / (_fs * 8)) - 1;
        int payload = offset + 1;
        OpusRangeEncoder enc = _enc;
        enc.Init(data, payload, maxDataBytes - 1);

        int channels = _channels;
        Span<float> pcmBuf = _pcmBuf.AsSpan(0, (totalBuffer + frameSize) * channels);
        _delayBuffer.AsSpan((_encoderBuffer - totalBuffer) * channels, totalBuffer * channels).CopyTo(pcmBuf);

        int hpFreqSmth1 = _mode == ModeCeltOnly
            ? LShift(SilkSigProc.Lin2Log(VariableHpMinCutoffHz), 8)
            : _silk.Channel(0).VariableHpSmth1Q15;

        _variableHpSmth2Q15 = Smlawb(_variableHpSmth2Q15, hpFreqSmth1 - _variableHpSmth2Q15, SilkEncoderQuant.FixConst(VariableHpSmthCoef2, 16));

        // Convert from log scale to Hertz.
        int cutoffHz = SilkSigProc.Log2Lin(_variableHpSmth2Q15 >> 8);

        Span<float> pcmBufNew = pcmBuf[(totalBuffer * channels)..];
        if (_application == ApplicationVoip)
        {
            HpCutoff(pcm, cutoffHz, pcmBufNew, _hpMem, frameSize, channels, _fs);
        }
        else
        {
            DcReject(pcm, 3, pcmBufNew, _hpMem, frameSize, channels, _fs);
        }

        if (floatApi)
        {
            float sum = CeltMath.InnerProduct(pcmBufNew, pcmBufNew, frameSize * channels);
            // This should filter out both NaNs and ridiculous signals that could cause NaNs further down.
            if (!(sum < 1e9f) || float.IsNaN(sum))
            {
                pcmBufNew[..(frameSize * channels)].Clear();
                _hpMem[0] = _hpMem[1] = _hpMem[2] = _hpMem[3] = 0;
            }
        }

        // SILK processing.
        float hbGain = 1f;
        if (_mode != ModeCeltOnly)
        {
            Span<short> pcmSilk = _pcmSilk.AsSpan(0, channels * frameSize);

            // Distribute bits between SILK and CELT.
            int totalBitRate = 8 * bytesTarget * frameRate;
            if (_mode == ModeHybrid)
            {
                // Base rate for SILK.
                _silkMode.BitRate = ComputeSilkRateForHybrid(totalBitRate, currBandwidth, _fs == 50 * frameSize ? 1 : 0, _useVbr, _silkMode.LbrrCoded, _streamChannels);
                if (_energyMasking == null)
                {
                    // Increasingly attenuate high band when it gets allocated fewer bits.
                    int celtRate = totalBitRate - _silkMode.BitRate;
                    hbGain = 1f - CeltMath.Exp2(-celtRate * (1f / 1024));
                }
            }
            else
            {
                // SILK gets all bits.
                _silkMode.BitRate = totalBitRate;
            }

            // Surround masking for SILK.
            if (_energyMasking != null && _useVbr != 0 && _lfe == 0)
            {
                float maskSum = 0;
                int end = 17;
                short srate = 16000;
                if (_bandwidth == BandwidthNarrowband)
                {
                    end = 13;
                    srate = 8000;
                }
                else if (_bandwidth == BandwidthMediumband)
                {
                    end = 15;
                    srate = 12000;
                }

                for (int c = 0; c < channels; c++)
                {
                    for (int i = 0; i < end; i++)
                    {
                        float mask = Max(Min(_energyMasking[(21 * c) + i], .5f), -2.0f);
                        if (mask > 0)
                        {
                            mask = .5f * mask;
                        }

                        maskSum += mask;
                    }
                }

                // Conservative rate reduction, we cut the masking in half.
                float maskingDepth = maskSum / end * channels;
                maskingDepth += .2f;
                int rateOffset = (int)(srate * maskingDepth);
                rateOffset = Math.Max(rateOffset, -2 * _silkMode.BitRate / 3);
                // Split the rate change between the SILK and CELT part for hybrid.
                if (_bandwidth == BandwidthSuperwideband || _bandwidth == BandwidthFullband)
                {
                    _silkMode.BitRate += 3 * rateOffset / 5;
                }
                else
                {
                    _silkMode.BitRate += rateOffset;
                }
            }

            _silkMode.PayloadSizeMs = 1000 * frameSize / _fs;
            _silkMode.NChannelsApi = channels;
            _silkMode.NChannelsInternal = _streamChannels;
            if (currBandwidth == BandwidthNarrowband)
            {
                _silkMode.DesiredInternalSampleRate = 8000;
            }
            else if (currBandwidth == BandwidthMediumband)
            {
                _silkMode.DesiredInternalSampleRate = 12000;
            }
            else
            {
                _silkMode.DesiredInternalSampleRate = 16000;
            }

            // Hybrid mode only supports 16 kHz internal.
            _silkMode.MinInternalSampleRate = _mode == ModeHybrid ? 16000 : 8000;

            _silkMode.MaxInternalSampleRate = 16000;
            if (_mode == ModeSilkOnly)
            {
                int effectiveMaxRate = frameRate * maxDataBytes * 8;
                if (frameRate > 50)
                {
                    effectiveMaxRate = effectiveMaxRate * 2 / 3;
                }

                if (effectiveMaxRate < 8000)
                {
                    _silkMode.MaxInternalSampleRate = 12000;
                    _silkMode.DesiredInternalSampleRate = Math.Min(12000, _silkMode.DesiredInternalSampleRate);
                }

                if (effectiveMaxRate < 7000)
                {
                    _silkMode.MaxInternalSampleRate = 8000;
                    _silkMode.DesiredInternalSampleRate = Math.Min(8000, _silkMode.DesiredInternalSampleRate);
                }
            }

            _silkMode.UseCbr = _useVbr == 0 ? 1 : 0;

            // Call SILK encoder for the low band.

            // Max bits for SILK, counting ToC, redundancy bytes, and optionally redundancy.
            _silkMode.MaxBits = (maxDataBytes - 1) * 8;
            if (redundancy != 0 && redundancyBytes >= 2)
            {
                // Counting 1 bit for redundancy position and 20 bits for flag+size (only for hybrid).
                _silkMode.MaxBits -= (redundancyBytes * 8) + 1;
                if (_mode == ModeHybrid)
                {
                    _silkMode.MaxBits -= 20;
                }
            }

            if (_silkMode.UseCbr != 0)
            {
                // When we're in CBR mode, but we have non-SILK data to encode, switch SILK to VBR with cap to save on
                // complexity. Any variations will be absorbed by CELT and/or DRED and we can still produce a constant bitrate
                // without wasting bits.
                if (_mode == ModeHybrid)
                {
                    // Allow SILK to steal up to 25% of the remaining bits.
                    short otherBits = (short)Math.Max(0, _silkMode.MaxBits - (_silkMode.BitRate * frameSize / _fs));
                    _silkMode.MaxBits = Math.Max(0, _silkMode.MaxBits - (otherBits * 3 / 4));
                    _silkMode.UseCbr = 0;
                }
            }
            else
            {
                // Constrained VBR.
                if (_mode == ModeHybrid)
                {
                    // Compute SILK bitrate corresponding to the max total bits available.
                    int maxBitRate = ComputeSilkRateForHybrid(_silkMode.MaxBits * _fs / frameSize, currBandwidth, _fs == 50 * frameSize ? 1 : 0,
                        _useVbr, _silkMode.LbrrCoded, _streamChannels);
                    _silkMode.MaxBits = maxBitRate * frameSize / _fs;
                }
            }

            if (prefill != 0)
            {
                int zero = 0;
                // Use a smooth onset for the SILK prefill to avoid the encoder trying to encode a discontinuity. The exact
                // location is what we need to avoid leaving any "gap" in the audio when mixing with the redundant CELT frame.
                // Here we can afford to overwrite st->delay_buffer because the only thing that uses it before it gets
                // rewritten is tmp_prefill[] and even then only the part after the ramp really gets used (rather than sent
                // to the encoder and discarded).
                int prefillOffset = channels * (_encoderBuffer - _delayCompensation - (_fs / 400));
                GainFade(_delayBuffer.AsSpan(prefillOffset), _delayBuffer.AsSpan(prefillOffset), 0, 1f, Overlap, _fs / 400, channels, _fs);
                _delayBuffer.AsSpan(0, prefillOffset).Clear();
                for (int i = 0; i < _encoderBuffer * channels; i++)
                {
                    _pcmSilk[i] = Float2Int16(_delayBuffer[i]);
                }

                _silk.Encode(_silkMode, _pcmSilk, _encoderBuffer, null, ref zero, prefill, activity);
                // Prevent a second switch in the real encode call.
                _silkMode.OpusCanSwitch = 0;
            }

            for (int i = 0; i < frameSize * channels; i++)
            {
                pcmSilk[i] = Float2Int16(pcmBufNew[i]);
            }

            ret = _silk.Encode(_silkMode, pcmSilk, frameSize, enc, ref nBytes, 0, activity);
            if (ret != 0)
            {
                // Handle error.
                return OpusPacket.InternalError;
            }

            // Extract SILK internal bandwidth for signaling in first byte.
            if (_mode == ModeSilkOnly)
            {
                if (_silkMode.InternalSampleRate == 8000)
                {
                    currBandwidth = BandwidthNarrowband;
                }
                else if (_silkMode.InternalSampleRate == 12000)
                {
                    currBandwidth = BandwidthMediumband;
                }
                else if (_silkMode.InternalSampleRate == 16000)
                {
                    currBandwidth = BandwidthWideband;
                }
            }

            _silkMode.OpusCanSwitch = _silkMode.SwitchReady != 0 && _nonfinalFrame == 0 ? 1 : 0;

            if (nBytes == 0)
            {
                _rangeFinal = 0;
                data[offset] = GenToc(_mode, _fs / frameSize, currBandwidth, _streamChannels);
                return 1;
            }

            // FIXME: How do we allocate the redundancy for CBR?
            if (_silkMode.OpusCanSwitch != 0)
            {
                redundancyBytes = ComputeRedundancyBytes(maxDataBytes, _bitrateBps, frameRate, _streamChannels);
                redundancy = redundancyBytes != 0 ? 1 : 0;
                celtToSilk = 0;
                _silkBwSwitch = 1;
            }
        }

        // CELT processing.
        {
            int endband = currBandwidth switch
            {
                BandwidthNarrowband => 13,
                BandwidthMediumband or BandwidthWideband => 17,
                BandwidthSuperwideband => 19,
                _ => 21,
            };
            _celt.EndBand = endband;
            _celt.StreamChannels = _streamChannels;
        }

        SetCeltBitrate(BitrateMax);
        if (_mode != ModeSilkOnly)
        {
            int celtPred = 2;
            // We may still decide to disable prediction later.
            if (_silkMode.ReducedDependency != 0)
            {
                celtPred = 0;
            }

            SetCeltPrediction(celtPred);
        }

        Span<float> tmpPrefill = _tmpPrefill;
        if (_mode != ModeSilkOnly && _mode != _prevMode && _prevMode > 0)
        {
            _delayBuffer.AsSpan((_encoderBuffer - totalBuffer - (_fs / 400)) * channels, channels * _fs / 400).CopyTo(tmpPrefill);
        }

        if (channels * (_encoderBuffer - (frameSize + totalBuffer)) > 0)
        {
            Array.Copy(_delayBuffer, channels * frameSize, _delayBuffer, 0, channels * (_encoderBuffer - frameSize - totalBuffer));
            pcmBuf[..((frameSize + totalBuffer) * channels)].CopyTo(_delayBuffer.AsSpan(channels * (_encoderBuffer - frameSize - totalBuffer)));
        }
        else
        {
            pcmBuf.Slice((frameSize + totalBuffer - _encoderBuffer) * channels, _encoderBuffer * channels).CopyTo(_delayBuffer);
        }

        // gain_fade() and stereo_fade() need to be after the buffer copying because we don't want any of this to affect the
        // SILK part.
        if (_prevHbGain < 1f || hbGain < 1f)
        {
            GainFade(pcmBuf, pcmBuf, _prevHbGain, hbGain, Overlap, frameSize, channels, _fs);
        }

        _prevHbGain = hbGain;
        if (_mode != ModeHybrid || _streamChannels == 1)
        {
            if (equivRate > 32000)
            {
                _silkMode.StereoWidthQ14 = 16384;
            }
            else if (equivRate < 16000)
            {
                _silkMode.StereoWidthQ14 = 0;
            }
            else
            {
                _silkMode.StereoWidthQ14 = 16384 - (2048 * (32000 - equivRate) / (equivRate - 14000));
            }
        }

        if (_energyMasking == null && channels == 2)
        {
            // Apply stereo width reduction (at low bitrates).
            if (_hybridStereoWidthQ14 < (1 << 14) || _silkMode.StereoWidthQ14 < (1 << 14))
            {
                float g1 = _hybridStereoWidthQ14;
                float g2 = _silkMode.StereoWidthQ14;
                g1 *= 1f / 16384;
                g2 *= 1f / 16384;
                StereoFade(pcmBuf, pcmBuf, g1, g2, Overlap, frameSize, channels, _fs);
                _hybridStereoWidthQ14 = (short)_silkMode.StereoWidthQ14;
            }
        }

        if (_mode != ModeCeltOnly && enc.Tell() + 17 + (20 * (_mode == ModeHybrid ? 1 : 0)) <= 8 * (maxDataBytes - 1))
        {
            // For SILK mode, the redundancy is inferred from the length.
            if (_mode == ModeHybrid)
            {
                enc.EncodeBitLogp(redundancy != 0, 12);
            }

            if (redundancy != 0)
            {
                enc.EncodeBitLogp(celtToSilk != 0, 1);
                int maxRedundancy = _mode == ModeHybrid
                    ? maxDataBytes - 1 - ((enc.Tell() + 8 + 3 + 7) >> 3) // Reserve the 8 bits needed for the redundancy length, and at least a few bits for CELT if possible.
                    : maxDataBytes - 1 - ((enc.Tell() + 7) >> 3);
                // Target the same bit-rate for redundancy as for the rest, up to a max of 257 bytes.
                redundancyBytes = Math.Min(maxRedundancy, redundancyBytes);
                redundancyBytes = Math.Min(257, Math.Max(2, redundancyBytes));
                if (_mode == ModeHybrid)
                {
                    enc.EncodeUint((uint)(redundancyBytes - 2), 256);
                }
            }
        }
        else
        {
            redundancy = 0;
        }

        if (redundancy == 0)
        {
            _silkBwSwitch = 0;
            redundancyBytes = 0;
        }

        if (_mode != ModeCeltOnly)
        {
            startBand = 17;
        }

        int nbComprBytes;
        if (_mode == ModeSilkOnly)
        {
            ret = (enc.Tell() + 7) >> 3;
            enc.Done();
            nbComprBytes = ret;
        }
        else
        {
            nbComprBytes = maxDataBytes - 1 - redundancyBytes;
            enc.Shrink((uint)nbComprBytes);
        }

        if (redundancy != 0 || _mode != ModeSilkOnly)
        {
            _celt.SetAnalysis(in analysisInfo);
        }

        if (_mode == ModeHybrid)
        {
            OpusSilkInfo info;
            info.SignalType = _silkMode.SignalType;
            info.Offset = _silkMode.Offset;
            _celt.SetSilkInfo(in info);
        }

        // 5 ms redundant frame for CELT->SILK.
        if (redundancy != 0 && celtToSilk != 0)
        {
            _celt.StartBand = 0;
            _celt.Vbr = false;
            SetCeltBitrate(BitrateMax);
            int err = _celt.Encode(pcmBuf, _fs / 200, data, payload + nbComprBytes, redundancyBytes, null);
            if (err < 0)
            {
                return OpusPacket.InternalError;
            }

            redundantRng = _celt.FinalRange;
            _celt.Reset();
        }

        _celt.StartBand = startBand;

        if (_mode != ModeSilkOnly)
        {
            _celt.Vbr = _useVbr != 0;
            if (_mode == ModeHybrid)
            {
                if (_useVbr != 0)
                {
                    SetCeltBitrate(_bitrateBps - _silkMode.BitRate);
                    _celt.ConstrainedVbr = false;
                }
            }
            else
            {
                if (_useVbr != 0)
                {
                    _celt.Vbr = true;
                    _celt.ConstrainedVbr = _vbrConstraint != 0;
                    SetCeltBitrate(_bitrateBps);
                }
            }

            if (_mode != _prevMode && _prevMode > 0)
            {
                _celt.Reset();

                // Prefilling.
                _celt.Encode(tmpPrefill, _fs / 400, _dummy, 0, 2, null);
                SetCeltPrediction(0);
            }

            // If false, we already busted the budget and we'll end up with a "PLC frame".
            if (enc.Tell() <= 8 * nbComprBytes)
            {
                ret = _celt.Encode(pcmBuf, frameSize, null, 0, nbComprBytes, enc);
                if (ret < 0)
                {
                    return OpusPacket.InternalError;
                }

                // Put CELT->SILK redundancy data in the right place.
                if (redundancy != 0 && celtToSilk != 0 && _mode == ModeHybrid && nbComprBytes != ret)
                {
                    Array.Copy(data, payload + nbComprBytes, data, payload + ret, redundancyBytes);
                    nbComprBytes = ret + redundancyBytes;
                }
            }
        }

        // 5 ms redundant frame for SILK->CELT.
        if (redundancy != 0 && celtToSilk == 0)
        {
            int n2 = _fs / 200;
            int n4 = _fs / 400;

            _celt.Reset();
            _celt.StartBand = 0;
            SetCeltPrediction(0);
            _celt.Vbr = false;
            SetCeltBitrate(BitrateMax);

            if (_mode == ModeHybrid)
            {
                // Shrink packet to what the encoder actually used.
                nbComprBytes = ret;
                enc.Shrink((uint)nbComprBytes);
            }

            // NOTE: We could speed this up slightly (at the expense of code size) by just adding a function that prefills the
            // buffer.
            _celt.Encode(pcmBuf[(channels * (frameSize - n2 - n4))..], n4, _dummy, 0, 2, null);

            int err = _celt.Encode(pcmBuf[(channels * (frameSize - n2))..], n2, data, payload + nbComprBytes, redundancyBytes, null);
            if (err < 0)
            {
                return OpusPacket.InternalError;
            }

            redundantRng = _celt.FinalRange;
        }

        // Signalling the mode in the first byte.
        data[offset] = GenToc(_mode, _fs / frameSize, currBandwidth, _streamChannels);

        _rangeFinal = enc.Range ^ redundantRng;

        _prevMode = toCelt != 0 ? ModeCeltOnly : _mode;
        _prevChannels = _streamChannels;
        _prevFramesize = frameSize;

        _first = 0;

        // DTX decision.
        if (_useDtx != 0 && (analysisInfo.Valid || isSilence))
        {
            if (DecideDtxMode(activity, ref _nbNoActivityMsQ1, 2 * 1000 * frameSize / _fs))
            {
                _rangeFinal = 0;
                data[offset] = GenToc(_mode, _fs / frameSize, currBandwidth, _streamChannels);
                return 1;
            }
        }
        else
        {
            _nbNoActivityMsQ1 = 0;
        }

        // In the unlikely case that the SILK encoder busted its target, tell the decoder to call the PLC.
        if (enc.Tell() > (maxDataBytes - 1) * 8)
        {
            if (maxDataBytes < 2)
            {
                return OpusPacket.BufferTooSmall;
            }

            data[offset + 1] = 0;
            ret = 1;
            _rangeFinal = 0;
        }
        else if (_mode == ModeSilkOnly && redundancy == 0)
        {
            // When in LPC only mode it's perfectly reasonable to strip off trailing zero bytes as the required range decoder
            // behavior is to fill these in. This can't be done when the MDCT modes are used because the decoder needs to know
            // the actual length for allocation purposes.
            while (ret > 2 && data[offset + ret] == 0)
            {
                ret--;
            }
        }

        // Count ToC and redundancy.
        ret += 1 + redundancyBytes;
        if (_useVbr == 0)
        {
            if (_padRp.Pad(data.AsSpan(offset), ret, maxDataBytes) != 0)
            {
                return OpusPacket.InternalError;
            }

            ret = maxDataBytes;
        }

        return ret;
    }

    // OPUS_SET_BITRATE on the CELT encoder: values of 500 or less are refused, larger ones capped at 260 kbit/s per channel.
    private void SetCeltBitrate(int value)
    {
        if (value <= 500 && value != BitrateMax)
        {
            return;
        }

        _celt.Bitrate = value == BitrateMax ? value : Math.Min(value, 260000 * _channels);
    }

    // CELT_SET_PREDICTION.
    private void SetCeltPrediction(int value)
    {
        _celt.DisablePrefilter = value <= 1;
        _celt.ForceIntra = value == 0;
    }

    private static byte GenToc(int mode, int framerate, int bandwidth, int channels)
    {
        int period = 0;
        while (framerate < 400)
        {
            framerate <<= 1;
            period++;
        }

        int toc;
        if (mode == ModeSilkOnly)
        {
            toc = (bandwidth - BandwidthNarrowband) << 5;
            toc |= (period - 2) << 3;
        }
        else if (mode == ModeCeltOnly)
        {
            int tmp = bandwidth - BandwidthMediumband;
            if (tmp < 0)
            {
                tmp = 0;
            }

            toc = 0x80;
            toc |= tmp << 5;
            toc |= period << 3;
        }
        else
        {
            // Hybrid.
            toc = 0x60;
            toc |= (bandwidth - BandwidthSuperwideband) << 4;
            toc |= (period - 2) << 3;
        }

        toc |= (channels == 2 ? 1 : 0) << 2;
        return (byte)toc;
    }

    // silk_biquad_float: second-order ARMA filter with float state, one channel of an interleaved buffer.
    private static void BiquadFloat(ReadOnlySpan<float> input, ReadOnlySpan<int> bQ28, ReadOnlySpan<int> aQ28, Span<float> s, Span<float> output,
        int len, int stride)
    {
        float a0 = aQ28[0] * (1f / (1 << 28));
        float a1 = aQ28[1] * (1f / (1 << 28));
        float b0 = bQ28[0] * (1f / (1 << 28));
        float b1 = bQ28[1] * (1f / (1 << 28));
        float b2 = bQ28[2] * (1f / (1 << 28));

        // Negate A_Q28 values and split in two parts.
        for (int k = 0; k < len; k++)
        {
            // S[ 0 ], S[ 1 ]: Q12.
            float inval = input[k * stride];
            float vout = s[0] + (b0 * inval);

            s[0] = s[1] - (vout * a0) + (b1 * inval);

            s[1] = (-vout * a1) + (b2 * inval) + VerySmall;

            // Scale back to Q0 and saturate.
            output[k * stride] = vout;
        }
    }

    // hp_cutoff: the variable high-pass of the VoIP application.
    private static void HpCutoff(ReadOnlySpan<float> input, int cutoffHz, Span<float> output, float[] hpMem, int len, int channels, int fs)
    {
        Span<int> bQ28 = stackalloc int[3];
        Span<int> aQ28 = stackalloc int[2];

        int fcQ19 = Smulbb(SilkEncoderQuant.FixConst(1.5 * 3.14159 / 1000, 19), cutoffHz) / (fs / 1000);

        int rQ28 = SilkEncoderQuant.FixConst(1.0, 28) - Mul(SilkEncoderQuant.FixConst(0.92, 9), fcQ19);

        // b = r * [ 1; -2; 1 ]; a = [ 1; -2 * r * ( 1 - 0.5 * Fc^2 ); r^2 ].
        bQ28[0] = rQ28;
        bQ28[1] = LShift(-rQ28, 1);
        bQ28[2] = rQ28;

        // -r * ( 2 - Fc * Fc ).
        int rQ22 = rQ28 >> 6;
        aQ28[0] = Smulww(rQ22, Smulww(fcQ19, fcQ19) - SilkEncoderQuant.FixConst(2.0, 22));
        aQ28[1] = Smulww(rQ22, rQ22);

        BiquadFloat(input, bQ28, aQ28, hpMem.AsSpan(0, 2), output, len, channels);
        if (channels == 2)
        {
            BiquadFloat(input[1..], bQ28, aQ28, hpMem.AsSpan(2, 2), output[1..], len, channels);
        }
    }

    // dc_reject: a 3 Hz first-order high-pass.
    private static void DcReject(ReadOnlySpan<float> input, int cutoffHz, Span<float> output, float[] hpMem, int len, int channels, int fs)
    {
        float coef = 6.3f * cutoffHz / fs;
        float coef2 = 1 - coef;
        if (channels == 2)
        {
            float m0 = hpMem[0];
            float m2 = hpMem[2];
            for (int i = 0; i < len; i++)
            {
                float x0 = input[2 * i];
                float x1 = input[(2 * i) + 1];
                float out0 = x0 - m0;
                float out1 = x1 - m2;
                m0 = (coef * x0) + VerySmall + (coef2 * m0);
                m2 = (coef * x1) + VerySmall + (coef2 * m2);
                output[2 * i] = out0;
                output[(2 * i) + 1] = out1;
            }

            hpMem[0] = m0;
            hpMem[2] = m2;
        }
        else
        {
            float m0 = hpMem[0];
            for (int i = 0; i < len; i++)
            {
                float x = input[i];
                float y = x - m0;
                m0 = (coef * x) + VerySmall + (coef2 * m0);
                output[i] = y;
            }

            hpMem[0] = m0;
        }
    }

    private static void StereoFade(ReadOnlySpan<float> input, Span<float> output, float g1, float g2, int overlap48, int frameSize, int channels, int fs)
    {
        ReadOnlySpan<float> window = CeltTables.Window120;
        int inc = 48000 / fs;
        int overlap = overlap48 / inc;
        g1 = 1f - g1;
        g2 = 1f - g2;
        int i;
        for (i = 0; i < overlap; i++)
        {
            float w = window[i * inc] * window[i * inc];
            float g = (w * g2) + ((1f - w) * g1);
            float diff = .5f * (input[i * channels] - input[(i * channels) + 1]);
            diff = g * diff;
            output[i * channels] = output[i * channels] - diff;
            output[(i * channels) + 1] = output[(i * channels) + 1] + diff;
        }

        for (; i < frameSize; i++)
        {
            float diff = .5f * (input[i * channels] - input[(i * channels) + 1]);
            diff = g2 * diff;
            output[i * channels] = output[i * channels] - diff;
            output[(i * channels) + 1] = output[(i * channels) + 1] + diff;
        }
    }

    private static void GainFade(ReadOnlySpan<float> input, Span<float> output, float g1, float g2, int overlap48, int frameSize, int channels, int fs)
    {
        ReadOnlySpan<float> window = CeltTables.Window120;
        int inc = 48000 / fs;
        int overlap = overlap48 / inc;
        if (channels == 1)
        {
            for (int i = 0; i < overlap; i++)
            {
                float w = window[i * inc] * window[i * inc];
                float g = (w * g2) + ((1f - w) * g1);
                output[i] = g * input[i];
            }
        }
        else
        {
            for (int i = 0; i < overlap; i++)
            {
                float w = window[i * inc] * window[i * inc];
                float g = (w * g2) + ((1f - w) * g1);
                output[i * 2] = g * input[i * 2];
                output[(i * 2) + 1] = g * input[(i * 2) + 1];
            }
        }

        int c = 0;
        do
        {
            for (int i = overlap; i < frameSize; i++)
            {
                output[(i * channels) + c] = g2 * input[(i * channels) + c];
            }
        }
        while (++c < channels);
    }

    // compute_stereo_width: how wide the stereo image is, 0 (mono) to 1, smoothed over time.
    private float ComputeStereoWidth(ReadOnlySpan<float> pcm, int frameSize)
    {
        int frameRate = _fs / frameSize;
        float shortAlpha = 1f - (25f / Math.Max(50, frameRate));
        float xx = 0;
        float xy = 0;
        float yy = 0;
        // Unroll by 4. The frame size is always a multiple of 4.
        for (int i = 0; i < frameSize - 3; i += 4)
        {
            float pxx = 0;
            float pxy = 0;
            float pyy = 0;
            float x = pcm[2 * i];
            float y = pcm[(2 * i) + 1];
            pxx = x * x;
            pxy = x * y;
            pyy = y * y;
            x = pcm[(2 * i) + 2];
            y = pcm[(2 * i) + 3];
            pxx += x * x;
            pxy += x * y;
            pyy += y * y;
            x = pcm[(2 * i) + 4];
            y = pcm[(2 * i) + 5];
            pxx += x * x;
            pxy += x * y;
            pyy += y * y;
            x = pcm[(2 * i) + 6];
            y = pcm[(2 * i) + 7];
            pxx += x * x;
            pxy += x * y;
            pyy += y * y;

            xx += pxx;
            xy += pxy;
            yy += pyy;
        }

        if (!(xx < 1e9f) || float.IsNaN(xx) || !(yy < 1e9f) || float.IsNaN(yy))
        {
            xy = xx = yy = 0;
        }

        _widthXx += shortAlpha * (xx - _widthXx);
        _widthXy += shortAlpha * (xy - _widthXy);
        _widthYy += shortAlpha * (yy - _widthYy);
        _widthXx = Max(0, _widthXx);
        _widthXy = Max(0, _widthXy);
        _widthYy = Max(0, _widthYy);
        if (Max(_widthXx, _widthYy) > 8e-4f)
        {
            float sqrtXx = CeltMath.Sqrt(_widthXx);
            float sqrtYy = CeltMath.Sqrt(_widthYy);
            float qrrtXx = CeltMath.Sqrt(sqrtXx);
            float qrrtYy = CeltMath.Sqrt(sqrtYy);
            // Inter-channel correlation.
            _widthXy = Min(_widthXy, sqrtXx * sqrtYy);
            float corr = _widthXy / (Epsilon + (sqrtXx * sqrtYy));
            // Approximate loudness difference.
            float ldiff = 1f * MathF.Abs(qrrtXx - qrrtYy) / (Epsilon + qrrtXx + qrrtYy);
            float width = CeltMath.Sqrt(1f - (corr * corr)) * ldiff;
            // Smoothing over one second.
            _smoothedWidth += (width - _smoothedWidth) / frameRate;
            // Peak follower.
            _maxFollower = Max(_maxFollower - (.02f / frameRate), _smoothedWidth);
        }

        return Min(1f, 20 * _maxFollower);
    }

    private static int DecideFec(int useInBandFec, int packetLossPerc, int lastFec, int mode, ref int bandwidth, int rate)
    {
        if (useInBandFec == 0 || packetLossPerc == 0 || mode == ModeCeltOnly)
        {
            return 0;
        }

        int origBandwidth = bandwidth;
        while (true)
        {
            int lbrrRateThresBps = FecThresholds[2 * (bandwidth - BandwidthNarrowband)];
            int hysteresis = FecThresholds[(2 * (bandwidth - BandwidthNarrowband)) + 1];
            if (lastFec == 1)
            {
                lbrrRateThresBps -= hysteresis;
            }

            if (lastFec == 0)
            {
                lbrrRateThresBps += hysteresis;
            }

            lbrrRateThresBps = Smulwb(Mul(lbrrRateThresBps, 125 - Math.Min(packetLossPerc, 25)), SilkEncoderQuant.FixConst(0.01, 16));
            // If loss <= 5%, we look at whether we have enough rate to enable FEC. If loss > 5%, we decrease the bandwidth until
            // we can enable FEC.
            if (rate > lbrrRateThresBps)
            {
                return 1;
            }
            else if (packetLossPerc <= 5)
            {
                return 0;
            }
            else if (bandwidth > BandwidthNarrowband)
            {
                bandwidth--;
            }
            else
            {
                break;
            }
        }

        // Couldn't find any bandwidth to enable FEC, keep original bandwidth.
        bandwidth = origBandwidth;
        return 0;
    }

    private static int ComputeSilkRateForHybrid(int rate, int bandwidth, int frame20Ms, int vbr, int fec, int channels)
    {
        ReadOnlySpan<int> rateTable = HybridRateTable;
        const int N = 7;
        int silkRate;
        rate /= channels;
        int entry = 1 + frame20Ms + (2 * fec);
        int i;
        for (i = 1; i < N; i++)
        {
            if (rateTable[i * 5] > rate)
            {
                break;
            }
        }

        if (i == N)
        {
            silkRate = rateTable[((i - 1) * 5) + entry];
            // For now, just give 50% of the extra bits to SILK.
            silkRate += (rate - rateTable[(i - 1) * 5]) / 2;
        }
        else
        {
            int lo = rateTable[((i - 1) * 5) + entry];
            int hi = rateTable[(i * 5) + entry];
            int x0 = rateTable[(i - 1) * 5];
            int x1 = rateTable[i * 5];
            silkRate = ((lo * (x1 - rate)) + (hi * (rate - x0))) / (x1 - x0);
        }

        if (vbr == 0)
        {
            // Tiny boost to SILK for CBR. We should probably tune this better.
            silkRate += 100;
        }

        if (bandwidth == BandwidthSuperwideband)
        {
            silkRate += 300;
        }

        silkRate *= channels;
        // Small adjustment for stereo (calibrated for 32 kb/s, haven't tried other bitrates).
        if (channels == 2 && rate >= 12000)
        {
            silkRate -= 1000;
        }

        return silkRate;
    }

    // compute_equiv_rate: the equivalent bitrate at 20 ms, complexity 10, VBR and no loss.
    private static int ComputeEquivRate(int bitrate, int channels, int frameRate, int vbr, int mode, int complexity, int loss)
    {
        int equiv = bitrate;
        // Take into account overhead from smaller frames.
        if (frameRate > 50)
        {
            equiv -= ((40 * channels) + 20) * (frameRate - 50);
        }

        // CBR is about a 8% penalty for both SILK and CELT.
        if (vbr == 0)
        {
            equiv -= equiv / 12;
        }

        // Complexity makes about 10% difference (from 0 to 10) in general.
        equiv = equiv * (90 + complexity) / 100;
        if (mode == ModeSilkOnly || mode == ModeHybrid)
        {
            // SILK complexity 0-1 uses the non-delayed-decision NSQ, which costs about 20%.
            if (complexity < 2)
            {
                equiv = equiv * 4 / 5;
            }

            equiv -= equiv * loss / ((6 * loss) + 10);
        }
        else if (mode == ModeCeltOnly)
        {
            // CELT complexity 0-4 doesn't have the pitch filter, which costs about 10%.
            if (complexity < 5)
            {
                equiv = equiv * 9 / 10;
            }
        }
        else
        {
            // Mode not known yet. Half the SILK loss.
            equiv -= equiv * loss / ((12 * loss) + 20);
        }

        return equiv;
    }

    private static bool IsDigitalSilence(ReadOnlySpan<float> pcm, int frameSize, int channels, int lsbDepth)
    {
        float maxval = 0;
        float minval = 0;
        int len = frameSize * channels;
        for (int i = 0; i < len; i++)
        {
            maxval = Max(maxval, pcm[i]);
            minval = Min(minval, pcm[i]);
        }

        return Max(maxval, -minval) <= (float)1 / (1 << lsbDepth);
    }

    private static float ComputeFrameEnergy(ReadOnlySpan<float> pcm, int frameSize, int channels)
    {
        int len = frameSize * channels;
        return CeltMath.InnerProduct(pcm, pcm, len) / len;
    }

    // decide_dtx_mode: true when the frame should be dropped.
    private static bool DecideDtxMode(int activity, ref int nbNoActivityMsQ1, int frameSizeMsQ1)
    {
        if (activity == 0)
        {
            // The number of consecutive DTX frames should be within the allowed bounds.
            nbNoActivityMsQ1 += frameSizeMsQ1;
            if (nbNoActivityMsQ1 > NbSpeechFramesBeforeDtx * 20 * 2)
            {
                if (nbNoActivityMsQ1 <= (NbSpeechFramesBeforeDtx + MaxConsecutiveDtx) * 20 * 2)
                {
                    // Valid frame for DTX!
                    return true;
                }
                else
                {
                    nbNoActivityMsQ1 = NbSpeechFramesBeforeDtx * 20 * 2;
                }
            }
        }
        else
        {
            nbNoActivityMsQ1 = 0;
        }

        return false;
    }

    private static int ComputeRedundancyBytes(int maxDataBytes, int bitrateBps, int frameRate, int channels)
    {
        int baseBits = (40 * channels) + 20;

        // Equivalent rate for 5 ms frames.
        int redundancyRate = bitrateBps + (baseBits * (200 - frameRate));
        // For VBR, further increase the bitrate if we can afford it. It's pretty short and we'll avoid artefacts.
        redundancyRate = 3 * redundancyRate / 2;
        int redundancyBytes = redundancyRate / 1600;

        // Compute the max rate we can use given CBR or VBR with cap.
        int availableBits = (maxDataBytes * 8) - (2 * baseBits);
        int redundancyBytesCap = ((availableBits * 240 / (240 + (48000 / frameRate))) + baseBits) / 8;
        redundancyBytes = Math.Min(redundancyBytes, redundancyBytesCap);
        // It we can't get enough bits for redundancy to be worth it, rely on the decoder PLC.
        return redundancyBytes > 4 + (8 * channels) ? Math.Min(257, redundancyBytes) : 0;
    }

    // FLOAT2INT16.
    private static short Float2Int16(float x)
    {
        x *= 32768f;
        x = Max(x, -32768);
        x = Min(x, 32767);
        return (short)MathF.Round(x);
    }

    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;
}
