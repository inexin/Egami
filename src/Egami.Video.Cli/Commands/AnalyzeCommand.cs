using System.CommandLine;
using System.Globalization;

using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// The <c>analyze</c> command (V1.4 Build Plan task 17, Design Q11): runs any set of the Egami.Video.Analysis detectors over a
/// file in one decode and writes what they found as text, JSON or CSV, with per-frame or per-100 ms values to a CSV file on
/// request.
/// </summary>
internal static class AnalyzeCommand
{
    /// <summary>The detectors by name, in the order they are reported; the video ones first.</summary>
    public static readonly string[] Detectors =
        ["scenes", "black", "freeze", "crop", "interlace", "duplicates", "signal", "blur", "blockiness", "silence", "levels", "loudness"];

    private static readonly HashSet<string> AudioDetectors = new(StringComparer.Ordinal) { "silence", "levels", "loudness" };

    public static Command Create(VideoSession session)
    {
        MediaOptions media = new();
        Option<string[]> detect = new("--detect", "-d")
        {
            Description = $"What to look for, separated by commas or repeated: {string.Join(", ", Detectors)}.",
            HelpName = "detectors",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<bool> all = new("--all") { Description = "Run every detector the file has a track for; blur and blockiness come with signal." };
        Option<string?> start = new("--start", "-ss") { Description = "Start here instead of at the beginning.", HelpName = "time" };
        Option<string?> duration = new("--duration") { Description = "Look at this much and stop.", HelpName = "time" };
        Option<string?> series = new("--series") { Description = "Also write per-frame and per-100 ms values (scene scores, black share, signal levels, loudness) to this CSV file.", HelpName = "file.csv" };
        Option<bool> json = new("--json") { Description = "Write the findings as JSON." };
        Option<bool> csv = new("--csv") { Description = "Write the findings as CSV, one row per range or value." };
        Option<string?> ffmpegLog = new("--ffmpeg-log")
        {
            Description = $"Instead of the report, print the findings on standard error in the shape these ffmpeg filters log them, for scripts that read ffmpeg's output: {string.Join(", ", LogShapes)}.",
            HelpName = "filters",
        };
        Settings settings = new();
        Command command = new("analyze", "Look for black, frozen, silent, cropped, interlaced and duplicate stretches, scene cuts, signal levels and loudness in one pass, as ffmpeg's detection filters do.");
        media.AddTo(command, write: false);
        command.Options.Add(detect);
        command.Options.Add(all);
        command.Options.Add(start);
        command.Options.Add(duration);
        command.Options.Add(series);
        command.Options.Add(json);
        command.Options.Add(csv);
        command.Options.Add(ffmpegLog);
        settings.AddTo(command);
        command.SetAction(result =>
        {
            if (result.GetValue(json) && result.GetValue(csv))
            {
                throw new UsageException("Give --json or --csv, not both.");
            }

            string[]? shapes = LogFilters(result.GetValue(ffmpegLog));
            if (shapes is not null && (result.GetValue(json) || result.GetValue(csv)))
            {
                throw new UsageException("--ffmpeg-log replaces the report; give it without --json and --csv.");
            }

            HashSet<string> asked = Asked(result.GetValue(detect), result.GetValue(all));
            Plan plan = settings.Read(result);
            MediaTime? from = result.GetValue(start) is { } startText ? Time(startText, "--start", allowZero: true) : null;
            MediaTime? length = result.GetValue(duration) is { } lengthText ? Time(lengthText, "--duration", allowZero: false) : null;
            string? seriesPath = result.GetValue(series) is { } seriesText ? session.ResolvePath(seriesText) : null;
            BatchOptions batch = media.Batch(session, result);
            List<InputFile> inputs = media.ExpandInputs(session, result);
            TrackSelection selection = media.Tracks(result);
            bool every = result.GetValue(all);
            Findings?[] found = new Findings?[inputs.Count];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                found[input.Index] = Analyze(session, input, selection, asked, every, plan, from, length, seriesPath is not null, inputs.Count);
                return [];
            });
            if (shapes is not null)
            {
                WriteLog(session.Console.Error, found, shapes);
            }
            else
            {
                Write(session.Console.Text, inputs, found, result.GetValue(json), result.GetValue(csv));
            }

            if (seriesPath is not null)
            {
                WriteSeries(session, seriesPath, inputs, found);
            }

            return code;
        });
        return command;
    }

    /// <summary>The ffmpeg filters whose log lines <c>--ffmpeg-log</c> can print.</summary>
    public static readonly string[] LogShapes =
        ["blackdetect", "freezedetect", "cropdetect", "idet", "signalstats", "blurdetect", "blockdetect", "silencedetect", "volumedetect", "astats", "ebur128"];

    private static string[]? LogFilters(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string[] names = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string name in names)
        {
            if (!LogShapes.Contains(name))
            {
                throw new UsageException($"--ffmpeg-log '{name}' is not one of {string.Join(", ", LogShapes)}.");
            }
        }

        return names.Length == 0 ? throw new UsageException("--ffmpeg-log needs one or more filter names.") : names;
    }

    private static void WriteLog(TextWriter error, Findings?[] found, string[] shapes)
    {
        foreach (Findings findings in found.OfType<Findings>())
        {
            for (int i = 0; i < shapes.Length; i++)
            {
                foreach (string line in findings.Log.TryGetValue(shapes[i], out List<string>? lines) ? lines : [])
                {
                    error.WriteLine(line.Length == 0 ? string.Empty : $"[Parsed_{shapes[i]}_{GraphIndex(shapes, i)} @ egami] {line}");
                }
            }
        }
    }

    /// <summary>A filter's number in its graph, as ffmpeg names it: -vf and -af are separate graphs, each counted from 0.</summary>
    private static int GraphIndex(string[] shapes, int index)
    {
        static bool Audio(string filter) => filter is "silencedetect" or "volumedetect" or "astats" or "ebur128";
        return shapes.Take(index).Count(s => Audio(s) == Audio(shapes[index]));
    }

    private static HashSet<string> Asked(string[]? detect, bool all)
    {
        HashSet<string> asked = new(StringComparer.Ordinal);
        foreach (string name in (detect ?? []).SelectMany(d => d.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            string key = name.ToLowerInvariant() switch
            {
                "scene" => "scenes",
                "duplicate" or "dup" or "dups" => "duplicates",
                "level" => "levels",
                string other => other,
            };
            if (!Detectors.Contains(key))
            {
                throw new UsageException($"--detect '{name}' is not a detector: give {string.Join(", ", Detectors)}.");
            }

            asked.Add(key);
        }

        if (all)
        {
            asked.UnionWith(Detectors);
        }
        else if (asked.Count == 0)
        {
            throw new UsageException("Nothing to look for: give --detect with one or more of " + string.Join(", ", Detectors) + ", or --all.");
        }

        return asked;
    }

    private static MediaTime Time(string text, string option, bool allowZero)
    {
        TimeValue value = allowZero ? TimeValue.Parse(text, option) : TimeValue.ParseDuration(text, option, allowFrames: false);
        if (value.IsFrame || value.Seconds < 0)
        {
            throw new UsageException($"{option} '{text}' must be a time of 0 or more in seconds or [HH:]MM:SS[.mmm], not a frame number.");
        }

        return MediaTime.FromSeconds(value.Seconds);
    }

    /// <summary>Runs the detectors over one input and collects what they found.</summary>
    private static Findings Analyze(VideoSession session, InputFile input, TrackSelection selection, HashSet<string> asked, bool every,
        Plan plan, MediaTime? start, MediaTime? duration, bool series, int count)
    {
        string path = input.Path ?? throw new UsageException("analyze reads files, not standard input: save the stream to a file first.");
        MediaInfo info = MediaIO.Probe(session, input);
        bool wantsVideo = asked.Any(d => !AudioDetectors.Contains(d));
        bool wantsAudio = asked.Any(AudioDetectors.Contains);
        int? videoTrack = null, audioTrack = null;
        if (wantsVideo)
        {
            videoTrack = every && !selection.Select(info.Tracks, input.Display).Any(t => t.Kind == TrackKind.Video)
                ? null
                : selection.SelectOne(info.Tracks, TrackKind.Video, input.Display).Number;
        }

        if (wantsAudio)
        {
            audioTrack = every && !selection.Select(info.Tracks, input.Display).Any(t => t.Kind == TrackKind.Audio)
                ? null
                : selection.SelectOne(info.Tracks, TrackKind.Audio, input.Display).Number;
        }

        if (videoTrack is null && audioTrack is null)
        {
            throw new UsageException($"{input.Display}: no video or audio track to analyze; it has {TrackSelection.Describe(info.Tracks)}.");
        }

        bool Has(string name) => asked.Contains(name) && (AudioDetectors.Contains(name) ? audioTrack is not null : videoTrack is not null);
        BlackDetector? black = Has("black") ? new BlackDetector(plan.Black with { KeepSeries = series }) : null;
        using FreezeDetector? freeze = Has("freeze") ? new FreezeDetector(plan.Freeze) : null;
        CropDetector? crop = Has("crop") ? new CropDetector(plan.Crop) : null;
        InterlaceDetector? interlace = Has("interlace") ? new InterlaceDetector() : null;
        DuplicateDetector? duplicates = Has("duplicates") ? new DuplicateDetector(plan.Duplicates) : null;
        bool blur = Has("blur") || (every && Has("signal")), blockiness = Has("blockiness") || (every && Has("signal"));
        SignalMeter? signal = Has("signal") || blur || blockiness
            ? new SignalMeter(new SignalOptions { Blur = blur, Blockiness = blockiness, KeepSeries = series })
            : null;
        SilenceDetector? silence = Has("silence") ? new SilenceDetector(plan.Silence) : null;
        AudioLevelMeter? levels = Has("levels") ? new AudioLevelMeter() : null;
        LoudnessMeter? loudness = Has("loudness") ? new LoudnessMeter(new LoudnessOptions { KeepSeries = series }) : null;
        List<IVideoDetector> video = [];
        video.AddRange(new IVideoDetector?[] { black, freeze, crop, interlace, duplicates, signal }.OfType<IVideoDetector>());
        List<IAudioDetector> audio = [];
        audio.AddRange(new IAudioDetector?[] { silence, levels, loudness }.OfType<IAudioDetector>());
        SceneOptions? scenes = Has("scenes") ? plan.Scenes with { KeepSeries = series } : null;

        MediaAnalysisResult result = MediaAnalysis.Run(path, new MediaAnalysisOptions
        {
            Scenes = scenes,
            VideoDetectors = video,
            AudioDetectors = audio,
            VideoTrack = videoTrack,
            AudioTrack = audioTrack,
            Start = start,
            Duration = duration,
        }, session.Context, session.ProgressSink?.For(input, input.Index, count), session.Cancellation);

        Findings findings = new(input.Display, result);
        if (scenes is not null)
        {
            findings.Scenes(result);
        }

        if (black is not null)
        {
            findings.Ranges("black", black.Ranges, "ratio");
            findings.Series("black", black.Series.Select(v => (v.Time, "ratio", v.Value)));
        }

        if (freeze is not null)
        {
            findings.Ranges("freeze", freeze.Ranges, "difference");
        }

        if (crop is not null)
        {
            findings.Crop(crop.Report);
        }

        if (interlace is not null)
        {
            findings.Interlace(interlace.Report);
        }

        if (duplicates is not null)
        {
            findings.Ranges("duplicates", duplicates.Ranges, "frames");
        }

        if (signal is not null)
        {
            findings.Signal(signal.Report, signal.Series);
        }

        if (silence is not null)
        {
            findings.Ranges("silence", silence.Ranges, "peak");
            for (int c = 0; c < silence.ChannelRanges.Count; c++)
            {
                findings.Ranges($"silence.{c + 1}", silence.ChannelRanges[c], "peak");
            }
        }

        if (levels is not null)
        {
            findings.Levels(levels.Report());
        }

        if (loudness is not null)
        {
            findings.Loudness(loudness.Report(), loudness.MomentarySeries, loudness.ShortTermSeries);
        }

        session.Info($"{input.Display}: analyzed {TimeValue.Format((result.End - result.Start).Seconds)} ({result.VideoFrames} frames)");
        return findings;
    }

    private static void Write(TextWriter output, List<InputFile> inputs, Findings?[] found, bool json, bool csv)
    {
        if (json)
        {
            ReportObject report = new();
            List<ReportObject> files = report.List("files");
            files.AddRange(found.OfType<Findings>().Select(f => f.Json));
            ReportWriter.Json(output, report);
            return;
        }

        if (csv)
        {
            ReportWriter.Csv(output, [.. found.OfType<Findings>().SelectMany(f => f.Rows)]);
            return;
        }

        for (int i = 0; i < inputs.Count; i++)
        {
            if (found[i] is not { } findings)
            {
                continue;
            }

            if (inputs.Count > 1)
            {
                output.WriteLine($"{inputs[i].Display}:");
            }

            foreach (string line in findings.Text)
            {
                output.WriteLine(line);
            }
        }
    }

    private static void WriteSeries(VideoSession session, string path, List<InputFile> inputs, Findings?[] found)
    {
        if (!session.InPlace && inputs.Any(i => i.Path is { } input && string.Equals(Path.GetFullPath(input), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            throw new UsageException($"--series '{path}' is one of the inputs; give another file.");
        }

        List<ReportObject> rows = [.. found.OfType<Findings>().SelectMany(f => f.SeriesRows)];
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        using StreamWriter writer = new(path, append: false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        if (rows.Count == 0)
        {
            writer.Write("file,detector,time,seconds,name,value\n");
            return;
        }

        ReportWriter.Csv(writer, rows);
    }

    private static double Round(double value, int digits = 6) => double.IsFinite(value) ? Math.Round(value, digits) : value;

    private static string Number(double value, string format = "0.000") =>
        double.IsNegativeInfinity(value) ? "-inf" : double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "n/a";

    /// <summary>The detector settings the command line gives.</summary>
    private sealed record Plan(SceneOptions Scenes, BlackOptions Black, FreezeOptions Freeze, CropOptions Crop, DuplicateOptions Duplicates, SilenceOptions Silence);

    /// <summary>The per-detector options, named after the detector and its ffmpeg filter's option.</summary>
    private sealed class Settings
    {
        private readonly Option<string?> _sceneMethod = new("--scene-method") { Description = "How scene changes are scored: adaptive (the default), content or histogram.", HelpName = "method" };
        private readonly Option<double?> _sceneThreshold = new("--scene-threshold") { Description = "How sharp a change starts a scene, from 0 to 1. Default 0.15 for adaptive, 0.4 otherwise.", HelpName = "0-1" };
        private readonly Option<string?> _sceneMin = new("--scene-min") { Description = "The shortest scene. Default 0.6s.", HelpName = "time" };
        private readonly Option<string?> _blackMin = new("--black-min") { Description = "The shortest black stretch reported, as blackdetect's d. Default 2s.", HelpName = "time" };
        private readonly Option<double?> _blackRatio = new("--black-ratio") { Description = "The share of black pixels that makes a frame black, as blackdetect's pic_th. Default 0.98.", HelpName = "0-1" };
        private readonly Option<double?> _blackPixel = new("--black-pixel") { Description = "How dark a pixel must be to count as black, as a share of the luma range, as blackdetect's pix_th. Default 0.10.", HelpName = "0-1" };
        private readonly Option<string?> _freezeMin = new("--freeze-min") { Description = "The shortest frozen stretch reported, as freezedetect's d. Default 2s.", HelpName = "time" };
        private readonly Option<string?> _freezeNoise = new("--freeze-noise") { Description = "How much change still counts as frozen, as a ratio (0.001) or in dB (-60dB), as freezedetect's n. Default 0.001.", HelpName = "level" };
        private readonly Option<string?> _silenceMin = new("--silence-min") { Description = "The shortest silence reported, as silencedetect's d. Default 2s.", HelpName = "time" };
        private readonly Option<string?> _silenceNoise = new("--silence-noise") { Description = "The level under which audio is silent, in dB (-50dB) or as a ratio (0.003), as silencedetect's n. Default -60dB.", HelpName = "level" };
        private readonly Option<bool> _silenceChannels = new("--silence-per-channel") { Description = "Also report each channel's silences, as silencedetect's mono." };
        private readonly Option<double?> _cropLimit = new("--crop-limit") { Description = "The brightest a border may be, as a share of the range (0.094) or an 8-bit value (24), as cropdetect's limit. Default 24.", HelpName = "level" };
        private readonly Option<int?> _cropRound = new("--crop-round") { Description = "Round the width and height down to a multiple of this, as cropdetect's round. Default 2.", HelpName = "n" };
        private readonly Option<int?> _cropSkip = new("--crop-skip") { Description = "Frames at the start not measured, as cropdetect's skip. Default 2.", HelpName = "n" };
        private readonly Option<int?> _dupHigh = new("--dup-hi") { Description = "The block difference no 8x8 block may pass, as mpdecimate's hi. Default 768.", HelpName = "n" };
        private readonly Option<int?> _dupLow = new("--dup-lo") { Description = "The block difference only --dup-frac of blocks may pass, as mpdecimate's lo. Default 320.", HelpName = "n" };
        private readonly Option<double?> _dupFraction = new("--dup-frac") { Description = "The share of blocks that may pass --dup-lo, as mpdecimate's frac. Default 0.33.", HelpName = "0-1" };

        public void AddTo(Command command)
        {
            foreach (Option option in new Option[]
            {
                _sceneMethod, _sceneThreshold, _sceneMin, _blackMin, _blackRatio, _blackPixel, _freezeMin, _freezeNoise, _silenceMin, _silenceNoise,
                _silenceChannels, _cropLimit, _cropRound, _cropSkip, _dupHigh, _dupLow, _dupFraction,
            })
            {
                command.Options.Add(option);
            }
        }

        public Plan Read(ParseResult result)
        {
            SceneOptions scenes = new()
            {
                Method = result.GetValue(_sceneMethod)?.ToLowerInvariant() switch
                {
                    null or "adaptive" => SceneMethod.Adaptive,
                    "content" => SceneMethod.Content,
                    "histogram" => SceneMethod.Histogram,
                    string other => throw new UsageException($"--scene-method '{other}' is not adaptive, content or histogram."),
                },
                Threshold = Fraction(result.GetValue(_sceneThreshold), "--scene-threshold"),
            };
            if (Length(result.GetValue(_sceneMin), "--scene-min", allowZero: true) is { } sceneMin)
            {
                scenes = scenes with { MinSceneLength = sceneMin };
            }

            BlackOptions black = new();
            black = Fraction(result.GetValue(_blackRatio), "--black-ratio") is { } ratio ? black with { PictureRatio = ratio } : black;
            black = Fraction(result.GetValue(_blackPixel), "--black-pixel") is { } pixel ? black with { PixelThreshold = pixel } : black;
            black = Length(result.GetValue(_blackMin), "--black-min", allowZero: true) is { } blackMin ? black with { MinDuration = blackMin } : black;

            FreezeOptions freeze = new();
            freeze = Length(result.GetValue(_freezeMin), "--freeze-min", allowZero: false) is { } freezeMin ? freeze with { MinDuration = freezeMin } : freeze;
            freeze = result.GetValue(_freezeNoise) is { } freezeNoise ? freeze with { Noise = Level(freezeNoise, "--freeze-noise") } : freeze;

            SilenceOptions silence = new() { PerChannel = result.GetValue(_silenceChannels) };
            silence = Length(result.GetValue(_silenceMin), "--silence-min", allowZero: true) is { } silenceMin ? silence with { MinDuration = silenceMin } : silence;
            silence = result.GetValue(_silenceNoise) is { } silenceNoise ? silence with { Noise = 20 * Math.Log10(Level(silenceNoise, "--silence-noise")) } : silence;

            CropOptions crop = new();
            if (result.GetValue(_cropLimit) is { } limit)
            {
                crop = limit is >= 0 and <= 255 && double.IsFinite(limit)
                    ? crop with { Limit = limit > 1 ? limit / 255 : limit }
                    : throw new UsageException("--crop-limit must be a share from 0 to 1 or an 8-bit value from 0 to 255.");
            }

            crop = Whole(result.GetValue(_cropRound), "--crop-round", 1) is { } round ? crop with { Round = round } : crop;
            crop = Whole(result.GetValue(_cropSkip), "--crop-skip", 0) is { } skip ? crop with { Skip = skip } : crop;

            DuplicateOptions duplicates = new();
            duplicates = Whole(result.GetValue(_dupHigh), "--dup-hi", 0) is { } high ? duplicates with { High = high } : duplicates;
            duplicates = Whole(result.GetValue(_dupLow), "--dup-lo", 0) is { } low ? duplicates with { Low = low } : duplicates;
            duplicates = Fraction(result.GetValue(_dupFraction), "--dup-frac") is { } share ? duplicates with { Fraction = share } : duplicates;
            return new Plan(scenes, black, freeze, crop, duplicates, silence);
        }

        private static double? Fraction(double? value, string option) =>
            value is { } v && (v is < 0 or > 1 || double.IsNaN(v)) ? throw new UsageException($"{option} must be from 0 to 1.") : value;

        private static int? Whole(int? value, string option, int min) =>
            value is { } v && v < min ? throw new UsageException($"{option} must be {min} or more.") : value;

        private static MediaTime? Length(string? text, string option, bool allowZero)
        {
            if (text is null)
            {
                return null;
            }

            TimeValue value = TimeValue.Parse(text, option);
            return value.IsFrame || value.Seconds < 0 || (!allowZero && value.Seconds == 0)
                ? throw new UsageException($"{option} '{text}' must be a length of time{(allowZero ? " of 0 or more" : " more than zero")}, such as 2s or 500ms.")
                : MediaTime.FromSeconds(value.Seconds);
        }

        /// <summary>Parses a level as ffmpeg's detection filters take it: a ratio (0.001) or decibels (-60dB), returned as a ratio.</summary>
        private static double Level(string text, string option)
        {
            string value = text.Trim();
            bool decibels = value.EndsWith("db", StringComparison.OrdinalIgnoreCase);
            if (double.TryParse(decibels ? value[..^2] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            {
                double ratio = decibels ? Math.Pow(10, number / 20) : number;
                if (ratio is > 0 and <= 1)
                {
                    return ratio;
                }
            }

            throw new UsageException($"{option} '{text}' is not a level: give a ratio above 0 and up to 1 (0.001) or decibels up to 0 (-60dB).");
        }
    }

    /// <summary>What one file's detectors found, as JSON, CSV rows, text lines and series rows.</summary>
    private sealed class Findings
    {
        private readonly string _file;

        public Findings(string file, MediaAnalysisResult result)
        {
            _file = file;
            Json = new ReportObject().Add("file", file).Add("start", Round(result.Start.Seconds)).Add("end", Round(result.End.Seconds)).Add("frames", result.VideoFrames);
            Text.Add($"Analyzed {TimeValue.Format(result.Start)} to {TimeValue.Format(result.End)}, {result.VideoFrames} video frames");
        }

        public ReportObject Json { get; }

        public List<ReportObject> Rows { get; } = [];

        public List<string> Text { get; } = [];

        public List<ReportObject> SeriesRows { get; } = [];

        /// <summary>Gets the lines ffmpeg's filters would log for these findings, by filter name, without the <c>[filter @ …]</c> prefix.</summary>
        public Dictionary<string, List<string>> Log { get; } = new(StringComparer.Ordinal);

        private List<string> LogOf(string filter) => Log.TryGetValue(filter, out List<string>? lines) ? lines : Log[filter] = [];

        public void Scenes(MediaAnalysisResult result)
        {
            List<ReportObject> list = Json.List("scenes");
            Text.Add($"scenes: {result.Scenes.Count}");
            foreach (Scene scene in result.Scenes)
            {
                string kind = scene.StartKind.ToString().ToLowerInvariant();
                list.Add(new ReportObject().Add("scene", scene.Number).Add("frame", scene.StartFrame).Add("start", Round(scene.Start.Seconds))
                    .Add("end", Round(scene.End.Seconds)).Add("score", Round(scene.Score, 4)).Add("kind", kind));
                Rows.Add(Row("scenes", scene.Start, scene.End, "score", Round(scene.Score, 4)));
                Text.Add(string.Create(CultureInfo.InvariantCulture, $"  {scene.Number,4}  {TimeValue.Format(scene.Start)}  frame {scene.StartFrame,-6}  {kind,-5}  score {scene.Score:0.000}"));
            }

            List<ReportObject> fades = Json.List("fades");
            foreach (Fade fade in result.Fades)
            {
                string kind = fade.Kind.ToString().ToLowerInvariant();
                fades.Add(new ReportObject().Add("kind", kind).Add("start", Round(fade.Start.Seconds)).Add("end", Round(fade.End.Seconds)));
                Rows.Add(Row("fades", fade.Start, fade.End, "kind", kind));
                Text.Add($"  fade {kind}: {TimeValue.Format(fade.Start)} to {TimeValue.Format(fade.End)}");
            }

            Series("scenes", result.SceneScores.Select(s => (s.Time, "score", s.Score)));
        }

        public void Ranges(string name, IReadOnlyList<DetectedRange> ranges, string valueName)
        {
            List<ReportObject> list = Json.List(name);
            Text.Add($"{name}: {ranges.Count} {(ranges.Count == 1 ? "range" : "ranges")}");
            foreach (DetectedRange range in ranges)
            {
                double length = (range.End - range.Start).Seconds;
                list.Add(new ReportObject().Add("start", Round(range.Start.Seconds)).Add("end", Round(range.End.Seconds)).Add("duration", Round(length))
                    .Add(valueName, Round(range.Value, 4)));
                Rows.Add(Row(name, range.Start, range.End, valueName, Round(range.Value, 4)));
                Text.Add($"  {TimeValue.Format(range.Start)} to {TimeValue.Format(range.End)}  ({Number(length)} s, {valueName} {Number(range.Value)})");
                string start = Seconds(range.Start.Seconds), end = Seconds(range.End.Seconds), duration = Seconds(length);
                switch (name)
                {
                    case "black":
                        LogOf("blackdetect").Add($"black_start:{start} black_end:{end} black_duration:{duration}");
                        break;
                    case "freeze":
                        LogOf("freezedetect").AddRange([$"lavfi.freezedetect.freeze_start: {start}", $"lavfi.freezedetect.freeze_duration: {duration}", $"lavfi.freezedetect.freeze_end: {end}"]);
                        break;
                    case "silence":
                        LogOf("silencedetect").AddRange([$"silence_start: {start}", $"silence_end: {end} | silence_duration: {duration}"]);
                        break;
                }
            }
        }

        public void Crop(CropReport report)
        {
            ReportObject crop = Json.Object("crop");
            Rect r = report.Crop;
            crop.Add("x", r.X).Add("y", r.Y).Add("width", r.Width).Add("height", r.Height).Add("frames", report.Frames)
                .Add("frameWidth", report.FrameSize.Width).Add("frameHeight", report.FrameSize.Height);
            ReportObject common = crop.Object("mostCommon");
            common.Add("x", report.MostCommon.X).Add("y", report.MostCommon.Y).Add("width", report.MostCommon.Width).Add("height", report.MostCommon.Height);
            List<ReportObject> runs = crop.List("runs");
            foreach (CropRun run in report.Runs)
            {
                runs.Add(new ReportObject().Add("start", Round(run.Start.Seconds)).Add("end", Round(run.End.Seconds)).Add("frames", run.Frames)
                    .Add("x", run.Crop.X).Add("y", run.Crop.Y).Add("width", run.Crop.Width).Add("height", run.Crop.Height));
                Rows.Add(Row("crop.run", run.Start, run.End, "crop", CropText(run.Crop)));
            }

            foreach (CropRun run in report.Runs)
            {
                Rect c = run.Crop;
                LogOf("cropdetect").Add(string.Create(CultureInfo.InvariantCulture,
                    $"x1:{c.X} x2:{c.X + c.Width - 1} y1:{c.Y} y2:{c.Y + c.Height - 1} w:{c.Width} h:{c.Height} x:{c.X} y:{c.Y} t:{Seconds(run.Start.Seconds)} crop={CropText(c)}"));
            }

            Rows.Add(Row("crop", null, null, "crop", CropText(r)));
            Rows.Add(Row("crop", null, null, "mostCommon", CropText(report.MostCommon)));
            Text.Add($"crop: {CropText(r)} of {report.FrameSize.Width}x{report.FrameSize.Height} (most common {CropText(report.MostCommon)}, {report.Runs.Count} {(report.Runs.Count == 1 ? "run" : "runs")})");
            foreach (CropRun run in report.Runs.Count > 1 ? report.Runs : [])
            {
                Text.Add($"  {TimeValue.Format(run.Start)} to {TimeValue.Format(run.End)}  {CropText(run.Crop)}");
            }
        }

        public void Interlace(InterlaceReport report)
        {
            string verdict = report.Verdict switch
            {
                InterlaceVerdict.InterlacedTopFirst => "interlaced, top field first",
                InterlaceVerdict.InterlacedBottomFirst => "interlaced, bottom field first",
                InterlaceVerdict.Telecined => "telecined",
                _ => report.Verdict.ToString().ToLowerInvariant(),
            };
            Values("interlace", [
                ("verdict", verdict), ("tff", report.TopFieldFirst), ("bff", report.BottomFieldFirst), ("progressive", report.Progressive),
                ("undetermined", report.Undetermined), ("repeatedTop", report.RepeatedTop), ("repeatedBottom", report.RepeatedBottom), ("notRepeated", report.NotRepeated),
            ]);
            LogOf("idet").AddRange([
                $"Repeated Fields: Neither:{report.NotRepeated,6} Top:{report.RepeatedTop,6} Bottom:{report.RepeatedBottom,6}",
                $"Multi frame detection: TFF:{report.TopFieldFirst,6} BFF:{report.BottomFieldFirst,6} Progressive:{report.Progressive,6} Undetermined:{report.Undetermined,6}",
            ]);
        }

        public void Signal(SignalReport report, IReadOnlyList<SignalFrame> series)
        {
            ReportObject signal = Json.Object("signal");
            signal.Add("frames", report.Frames).Add("bitDepth", report.BitDepth).Add("range", report.Range.ToString().ToLowerInvariant())
                .Add("alpha", report.Alpha.ToString().ToLowerInvariant()).Add("outOfRange", Round(report.OutOfRange)).AddIf("blur", report.Blur is { } b ? Round(b, 4) : null)
                .AddIf("blockiness", report.Blockiness is { } k ? Round(k, 4) : null);
            foreach ((string name, PlaneLevels levels) in new[] { ("y", report.Y), ("cb", report.Cb), ("cr", report.Cr), ("saturation", report.Saturation) })
            {
                signal.Object(name).Add("min", Round(levels.Min, 3)).Add("low", Round(levels.Low, 3)).Add("mean", Round(levels.Mean, 3))
                    .Add("high", Round(levels.High, 3)).Add("max", Round(levels.Max, 3));
                Rows.Add(Row("signal", null, null, name, PlaneText(levels)));
            }

            Rows.Add(Row("signal", null, null, "range", report.Range.ToString().ToLowerInvariant()));
            Rows.Add(Row("signal", null, null, "alpha", report.Alpha.ToString().ToLowerInvariant()));
            Rows.Add(Row("signal", null, null, "outOfRange", Round(report.OutOfRange)));
            if (report.Blur is { } blur)
            {
                Rows.Add(Row("signal", null, null, "blur", Round(blur, 4)));
            }

            if (report.Blockiness is { } blockiness)
            {
                Rows.Add(Row("signal", null, null, "blockiness", Round(blockiness, 4)));
            }

            int signalStart = Text.Count;
            Text.Add($"signal: {report.Frames} frames, {report.BitDepth}-bit, {report.Range.ToString().ToLowerInvariant()} range, alpha {report.Alpha.ToString().ToLowerInvariant()}, {Number(report.OutOfRange * 100, "0.00")} % out of broadcast range");
            Text.Add($"  Y  {PlaneText(report.Y)}");
            Text.Add($"  Cb {PlaneText(report.Cb)}");
            Text.Add($"  Cr {PlaneText(report.Cr)}");
            Text.Add($"  saturation {PlaneText(report.Saturation)}");
            if (report.Blur is not null || report.Blockiness is not null)
            {
                Text.Add($"  blur {(report.Blur is { } b2 ? Number(b2, "0.00") + " px" : "not measured")}, blockiness {(report.Blockiness is { } k2 ? Number(k2, "0.00") : "not measured")}");
            }

            LogOf("signalstats").AddRange(Text.Skip(signalStart));

            // ffmpeg's documentation gives no log shape for blurdetect and blockdetect: they print the report's own line.
            LogOf("blurdetect").Add($"blur: {(report.Blur is { } logBlur ? Number(logBlur, "0.00") + " px" : "not measured")}");
            LogOf("blockdetect").Add($"blockiness: {(report.Blockiness is { } logBlock ? Number(logBlock, "0.00") : "not measured")}");
            foreach (SignalFrame frame in series)
            {
                AddSeries("signal", frame.Time, "y.mean", frame.Y.Mean);
                AddSeries("signal", frame.Time, "saturation.mean", frame.Saturation.Mean);
                AddSeries("signal", frame.Time, "outOfRange", frame.OutOfRange);
                if (frame.Blur is { } frameBlur)
                {
                    AddSeries("signal", frame.Time, "blur", frameBlur);
                }

                if (frame.Blockiness is { } frameBlockiness)
                {
                    AddSeries("signal", frame.Time, "blockiness", frameBlockiness);
                }
            }
        }

        public void Levels(AudioLevelReport report)
        {
            ReportObject levels = Json.Object("levels");
            levels.Add("samples", report.Samples).Add("sampleRate", report.SampleRate);
            Add(levels.Object("overall"), report.Overall);
            List<ReportObject> channels = levels.List("channels");
            foreach (ChannelLevels channel in report.Channels)
            {
                ReportObject item = new();
                Add(item, channel);
                channels.Add(item);
            }

            Text.Add($"levels: peak {Number(report.Overall.Peak, "0.0")} dBFS, RMS {Number(report.Overall.Rms, "0.0")} dBFS, crest {Number(report.Overall.CrestFactor, "0.0")} dB, noise floor {Number(report.Overall.NoiseFloor, "0.0")} dBFS, {report.Overall.Clipped} clipped");
            AddLevelRows("levels", report.Overall);
            int channelCount = Math.Max(1, report.Channels.Count);
            LogOf("volumedetect").AddRange([
                $"n_samples: {report.Samples * channelCount}",
                $"mean_volume: {Number(report.Overall.Rms, "0.0")} dB",
                $"max_volume: {Number(report.Overall.Peak, "0.0")} dB",
            ]);
            for (int c = 0; c < report.Channels.Count; c++)
            {
                LogOf("astats").Add($"Channel: {c + 1}");
                LogOf("astats").AddRange(AstatsLines(report.Channels[c]));
            }

            LogOf("astats").Add("Overall");
            LogOf("astats").AddRange(AstatsLines(report.Overall));
            LogOf("astats").Add($"Number of samples: {report.Samples}");
            for (int c = 0; c < report.Channels.Count; c++)
            {
                ChannelLevels channel = report.Channels[c];
                Text.Add($"  channel {c + 1}: peak {Number(channel.Peak, "0.0")}, RMS {Number(channel.Rms, "0.0")}, DC {Number(channel.DcOffset, "0.00000")}, {channel.Clipped} clipped, {channel.ZeroCrossings} zero crossings");
                AddLevelRows($"levels.{c + 1}", channel);
            }
        }

        public void Loudness(LoudnessReport report, IReadOnlyList<LoudnessValue> momentary, IReadOnlyList<LoudnessValue> shortTerm)
        {
            Values("loudness", [
                ("integrated", Round(report.Integrated, 2)), ("range", Round(report.Range, 2)), ("rangeLow", Round(report.RangeLow, 2)),
                ("rangeHigh", Round(report.RangeHigh, 2)), ("truePeak", Round(report.TruePeak, 2)), ("samplePeak", Round(report.SamplePeak, 2)),
                ("maxMomentary", Round(report.MaxMomentary, 2)), ("maxShortTerm", Round(report.MaxShortTerm, 2)),
            ], text: $"loudness: integrated {Number(report.Integrated, "0.0")} LUFS, range {Number(report.Range, "0.0")} LU, true peak {Number(report.TruePeak, "0.0")} dBTP, sample peak {Number(report.SamplePeak, "0.0")} dBFS");
            LogOf("ebur128").AddRange([
                "Summary:", string.Empty, "  Integrated loudness:", $"    I:         {Number(report.Integrated, "0.0"),5} LUFS", string.Empty,
                "  Loudness range:", $"    LRA:       {Number(report.Range, "0.0"),5} LU", $"    LRA low:   {Number(report.RangeLow, "0.0"),5} LUFS",
                $"    LRA high:  {Number(report.RangeHigh, "0.0"),5} LUFS", string.Empty, "  Sample peak:", $"    Peak:      {Number(report.SamplePeak, "0.0"),5} dBFS",
                string.Empty, "  True peak:", $"    Peak:      {Number(report.TruePeak, "0.0"),5} dBFS",
            ]);
            Series("loudness", momentary.Select(v => (v.Time, "momentary", v.Loudness)));
            Series("loudness", shortTerm.Select(v => (v.Time, "shortTerm", v.Loudness)));
        }

        public void Series(string detector, IEnumerable<(MediaTime Time, string Name, double Value)> values)
        {
            foreach ((MediaTime time, string name, double value) in values)
            {
                AddSeries(detector, time, name, value);
            }
        }

        private void AddSeries(string detector, MediaTime time, string name, double value)
        {
            SeriesRows.Add(new ReportObject().Add("file", _file).Add("detector", detector).Add("time", TimeValue.Format(time)).Add("seconds", Round(time.Seconds))
                .Add("name", name).Add("value", double.IsFinite(value) ? Math.Round(value, 6) : value));
        }

        private void Values(string name, (string Key, object Value)[] values, string? text = null)
        {
            ReportObject json = Json.Object(name);
            foreach ((string key, object value) in values)
            {
                json.Add(key, value);
                Rows.Add(Row(name, null, null, key, value));
            }

            Text.Add(text ?? $"{name}: " + string.Join(", ", values.Select(v => $"{v.Key} {ReportWriter.Text(v.Value)}")));
        }

        private void AddLevelRows(string detector, ChannelLevels levels)
        {
            foreach ((string key, object value) in LevelFields(levels))
            {
                Rows.Add(Row(detector, null, null, key, value));
            }
        }

        private static void Add(ReportObject target, ChannelLevels levels)
        {
            foreach ((string key, object value) in LevelFields(levels))
            {
                target.Add(key, value);
            }

            target.Add("histogram", string.Join(' ', levels.Histogram.Select(h => h.ToString(CultureInfo.InvariantCulture))));
        }

        private static (string, object)[] LevelFields(ChannelLevels levels) =>
        [
            ("peak", Round(levels.Peak, 3)), ("rms", Round(levels.Rms, 3)), ("dcOffset", Round(levels.DcOffset, 7)), ("crestFactor", Round(levels.CrestFactor, 3)),
            ("min", Round(levels.Min, 7)), ("max", Round(levels.Max, 7)), ("clipped", levels.Clipped), ("peakCount", levels.PeakCount),
            ("noiseFloor", Round(levels.NoiseFloor, 3)), ("zeroCrossings", levels.ZeroCrossings),
        ];

        private ReportObject Row(string detector, MediaTime? start, MediaTime? end, string name, object value) =>
            new ReportObject().Add("file", _file).Add("detector", detector)
                .Add("start", start is { } s ? Round(s.Seconds) : null).Add("end", end is { } e ? Round(e.Seconds) : null)
                .Add("duration", start is { } s2 && end is { } e2 ? Round((e2 - s2).Seconds) : null).Add("name", name).Add("value", value);

        private static string[] AstatsLines(ChannelLevels levels) =>
        [
            $"DC offset: {Number(levels.DcOffset, "0.000000")}", $"Min level: {Number(levels.Min, "0.000000")}", $"Max level: {Number(levels.Max, "0.000000")}",
            $"Peak level dB: {Number(levels.Peak, "0.000000")}", $"RMS level dB: {Number(levels.Rms, "0.000000")}", $"Crest factor: {Number(Math.Pow(10, levels.CrestFactor / 20), "0.000000")}",
            $"Zero crossings: {levels.ZeroCrossings}", $"Noise floor dB: {Number(levels.NoiseFloor, "0.000000")}", $"Number of clipped samples: {levels.Clipped}",
        ];

        /// <summary>A number as ffmpeg's filters log times: up to six decimals, no trailing zeros.</summary>
        private static string Seconds(double value) => Number(Math.Round(value, 6), "0.######");

        private static string CropText(Rect r) => $"{r.Width}:{r.Height}:{r.X}:{r.Y}";

        private static string PlaneText(PlaneLevels levels) =>
            string.Create(CultureInfo.InvariantCulture, $"min {levels.Min:0.#} low {levels.Low:0.#} mean {levels.Mean:0.##} high {levels.High:0.#} max {levels.Max:0.#}");
    }
}
