using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// Encodes an Opus track: cuts the input into frames of the chosen duration, interleaves them in Vorbis channel order and codes
/// each with <see cref="OpusMultistreamEncoder"/>, mapping family 0 for mono and stereo and 1 for three to eight channels.
/// Packets are timed at 48 kHz from minus the pre-skip, the encoder's look-ahead, as Ogg and Matroska count them; at the end the
/// encoder codes silence until the look-ahead is out and gives the last packet the duration that trims it.
/// </summary>
internal sealed class OpusTrackEncoder : IAudioEncoder
{
    private static readonly Rational s_sampleTime = new(1, 48000);

    private readonly OpusMultistreamEncoder _encoder;
    private readonly AudioChunker _chunker;
    private readonly Resampler? _resampler;
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly int[] _order;
    private readonly int _channels;
    private readonly int _scale;
    private readonly int _preSkip;
    private readonly float[] _pcm;
    private readonly byte[] _data;
    private readonly OpusHead _head;
    private long _origin;
    private bool _started;
    private long _samplesIn;
    private long _samplesCoded;

    public OpusTrackEncoder(AudioEncoderOptions options, MediaContext context)
    {
        OpusEncoderOptions opus = options.Opus ?? OpusEncoderOptions.Default;
        int rate = options.SampleRate;
        if (rate < 1)
        {
            throw new NotSupportedException($"The sample rate must be positive, not {rate}.");
        }

        if (rate is not (8000 or 12000 or 16000 or 24000 or 48000))
        {
            // Opus codes five rates; others are resampled to 48 kHz, and the header keeps the rate the audio came at.
            _resampler = new Resampler(rate, 48000, options.Layout, ResamplerQuality.Good, null, SampleFormat.F32Planar, context);
            rate = 48000;
        }

        _channels = options.Layout.ChannelCount;
        if (_channels is < 1 or > 8)
        {
            throw new NotSupportedException($"Opus encodes 1 to 8 channels, not {_channels}.");
        }

        int application = opus.Application switch
        {
            OpusApplication.Voice => OpusEncoder.ApplicationVoip,
            OpusApplication.LowDelay => OpusEncoder.ApplicationRestrictedLowDelay,
            _ => OpusEncoder.ApplicationAudio,
        };
        int family = _channels <= 2 ? 0 : 1;
        _encoder = new OpusMultistreamEncoder(rate, _channels, family, application)
        {
            Bitrate = opus.Bitrate ?? 64_000 * _channels,
        };
        foreach (OpusEncoder stream in _encoder.Encoders)
        {
            stream.Complexity = opus.Effort;
            stream.Vbr = opus.RateMode != OpusRateMode.Cbr;
            stream.VbrConstraint = opus.RateMode == OpusRateMode.ConstrainedVbr;
            stream.InbandFec = opus.InbandFec ? 1 : 0;
            stream.PacketLossPercentage = opus.ExpectedLoss;
            stream.Dtx = opus.Dtx;
            if (opus.Bandwidth is { } bandwidth)
            {
                stream.MaxBandwidth = (int)bandwidth;
            }
        }

        // Frame durations are multiples of 2.5 ms, which every Opus rate divides.
        FrameSize = (int)(opus.FrameDuration.Ticks * rate / TimeSpan.TicksPerSecond);
        _scale = 48000 / rate;
        _preSkip = _encoder.Lookahead * _scale;
        (ChannelLayout layout, _order) = VorbisChannelOrder.Layout(_channels);
        _pcm = new float[FrameSize * _channels];
        _data = new byte[((6 * 1275) + 12) * _encoder.Streams];
        _chunker = new AudioChunker(SampleFormat.F32Planar, 0, layout, rate, FrameSize, context);
        _head = new OpusHead(1, _channels, _preSkip, options.SampleRate, 0, family, _encoder.Streams, _encoder.CoupledStreams, _encoder.Mapping.ToArray());
        Layout = layout;
        Track = MakeTrack();
    }

    public AudioTrackInfo Track { get; private set; }

    public int FrameSize { get; }

    private ChannelLayout Layout { get; }

    public void Send(AudioFrame frame)
    {
        if (_resampler is null)
        {
            _chunker.Push(frame);
        }
        else
        {
            _resampler.Send(frame);
            Resampled();
        }

        Drain();
    }

    public void Flush()
    {
        if (_resampler is not null)
        {
            _resampler.Flush();
            Resampled();
        }

        _chunker.Flush();
        Drain();

        // Code silence until the samples the look-ahead held back are out.
        while (_samplesIn > 0 && _samplesCoded < _samplesIn + _encoder.Lookahead)
        {
            Array.Clear(_pcm);
            EncodeFrame();
        }

        Track = MakeTrack();
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet) => _ready.TryDequeue(out packet);

    public void Dispose()
    {
        _chunker.Dispose();
        _resampler?.Dispose();
        while (_ready.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }
    }

    private AudioTrackInfo MakeTrack() => new()
    {
        Number = 1,
        Kind = TrackKind.Audio,
        CodecId = CodecIds.Opus,
        CodecPrivate = _head.Write(),
        TimeBase = s_sampleTime,
        Duration = _samplesIn > 0 ? new MediaTime(_samplesIn * _scale, s_sampleTime) : null,
        SampleRate = 48000,
        Channels = _channels,
        Layout = Layout,
        CodecDelay = new MediaTime(_preSkip, s_sampleTime),
        SeekPreRoll = new MediaTime(80, new Rational(1, 1000)),
    };

    private void Resampled()
    {
        while (_resampler!.TryReceive(out AudioFrame? frame))
        {
            using (frame)
            {
                _chunker.Push(frame);
            }
        }
    }

    private void Drain()
    {
        while (_chunker.TryTake(out AudioFrame? frame))
        {
            using (frame)
            {
                if (!_started)
                {
                    _started = true;
                    _origin = frame.Timestamp.Rescale(s_sampleTime).Ticks;
                }

                int n = frame.SampleCount;
                Span<float> pcm = _pcm;
                for (int c = 0; c < _channels; c++)
                {
                    ReadOnlySpan<float> plane = frame.AsSpan<float>(_order[c]);
                    for (int i = 0, j = c; i < n; i++, j += _channels)
                    {
                        pcm[j] = plane[i];
                    }
                }

                if (n < FrameSize)
                {
                    // The last frame: pad it with silence.
                    pcm[(n * _channels)..].Clear();
                }

                _samplesIn += n;
                EncodeFrame();
            }
        }
    }

    private void EncodeFrame()
    {
        int bytes = _encoder.Encode(_pcm, FrameSize, _data, 0, _data.Length);
        if (bytes < 0)
        {
            throw new InvalidOperationException($"The Opus encoder failed (error {bytes}).");
        }

        long start = (_samplesCoded * _scale) - _preSkip;
        _samplesCoded += FrameSize;

        // The last packet ends where the input does; the decoder drops what it decodes past that.
        long duration = FrameSize * _scale;
        long end = _samplesIn * _scale;
        if (_samplesCoded >= _samplesIn + _encoder.Lookahead && start + duration > end)
        {
            duration = Math.Max(0, end - start);
        }

        EncodedPacket packet = EncodedPacket.Rent(1, bytes, new MediaTime(_origin + start, s_sampleTime));
        _data.AsSpan(0, bytes).CopyTo(packet.GetWritableData().Span);
        packet.Duration = new MediaTime(duration, s_sampleTime);
        packet.IsKeyframe = true;
        _ready.Enqueue(packet);
    }
}
