// Translated from libopus 1.5.2 src/opus_decoder.c (opus_decoder_init, opus_decode_frame, opus_decode_native, smooth_fade,
// opus_packet_get_mode, opus_packet_has_lbrr), float build, without DRED, OSCE or deep PLC.
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// One Opus stream (mono or stereo) at 48 kHz: SILK, CELT and hybrid frames, mode transitions and redundancy frames,
/// concealment of lost packets and in-band FEC, as libopus's OpusDecoder does them. Output is interleaved float.
/// </summary>
internal sealed class OpusStreamDecoder
{
    public const int SampleRate = 48000;

    private const int ModeSilkOnly = 1000;
    private const int ModeHybrid = 1001;
    private const int ModeCeltOnly = 1002;

    private const int F20 = SampleRate / 50;
    private const int F10 = F20 >> 1;
    private const int F5 = F10 >> 1;
    private const int F2_5 = F5 >> 1;

    private readonly SilkDecoder _silk = new();
    private readonly CeltDecoder _celt;
    private readonly SilkDecodeControl _control = new();
    private readonly OpusRangeDecoder _range = new();
    private readonly int[] _offsets = new int[48];
    private readonly short[] _sizes = new short[48];
    private short[] _pcmSilk = [];
    private readonly float[] _transitionSilk = new float[F5 * 2];
    private readonly float[] _transitionCelt = new float[F5 * 2];
    private byte[] _packet = new byte[1276];
    private readonly float[] _redundant = new float[F5 * 2];
    private int _streamChannels;
    private OpusBandwidth _bandwidth;
    private int _mode;
    private int _prevMode;
    private int _frameSize;
    private bool _prevRedundancy;

    public OpusStreamDecoder(int channels)
    {
        Channels = channels;
        _celt = new CeltDecoder(channels);
        _control.ApiSampleRate = SampleRate;
        _control.ChannelsApi = channels;
        Reset();
    }

    /// <summary>Gets the output channels, 1 or 2.</summary>
    public int Channels { get; }

    /// <summary>Gets or sets the output gain in Q8 dB (OPUS_SET_GAIN), from OpusHead's output gain.</summary>
    public int Gain { get; set; }

    /// <summary>Gets the final range of the last packet, which libopus reports for conformance testing.</summary>
    public uint FinalRange { get; private set; }

    /// <summary>Gets the length of the last packet decoded, in samples per channel.</summary>
    public int LastPacketDuration { get; private set; }

    public void Reset()
    {
        _silk.Reset();
        _celt.Reset();
        _streamChannels = Channels;
        _bandwidth = 0;
        _mode = 0;
        _prevMode = 0;
        _frameSize = SampleRate / 400;
        _prevRedundancy = false;
        LastPacketDuration = 0;
        FinalRange = 0;
    }

