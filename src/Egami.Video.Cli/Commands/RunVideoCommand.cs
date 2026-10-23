using System.CommandLine;
using System.Globalization;

using Egami.Cli.Commands;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Pixels;
using Egami.Video.Cli.Host;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video run</c> (Build Plan task 12, Video CLI design Q7): egami run's steps, written the same way and run by the same
/// step engine, over every decoded frame of a video, then encoded again. <c>save</c> names the video output; audio and subtitle
/// tracks are copied as they are, and every frame keeps its timestamp.
/// </summary>
internal static class RunVideoCommand
{
    /// <summary>The options of egami-video run that egami run does not have: name → takes a value.</summary>
    private static readonly Dictionary<string, bool> OwnOptions = new(StringComparer.Ordinal)
    {
        ["--codec"] = true,
        ["--rate"] = true,
        ["--crop"] = true,
        ["--track"] = true,
        ["-t"] = true,
        ["--no-audio"] = false,
        ["-an"] = false,
        ["--no-subtitles"] = false,
        ["-sn"] = false,
        ["--progress"] = true,
        ["--progress-format"] = true,
    };

    /// <summary>The command, for help; the tool parses run's arguments itself, since steps carry their own options.</summary>
    public static Command Create(VideoSession session)
    {
        Command command = new(
            "run",
            "Run egami's steps over every frame of a video, separated by +, and encode it again: "
            + "'egami-video run in.mkv crop 0 60 1920 960 + resize 1280x + save out.mkv'. 'egami-video run --list' lists the steps; "
            + "audio and subtitles are copied, and frames keep their times.")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        command.Arguments.Add(new Argument<string[]>("inputs-and-steps") { Description = "Videos (files, globs, -), then steps separated by +, ending in save out.mkv if -o is not given.", Arity = ArgumentArity.ZeroOrMore });
        command.Options.Add(new Option<string?>("--output", "-o") { Description = "Where the video goes when save has no path: a file, a folder, a template with {name}, or - with --format.", HelpName = "path" });
        command.Options.Add(new Option<string?>("--format", "-f") { Description = "The output container (mkv, avi …), when the output's extension does not say.", HelpName = "container" });
        command.Options.Add(new Option<string?>("--codec") { Description = "The video codec to encode with: mjpeg, vp8, vp9 or raw. The input's codec when Egami.Video can encode it, else mjpeg.", HelpName = "codec" });
        command.Options.Add(new Option<int?>("--quality") { Description = "The encoder's quality from 1 to 100.", HelpName = "1-100" });
        command.Options.Add(new Option<string?>("--crop") { Description = "auto finds the black bars of letterbox, pillarbox and windowbox video, as the analyze command's crop detector does, and crops them off before the other steps.", HelpName = "auto" });
        command.Options.Add(new Option<string?>("--rate") { Description = "Frames per second of the output, dropping or repeating frames to match; frames keep their own times by default.", HelpName = "fps" });
        command.Options.Add(new Option<string?>("--track", "-t") { Description = "Which video track to run the steps over: v:1 for the second, or a track number; audio and subtitle tracks are copied.", HelpName = "track" });
        command.Options.Add(new Option<bool>("--no-audio", "-an") { Description = "Leave out the audio tracks." });
        command.Options.Add(new Option<bool>("--no-subtitles", "-sn") { Description = "Leave out the subtitle tracks." });
        command.Options.Add(new Option<string?>("--workflow", "-w") { Description = "Take the steps from an egami JSON workflow file.", HelpName = "file" });
        command.Options.Add(new Option<string?>("--save-workflow") { Description = "Write the steps to a JSON workflow file (then run them, unless --dry-run).", HelpName = "file" });
        command.Options.Add(new Option<bool>("--dry-run") { Description = "Print the steps and the file each input would write, and stop." });
        command.Options.Add(new Option<bool>("--list") { Description = "List every step with its arguments; --list <step> shows one." });
        command.Options.Add(new Option<bool>("--recursive", "-r") { Description = "Take every media file in folders given as inputs." });
        command.Options.Add(new Option<int>("--jobs", "-j") { Description = "How many files to work on at once; 0 for one per processor.", HelpName = "count" });
        command.Options.Add(new Option<bool>("--in-place") { Description = "Allow outputs to replace their inputs." });
        command.Options.Add(new Option<bool>("--fail-fast") { Description = "Stop at the first file that fails." });
        command.SetAction(_ => ExitCodes.Usage);
        _ = session;
        return command;
    }

