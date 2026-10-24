using Egami.Formats.Av1;
using Egami.Pixels;
using Egami.Video.Audio.Codecs.Opus;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Av1;
using Egami.Video.Containers;
using Egami.Video.Containers.Flac;

namespace Egami.Video.Trimming;

/// <summary>
/// One trim (V5 Encoders Design, V5.6): reads the input once, finds the keyframe at or before the start and the first frame at or
/// after it, and writes the cut. Video is copied from the keyframe (<see cref="TrimMode.Keyframe"/>), or encoded again from the
/// first frame up to the next keyframe and copied from there (<see cref="TrimMode.Smart"/>), or encoded again whole
/// (<see cref="TrimMode.Reencode"/>). The cut ends after the packet that shows the last frame before the end: decode order is
/// packet order for these codecs and hidden frames come before the frames that show them, so a hidden frame after it is
/// dropped. Audio is cut to the sample where the format can say so (PCM by slicing packets, MP4 by its edit list, Matroska Opus
/// by CodecDelay and DiscardPadding), else to the nearest packet; subtitle cues are clipped; everything starts at 0.
/// </summary>
internal sealed class TrimJob : IDisposable
{
    /// <summary>
    /// How long other tracks may wait for a Matroska Opus track's first packet, which sets its CodecDelay, before the muxer opens
    /// without it.
    /// </summary>
    private static readonly MediaTime OtherLookBack = MediaTime.FromSeconds(2);

    /// <summary>
    /// How far before the keyframe a seek lands, so the audio a file stores ahead of the video (encoders' lookahead puts it up to
    /// a second or two ahead) and the audio's pre-roll are read too. A start closer to the beginning reads the file from there.
    /// </summary>
    private static readonly MediaTime SeekLookBack = MediaTime.FromSeconds(5);

    /// <summary>How far back a cue that starts before the cut and is still shown at it may lie.</summary>
    private static readonly MediaTime CueLookBack = MediaTime.FromSeconds(30);

    private readonly IDemuxer _demuxer;
    private readonly TrackInfo[] _kept;
    private readonly Dictionary<int, TrackInfo> _tracks;
    private readonly VideoTrackInfo? _main;
    private readonly TrimCodec? _codec;
    private readonly MediaTime _start;
    private readonly long _halfTick;
    private readonly MediaTime? _end;
    private readonly TrimMode _asked;
    private readonly ContainerFormat _format;
    private readonly MediaContext _context;
    private readonly Func<IReadOnlyList<TrackInfo>, MediaMetadata, IMuxer> _createMuxer;
    private readonly Action<EncodedPacket>? _written;
    private readonly CancellationToken _token;
    private readonly List<MediaWarning> _warnings = [];

    // Main video: the packets since the keyframe at or before the start, with whether each shows a frame.
    private readonly List<(EncodedPacket Packet, bool Shows)> _gop = [];
    private readonly List<EncodedPacket> _pendingHidden = [];
    private Phase _phase;
    private int _first = -1;
    private TrimMode _mode;
    private TrackInfo? _mainOut;
    private VideoTrackInfo? _finalTrack;

    // Other tracks: their packets before the start is known, then their state.
    private readonly List<EncodedPacket> _raw = [];
    private readonly Dictionary<int, AudioCut> _audio = [];
    private readonly HashSet<int> _started = [];
    private readonly HashSet<int> _running = [];
    private MediaTime? _cutIn;
    private bool _holdOthers;
    private MediaTime _lastRead = MediaTime.Zero;

    // Re-encoding: the decoder, the encoder, and the times of the frames in flight.
    private IVideoDecoder? _decoder;
    private IVideoEncoder? _encoder;
    private TrimCodec? _encodedCodec;
    private readonly Queue<(MediaTime Time, MediaTime? Duration, bool Wanted)> _decodeTimes = new();
    private readonly Queue<(MediaTime Time, MediaTime? Duration)> _encodeTimes = new();
    private int? _quantizer;
    private bool _delimiters;

    // Output: video packets in order, other packets by decode time, released as the video reaches them.
    private readonly Queue<EncodedPacket> _videoQueue = new();
    private readonly List<EncodedPacket> _otherQueue = [];
    private MediaTime? _lastVideo;
    private bool _videoDone;
    private bool _endOfStream;
    private long _packets;
    private long _frames;
    private long _reencoded;

    public TrimJob(IDemuxer demuxer, TrackInfo[] kept, MediaTime start, MediaTime? end, TrimMode mode, ContainerFormat format, MediaContext context,
        Func<IReadOnlyList<TrackInfo>, MediaMetadata, IMuxer> createMuxer, Action<EncodedPacket>? written, CancellationToken token)
    {
        _demuxer = demuxer;
        _kept = kept;
        _tracks = kept.ToDictionary(t => t.Number);
        _main = kept.OfType<VideoTrackInfo>().FirstOrDefault();
        _codec = _main is null ? null : new TrimCodec(_main);
        _start = start;
        _end = end;

        // Containers round timestamps to their tick (Matroska's millisecond): a frame within half a tick of a time is at it.
        _halfTick = _main is null ? 0 : new MediaTime(1, _main.TimeBase).Rescale(Rational.Nanosecond).Ticks / 2;
        _asked = mode;
        _mode = mode;
        _format = format;
        _context = context;
        _createMuxer = createMuxer;
        _written = written;
        _token = token;
        foreach (TrackInfo track in kept)
        {
            if (track.Number != _main?.Number && track.Kind is TrackKind.Video or TrackKind.Audio)
            {
                _running.Add(track.Number);
            }

            if (track is AudioTrackInfo audio)
            {
                _audio[track.Number] = new AudioCut(CutKind(audio));
            }
        }

        if (_main is null)
        {
            _cutIn = start;
            _videoDone = true;
            _phase = Phase.Done;
        }
    }

    private enum Phase
    {
        Locating,
        Head,
        Copying,
        Reencoding,
        Done,
    }

