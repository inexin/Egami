// Translated from libopus 1.5.2 src/opus_multistream_encoder.c and the layout helpers of src/opus_multistream.c (float build,
// mapping families 0 and 1).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// OpusMSEncoder: one to eight channels as Opus streams in one packet, laid out by channel mapping family 0 (mono or stereo) or 1
/// (Vorbis order, up to 7.1). For surround input it measures how much each channel masks the others and hands the masks to the
/// stream encoders, splits the bitrate between coupled, mono and LFE streams and codes the LFE narrowband.
/// </summary>
internal sealed class OpusMultistreamEncoder
{
    private const int MsFrameTmp = (6 * 1275) + 12;
    private const int Overlap = 120;

    // vorbis_mappings[8]: streams, coupled streams, mapping.
    private static readonly (int Streams, int Coupled, byte[] Mapping)[] VorbisMappings =
    [
        (1, 0, [0]),
        (1, 1, [0, 1]),
        (2, 1, [0, 2, 1]),
        (2, 2, [0, 1, 2, 3]),
        (3, 2, [0, 4, 1, 2, 3]),
        (4, 2, [0, 4, 1, 2, 3, 5]),
        (4, 3, [0, 4, 1, 2, 3, 5, 6]),
        (5, 3, [0, 6, 1, 2, 3, 4, 5, 7]),
    ];

    private static ReadOnlySpan<float> DiffTable => [0.5000000f, 0.2924813f, 0.1609640f, 0.0849625f, 0.0437314f, 0.0221971f, 0.0111839f, 0.0056136f, 0.0028123f, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly OpusEncoder[] _encoders;
    private readonly int _channels;
    private readonly int _streams;
    private readonly int _coupledStreams;
    private readonly byte[] _mapping;
    private readonly int _lfeStream;
    private readonly bool _surround;
    private readonly int _fs;
    private int _bitrateBps;
    private int _variableDuration;
    private readonly float[] _windowMem;
    private readonly float[] _preemphMem;

    // Scratch.
    private readonly byte[] _tmpData = new byte[MsFrameTmp];
    private readonly OpusRepacketizer _rp = new();
    private readonly float[] _buf;
    private readonly float[] _bandSmr;
    private readonly float[] _bandLogE = new float[42];
    private readonly int[] _bitrates;
    private readonly float[] _in = new float[5760 + Overlap];
    private readonly float[] _x = new float[5760];
    private readonly float[] _freq = new float[960];

    /// <summary>
    /// opus_multistream_surround_encoder_init: <paramref name="channels"/> channels at <paramref name="sampleRate"/> with channel
    /// mapping family <paramref name="mappingFamily"/> (0 for 1 or 2 channels, 1 for 1 to 8).
    /// </summary>
    public OpusMultistreamEncoder(int sampleRate, int channels, int mappingFamily, int application)
    {
        _lfeStream = -1;
        if (mappingFamily == 0 && channels is 1 or 2)
        {
            _streams = 1;
            _coupledStreams = channels - 1;
            _mapping = channels == 1 ? [0] : [0, 1];
        }
        else if (mappingFamily == 1 && channels is >= 1 and <= 8)
        {
            (_streams, _coupledStreams, byte[] mapping) = VorbisMappings[channels - 1];
            _mapping = (byte[])mapping.Clone();
            if (channels >= 6)
            {
                _lfeStream = _streams - 1;
            }
        }
        else
        {
            throw new ArgumentException("Opus encoding supports channel mapping family 0 (1 or 2 channels) and 1 (1 to 8 channels).");
        }

        _channels = channels;
        _surround = channels > 2 && mappingFamily == 1;
        _fs = sampleRate;
        _bitrateBps = OpusEncoder.Auto;
        _variableDuration = OpusEncoder.FramesizeArg;
        _encoders = new OpusEncoder[_streams];
        for (int i = 0; i < _streams; i++)
        {
            _encoders[i] = new OpusEncoder(sampleRate, i < _coupledStreams ? 2 : 1, application);
            if (i == _lfeStream)
            {
                _encoders[i].Lfe = true;
            }
        }

        _windowMem = new float[channels * Overlap];
        _preemphMem = new float[channels];
        _buf = new float[2 * 3 * sampleRate / 25];
        _bandSmr = new float[21 * channels];
        _bitrates = new int[_streams];
    }

    /// <summary>Gets the stream count.</summary>
    public int Streams => _streams;

    /// <summary>Gets the coupled (stereo) stream count.</summary>
    public int CoupledStreams => _coupledStreams;

    /// <summary>Gets the channel mapping table of the Ogg Opus header.</summary>
    public ReadOnlySpan<byte> Mapping => _mapping;

    /// <summary>Gets the stream encoders, coupled streams first.</summary>
    public ReadOnlySpan<OpusEncoder> Encoders => _encoders;

    /// <summary>Gets or sets the total bitrate, <see cref="OpusEncoder.Auto"/> or <see cref="OpusEncoder.BitrateMax"/> (OPUS_SET_BITRATE).</summary>
    public int Bitrate
    {
        get
        {
            int value = 0;
            foreach (OpusEncoder enc in _encoders)
            {
                value += enc.Bitrate;
            }

            return value;
        }

        set
        {
            if (value != OpusEncoder.Auto && value != OpusEncoder.BitrateMax)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
                value = Math.Min(300000 * _channels, Math.Max(500 * _channels, value));
            }

            _bitrateBps = value;
        }
    }