    /// <summary>Runs <c>run</c> from its raw arguments (everything after the word run).</summary>
    public static int Execute(VideoSession session, string[] args)
    {
        if (args is ["--list", ..])
        {
            ListSteps(session, args.Length > 1 ? args[1] : null);
            return ExitCodes.Success;
        }

        Dictionary<string, List<string>> own = TakeOwnOptions(args, out string[] rest);
        RunCommand.Parsed parsed;
        try
        {
            parsed = RunCommand.Parse(rest);
        }
        catch (UsageException error)
        {
            throw new UsageException(error.Message.Replace("egami run", "egami-video run", StringComparison.Ordinal));
        }

        Verbosity verbosity = parsed.Flags.Contains("-q") ? Verbosity.Quiet : parsed.Flags.Contains("-vv") ? Verbosity.Debug : parsed.Flags.Contains("-v") ? Verbosity.Verbose : Verbosity.Normal;
        session.Configure(verbosity, parsed.Value("--limit"));
        session.ProgressSink ??= ProgressSink.Create(session, Last(own, "--progress"), Last(own, "--progress-format"));
        session.InPlace = parsed.Flags.Contains("--in-place");
        if (parsed.Value("--to") is not null)
        {
            throw new UsageException("run takes no --to: frames are decoded to sRGB; use a convert-profile step to change their colors.");
        }

        bool autoCrop = Last(own, "--crop") switch
        {
            null => false,
            "auto" => true,
            string other => throw new UsageException($"--crop '{other}' is not auto; to crop by numbers, use the crop step: crop x y width height."),
        };
        List<PipelineItem> items = Items(session, parsed, autoCrop);
        List<BoundStep> steps = [];
        SaveSpec? save = null;
        foreach (PipelineItem item in items)
        {
            switch (item)
            {
                case StepItem step when save is null:
                    steps.Add(step.Step);
                    break;
                case SaveItem saveItem when save is null:
                    save = saveItem.Save;
                    break;
                case EditItem edit:
                    throw new UsageException($"{edit.Name} changes an image's metadata; run writes video, which has none of its own.");
                default:
                    throw new UsageException("run writes one video: give one save, at the end of the steps, such as '+ save out.mkv'.");
            }
        }

        save = WithDefaults(save ?? SaveSpec.Default, parsed);
        int? quality = Quality(session, save);
        if (parsed.Value("--save-workflow") is { } workflowPath)
        {
            Workflow.Save(session.ResolvePath(workflowPath), items);
            session.Info($"wrote {workflowPath}");
        }

        int jobs = parsed.Value("--jobs") is { } jobsText ? ValueParsers.ParseInt(jobsText) : 0;
        if (jobs < 0)
        {
            throw new UsageException("--jobs must be 0 or more.");
        }

        if (parsed.Inputs.Count == 0)
        {
            if (parsed.Value("--save-workflow") is not null)
            {
                return ExitCodes.Success;
            }

            throw new UsageException("No inputs: 'egami-video run in.mkv resize 1280x + save out.mkv'.");
        }

        List<InputFile> inputs = Globber.Expand(session, parsed.Inputs, parsed.Flags.Contains("-r"));
        string outputText = save.Path ?? parsed.Value("-o")
            ?? throw new UsageException("No output: end the steps with '+ save out.mkv', or give -o with a file, a folder or a template such as out/{name}.mkv.");
        OutputTemplate output = MediaOptions.CheckTemplate(session, outputText, inputs, "out/{name}.mkv");
        RunJob job = new(
            steps,
            Last(own, "--codec") is { } codec ? FindCodec(session, codec) : null,
            Last(own, "--rate") is { } rate ? PictureFrames.ParseRate(rate, "--rate") : null,
            quality,
            autoCrop,
            TrackSelection.Parse(own.GetValueOrDefault("--track"), own.ContainsKey("--no-audio"), own.ContainsKey("--no-subtitles")));

        if (parsed.Flags.Contains("--dry-run"))
        {
            TextWriter text = session.Console.Text;
            foreach (BoundStep step in steps)
            {
                text.WriteLine(step.ToString());
            }

            foreach (InputFile input in inputs)
            {
                MediaTarget target = MediaIO.Resolve(session, input, output, save.Format, "mkv");
                text.WriteLine($"{input.Display} -> {(target.IsStdout ? "standard output" : Path.GetRelativePath(session.WorkingDirectory, target.Path).Replace('\\', '/'))} ({target.Container.Name})");
            }

            return ExitCodes.Success;
        }

        BatchOptions batch = new(jobs, parsed.Flags.Contains("--fail-fast"), session.InPlace);
        return Batch.Run(session, inputs, batch, input =>
        {
            MediaTarget target = MediaIO.Resolve(session, input, output, save.Format, "mkv");
            job.Run(session, input, target, inputs.Count);
            return [target.Path];
        });
    }

