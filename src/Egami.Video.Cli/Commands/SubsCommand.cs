using System.CommandLine;
using System.Globalization;

using Egami.Video.Cli.Host;
using Egami.Video.Containers;
using Egami.Video.Subtitles;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video subs</c> (V1.2 Build Plan task 13, V1.2 design Q8): converts SRT, WebVTT and ASS files, shifts, scales and
/// syncs their timing, writes a media file's subtitle tracks as text files and adds text files to a media file as tracks. Like
/// <c>ffmpeg -i in.srt out.vtt</c>, <c>ffmpeg -itsoffset</c>, <c>ffmpeg -i in.mkv -map 0:s:0 out.srt</c> and
/// <c>ffmpeg -i in.mp4 -i in.srt -map 0 -map 1 -c copy -c:s mov_text out.mp4</c>.
/// </summary>
internal static class SubsCommand
{
    public static Command Create(VideoSession session)
    {
        Command command = new("subs", "Convert SRT, WebVTT and ASS files, shift and sync their timing, and copy subtitle tracks out of and into media files.");
        command.Subcommands.Add(Convert(session));
        command.Subcommands.Add(Shift(session));
        command.Subcommands.Add(Sync(session));
        command.Subcommands.Add(Extract(session));
        command.Subcommands.Add(Embed(session));
        return command;
    }

    private static Command Convert(VideoSession session)
    {
        TextOptions options = new();
        Option<string?> from = new("--from") { Description = "Keep only what shows from this time on; cues across it are cut there.", HelpName = "time" };
        Option<string?> to = new("--to") { Description = "Keep only what shows before this time; cues across it are cut there.", HelpName = "time" };
        Option<bool> rebase = new("--rebase") { Description = "Move the cues so --from becomes 0, as trim does to a video." };
        Option<bool> sort = new("--sort") { Description = "Put the cues in time order." };
        Option<bool> fixOverlaps = new("--fix-overlaps") { Description = "Sort, and end each cue when the next one on its layer starts, so lines never show at once by accident." };
        Command command = new("convert", "Write subtitle files in another format (SRT, WebVTT, ASS), optionally cut to a time range, sorted or with overlaps fixed. What the target cannot hold is reported once per kind.");
        options.AddTo(command);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(rebase);
        command.Options.Add(sort);
        command.Options.Add(fixOverlaps);
        command.SetAction(result =>
        {
            MediaTime? start = result.GetValue(from) is { } a ? Time(a, "--from") : null;
            MediaTime? end = result.GetValue(to) is { } b ? Time(b, "--to") : null;
            if (start is { } s && end is { } e && e <= s)
            {
                throw new UsageException("--to must be after --from.");
            }

            if (result.GetValue(rebase) && start is null)
            {
                throw new UsageException("--rebase moves --from to 0: give --from too.");
            }

            return options.Run(session, result, document =>
            {
                if (start is not null || end is not null)
                {
                    int removed = document.Clip(start ?? MediaTime.Zero, end ?? MediaTime.FromSeconds(1e9));
                    session.Info($"--from and --to left out {removed} cues.");
                    if (result.GetValue(rebase))
                    {
                        document.Shift(MediaTime.FromSeconds(-start!.Value.Seconds));
                    }
                }

                if (result.GetValue(fixOverlaps))
                {
                    session.Info($"shortened {document.FixOverlaps()} overlapping cues.");
                }
                else if (result.GetValue(sort))
                {
                    document.Sort();
                }
            });
        });
        return command;
    }