    /// <summary>Gets or sets the frame duration rule (OPUS_SET_EXPERT_FRAME_DURATION).</summary>
    public int ExpertFrameDuration
    {
        get => _variableDuration;
        set => _variableDuration = value;
    }

    /// <summary>Gets the look-ahead in samples (OPUS_GET_LOOKAHEAD).</summary>
    public int Lookahead => _encoders[0].Lookahead;

    /// <summary>Gets the XOR of the streams' final ranges (OPUS_GET_FINAL_RANGE).</summary>
    public uint FinalRange
    {
        get
        {
            uint value = 0;
            foreach (OpusEncoder enc in _encoders)
            {
                value ^= enc.FinalRange;
            }

            return value;
        }
    }

    /// <summary>OPUS_RESET_STATE.</summary>
    public void Reset()
    {
        if (_surround)
        {
            Array.Clear(_preemphMem);
            Array.Clear(_windowMem);
        }

        foreach (OpusEncoder enc in _encoders)
        {
            enc.Reset();
        }
    }

    /// <summary>
    /// opus_multistream_encode_float: encodes <paramref name="analysisFrameSize"/> samples per channel of interleaved
    /// <paramref name="pcm"/> into at most <paramref name="maxDataBytes"/> bytes of <paramref name="data"/> from
    /// <paramref name="offset"/>. Returns the packet size or a negative <see cref="OpusPacket"/> error.
    /// </summary>
    public int Encode(ReadOnlySpan<float> pcm, int analysisFrameSize, byte[] data, int offset, int maxDataBytes)
    {
        const int LsbDepth = 24;
        bool vbr = _encoders[0].Vbr;
        int fs = _fs;

        int frameSize = OpusEncoder.FrameSizeSelect(analysisFrameSize, _variableDuration, fs);
        if (frameSize <= 0)
        {
            return OpusPacket.BadArgument;
        }

        // Smallest packet the encoder can produce.
        int smallestPacket = (_streams * 2) - 1;
        // 100 ms needs an extra byte per stream for the ToC.
        if (fs / frameSize == 10)
        {
            smallestPacket += _streams;
        }

        if (maxDataBytes < smallestPacket)
        {
            return OpusPacket.BufferTooSmall;
        }

        if (_surround)
        {
            SurroundAnalysis(pcm, _bandSmr, frameSize, Overlap, _channels, fs);
        }

        // Compute bitrate allocation between streams.
        int rateSum = RateAllocation(_bitrates, frameSize);

        if (!vbr)
        {
            if (_bitrateBps == OpusEncoder.Auto)
            {
                maxDataBytes = Math.Min(maxDataBytes, 3 * rateSum / (3 * 8 * fs / frameSize));
            }
            else if (_bitrateBps != OpusEncoder.BitrateMax)
            {
                maxDataBytes = Math.Min(maxDataBytes, Math.Max(smallestPacket, 3 * _bitrateBps / (3 * 8 * fs / frameSize)));
            }
        }

        for (int s = 0; s < _streams; s++)
        {
            OpusEncoder enc = _encoders[s];
            enc.Bitrate = _bitrates[s];
            if (_surround)
            {
                int equivRate = _bitrateBps;
                if (frameSize * 50 < fs)
                {
                    equivRate -= 60 * ((fs / frameSize) - 50) * _channels;
                }

                if (equivRate > 10000 * _channels)
                {
                    enc.Bandwidth = OpusEncoder.BandwidthFullband;
                }
                else if (equivRate > 7000 * _channels)
                {
                    enc.Bandwidth = OpusEncoder.BandwidthSuperwideband;
                }
                else if (equivRate > 5000 * _channels)
                {
                    enc.Bandwidth = OpusEncoder.BandwidthWideband;
                }
                else
                {
                    enc.Bandwidth = OpusEncoder.BandwidthNarrowband;
                }

                if (s < _coupledStreams)
                {
                    // To preserve the spatial image, force stereo CELT on coupled streams.
                    enc.ForceMode = OpusEncoder.ModeCeltOnly;
                    enc.ForceChannels = 2;
                }
            }
        }

        // Counting ToC.
        int totSize = 0;
        Span<float> buf = _buf;
        for (int s = 0; s < _streams; s++)
        {
            int c1;
            int c2;
            _rp.Init();
            OpusEncoder enc = _encoders[s];
            if (s < _coupledStreams)
            {
                int left = GetLeftChannel(s);
                int right = GetRightChannel(s);
                CopyChannelIn(buf, 2, pcm, _channels, left, frameSize);
                CopyChannelIn(buf[1..], 2, pcm, _channels, right, frameSize);
                if (_surround)
                {
                    for (int i = 0; i < 21; i++)
                    {
                        _bandLogE[i] = _bandSmr[(21 * left) + i];
                        _bandLogE[21 + i] = _bandSmr[(21 * right) + i];
                    }
                }

                c1 = left;
                c2 = right;
            }
            else
            {
                int chan = GetMonoChannel(s);
                CopyChannelIn(buf, 1, pcm, _channels, chan, frameSize);
                if (_surround)
                {
                    for (int i = 0; i < 21; i++)
                    {
                        _bandLogE[i] = _bandSmr[(21 * chan) + i];
                    }
                }

                c1 = chan;
                c2 = -1;
            }

            if (_surround)
            {
                enc.SetEnergyMask(_bandLogE);
            }

            // Number of bytes left (+Toc).
            int currMax = maxDataBytes - totSize;
            // Reserve one byte for the last stream and two for the others.
            currMax -= Math.Max(0, (2 * (_streams - s - 1)) - 1);
            // For 100 ms, reserve an extra byte per stream for the ToC.
            if (fs / frameSize == 10)
            {
                currMax -= _streams - s - 1;
            }

            currMax = Math.Min(currMax, MsFrameTmp);
            // Repacketizer will add one or two bytes for self-delimited frames.
            if (s != _streams - 1)
            {
                currMax -= currMax > 253 ? 2 : 1;
            }

            if (!vbr && s == _streams - 1 && currMax > 0)
            {
                enc.Bitrate = currMax * (8 * fs / frameSize);
            }

            int len = enc.EncodeNative(buf[..(frameSize * enc.Channels)], frameSize, _tmpData, 0, currMax, LsbDepth, pcm, analysisFrameSize, c1, c2,
                _channels, true);
            if (len < 0)
            {
                return len;
            }

            // We need to use the repacketizer to add the self-delimiting lengths while taking into account the fact that the
            // encoder can now return more than one frame at a time (e.g. 60 ms CELT-only).
            if (_rp.Cat(_tmpData.AsSpan(0, len)) != 0)
            {
                return OpusPacket.InternalError;
            }

            len = _rp.Out(0, _rp.FrameCount, data.AsSpan(offset + totSize), maxDataBytes - totSize, s != _streams - 1, !vbr && s == _streams - 1);
            totSize += len;
        }

        return totSize;
    }

