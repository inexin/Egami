using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// The Opus codec adapter: the send and receive loop over the multistream decoder, with the setup from OpusHead, channels
/// remapped from Vorbis order to the canonical one, padding trimmed to the packet's duration, and lost packets (reported by
/// <see cref="SendLoss"/> or seen as a gap in timestamps) concealed, or recovered from the next packet's in-band FEC.
/// </summary>
internal sealed class OpusDecoder : IAudioDecoder
{
    /// <summary>The longest packet: 120 ms at 48 kHz.</summary>
    private const int MaxFrame = 5760;

    private static readonly Rational s_sampleTime = new(1, OpusStreamDecoder.SampleRate);

    private readonly AudioDecoderOptions _options;
    private readonly MediaContext _context;
    private readonly OpusMultistreamDecoder _decoder;
    private readonly ChannelLayout _layout;
    private readonly long? _delay;
    private readonly Queue<AudioFrame> _ready = new();
    private DiagnosticReporter? _diagnostics;
    private long _lostSamples;
    private MediaTime? _next;
    private int _packet = -1;

    public OpusDecoder(AudioTrackInfo track, AudioDecoderOptions options, MediaContext context)
    {
        _options = options;
        _context = context;
        OpusHead head = OpusHead.Read(track.CodecPrivate.Span)
            ?? (track.Channels is 1 or 2
                ? new OpusHead(1, track.Channels, 0, 48000, 0, 0, 1, track.Channels - 1, track.Channels == 1 ? [0] : [0, 1])
                : throw new UnsupportedFormatException($"Opus track {track.Number} has {track.Channels} channels but no OpusHead."));
        if (head.MappingFamily is not (0 or 1 or 255))
        {
            Report(DiagnosticLevel.Warning, "Opus.UnsupportedMapping", $"Opus channel mapping family {head.MappingFamily} (ambisonics) is not decoded.");
            throw new UnsupportedFormatException($"Opus channel mapping family {head.MappingFamily} (ambisonics) is not decoded.");
        }

        if (head.MappingFamily == 1 && head.Channels > 8)
        {
            throw new UnsupportedFormatException($"Opus mapping family 1 holds up to 8 channels, not {head.Channels}.");
        }

        _decoder = new OpusMultistreamDecoder(head);
        _delay = track.CodecDelay?.Rescale(s_sampleTime).Ticks;
        (_layout, _decoder.ChannelOrder) = Layout(head);
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        ReadOnlySpan<byte> data = packet.Data.Span;
        if (_next is { } previous && packet.Timestamp.Rescale(s_sampleTime).Ticks < previous.Rescale(s_sampleTime).Ticks - 48)
        {
            // A packet that goes back in time starts a new stream, such as the next link of a chained Ogg file.
            _decoder.Reset();
            _lostSamples = 0;
            _next = null;
        }

        int frameSamples = data.IsEmpty ? 0 : OpusPacket.SampleCount(data, OpusStreamDecoder.SampleRate);
        MediaTime timestamp = Snap(packet.Timestamp, frameSamples);

        // A gap in timestamps is a lost packet too, unless SendLoss already reported it.
        if (_next is { } next && timestamp > next)
        {
            long gap = timestamp.Rescale(s_sampleTime).Ticks - next.Rescale(s_sampleTime).Ticks;
            if (gap >= 120)
            {
                _lostSamples = Math.Max(_lostSamples, gap);
            }
        }

        if (_lostSamples > 0)
        {
            Recover(data, _next ?? timestamp);
        }

        if (data.IsEmpty || frameSamples <= 0)
        {
            Fail($"Packet {_packet} is not a valid Opus packet.", packet.Timestamp, packet.Duration);
            return;
        }

        AudioFrame frame = new(SampleFormat.F32Planar, _layout, OpusStreamDecoder.SampleRate, MaxFrame, timestamp, _context);
        int ret = _decoder.Decode(data, frame, MaxFrame, false);
        if (ret < 0)
        {
            frame.Dispose();
            Fail($"Packet {_packet} cannot be decoded (Opus error {ret}).", packet.Timestamp, packet.Duration);
            return;
        }

        int keep = ret;
        if (!_options.KeepPadding && packet.Duration is { } duration)
        {
            // The container says the packet ends early: the rest is padding (Ogg's last granule position, Matroska DiscardPadding).
            long said = duration.Rescale(s_sampleTime).Ticks;
            if (said >= 0 && said < ret)
            {
                keep = (int)said;
            }
        }

        frame.Resize(keep);
        _next = new MediaTime(timestamp.Rescale(s_sampleTime).Ticks + ret, s_sampleTime);
        if (keep > 0)
        {
            _ready.Enqueue(frame);
        }
        else
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// Puts a container's rounded timestamp (Matroska's milliseconds) on the sample it stands for: the end of the last packet when
    /// it is within a millisecond of it, minus the codec delay for the first packet, or a whole number of packets after that.
    /// </summary>
    private MediaTime Snap(MediaTime timestamp, int frameSamples)
    {
        long ticks = timestamp.Rescale(s_sampleTime).Ticks;
        if (_next is { } next)
        {
            long n = next.Rescale(s_sampleTime).Ticks;
            return Math.Abs(ticks - n) <= 48 ? next : new MediaTime(ticks, s_sampleTime);
        }

        if (_delay is { } delay && Math.Abs(ticks + delay) <= 48)
        {
            return new MediaTime(-delay, s_sampleTime);
        }

        // After a seek: packets of one length lie on a grid that starts at minus the codec delay.
        if (frameSamples > 0)
        {
            long origin = -(_delay ?? 0);
            long grid = origin + ((long)Math.Round((ticks - origin) / (double)frameSamples) * frameSamples);
            if (Math.Abs(grid - ticks) <= 48)
            {
                return new MediaTime(grid, s_sampleTime);
            }
        }

        return new MediaTime(ticks, s_sampleTime);
    }

    public void SendLoss(MediaTime duration) => _lostSamples += Math.Max(0, duration.Rescale(s_sampleTime).Ticks);

    public void Flush()
    {
        if (_lostSamples > 0 && _next is { } next)
        {
            Recover([], next);
        }
    }

    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        _decoder.Reset();
        _lostSamples = 0;
        _next = null;
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

    /// <summary>The layout of the decoded channels and, for each coded channel, where it goes in that layout.</summary>
    private static (ChannelLayout Layout, int[] Order) Layout(OpusHead head)
    {
        int n = head.Channels;
        if (head.MappingFamily == 255 || (head.MappingFamily == 0 && n > 2))
        {
            return (ChannelLayout.Unpositioned(n), [.. Enumerable.Range(0, n)]);
        }

        // Vorbis channel order (Vorbis I specification section 4.3.9), which mapping family 1 uses.
        Speakers[] order = n switch
        {
            1 => [Speakers.FrontCenter],
            2 => [Speakers.FrontLeft, Speakers.FrontRight],
            3 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight],
            4 => [Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight],
            5 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight],
            6 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight, Speakers.LowFrequency],
            7 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.SideLeft, Speakers.SideRight, Speakers.BackCenter, Speakers.LowFrequency],
            _ => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.SideLeft, Speakers.SideRight, Speakers.BackLeft, Speakers.BackRight, Speakers.LowFrequency],
        };
        Speakers all = Speakers.None;
        foreach (Speakers s in order)
        {
            all |= s;
        }

        ChannelLayout layout = new(all);
        return (layout, [.. order.Select(layout.IndexOf)]);
    }

    /// <summary>Fills a loss: concealment for most of it, and the next packet's FEC for its last frame when it has some.</summary>
    private void Recover(ReadOnlySpan<byte> next, MediaTime start)
    {
        long lost = _lostSamples;
        _lostSamples = 0;
        bool fec = !next.IsEmpty && OpusStreamDecoder.HasLbrr(next);
        MediaTime time = start;
        while (lost > 0)
        {
            // Concealment runs in multiples of 2.5 ms, at most 120 ms at a time.
            int chunk = (int)Math.Min(lost, MaxFrame);
            chunk -= chunk % 120;
            if (chunk == 0)
            {
                break;
            }

            bool last = lost - chunk < 120;
            AudioFrame frame = new(SampleFormat.F32Planar, _layout, OpusStreamDecoder.SampleRate, chunk, time, _context);
            int ret = _decoder.Decode(last && fec ? next : [], frame, chunk, last && fec);
            if (ret <= 0)
            {
                frame.Dispose();
                break;
            }

            frame.Resize(ret);
            _ready.Enqueue(frame);
            time += new MediaTime(ret, s_sampleTime);
            lost -= ret;
        }

        _next = time;
    }

    private void Fail(string message, MediaTime time, MediaTime? duration)
    {
        Report(DiagnosticLevel.Warning, "Opus.CorruptPacket", message);
        switch (_options.Errors)
        {
            case DecodeErrorMode.Throw:
                throw new CorruptDataException("Opus", -1, message);
            case DecodeErrorMode.Conceal:
                _next ??= time;
                _lostSamples += duration is { } d ? d.Rescale(s_sampleTime).Ticks : 960;
                Recover([], _next.Value);
                break;
        }
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
