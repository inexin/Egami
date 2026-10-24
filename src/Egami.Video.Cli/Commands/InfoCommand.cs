using System.CommandLine;
using System.Globalization;

using Egami.Colors;
using Egami.Video.Audio;
using Egami.Video.Cli.Host;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video info</c> (Video CLI design Q6, Build Plan tasks 4 and 17): what media files hold, from their headers; with
/// <c>--all</c> packet counts and bit rates, <c>--headers</c> the codec headers of each track's first frame, <c>--packets</c>
/// every packet and <c>--frames</c> every decoded frame. Text by default; JSON with Egami's schema, CSV, XML with the same
/// fields as JSON, or JSON with ffprobe's field names.
/// </summary>
internal static class InfoCommand
{
    /// <summary>The version of the <c>info --format json</c> schema (docs/schemas/egami-video-info-1.json).</summary>
    public const int Schema = 1;

    private static readonly Dictionary<string, string> XmlItems = new(StringComparer.Ordinal)
    {
        ["files"] = "file",
        ["tracks"] = "track",
        ["chapters"] = "chapter",
        ["attachments"] = "attachment",
        ["packets"] = "packet",
        ["frames"] = "frame",
        ["headers"] = "header",
    };

    public static Command Create(VideoSession session)
    {
        MediaOptions options = new();
        Option<bool> all = new("--all", "-a") { Description = "Also read every packet: packet and keyframe counts, start times, sizes and bit rates per track." };
        Option<bool> headers = new("--headers") { Description = "Also show the codec headers of each track's first frame (profile, size, color and so on)." };
        Option<bool> packets = new("--packets") { Description = "List every packet: track, timestamps, duration, size and flags." };
        Option<bool> frames = new("--frames") { Description = "Decode and list every frame: track, timestamp, duration, size or samples." };
        Option<string?> format = new("--format", "-f") { Description = "How to write: text (the default), json, csv (packet and frame lists), xml (the json fields), or ffprobe (JSON with ffprobe's field names).", HelpName = "text|json|csv|xml|ffprobe" };
        Option<bool> json = new("--json") { Description = "Write JSON (schema 1); the same as --format json." };
        Option<bool> csv = new("--csv") { Description = "Write CSV; the same as --format csv." };
        Command command = new("info", "Show what media files hold: container, duration, tracks and codecs, tags, chapters and attachments; with options, packets and frames. Like ffprobe.");
        options.AddTo(command, write: false);
        command.Options.Add(all);
        command.Options.Add(headers);
        command.Options.Add(packets);
        command.Options.Add(frames);
        command.Options.Add(format);
        command.Options.Add(json);
        command.Options.Add(csv);
        command.SetAction(result =>
        {
            string style = (result.GetValue(json), result.GetValue(csv), result.GetValue(format)) switch
            {
                (true, true, _) => throw new UsageException("Give --json or --csv, not both."),
                (true, _, { } f) when f != "json" => throw new UsageException($"--json and --format {f} disagree."),
                (_, true, { } f) when f != "csv" => throw new UsageException($"--csv and --format {f} disagree."),
                (true, _, _) => "json",
                (_, true, _) => "csv",
                (_, _, { } f) => f.ToLowerInvariant(),
                _ => "text",
            };
            if (style is not ("text" or "json" or "csv" or "xml" or "ffprobe"))
            {
                throw new UsageException($"--format '{style}' is not text, json, csv, xml or ffprobe.");
            }

            InfoRequest request = new(
                result.GetValue(all) || style == "ffprobe",
                result.GetValue(headers),
                result.GetValue(packets),
                result.GetValue(frames),
                options.Tracks(result));
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            InfoData?[] found = new InfoData?[inputs.Count];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                found[input.Index] = Gather(session, input, request);
                return [];
            });

            List<InfoData> done = [.. found.OfType<InfoData>()];
            Write(session.Console.Text, style, done, request);
            return code;
        });
        return command;
    }

    /// <summary>What to gather beyond the headers.</summary>
    internal sealed record InfoRequest(bool Scan, bool Headers, bool Packets, bool Frames, TrackSelection Tracks);

    /// <summary>One packet as <c>--packets</c> lists it.</summary>
    internal sealed record PacketRow(TrackInfo Track, int Index, long Pts, long? Dts, long? Duration, int Size, bool Keyframe, bool Discardable);

    /// <summary>One decoded frame as <c>--frames</c> lists it.</summary>
    internal sealed record FrameRow(TrackInfo Track, int Index, MediaTime Timestamp, MediaTime? Duration, int Width, int Height, int Samples, int SampleRate, int Channels, string? SampleFormat, bool? Keyframe);

    /// <summary>Per-track figures from reading every packet.</summary>
    internal sealed class TrackStats
    {
        public long Packets { get; set; }

        public long Keyframes { get; set; }

        public long Bytes { get; set; }

        public MediaTime? Start { get; set; }

        public MediaTime? End { get; set; }
    }

    /// <summary>Everything gathered for one input.</summary>
    internal sealed record InfoData(InputFile File, MediaInfo Info, long? Size, IReadOnlyList<TrackInfo> Tracks)
    {
        public Dictionary<int, TrackStats> Stats { get; } = [];

        public Dictionary<int, IReadOnlyList<CodecHeaderField>> Headers { get; } = [];

        public List<PacketRow> PacketRows { get; } = [];

        public List<FrameRow> FrameRows { get; } = [];
    }

    /// <summary>Reads an input's headers and, as asked, its packets and frames.</summary>
    internal static InfoData Gather(VideoSession session, InputFile input, InfoRequest request)
    {
        // Standard input is read once: a request that decodes frames keeps it in memory to read it again.
        byte[]? stdin = null;
        if (input.IsStdin && request.Frames)
        {
            using Stream stream = session.Console.In();
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            stdin = buffer.ToArray();
        }

        Stream Open() => stdin is not null ? new MemoryStream(stdin, writable: false) : MediaIO.OpenStream(session, input);
        long? size = input.Path is { } path ? new FileInfo(path).Length : stdin?.LongLength;

        using IDemuxer demuxer = MediaFile.OpenDemuxer(Open(), session.Context);
        MediaInfo info = demuxer.Info;
        IReadOnlyList<TrackInfo> tracks = request.Tracks.Select(demuxer.Tracks, input.Display);
        InfoData data = new(input, info, size, tracks);
        if (request.Scan || request.Headers || request.Packets)
        {
            Dictionary<int, TrackInfo> byNumber = tracks.ToDictionary(t => t.Number);
            Dictionary<int, CodecHeaderReader> readers = [];
            try
            {
                foreach (TrackInfo track in tracks)
                {
                    data.Stats[track.Number] = new TrackStats();
                    if (request.Headers && CodecHeaders.CanDescribe(track))
                    {
                        readers[track.Number] = new CodecHeaderReader(track, session.Context);
                    }
                }

                Dictionary<int, int> indexes = tracks.Select((t, i) => (t.Number, i)).ToDictionary(p => p.Number, p => p.i);
                while (demuxer.ReadPacket() is { } packet)
                {
                    using (packet)
                    {
                        session.Cancellation.ThrowIfCancellationRequested();
                        if (!byNumber.TryGetValue(packet.TrackNumber, out TrackInfo? track))
                        {
                            continue;
                        }

                        TrackStats stats = data.Stats[track.Number];
                        stats.Packets++;
                        stats.Bytes += packet.Data.Length;
                        if (packet.IsKeyframe)
                        {
                            stats.Keyframes++;
                        }

                        if (stats.Start is null || packet.Timestamp < stats.Start.Value)
                        {
                            stats.Start = packet.Timestamp;
                        }

                        MediaTime end = packet.Duration is { } d ? packet.Timestamp + d : packet.Timestamp;
                        if (stats.End is null || end > stats.End.Value)
                        {
                            stats.End = end;
                        }

                        if (readers.TryGetValue(track.Number, out CodecHeaderReader? reader) && !data.Headers.ContainsKey(track.Number))
                        {
                            IReadOnlyList<CodecHeaderField> fields = reader.Describe(packet);
                            if (fields.Count > 0)
                            {
                                data.Headers[track.Number] = fields;
                            }
                        }

                        if (request.Packets)
                        {
                            Rational timeBase = track.TimeBase;
                            data.PacketRows.Add(new PacketRow(
                                track,
                                indexes[track.Number],
                                packet.Timestamp.Rescale(timeBase).Ticks,
                                packet.DecodeTimestamp?.Rescale(timeBase).Ticks,
                                packet.Duration?.Rescale(timeBase).Ticks,
                                packet.Data.Length,
                                packet.IsKeyframe,
                                packet.IsDiscardable));
                        }
                    }

                    if (!request.Scan && !request.Packets && data.Headers.Count == readers.Count)
                    {
                        break;
                    }
                }
            }
            finally
            {
                foreach (CodecHeaderReader reader in readers.Values)
                {
                    reader.Dispose();
                }
            }
        }

        if (request.Frames)
        {
            int index = 0;
            foreach (TrackInfo track in tracks)
            {
                if (track is VideoTrackInfo video && session.Context.FindCodec(video) is not null)
                {
                    using VideoReader reader = VideoReader.Open(Open(), new VideoReaderOptions { TrackNumber = video.Number }, session.Context);
                    while (reader.ReadFrame() is { } frame)
                    {
                        using (frame)
                        {
                            session.Cancellation.ThrowIfCancellationRequested();
                            data.FrameRows.Add(new FrameRow(track, index, frame.Timestamp, frame.Duration, frame.Width, frame.Height, 0, 0, 0, null, null));
                        }
                    }
                }
                else if (track is AudioTrackInfo audio && session.Context.FindAudioCodec(audio) is not null)
                {
                    using AudioReader reader = AudioReader.Open(Open(), new AudioReaderOptions { TrackNumber = audio.Number, SampleFormat = null, KeepAllSamples = true }, session.Context);
                    while (reader.TryRead(out AudioFrame? frame))
                    {
                        using (frame)
                        {
                            session.Cancellation.ThrowIfCancellationRequested();
                            data.FrameRows.Add(new FrameRow(track, index, frame.Timestamp, frame.Duration, 0, 0, frame.SampleCount, frame.SampleRate, frame.ChannelCount, SampleFormatName(frame.Format), true));
                        }
                    }
                }

                index++;
            }
        }

        return data;
    }

    /// <summary>Writes the gathered files in the style asked for.</summary>
    internal static void Write(TextWriter output, string style, IReadOnlyList<InfoData> files, InfoRequest request)
    {
        switch (style)
        {
            case "json":
                ReportWriter.Json(output, Report(files, request));
                break;
            case "xml":
                ReportWriter.Xml(output, "egamiVideoInfo", Report(files, request), XmlItems);
                break;
            case "ffprobe":
                ReportWriter.Json(output, FfprobeReport.Build(files, request));
                break;
            case "csv":
                ReportWriter.Csv(output, CsvRows(files, request));
                break;
            default:
                foreach (InfoData file in files)
                {
                    WriteText(output, file, request);
                }

                break;
        }
    }

    /// <summary>The report in Egami's own schema.</summary>
    internal static ReportObject Report(IReadOnlyList<InfoData> files, InfoRequest request)
    {
        ReportObject root = new();
        root.Add("schema", Schema);
        List<ReportObject> list = root.List("files");
        foreach (InfoData file in files)
        {
            list.Add(FileReport(file, request));
        }

        return root;
    }

    private static ReportObject FileReport(InfoData data, InfoRequest request)
    {
        MediaInfo info = data.Info;
        ReportObject file = new();
        file.Add("path", data.File.Display);
        file.Add("format", info.FormatName);
        file.Add("duration", info.Duration?.Seconds);
        file.Add("size", data.Size);
        file.Add("bitRate", info.Duration is { Seconds: > 0 } d && data.Size is { } size ? (long)Math.Round(size * 8 / d.Seconds) : null);
        file.Add("timecode", info.Timecode);
        file.Add("tags", info.Tags);
        List<ReportObject> tracks = file.List("tracks");
        int index = 0;
        foreach (TrackInfo track in info.Tracks)
        {
            if (data.Tracks.Contains(track))
            {
                tracks.Add(TrackReport(track, index, data));
            }

            index++;
        }

        List<ReportObject> chapters = file.List("chapters");
        foreach (ChapterInfo chapter in info.Chapters)
        {
            chapters.Add(new ReportObject()
                .Add("start", chapter.Start.Seconds)
                .Add("end", chapter.End?.Seconds)
                .Add("title", chapter.Title)
                .Add("language", chapter.Language));
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

        if (request.Packets)
        {
            List<ReportObject> packets = file.List("packets");
            foreach (PacketRow packet in data.PacketRows)
            {
                packets.Add(PacketReport(packet));
            }
        }

        if (request.Frames)
        {
            List<ReportObject> frames = file.List("frames");
            foreach (FrameRow frame in data.FrameRows)
            {
                frames.Add(FrameReport(frame));
            }
        }

        return file;
    }

    private static ReportObject TrackReport(TrackInfo track, int index, InfoData data)
    {
        ReportObject report = new();
        report.Add("number", track.Number);
        report.Add("index", index);
        report.Add("kind", Kinds.Name(track.Kind));
        report.Add("codec", CodecNames.Name(track.CodecId));
        report.Add("codecId", track.CodecId);
        report.Add("codecPrivateSize", track.CodecPrivate.Length);
        report.Add("timeBase", Ratio(track.TimeBase));
        report.Add("duration", track.Duration?.Seconds);
        report.Add("language", track.Language);
        report.Add("name", track.Name);
        report.Add("default", track.IsDefault);
        report.Add("codecDelay", track.CodecDelay?.Seconds);
        report.Add("tags", track.Tags);
        if (track is VideoTrackInfo video)
        {
            report.Add("width", video.Width);
            report.Add("height", video.Height);
            report.Add("frameRate", video.FrameRate is { } rate ? Ratio(rate) : null);
            report.Add("pixelAspectRatio", Ratio(video.PixelAspectRatio));
            report.Add("bitDepth", video.BitDepth > 0 ? video.BitDepth : null);
            report.Add("chroma", video.ChromaFormat is { } chroma ? Chroma(chroma) : null);
            report.Add("alpha", video.HasAlpha);
            report.Add("fieldOrder", FieldOrderName(video.FieldOrder));
            report.Add("orientation", video.Orientation.ToString());
            report.Add("fourcc", video.FourCC);
            report.Add("color", video.Color is { } color ? ColorReport(color) : null);
            report.Add("hdr", video.Hdr is not null);
        }
        else if (track is AudioTrackInfo audio)
        {
            report.Add("sampleRate", track.CodecId == CodecIds.Opus ? 48000 : audio.SampleRate);
            report.Add("sourceSampleRate", track.CodecId == CodecIds.Opus ? audio.SampleRate : null);
            report.Add("channels", audio.Channels);
            report.Add("layout", AudioLayout(audio));
            report.Add("sampleFormat", TrackSampleFormat(audio));
            report.Add("bitsPerSample", audio.BitsPerSample > 0 && IsLossless(track.CodecId) ? audio.BitsPerSample : null);
            report.Add("seekPreRoll", audio.SeekPreRoll?.Seconds);
        }

        if (data.Stats.TryGetValue(track.Number, out TrackStats? stats))
        {
            report.Add("packets", stats.Packets);
            report.Add("keyframes", stats.Keyframes);
            report.Add("bytes", stats.Bytes);
            report.Add("startTime", stats.Start?.Seconds);
            double? seconds = stats.End is { } end && stats.Start is { } start ? (end - start).Seconds : null;
            report.Add("bitRate", seconds is > 0 ? (long)Math.Round(stats.Bytes * 8 / seconds.Value) : null);
        }

        if (data.Headers.TryGetValue(track.Number, out IReadOnlyList<CodecHeaderField>? headers))
        {
            List<ReportObject> list = report.List("headers");
            foreach (CodecHeaderField field in headers)
            {
                list.Add(new ReportObject().Add("name", field.Name).Add("value", field.Value));
            }
        }

        return report;
    }

    private static ReportObject PacketReport(PacketRow packet)
    {
        double tb = packet.Track.TimeBase.ToDouble();
        return new ReportObject()
            .Add("track", packet.Track.Number)
            .Add("kind", Kinds.Name(packet.Track.Kind))
            .Add("pts", packet.Pts)
            .Add("ptsTime", packet.Pts * tb)
            .Add("dts", packet.Dts ?? packet.Pts)
            .Add("dtsTime", (packet.Dts ?? packet.Pts) * tb)
            .Add("duration", packet.Duration)
            .Add("durationTime", packet.Duration * tb)
            .Add("size", packet.Size)
            .Add("keyframe", packet.Keyframe)
            .Add("discardable", packet.Discardable);
    }

    private static ReportObject FrameReport(FrameRow frame)
    {
        ReportObject report = new ReportObject()
            .Add("track", frame.Track.Number)
            .Add("kind", Kinds.Name(frame.Track.Kind))
            .Add("time", frame.Timestamp.Seconds)
            .Add("duration", frame.Duration?.Seconds);
        if (frame.Track.Kind == TrackKind.Video)
        {
            report.Add("width", frame.Width).Add("height", frame.Height);
        }
        else
        {
            report.Add("samples", frame.Samples).Add("sampleRate", frame.SampleRate).Add("channels", frame.Channels).Add("sampleFormat", frame.SampleFormat);
        }

        return report;
    }

    private static List<ReportObject> CsvRows(IReadOnlyList<InfoData> files, InfoRequest request)
    {
        List<ReportObject> rows = [];
        foreach (InfoData file in files)
        {
            IEnumerable<ReportObject> items = request.Packets ? file.PacketRows.Select(PacketReport)
                : request.Frames ? file.FrameRows.Select(FrameReport)
                : file.Info.Tracks.Where(file.Tracks.Contains).Select((t, i) => TrackReport(t, file.Info.Tracks.ToList().IndexOf(t), file));
            foreach (ReportObject item in items)
            {
                ReportObject row = new();
                row.Add("path", file.File.Display);
                foreach ((string name, object? value) in item.Fields)
                {
                    if (value is not IReadOnlyDictionary<string, string> and not ReportObject and not List<ReportObject>)
                    {
                        row.Add(name, value);
                    }
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    private static void WriteText(TextWriter output, InfoData data, InfoRequest request)
    {
        MediaInfo info = data.Info;
        string duration = info.Duration is { } d ? TimeValue.Format(d) : "unknown duration";
        string size = data.Size is { } bytes ? $", {Bytes(bytes)}" : string.Empty;
        string rate = info.Duration is { Seconds: > 0 } dd && data.Size is { } s ? $", {Kbps(s * 8 / dd.Seconds)}" : string.Empty;
        output.WriteLine($"{data.File.Display}: {info.FormatName}, {duration}{size}{rate}");
        if (info.Timecode is { } timecode)
        {
            output.WriteLine($"  timecode {timecode}");
        }

        int index = 0;
        foreach (TrackInfo track in info.Tracks)
        {
            if (data.Tracks.Contains(track))
            {
                output.WriteLine($"  {TrackLine(track, index, data)}");
                if (data.Headers.TryGetValue(track.Number, out IReadOnlyList<CodecHeaderField>? headers))
                {
                    output.WriteLine($"      headers: {string.Join(", ", headers.Select(h => $"{h.Name}={h.Value.ToString(CultureInfo.InvariantCulture)}"))}");
                }

                if (request.Scan)
                {
                    foreach ((string key, string value) in track.Tags)
                    {
                        output.WriteLine($"      {key}: {value}");
                    }
                }
            }

            index++;
        }

        foreach ((string key, string value) in info.Tags)
        {
            output.WriteLine($"  {key}: {value}");
        }

        foreach (ChapterInfo chapter in info.Chapters)
        {
            string end = chapter.End is { } e ? $" to {TimeValue.Format(e)}" : string.Empty;
            output.WriteLine($"  chapter {TimeValue.Format(chapter.Start)}{end}{(chapter.Title is { } title ? ": " + title : string.Empty)}");
        }

        foreach (AttachmentInfo attachment in info.Attachments)
        {
            output.WriteLine($"  attachment {attachment.Name} ({attachment.MediaType}, {Bytes(attachment.Data.Length)}){(attachment.IsCoverArt ? ", cover art" : string.Empty)}");
        }

        if (request.Packets)
        {
            output.WriteLine("  packets: track pts dts duration size flags");
            foreach (PacketRow packet in data.PacketRows)
            {
                double tb = packet.Track.TimeBase.ToDouble();
                string dts = packet.Dts is { } dt ? Seconds(dt * tb) : "=";
                string dur = packet.Duration is { } du ? Seconds(du * tb) : "-";
                string flags = (packet.Keyframe ? "K" : "_") + (packet.Discardable ? "D" : "_");
                output.WriteLine($"    {packet.Track.Number} {Seconds(packet.Pts * tb)} {dts} {dur} {packet.Size} {flags}");
            }
        }

        if (request.Frames)
        {
            output.WriteLine("  frames: track time duration size");
            foreach (FrameRow frame in data.FrameRows)
            {
                string what = frame.Track.Kind == TrackKind.Video ? $"{frame.Width}x{frame.Height}" : $"{frame.Samples} samples";
                output.WriteLine($"    {frame.Track.Number} {Seconds(frame.Timestamp.Seconds)} {(frame.Duration is { } fd ? Seconds(fd.Seconds) : "-")} {what}");
            }
        }
    }

    /// <summary>One track as the text summary shows it: <c>#1 video: VP9 1920x1080 25 fps, yuv420 8-bit, eng, 812 packets</c>.</summary>
    private static string TrackLine(TrackInfo track, int index, InfoData data)
    {
        List<string> parts = [];
        if (track is VideoTrackInfo video)
        {
            string fps = video.FrameRate is { } rate ? $" {Fps(rate)} fps" : string.Empty;
            string aspect = video.PixelAspectRatio != new Rational(1, 1) ? $" (pixels {Ratio(video.PixelAspectRatio)})" : string.Empty;
            parts.Add($"{CodecNames.Name(track.CodecId)} {video.Width}x{video.Height}{fps}{aspect}");
            List<string> pixels = [];
            if (video.ChromaFormat is { } chroma)
            {
                pixels.Add(Chroma(chroma));
            }

            if (video.BitDepth > 0)
            {
                pixels.Add($"{video.BitDepth}-bit");
            }

            if (video.HasAlpha)
            {
                pixels.Add("alpha");
            }

            if (pixels.Count > 0)
            {
                parts.Add(string.Join(' ', pixels));
            }

            if (video.FieldOrder != FieldOrder.Progressive)
            {
                parts.Add(FieldOrderName(video.FieldOrder));
            }

            if (video.Hdr is not null)
            {
                parts.Add("HDR");
            }

            if (video.Orientation is not (Metadata.ImageOrientation.Normal or Metadata.ImageOrientation.Unknown))
            {
                parts.Add($"orientation {video.Orientation}");
            }
        }
        else if (track is AudioTrackInfo audio)
        {
            string bits = audio.BitsPerSample > 0 && IsLossless(track.CodecId) ? $" {audio.BitsPerSample}-bit" : string.Empty;
            int rate = track.CodecId == CodecIds.Opus ? 48000 : audio.SampleRate;
            parts.Add($"{CodecNames.Name(track.CodecId)} {rate} Hz {AudioLayout(audio)}{bits}");
        }
        else
        {
            parts.Add(CodecNames.Name(track.CodecId));
        }

        if (track.Language is { } language)
        {
            parts.Add(language);
        }

        if (track.Name is { } name)
        {
            parts.Add($"\"{name}\"");
        }

        if (!track.IsDefault)
        {
            parts.Add("not default");
        }

        if (track.Duration is { } duration)
        {
            parts.Add(TimeValue.Format(duration));
        }

        if (data.Stats.TryGetValue(track.Number, out TrackStats? stats))
        {
            string keys = track.Kind == TrackKind.Video ? $" ({stats.Keyframes} keyframes)" : string.Empty;
            parts.Add($"{stats.Packets} packets{keys}");
            if (stats.End is { } end && stats.Start is { } start && (end - start).Seconds > 0)
            {
                parts.Add(Kbps(stats.Bytes * 8 / (end - start).Seconds));
            }
        }

        return $"#{track.Number} {Kinds.Name(track.Kind)} ({Kinds.Letter(track.Kind)}:{IndexInKind(data.Info.Tracks, track)}): {string.Join(", ", parts)}";
    }

    private static bool IsLossless(string codecId) => codecId is CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat or CodecIds.Flac or CodecIds.Alac;

    private static int IndexInKind(IReadOnlyList<TrackInfo> tracks, TrackInfo track) => tracks.Where(t => t.Kind == track.Kind).ToList().IndexOf(track);

    internal static string Ratio(Rational value) => string.Create(CultureInfo.InvariantCulture, $"{value.Numerator}/{value.Denominator}");

    internal static string Fps(Rational rate) => (rate.ToDouble() is var fps && Math.Abs(fps - Math.Round(fps)) < 1e-9 ? Math.Round(fps) : Math.Round(fps, 3)).ToString(CultureInfo.InvariantCulture);

    internal static string Chroma(Pixels.ChromaFormat chroma) => chroma.ToString().ToLowerInvariant();

    internal static string FieldOrderName(FieldOrder order) => order switch
    {
        FieldOrder.TopFieldFirst => "top field first",
        FieldOrder.BottomFieldFirst => "bottom field first",
        _ => "progressive",
    };

    internal static string AudioLayout(AudioTrackInfo audio)
    {
        ChannelLayout layout = audio.Layout is { ChannelCount: > 0 } known && known.ChannelCount == audio.Channels
            ? known
            : ChannelLayout.ForChannelCount(Math.Clamp(audio.Channels, 1, ChannelLayout.MaxChannels));
        return layout.ToString();
    }

    /// <summary>The sample format a track's decoder gives, in ffprobe's names: s16, s32, flt, fltp, u8 and so on.</summary>
    internal static string? TrackSampleFormat(AudioTrackInfo audio)
    {
        int bits = audio.BitsPerSample;
        return audio.CodecId switch
        {
            CodecIds.PcmFloat => bits == 64 ? "dbl" : "flt",
            CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian => bits switch { 8 => "u8", <= 16 => "s16", <= 32 => "s32", _ => "s64" },
            CodecIds.Flac => bits is > 0 and <= 16 ? "s16" : "s32",
            CodecIds.Alac => bits is > 0 and <= 16 ? "s16p" : "s32p",
            CodecIds.G711MuLaw or CodecIds.G711ALaw => "s16",
            CodecIds.Opus or CodecIds.Vorbis or CodecIds.Aac or CodecIds.Ac3 => "fltp",
            CodecIds.Mp3 or CodecIds.Mp2 => "fltp",
            _ => null,
        };
    }

    internal static string SampleFormatName(SampleFormat format) => format switch
    {
        SampleFormat.U8 => "u8",
        SampleFormat.S16 => "s16",
        SampleFormat.S32 => "s32",
        SampleFormat.S64 => "s64",
        SampleFormat.F32 => "flt",
        SampleFormat.F64 => "dbl",
        SampleFormat.U8Planar => "u8p",
        SampleFormat.S16Planar => "s16p",
        SampleFormat.S32Planar => "s32p",
        SampleFormat.S64Planar => "s64p",
        SampleFormat.F32Planar => "fltp",
        _ => "dblp",
    };

    private static ReportObject ColorReport(ColorCodePoints color)
    {
        return new ReportObject()
            .Add("primaries", H273.Primaries(color.Primaries))
            .Add("transfer", H273.Transfer(color.TransferCharacteristics))
            .Add("matrix", H273.Matrix(color.MatrixCoefficients))
            .Add("range", color.FullRange ? "full" : "limited");
    }

    internal static string Bytes(long bytes)
    {
        return bytes >= 1 << 30 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 30):0.0} GiB")
            : bytes >= 1 << 20 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 20):0.0} MiB")
            : bytes >= 1 << 10 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KiB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
    }

    private static string Kbps(double bitsPerSecond) => string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1000:0} kb/s");

    private static string Seconds(double seconds) => seconds.ToString("0.000000", CultureInfo.InvariantCulture);
}

