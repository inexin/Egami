using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// Encodes an AAC LC track: cuts the input into blocks of 1024 samples per channel in the order of its channel configuration,
/// codes each with <see cref="AacFrameEncoder"/> and gives each raw data block as a packet. The transform overlaps a block with
/// the one before, so the first packet decodes to 1024 samples of silence: packets are timed from minus that priming, which MP4
/// (edit list and iTunSMPB) and Matroska (CodecDelay) record, and the last one's duration trims the padding after the input.
/// </summary>
internal sealed class AacEncoder : IAudioEncoder
{
    private const int Priming = AacFrameEncoder.FrameLength;

    private readonly AacFrameEncoder _encoder;
    private readonly AudioChunker _chunker;
    private readonly Resampler? _resampler;
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly float[][] _planes;
    private readonly Rational _sampleTime;
    private readonly int _rate;
    private long _origin;
    private bool _started;
    private long _samplesIn;
    private long _packets;

    public AacEncoder(AudioEncoderOptions options, MediaContext context)
    {
        AacEncoderOptions aac = options.Aac ?? AacEncoderOptions.Default;
        int rate = options.SampleRate;
        if (rate < 1)
        {
            throw new NotSupportedException($"The sample rate must be positive, not {rate}.");
        }

        int channels = options.Layout.ChannelCount;
        int configuration = channels switch
        {
            >= 1 and <= 6 => channels,
            8 => 7,
            _ => throw new NotSupportedException($"AAC encodes 1 to 6 or 8 channels, not {channels}."),
        };

        if (options.Layout.IsPositioned && options.Layout.Speakers != AacConfig.LayoutOf(configuration)!.Value.Speakers)
        {
            // Without a program config element each count has one set of speakers.
            throw new NotSupportedException($"AAC codes {channels} channels as {AacConfig.LayoutOf(configuration)!.Value}, not {options.Layout}.");
        }

        if (AacConfig.RateIndex(rate) < 0 || rate == 7350)
        {
            // AAC codes 8 to 96 kHz at 12 rates; others are resampled to the nearest rate above, or 96 kHz.
            int target = 96000;
            foreach (int r in AacConfig.Rates[..12])
            {
                if (r >= rate)
                {
                    target = r;
                }
            }

            _resampler = new Resampler(rate, target, options.Layout, ResamplerQuality.Good, null, SampleFormat.F32Planar, context);
            rate = target;
        }

        int full = channels - (configuration >= 6 ? 1 : 0);
        int bitrate = aac.Bitrate ?? (64_000 * channels);
        int perChannel = aac.Quality is int q ? (q switch { 1 => 32_000, 2 => 40_000, 3 => 56_000, 4 => 72_000, _ => 112_000 }) : bitrate / full;
        int bandwidth = Math.Min(aac.Bandwidth ?? DefaultBandwidth(perChannel), rate / 2);
        bitrate = (int)Math.Min(bitrate, 6144L * channels * rate / AacFrameEncoder.FrameLength);

        _rate = rate;
        _sampleTime = new Rational(1, rate);
        _encoder = new AacFrameEncoder(rate, configuration, bitrate, aac.Quality, bandwidth);
        Layout = AacConfig.LayoutOf(configuration)!.Value;
        _planes = new float[channels][];
        _chunker = new AudioChunker(SampleFormat.F32Planar, 0, Layout, rate, AacFrameEncoder.FrameLength, context);
        Track = MakeTrack();
    }

    public AudioTrackInfo Track { get; private set; }

    public int FrameSize => AacFrameEncoder.FrameLength;

    private ChannelLayout Layout { get; }

    /// <summary>The default bandwidth for a rate per full-bandwidth channel: 11 kHz at 16 kbit/s up to 20 kHz from 32.</summary>
    public static int DefaultBandwidth(int perChannel)
    {
        ReadOnlySpan<int> rates = [16_000, 24_000, 32_000];
        ReadOnlySpan<int> hertz = [11_000, 15_000, 20_000];
        if (perChannel <= rates[0])
        {
            return hertz[0];
        }

        for (int i = 1; i < rates.Length; i++)
        {
            if (perChannel <= rates[i])
            {
                double t = (double)(perChannel - rates[i - 1]) / (rates[i] - rates[i - 1]);
                return (int)(hertz[i - 1] + (t * (hertz[i] - hertz[i - 1])));
            }
        }

        return hertz[^1];
    }

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

        // Push silence until the packet that decodes the last input sample is out; packet k decodes input [1024 (k - 1), 1024 k).
        long last = (_samplesIn + AacFrameEncoder.FrameLength - 1) / AacFrameEncoder.FrameLength;
        while (_samplesIn > 0 && _packets <= last)
        {
            Push(null, 0);
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
        CodecId = CodecIds.Aac,
        CodecPrivate = _encoder.AudioSpecificConfig,
        TimeBase = _sampleTime,
        Duration = _samplesIn > 0 ? new MediaTime(_samplesIn, _sampleTime) : null,
        SampleRate = _rate,
        Channels = _encoder.ChannelCount,
        Layout = Layout,
        CodecDelay = new MediaTime(Priming, _sampleTime),
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
                    _origin = frame.Timestamp.Rescale(_sampleTime).Ticks;
                }

                int n = frame.SampleCount;
                for (int c = 0; c < _planes.Length; c++)
                {
                    _planes[c] ??= new float[AacFrameEncoder.FrameLength];
                    frame.AsSpan<float>(c)[..n].CopyTo(_planes[c]);
                }

                _samplesIn += n;
                Push(_planes, n);
            }
        }
    }

    private void Push(float[][]? planes, int samples)
    {
        if (!_encoder.Push(planes, samples, out ReadOnlySpan<byte> block))
        {
            return;
        }

        long start = (_packets * AacFrameEncoder.FrameLength) - Priming;
        _packets++;
        long duration = AacFrameEncoder.FrameLength;
        if (start + duration > _samplesIn)
        {
            // Past the input: the packet ends where the input does, and the decoder drops the rest.
            duration = Math.Max(0, _samplesIn - start);
        }

        EncodedPacket packet = EncodedPacket.Rent(1, block.Length, new MediaTime(_origin + start, _sampleTime));
        block.CopyTo(packet.GetWritableData().Span);
        packet.Duration = new MediaTime(duration, _sampleTime);
        packet.IsKeyframe = true;
        _ready.Enqueue(packet);
    }
}
