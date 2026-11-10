using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// The AAC codec adapter: each packet holds one raw data block (MP4, Matroska, CAF) or up to four (ADTS without CRCs), each
/// decoded into a planar float frame. The stream's AudioSpecificConfig gives the rate and channel configuration. HE-AAC decodes
/// its AAC LC core at twice the core rate, with one "aac-sbr-ignored" warning; a mono HE-AAC stream comes out as two equal
/// channels, as parametric stereo may widen it. Damaged blocks are handled as <see cref="AudioDecoderOptions.Errors"/> says;
/// Conceal puts silence of the block's length in its place.
/// </summary>
internal sealed class AacDecoder : IAudioDecoder
{
    private const int MaxBlocks = 4;

    private readonly AudioDecoderOptions _options;
    private readonly MediaContext _context;
    private readonly AacFrameDecoder _decoder;
    private readonly int _coreRate;
    private readonly ChannelLayout _layout;
    private readonly Queue<AudioFrame> _ready = new();
    private readonly float[][] _samples = new float[AacFrameDecoder.MaxChannels][];
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _next;
    private MediaTime? _packetEnd;
    private Rational _packetTick = new(1, 1000000);
    private int _packet = -1;
    private readonly MediaTime? _delay;
    private bool _warned;
    private bool _noiseBefore;

    public AacDecoder(AudioTrackInfo track, AudioDecoderOptions options, MediaContext context)
    {
        _options = options;
        _context = context;
        _delay = track.CodecDelay is { Ticks: > 0 } delay ? delay : null;
        if (!AacConfig.TryRead(track.CodecPrivate.Span, out AacConfig config))
        {
            // No AudioSpecificConfig: take an LC stream at the track's rate and channel count.
            int configuration = track.Channels switch { >= 1 and <= 6 => track.Channels, 8 => 7, _ => 0 };
            config = new AacConfig(AacConfig.LowComplexity, track.SampleRate, configuration, AacConfig.LowComplexity, track.SampleRate);
        }

        bool explicitSbr = config.ObjectType is AacConfig.Sbr or AacConfig.ParametricStereo;
        if (config.CoreObjectType != AacConfig.LowComplexity)
        {
            throw new UnsupportedFormatException($"AAC object type {config.CoreObjectType} is not supported: only AAC LC and the AAC LC core of HE-AAC are decoded.");
        }

        if (config.ChannelConfiguration is < 1 or > 7)
        {
            throw new UnsupportedFormatException($"AAC channel configuration {config.ChannelConfiguration} is not supported: channels set by a program config element are not decoded.");
        }

        if (config.CoreSampleRate <= 0)
        {
            throw new UnsupportedFormatException("The AAC track has no sampling rate.");
        }

        _coreRate = config.CoreSampleRate;
        _decoder = new AacFrameDecoder(NearestRateIndex(_coreRate), config.ChannelConfiguration, explicitSbr);
        _layout = AacConfig.LayoutOf(config.ChannelConfiguration)!.Value;
        for (int c = 0; c < _samples.Length; c++)
        {
            _samples[c] = new float[2 * AacFrameDecoder.CoreFrameLength];
        }
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        MediaTime start = Snap(packet.Timestamp);
        _next = start;
        _packetEnd = !_options.KeepPadding && packet.Duration is { } duration ? start + duration : null;
        _packetTick = packet.Timestamp.TimeBase;
        AacBitReader reader = new(packet.Data.Span);
        int blocks = 0;
        do
        {
            if (!DecodeBlock(ref reader))
            {
                break;
            }
        }
        while (++blocks < MaxBlocks && reader.Remaining >= 8);
    }

    public void SendLoss(MediaTime duration)
    {
        _decoder.Reset();
        int rate = OutputRate;
        long samples = duration.Rescale(new Rational(1, rate)).Ticks;
        MediaTime time = _next ?? MediaTime.Zero;
        while (samples > 0)
        {
            int n = (int)Math.Min(samples, _decoder.FrameLength);
            AudioFrame frame = new(SampleFormat.F32Planar, OutputLayout, rate, n, time, _context);
            _ready.Enqueue(frame);
            time += frame.Duration;
            samples -= n;
        }

        _next = time;
    }

    public void Flush()
    {
    }

    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        _decoder.Reset();
        _next = null;
        _packetEnd = null;
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    public void Dispose()
    {
        Reset();
        _diagnostics?.Dispose();
    }

    /// <summary>Gets or sets where to record the timestamps of frames that noise substitution touched (this block's or the previous one's), for tests.</summary>
    internal List<MediaTime>? NoisyFrames { get; set; }