/// <summary>The names of ITU-T H.273 code points, as video tools commonly write them.</summary>
internal static class H273
{
    public static string Primaries(byte code) => code switch
    {
        1 => "bt709",
        2 => "unknown",
        4 => "bt470m",
        5 => "bt470bg",
        6 => "smpte170m",
        7 => "smpte240m",
        8 => "film",
        9 => "bt2020",
        10 => "smpte428",
        11 => "smpte431",
        12 => "smpte432",
        22 => "jedec-p22",
        _ => code.ToString(CultureInfo.InvariantCulture),
    };

    public static string Transfer(byte code) => code switch
    {
        1 => "bt709",
        2 => "unknown",
        4 => "gamma22",
        5 => "gamma28",
        6 => "smpte170m",
        7 => "smpte240m",
        8 => "linear",
        9 => "log100",
        10 => "log316",
        11 => "iec61966-2-4",
        12 => "bt1361e",
        13 => "iec61966-2-1",
        14 => "bt2020-10",
        15 => "bt2020-12",
        16 => "smpte2084",
        17 => "smpte428",
        18 => "arib-std-b67",
        _ => code.ToString(CultureInfo.InvariantCulture),
    };

    public static string Matrix(byte code) => code switch
    {
        0 => "gbr",
        1 => "bt709",
        2 => "unknown",
        4 => "fcc",
        5 => "bt470bg",
        6 => "smpte170m",
        7 => "smpte240m",
        8 => "ycgco",
        9 => "bt2020nc",
        10 => "bt2020c",
        11 => "smpte2085",
        12 => "chroma-derived-nc",
        13 => "chroma-derived-c",
        14 => "ictcp",
        _ => code.ToString(CultureInfo.InvariantCulture),
    };
}