    private enum AudioCutKind
    {
        /// <summary>PCM: packets are sliced at the sample.</summary>
        Pcm,

        /// <summary>The container says where audio starts and stops: MP4's edit list, Matroska's CodecDelay and DiscardPadding for Opus.</summary>
        Container,

        /// <summary>Whole packets: the ones whose middle falls inside the cut.</summary>
        Nearest,
    }

    /// <summary>Gets the muxer, once the job opened it; the caller disposes it.</summary>
    public IMuxer? Muxer { get; private set; }

    public TrimResult Run()
    {
        _token.ThrowIfCancellationRequested();
        SeekNear();
        while (_demuxer.ReadPacket() is { } packet)
        {
            if (_token.IsCancellationRequested)
            {
                packet.Dispose();
                _token.ThrowIfCancellationRequested();
            }

            if (!_tracks.ContainsKey(packet.TrackNumber))
            {
                packet.Dispose();
                continue;
            }

            _lastRead = packet.Timestamp > _lastRead ? packet.Timestamp : _lastRead;
            if (packet.TrackNumber == _main?.Number)
            {
                Main(packet);
            }
            else
            {
                Other(packet);
            }

            if (_end is not null && _cutIn is not null && _videoDone && _running.Count == 0)
            {
                break;
            }
        }

        EndOfStream();
        return new TrimResult
        {
            Mode = _mode,
            Start = _cutIn ?? _start,
            Frames = _frames,
            ReencodedFrames = _reencoded,
            Packets = _packets,
            Warnings = _warnings,
        };
    }

    public void Dispose()
    {
        foreach ((EncodedPacket packet, _) in _gop)
        {
            packet.Dispose();
        }

        _gop.Clear();
        DisposeAll(_pendingHidden);
        DisposeAll(_raw);
        DisposeAll(_otherQueue);
        while (_videoQueue.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }

        _decoder?.Dispose();
        _encoder?.Dispose();
        _decoder = null;
        _encoder = null;
    }

