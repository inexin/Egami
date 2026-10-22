using Egami.Video.Containers;

namespace Egami.Video;

/// <summary>
/// Entry points for media files: probe what a file holds, open a demuxer to read its packets, or create a muxer to write
/// one. The container is detected from the file's first bytes when reading, and chosen by extension when writing.
/// </summary>
/// <example>
/// <code language="csharp">
/// MediaInfo info = MediaFile.Probe("in.avi");
/// using IDemuxer demuxer = MediaFile.OpenDemuxer("in.avi");
/// using IMuxer muxer = MediaFile.CreateMuxer("out.mkv", demuxer.Tracks);
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///         muxer.WritePacket(packet);
/// }
/// </code>
/// </example>
public static class MediaFile
{
    /// <summary>Reads a file's headers: format, duration, tracks and tags. No frame is decoded.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>What the file holds.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static MediaInfo Probe(string path, MediaContext? context = null)
    {
        using IDemuxer demuxer = OpenDemuxer(path, context);
        return demuxer.Info;
    }

    /// <summary>Reads a stream's headers. See <see cref="Probe(string, MediaContext?)"/>.</summary>
    /// <param name="stream">The file, read from its current position and left open.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>What the file holds.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static MediaInfo Probe(Stream stream, MediaContext? context = null)
    {
        using IDemuxer demuxer = OpenDemuxer(stream, context, leaveOpen: true);
        return demuxer.Info;
    }