    private int GetLeftChannel(int streamId) => Array.IndexOf(_mapping, (byte)(streamId * 2));

    private int GetRightChannel(int streamId) => Array.IndexOf(_mapping, (byte)((streamId * 2) + 1));

    private int GetMonoChannel(int streamId) => Array.IndexOf(_mapping, (byte)(streamId + _coupledStreams));

    private static void CopyChannelIn(Span<float> dst, int dstStride, ReadOnlySpan<float> src, int srcStride, int srcChannel, int frameSize)
    {
        for (int i = 0; i < frameSize; i++)
        {
            dst[i * dstStride] = src[(i * srcStride) + srcChannel];
        }
    }

    private int RateAllocation(Span<int> rate, int frameSize)
    {
        SurroundRateAllocation(rate, frameSize, _fs);
        int rateSum = 0;
        for (int i = 0; i < _streams; i++)
        {
            rate[i] = Math.Max(rate[i], 500);
            rateSum += rate[i];
        }

        return rateSum;
    }

    private void SurroundRateAllocation(Span<int> rate, int frameSize, int fs)
    {
        int nbLfe = _lfeStream != -1 ? 1 : 0;
        int nbCoupled = _coupledStreams;
        int nbUncoupled = _streams - nbCoupled - nbLfe;
        int nbNormal = (2 * nbCoupled) + nbUncoupled;

        // Give each non-LFE channel enough bits per channel for coding band energy.
        int channelOffset = 40 * Math.Max(50, fs / frameSize);

        int bitrate;
        if (_bitrateBps == OpusEncoder.Auto)
        {
            bitrate = (nbNormal * (channelOffset + fs + 10000)) + (8000 * nbLfe);
        }
        else if (_bitrateBps == OpusEncoder.BitrateMax)
        {
            bitrate = (nbNormal * 300000) + (nbLfe * 128000);
        }
        else
        {
            bitrate = _bitrateBps;
        }

        // Give LFE some basic stream_channel allocation but never exceed 1/20 of the total rate for the channels.
        int lfeOffset = Math.Min(bitrate / 20, 3000) + (15 * Math.Max(50, fs / frameSize));

        // We give each stream (coupled or uncoupled) a starting bitrate. This models the main saving of coupled channels over
        // uncoupled.
        int streamOffset = (bitrate - (channelOffset * nbNormal) - (lfeOffset * nbLfe)) / nbNormal / 2;
        streamOffset = Math.Max(0, Math.Min(20000, streamOffset));

        // Coupled streams get twice the mono rate after the offset is allocated.
        const int CoupledRatio = 512;
        // Should depend on the bitrate, for now we assume LFE gets 1/8 the bits of mono.
        const int LfeRatio = 32;

        // Compute bitrate allocation between streams.
        int total = (nbUncoupled << 8) + (CoupledRatio * nbCoupled) + (nbLfe * LfeRatio);
        int channelRate = (int)(256 * (long)(bitrate - (lfeOffset * nbLfe) - (streamOffset * (nbCoupled + nbUncoupled)) - (channelOffset * nbNormal)) / total);

        for (int i = 0; i < _streams; i++)
        {
            if (i < _coupledStreams)
            {
                rate[i] = (2 * channelOffset) + Math.Max(0, streamOffset + (channelRate * CoupledRatio >> 8));
            }
            else if (i != _lfeStream)
            {
                rate[i] = channelOffset + Math.Max(0, streamOffset + channelRate);
            }
            else
            {
                rate[i] = Math.Max(0, lfeOffset + (channelRate * LfeRatio >> 8));
            }
        }
    }