    /// <summary>The steps from the command line or a workflow file.</summary>
    private static List<PipelineItem> Items(VideoSession session, RunCommand.Parsed parsed, bool autoCrop)
    {
        List<PipelineItem> items;
        if (parsed.Value("--workflow") is { } workflowPath)
        {
            if (parsed.Steps.Count > 0)
            {
                throw new UsageException("Give the steps either on the command line or with --workflow, not both.");
            }

            items = Workflow.Load(session.ResolvePath(workflowPath));
        }
        else
        {
            items = [.. parsed.Steps.Select(RunCommand.Bind)];
        }

        return items.Count > 0 || autoCrop
            ? items
            : throw new UsageException("No steps: 'egami-video run in.mkv resize 1280x + save out.mkv', or --workflow file.json. 'egami-video run --list' lists the steps.");
    }

    /// <summary>The save with the writer options given outside it (<c>--format</c>, <c>--quality</c> …) where it does not set them.</summary>
    private static SaveSpec WithDefaults(SaveSpec save, RunCommand.Parsed parsed)
    {
        if (parsed.SaveDefaults.Count == 0)
        {
            return save;
        }

        RunCommand.ParsedStep defaults = new("save");
        defaults.Named.AddRange(parsed.SaveDefaults);
        SaveSpec extra = RunCommand.SaveSpecOf(defaults);
        Dictionary<string, string> common = new(extra.Common, StringComparer.Ordinal);
        foreach ((string key, string value) in save.Common)
        {
            common[key] = value;
        }

        Dictionary<string, string> settings = new(extra.Settings, StringComparer.Ordinal);
        foreach ((string key, string value) in save.Settings)
        {
            settings[key] = value;
        }

        return save with { Format = save.Format ?? extra.Format, Common = common, Settings = settings };
    }

    /// <summary>The encoder's quality from <c>--quality</c>; the image writers' other settings mean nothing to a video encoder.</summary>
    private static int? Quality(VideoSession session, SaveSpec save)
    {
        int? quality = null;
        foreach ((string key, string value) in save.Common)
        {
            if (key == "quality")
            {
                quality = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int q) && q is >= 1 and <= 100
                    ? q
                    : throw new UsageException($"--quality '{value}' must be a whole number from 1 to 100.");
            }
            else
            {
                session.Warn($"--{(key == "strip-metadata" ? "strip" : key)} is for image formats; the video encoder ignores it.");
            }
        }

        foreach (string key in save.Settings.Keys)
        {
            session.Warn($"--set {key} is for image formats; the video encoder ignores it.");
        }

