using System.CommandLine;
using System.Globalization;

using Egami.Video.Cli.Host;
using Egami.Video.Containers;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video extract</c> and <c>embed</c> (Build Plan task 8, Video CLI design Q3 and Q4): copy a track out of a file
/// into its own file, or tracks, attachments and cover art from other files into one, without decoding. Like
/// <c>ffmpeg -map 0:a:0 -c copy</c>, <c>ffmpeg -dump_attachment</c> and <c>ffmpeg -i a -i b -map 0 -map 1 -c copy</c>.
/// </summary>
internal static class TrackCommands
{
    public static Command Extract(VideoSession session)
    {
        MediaOptions options = new();
        Option<string[]> attachment = new("--attachment")
        {
            Description = "Write attachments as files instead of a track: by name, by index counted from 0, or all.",
            HelpName = "name|index|all",
        };
        Option<bool> cover = new("--cover") { Description = "Write the cover art as a file instead of a track." };
        Command command = new("extract", "Copy a track into its own file without decoding (VP8, VP9 and AV1 to IVF, PCM to WAV, FLAC to FLAC, Opus and Vorbis to Ogg, the rest to Matroska), or write attachments and cover art as files.");
        options.AddTo(command);
        command.Options.Add(attachment);
        command.Options.Add(cover);
        command.SetAction(result =>
        {
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            string text = result.GetValue(options.Output)
                ?? throw new UsageException("No output: give -o with a file, a folder, a template such as out/{name}-{track}.{format}, or - with --format for standard output.");
            OutputTemplate output = MediaOptions.CheckTemplate(session, text, inputs, "out/{name}-{track}.{format}", "track");
            TrackSelection selection = options.Tracks(result);
            string? format = result.GetValue(options.Format);
            string[] attachments = result.GetValue(attachment) ?? [];
            bool wantCover = result.GetValue(cover);
            bool files = attachments.Length > 0 || wantCover;
            if (files && selection.IsExplicit)
            {
                throw new UsageException("Give --track for a track, or --attachment and --cover for attached files, not both.");
            }

            return Batch.Run(session, inputs, batch, input => files
                ? ExtractAttachments(session, input, output, attachments, wantCover)
                : ExtractTracks(session, input, output, selection, format, inputs.Count));
        });
        return command;
    }