    /// <summary>Reads a file's headers on the thread pool. See <see cref="Probe(string, MediaContext?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels before the read starts.</param>
    /// <returns>What the file holds.</returns>
    public static Task<MediaInfo> ProbeAsync(string path, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Probe(path, context), cancellationToken);
    }

    /// <summary>
    /// Reads a stream's headers. A stream that is not a <see cref="FileStream"/> or <see cref="MemoryStream"/> is first copied
    /// into memory asynchronously, as Egami's <c>LoadAsync</c> does, so no thread blocks on network or pipe reads.
    /// </summary>
    /// <param name="stream">The file, read from its current position and left open.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>What the file holds.</returns>
    public static async Task<MediaInfo> ProbeAsync(Stream stream, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is FileStream or MemoryStream)
        {
            return await Task.Run(() => Probe(stream, context), cancellationToken).ConfigureAwait(false);
        }

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return Probe(buffer, context);
    }

    /// <summary>Opens a file for reading packets.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The demuxer, which owns the file. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static IDemuxer OpenDemuxer(string path, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            return OpenDemuxer(stream, context, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a stream for reading packets, from its current position. A non-seekable stream works too: its first bytes are
    /// buffered for detection and replayed, and the demuxer cannot seek.
    /// </summary>
    /// <param name="stream">The file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the demuxer leaves the stream open.</param>
    /// <returns>The demuxer. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static IDemuxer OpenDemuxer(Stream stream, MediaContext? context = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        context ??= MediaContext.Default;
        (ContainerFormat format, Stream input) = Detect(stream, context);
        return format.OpenDemuxer(input, context, leaveOpen);
    }

    /// <summary>Opens a file for reading packets on the thread pool. See <see cref="OpenDemuxer(string, MediaContext?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels before the open starts.</param>
    /// <returns>The demuxer. The caller must dispose it.</returns>
    public static Task<IDemuxer> OpenDemuxerAsync(string path, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => OpenDemuxer(path, context), cancellationToken);
    }

    /// <summary>Creates a file and a muxer for it, choosing the container by the file's extension. An existing file is replaced.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="tracks">The tracks to write, numbered as the packets will be.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="metadata">Tags, chapters and attachments to write, or <see langword="null"/> for none.</param>
    /// <returns>The muxer, which owns the file. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container writes the extension.</exception>
    /// <exception cref="NotSupportedException">The container cannot hold one of the tracks.</exception>
    public static IMuxer CreateMuxer(string path, IReadOnlyList<TrackInfo> tracks, MediaContext? context = null, MediaMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        context ??= MediaContext.Default;
        ContainerFormat format = FormatForPath(path, context);
        FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        try
        {
            return format.CreateMuxer(stream, tracks, metadata ?? MediaMetadata.Empty, context, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Creates a muxer that writes to a stream in the named container.</summary>
    /// <param name="stream">Where to write. A non-seekable stream gets the container's streaming layout, when it has one.</param>
    /// <param name="formatNameOrExtension">"AVI", "Matroska", ".mkv", "webm" and so on.</param>
    /// <param name="tracks">The tracks to write, numbered as the packets will be.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <param name="metadata">Tags, chapters and attachments to write, or <see langword="null"/> for none.</param>
    /// <returns>The muxer. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container has that name or extension, or it cannot write.</exception>
    /// <exception cref="NotSupportedException">The container cannot hold one of the tracks.</exception>
    public static IMuxer CreateMuxer(Stream stream, string formatNameOrExtension, IReadOnlyList<TrackInfo> tracks, MediaContext? context = null, bool leaveOpen = false, MediaMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        context ??= MediaContext.Default;
        ContainerFormat format = context.FindContainer(formatNameOrExtension) is { CanMux: true } found
            ? found
            : throw new UnsupportedFormatException($"No registered container writes '{formatNameOrExtension}'.");
        return format.CreateMuxer(stream, tracks, metadata ?? MediaMetadata.Empty, context, leaveOpen);
    }

    /// <summary>
    /// Copies the packets of one file into another container without decoding, for example AVI to Matroska keeping the
    /// sound, with its tags, chapters and attachments where the output container can hold them. The output container comes
    /// from the extension.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write; an existing file is replaced.</param>
    /// <param name="tracks">Which tracks to keep, or <see langword="null"/> for all.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>How many packets were copied.</returns>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes the output's extension.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks.</exception>
    public static long Remux(string inputPath, string outputPath, Func<TrackInfo, bool>? tracks = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        context ??= MediaContext.Default;
        using IDemuxer demuxer = OpenDemuxer(inputPath, context);
        TrackInfo[] kept = [.. demuxer.Tracks.Where(t => tracks?.Invoke(t) ?? true)];
        HashSet<int> numbers = [.. kept.Select(t => t.Number)];
        using IMuxer muxer = CreateMuxer(outputPath, kept, context, demuxer.Info.Metadata);
        long count = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (numbers.Contains(packet.TrackNumber))
                {
                    muxer.WritePacket(packet);
                    count++;
                }
            }
        }

        muxer.Finish();
        return count;
    }

    internal static ContainerFormat FormatForPath(string path, MediaContext context)
    {
        string extension = Path.GetExtension(path);
        return extension.Length > 1 && context.FindContainer(extension) is { CanMux: true } format
            ? format
            : throw new UnsupportedFormatException($"No registered container writes '{extension}' files.");
    }

    private static (ContainerFormat Format, Stream Input) Detect(Stream stream, MediaContext context)
    {
        int needed = context.Containers.Count == 0 ? 0 : context.Containers.Max(f => f.SignatureLength);
        byte[] header = new byte[needed];
        Stream input = stream;
        int read;
        if (stream.CanSeek)
        {
            long start = stream.Position;
            read = stream.ReadAtLeast(header, needed, throwOnEndOfStream: false);
            stream.Position = start;
        }
        else
        {
            read = stream.ReadAtLeast(header, needed, throwOnEndOfStream: false);
            input = new ReplayStream(header.AsMemory(0, read), stream);
        }

        foreach (ContainerFormat format in context.Containers)
        {
            if (format.IsMatch(header.AsSpan(0, Math.Min(read, format.SignatureLength))))
            {
                return (format, input);
            }
        }

        throw new UnsupportedFormatException("No registered container matches the data.");
    }
}