        return quality;
    }

    /// <summary>Takes egami-video's own options out of the arguments, so egami's run parser sees only its own.</summary>
    private static Dictionary<string, List<string>> TakeOwnOptions(string[] args, out string[] rest)
    {
        Dictionary<string, List<string>> own = new(StringComparer.Ordinal);
        List<string> left = [];
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            int equals = token.IndexOf('=', StringComparison.Ordinal);
            string name = token.StartsWith("--", StringComparison.Ordinal) && equals > 0 ? token[..equals] : token;
            if (!OwnOptions.TryGetValue(name, out bool takesValue))
            {
                left.Add(token);
                continue;
            }

            string canonical = name switch
            {
                "-t" => "--track",
                "-an" => "--no-audio",
                "-sn" => "--no-subtitles",
                _ => name,
            };
            string value = "true";
            if (takesValue)
            {
                value = equals > 0 && name != token ? token[(equals + 1)..]
                    : i + 1 < args.Length && args[i + 1] != "+" ? args[++i]
                    : throw new UsageException($"{name} needs a value.");
            }

            if (!own.TryGetValue(canonical, out List<string>? values))
            {
                own[canonical] = values = [];
            }

            values.Add(value);
        }

        rest = [.. left];
        return own;
    }

    private static string? Last(Dictionary<string, List<string>> own, string name) => own.TryGetValue(name, out List<string>? values) ? values[^1] : null;

    /// <summary>Finds a video codec by name; it must be one Egami.Video can encode.</summary>
    private static VideoCodec FindCodec(VideoSession session, string name)
    {
        VideoCodec? codec = name.ToLowerInvariant() switch
        {
            "mjpeg" or "motion-jpeg" or "motionjpeg" or "jpeg" => MotionJpegCodec.Instance,
            "raw" or "rawvideo" or "uncompressed" => RawVideoCodec.Instance,
            "vp8" => Vp8Codec.Instance,
            "vp9" => Vp9Codec.Instance,
            "av1" => Av1Codec.Instance,
            _ => session.Context.Codecs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)),
        };
        return codec is null ? throw new UsageException($"--codec '{name}' is not a video codec egami-video knows; give mjpeg, vp8, vp9 or raw.")
            : codec.CanEncode ? codec
            : throw new UsageException($"egami-video cannot encode {codec.Name} yet; give --codec mjpeg, vp8, vp9 or raw.");
    }

    private static void ListSteps(VideoSession session, string? name)
    {
        if (name is not null)
        {
            if (name == "save")
            {
                session.Console.Text.WriteLine("save: Writes the video: save <path> [--format container] [--quality 1-100].");
                return;
            }

            // egami's own listing of one step: its overloads, parameters and help.
            RunCommand.Execute(session.Images, ["--list", name]);
            return;
        }

        TextWriter text = session.Console.Text;
        text.WriteLine("Steps for 'egami-video run', applied to every frame (separate them with +; 'egami-video run --list <step>' shows one):");
        foreach (StepInfo step in StepCatalog.All)
        {
            text.WriteLine($"  {step.Name,-26} {step.Summary}");
        }

        text.WriteLine($"  {"save",-26} Writes the video: save <path> [--format container] [--quality 1-100]; one save, at the end.");
    }

    /// <summary>The steps and encoder settings of a run, applied to one input at a time.</summary>
    private sealed class RunJob(List<BoundStep> allSteps, VideoCodec? codec, Rational? rate, int? quality, bool autoCrop, TrackSelection selection)
    {
        /// <summary>Decodes an input's video, runs the steps over every frame and encodes the frames into <paramref name="target"/>.</summary>
        public void Run(VideoSession session, InputFile input, MediaTarget target, int count)
        {
            using FrameSource source = FrameSource.Open(session, input, selection);
            VideoTrackInfo track = source.Track;
            List<BoundStep> steps = autoCrop && AutoCrop(session, input, track, count) is { } crop ? [crop, .. allSteps] : allSteps;
            if (target.IsStdout && target.Container is AviFormat)
            {
                throw new UsageException("AVI needs a file it can seek in; write mkv or y4m to standard output.");
            }

            VideoCodec encoder = codec ?? (session.Context.FindCodec(track) is { CanEncode: true } same ? same : MotionJpegCodec.Instance);
            using OtherPackets others = OtherPackets.Open(session, input, selection);
            VideoWriterOptions options = new()
            {
                Codec = encoder,
                FrameRate = rate ?? (track.FrameRate is { Numerator: > 0 } own ? own : new Rational(30, 1)),
                Quality = quality,

                // Uncompressed output keeps every pixel's color; other codecs take their usual subsampling.
                ChromaFormat = encoder is RawVideoCodec ? ChromaFormat.Yuv444 : null,
                OtherTracks = others.Tracks,
                Metadata = source.Info.Metadata,
            };
            session.Info($"{input.Display}: {steps.Count} steps over track {track.Number}, encoding {encoder.Name} into {target.Container.Name}");

            MediaIO.Write(session, target.Path, stream =>
            {
                CountingStream counted = new(stream);
                using VideoWriter writer = VideoWriter.Create(counted, target.Container.Name, options, session.Context, leaveOpen: true);
                JobProgress progress = new(session.ProgressSink?.For(input, input.Index, count), source.Info.Duration, [track], session.Cancellation, counted);
                FrameSteps frames = new(session, input.Display, steps);
                if (rate is { } fps)
                {
                    Resample(source, writer, frames, others, progress, fps);
                }
                else
                {
                    foreach (YuvVideoFrame frame in source.Frames())
                    {
                        using (frame)
                        {
                            progress.Frame(frame.Timestamp);
                            Write(writer, others, frames.Run(frame), frame.Timestamp);
                        }
                    }
                }

                if (frames.Count == 0)
                {
                    throw new InvalidDataException($"{input.Display}: the video has no frames.");
                }

                others.WriteUpTo(writer, double.PositiveInfinity);
                writer.Finish();
                progress.Finish();
            });
        }

        /// <summary>
        /// <c>--crop auto</c>: one pass of the crop detector over the track, then a crop step to the rectangle it found, or none when
        /// the picture has no bars.
        /// </summary>
        private static BoundStep? AutoCrop(VideoSession session, InputFile input, VideoTrackInfo track, int count)
        {
            string path = input.Path ?? throw new UsageException("--crop auto reads the video twice, so it needs a file, not standard input.");
            Analysis.CropDetector detector = new(new Analysis.CropOptions());
            Analysis.MediaAnalysis.Run(path, new Analysis.MediaAnalysisOptions { VideoDetectors = [detector], VideoTrack = track.Number },
                session.Context, session.ProgressSink?.For(input, input.Index, count), session.Cancellation);
            Analysis.CropReport report = detector.Report;
            Rect r = report.Crop;
            if (report.Frames == 0 || r.Width <= 0 || r.Height <= 0 || (r.Width == report.FrameSize.Width && r.Height == report.FrameSize.Height))
            {
                session.Info($"{input.Display}: --crop auto found no bars to crop");
                return null;
            }

            session.Info($"{input.Display}: --crop auto crops {report.FrameSize.Width}x{report.FrameSize.Height} to {r.Width}x{r.Height} at {r.X},{r.Y}");
            string[] numbers = [.. new[] { r.X, r.Y, r.Width, r.Height }.Select(n => n.ToString(CultureInfo.InvariantCulture))];
            return StepCatalog.Bind(StepCatalog.Find("crop")!, numbers, []);
        }

        /// <summary>Writes the frames shown at each step of <paramref name="fps"/>, from the first frame's time: frames are dropped or repeated.</summary>
        private static void Resample(FrameSource source, VideoWriter writer, FrameSteps frames, OtherPackets others, JobProgress progress, Rational fps)
        {
            YuvVideoFrame? held = null;
            MediaTime start = MediaTime.Zero;
            long slot = 0;
            Rational step = fps.Reciprocal();
            double sourceStep = source.Track.FrameRate is { Numerator: > 0 } r ? 1 / r.ToDouble() : step.ToDouble();
            MediaTime Slot(long k) => start + new MediaTime(k, step);
            try
            {
                foreach (YuvVideoFrame frame in source.Frames())
                {
                    progress.Frame(frame.Timestamp);
                    if (held is null)
                    {
                        held = frame;
                        start = frame.Timestamp;
                        continue;
                    }

                    WriteHeld(frame.Timestamp.Seconds);
                    held.Dispose();
                    held = frame;
                }

                if (held is not null)
                {
                    WriteHeld(held.Timestamp.Seconds + (held.Duration is { Ticks: > 0 } d ? d.Seconds : sourceStep));
                }
            }
            finally
            {
                held?.Dispose();
            }

            // The held frame is shown at every slot before the next frame's time.
            void WriteHeld(double until)
            {
                Raster<Rgba8>? image = null;
                try
                {
                    while (Slot(slot).Seconds < until - 1e-6)
                    {
                        image ??= frames.Run(held!);
                        writer.WriteFrame(image, Slot(slot));
                        others.WriteUpTo(writer, Slot(slot).Seconds);
                        slot++;
                    }
                }
                finally
                {
                    image?.Dispose();
                }
            }
        }

        private static void Write(VideoWriter writer, OtherPackets others, Raster<Rgba8> image, MediaTime timestamp)
        {
            using (image)
            {
                writer.WriteFrame(image, timestamp);
            }

            others.WriteUpTo(writer, timestamp.Seconds);
        }
    }

    /// <summary>Runs the steps over frames, checking that every frame comes out the size of the first, which the encoder took.</summary>
    private sealed class FrameSteps(VideoSession session, string display, List<BoundStep> steps)
    {
        private Size? _first;
        private List<Size>? _firstSizes;

        /// <summary>Gets how many frames have been run.</summary>
        public long Count { get; private set; }

        /// <summary>The frame as RGBA pixels with the steps run over it.</summary>
        public Raster<Rgba8> Run(YuvVideoFrame frame)
        {
            long index = Count++;
            Raster<Rgba8> image = FrameSource.ToImage(session, frame);
            try
            {
                if (_first is null && RunSteps.ChangesSize(steps))
                {
                    _firstSizes = RunSteps.SizesAfterEach(image, steps);
                }

                RunSteps.Apply(image, steps);
                Size size = new(image.Width, image.Height);
                _first ??= size;
                if (size != _first)
                {
                    throw Mismatch(frame, index, size);
                }

                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private InvalidDataException Mismatch(YuvVideoFrame frame, long index, Size size)
        {
            Size first = _first!.Value;
            string where = $"{display}: frame #{index} ({TimeValue.Format(frame.Timestamp)}) comes out {size.Width}x{size.Height}, but the first frame came out {first.Width}x{first.Height}, and the encoder needs one size";
            if (_firstSizes is { } firstSizes)
            {
                using Raster<Rgba8> again = FrameSource.ToImage(session, frame);
                List<Size> sizes = RunSteps.SizesAfterEach(again, steps);
                for (int i = 0; i < steps.Count; i++)
                {
                    if (sizes[i] != firstSizes[i])
                    {
                        return new InvalidDataException($"{where}: '{steps[i]}' gives frames of different sizes. Give it a fixed size, or add a step such as 'resize {first.Width}x{first.Height}!' after it.");
                    }
                }
            }

            return new InvalidDataException($"{where}: the video itself changes size there. Add a step such as 'resize {first.Width}x{first.Height}!'.");
        }
    }

    /// <summary>
    /// The audio and subtitle packets of a file, read through a second demuxer and written between the frames in time order,
    /// unchanged. Standard input is read only once, by the decoder, so only its video is written.
    /// </summary>
    private sealed class OtherPackets : IDisposable
    {
        private readonly HashSet<int> _numbers;
        private IDemuxer? _demuxer;
        private EncodedPacket? _next;

        private OtherPackets(IDemuxer? demuxer, IReadOnlyList<TrackInfo> tracks)
        {
            _demuxer = demuxer;
            Tracks = tracks;
            _numbers = [.. tracks.Select(t => t.Number)];
        }

        /// <summary>Gets the tracks copied.</summary>
        public IReadOnlyList<TrackInfo> Tracks { get; }

        /// <summary>
        /// Opens the tracks to copy: every audio and subtitle track, less those <c>--no-audio</c> and <c>--no-subtitles</c> leave
        /// out, or the audio and subtitle tracks <c>--track</c> names when it names some.
        /// </summary>
        public static OtherPackets Open(VideoSession session, InputFile input, TrackSelection selection)
        {
            if (input.IsStdin)
            {
                session.Info($"{input.Display}: standard input is read once, so only its video is written.");
                return new OtherPackets(null, []);
            }

            IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
            try
            {
                static bool Copied(TrackInfo t) => t.Kind is TrackKind.Audio or TrackKind.Subtitle;
                IReadOnlyList<TrackInfo> selected = selection.Select(demuxer.Tracks, input.Display);
                List<TrackInfo> tracks = selection.IsExplicit && selected.Any(Copied)
                    ? [.. selected.Where(Copied)]
                    : [.. demuxer.Tracks.Where(t => Copied(t) && !(selection.NoAudio && t.Kind == TrackKind.Audio) && !(selection.NoSubtitles && t.Kind == TrackKind.Subtitle))];
                if (tracks.Count == 0)
                {
                    demuxer.Dispose();
                    return new OtherPackets(null, []);
                }

                return new OtherPackets(demuxer, tracks);
            }
            catch
            {
                demuxer.Dispose();
                throw;
            }
        }

        /// <summary>Writes the packets up to a time.</summary>
        public void WriteUpTo(VideoWriter writer, double seconds)
        {
            while (Peek() is { } packet && packet.Timestamp.Seconds <= seconds)
            {
                _next = null;
                using (packet)
                {
                    writer.WritePacket(packet);
                }
            }
        }

        public void Dispose()
        {
            _next?.Dispose();
            _demuxer?.Dispose();
        }

        private EncodedPacket? Peek()
        {
            while (_next is null && _demuxer is not null)
            {
                if (_demuxer.ReadPacket() is not { } packet)
                {
                    _demuxer.Dispose();
                    _demuxer = null;
                    break;
                }

                if (_numbers.Contains(packet.TrackNumber))
                {
                    _next = packet;
                }
                else
                {
                    packet.Dispose();
                }
            }

            return _next;
        }
    }
}