    // channel_pos: 0 LFE, 1 left, 2 centre, 3 right.
    private static void ChannelPos(int channels, Span<int> pos)
    {
        if (channels == 4)
        {
            pos[0] = 1;
            pos[1] = 3;
            pos[2] = 1;
            pos[3] = 3;
        }
        else if (channels is 3 or 5 or 6)
        {
            pos[0] = 1;
            pos[1] = 2;
            pos[2] = 3;
            pos[3] = 1;
            pos[4] = 3;
            pos[5] = 0;
        }
        else if (channels == 7)
        {
            pos[0] = 1;
            pos[1] = 2;
            pos[2] = 3;
            pos[3] = 1;
            pos[4] = 3;
            pos[5] = 2;
            pos[6] = 0;
        }
        else if (channels == 8)
        {
            pos[0] = 1;
            pos[1] = 2;
            pos[2] = 3;
            pos[3] = 1;
            pos[4] = 3;
            pos[5] = 1;
            pos[6] = 3;
            pos[7] = 0;
        }
    }

    // Computes a rough approximation of log2(2^a + 2^b).
    private static float LogSum(float a, float b)
    {
        float max;
        float diff;
        if (a > b)
        {
            max = a;
            diff = a - b;
        }
        else
        {
            max = b;
            diff = b - a;
        }

        // inf or NaN.
        if (!(diff < 8f))
        {
            return max;
        }

        ReadOnlySpan<float> diffTable = DiffTable;
        int low = (int)Math.Floor(2 * diff);
        float frac = (2 * diff) - low;
        return max + diffTable[low] + (frac * (diffTable[low + 1] - diffTable[low]));
    }

