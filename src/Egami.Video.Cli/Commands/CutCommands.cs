using System.CommandLine;
using System.Globalization;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video trim</c>, <c>concat</c> and <c>segment</c> (Build Plan task 6, Video CLI design Q5): cut, join and split files
/// by copying packets, never decoding. Cuts start at video keyframes, so a stream copy plays from its first frame; each command
/// says on standard error where a cut really starts when that is not where it was asked to.
/// </summary>
internal static class CutCommands
{
    public static Command Trim(VideoSession session)
    {
        MediaOptions options = new();
        Option<string?> from = new("--from") { Description = "Where the cut starts: seconds, [HH:]MM:SS[.mmm] or a frame number such as #250; the copy starts at the video keyframe at or before it.", HelpName = "time" };
        Option<string?> to = new("--to") { Description = "Where the cut ends, written as for --from; nothing that starts at or after it is kept.", HelpName = "time" };
        Option<string?> duration = new("--duration") { Description = "How long the cut is, counted from --from, instead of --to.", HelpName = "time" };
        Command command = new("trim", "Cut a part out of files without decoding: every track from the video keyframe at or before --from up to --to, with timestamps starting at 0. Like ffmpeg -ss -to -c copy.");
        options.AddTo(command);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(duration);
        command.SetAction(result =>
        {
            string? fromText = result.GetValue(from);
            string? toText = result.GetValue(to);
            string? durationText = result.GetValue(duration);
            if (fromText is null && toText is null && durationText is null)
            {
                throw new UsageException("trim needs --from, --to or --duration, such as --from 1:30 --to 2:00.");
            }

            if (toText is not null && durationText is not null)
            {
                throw new UsageException("Give --to or --duration, not both.");
            }

            TimeValue? start = fromText is null ? null : TimeValue.Parse(fromText, "--from");
            TimeValue? end = toText is null ? null : TimeValue.Parse(toText, "--to");
            TimeValue? length = durationText is null ? null : TimeValue.ParseDuration(durationText, "--duration");
            if (start is { } s && end is { } e && s.IsFrame == e.IsFrame && (s.IsFrame ? e.Frame <= s.Frame : e.Seconds <= s.Seconds))
            {
                throw new UsageException($"--to {e.Text} must be after --from {s.Text}.");
            }

            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs, "out/{name}.mkv");
            TrackSelection selection = options.Tracks(result);
            string? format = result.GetValue(options.Format);
            return Batch.Run(session, inputs, batch, input =>
            {
                MediaTarget target = MediaIO.Resolve(session, input, output, format, OutputExtension(session, input));
                RunTrim(session, input, target, selection, start, end, length, inputs.Count);
                return [target.Path];
            });
        });
        return command;
    }

    public static Command Concat(VideoSession session)
    {
        MediaOptions options = new();
        Command command = new("concat", "Join clips with the same tracks and codecs into one file without decoding, one after another in the order given (globs sorted by name).");
        options.AddTo(command);
        command.SetAction(result =>
        {
            options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            if (inputs.Count < 2)
            {
                throw new UsageException("concat joins two or more clips: give them in order, such as concat a.mkv b.mkv -o joined.mkv.");
            }

            if (inputs.Count(i => i.IsStdin) > 1)
            {
                throw new UsageException("Standard input (-) can be one of the clips only once.");
            }

            string text = result.GetValue(options.Output)
                ?? throw new UsageException("No output: give -o with a file, such as -o joined.mkv, or - with --format for standard output.");
            OutputTemplate template = OutputTemplate.Parse(text);
            MediaTarget target = MediaIO.Resolve(session, inputs[0], template, result.GetValue(options.Format), OutputExtension(session, inputs[0]));
            if (!session.InPlace && inputs.FirstOrDefault(i => i.Path is { } path && Batch.SamePath(path, target.Path)) is { } same)
            {
                throw new UsageException($"{same.Display}: the output would replace an input; give another -o, or --in-place to replace it.");
            }

            RunConcat(session, inputs, target, options.Tracks(result));
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command Segment(VideoSession session)
    {
        MediaOptions options = new();
        Option<string?> every = new("--every") { Description = "Split every so often, such as 10 or 1:00; each part starts at the first video keyframe at or after its split point.", HelpName = "time" };
        Option<string?> at = new("--at") { Description = "Split at these times, separated by commas, such as 1:00,2:30 or #250,#500; each part starts at the first video keyframe at or after its split point.", HelpName = "times" };
        Option<bool> keepTimestamps = new("--keep-timestamps") { Description = "Keep the input's timestamps in every part instead of starting each part at 0." };
        Command command = new("segment", "Split files into parts without decoding, at video keyframes: -o names the parts with {part} (from 1), such as out-{part:000}.mkv; a folder takes {name}-{part:000}.{ext}.");
        options.AddTo(command);
        command.Options.Add(every);
        command.Options.Add(at);
        command.Options.Add(keepTimestamps);
        command.SetAction(result =>
        {
            string? everyText = result.GetValue(every);
            string? atText = result.GetValue(at);
            if ((everyText is null) == (atText is null))
            {
                throw new UsageException("segment needs --every or --at (not both), such as --every 10 or --at 1:00,2:30.");
            }

            TimeValue? interval = everyText is null ? null : TimeValue.ParseDuration(everyText, "--every", allowFrames: false);
            TimeValue[] points = atText is null ? [] : [.. atText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => TimeValue.Parse(t, "--at"))];
            if (atText is not null && points.Length == 0)
            {
                throw new UsageException("--at needs one or more times separated by commas, such as --at 1:00,2:30.");
            }

            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            string text = result.GetValue(options.Output)
                ?? throw new UsageException("No output: give -o with a folder or a template with {part}, such as -o out/{name}-{part:000}.mkv.");
            if (text == "-")
            {
                throw new UsageException("segment writes several files, not standard output: give a folder or a template with {part}, such as -o out-{part:000}.mkv.");
            }

            OutputTemplate output = MediaOptions.CheckTemplate(session, text, inputs, "out/{name}-{part:000}.mkv", part: "part");
            bool folder = text.EndsWith('/') || text.EndsWith('\\') || (!output.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
            if (!folder && !text.Contains("{part", StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageException($"segment writes one file per part: put {{part}} in -o, such as {Path.GetFileNameWithoutExtension(text)}-{{part:000}}{Path.GetExtension(text)}, or give a folder.");
            }

            TrackSelection selection = options.Tracks(result);
            string? format = result.GetValue(options.Format);
            bool keep = result.GetValue(keepTimestamps);
            return Batch.Run(session, inputs, batch, input => RunSegment(session, input, output, format, selection, interval, points, keep, inputs.Count));
        });
        return command;
    }

    /// <summary>The extension an output takes when -o does not say: the input's when egami-video writes it, otherwise mkv.</summary>
    internal static string OutputExtension(VideoSession session, InputFile input)
    {
        if (input.Extension.Length == 0)
        {
            return "mkv";
        }

        try
        {
            MediaIO.FindContainer(session, input.Extension);
            return input.Extension;
        }
        catch (UsageException)
        {
            return "mkv";
        }
    }

    /// <summary>The tracks to give a muxer: the input's, without the durations that no longer hold for a cut.</summary>
    private static TrackInfo[] ForOutput(IEnumerable<TrackInfo> tracks) => [.. tracks.Select(t => t with { Duration = null })];

    private static string Seconds(MediaTime time) => Math.Abs(time.Seconds).ToString("0.000", CultureInfo.InvariantCulture);

    private static void RunTrim(VideoSession session, InputFile input, MediaTarget target, TrackSelection selection, TimeValue? fromValue, TimeValue? toValue, TimeValue? lengthValue, int count)
    {
        IReadOnlyList<MediaTime>? frames = null;
        IReadOnlyList<MediaTime> FrameTimes() => frames ??= input.IsStdin
            ? throw new UsageException("Frame numbers (#n) need a file, not standard input: give a time such as 1:30.")
            : MediaIO.FrameTimes(session, input);

        MediaTime from = fromValue?.Resolve(FrameTimes, input.Display, "--from") ?? MediaTime.Zero;
        MediaTime? to = toValue?.Resolve(FrameTimes, input.Display, "--to");
        if (lengthValue is { } length)
        {
            if (length.Frame is { } frameCount)
            {
                IReadOnlyList<MediaTime> times = FrameTimes();
                int first = 0;
                while (first < times.Count && times[first] < from)
                {
                    first++;
                }

                to = first + frameCount < times.Count ? times[(int)(first + frameCount)] : null;
            }
            else
            {
                to = from + MediaTime.FromSeconds(length.Seconds);
            }
        }

        if (to is { } end && end <= from)
        {
            throw new UsageException($"{input.Display}: --to ({TimeValue.Format(end)}) must be after --from ({TimeValue.Format(from)}).");
        }

        using IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
        TrackInfo[] tracks = [.. selection.Select(demuxer.Tracks, input.Display)];
        if (tracks.Length == 0)
        {
            throw new UsageException($"{input.Display}: no tracks left to write.");
        }

        IProgress<MediaProgress>? progress = session.ProgressSink?.For(input, input.Index, count);
        MediaIO.Write(session, target.Path, output => new TrimJob(session, input, demuxer, tracks, target, from, to, fromValue is not null, progress).Run(output));
    }

    /// <summary>
    /// One trim: finds the main track's keyframe at or before --from (seeking near it when the input can seek, reading through
    /// otherwise), holds the packets read since that keyframe until it is sure no later keyframe comes before --from, then copies
    /// every selected track's packets from there up to --to, moved so the output starts at 0.
    /// </summary>
    private sealed class TrimJob(VideoSession session, InputFile input, IDemuxer demuxer, TrackInfo[] tracks, MediaTarget target, MediaTime from, MediaTime? to, bool fromGiven, IProgress<MediaProgress>? sink)
    {
        private readonly Dictionary<int, TrackInfo> _tracks = tracks.ToDictionary(t => t.Number);
        private readonly TrackInfo _main = CutTimes.MainTrack(tracks);
        private readonly HashSet<int> _started = [];
        private readonly HashSet<int> _running = [.. tracks.Where(t => t.Kind is TrackKind.Video or TrackKind.Audio).Select(t => t.Number)];
        private IMuxer? _muxer;
        private JobProgress? _progress;
        private MediaTime _start;

        public void Run(Stream output)
        {
            CountingStream counted = new(output);
            MediaTime? length = to is { } end ? end - from : demuxer.Info.Duration is { } d && d > from ? d - from : null;
            _progress = new JobProgress(sink, length, tracks, session.Cancellation, counted);
            SeekNear();

            List<EncodedPacket> pending = [];
            MediaTime? candidate = null;
            MediaTime mainEnd = MediaTime.Zero;
            bool started = false;
            try
            {
                while (demuxer.ReadPacket() is { } packet)
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    if (!_tracks.ContainsKey(packet.TrackNumber))
                    {
                        packet.Dispose();
                        continue;
                    }

                    if (started)
                    {
                        if (Copy(packet))
                        {
                            break;
                        }

                        continue;
                    }

                    if (packet.TrackNumber == _main.Number)
                    {
                        MediaTime stop = packet.Timestamp + (CutTimes.Length(packet, _main) ?? MediaTime.Zero);
                        mainEnd = stop > mainEnd ? stop : mainEnd;
                        if (packet.IsKeyframe && packet.Timestamp <= from)
                        {
                            // A later keyframe at or before --from: everything held before it is no longer needed.
                            MediaTime keyframe = packet.Timestamp;
                            candidate = keyframe;
                            Drop(pending, p => p.TrackNumber == _main.Number || p.Timestamp < keyframe);
                            pending.Add(packet);
                            continue;
                        }

                        if (packet.Timestamp <= from || (candidate is null && !packet.IsKeyframe))
                        {
                            if (candidate is null)
                            {
                                packet.Dispose();
                            }
                            else
                            {
                                pending.Add(packet);
                            }

                            continue;
                        }

                        // Past --from: the start is the last keyframe at or before it, or this one when there was none.
                        pending.Add(packet);
                        started = true;
                        if (Begin(candidate ?? packet.Timestamp, counted, pending))
                        {
                            break;
                        }

                        continue;
                    }

                    if (candidate is { } c && packet.Timestamp < c)
                    {
                        packet.Dispose();
                    }
                    else
                    {
                        pending.Add(packet);
                    }
                }

                if (!started)
                {
                    if (candidate is not { } last || (fromGiven && mainEnd <= from))
                    {
                        throw new InvalidDataException(fromGiven
                            ? $"--from {TimeValue.Format(from)} is at or past the end of the {Kinds.Name(_main.Kind)}; nothing to copy."
                            : $"the {Kinds.Name(_main.Kind)} has no keyframe; nothing to copy.");
                    }

                    Begin(last, counted, pending);
                }

                _muxer!.Finish();
                _progress.Finish();
            }
            finally
            {
                Drop(pending, _ => true);
                _muxer?.Dispose();
            }
        }

        /// <summary>
        /// Seeks to the main track's keyframe before the one at or before --from, when the input can seek: starting one keyframe
        /// early also reads the packets of other tracks that the file stores ahead of the start.
        /// </summary>
        private void SeekNear()
        {
            if (!demuxer.CanSeek || from <= MediaTime.Zero)
            {
                return;
            }

            demuxer.Seek(from, _main.Number);
            MediaTime? found = null;
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (packet.TrackNumber == _main.Number)
                    {
                        found = packet.Timestamp;
                        break;
                    }
                }
            }

            demuxer.Seek(found is { } keyframe ? keyframe - CutTimes.FromNanoseconds(1) : from, _main.Number);
        }

        /// <summary>Opens the muxer once the start is known, says how far it moved, and copies the held packets.</summary>
        private bool Begin(MediaTime start, CountingStream counted, List<EncodedPacket> pending)
        {
            _start = start;
            double moved = (from - start).Seconds;
            if (fromGiven && Math.Abs(moved) >= 0.0005)
            {
                string what = _main.Kind == TrackKind.Video ? "keyframe" : "packet";
                session.Warn(moved > 0
                    ? $"{input.Display}: starts at {TimeValue.Format(start)}, {Seconds(from - start)} s before --from (the {what} before it)"
                    : $"{input.Display}: starts at {TimeValue.Format(start)}, {Seconds(start - from)} s after --from (the first {what})");
            }

            _muxer = MediaIO.CreateMuxer(session, counted, target.Container, ForOutput(tracks), CutTimes.Metadata(demuxer.Info, start, to, start));
            bool done = false;
            EncodedPacket[] held = [.. pending];
            pending.Clear();
            for (int i = 0; i < held.Length; i++)
            {
                try
                {
                    done = Copy(held[i]) || done;
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

            return done;
        }

        /// <summary>Copies a packet when it falls inside the cut; returns whether every video and audio track has passed --to.</summary>
        private bool Copy(EncodedPacket packet)
        {
            using (packet)
            {
                TrackInfo track = _tracks[packet.TrackNumber];
                MediaTime time = packet.Timestamp;
                bool inside = time >= _start && (to is not { } end || time < end);
                if (track.Kind == TrackKind.Video && !_started.Contains(track.Number))
                {
                    // A video track starts at a keyframe: the main track at the start itself, others at their first one after it.
                    inside = inside && packet.IsKeyframe && (track.Number != _main.Number || time == _start);
                    if (inside)
                    {
                        _started.Add(track.Number);
                    }
                }

                if (to is { } stop && CutTimes.DecodeTime(packet) >= stop)
                {
                    _running.Remove(track.Number);
                }

                if (inside)
                {
                    CutTimes.ShiftBack(packet, _start);
                    _muxer!.WritePacket(packet);
                    _progress!.Packet(packet.TrackNumber, packet.Timestamp);
                }

                return to is not null && _running.Count == 0 && tracks.Any(t => t.Kind is TrackKind.Video or TrackKind.Audio);
            }
        }

        private static void Drop(List<EncodedPacket> packets, Predicate<EncodedPacket> match)
        {
            packets.RemoveAll(p =>
            {
                if (match(p))
                {
                    p.Dispose();
                    return true;
                }

                return false;
            });
        }
    }

    /// <summary>One clip of a concat: its input, the demuxer standard input was read with, its tracks and headers.</summary>
    private sealed record Clip(InputFile Input, IDemuxer? Open, TrackInfo[] Tracks, MediaInfo Info);

    private static void RunConcat(VideoSession session, List<InputFile> inputs, MediaTarget target, TrackSelection selection)
    {
        List<Clip> clips = [];
        try
        {
            foreach (InputFile input in inputs)
            {
                IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
                Clip clip;
                try
                {
                    clip = new(input, input.IsStdin ? demuxer : null, [.. selection.Select(demuxer.Tracks, input.Display)], demuxer.Info);
                }
                catch
                {
                    demuxer.Dispose();
                    throw;
                }

                if (!input.IsStdin)
                {
                    demuxer.Dispose();
                }

                clips.Add(clip);
                if (clip.Tracks.Length == 0)
                {
                    throw new UsageException($"{input.Display}: no tracks left to write.");
                }

                if (clips.Count > 1 && Mismatch(clips[0], clip) is { } difference)
                {
                    throw new InvalidDataException($"{input.Display} does not match {clips[0].Input.Display}: {difference}. concat joins clips with the same tracks and codecs.");
                }
            }

            TrackInfo[] tracks = clips[0].Tracks;
            MediaTime? total = clips.All(c => c.Info.Duration is not null) ? CutTimes.FromNanoseconds(clips.Sum(c => CutTimes.Nanoseconds(c.Info.Duration!.Value))) : null;
            IProgress<MediaProgress>? sink = session.ProgressSink?.For(inputs[0], 0, 1);
            MediaIO.Write(session, target.Path, output =>
            {
                CountingStream counted = new(output);
                using IMuxer muxer = MediaIO.CreateMuxer(session, counted, target.Container, ForOutput(tracks), clips[0].Info.Metadata);
                JobProgress progress = new(sink, total, tracks, session.Cancellation, counted);
                MediaTime offset = MediaTime.Zero;
                for (int i = 0; i < clips.Count; i++)
                {
                    offset = CopyClip(session, clips[i], tracks, i == 0, offset, muxer, progress);
                    session.Info($"{clips[i].Input.Display}: ends at {TimeValue.Format(offset)}");
                }

                muxer.Finish();
                progress.Finish();
            });
        }
        finally
        {
            foreach (Clip clip in clips)
            {
                clip.Open?.Dispose();
            }
        }
    }

    /// <summary>The first way a clip differs from the first one, or <see langword="null"/> when they can be joined.</summary>
    private static string? Mismatch(Clip first, Clip clip)
    {
        if (first.Tracks.Length != clip.Tracks.Length)
        {
            return $"it has {clip.Tracks.Length} tracks to join, the first clip {first.Tracks.Length} ({TrackSelection.Describe(clip.Tracks)}, against {TrackSelection.Describe(first.Tracks)})";
        }

        for (int i = 0; i < first.Tracks.Length; i++)
        {
            TrackInfo a = first.Tracks[i];
            TrackInfo b = clip.Tracks[i];
            string name = $"track {b.Number} ({Kinds.Name(b.Kind)} {CodecNames.Name(b.CodecId)})";
            if (a.Kind != b.Kind || a.CodecId != b.CodecId)
            {
                return $"{name} is in the place of {Kinds.Name(a.Kind)} {CodecNames.Name(a.CodecId)}";
            }

            switch (a, b)
            {
                case (VideoTrackInfo va, VideoTrackInfo vb) when va.Width != vb.Width || va.Height != vb.Height:
                    return $"{name} is {vb.Width}x{vb.Height}, the first clip's {va.Width}x{va.Height}";
                case (AudioTrackInfo aa, AudioTrackInfo ab) when aa.SampleRate != ab.SampleRate:
                    return $"{name} has {ab.SampleRate} Hz, the first clip's {aa.SampleRate} Hz";
                case (AudioTrackInfo aa, AudioTrackInfo ab) when aa.Channels != ab.Channels:
                    return $"{name} has {ab.Channels} channels, the first clip's {aa.Channels}";
            }

            if (!a.CodecPrivate.Span.SequenceEqual(b.CodecPrivate.Span))
            {
                return $"{name} has other codec setup data (CodecPrivate) than the first clip's, so it was encoded with other settings";
            }
        }

        return null;
    }

    /// <summary>
    /// Copies one clip's packets, placed at <paramref name="offset"/>: the clip's start (its first main-track packet; the first
    /// clip keeps its own timestamps) moves there. Returns where the clip ends: the end of its video, or of its longest track
    /// when it has no video.
    /// </summary>
    private static MediaTime CopyClip(VideoSession session, Clip clip, TrackInfo[] tracks, bool first, MediaTime offset, IMuxer muxer, JobProgress progress)
    {
        Dictionary<int, TrackInfo> map = [];
        for (int i = 0; i < clip.Tracks.Length; i++)
        {
            map[clip.Tracks[i].Number] = tracks[i];
        }

        int main = CutTimes.MainTrack(clip.Tracks).Number;
        bool hasVideo = clip.Tracks.Any(t => t.Kind == TrackKind.Video);
        MediaTime? shift = first ? MediaTime.Zero : null;
        MediaTime end = offset;
        Dictionary<int, long> steps = [];
        Dictionary<int, MediaTime> last = [];
        List<EncodedPacket> held = [];

        void Write(EncodedPacket packet)
        {
            using (packet)
            {
                TrackInfo track = map[packet.TrackNumber];
                packet.TrackNumber = track.Number;
                CutTimes.ShiftBack(packet, shift!.Value);
                if (last.TryGetValue(track.Number, out MediaTime previous) && packet.Timestamp > previous)
                {
                    long delta = CutTimes.Nanoseconds(packet.Timestamp - previous);
                    steps[track.Number] = steps.TryGetValue(track.Number, out long step) ? Math.Min(step, delta) : delta;
                }

                last[track.Number] = packet.Timestamp;
                if (!hasVideo || track.Kind == TrackKind.Video)
                {
                    MediaTime stop = packet.Timestamp + (CutTimes.Length(packet, track) ?? CutTimes.FromNanoseconds(steps.GetValueOrDefault(track.Number)));
                    end = stop > end ? stop : end;
                }

                muxer.WritePacket(packet);
                progress.Packet(packet.TrackNumber, packet.Timestamp);
            }
        }

        void Flush()
        {
            EncodedPacket[] packets = [.. held];
            held.Clear();
            for (int i = 0; i < packets.Length; i++)
            {
                try
                {
                    Write(packets[i]);
                }
                catch
                {
                    for (int j = i + 1; j < packets.Length; j++)
                    {
                        packets[j].Dispose();
                    }

                    throw;
                }
            }
        }

        IDemuxer demuxer = clip.Open ?? MediaIO.OpenDemuxer(session, clip.Input);
        try
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                if (!map.ContainsKey(packet.TrackNumber))
                {
                    packet.Dispose();
                    continue;
                }

                if (shift is null)
                {
                    // The clip's start is its first main-track packet that shows; what comes before it waits for it.
                    held.Add(packet);
                    if (packet.TrackNumber == main && !packet.IsDiscardable)
                    {
                        shift = packet.Timestamp - offset;
                        Flush();
                    }

                    continue;
                }

                Write(packet);
            }

            if (held.Count > 0)
            {
                shift ??= held.Min(p => p.Timestamp) - offset;
                Flush();
            }
        }
        finally
        {
            foreach (EncodedPacket h in held)
            {
                h.Dispose();
            }

            if (clip.Open is null)
            {
                demuxer.Dispose();
            }
        }

        return end;
    }

    /// <summary>The split points of a segment job, in order, from --every or --at.</summary>
    private sealed class SplitPoints(long? every, List<MediaTime> at)
    {
        private int _index;
        private long _count = 1;

        /// <summary>Gets the next split point, or <see langword="null"/> when there are no more.</summary>
        public MediaTime? Next => every is { } step
            ? CutTimes.FromNanoseconds(step * _count)
            : _index < at.Count ? at[_index] : null;

        /// <summary>Moves past every split point at or before <paramref name="time"/>.</summary>
        public void After(MediaTime time)
        {
            if (every is { } step)
            {
                _count = Math.Max(_count, (CutTimes.Nanoseconds(time) / step) + 1);
                return;
            }

            while (_index < at.Count && at[_index] <= time)
            {
                _index++;
            }
        }
    }

    /// <summary>One output file of a segment job.</summary>
    private sealed class Part(int number, MediaTime? start, MediaTime shift, CutFile file, IMuxer muxer) : IDisposable
    {
        public int Number => number;

        /// <summary>Gets where the part starts in the input, or <see langword="null"/> for the first part.</summary>
        public MediaTime? Start => start;

        public MediaTime Shift => shift;

        public IMuxer Muxer => muxer;

        public void Commit()
        {
            muxer.Finish();
            muxer.Dispose();
            file.Commit();
        }

        public void Dispose()
        {
            muxer.Dispose();
            file.Dispose();
        }
    }

    private static List<string> RunSegment(VideoSession session, InputFile input, OutputTemplate output, string? format, TrackSelection selection, TimeValue? interval, TimeValue[] points, bool keep, int count)
    {
        IReadOnlyList<MediaTime>? frames = null;
        IReadOnlyList<MediaTime> FrameTimes() => frames ??= input.IsStdin
            ? throw new UsageException("Frame numbers (#n) need a file, not standard input: give times such as 1:00,2:30.")
            : MediaIO.FrameTimes(session, input);
        List<MediaTime> at = [.. points.Select(p => p.Resolve(FrameTimes, input.Display, "--at")).Distinct().Order()];
        SplitPoints splits = new(interval is { } every ? CutTimes.Nanoseconds(MediaTime.FromSeconds(every.Seconds)) : null, at);

        string extension = OutputExtension(session, input);
        using IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
        TrackInfo[] tracks = [.. selection.Select(demuxer.Tracks, input.Display)];
        if (tracks.Length == 0)
        {
            throw new UsageException($"{input.Display}: no tracks left to write.");
        }

        TrackInfo main = CutTimes.MainTrack(tracks);
        HashSet<int> kept = [.. tracks.Select(t => t.Number)];
        TrackInfo[] outputTracks = ForOutput(tracks);
        List<Part> open = [];
        List<string> written = [];
        List<EncodedPacket> held = [];
        JobProgress progress = new(session.ProgressSink?.For(input, input.Index, count), demuxer.Info.Duration, tracks, session.Cancellation);

        Part NewPart(MediaTime? start)
        {
            MediaTarget target = MediaIO.Resolve(session, input, output, format, extension, written.Count + 1);
            MediaTime shift = keep || start is null ? MediaTime.Zero : start.Value;
            CutFile file = CutFile.Create(session, target.Path);
            try
            {
                MediaMetadata metadata = CutTimes.Metadata(demuxer.Info, start ?? MediaTime.Zero, splits.Next, shift);
                Part part = new(written.Count + 1, start, shift, file, MediaIO.CreateMuxer(session, file.Stream, target.Container, outputTracks, metadata));
                written.Add(target.Path);
                open.Add(part);
                return part;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        void Write(Part part, EncodedPacket packet)
        {
            using (packet)
            {
                MediaTime time = packet.Timestamp;
                CutTimes.ShiftBack(packet, part.Shift);
                part.Muxer.WritePacket(packet);
                progress.Packet(packet.TrackNumber, time);
            }
        }

        // A packet of another track goes to the part its time falls in, among the parts still open.
        void Route(EncodedPacket packet) => Write(open.LastOrDefault(p => p.Start is not { } s || s <= packet.Timestamp) ?? open[0], packet);

        void Flush()
        {
            EncodedPacket[] packets = [.. held];
            held.Clear();
            for (int i = 0; i < packets.Length; i++)
            {
                try
                {
                    Route(packets[i]);
                }
                catch
                {
                    for (int j = i + 1; j < packets.Length; j++)
                    {
                        packets[j].Dispose();
                    }

                    throw;
                }
            }
        }

        try
        {
            Part current = NewPart(null);
            bool mainSeen = false;
            while (demuxer.ReadPacket() is { } packet)
            {
                if (!kept.Contains(packet.TrackNumber))
                {
                    packet.Dispose();
                    continue;
                }

                if (packet.TrackNumber == main.Number)
                {
                    if (!mainSeen)
                    {
                        // Split points at or before the start make no empty part.
                        mainSeen = true;
                        splits.After(packet.Timestamp);
                    }
                    else if (packet.IsKeyframe && splits.Next is { } next && packet.Timestamp >= next)
                    {
                        splits.After(packet.Timestamp);
                        if (open.Count == 2)
                        {
                            open[0].Commit();
                            open.RemoveAt(0);
                        }

                        try
                        {
                            current = NewPart(packet.Timestamp);
                        }
                        catch
                        {
                            packet.Dispose();
                            throw;
                        }

                        string message = $"{input.Display}: part {current.Number} starts at {TimeValue.Format(packet.Timestamp)}";
                        if ((packet.Timestamp - next).Seconds >= 0.0005)
                        {
                            session.Warn($"{message}, {Seconds(packet.Timestamp - next)} s after the split point {TimeValue.Format(next)} (the next {(main.Kind == TrackKind.Video ? "keyframe" : "packet")})");
                        }
                        else
                        {
                            session.Info(message);
                        }

                        Flush();
                    }

                    Write(current, packet);
                }
                else if (mainSeen && splits.Next is { } pendingSplit && packet.Timestamp >= pendingSplit)
                {
                    // Past the next split point, but the part it belongs to starts at a keyframe not read yet.
                    held.Add(packet);
                }
                else
                {
                    Route(packet);
                }
            }

            Flush();
            foreach (Part part in open)
            {
                part.Commit();
            }

            open.Clear();
            progress.Finish();
            session.Info($"{input.Display}: {written.Count} part{(written.Count == 1 ? string.Empty : "s")}");
            return written;
        }
        finally
        {
            foreach (EncodedPacket h in held)
            {
                h.Dispose();
            }

            foreach (Part part in open)
            {
                part.Dispose();
            }
        }
    }
}