    private int OutputRate => _decoder.HighEfficiency ? 2 * _coreRate : _coreRate;

    private ChannelLayout OutputLayout => _decoder.HighEfficiency && _layout.ChannelCount == 1 ? ChannelLayout.Stereo : _layout;

    /// <summary>The band tables of the nearest standard rate, for a rate written out in full.</summary>
    private static int NearestRateIndex(int rate)
    {
        int best = 0;
        for (int i = 1; i < AacConfig.Rates.Length; i++)
        {
            if (Math.Abs(AacConfig.Rates[i] - rate) < Math.Abs(AacConfig.Rates[best] - rate))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Puts a container's rounded timestamp (Matroska's milliseconds) on the sample after the last frame when it is within a
    /// millisecond of it; the first packet goes on the frame grid that starts the track's CodecDelay before zero, which the
    /// container rounded too.
    /// </summary>
    private MediaTime Snap(MediaTime timestamp)
    {
        MediaTime tolerance = new(1, new Rational(1, 1000));
        if (_next is { } next)
        {
            if (timestamp >= next - tolerance && timestamp <= next + tolerance)
            {
                return next;
            }
        }
        else if (_delay is { } delay && OutputRate > 0)
        {
            Rational sampleTime = new(1, OutputRate);
            long origin = -delay.Rescale(sampleTime).Ticks;
            long frame = _decoder.FrameLength;
            long k = (long)Math.Round((double)(timestamp.Rescale(sampleTime).Ticks - origin) / frame);
            MediaTime grid = new(origin + (k * frame), sampleTime);
            if (timestamp >= grid - tolerance && timestamp <= grid + tolerance)
            {
                return grid;
            }
        }

        return timestamp;
    }

    /// <summary>Decodes one raw data block into a frame; false when the rest of the packet cannot be read.</summary>
    private bool DecodeBlock(ref AacBitReader reader)
    {
        bool decoded = _decoder.TryDecode(ref reader, _samples, out string? error);
        if (_decoder.SbrSeen && !_warned)
        {
            _warned = true;
            _context.Warnings?.Invoke(new MediaWarning("aac-sbr-ignored", "The AAC stream carries SBR (HE-AAC), which is not decoded: its AAC LC core is, at twice the core rate when the core is 24 kHz or less."));
        }

        int rate = OutputRate;
        int samples = _decoder.FrameLength;
        MediaTime time = _next ?? MediaTime.Zero;
        Rational sampleTime = new(1, rate);
        if (time.TimeBase != sampleTime)
        {
            time = time.Rescale(sampleTime);
        }

        AudioFrame frame = new(SampleFormat.F32Planar, OutputLayout, rate, samples, time, _context);
        _next = time + frame.Duration;
        if (NoisyFrames is not null && (_decoder.UsedNoise || _noiseBefore))
        {
            NoisyFrames.Add(time);
        }

        _noiseBefore = decoded && _decoder.UsedNoise;
        if (!decoded)
        {
            string message = $"Packet {_packet}: an AAC block cannot be decoded: {error}";
            Report(DiagnosticLevel.Warning, "Aac.CorruptBlock", message);
            switch (_options.Errors)
            {
                case DecodeErrorMode.Throw:
                    frame.Dispose();
                    throw new CorruptDataException("AAC", -1, message);
                case DecodeErrorMode.Conceal:
                    for (int c = 0; c < frame.ChannelCount; c++)
                    {
                        frame.GetSpan<float>(c).Clear();
                    }

                    break;
                default:
                    frame.Dispose();
                    return false;
            }
        }
        else
        {
            for (int c = 0; c < frame.ChannelCount; c++)
            {
                _samples[Math.Min(c, _decoder.ChannelCount - 1)].AsSpan(0, samples).CopyTo(frame.GetSpan<float>(c));
            }
        }

        // Timestamps coarser than a sample (Matroska's milliseconds) may round the end up to one tick early.
        bool coarse = _packetTick.Numerator * rate > _packetTick.Denominator;
        if (_packetEnd is { } end && _next > (coarse ? end + new MediaTime(1, _packetTick) : end))
        {
            long keep = Math.Max(0, end.Rescale(sampleTime).Ticks - time.Ticks);
            if (keep < samples)
            {
                frame.Resize((int)keep);
            }
        }

        if (frame.SampleCount > 0)
        {
            _ready.Enqueue(frame);
        }
        else
        {
            frame.Dispose();
        }

        return decoded;
    }

    private void Report(DiagnosticLevel level, string id, string message)
    {
        if (_context.Egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _context.Egami.StartDiagnostics();
        _diagnostics.Report(level, id, message, frame: _packet);
    }
}
