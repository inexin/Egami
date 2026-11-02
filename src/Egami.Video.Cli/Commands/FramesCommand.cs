using System.CommandLine;
using System.Globalization;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Video.Cli.Host;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video frames</c> (Build Plan task 9): writes decoded frames as images through Egami, every frame or those
/// <c>--every</c>, <c>--at</c>, <c>--keyframes</c>, <c>--count</c> and <c>--from</c>/<c>--to</c> pick. Motion JPEG frames
/// written as JPEG are the packets themselves, not encoded again. Like <c>ffmpeg -i in.mkv -vf fps=1 shots/%04d.png</c>.
/// </summary>
internal static class FramesCommand
{
    public static Command Create(VideoSession session)
    {
        MediaOptions media = new();
        Option<string?> output = new("--output", "-o")
        {
            Description = "Where to write: a template with {frame} (the frame number from 0, padded as in {frame:0000}), {name}, {dir}, {index} and {format}, a folder, or - for standard output (one frame, with --format).",
            HelpName = "template",
        };
        Option<string?> format = new("--format", "-f") { Description = "The image format by name or extension (png, jpg, webp, ...), when -o's extension does not say; always for -o -.", HelpName = "format" };
        Option<int?> quality = new("--quality") { Description = "Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL).", HelpName = "0-100" };
        Option<string[]> set = new("--set")
        {
            Description = "Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.",
            HelpName = "fmt.key=value",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<string?> every = new("--every") { Description = "One frame per length of time (10s, 1:00), or every nth frame with #n (#25).", HelpName = "time|#n" };
        Option<string?> at = new("--at") { Description = "Only the frames shown at these times, separated by commas (5, 1:30, #100).", HelpName = "t1,t2,..." };
        Option<bool> keyframes = new("--keyframes") { Description = "Only the keyframes." };
        Option<int?> count = new("--count") { Description = "This many frames, evenly spaced from the first frame to the last.", HelpName = "n" };
        Option<string?> from = new("--from") { Description = "Start at this time, or at a frame number with #n.", HelpName = "time" };
        Option<string?> to = new("--to") { Description = "Stop before this time, or before a frame number with #n.", HelpName = "time" };
        Option<bool> autoOrient = new("--auto-orient") { Description = "Turn the frames upright by the track's rotation, as players show them." };
        Command command = new("frames", "Write video frames as images: every frame, one every few seconds, at given times, keyframes or a few evenly spaced.");
        media.AddTo(command, write: false, tracks: false);
        command.Options.Add(output);
        command.Options.Add(format);
        command.Options.Add(quality);
        command.Options.Add(set);
        command.Options.Add(media.Track);
        command.Options.Add(every);
        command.Options.Add(at);
        command.Options.Add(keyframes);
        command.Options.Add(count);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(autoOrient);
        Deinterlacing.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = media.Batch(session, result);
            Pick pick = Pick.Parse(result.GetValue(every), result.GetValue(at), result.GetValue(keyframes), result.GetValue(count), result.GetValue(from), result.GetValue(to));
            List<InputFile> inputs = media.ExpandInputs(session, result);
            string text = result.GetValue(output)
                ?? throw new UsageException("No output: give -o with a template such as shots/{name}-{frame:0000}.png, a folder, or - with --format for one frame on standard output.");
            OutputTemplate template = MediaOptions.CheckTemplate(session, text, inputs, "shots/{name}-{frame:0000}.png");
            bool folder = !template.IsStdout && (text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text))));
            bool namesFrames = folder || text.Contains("{frame", StringComparison.OrdinalIgnoreCase);
            if (!namesFrames && !pick.IsOneFrame)
            {
                throw new UsageException(template.IsStdout
                    ? "-o - writes one image: pick one frame with --at (such as --at 5) or --count 1."
                    : $"The output '{text}' names one file; put {{frame}} in it, such as shots/{{name}}-{{frame:0000}}.png, or pick one frame with --at or --count 1.");
            }

            string? formatName = result.GetValue(format);
            FormatInfo image = formatName is not null
                ? Runner.FindFormat(session.Images, formatName) ?? throw new UsageException($"'{formatName}' is not an image format egami knows; 'egami formats' lists them.")
                : template.IsStdout ? throw new UsageException("-o - writes to standard output: give --format too, such as --format png.")
                : folder ? Runner.FindFormat(session.Images, "png")!
                : Runner.FindFormat(session.Images, Path.GetExtension(text)) ?? throw new UsageException($"The output '{text}' has no extension egami writes; give --format, such as --format png.");
            if (image.Writer is null)
            {
                throw new UsageException($"egami can read {image.Name} but not write it; 'egami formats' lists what it writes.");
            }

            int? lossy = result.GetValue(quality);
            if (lossy is < 0 or > 100)
            {
                throw new UsageException("--quality must be from 0 to 100.");
            }

            Dictionary<string, string> common = new(StringComparer.Ordinal);
            if (lossy is { } q)
            {
                common["quality"] = q.ToString(CultureInfo.InvariantCulture);
            }

            Dictionary<string, string> settings = ImageOptions.SetValues(result.GetValue(set));
            WriterOptions writerOptions = Runner.Options(session.Images, new SaveSpec(null, formatName, settings, common), image);
            Filters.DeinterlacerOptions? deinterlace = Deinterlacing.Parse(result);
            Job job = new(session, pick, template, folder, image, writerOptions, result.GetValue(autoOrient), TrackSelection.Parse(result.GetValue(media.Track), false, false), inputs.Count)
            {
                Deinterlace = deinterlace,
                CopyJpeg = image == JpegFormat.Info && lossy is null && settings.Count == 0 && deinterlace is null,
            };
            return Batch.Run(session, inputs, batch, job.Run);
        });
        return command;
    }

    /// <summary>Which frames to write.</summary>
    private sealed record Pick(TimeValue? Every, IReadOnlyList<TimeValue> At, bool Keyframes, int? Count, TimeValue? From, TimeValue? To)
    {
        /// <summary>Gets a value indicating whether exactly one frame is picked, so a plain file name or standard output will do.</summary>
        public bool IsOneFrame => At.Count == 1 || Count == 1;

        /// <summary>Gets a value indicating whether the frames are picked by name (times, keyframes, a count), so a file can seek to them.</summary>
        public bool HasTargets => At.Count > 0 || Keyframes || Count is not null;

        public static Pick Parse(string? every, string? at, bool keyframes, int? count, string? from, string? to)
        {
            int ways = (every is null ? 0 : 1) + (at is null ? 0 : 1) + (keyframes ? 1 : 0) + (count is null ? 0 : 1);
            if (ways > 1)
            {
                throw new UsageException("Pick frames one way: --every, --at, --keyframes or --count.");
            }

            if (count is < 1)
            {
                throw new UsageException("--count must be 1 or more.");
            }

            List<TimeValue> times = [.. (at ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => TimeValue.Parse(t, "--at"))];
            if (at is not null && times.Count == 0)
            {
                throw new UsageException("--at needs one or more times, such as --at 5,1:30,#100.");
            }

            if (times.Count > 0 && (from is not null || to is not null))
            {
                throw new UsageException("--at picks its own frames; leave out --from and --to.");
            }

            return new Pick(
                every is null ? null : TimeValue.ParseDuration(every, "--every"),
                times,
                keyframes,
                count,
                from is null ? null : TimeValue.Parse(from, "--from"),
                to is null ? null : TimeValue.Parse(to, "--to"));
        }
    }

    /// <summary>The frames of one input, written as images.</summary>
    private sealed class Job(VideoSession session, Pick pick, OutputTemplate template, bool folder, FormatInfo image, WriterOptions writerOptions, bool autoOrient, TrackSelection tracks, int inputCount)
    {
        /// <summary>Gets a value indicating whether Motion JPEG packets are written as they are.</summary>
        public bool CopyJpeg { get; init; }

        /// <summary>Gets how the frames are deinterlaced, or <see langword="null"/> to take them as they are.</summary>
        public Filters.DeinterlacerOptions? Deinterlace { get; init; }

        public List<string> Run(InputFile input)
        {
            Writer writer = new(session, input, template, folder, image, writerOptions, autoOrient);
            try
            {
                return Frames(input, writer);
            }
            catch
            {
                writer.Abandon();
                throw;
            }
        }

        private List<string> Frames(InputFile input, Writer writer)
        {
            Stream? replay = null;
            if (CopyJpeg && CopyPackets(input, writer, out replay))
            {
                return Finish(input, writer);
            }

            using FrameSource source = FrameSource.Open(session, input, tracks, source: replay);
            writer.Orientation = source.Track.Orientation;
            JobProgress progress = new(session.ProgressSink?.For(input, input.Index, inputCount), source.Info.Duration, [], session.Cancellation);
            if (pick.HasTargets && !input.IsStdin && source.Reader.CanSeek)
            {
                Seek(input, source, writer, progress);
            }
            else
            {
                if (pick.Keyframes || pick.Count is not null)
                {
                    throw new UsageException($"{input.Display}: {(pick.Keyframes ? "--keyframes" : "--count")} needs a file it can seek in; standard input is read once.");
                }

                Scan? scan = null;
                long index = 0;
                if (pick.From is { } start && !input.IsStdin && source.Reader.CanSeek && (start.IsFrame ? start.Frame > 0 : start.Seconds > 0))
                {
                    // Seek to --from instead of decoding up to it; frame numbers still count from the start.
                    scan = Scan.Read(session, input, source.Track.Number);
                    source.Reader.Seek(start.Resolve(() => scan.Times, input.Display, "--from"));
                    index = -1;
                }

                Picker<YuvVideoFrame> picker = new(session, input, pick, source.Info.Duration, (frame, number) => writer.Frame(frame, number));
                foreach (YuvVideoFrame frame in source.Frames(Deinterlace, session.Context))
                {
                    if (index < 0)
                    {
                        index = scan!.IndexOf(frame.Timestamp);
                    }

                    progress.Frame(frame.Timestamp);
                    if (!picker.Offer(frame, index++, frame.Timestamp, keyframe: false))
                    {
                        break;
                    }
                }

                picker.End();
            }

            progress.Finish();
            return Finish(input, writer);
        }

        private List<string> Finish(InputFile input, Writer writer)
        {
            writer.Drain();
            if (writer.Written.Count == 0)
            {
                session.Warn($"{input.Display}: no frames picked; nothing written.");
            }
            else
            {
                session.Info($"{input.Display}: {writer.Written.Count} frames");
            }

            return writer.Written;
        }

        /// <summary>The frames a file names (times, keyframes, a count), each found by seeking.</summary>
        private void Seek(InputFile input, FrameSource source, Writer writer, JobProgress progress)
        {
            Scan scan = Scan.Read(session, input, source.Track.Number);
            MediaTime? duration = source.Info.Duration is { Ticks: > 0 } d ? d : null;
            List<(MediaTime Time, long? Index, string Text)> targets = [];
            if (pick.At.Count > 0)
            {
                foreach (TimeValue at in pick.At)
                {
                    MediaTime time = at.Resolve(() => scan.Times, input.Display, "--at");
                    if (!at.IsFrame && duration is { } end && time >= end)
                    {
                        session.Warn($"{input.Display}: --at {at.Text} is past the end ({TimeValue.Format(end)}); skipped.");
                        continue;
                    }

                    targets.Add((time, at.Frame, at.Text));
                }
            }
            else
            {
                (long first, long last) = Range(input, scan);
                IEnumerable<long> indexes = pick.Keyframes
                    ? scan.Keyframes.Select(scan.IndexOf).Where(i => i >= first && i < last)
                    : Spread(first, last, pick.Count!.Value);
                targets.AddRange(indexes.Distinct().Select(i => (scan.Times[(int)i], (long?)i, $"#{i}")));
            }

            HashSet<long> done = [];
            foreach ((MediaTime time, long? number, string text) in targets.OrderBy(t => t.Time))
            {
                progress.Frame(time);
                source.Reader.Seek(time);
                using YuvVideoFrame? frame = source.ReadFrame(Deinterlace, session.Context);
                if (frame is null)
                {
                    session.Warn($"{input.Display}: no frame at {text}; skipped.");
                    continue;
                }

                long index = number ?? scan.IndexOf(frame.Timestamp);
                if (done.Add(index))
                {
                    writer.Frame(frame, index);
                }
            }
        }

        /// <summary>The frame numbers <c>--from</c> and <c>--to</c> keep: from the first, up to before the last.</summary>
        private (long First, long Last) Range(InputFile input, Scan scan)
        {
            long first = pick.From is { } from ? (from.IsFrame ? from.Frame!.Value : scan.FirstAtOrAfter(from.Seconds)) : 0;
            long last = pick.To is { } to ? (to.IsFrame ? to.Frame!.Value : scan.FirstAtOrAfter(to.Seconds)) : scan.Times.Count;
            last = Math.Min(last, scan.Times.Count);
            return first < last
                ? (first, last)
                : throw new UsageException($"{input.Display}: --from and --to leave no frames; the video has {scan.Times.Count} frames.");
        }

        /// <summary><paramref name="count"/> frame numbers evenly spaced from <paramref name="first"/> to the frame before <paramref name="last"/>.</summary>
        private static IEnumerable<long> Spread(long first, long last, int count)
        {
            long n = Math.Min(count, last - first);
            return n <= 1
                ? [first]
                : Enumerable.Range(0, (int)n).Select(i => first + (long)Math.Round(i * (last - first - 1) / (double)(n - 1), MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Writes a Motion JPEG track's packets as JPEG files without decoding them. Returns <see langword="false"/> when the
        /// track is another codec (or must be turned upright), with <paramref name="replay"/> set to standard input read again.
        /// </summary>
        private bool CopyPackets(InputFile input, Writer writer, out Stream? replay)
        {
            replay = null;
            Stream stream = MediaIO.OpenStream(session, input);
            FrameReplayStream? recorder = input.IsStdin ? new FrameReplayStream(stream) : null;
            Stream opened = recorder ?? stream;
            bool copied = false;
            try
            {
                using IDemuxer demuxer = MediaFile.OpenDemuxer(opened, session.Context, leaveOpen: true);
                VideoTrackInfo video = (VideoTrackInfo)tracks.SelectOne(demuxer.Tracks, TrackKind.Video, input.Display);
                bool upright = !autoOrient || video.Orientation is ImageOrientation.Normal or ImageOrientation.Unknown;
                if (video.CodecId != CodecIds.MotionJpeg || !upright)
                {
                    replay = recorder?.Replay();
                    return false;
                }

                copied = true;
                List<long>? spread = null;
                if (pick.Count is { } count)
                {
                    if (input.IsStdin)
                    {
                        throw new UsageException($"{input.Display}: --count needs a file it can seek in; standard input is read once.");
                    }

                    (long first, long last) = Range(input, Scan.Read(session, input, video.Number));
                    spread = [.. Spread(first, last, count)];
                }

                JobProgress progress = new(session.ProgressSink?.For(input, input.Index, inputCount), demuxer.Info.Duration, [], session.Cancellation);
                using IVideoDecoder decoder = VideoCodecs.CreateDecoder(video, null, session.Context);
                Pick packets = spread is null ? pick : pick with { Count = null, From = null, To = null, At = [.. spread.Select(i => new TimeValue(0, i, $"#{i}"))] };
                Picker<EncodedPacket> picker = new(session, input, packets, demuxer.Info.Duration, (packet, index) => writer.Packet(packet, index, decoder));
                long number = 0;
                while (demuxer.ReadPacket() is { } packet)
                {
                    if (packet.TrackNumber != video.Number || packet.IsDiscardable)
                    {
                        packet.Dispose();
                        continue;
                    }

                    progress.Frame(packet.Timestamp);
                    if (!picker.Offer(packet, number++, packet.Timestamp, packet.IsKeyframe))
                    {
                        break;
                    }
                }

                picker.End();
                progress.Finish();
                return true;
            }
            finally
            {
                // Standard input read again for decoding keeps the stream; otherwise it is done with.
                if (replay is null || copied)
                {
                    opened.Dispose();
                }
            }
        }
    }

    /// <summary>A video track's frame times in presentation order and its keyframes' times, read from the packets once.</summary>
    private sealed class Scan
    {
        public List<MediaTime> Times { get; } = [];

        public List<MediaTime> Keyframes { get; } = [];

        public static Scan Read(VideoSession session, InputFile input, int track)
        {
            Scan scan = new();
            using IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    if (packet.TrackNumber == track && !packet.IsDiscardable)
                    {
                        scan.Times.Add(packet.Timestamp);
                        if (packet.IsKeyframe)
                        {
                            scan.Keyframes.Add(packet.Timestamp);
                        }
                    }
                }
            }

            scan.Times.Sort();
            scan.Keyframes.Sort();
            return scan;
        }

        /// <summary>The number of the frame shown at <paramref name="time"/>: the last one starting at or before it.</summary>
        public long IndexOf(MediaTime time)
        {
            int at = Times.BinarySearch(time);
            return at >= 0 ? at : Math.Max(0, ~at - 1);
        }

        /// <summary>The number of the first frame starting at or after <paramref name="seconds"/>.</summary>
        public long FirstAtOrAfter(double seconds)
        {
            int at = Times.BinarySearch(MediaTime.FromSeconds(seconds));
            return at >= 0 ? at : ~at;
        }
    }

    /// <summary>
    /// Picks frames as they come, in presentation order: every frame, one per interval, every nth, keyframes, or the frames
    /// shown at given times (holding the last frame until the next one shows the time has passed). Takes ownership of each item.
    /// </summary>
    private sealed class Picker<T>(VideoSession session, InputFile input, Pick pick, MediaTime? duration, Action<T, long> write)
        where T : class, IDisposable
    {
        private readonly Queue<TimeValue> _times = new(pick.At.Where(t => !t.IsFrame).OrderBy(t => t.Seconds));
        private readonly HashSet<long> _frames = [.. pick.At.Where(t => t.IsFrame).Select(t => t.Frame!.Value)];
        private readonly HashSet<long> _written = [];
        private T? _held;
        private long _heldIndex;
        private long _first = -1;
        private double _next;

        /// <summary>Offers the next frame; returns <see langword="false"/> once no later frame can be picked.</summary>
        public bool Offer(T item, long index, MediaTime time, bool keyframe)
        {
            if (pick.At.Count > 0)
            {
                while (_times.Count > 0 && time.Seconds > _times.Peek().Seconds)
                {
                    _times.Dequeue();
                    Emit(_held ?? item, _held is null ? index : _heldIndex);
                }

                if (_frames.Contains(index))
                {
                    Emit(item, index);
                }

                _held?.Dispose();
                _held = item;
                _heldIndex = index;
                return _times.Count > 0 || _frames.Any(f => f > index);
            }

            using (item)
            {
                if (pick.To is { } to && (to.IsFrame ? index >= to.Frame : time.Seconds >= to.Seconds))
                {
                    return false;
                }

                if (pick.From is { } from && (from.IsFrame ? index < from.Frame : time.Seconds < from.Seconds))
                {
                    return true;
                }

                if (_first < 0)
                {
                    _first = index;
                    _next = time.Seconds;
                }

                bool keep = true;
                if (pick.Keyframes)
                {
                    keep = keyframe;
                }
                else if (pick.Every is { IsFrame: true } nth)
                {
                    keep = (index - _first) % nth.Frame!.Value == 0;
                }
                else if (pick.Every is { } every)
                {
                    // One frame per interval, on a grid from the first frame, so the spacing does not drift.
                    keep = time.Seconds >= _next - 1e-9;
                    while (_next <= time.Seconds + 1e-9)
                    {
                        _next += every.Seconds;
                    }
                }

                if (keep)
                {
                    Emit(item, index);
                }

                return true;
            }
        }

        /// <summary>Writes the frames still owed for times after the last frame, and drops the held frame.</summary>
        public void End()
        {
            while (_times.TryDequeue(out TimeValue at))
            {
                if (_held is null || (duration is { Ticks: > 0 } end && at.Seconds >= end.Seconds))
                {
                    session.Warn($"{input.Display}: --at {at.Text} is past the end{(duration is { Ticks: > 0 } d ? $" ({TimeValue.Format(d)})" : string.Empty)}; skipped.");
                    continue;
                }

                Emit(_held, _heldIndex);
            }

            foreach (long frame in _frames.Where(f => !_written.Contains(f)).Order())
            {
                session.Warn($"{input.Display}: --at #{frame} is past the last frame; skipped.");
            }

            _held?.Dispose();
            _held = null;
        }

        private void Emit(T item, long index)
        {
            if (_written.Add(index))
            {
                write(item, index);
            }
        }
    }

    /// <summary>Writes picked frames as image files (or to standard output) and keeps their paths.</summary>
    private sealed class Writer(VideoSession session, InputFile input, OutputTemplate template, bool folder, FormatInfo image, WriterOptions options, bool autoOrient)
    {
        // Images are encoded on other threads while the next frames decode: a few at a time per processor, so memory stays bounded.
        private readonly List<Task> _pending = [];
        private readonly int _parallel = template.IsStdout ? 1 : Math.Max(1, Environment.ProcessorCount);

        public List<string> Written { get; } = [];

        public ImageOrientation Orientation { get; set; } = ImageOrientation.Normal;

        /// <summary>Waits for the images still being written; a failed one throws here.</summary>
        public void Drain()
        {
            try
            {
                Task.WaitAll([.. _pending]);
            }
            catch (AggregateException error) when (error.InnerExceptions.Count > 0)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
            }
            finally
            {
                _pending.Clear();
            }
        }

        /// <summary>Waits for the images still being written after the job failed or was stopped, keeping the first error.</summary>
        public void Abandon()
        {
            try
            {
                Task.WaitAll([.. _pending]);
            }
            catch (AggregateException)
            {
            }

            _pending.Clear();
        }

        /// <summary>Writes a decoded frame; PQ and HLG frames are tone mapped to sRGB, as 8-bit images need.</summary>
        public void Frame(YuvVideoFrame frame, long index)
        {
            Raster<Rgba8> picture = frame.Color?.TransferCharacteristics is 16 or 18
                ? frame.ToRaster<Rgba8>(ColorProfile.Srgb)
                : FrameSource.ToImage(session, frame);
            string path;
            try
            {
                path = PathOf(index);
                Written.Add(path);
            }
            catch
            {
                picture.Dispose();
                throw;
            }

            if (_pending.Count >= _parallel)
            {
                Task done = _pending[Task.WaitAny([.. _pending])];
                _pending.Remove(done);
                done.GetAwaiter().GetResult();
            }

            void Write()
            {
                using (picture)
                {
                    if (autoOrient && Orientation is not (ImageOrientation.Normal or ImageOrientation.Unknown))
                    {
                        // Egami's AutoOrient turns the picture by its EXIF orientation, set here from the track's rotation.
                        picture.Metadata.Exif ??= new ExifData();
                        picture.Metadata.Exif.Orientation = Orientation;
                        picture.Apply(x => x.AutoOrient());
                    }

                    Output.Write(session, path, stream => picture.Save(stream, image, options));
                }
            }

            if (_parallel == 1)
            {
                Write();
            }
            else
            {
                _pending.Add(Task.Run(Write));
            }
        }

        /// <summary>Writes a Motion JPEG packet as its JPEG file, or decodes it when it is not one complete image.</summary>
        public void Packet(EncodedPacket packet, long index, IVideoDecoder decoder)
        {
            if (FramePackets.Jpeg(packet.Data.Span) is { } jpeg)
            {
                Save(index, stream => stream.Write(jpeg));
                return;
            }

            decoder.Send(packet);
            decoder.Flush();
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    Frame(frame, index);
                }
            }
        }

        private void Save(long index, Action<Stream> write)
        {
            string path = PathOf(index);
            Output.Write(session, path, write);
            Written.Add(path);
        }

        private string PathOf(long index)
        {
            if (template.IsStdout)
            {
                return "-";
            }

            string extension = image.Extensions[0].TrimStart('.');
            string path = folder
                ? Path.Combine(session.ResolvePath(template.Text), string.Create(CultureInfo.InvariantCulture, $"{input.Name}-{index:0000}.{extension}"))
                : session.ResolvePath(template.Resolve(input, extension, checked((int)index)));
            return !session.InPlace && input.Path is { } source && Batch.SamePath(path, source)
                ? throw new UsageException($"{input.Display}: the output would replace the input; give another -o.")
                : path;
        }
    }
}