    /// <summary>
    /// Decodes a packet (or conceals a lost one when <paramref name="data"/> is empty) into <paramref name="pcm"/>, at most
    /// <paramref name="frameSize"/> samples per channel. With <paramref name="decodeFec"/>, decodes the in-band FEC the packet
    /// carries for the one before it. <paramref name="selfDelimited"/> is for all but the last stream of a multistream packet;
    /// <paramref name="packetLength"/> receives the bytes it took. Returns samples per channel or a negative error.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> data, Span<float> pcm, int frameSize, bool decodeFec, bool selfDelimited, out int packetLength)
    {
        packetLength = 0;
        if ((decodeFec || data.IsEmpty) && frameSize % (SampleRate / 400) != 0)
        {
            return OpusPacket.BadArgument;
        }

        if (data.IsEmpty)
        {
            int count = 0;
            do
            {
                int ret = DecodeFrame(null, 0, 0, pcm[(count * Channels)..], frameSize - count, false);
                if (ret < 0)
                {
                    return ret;
                }

                count += ret;
            }
            while (count < frameSize);
            LastPacketDuration = count;
            return count;
        }

        int packetMode = Mode(data[0]);
        OpusBandwidth packetBandwidth = OpusPacket.Bandwidth(data[0]);
        int packetFrameSize = OpusPacket.SamplesPerFrame(data[0], SampleRate);
        int packetChannels = OpusPacket.Channels(data[0]);
        int frames = OpusPacket.Parse(data, selfDelimited, out _, _offsets, _sizes, out _, out packetLength);
        if (frames < 0)
        {
            return frames;
        }

        // The frames are decoded from a private copy, so the range decoder can hold on to it.
        if (_packet.Length < packetLength)
        {
            _packet = new byte[packetLength];
        }

        data[..packetLength].CopyTo(_packet);
        byte[] buffer = _packet;
        if (decodeFec)
        {
            if (frameSize < packetFrameSize || packetMode == ModeCeltOnly || _mode == ModeCeltOnly)
            {
                return Decode([], pcm, frameSize, false, false, out _);
            }

            int durationCopy = LastPacketDuration;
            if (frameSize - packetFrameSize != 0)
            {
                int ret = Decode([], pcm, frameSize - packetFrameSize, false, false, out _);
                if (ret < 0)
                {
                    LastPacketDuration = durationCopy;
                    return ret;
                }
            }

            _mode = packetMode;
            _bandwidth = packetBandwidth;
            _frameSize = packetFrameSize;
            _streamChannels = packetChannels;
            int fec = DecodeFrame(buffer, _offsets[0], _sizes[0], pcm[(Channels * (frameSize - packetFrameSize))..], packetFrameSize, true);
            if (fec < 0)
            {
                return fec;
            }

            LastPacketDuration = frameSize;
            return frameSize;
        }

        if (frames * packetFrameSize > frameSize)
        {
            return OpusPacket.BufferTooSmall;
        }

        _mode = packetMode;
        _bandwidth = packetBandwidth;
        _frameSize = packetFrameSize;
        _streamChannels = packetChannels;
        int samples = 0;
        for (int i = 0; i < frames; i++)
        {
            int ret = DecodeFrame(buffer, _offsets[i], _sizes[i], pcm[(samples * Channels)..], frameSize - samples, false);
            if (ret < 0)
            {
                return ret;
            }

            samples += ret;
        }

        LastPacketDuration = samples;
        return samples;
    }

    /// <summary>Whether a packet carries LBRR (in-band FEC) data for the packet before it.</summary>
    public static bool HasLbrr(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty || Mode(packet[0]) == ModeCeltOnly)
        {
            return false;
        }

        int frameSize = OpusPacket.SamplesPerFrame(packet[0], 48000);
        int frames = frameSize > 960 ? frameSize / 960 : 1;
        Span<int> offsets = stackalloc int[48];
        Span<short> sizes = stackalloc short[48];
        if (OpusPacket.Parse(packet, false, out _, offsets, sizes, out _, out _) <= 0 || sizes[0] == 0)
        {
            return false;
        }

        byte first = packet[offsets[0]];
        bool lbrr = ((first >> (7 - frames)) & 1) != 0;
        if (OpusPacket.Channels(packet[0]) == 2)
        {
            lbrr |= ((first >> (6 - (2 * frames))) & 1) != 0;
        }