    /// <summary>
    /// Seeks near the start when the input can: to the main track's keyframe at or before a point a little before the start, so
    /// the other tracks' packets stored ahead of the video (and cues shown across the cut) are read too.
    /// </summary>
    private void SeekNear()
    {
        if (!_demuxer.CanSeek || _start <= SeekLookBack + SeekLookBack)
        {
            return;
        }

        int track = _main?.Number ?? _kept[0].Number;
        _demuxer.Seek(_start, track);
        MediaTime? found = null;
        while (_demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track)
                {
                    found = packet.Timestamp;
                    break;
                }
            }
        }

        MediaTime from = found is { } keyframe && keyframe < _start ? keyframe : _start;
        MediaTime target = from - (_kept.Any(t => t.Kind == TrackKind.Subtitle) ? CueLookBack : SeekLookBack);
        _demuxer.Seek(target <= MediaTime.Zero ? MediaTime.Zero : target, track);
    }

    private void Main(EncodedPacket packet)
    {
        switch (_phase)
        {
            case Phase.Locating:
                Locate(packet);
                break;
            case Phase.Head:
                Head(packet);
                break;
            case Phase.Copying:
                Copy(packet);
                break;
            case Phase.Reencoding:
                Reencode(packet);
                break;
            default:
                packet.Dispose();
                break;
        }
    }

    /// <summary>Holds the main track's packets from the latest keyframe at or before the start until the first frame at or after it.</summary>
    private void Locate(EncodedPacket packet)
    {
        bool key = _codec!.IsKeyframe(packet);
        bool shows = _codec.ShowsFrame(packet);
        if (key)
        {
            // A keyframe before the first frame that is wanted: everything held before it is no longer needed.
            DropGop();
            _gop.Add((packet, shows));
            DropRawBefore(packet.Timestamp);
        }
        else if (_gop.Count == 0)
        {
            packet.Dispose();
            return;
        }
        else
        {
            _gop.Add((packet, shows));
        }

        bool found = (!_codec.InOrder || (shows && !packet.IsDiscardable)) && !Before(packet.Timestamp, _start);
        if (found)
        {
            Decide(_gop.Count - 1);
        }
    }

    /// <summary>The first frame at or after the start is known: picks how the video starts, and lets the held packets through.</summary>
    private void Decide(int first)
    {
        _first = first;
        EncodedPacket frame = _gop[first].Packet;
        bool onKey = _codec!.IsKeyframe(frame);
        VideoCodec? codec = _context.FindCodec(_main!);
        if (_mode == TrimMode.Smart && !onKey && (!_codec.CanMatch || codec is not { CanEncode: true }))
        {
            _mode = TrimMode.Keyframe;
            Warn("trim-keyframe", $"Smart cut cannot encode {CodecName(_main!)}, so the cut starts at the keyframe at {Seconds(_gop[0].Packet.Timestamp)} s, {Seconds(frame.Timestamp - _gop[0].Packet.Timestamp)} s before the first frame asked for.");
        }

        if (_mode == TrimMode.Reencode && codec is not { CanEncode: true })
        {
            throw new NotSupportedException($"{CodecName(_main!)} cannot be encoded, so the cut cannot be encoded again; trim with TrimMode.Keyframe.");
        }

        switch (_mode)
        {
            case TrimMode.Keyframe:
                _cutIn = _gop[0].Packet.Timestamp;
                if (_asked == TrimMode.Keyframe && _cutIn < frame.Timestamp)
                {
                    Warn("trim-keyframe", $"The cut starts at the keyframe at {Seconds(_cutIn.Value)} s, {Seconds(frame.Timestamp - _cutIn.Value)} s before the first frame asked for.");
                }

                _mainOut = _main;
                _phase = Phase.Copying;
                AdmitRaw();
                foreach ((EncodedPacket packet, _) in TakeGop(0))
                {
                    Copy(packet);
                }

                break;

            case TrimMode.Smart when onKey:
                _cutIn = frame.Timestamp;
                _mainOut = _main;
                _phase = Phase.Copying;
                AdmitRaw();
                foreach ((EncodedPacket packet, _) in TakeGop(first))
                {
                    Copy(packet);
                }

                break;

            case TrimMode.Smart:
                // The other tracks wait for the head: should it have to start at the keyframe after all, they start there too.
                _cutIn = frame.Timestamp;
                _phase = Phase.Head;
                _holdOthers = true;
                break;

            default:
                _cutIn = frame.Timestamp;
                AdmitRaw();
                _quantizer = AverageQuantizer(0);
                StartReencode();
                break;
        }
    }

    /// <summary>Smart cut: holds the packets up to the next keyframe (or the end), then encodes their frames again.</summary>
    private void Head(EncodedPacket packet)
    {
        bool key = _codec!.IsKeyframe(packet);
        bool shows = _codec.ShowsFrame(packet);
        if (key)
        {
            RunHead();
            Main(packet);
            return;
        }

        if (shows && _end is { } end && !Before(packet.Timestamp, end))
        {
            packet.Dispose();
            RunHead();
            if (_phase == Phase.Reencoding)
            {
                FinishReencode();
            }
            else
            {
                VideoDone();
            }

            return;
        }

        _gop.Add((packet, shows));
    }

    /// <summary>
    /// Encodes the held frames from the first one wanted as a new group that matches the stream, or, when the encoder cannot
    /// match it, starts encoding the whole cut again.
    /// </summary>
    private void RunHead()
    {
        List<(MediaTime Time, MediaTime? Duration)> wanted = [];
        for (int i = _first; i < _gop.Count; i++)
        {
            (EncodedPacket packet, bool shows) = _gop[i];
            if (shows && !packet.IsDiscardable)
            {
                wanted.Add((packet.Timestamp, packet.Duration));
            }
        }

        int? quantizer = AverageQuantizer(_first);
        List<EncodedPacket>? head = null;
        string? reason;
        bool resized = false;
        try
        {
            head = EncodeHead(quantizer, wanted, out reason, out resized);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or CorruptDataException or UnsupportedFormatException or InvalidOperationException)
        {
            reason = e.Message;
        }

        _holdOthers = false;
        if (head is not null)
        {
            _mainOut = _main;
            _phase = Phase.Copying;
            DropGop();
            AdmitRaw();
            foreach (EncodedPacket packet in head)
            {
                EmitVideo(packet, shows: true, reencoded: true);
            }

            return;
        }

        if (resized)
        {
            // The frames change size: no encoder makes one stream of them, so the cut copies from the keyframe.
            EncodedPacket frame = _gop[_first].Packet;
            _mode = TrimMode.Keyframe;
            _cutIn = _gop[0].Packet.Timestamp;
            Warn("trim-keyframe", $"Smart cut cannot encode the {CodecName(_main!)} stream again ({reason}), so the cut starts at the keyframe at {Seconds(_cutIn.Value)} s, {Seconds(frame.Timestamp - _cutIn.Value)} s before the first frame asked for.");
            _mainOut = _main;
            _phase = Phase.Copying;
            AdmitRaw();
            foreach ((EncodedPacket packet, _) in TakeGop(0))
            {
                Copy(packet);
            }

            return;
        }

        Warn("trim-reencode", $"Smart cut cannot match the {CodecName(_main!)} stream ({reason}), so the whole cut was encoded again.");
        _mode = TrimMode.Reencode;
        _quantizer = quantizer;
        AdmitRaw();
        StartReencode();
    }

    /// <summary>
    /// Decodes the held packets from the keyframe and encodes the frames from the first one wanted, at the source's quantizer,
    /// with the stream's parameters (for AV1 its sequence header). Returns the packets, timed as the frames they replace, or
    /// null with the reason they do not match the stream.
    /// </summary>
    private List<EncodedPacket>? EncodeHead(int? quantizer, List<(MediaTime Time, MediaTime? Duration)> wanted, out string? reason, out bool resized)
    {
        resized = false;
        ReadOnlyMemory<byte> keyframe = _gop[0].Packet.Data;
        string? source = _codec!.StreamParameters(keyframe);
        if (source is null)
        {
            reason = "its keyframe states no stream parameters";
            return null;
        }

        Av1SequenceOptions? sequence = null;
        if (_main!.CodecId == CodecIds.Av1)
        {
            (sequence, string? why) = _codec.SequenceOptions(keyframe.Span);
            if (sequence is null)
            {
                reason = why;
                return null;
            }

            if (sequence.Chroma == ChromaFormat.Yuv400)
            {
                reason = "the AV1 encoder does not code monochrome (4:0:0) frames";
                return null;
            }

            _delimiters = StartsWithDelimiter(keyframe.Span);
        }

        int skip = 0;
        for (int i = 0; i < _first; i++)
        {
            skip += _gop[i].Shows ? 1 : 0;
        }

        Queue<(MediaTime Time, MediaTime? Duration)> times = new(wanted);
        List<EncodedPacket> output = [];
        IVideoEncoder? encoder = null;
        TrimCodec encoded = new(_main);
        int shown = 0, decoded = 0;
        string? sizes = null;
        bool ok = false;
        try
        {
            using IVideoDecoder decoder = CreateDecoder();
            bool Frames()
            {
                while (decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    using (frame)
                    {
                        _token.ThrowIfCancellationRequested();
                        if (decoded++ < skip || decoded - skip > wanted.Count)
                        {
                            continue;
                        }

                        if (frame.Width != _main.Width || frame.Height != _main.Height)
                        {
                            sizes = $"the frames change size, from {_main.Width}x{_main.Height} to {frame.Width}x{frame.Height}";
                            return false;
                        }

                        encoder ??= CreateEncoder(frame, quantizer, sequence, wanted.Count, _codec.Vp9ColorConfig(keyframe));
                        encoder.Send(frame, decoded - skip == 1 ? new EncodeFrameOptions { ForceKeyframe = true } : EncodeFrameOptions.Default);
                        shown += Drain(encoder, encoded, times, output);
                    }
                }

                return true;
            }

            bool going = true;
            foreach ((EncodedPacket packet, _) in _gop)
            {
                decoder.Send(packet);
                if (!(going = Frames()))
                {
                    break;
                }
            }

            if (going)
            {
                decoder.Flush();
                going = Frames();
            }

            if (sizes is not null)
            {
                reason = sizes;
                resized = true;
                return null;
            }
            if (encoder is null)
            {
                reason = "none of its frames decoded";
                return null;
            }

            encoder.Flush();
            shown += Drain(encoder, encoded, times, output);
            string? made = output.Count > 0 ? encoded.StreamParameters(output[0].Data) : null;
            if (shown != wanted.Count || output.Count == 0 || !output[0].IsKeyframe)
            {
                reason = $"the encoder gave {shown} frames for {wanted.Count}";
                return null;
            }

            if (made != source)
            {
                reason = $"the encoder makes {made ?? "an unknown stream"}; the stream has {source}";
                return null;
            }

            ok = true;
            reason = null;
            return output;
        }
        finally
        {
            encoder?.Dispose();
            if (!ok)
            {
                DisposeAll(output);
            }
        }
    }

    /// <summary>Starts encoding the whole cut again: decodes the held packets from the keyframe, and the rest as they come.</summary>
    private void StartReencode()
    {
        _phase = Phase.Reencoding;
        _decoder = CreateDecoder();
        _delimiters = _main!.CodecId == CodecIds.Av1 && _gop.Count > 0 && StartsWithDelimiter(_gop[0].Packet.Data.Span);
        _encodedCodec = new TrimCodec(_main!);
        foreach ((EncodedPacket packet, bool shows) in TakeGop(0))
        {
            Feed(packet, shows);
        }
    }

    private void Reencode(EncodedPacket packet)
    {
        bool shows = _codec!.ShowsFrame(packet);
        if (shows && _end is { } end && !Before(packet.Timestamp, end))
        {
            packet.Dispose();
            FinishReencode();
            return;
        }

        Feed(packet, shows);
    }

    private void Feed(EncodedPacket packet, bool shows)
    {
        using (packet)
        {
            if (shows)
            {
                bool wanted = !packet.IsDiscardable && packet.Timestamp >= _cutIn!.Value && (_end is not { } end || Before(packet.Timestamp, end));
                _decodeTimes.Enqueue((packet.Timestamp, packet.Duration, wanted));
            }

            _decoder!.Send(packet);
        }

        DrainDecoder();
    }

    private void DrainDecoder()
    {
        while (_decoder!.TryReceive(out YuvVideoFrame? frame))
        {
            using (frame)
            {
                _token.ThrowIfCancellationRequested();
                if (!_decodeTimes.TryDequeue(out var time) || !time.Wanted)
                {
                    continue;
                }

                bool first = _encoder is null;
                if (_encoder is { } open && (frame.Width != open.Track.Width || frame.Height != open.Track.Height))
                {
                    throw new NotSupportedException($"The video changes size at {Seconds(time.Time)} s, from {open.Track.Width}x{open.Track.Height} to {frame.Width}x{frame.Height}; encoding it again needs one size, so trim it with TrimMode.Keyframe.");
                }

                _encoder ??= CreateEncoder(frame, _quantizer, null, 0);
                _encodeTimes.Enqueue((time.Time, time.Duration));
                _encoder.Send(frame, first ? new EncodeFrameOptions { ForceKeyframe = true } : EncodeFrameOptions.Default);
                if (first)
                {
                    _mainOut = ReencodedTrack(_encoder.Track);
                }

                List<EncodedPacket> packets = [];
                Drain(_encoder, _encodedCodec!, _encodeTimes, packets);
                foreach (EncodedPacket packet in packets)
                {
                    EmitVideo(packet, _encodedCodec!.ShowsFrame(packet), reencoded: true);
                }
            }
        }
    }

    private void FinishReencode()
    {
        _decoder!.Flush();
        DrainDecoder();
        if (_encoder is not null)
        {
            _encoder.Flush();
            List<EncodedPacket> packets = [];
            Drain(_encoder, _encodedCodec!, _encodeTimes, packets);
            foreach (EncodedPacket packet in packets)
            {
                EmitVideo(packet, _encodedCodec!.ShowsFrame(packet), reencoded: true);
            }

            _finalTrack = ReencodedTrack(_encoder.Track);
        }

        VideoDone();
    }

    /// <summary>
    /// Takes what an encoder has ready, timed as the frames it codes: a packet that shows a frame takes the next frame's time and
    /// duration, a hidden one the time of the frame after it. Returns how many show a frame.
    /// </summary>
    private int Drain(IVideoEncoder encoder, TrimCodec codec, Queue<(MediaTime Time, MediaTime? Duration)> times, List<EncodedPacket> output)
    {
        int shown = 0;
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            if (_delimiters)
            {
                EncodedPacket marked = EncodedPacket.Rent(packet.TrackNumber, packet.Data.Length + 2, packet.Timestamp);
                Av1Obus.TemporalDelimiterObu.CopyTo(marked.GetWritableData().Span);
                packet.Data.Span.CopyTo(marked.GetWritableData().Span[2..]);
                marked.IsKeyframe = packet.IsKeyframe;
                packet.Dispose();
                packet = marked;
            }

            packet.TrackNumber = _main!.Number;
            packet.DecodeTimestamp = null;
            if (codec.ShowsFrame(packet))
            {
                shown++;
                if (times.TryDequeue(out var time))
                {
                    packet.Timestamp = time.Time;
                    packet.Duration = time.Duration;
                }
            }
            else if (times.TryPeek(out var next))
            {
                packet.Timestamp = next.Time;
                packet.Duration = null;
            }

            output.Add(packet);
        }

        return shown;
    }

    private IVideoDecoder CreateDecoder()
    {
        VideoCodec codec = _context.FindCodec(_main!) ?? throw new NotSupportedException($"No registered codec decodes {CodecName(_main!)}.");
        return codec.CreateDecoder(_main!, new VideoDecoderOptions { ApplyFilmGrain = false }, _context);
    }

    /// <summary>
    /// An encoder for the cut: the frames' size, the track's frame rate, a fixed quantizer when the source's is known, and no
    /// hidden VP8 frames (so each packet is one frame). A head of <paramref name="headFrames"/> frames gets one keyframe, at its
    /// start; an AV1 head reproduces <paramref name="sequence"/>.
    /// </summary>
    private IVideoEncoder CreateEncoder(VideoFrame first, int? quantizer, Av1SequenceOptions? sequence, int headFrames, (int Space, int Range)? vp9Color = null)
    {
        VideoCodec codec = _context.FindCodec(_main!) ?? throw new NotSupportedException($"No registered codec encodes {CodecName(_main!)}.");
        Rational rate = _main!.FrameRate is { Numerator: > 0, Denominator: > 0 } r ? r : new Rational(30, 1);
        bool head = headFrames > 0;
        VideoEncoderOptions options = new()
        {
            Width = first.Width,
            Height = first.Height,
            FrameRate = rate,
            RateControl = quantizer is null ? RateControlMode.Quality : RateControlMode.Quantizer,
            Quantizer = quantizer,
            KeyframeInterval = head ? headFrames + 1 : null,
            SceneCutKeyframes = !head,
            Vp8 = new Vp8EncoderOptions { AlternateReference = false, MinQuantizer = 0, Alpha = _main.HasAlpha },
            Vp9 = new Vp9EncoderOptions { Alpha = _main.HasAlpha },
        };
        return codec switch
        {
            Av1Codec when sequence is not null => new Av1VideoEncoder(options, _context, sequence),
            Vp9Codec when vp9Color is not null => new Codecs.Vp9.Vp9VideoEncoder(options, _context, vp9Color),
            _ => codec.CreateEncoder(options, _context),
        };
    }

    /// <summary>The track of video encoded again: the encoder's, keeping the source's number, names, timing and display.</summary>
    private VideoTrackInfo ReencodedTrack(VideoTrackInfo encoded) => encoded with
    {
        Number = _main!.Number,
        TimeBase = _main.TimeBase,
        FrameRate = _main.FrameRate ?? encoded.FrameRate,
        Language = _main.Language,
        Name = _main.Name,
        IsDefault = _main.IsDefault,
        Tags = _main.Tags,
        PixelAspectRatio = _main.PixelAspectRatio,
        Orientation = _main.Orientation,
        FieldOrder = _main.FieldOrder,
        Duration = null,
    };

    /// <summary>
    /// The average quantizer index of the held frames from <paramref name="from"/> on, as their headers say (for AV1 by
    /// decoding them from the keyframe), or null when none says.
    /// </summary>
    private int? AverageQuantizer(int from)
    {
        if (!_codec!.CanMatch || _gop.Count == 0)
        {
            return null;
        }

        long sum = 0, count = 0;
        try
        {
            using CodecHeaderReader reader = new(_main!, _context);
            for (int i = 0; i < _gop.Count; i++)
            {
                IReadOnlyList<CodecHeaderField> fields = reader.Describe(_gop[i].Packet);
                if (i >= from)
                {
                    foreach (int q in TrimCodec.Quantizers(fields, _main!.CodecId))
                    {
                        sum += q;
                        count++;
                    }
                }
            }
        }
        catch (CorruptDataException)
        {
        }

        return count == 0 ? null : (int)Math.Clamp((sum + (count / 2)) / count, 0, _codec.MaxQuantizer);
    }

    /// <summary>Copies a main track packet, ending the video after the last packet that shows a frame before the end.</summary>
    private void Copy(EncodedPacket packet)
    {
        if (!_codec!.InOrder)
        {
            // Frames may be reordered: keep what starts before the end, until packets decode at or after it.
            bool inside = _end is not { } stop || Before(packet.Timestamp, stop);
            bool past = _end is { } last && (packet.DecodeTimestamp ?? packet.Timestamp) >= last;
            if (inside)
            {
                EmitVideo(packet, shows: !packet.IsDiscardable, reencoded: false);
            }
            else
            {
                packet.Dispose();
            }

            if (past)
            {
                VideoDone();
            }

            return;
        }

        bool shows = _codec.ShowsFrame(packet);
        if (shows && _end is { } end && !Before(packet.Timestamp, end))
        {
            packet.Dispose();
            VideoDone();
            return;
        }

        if (!shows)
        {
            // A hidden frame is kept only once a frame it serves is.
            _pendingHidden.Add(packet);
            return;
        }

        foreach (EncodedPacket hidden in _pendingHidden)
        {
            EmitVideo(hidden, shows: false, reencoded: false);
        }

        _pendingHidden.Clear();
        EmitVideo(packet, shows: !packet.IsDiscardable, reencoded: false);
    }

    /// <summary>Whether a main track frame starts before <paramref name="limit"/>, by more than the container's rounding.</summary>
    private bool Before(MediaTime frame, MediaTime limit) => frame.Rescale(Rational.Nanosecond).Ticks + _halfTick < limit.Rescale(Rational.Nanosecond).Ticks;

    private void VideoDone()
    {
        _phase = Phase.Done;
        _videoDone = true;
        DisposeAll(_pendingHidden);
        Pump();
    }

    private void Other(EncodedPacket packet)
    {
        if (_cutIn is null || _holdOthers)
        {
            _raw.Add(packet);
            return;
        }

        Admit(packet);
    }

    /// <summary>Lets the packets of other tracks held while the start was unknown through, in the order read.</summary>
    private void AdmitRaw()
    {
        EncodedPacket[] held = [.. _raw];
        _raw.Clear();
        for (int i = 0; i < held.Length; i++)
        {
            try
            {
                Admit(held[i]);
            }
            catch
            {
                for (int j = i + 1; j < held.Length; j++)
                {
                    held[j].Dispose();
                }

                throw;
            }
        }
    }

    /// <summary>Drops held packets of other tracks that end too long before a keyframe at or before the start to matter.</summary>
    private void DropRawBefore(MediaTime keyframe)
    {
        _raw.RemoveAll(p =>
        {
            bool drop = _tracks[p.TrackNumber].Kind == TrackKind.Subtitle
                ? (p.Duration is { } shown ? p.Timestamp + shown <= keyframe : p.Timestamp < keyframe - CueLookBack)
                : p.Timestamp < keyframe - OtherLookBack;
            if (drop)
            {
                p.Dispose();
            }

            return drop;
        });
    }

    /// <summary>Cuts a packet of another track to <c>[start, end)</c> and queues it, moved so the cut starts at 0.</summary>
    private void Admit(EncodedPacket packet)
    {
        TrackInfo track = _tracks[packet.TrackNumber];
        MediaTime cut = _cutIn!.Value;
        if (_end is { } stop && (packet.DecodeTimestamp ?? packet.Timestamp) >= stop)
        {
            _running.Remove(track.Number);
            if (_audio.TryGetValue(track.Number, out AudioCut? done))
            {
                done.Resolved = true;
            }
        }

        switch (track)
        {
            case AudioTrackInfo audio:
                AdmitAudio(packet, audio, cut);
                return;

            case VideoTrackInfo:
                {
                    bool inside = packet.Timestamp >= cut && (_end is not { } end || packet.Timestamp < end);
                    if (!_started.Contains(track.Number))
                    {
                        // Another video track starts at its first keyframe in the cut.
                        inside = inside && packet.IsKeyframe;
                        if (inside)
                        {
                            _started.Add(track.Number);
                        }
                    }

                    Queue(packet, inside);
                    return;
                }

            default:
                if (track.Kind == TrackKind.Subtitle)
                {
                    Queue(packet, ClipCue(packet, cut, _end));
                }
                else
                {
                    Queue(packet, packet.Timestamp >= cut && (_end is not { } end || packet.Timestamp < end));
                }

                return;
        }
    }

    private void AdmitAudio(EncodedPacket packet, AudioTrackInfo track, MediaTime cut)
    {
        AudioCut state = _audio[track.Number];
        MediaTime time = packet.Timestamp;
        MediaTime? length = packet.Duration ?? Length(track, packet) ?? state.Step;
        if (state.Last is { } previous && time > previous)
        {
            MediaTime step = time - previous;
            state.Step = state.Step is { } known && known < step ? known : step;
        }

        state.Last = time;
        if (_end is { } stop && time >= stop)
        {
            packet.Dispose();
            return;
        }

        AudioCutKind kind = length is null && state.Kind == AudioCutKind.Pcm ? AudioCutKind.Nearest : state.Kind;
        switch (kind)
        {
            case AudioCutKind.Pcm:
                Queue(SlicePcm(packet, track, cut, length!.Value), true);
                return;

            case AudioCutKind.Container:
                {
                    MediaTime preRoll = track.SeekPreRoll ?? MediaTime.Zero;
                    MediaTime from = cut - preRoll;
                    if (length is { } d ? time + d <= from : time < from)
                    {
                        packet.Dispose();
                        return;
                    }

                    if (!state.Resolved)
                    {
                        state.Resolved = true;
                        state.Delay = time < cut ? Between(time, cut) : null;
                    }

                    if (_end is { } end && length is { } shown && time + shown > end)
                    {
                        // The container's own means stop the audio at the end: MP4's edit list, Matroska's DiscardPadding.
                        packet.Duration = Between(time, end).Rescale(new Rational(1, track.SampleRate));
                    }

                    Queue(packet, true);
                    return;
                }

            default:
                {
                    MediaTime middle = length is { } d ? time + new MediaTime(d.Ticks / 2, d.TimeBase) : time;
                    if (middle < cut || (_end is { } end && middle >= end))
                    {
                        packet.Dispose();
                        return;
                    }

                    if (!state.Resolved)
                    {
                        state.Resolved = true;
                        if (Math.Abs((time - cut).Seconds) * track.SampleRate >= 1)
                        {
                            Warn("trim-audio-packet", $"Audio track {track.Number} ({track.CodecId}) is cut at its packet {Seconds(time)} s, {Seconds(time - cut)} s from the video's start: {_format.Name} cannot cut it at the sample.", track.Number);
                        }
                    }

                    Shift(packet);
                    if (packet.Timestamp < MediaTime.Zero && _format is not Mp4Format)
                    {
                        packet.Timestamp = MediaTime.Zero;
                        packet.DecodeTimestamp = packet.DecodeTimestamp is null ? null : MediaTime.Zero;
                    }

                    EnqueueOther(packet);
                    return;
                }
        }
    }

    /// <summary>Gets an audio packet's length from its bytes, when the codec says it there (PCM, Opus).</summary>
    private static MediaTime? Length(AudioTrackInfo track, EncodedPacket packet)
    {
        if (FrameBytes(track) is int bytes && track.SampleRate > 0)
        {
            return new MediaTime(packet.Data.Length / bytes, new Rational(1, track.SampleRate));
        }

        if (track.CodecId == CodecIds.Opus && !packet.Data.IsEmpty)
        {
            int samples = OpusPacket.SampleCount(packet.Data.Span, 48000);
            return samples > 0 ? new MediaTime(samples, new Rational(1, 48000)) : null;
        }

        return null;
    }

    /// <summary>The bytes of one sample of every channel in a PCM track, or null for other codecs.</summary>
    private static int? FrameBytes(AudioTrackInfo track) => track.CodecId switch
    {
        CodecIds.G711MuLaw or CodecIds.G711ALaw when track.Channels > 0 => track.Channels,
        CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat when track.Channels > 0 && track.BitsPerSample is > 0 and var bits && bits % 8 == 0 => track.Channels * (bits / 8),
        _ => null,
    };

    /// <summary>Cuts a PCM packet to the samples inside <c>[cut, end)</c>; null when none are.</summary>
    private EncodedPacket? SlicePcm(EncodedPacket packet, AudioTrackInfo track, MediaTime cut, MediaTime length)
    {
        using (packet)
        {
            int bytes = FrameBytes(track)!.Value;
            Rational sample = new(1, track.SampleRate);
            long at = packet.Timestamp.Rescale(sample).Ticks;
            long count = packet.Data.Length / bytes;

            // A container's rounded timestamp (Matroska's millisecond) stands for the sample after the packet before it.
            AudioCut state = _audio[track.Number];
            long slack = Math.Max(1, new MediaTime(1, track.TimeBase).Rescale(sample).Ticks);
            if (state.NextSample is { } next && Math.Abs(at - next) <= slack)
            {
                at = next;
            }

            state.NextSample = at + count;
            long first = Math.Clamp(cut.Rescale(sample).Ticks - at, 0, count);
            long last = _end is { } end ? Math.Clamp(end.Rescale(sample).Ticks - at, 0, count) : count;
            if (last <= first)
            {
                return null;
            }

            EncodedPacket sliced = EncodedPacket.Rent(packet.TrackNumber, (int)((last - first) * bytes), new MediaTime(at + first, sample));
            packet.Data.Span.Slice((int)(first * bytes), (int)((last - first) * bytes)).CopyTo(sliced.GetWritableData().Span);
            sliced.Duration = new MediaTime(last - first, sample);
            sliced.IsKeyframe = true;
            return sliced;
        }
    }

    private AudioCutKind CutKind(AudioTrackInfo track)
    {
        if (FrameBytes(track) is not null && track.SampleRate > 0)
        {
            return AudioCutKind.Pcm;
        }

        return _format is Mp4Format || (_format is MatroskaFormat && track.CodecId == CodecIds.Opus) ? AudioCutKind.Container : AudioCutKind.Nearest;
    }

    /// <summary>
    /// Clips a cue to <c>[start, end)</c>: one shown across the start begins there, and one shown past the end stops there.
    /// Returns whether any of it is left; a cue without a duration is left when it starts inside.
    /// </summary>
    private static bool ClipCue(EncodedPacket packet, MediaTime start, MediaTime? end)
    {
        MediaTime time = packet.Timestamp;
        if (end is { } stop && time >= stop)
        {
            return false;
        }

        if (packet.Duration is not { } duration)
        {
            return time >= start;
        }

        MediaTime last = time + duration;
        if (time < start)
        {
            if (last <= start)
            {
                return false;
            }

            packet.Timestamp = start;
            packet.DecodeTimestamp = packet.DecodeTimestamp is null ? null : start;
            time = start;
        }

        if (end is { } limit && last > limit)
        {
            last = limit;
        }

        packet.Duration = Between(time, last);
        return true;
    }

    /// <summary>The time from <paramref name="from"/> to <paramref name="to"/>, in nanoseconds: a difference taken in either one's time base could round away.</summary>
    private static MediaTime Between(MediaTime from, MediaTime to) =>
        new(to.Rescale(Rational.Nanosecond).Ticks - from.Rescale(Rational.Nanosecond).Ticks, Rational.Nanosecond);

    /// <summary>Queues a packet of another track when it is kept, moved so the cut starts at 0; disposes it otherwise.</summary>
    private void Queue(EncodedPacket? packet, bool keep)
    {
        if (packet is null)
        {
            return;
        }

        if (!keep)
        {
            packet.Dispose();
            return;
        }

        Shift(packet);
        EnqueueOther(packet);
    }

    private void EnqueueOther(EncodedPacket packet)
    {
        // By decode time, after any packet with the same time, so each track keeps its order.
        MediaTime time = packet.DecodeTimestamp ?? packet.Timestamp;
        int lo = 0, hi = _otherQueue.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if ((_otherQueue[mid].DecodeTimestamp ?? _otherQueue[mid].Timestamp) <= time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        _otherQueue.Insert(lo, packet);
        Pump();
    }

    private void EmitVideo(EncodedPacket packet, bool shows, bool reencoded)
    {
        Shift(packet);
        _videoQueue.Enqueue(packet);
        _frames += shows ? 1 : 0;
        _reencoded += shows && reencoded ? 1 : 0;
        Pump();
    }

    private void Shift(EncodedPacket packet)
    {
        MediaTime cut = _cutIn!.Value;
        packet.Timestamp -= cut;
        if (packet.DecodeTimestamp is { } decode)
        {
            packet.DecodeTimestamp = decode - cut;
        }
    }

    /// <summary>
    /// Whether the muxer can open: the start and the main track's output are known, and each Matroska Opus track has its first
    /// packet (which sets its CodecDelay), or the input has moved two seconds past the start without one.
    /// </summary>
    private bool Ready()
    {
        if (_endOfStream)
        {
            return true;
        }

        if (_cutIn is not { } cut || (_main is not null && _mainOut is null))
        {
            return false;
        }

        return _lastRead >= cut + OtherLookBack
            || _audio.All(a => a.Value.Kind != AudioCutKind.Container || _format is not MatroskaFormat || a.Value.Resolved);
    }

    /// <summary>Writes what can be written: each video packet, with the other packets that decode before it first.</summary>
    private void Pump()
    {
        if (!Ready())
        {
            return;
        }

        Muxer ??= Open();
        while (_videoQueue.TryDequeue(out EncodedPacket? video))
        {
            MediaTime time = video.DecodeTimestamp ?? video.Timestamp;
            WriteOthers(time);
            _lastVideo = time;
            Write(video);
        }

        if (_videoDone || _endOfStream)
        {
            WriteOthers(null);
        }
        else if (_lastVideo is { } last)
        {
            WriteOthers(last);
        }
    }

    private void WriteOthers(MediaTime? upTo)
    {
        int count = 0;
        while (count < _otherQueue.Count && (upTo is not { } limit || (_otherQueue[count].DecodeTimestamp ?? _otherQueue[count].Timestamp) <= limit))
        {
            count++;
        }

        EncodedPacket[] ready = [.. _otherQueue.Take(count)];
        _otherQueue.RemoveRange(0, count);
        for (int i = 0; i < ready.Length; i++)
        {
            try
            {
                Write(ready[i]);
            }
            catch
            {
                for (int j = i + 1; j < ready.Length; j++)
                {
                    ready[j].Dispose();
                }

                throw;
            }
        }
    }

    private void Write(EncodedPacket packet)
    {
        using (packet)
        {
            Muxer!.WritePacket(packet);
            _packets++;
            _written?.Invoke(packet);
        }
    }

    private IMuxer Open()
    {
        MediaTime cut = _cutIn ?? _start;
        List<TrackInfo> tracks = [];
        foreach (TrackInfo track in _kept)
        {
            TrackInfo output = track.Number == _main?.Number ? _mainOut ?? track : track;
            if (output is AudioTrackInfo audio && _audio.TryGetValue(audio.Number, out AudioCut? state))
            {
                output = state.Kind switch
                {
                    AudioCutKind.Container when _format is MatroskaFormat => audio with { CodecDelay = state.Delay },
                    AudioCutKind.Nearest when cut > MediaTime.Zero => audio with { CodecDelay = null },
                    _ => audio,
                };
            }

            tracks.Add(output with { Duration = null });
        }
        MediaInfo info = _demuxer.Info;
        MediaMetadata metadata = info.Metadata with { Chapters = Clip(info.Chapters, cut, _end) };
        return _createMuxer(tracks, metadata);
    }

    /// <summary>The chapters that overlap <c>[start, end)</c>, clipped to it and moved so it starts at 0.</summary>
    private static List<ChapterInfo> Clip(IReadOnlyList<ChapterInfo> chapters, MediaTime start, MediaTime? end)
    {
        List<ChapterInfo> clipped = [];
        foreach (ChapterInfo chapter in chapters)
        {
            MediaTime? chapterEnd = chapter.End ?? end;
            if ((end is { } stop && chapter.Start >= stop) || (chapterEnd is { } last && last <= start))
            {
                continue;
            }

            MediaTime from = chapter.Start < start ? start : chapter.Start;
            MediaTime? to = chapterEnd is { } e && end is { } limit && e > limit ? limit : chapterEnd;
            clipped.Add(chapter with { Start = from - start, End = to is { } t ? t - start : null });
        }

        return clipped;
    }

    private void EndOfStream()
    {
        switch (_phase)
        {
            case Phase.Locating:
                if (_gop.Count == 0)
                {
                    throw new InvalidDataException($"The {CodecName(_main!)} track has no keyframe, so there is nothing to cut.");
                }

                throw new ArgumentOutOfRangeException("start", _start, $"The start {Seconds(_start)} s is at or past the last frame of the video.");
            case Phase.Head:
                RunHead();
                if (_phase == Phase.Reencoding)
                {
                    FinishReencode();
                }
                else
                {
                    VideoDone();
                }

                break;
            case Phase.Reencoding:
                FinishReencode();
                break;
            case Phase.Copying:
                VideoDone();
                break;
        }

        AdmitRaw();
        _endOfStream = true;
        Pump();
        Muxer ??= Open();
        if (_finalTrack is { } track && Muxer is ITrackUpdatable updatable)
        {
            updatable.UpdateTrack(track);
        }

        Muxer.Finish();
    }

    private IEnumerable<(EncodedPacket Packet, bool Shows)> TakeGop(int from)
    {
        (EncodedPacket Packet, bool Shows)[] taken = [.. _gop];
        _gop.Clear();
        for (int i = 0; i < from; i++)
        {
            taken[i].Packet.Dispose();
        }

        return taken.Skip(from);
    }

    private void DropGop()
    {
        foreach ((EncodedPacket packet, _) in _gop)
        {
            packet.Dispose();
        }

        _gop.Clear();
    }

    /// <summary>Whether an AV1 temporal unit starts with a temporal delimiter, as IVF and raw OBU files keep them.</summary>
    private static bool StartsWithDelimiter(ReadOnlySpan<byte> unit) => unit.Length >= 2 && ((unit[0] >> 3) & 15) == Av1Obus.TemporalDelimiter;

    private void Warn(string code, string message, int? track = null)
    {
        MediaWarning warning = new(code, message, track ?? _main?.Number);
        _warnings.Add(warning);
        _context.Warnings?.Invoke(warning);
    }

    private static string CodecName(TrackInfo track) => track.CodecId switch
    {
        CodecIds.Vp8 => "VP8",
        CodecIds.Vp9 => "VP9",
        CodecIds.Av1 => "AV1",
        CodecIds.Avc => "H.264",
        CodecIds.Hevc => "HEVC",
        CodecIds.MotionJpeg => "Motion JPEG",
        _ => track.CodecId,
    };

    private static string Seconds(MediaTime time) => time.Seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    private static void DisposeAll(List<EncodedPacket> packets)
    {
        foreach (EncodedPacket packet in packets)
        {
            packet.Dispose();
        }

        packets.Clear();
    }

    /// <summary>How an audio track is being cut.</summary>
    private sealed class AudioCut(AudioCutKind kind)
    {
        public AudioCutKind Kind => kind;

        /// <summary>Gets or sets whether the track's first packet in the cut has been seen.</summary>
        public bool Resolved { get; set; }

        /// <summary>Gets or sets how much of a Matroska Opus track's first packet comes before the start (its CodecDelay).</summary>
        public MediaTime? Delay { get; set; }

        /// <summary>Gets or sets the last packet's time, and the shortest step between packets, for packets without a duration.</summary>
        public MediaTime? Last { get; set; }

        public MediaTime? Step { get; set; }

        /// <summary>Gets or sets the sample after a PCM track's last packet.</summary>
        public long? NextSample { get; set; }
    }
}