    // surround_analysis: per channel band energies relative to the masking of the other channels on the same side.
    private void SurroundAnalysis(ReadOnlySpan<float> pcm, Span<float> bandLogE, int len, int overlap, int channels, int rate)
    {
        Span<int> pos = stackalloc int[8];
        Span<float> bandE = stackalloc float[21];
        Span<float> tmpE = stackalloc float[21];
        Span<float> maskLogE = stackalloc float[3 * 21];

        int upsample = 48000 / rate;
        int frameSize = len * upsample;
        int freqSize = Math.Min(960, frameSize);

        // LM = log2(frame_size / 120).
        int lm;
        for (lm = 0; lm < 3; lm++)
        {
            if (CeltTables.ShortMdctSize << lm == frameSize)
            {
                break;
            }
        }

        Span<float> input = _in.AsSpan(0, frameSize + overlap);
        Span<float> x = _x.AsSpan(0, len);
        Span<float> freq = _freq.AsSpan(0, freqSize);

        ChannelPos(channels, pos);

        maskLogE.Fill(-28f);

        for (int c = 0; c < channels; c++)
        {
            int nbFrames = frameSize / freqSize;
            _windowMem.AsSpan(c * overlap, overlap).CopyTo(input);
            CopyChannelIn(x, 1, pcm, channels, c, len);
            CeltEncoder.Preemphasis(x, input[overlap..], frameSize, 1, upsample, ref _preemphMem[c], false);
            float sum = CeltMath.InnerProduct(input, input, frameSize + overlap);
            // This should filter out both NaNs and ridiculous signals that could cause NaNs further down.
            if (!(sum < 1e18f) || float.IsNaN(sum))
            {
                input.Clear();
                _preemphMem[c] = 0;
            }

            bandE.Clear();
            for (int frame = 0; frame < nbFrames; frame++)
            {
                CeltMdct.Forward(input[(960 * frame)..], freq, CeltTables.Window120, overlap, 3 - lm, 1);
                if (upsample != 1)
                {
                    int bound = freqSize / upsample;
                    int i;
                    for (i = 0; i < bound; i++)
                    {
                        freq[i] *= upsample;
                    }

                    for (; i < freqSize; i++)
                    {
                        freq[i] = 0;
                    }
                }

                CeltBandsEncoder.ComputeBandEnergies(freq, tmpE, 21, 1, lm);
                // If we have multiple frames, take the max energy.
                for (int i = 0; i < 21; i++)
                {
                    bandE[i] = bandE[i] > tmpE[i] ? bandE[i] : tmpE[i];
                }
            }

            Span<float> bandLogEc = bandLogE.Slice(21 * c, 21);
            CeltEnergy.Amp2Log2(21, 21, bandE, bandLogEc, 1);
            // Apply spreading function with -6 dB/band going up and -12 dB/band going down.
            for (int i = 1; i < 21; i++)
            {
                bandLogEc[i] = Max(bandLogEc[i], bandLogEc[i - 1] - 1f);
            }

            for (int i = 19; i >= 0; i--)
            {
                bandLogEc[i] = Max(bandLogEc[i], bandLogEc[i + 1] - 2f);
            }

            if (pos[c] == 1)
            {
                for (int i = 0; i < 21; i++)
                {
                    maskLogE[i] = LogSum(maskLogE[i], bandLogEc[i]);
                }
            }
            else if (pos[c] == 3)
            {
                for (int i = 0; i < 21; i++)
                {
                    maskLogE[42 + i] = LogSum(maskLogE[42 + i], bandLogEc[i]);
                }
            }
            else if (pos[c] == 2)
            {
                for (int i = 0; i < 21; i++)
                {
                    maskLogE[i] = LogSum(maskLogE[i], bandLogEc[i] - .5f);
                    maskLogE[42 + i] = LogSum(maskLogE[42 + i], bandLogEc[i] - .5f);
                }
            }

            input.Slice(frameSize, overlap).CopyTo(_windowMem.AsSpan(c * overlap));
        }

        for (int i = 0; i < 21; i++)
        {
            maskLogE[21 + i] = Min(maskLogE[i], maskLogE[42 + i]);
        }

        float channelOffset = .5f * CeltEnergy.Log2(2f / (channels - 1));
        for (int i = 0; i < 3 * 21; i++)
        {
            maskLogE[i] += channelOffset;
        }

        for (int c = 0; c < channels; c++)
        {
            Span<float> bandLogEc = bandLogE.Slice(21 * c, 21);
            if (pos[c] != 0)
            {
                ReadOnlySpan<float> mask = maskLogE.Slice((pos[c] - 1) * 21, 21);
                for (int i = 0; i < 21; i++)
                {
                    bandLogEc[i] -= mask[i];
                }
            }
            else
            {
                bandLogEc.Clear();
            }
        }
    }

    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;
}