        return lbrr;
    }

    private static int Mode(byte toc) => (toc & 0x80) != 0 ? ModeCeltOnly : (toc & 0x60) == 0x60 ? ModeHybrid : ModeSilkOnly;

    private static void SmoothFade(ReadOnlySpan<float> in1, ReadOnlySpan<float> in2, Span<float> output, int overlap, int channels, ReadOnlySpan<float> window)
    {
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < overlap; i++)
            {
                float w = window[i] * window[i];
                output[(i * channels) + c] = (w * in2[(i * channels) + c]) + ((1f - w) * in1[(i * channels) + c]);
            }
        }
    }

    private int DecodeFrame(byte[]? data, int offset, int length, Span<float> pcm, int frameSize, bool decodeFec)
    {
        int channels = Channels;
        if (frameSize < F2_5)
        {
            return OpusPacket.BufferTooSmall;
        }

        frameSize = Math.Min(frameSize, SampleRate / 25 * 3);
        if (length <= 1)
        {
            data = null;
            frameSize = Math.Min(frameSize, _frameSize);
        }

        int audioSize;
        int mode;
        OpusBandwidth bandwidth;
        OpusRangeDecoder dec = _range;
        if (data is not null)
        {
            audioSize = _frameSize;
            mode = _mode;
            bandwidth = _bandwidth;
            dec.Init(data, offset, length);
        }
        else
        {
            audioSize = frameSize;
            mode = _prevRedundancy ? ModeCeltOnly : _prevMode;
            bandwidth = 0;
            if (mode == 0)
            {
                pcm[..(audioSize * channels)].Clear();
                return audioSize;
            }

            if (audioSize > F20)
            {
                int left = audioSize;
                int at = 0;
                do
                {
                    int ret = DecodeFrame(null, 0, 0, pcm[(at * channels)..], Math.Min(left, F20), false);
                    if (ret < 0)
                    {
                        return ret;
                    }

                    at += ret;
                    left -= ret;
                }
                while (left > 0);
                return frameSize;
            }

            if (audioSize < F20)
            {
                if (audioSize > F10)
                {
                    audioSize = F10;
                }
                else if (mode != ModeSilkOnly && audioSize > F5 && audioSize < F10)
                {
                    audioSize = F5;
                }
            }
        }

        bool transition = false;
        Span<float> pcmTransition = default;
        if (data is not null && _prevMode > 0 && ((mode == ModeCeltOnly && _prevMode != ModeCeltOnly && !_prevRedundancy) || (mode != ModeCeltOnly && _prevMode == ModeCeltOnly)))
        {
            transition = true;
        }

        if (transition && mode == ModeCeltOnly)
        {
            pcmTransition = _transitionCelt.AsSpan(0, F5 * channels);
            DecodeFrame(null, 0, 0, pcmTransition, Math.Min(F5, audioSize), false);
        }

        if (audioSize > frameSize)
        {
            return OpusPacket.BadArgument;
        }

        frameSize = audioSize;

        if (mode != ModeCeltOnly)
        {
            int size = Math.Max(F10, frameSize) * channels;
            if (_pcmSilk.Length < size)
            {
                _pcmSilk = new short[size];
            }

            Span<short> silkPcm = _pcmSilk.AsSpan(0, size);
            if (_prevMode == ModeCeltOnly)
            {
                _silk.Reset();
            }

            _control.PayloadSizeMs = Math.Max(10, 1000 * audioSize / SampleRate);
            if (data is not null)
            {
                _control.ChannelsInternal = _streamChannels;
                _control.InternalSampleRate = mode == ModeSilkOnly
                    ? bandwidth switch
                    {
                        OpusBandwidth.Narrowband => 8000,
                        OpusBandwidth.Mediumband => 12000,
                        _ => 16000,
                    }
                    : 16000;
            }

            int lostFlag = data is null ? 1 : decodeFec ? 2 : 0;
            int decoded = 0;
            do
            {
                bool firstFrame = decoded == 0;
                int silkRet = _silk.Decode(_control, lostFlag, firstFrame, dec, silkPcm[(decoded * channels)..], out int silkFrameSize);
                if (silkRet != 0)
                {
                    if (lostFlag != 0)
                    {
                        silkFrameSize = frameSize;
                        silkPcm.Slice(decoded * channels, frameSize * channels).Clear();
                    }
                    else
                    {
                        return OpusPacket.InternalError;
                    }
                }

                decoded += silkFrameSize;
            }
            while (decoded < frameSize);
        }

        int startBand = 0;
        bool redundancy = false;
        int redundancyBytes = 0;
        bool celtToSilk = false;
        if (!decodeFec && mode != ModeCeltOnly && data is not null && dec.Tell() + 17 + (20 * (mode == ModeHybrid ? 1 : 0)) <= 8 * length)
        {
            redundancy = mode != ModeHybrid || dec.DecodeBitLogp(12);
            if (redundancy)
            {
                celtToSilk = dec.DecodeBitLogp(1);
                redundancyBytes = mode == ModeHybrid ? (int)dec.DecodeUint(256) + 2 : length - ((dec.Tell() + 7) >> 3);
                length -= redundancyBytes;
                if (length * 8 < dec.Tell())
                {
                    length = 0;
                    redundancyBytes = 0;
                    redundancy = false;
                }

                dec.SetStorage(dec.Storage - (uint)redundancyBytes);
            }
        }

        if (mode != ModeCeltOnly)
        {
            startBand = 17;
        }

        if (redundancy)
        {
            transition = false;
        }

        if (transition && mode != ModeCeltOnly)
        {
            pcmTransition = _transitionSilk.AsSpan(0, F5 * channels);
            DecodeFrame(null, 0, 0, pcmTransition, Math.Min(F5, audioSize), false);
        }

        if (bandwidth != 0)
        {
            _celt.EndBand = bandwidth switch
            {
                OpusBandwidth.Narrowband => 13,
                OpusBandwidth.Mediumband or OpusBandwidth.Wideband => 17,
                OpusBandwidth.Superwideband => 19,
                _ => 21,
            };
        }

        _celt.StreamChannels = _streamChannels;
        uint redundantRange = 0;
        Span<float> redundant = _redundant.AsSpan(0, F5 * channels);
        if (redundancy && celtToSilk)
        {
            _celt.StartBand = 0;
            _celt.Decode(data, offset + length, redundancyBytes, redundant, F5, null, false);
            redundantRange = _celt.FinalRange;
        }

        _celt.StartBand = startBand;
        int celtRet = 0;
        if (mode != ModeSilkOnly)
        {
            int celtFrameSize = Math.Min(F20, frameSize);
            if (mode != _prevMode && _prevMode > 0 && !_prevRedundancy)
            {
                _celt.Reset();
            }

            celtRet = _celt.Decode(decodeFec ? null : data, offset, length, pcm[..(celtFrameSize * channels)], celtFrameSize, dec, false);
        }
        else
        {
            pcm[..(frameSize * channels)].Clear();
            if (_prevMode == ModeHybrid && !(redundancy && celtToSilk && _prevRedundancy))
            {
                // A CELT silence frame lets the MDCT fade out after hybrid frames.
                _celt.StartBand = 0;
                _celt.Decode([0xFF, 0xFF], 0, 2, pcm[..(F2_5 * channels)], F2_5, null, false);
            }
        }

        if (mode != ModeCeltOnly)
        {
            ReadOnlySpan<short> silkPcm = _pcmSilk;
            for (int i = 0; i < frameSize * channels; i++)
            {
                pcm[i] += (1f / 32768f) * silkPcm[i];
            }
        }

        ReadOnlySpan<float> window = CeltDecoder.Window;
        if (redundancy && !celtToSilk)
        {
            _celt.Reset();
            _celt.StartBand = 0;
            _celt.Decode(data, offset + length, redundancyBytes, redundant, F5, null, false);
            redundantRange = _celt.FinalRange;
            Span<float> tail = pcm.Slice(channels * (frameSize - F2_5), channels * F2_5);
            SmoothFade(tail, redundant[(channels * F2_5)..], tail, F2_5, channels, window);
        }

        if (redundancy && celtToSilk && (_prevMode != ModeSilkOnly || _prevRedundancy))
        {
            redundant[..(channels * F2_5)].CopyTo(pcm);
            Span<float> rest = pcm.Slice(channels * F2_5, channels * F2_5);
            SmoothFade(redundant[(channels * F2_5)..], rest, rest, F2_5, channels, window);
        }

        if (transition)
        {
            if (audioSize >= F5)
            {
                pcmTransition[..(channels * F2_5)].CopyTo(pcm);
                Span<float> rest = pcm.Slice(channels * F2_5, channels * F2_5);
                SmoothFade(pcmTransition[(channels * F2_5)..], rest, rest, F2_5, channels, window);
            }
            else
            {
                SmoothFade(pcmTransition, pcm, pcm, F2_5, channels, window);
            }
        }

        if (Gain != 0)
        {
            float gain = MathF.Pow(2, 6.48814081e-4f * Gain);
            for (int i = 0; i < frameSize * channels; i++)
            {
                pcm[i] *= gain;
            }
        }

        FinalRange = length <= 1 ? 0 : dec.Range ^ redundantRange;
        _prevMode = mode;
        _prevRedundancy = redundancy && !celtToSilk;
        return celtRet < 0 ? celtRet : audioSize;
    }
}
