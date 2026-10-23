using System.CommandLine;
using System.Text;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video metadata</c> (Build Plan task 8, Video CLI design Q3): writes a file's tags, chapters, track names,
/// languages and default flags and attachment names as JSON (docs/schemas/egami-video-metadata-1.json), or writes a copy
/// (stream copy) with them changed from such a JSON, single tags set or removed and chapters added. Like
/// <c>ffmpeg -f ffmetadata</c> and <c>-map_metadata</c>.
/// </summary>
internal static class MetadataCommand
{
    /// <summary>The version of the metadata JSON schema.</summary>
    public const int Schema = 1;

    public static Command Create(VideoSession session)
    {
        MediaOptions options = new();
        Option<string?> load = new("--load") { Description = "Take tags, chapters, track fields and attachments from a JSON file as metadata writes it; keys it leaves out stay.", HelpName = "file.json" };
        Option<bool> clear = new("--clear") { Description = "Drop the tags, chapters, track names, languages and attachments that --load's JSON leaves out (all of them without --load)." };
        Option<string[]> set = new("--set") { Description = "Set a file tag, such as TITLE=Holiday; repeat it for more.", HelpName = "KEY=value" };
        Option<string[]> remove = new("--remove") { Description = "Remove a file tag; repeat it for more.", HelpName = "KEY" };
        Option<string[]> chapter = new("--chapter") { Description = "Add a chapter, such as 1:30-2:45=Intro or 90=Part two; repeat it for more.", HelpName = "start[-end]=title" };
        Command command = new("metadata", "Write tags, chapters, track names and languages and attachment names as JSON, or copy a file with them changed from such a JSON or with --set, --remove and --chapter.");
        options.AddTo(command);
        command.Options.Add(load);
        command.Options.Add(clear);
        command.Options.Add(set);
        command.Options.Add(remove);
        command.Options.Add(chapter);
        command.SetAction(result =>
        {
            MetadataEdit edit = new() { Clear = result.GetValue(clear) };
            if (result.GetValue(load) is { } path)
            {
                edit.LoadedPath = path;
                edit.Loaded = MetadataEdit.Load(session.ResolvePath(path));
            }

            try
            {
                foreach (string value in result.GetValue(set) ?? [])
                {
                    int equals = value.IndexOf('=', StringComparison.Ordinal);
                    edit.Set.Add(equals > 0
                        ? (value[..equals].Trim(), value[(equals + 1)..])
                        : throw new UsageException($"--set '{value}' needs a name and a value: KEY=value, such as TITLE=Holiday."));
                }

                edit.Remove.AddRange((result.GetValue(remove) ?? []).Select(k => k.Trim()));
                edit.Chapters.AddRange((result.GetValue(chapter) ?? []).Select(MetadataEdit.ParseChapter));
                BatchOptions batch = options.Batch(session, result);
                List<InputFile> inputs = options.ExpandInputs(session, result);
                TrackSelection selection = options.Tracks(result);
                return edit.Any
                    ? Write(session, options, result, inputs, batch, selection, edit)
                    : Dump(session, result.GetValue(options.Output), inputs, batch, selection);
            }
            finally
            {
                edit.Loaded?.Dispose();
            }
        });
        return command;
    }

    /// <summary>One file in the metadata JSON.</summary>
    internal static ReportObject FileReport(InputFile input, MediaInfo info, IReadOnlyList<TrackInfo> kept)
    {
        ReportObject file = new();
        file.Add("path", input.Display);
        file.Add("format", info.FormatName);
        file.Add("duration", info.Duration?.Seconds);
        file.Add("tags", info.Tags);
        List<ReportObject> chapters = file.List("chapters");
        foreach (ChapterInfo chapter in info.Chapters)
        {
            chapters.Add(new ReportObject()
                .Add("start", chapter.Start.Seconds)
                .Add("end", chapter.End?.Seconds)
                .Add("title", chapter.Title)
                .Add("language", chapter.Language));
        }

        List<ReportObject> tracks = file.List("tracks");
        for (int i = 0; i < info.Tracks.Count; i++)
        {
            TrackInfo track = info.Tracks[i];
            if (kept.Contains(track))
            {
                tracks.Add(new ReportObject()
                    .Add("number", track.Number)
                    .Add("index", i)
                    .Add("kind", Kinds.Name(track.Kind))
                    .Add("codec", CodecNames.Name(track.CodecId))
                    .Add("language", track.Language)
                    .Add("name", track.Name)
                    .Add("default", track.IsDefault)
                    .Add("tags", track.Tags));
            }
        }

        List<ReportObject> attachments = file.List("attachments");
        foreach (AttachmentInfo attachment in info.Attachments)
        {
            attachments.Add(new ReportObject()
                .Add("name", attachment.Name)
                .Add("mediaType", attachment.MediaType)
                .Add("size", attachment.Data.Length)
                .Add("description", attachment.Description)
                .Add("coverArt", attachment.IsCoverArt));
        }

        return file;
    }

    /// <summary>The metadata JSON of the inputs, to standard output or the file -o names.</summary>
    private static int Dump(VideoSession session, string? output, List<InputFile> inputs, BatchOptions batch, TrackSelection selection)
    {
        ReportObject?[] found = new ReportObject?[inputs.Count];
        int code = Batch.Run(session, inputs, batch, input =>
        {
            MediaInfo info = MediaIO.Probe(session, input);
            found[input.Index] = FileReport(input, info, selection.Select(info.Tracks, input.Display));
            return [];
        });

        ReportObject root = new();
        root.Add("schema", Schema);
        root.List("files").AddRange(found.OfType<ReportObject>());
        if (output is null or "-")
        {
            ReportWriter.Json(session.Console.Text, root);
        }
        else
        {
            StringWriter text = new() { NewLine = "\n" };
            ReportWriter.Json(text, root);
            MediaIO.Write(session, session.ResolvePath(output), stream => stream.Write(Encoding.UTF8.GetBytes(text.ToString())));
        }

        return code;
    }

    /// <summary>A copy of each input with the edits.</summary>
    private static int Write(VideoSession session, MediaOptions options, ParseResult result, List<InputFile> inputs, BatchOptions batch, TrackSelection selection, MetadataEdit edit)
    {
        OutputTemplate output = options.OutputTemplate(session, result, inputs);
        string? format = result.GetValue(options.Format);
        return Batch.Run(session, inputs, batch, input =>
        {
            MediaTarget target = MediaIO.Resolve(session, input, output, format, "mkv");
            using IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
            MediaInfo info = demuxer.Info;
            IReadOnlyList<TrackInfo> kept = selection.Select(demuxer.Tracks, input.Display);
            if (kept.Count == 0)
            {
                throw new UsageException($"{input.Display}: no tracks left to write.");
            }

            (List<TrackInfo> tracks, MediaMetadata metadata) = edit.Apply(info, kept, input);
            TrackCopy.Write(
                session,
                [new TrackSource(demuxer, tracks.ToDictionary(t => t.Number, t => t.Number))],
                target,
                tracks,
                metadata,
                session.ProgressSink?.For(input, input.Index, inputs.Count),
                info.Duration);
            return [target.Path];
        });
    }
}
