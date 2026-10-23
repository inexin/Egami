using Egami.Video.Containers;

namespace Egami.Video.Cli.Host;

/// <summary>An output a command writes: its full path (or <c>-</c>) and its container.</summary>
/// <param name="Path">The full path, or <c>-</c> for standard output.</param>
/// <param name="Container">The container to write.</param>
internal sealed record MediaTarget(string Path, ContainerFormat Container)
{
    /// <summary>Gets a value indicating whether the output is standard output.</summary>
    public bool IsStdout => Path == "-";
}

/// <summary>
/// Reading inputs and writing outputs: files, standard input (read as it comes, so a pipe needs no temporary file) and standard
/// output (written as it goes), with file outputs written through a temporary name that replaces the target only once the job
/// is complete, so a failed or cancelled job leaves no partial file.
/// </summary>
internal static class MediaIO
{
    /// <summary>Opens an input's bytes: the file, or standard input.</summary>
    public static Stream OpenStream(VideoSession session, InputFile input)
    {
        return input.Path is { } path
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan)
            : session.Console.In();
    }

    /// <summary>Opens an input for reading packets.</summary>
    public static IDemuxer OpenDemuxer(VideoSession session, InputFile input)
    {
        Stream stream = OpenStream(session, input);
        try
        {
            return MediaFile.OpenDemuxer(stream, session.Context, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads an input's headers.</summary>
    public static MediaInfo Probe(VideoSession session, InputFile input)
    {
        using IDemuxer demuxer = OpenDemuxer(session, input);
        return demuxer.Info;
    }

    /// <summary>Finds a container by name or extension; <paramref name="write"/> asks for one that can write.</summary>
    public static ContainerFormat FindContainer(VideoSession session, string nameOrExtension, bool write = true)
    {
        string key = nameOrExtension.TrimStart('.').ToLowerInvariant();
        ContainerFormat format = key switch
        {
            "matroska" or "mkv" or "mka" or "mks" => MatroskaFormat.Instance,
            "yuv4mpegpipe" or "yuv4mpeg" or "yuv4mpeg2" => Y4mFormat.Instance,
            "mov" or "m4v" or "m4a" or "mp4" or "quicktime" => Mp4Format.Instance,
            "w64" => WavFormat.Wave64,
            "obu" => Av1ObuFormat.Instance,
            "oga" or "opus" or "ogv" => OggFormat.Instance,
            _ => session.Context.FindContainer(key)
                ?? throw new UsageException($"'{nameOrExtension}' is not a container egami-video knows; 'egami-video formats' lists them."),
        };
        return !write || format.CanMux
            ? format
            : throw new UsageException($"egami-video can read {format.Name} but not write it; 'egami-video formats' lists what it writes.");
    }

    /// <summary>
    /// Where an input's output goes and in which container: <paramref name="template"/> resolved for the input (a folder takes
    /// the input's name with <paramref name="defaultExtension"/>), the container from <c>--format</c> or the extension.
    /// </summary>
    public static MediaTarget Resolve(VideoSession session, InputFile input, OutputTemplate template, string? format, string defaultExtension, int part = 0)
    {
        ContainerFormat? container = format is null ? null : FindContainer(session, format);
        if (template.IsStdout)
        {
            return container is null
                ? throw new UsageException("-o - writes to standard output: give --format too, such as --format mkv (mkv, webm, ivf, y4m, wav, flac, ogg or mp4).")
                : new MediaTarget("-", container);
        }

        string text = template.Text;
        bool folder = text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
        string extension = container is not null ? container.Extensions[0].TrimStart('.') : defaultExtension;
        string path = folder
            ? Path.Combine(session.ResolvePath(text), $"{input.Name}{(part > 0 || template.NamesFrames ? $"-{part:000}" : string.Empty)}.{extension}")
            : session.ResolvePath(template.Resolve(input, extension, part));
        if (container is null)
        {
            string ext = Path.GetExtension(path);
            container = ext.Length > 1
                ? FindContainer(session, ext)
                : throw new UsageException($"The output '{path}' has no extension; give --format.");
        }

        if (!session.InPlace && input.Path is { } source && Batch.SamePath(path, source))
        {
            throw new UsageException($"{input.Display}: the output would replace the input; give another -o, or --in-place to replace it.");
        }

        return new MediaTarget(path, container);
    }

    /// <summary>
    /// Writes an output: straight to standard output, or to a file through a temporary name in the same folder that replaces
    /// the target once <paramref name="write"/> returns. When it throws (a failure, or Ctrl+C) the temporary file is deleted.
    /// </summary>
    public static void Write(VideoSession session, string path, Action<Stream> write)
    {
        if (path == "-")
        {
            Stream stdout = session.Console.Out();
            write(stdout);
            stdout.Flush();
            return;
        }

        session.Paths?.Check(Path.GetFullPath(path, session.WorkingDirectory));
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = Path.Combine(folder ?? string.Empty, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.egami-video-tmp");
        try
        {
            using (FileStream file = new(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                write(file);
            }

            File.Move(temporary, path, overwrite: true);
            session.Written.Enqueue(path);
            session.Info($"wrote {path}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Opens a muxer on a stream; the stream stays open.</summary>
    public static IMuxer CreateMuxer(VideoSession session, Stream stream, ContainerFormat container, IReadOnlyList<TrackInfo> tracks, MediaMetadata? metadata = null)
    {
        if (!stream.CanSeek && container is AviFormat)
        {
            throw new UsageException("AVI needs a file it can seek in; write mkv, webm, ivf or y4m to standard output.");
        }

        return container.CreateMuxer(stream, tracks, metadata ?? MediaMetadata.Empty, session.Context, leaveOpen: true);
    }

    /// <summary>
    /// The timestamps of a video track's frames in presentation order, for <c>#n</c> times: from the packets, read through
    /// once. Returns an empty list when the input has no video.
    /// </summary>
    public static IReadOnlyList<MediaTime> FrameTimes(VideoSession session, InputFile input, int? trackNumber = null)
    {
        using IDemuxer demuxer = OpenDemuxer(session, input);
        int? number = trackNumber ?? demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Number;
        if (number is null)
        {
            return [];
        }

        List<MediaTime> times = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == number && !packet.IsDiscardable)
                {
                    times.Add(packet.Timestamp);
                }
            }
        }

        times.Sort();
        return times;
    }
}