    private static Command Shift(VideoSession session)
    {
        TextOptions options = new();
        Option<string?> by = new("--by") { Description = "Move every cue: positive to show later, negative to show sooner, such as 2.5, -1:30 or -500ms.", HelpName = "time" };
        Option<string?> scale = new("--scale") { Description = "Multiply every time first, such as 1001/1000 or 0.96.", HelpName = "factor" };
        Option<string?> fps = new("--fps") { Description = "Retime for a frame rate change first, such as 23.976:25 for a film sped up to 25 fps.", HelpName = "from:to" };
        Command command = new("shift", "Move subtitle times by an offset, and scale them for a speed or frame rate change.");
        options.AddTo(command);
        command.Options.Add(by);
        command.Options.Add(scale);
        command.Options.Add(fps);
        command.SetAction(result =>
        {
            MediaTime? offset = result.GetValue(by) is { } text ? SignedTime(text, "--by") : null;
            Rational? factor = result.GetValue(scale) is { } s ? Factor(s) : null;
            if (result.GetValue(fps) is { } rates)
            {
                if (factor is not null)
                {
                    throw new UsageException("Give --scale or --fps, not both.");
                }

                int colon = rates.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new UsageException($"--fps '{rates}' needs two frame rates: from:to, such as 23.976:25.");
                }

                Rational source = EncodeCommand.ParseRate(rates[..colon], "--fps");
                Rational target = EncodeCommand.ParseRate(rates[(colon + 1)..], "--fps");

                // A film sped up from 23.976 to 25 fps shows each frame sooner: times shrink by 23.976 / 25.
                factor = new Rational(source.Numerator * target.Denominator, source.Denominator * target.Numerator);
            }

            if (offset is null && factor is null)
            {
                throw new UsageException("Nothing to do: give --by, --scale or --fps.");
            }

            return options.Run(session, result, document =>
            {
                if (factor is { } f)
                {
                    document.Scale(f);
                }

                if (offset is { } o)
                {
                    document.Shift(o);
                }
            });
        });
        return command;
    }

    private static Command Sync(VideoSession session)
    {
        TextOptions options = new();
        Option<string[]> map = new("--map")
        {
            Description = "A time in the subtitles and where it should be in the video, such as 1:02.5=1:04; give two, far apart, to fix a speed difference too.",
            HelpName = "subtitle=video",
            Required = true,
        };
        Command command = new("sync", "Retime subtitles by sync points: one moves every cue by the same amount, two also fix a speed difference.");
        options.AddTo(command);
        command.Options.Add(map);
        command.SetAction(result =>
        {
            List<(MediaTime From, MediaTime To)> points = [.. (result.GetValue(map) ?? []).Select(Point)];
            if (points.Count is 0 or > 2)
            {
                throw new UsageException("--sync takes one or two --map points.");
            }

            if (points.Count == 2 && points[0].From == points[1].From)
            {
                throw new UsageException("The two --map points need different subtitle times.");
            }

            return options.Run(session, result, document =>
            {
                if (points.Count == 1)
                {
                    document.Shift(points[0].To - points[0].From);
                }
                else
                {
                    document.Sync(points[0].From, points[0].To, points[1].From, points[1].To);
                }
            });
        });
        return command;
    }

    private static Command Extract(VideoSession session)
    {
        MediaOptions media = new();
        TextFormatOptions text = new();
        Command command = new("extract", "Write a media file's text subtitle tracks as SRT, WebVTT or ASS files: each in its own format, or the one --format names.");
        media.Inputs.Description = "Media files with text subtitle tracks, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.";
        media.Track.Description = "Which subtitle tracks: s:1, s:eng or track numbers as info shows them; repeat it or separate with commas. Every subtitle track by default.";
        media.Output.Description = "Where to write: a file, a folder, a template with {name}, {track} (the track number) and {format}, or - for standard output.";
        media.AddTo(command, write: false, tracks: false);
        command.Options.Add(media.Output);
        command.Options.Add(media.Track);
        text.AddTo(command);
        command.Options.Add(media.InPlace);
        command.SetAction(result =>
        {
            BatchOptions batch = media.Batch(session, result);
            List<InputFile> inputs = media.ExpandInputs(session, result);
            string output = result.GetValue(media.Output)
                ?? throw new UsageException("No output: give -o with a file, a folder, a template such as out/{name}-{track}.{format}, or - for standard output.");
            OutputTemplate template = MediaOptions.CheckTemplate(session, output, inputs, "out/{name}-{track}.{format}", "track");
            string[] tracks = result.GetValue(media.Track) ?? [];
            TrackSelection selection = TrackSelection.Parse(tracks.Length == 0 ? ["s"] : tracks, false, false);
            SubtitleFormat? format = text.Format(result);
            SubtitleReaderOptions reader = text.Reader(result);
            SubtitleWriterOptions writer = text.Writer(result);
            return Batch.Run(session, inputs, batch, input =>
            {
                using IDemuxer probe = MediaIO.OpenDemuxer(session, input);
                if (!probe.Tracks.Any(t => t.Kind == TrackKind.Subtitle))
                {
                    throw new UsageException($"{input.Display}: no subtitle track to extract; it has {TrackSelection.Describe(probe.Tracks)}.");
                }

                List<TrackInfo> picked = [.. selection.Select(probe.Tracks, input.Display).Where(t => t.Kind == TrackKind.Subtitle)];
                if (picked.Count == 0)
                {
                    throw new UsageException($"{input.Display}: no subtitle track to extract; it has {TrackSelection.Describe(probe.Tracks)}.");
                }

                bool several = picked.Count > 1;
                if (several && (template.IsStdout || (!IsFolder(session, template) && !template.NamesFrames)))
                {
                    throw new UsageException($"{input.Display} has {picked.Count} subtitle tracks: give --track to pick one, such as --track s:eng, or a folder or a template with {{track}}.");
                }

                if (several && input.IsStdin)
                {
                    throw new UsageException("Standard input can be read once: extract one track from it per run.");
                }

                List<string> written = [];
                IDemuxer? open = probe;
                foreach (TrackInfo track in picked)
                {
                    IDemuxer demuxer = open ?? MediaIO.OpenDemuxer(session, input);
                    try
                    {
                        SubtitleDocument document = SubtitleFiles.ReadTrack(session, demuxer, track, reader, input.Display);
                        SubtitleFormat target = format ?? SubtitleFiles.FormatOf(track.CodecId) ?? SubtitleFormat.Srt;
                        string path = Resolve(session, input, template, target, several || template.NamesFrames ? track.Number : 0);
                        Save(session, document, path, target, writer);
                        written.Add(path);
                    }
                    finally
                    {
                        if (open is null)
                        {
                            demuxer.Dispose();
                        }

                        open = null;
                    }
                }

                return written;
            });
        });
        return command;
    }

    private static Command Embed(VideoSession session)
    {
        MediaOptions media = new();
        TextFormatOptions text = new() { Writes = false };
        Option<string[]> add = new("--add")
        {
            Description = "A subtitle file to add as a track, with an optional language after '=' (movie.nor.srt=nor); repeat it for more.",
            HelpName = "file[=language]",
            Required = true,
        };
        Option<string[]> name = new("--name") { Description = "Name an added track, in --add's order from 1: 1=Commentary.", HelpName = "index=name" };
        Option<string?> isDefault = new("--default") { Description = "Make an added track the default subtitles, in --add's order from 1.", HelpName = "index" };
        Option<bool> replace = new("--replace") { Description = "Leave out the input's own subtitle tracks." };
        Command command = new("embed", "Add subtitle files to a media file as tracks, copying its other tracks without decoding. SRT, WebVTT and ASS stay as they are in Matroska; WebM takes WebVTT and MP4 mov_text or WebVTT, converted as needed.");
        media.AddTo(command);
        command.Options.Add(add);
        command.Options.Add(name);
        command.Options.Add(isDefault);
        command.Options.Add(replace);
        text.AddTo(command);
        command.SetAction(result =>
        {
            List<(string Path, string? Language)> files = [.. (result.GetValue(add) ?? []).Select(a => ParseAdd(session, a))];
            Dictionary<int, string> names = [];
            foreach (string value in result.GetValue(name) ?? [])
            {
                int equals = value.IndexOf('=', StringComparison.Ordinal);
                names[equals > 0 ? AddIndex(value[..equals], files.Count, "--name") : throw new UsageException($"--name '{value}' needs an index and a name: 1=Commentary.")] = value[(equals + 1)..];
            }

            int? chosen = result.GetValue(isDefault) is { } d ? AddIndex(d, files.Count, "--default") : null;
            SubtitleReaderOptions reader = text.Reader(result);
            List<SubtitleDocument> documents = [.. files.Select(f => SubtitleFiles.Load(session, new InputFile(f.Path, f.Path, string.Empty, 0), reader))];
            BatchOptions batch = media.Batch(session, result);
            List<InputFile> inputs = media.ExpandInputs(session, result);
            OutputTemplate output = media.OutputTemplate(session, result, inputs);
            TrackSelection selection = media.Tracks(result);
            string? container = result.GetValue(media.Format);
            bool dropOwn = result.GetValue(replace);
            return Batch.Run(session, inputs, batch, input =>
            {
                MediaTarget target = MediaIO.Resolve(session, input, output, container, "mkv");
                using IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
                List<TrackInfo> tracks = [];
                Dictionary<int, int> numbers = [];
                foreach (TrackInfo track in selection.Select(demuxer.Tracks, input.Display).Where(t => !dropOwn || t.Kind != TrackKind.Subtitle))
                {
                    TrackInfo kept = track with { Number = tracks.Count + 1 };
                    if (chosen is not null && kept.Kind == TrackKind.Subtitle)
                    {
                        kept = kept with { IsDefault = false };
                    }

                    tracks.Add(kept);
                    numbers[track.Number] = tracks.Count;
                }

                string extension = target.Container.Extensions[0];
                List<TrackSource> sources = [new TrackSource(demuxer, numbers)];
                for (int i = 0; i < documents.Count; i++)
                {
                    string codec;
                    try
                    {
                        codec = SubtitleTrackWriter.CodecIdFor(extension, documents[i].Format);
                    }
                    catch (NotSupportedException)
                    {
                        throw new UsageException($"{target.Container.Name} holds no text subtitles; write .mkv, .webm or .mp4.");
                    }

                    int number = tracks.Count + 1;
                    SubtitleTrackWriter writer = new(documents[i], number, codec, session.Context);
                    TrackInfo track = writer.Track with
                    {
                        Language = files[i].Language ?? writer.Track.Language,
                        Name = names.TryGetValue(i + 1, out string? title) ? (title.Length == 0 ? null : title) : writer.Track.Name,
                        IsDefault = chosen is null ? writer.Track.IsDefault : chosen == i + 1,
                    };
                    tracks.Add(track);
                    sources.Add(new TrackSource(new SubtitleFiles.PacketDemuxer(writer, SubtitleFiles.Name(documents[i].Format)), new Dictionary<int, int> { [number] = number }));
                }

                try
                {
                    TrackCopy.Write(session, sources, target, tracks, demuxer.Info.Metadata, session.ProgressSink?.For(input, input.Index, inputs.Count), demuxer.Info.Duration);
                }
                finally
                {
                    foreach (TrackSource source in sources.Skip(1))
                    {
                        source.Demuxer.Dispose();
                    }
                }

                return [target.Path];
            });
        });
        return command;
    }

    /// <summary>A time that may be negative: a minus sign before what <see cref="TimeValue"/> reads.</summary>
    internal static MediaTime SignedTime(string text, string option)
    {
        string value = text.Trim();
        bool negative = value.StartsWith('-');
        TimeValue time = TimeValue.Parse(negative || value.StartsWith('+') ? value[1..] : value, option);
        if (time.IsFrame)
        {
            throw new UsageException($"{option} takes a time, not a frame number: subtitle files have no frames.");
        }

        return MediaTime.FromSeconds(negative ? -time.Seconds : time.Seconds);
    }

    private static MediaTime Time(string text, string option)
    {
        TimeValue time = TimeValue.Parse(text, option);
        return time.IsFrame
            ? throw new UsageException($"{option} takes a time, not a frame number: subtitle files have no frames.")
            : MediaTime.FromSeconds(time.Seconds);
    }

    private static (MediaTime From, MediaTime To) Point(string text)
    {
        int equals = text.IndexOf('=', StringComparison.Ordinal);
        return equals > 0
            ? (Time(text[..equals], "--map"), Time(text[(equals + 1)..], "--map"))
            : throw new UsageException($"--map '{text}' needs two times: subtitle=video, such as 1:02.5=1:04.");
    }

    private static Rational Factor(string text)
    {
        try
        {
            Rational factor = text.Contains('/', StringComparison.Ordinal)
                ? Rational.Parse(text.Trim())
                : double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
                    ? new Rational((long)Math.Round(value * 1_000_000), 1_000_000)
                    : throw new FormatException();
            return factor.Numerator > 0 && factor.Denominator > 0 ? factor : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new UsageException($"--scale '{text}' is not a factor above 0: give a fraction such as 1001/1000 or a number such as 0.96.");
        }
    }

    private static (string Path, string? Language) ParseAdd(VideoSession session, string text)
    {
        string whole = session.ResolvePath(text);
        int equals = text.LastIndexOf('=');
        (string path, string? language) = File.Exists(whole) || equals <= 0
            ? (whole, null)
            : (session.ResolvePath(text[..equals]), text[(equals + 1)..].Trim());
        if (!File.Exists(path))
        {
            throw new UsageException($"'{path}' does not exist.");
        }

        return SubtitleFiles.IsSubtitlePath(path)
            ? (path, string.IsNullOrEmpty(language) ? null : language)
            : throw new UsageException($"--add '{text}' is not a subtitle file: give .srt, .vtt, .ass or .ssa; 'egami-video embed --add' adds tracks of media files.");
    }

    private static int AddIndex(string text, int count, string option)
    {
        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 1 && index <= count
            ? index
            : throw new UsageException($"{option} '{text}' must name an added file by its place among the --add options, from 1 to {count}.");
    }

    private static bool IsFolder(VideoSession session, OutputTemplate output) =>
        !output.IsStdout && (output.Text.EndsWith('/') || output.Text.EndsWith('\\') || (!output.HasPlaceholders && Directory.Exists(session.ResolvePath(output.Text))));

    /// <summary>Where a subtitle output goes: the template for the input, a folder taking the input's name, or <c>-</c>.</summary>
    private static string Resolve(VideoSession session, InputFile input, OutputTemplate template, SubtitleFormat format, int part = 0)
    {
        if (template.IsStdout)
        {
            return "-";
        }

        string extension = SubtitleFiles.Extension(format);
        string path = IsFolder(session, template)
            ? Path.Combine(session.ResolvePath(template.Text), $"{input.Name}{(part > 0 ? $"-{part}" : string.Empty)}.{extension}")
            : session.ResolvePath(template.Resolve(input, extension, part));
        if (!session.InPlace && input.Path is { } source && Batch.SamePath(path, source))
        {
            throw new UsageException($"{input.Display}: the output would replace the input; give another -o, or --in-place to replace it.");
        }

        return path;
    }

    private static void Save(VideoSession session, SubtitleDocument document, string path, SubtitleFormat format, SubtitleWriterOptions options)
    {
        session.Cancellation.ThrowIfCancellationRequested();
        MediaIO.Write(session, path, stream => document.Save(stream, format, options, session.Context));
    }

    /// <summary>The encoding, strictness and output format options of the commands that read or write subtitle text.</summary>
    private sealed class TextFormatOptions
    {
        public Option<string?> FormatOption { get; } = new("--format", "-f") { Description = "The subtitle format to write: srt, vtt or ass, when -o's extension does not say.", HelpName = "srt|vtt|ass" };

        public Option<string?> Encoding { get; } = new("--encoding") { Description = "Read files without a byte order mark that are not UTF-8 in this code page, such as windows-1250, instead of Windows-1252.", HelpName = "name" };

        public Option<bool> Strict { get; } = new("--strict") { Description = "Fail on a malformed file instead of repairing it with a warning." };

        public Option<bool> Bom { get; } = new("--bom") { Description = "Start the written files with a UTF-8 byte order mark." };

        public Option<bool> Crlf { get; } = new("--crlf") { Description = "End lines with CR LF, as Windows tools write them, instead of LF." };

        /// <summary>Gets or sets a value indicating whether the command writes subtitle text, so the write options apply.</summary>
        public bool Writes { get; init; } = true;

        public void AddTo(Command command)
        {
            if (Writes)
            {
                command.Options.Add(FormatOption);
            }

            command.Options.Add(Encoding);
            command.Options.Add(Strict);
            if (Writes)
            {
                command.Options.Add(Bom);
                command.Options.Add(Crlf);
            }
        }

        public SubtitleFormat? Format(ParseResult result) => result.GetValue(FormatOption) is { } text ? SubtitleFiles.ParseFormat(text) : null;

        public SubtitleReaderOptions Reader(ParseResult result) => SubtitleFiles.ReaderOptions(result.GetValue(Encoding), result.GetValue(Strict));

        public SubtitleWriterOptions Writer(ParseResult result) => new()
        {
            ByteOrderMark = result.GetValue(Bom) ? true : null,
            NewLine = result.GetValue(Crlf) ? "\r\n" : null,
        };
    }

    /// <summary>
    /// The options of <c>convert</c>, <c>shift</c> and <c>sync</c>: subtitle files in, one changed file out per input. A media
    /// file's subtitle track (the first, or the one <c>--track</c> picks) can be the input too.
    /// </summary>
    private sealed class TextOptions
    {
        private readonly MediaOptions _media = new();
        private readonly TextFormatOptions _text = new();

        public void AddTo(Command command)
        {
            _media.Inputs.Description = "SRT, WebVTT, ASS or SSA files, globs (*, ?, **) expanded the same way in every shell, media files with a subtitle track, or - for standard input.";
            _media.Track.Description = "The subtitle track of a media file input: s:1, s:eng or a track number as info shows it; the first one by default.";
            _media.InPlace.Description = "Replace each input with its changed file when there is no -o, or allow -o to name an input.";
            command.Arguments.Add(_media.Inputs);
            command.Options.Add(_media.Output);
            _text.AddTo(command);
            command.Options.Add(_media.Track);
            command.Options.Add(_media.Jobs);
            command.Options.Add(_media.InPlace);
            command.Options.Add(_media.FailFast);
        }

        /// <summary>Loads each input, changes it with <paramref name="change"/> and writes it.</summary>
        public int Run(VideoSession session, ParseResult result, Action<SubtitleDocument> change)
        {
            BatchOptions batch = _media.Batch(session, result);
            List<InputFile> inputs = _media.ExpandInputs(session, result);
            string? text = result.GetValue(_media.Output);
            if (text is null && !batch.InPlace)
            {
                throw new UsageException("No output: give -o with a file, a folder, a template such as out/{name}.{format}, or - for standard output; or --in-place to replace the inputs.");
            }

            OutputTemplate? template = text is null ? null : MediaOptions.CheckTemplate(session, text, inputs, "out/{name}.{format}");
            SubtitleFormat? format = _text.Format(result);
            SubtitleReaderOptions reader = _text.Reader(result);
            SubtitleWriterOptions writer = _text.Writer(result);
            string[] tracks = result.GetValue(_media.Track) ?? [];
            return Batch.Run(session, inputs, batch, input =>
            {
                SubtitleDocument document = Load(session, input, reader, tracks);
                change(document);
                string? named = template is null ? input.Path : template.IsStdout || template.Text.EndsWith("{format}", StringComparison.Ordinal) ? null : template.Text;
                SubtitleFormat target = format
                    ?? (named is null ? null : SubtitleFiles.FromName(Path.GetExtension(named)))
                    ?? (document.Format == SubtitleFormat.Ssa ? SubtitleFormat.Ass : document.Format);
                if (target == SubtitleFormat.Ssa)
                {
                    throw new UsageException("SSA is read only; write .ass, which every player that reads SSA reads too.");
                }

                string path = template is null
                    ? input.Path ?? throw new UsageException("--in-place replaces files; standard input needs -o.")
                    : Resolve(session, input, template, target);
                if (path != "-" && SubtitleFiles.FromName(Path.GetExtension(path)) is null)
                {
                    throw new UsageException($"'{path}' is not a subtitle file name: end it with .srt, .vtt or .ass, or give --format.");
                }

                Save(session, document, path, target, writer);
                return [path];
            });
        }

        private static SubtitleDocument Load(VideoSession session, InputFile input, SubtitleReaderOptions reader, string[] tracks)
        {
            if (input.IsStdin || SubtitleFiles.IsSubtitlePath(input.Path))
            {
                return tracks.Length == 0
                    ? SubtitleFiles.Load(session, input, reader)
                    : throw new UsageException($"{input.Display} is a subtitle file; --track picks a subtitle track of a media file.");
            }

            using IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
            TrackSelection selection = TrackSelection.Parse(tracks.Length == 0 ? ["s:0"] : tracks, false, false);
            List<TrackInfo> picked = [.. selection.Select(demuxer.Tracks, input.Display).Where(t => t.Kind == TrackKind.Subtitle)];
            return picked.Count switch
            {
                1 => SubtitleFiles.ReadTrack(session, demuxer, picked[0], reader, input.Display),
                0 => throw new UsageException($"{input.Display}: no subtitle track; it has {TrackSelection.Describe(demuxer.Tracks)}."),
                _ => throw new UsageException($"{input.Display}: --track picks {picked.Count} subtitle tracks; pick one, or use 'subs extract' for several."),
            };
        }
    }
}