    public static Command Embed(VideoSession session)
    {
        MediaOptions options = new();
        Option<string[]> add = new("--add")
        {
            Description = "Add the tracks of another file, all of them or those a track spec after ':' picks (audio.flac, other.mkv:a:1).",
            HelpName = "file[:tracks]",
        };
        Option<string[]> attach = new("--attach") { Description = "Add a file as an attachment, such as a font for subtitles.", HelpName = "file" };
        Option<string?> cover = new("--cover") { Description = "Add a JPEG, PNG or WebP image as cover art, replacing any cover art the input has.", HelpName = "image" };
        Option<string[]> language = new("--language") { Description = "Set an output track's language, such as 2=eng or a:0=nor.", HelpName = "track=language" };
        Option<string[]> name = new("--name") { Description = "Set an output track's name, such as s:0=Commentary.", HelpName = "track=name" };
        Option<string[]> isDefault = new("--default") { Description = "Make an output track the default one of its kind; the others of that kind are not.", HelpName = "track" };
        Command command = new("embed", "Add tracks from other files, attachments and cover art to a file without decoding, and set track languages, names and defaults. Track numbers count the output's tracks from 1.");
        options.AddTo(command);
        command.Options.Add(add);
        command.Options.Add(attach);
        command.Options.Add(cover);
        command.Options.Add(language);
        command.Options.Add(name);
        command.Options.Add(isDefault);
        command.SetAction(result =>
        {
            EmbedRequest request = new(
                [.. (result.GetValue(add) ?? []).Select(a => ParseAdd(session, a))],
                [.. (result.GetValue(attach) ?? []).Select(session.ResolvePath)],
                result.GetValue(cover) is { } image ? session.ResolvePath(image) : null,
                [.. (result.GetValue(language) ?? []).Select(v => Pair(v, "--language"))],
                [.. (result.GetValue(name) ?? []).Select(v => Pair(v, "--name"))],
                result.GetValue(isDefault) ?? []);
            if (request.Adds.Count + request.Attach.Count + request.Languages.Count + request.Names.Count + request.Defaults.Count == 0 && request.Cover is null)
            {
                throw new UsageException("Nothing to embed: give --add, --attach, --cover, --language, --name or --default.");
            }

            foreach (string file in request.Adds.Select(a => a.Path).Concat(request.Attach).Append(request.Cover).OfType<string>())
            {
                if (!File.Exists(file))
                {
                    throw new UsageException($"'{file}' does not exist.");
                }
            }

            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs);
            TrackSelection selection = options.Tracks(result);
            string? format = result.GetValue(options.Format);
            return Batch.Run(session, inputs, batch, input =>
            {
                MediaTarget target = MediaIO.Resolve(session, input, output, format, "mkv");
                RunEmbed(session, input, target, selection, request, inputs.Count);
                return [target.Path];
            });
        });
        return command;
    }

    /// <summary>The default extension for an extracted track: a container made for its codec when it takes the track, else Matroska.</summary>
    internal static string DefaultExtension(VideoSession session, TrackInfo track)
    {
        string extension = track.CodecId switch
        {
            CodecIds.Vp8 or CodecIds.Vp9 or CodecIds.Av1 => "ivf",
            CodecIds.Uncompressed => "y4m",
            CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat => "wav",
            CodecIds.Flac => "flac",
            CodecIds.Alac => "m4a",
            CodecIds.Opus or CodecIds.Vorbis => "ogg",
            _ => string.Empty,
        };
        string matroska = track.Kind switch
        {
            TrackKind.Audio => "mka",
            TrackKind.Subtitle => "mks",
            _ => "mkv",
        };
        return extension.Length > 0 && TrackCopy.CanHold(MediaIO.FindContainer(session, extension), track) ? extension : matroska;
    }

    /// <summary>The media type of an attached file, from its extension.</summary>
    internal static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ass" or ".ssa" => "text/x-ssa",
        ".srt" => "application/x-subrip",
        ".txt" => "text/plain",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    /// <summary>Why a container cannot hold new attachments, or <see langword="null"/> when it can.</summary>
    internal static string? AttachmentProblem(ContainerFormat container, IReadOnlyList<AttachmentInfo> added)
    {
        return container switch
        {
            MatroskaFormat => null,
            Mp4Format when added.All(a => a.IsCoverArt && a.MediaType is "image/jpeg" or "image/png") => null,
            FlacFormat when added.All(a => a.MediaType.StartsWith("image/", StringComparison.Ordinal)) => null,
            Mp4Format => "MP4 holds cover art (JPEG or PNG) but no other attachments; write .mkv.",
            FlacFormat => "FLAC holds pictures but no other attachments; write .mkv.",
            _ => $"{container.Name} holds no attachments; write .mkv.",
        };
    }

    /// <summary>
    /// Splits an <c>--add</c> value into a file and a track spec: <c>other.mkv:a:1</c>. A colon is the separator only when what
    /// follows is a track spec, and a path that exists wins, so <c>C:\media\a.flac</c> stays a path.
    /// </summary>
    internal static (string Path, TrackSelection Tracks) ParseAdd(VideoSession session, string text)
    {
        string whole = session.ResolvePath(text);
        if (File.Exists(whole))
        {
            return (whole, TrackSelection.All);
        }

        (string Path, TrackSelection Tracks)? fallback = null;
        for (int colon = text.IndexOf(':', StringComparison.Ordinal); colon >= 0; colon = text.IndexOf(':', colon + 1))
        {
            if (colon == 0)
            {
                continue;
            }

            TrackSelection tracks;
            try
            {
                tracks = TrackSelection.Parse([text[(colon + 1)..]], false, false);
            }
            catch (UsageException)
            {
                continue;
            }

            string path = session.ResolvePath(text[..colon]);
            if (File.Exists(path))
            {
                return (path, tracks);
            }

            fallback ??= (path, tracks);
        }

        return fallback ?? (whole, TrackSelection.All);
    }

    private static List<string> ExtractTracks(VideoSession session, InputFile input, OutputTemplate output, TrackSelection selection, string? format, int count)
    {
        IDemuxer? first = MediaIO.OpenDemuxer(session, input);
        try
        {
            IReadOnlyList<TrackInfo> tracks = selection.Select(first.Tracks, input.Display);
            if (tracks.Count == 0)
            {
                throw new UsageException($"{input.Display}: no tracks left to extract.");
            }

            bool several = tracks.Count > 1;
            if (several && !selection.IsExplicit)
            {
                throw new UsageException($"{input.Display} has {TrackSelection.Describe(first.Tracks)}: give --track to pick one, such as --track a:0, or several with a folder or a template with {{track}}.");
            }

            bool folder = IsFolder(session, output);
            if (several && !folder && !output.NamesFrames)
            {
                throw new UsageException($"--track picks {tracks.Count} tracks of {input.Display}; give a folder or a template with {{track}}, such as out/{{name}}-{{track}}.{{format}}.");
            }

            if (several && input.IsStdin)
            {
                throw new UsageException("Standard input can be read once: extract one track from it per run.");
            }

            List<string> written = [];
            foreach (TrackInfo track in tracks)
            {
                MediaTarget target = MediaIO.Resolve(session, input, output, format, DefaultExtension(session, track), several || output.NamesFrames ? track.Number : 0);
                IDemuxer demuxer = first ?? MediaIO.OpenDemuxer(session, input);
                first = null;
                using (demuxer)
                {
                    MediaInfo info = demuxer.Info;
                    bool chapters = target.Container is MatroskaFormat or Mp4Format || (target.Container is WavFormat && !target.IsStdout);
                    MediaMetadata metadata = new() { Tags = info.Tags, Chapters = chapters ? info.Chapters : [] };
                    TrackCopy.Write(
                        session,
                        [new TrackSource(demuxer, new Dictionary<int, int> { [track.Number] = track.Number })],
                        target,
                        [track],
                        metadata,
                        session.ProgressSink?.For(input, input.Index, count),
                        track.Duration ?? info.Duration);
                }

                written.Add(target.Path);
            }

            return written;
        }
        finally
        {
            first?.Dispose();
        }
    }

    private static List<string> ExtractAttachments(VideoSession session, InputFile input, OutputTemplate output, string[] names, bool cover)
    {
        MediaInfo info = MediaIO.Probe(session, input);
        List<AttachmentInfo> picked = [];
        foreach (string name in names.SelectMany(n => n.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            List<AttachmentInfo> found = [.. info.Attachments.Where(a => name.Equals("all", StringComparison.OrdinalIgnoreCase) || a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))];
            if (found.Count == 0 && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < info.Attachments.Count)
            {
                found.Add(info.Attachments[index]);
            }

            if (found.Count == 0)
            {
                throw new UsageException($"{input.Display}: --attachment {name} matches no attachment; it has {Attachments(info)}.");
            }

            picked.AddRange(found.Where(a => !picked.Contains(a)));
        }

        if (cover)
        {
            AttachmentInfo art = info.Attachments.FirstOrDefault(a => a.IsCoverArt)
                ?? info.Attachments.FirstOrDefault(a => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"no cover art; it has attachments {Attachments(info)}.");
            if (!picked.Contains(art))
            {
                picked.Add(art);
            }
        }

        bool folder = IsFolder(session, output);
        if (picked.Count > 1 && !folder && !output.NamesFrames)
        {
            throw new UsageException($"{input.Display}: {picked.Count} attachments would all be written to '{output.Text}'; give a folder, or a template with {{track}} for the attachment's index.");
        }

        List<string> written = [];
        foreach (AttachmentInfo file in picked)
        {
            int index = IndexOf(info.Attachments, file);
            string safe = Path.GetFileName(file.Name.Replace('\\', '/'));
            if (safe.Length == 0 || safe is "." or "..")
            {
                safe = $"attachment-{index}";
            }

            string path = output.IsStdout ? "-"
                : folder ? Path.Combine(session.ResolvePath(output.Resolve(input, string.Empty)), safe)
                : session.ResolvePath(output.Resolve(input, Path.GetExtension(safe).TrimStart('.'), index));
            if (!session.InPlace && input.Path is { } source && path != "-" && Batch.SamePath(path, source))
            {
                throw new UsageException($"{input.Display}: the output would replace the input; give another -o, or --in-place to replace it.");
            }

            session.Cancellation.ThrowIfCancellationRequested();
            MediaIO.Write(session, path, stream => stream.Write(file.Data.Span));
            written.Add(path);
        }

        return written;
    }

    private static bool IsFolder(VideoSession session, OutputTemplate output) =>
        !output.IsStdout && (output.Text.EndsWith('/') || output.Text.EndsWith('\\') || (!output.HasPlaceholders && Directory.Exists(session.ResolvePath(output.Text))));

    private static int IndexOf(IReadOnlyList<AttachmentInfo> attachments, AttachmentInfo file)
    {
        for (int i = 0; i < attachments.Count; i++)
        {
            if (ReferenceEquals(attachments[i], file))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Attachments(MediaInfo info) => info.Attachments.Count == 0
        ? "none"
        : string.Join(", ", info.Attachments.Select((a, i) => $"{i} ({a.Name}, {a.MediaType})"));

    private static void RunEmbed(VideoSession session, InputFile input, MediaTarget target, TrackSelection selection, EmbedRequest request, int count)
    {
        foreach ((string path, _) in request.Adds)
        {
            if (!target.IsStdout && Batch.SamePath(target.Path, path))
            {
                throw new UsageException($"The output would replace '{path}', which --add reads; give another -o.");
            }
        }

        List<IDemuxer> demuxers = [];
        try
        {
            demuxers.Add(MediaIO.OpenDemuxer(session, input));
            foreach ((string path, _) in request.Adds)
            {
                demuxers.Add(MediaIO.OpenDemuxer(session, new InputFile(path, path, string.Empty, 0)));
            }

            List<TrackInfo> tracks = [];
            List<Dictionary<int, int>> numbers = [];
            for (int i = 0; i < demuxers.Count; i++)
            {
                string display = i == 0 ? input.Display : request.Adds[i - 1].Path;
                TrackSelection pick = i == 0 ? selection : request.Adds[i - 1].Tracks;
                Dictionary<int, int> map = [];
                foreach (TrackInfo track in pick.Select(demuxers[i].Tracks, display))
                {
                    tracks.Add(track with { Number = tracks.Count + 1 });
                    map[track.Number] = tracks.Count;
                }

                numbers.Add(map);
            }

            if (tracks.Count == 0)
            {
                throw new UsageException($"{input.Display}: no tracks left to write.");
            }

            foreach ((string key, string value) in request.Languages)
            {
                Edit(tracks, key, "--language", t => t with { Language = value.Length == 0 ? null : value });
            }

            foreach ((string key, string value) in request.Names)
            {
                Edit(tracks, key, "--name", t => t with { Name = value.Length == 0 ? null : value });
            }

            foreach (string key in request.Defaults)
            {
                foreach (TrackInfo chosen in Find(tracks, key, "--default"))
                {
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        if (tracks[i].Kind == chosen.Kind)
                        {
                            tracks[i] = tracks[i] with { IsDefault = tracks[i].Number == chosen.Number };
                        }
                    }
                }
            }

            MediaInfo info = demuxers[0].Info;
            List<AttachmentInfo> added = [.. request.Attach.Select(path => new AttachmentInfo(Path.GetFileName(path), MediaType(path), File.ReadAllBytes(path)))];
            List<AttachmentInfo> attachments = [.. info.Attachments];
            if (request.Cover is { } cover)
            {
                string type = MediaType(cover);
                if (type is not ("image/jpeg" or "image/png" or "image/webp"))
                {
                    throw new UsageException($"--cover takes a JPEG, PNG or WebP image; '{cover}' is not one.");
                }

                attachments.RemoveAll(a => a.IsCoverArt);
                added.Add(new AttachmentInfo("cover" + Path.GetExtension(cover).ToLowerInvariant(), type, File.ReadAllBytes(cover)));
            }

            if (added.Count > 0 && AttachmentProblem(target.Container, added) is { } problem)
            {
                throw new UsageException($"{input.Display}: {problem}");
            }

            attachments.AddRange(added);
            MediaMetadata metadata = info.Metadata with { Attachments = attachments };
            MediaTime? duration = demuxers.Select(d => d.Info.Duration).OfType<MediaTime>().OrderByDescending(d => d.Seconds).Cast<MediaTime?>().FirstOrDefault();
            TrackCopy.Write(
                session,
                [.. demuxers.Select((d, i) => new TrackSource(d, numbers[i]))],
                target,
                tracks,
                metadata,
                session.ProgressSink?.For(input, input.Index, count),
                duration);
        }
        finally
        {
            foreach (IDemuxer demuxer in demuxers)
            {
                demuxer.Dispose();
            }
        }
    }

    private static void Edit(List<TrackInfo> tracks, string key, string option, Func<TrackInfo, TrackInfo> change)
    {
        foreach (TrackInfo track in Find(tracks, key, option))
        {
            int at = tracks.FindIndex(t => t.Number == track.Number);
            tracks[at] = change(tracks[at]);
        }
    }

    /// <summary>The output tracks a <c>--language</c>, <c>--name</c> or <c>--default</c> key names: a number from 1, or a track spec.</summary>
    private static IReadOnlyList<TrackInfo> Find(List<TrackInfo> tracks, string key, string option)
    {
        try
        {
            return TrackSelection.Parse([key], false, false).Select(tracks, "the output");
        }
        catch (UsageException error)
        {
            throw new UsageException($"{option} {key}: {error.Message}");
        }
    }

    private static (string Track, string Value) Pair(string text, string option)
    {
        int equals = text.IndexOf('=', StringComparison.Ordinal);
        return equals > 0
            ? (text[..equals].Trim(), text[(equals + 1)..])
            : throw new UsageException($"{option} '{text}' needs a track and a value: track=value, such as 2=eng or a:0=eng.");
    }

    /// <summary>What <c>embed</c> adds and sets, the same for every input.</summary>
    private sealed record EmbedRequest(
        IReadOnlyList<(string Path, TrackSelection Tracks)> Adds,
        IReadOnlyList<string> Attach,
        string? Cover,
        IReadOnlyList<(string Track, string Value)> Languages,
        IReadOnlyList<(string Track, string Value)> Names,
        IReadOnlyList<string> Defaults);
}
