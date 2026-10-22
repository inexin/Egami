namespace Egami.Video.Containers;

/// <summary>
/// A container format: how to recognize it and how to open a demuxer or muxer for it. Register a new one with
/// <see cref="MediaContext.WithContainer"/>.
/// </summary>
/// <example>
/// <code language="csharp">
/// ContainerFormat? format = MediaContext.Default.FindContainer(".mkv");
/// using IDemuxer demuxer = format!.OpenDemuxer(File.OpenRead("in.mkv"), MediaContext.Default, leaveOpen: false);
/// </code>
/// </example>
public abstract class ContainerFormat
{
    /// <summary>Gets the format's name, such as "AVI" or "Matroska".</summary>
    public abstract string Name { get; }

    /// <summary>Gets the file extensions, with the dot, the first being the one writers use.</summary>
    public abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>Gets how many leading bytes <see cref="IsMatch"/> needs to decide.</summary>
    public abstract int SignatureLength { get; }

    /// <summary>Gets whether <see cref="CreateMuxer(Stream, IReadOnlyList{TrackInfo}, MediaMetadata, MediaContext, bool)"/> is supported.</summary>
    public virtual bool CanMux => false;

    /// <summary>Returns whether the file's first bytes belong to this format.</summary>
    /// <param name="header">Up to <see cref="SignatureLength"/> leading bytes; fewer when the file is shorter.</param>
    /// <returns>Whether the format matches.</returns>
    public abstract bool IsMatch(ReadOnlySpan<byte> header);

    /// <summary>Opens a demuxer that reads packets from <paramref name="stream"/>, starting at its current position.</summary>
    /// <param name="stream">The file. Seeking is used when the stream supports it.</param>
    /// <param name="context">The limits and registered codecs.</param>
    /// <param name="leaveOpen">Whether disposing the demuxer leaves the stream open.</param>
    /// <returns>The demuxer, with its tracks read.</returns>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit in <see cref="MediaContext.Limits"/>.</exception>
    public abstract IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen);

    /// <summary>Creates a muxer that writes <paramref name="tracks"/> to <paramref name="stream"/>.</summary>
    /// <param name="stream">Where to write. A non-seekable stream is written in a streaming layout when the format has one.</param>
    /// <param name="tracks">The tracks, as a demuxer or encoder describes them. The muxer numbers them its own way and maps packets by <see cref="TrackInfo.Number"/>.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The format cannot be written, or cannot hold one of the tracks.</exception>
    public IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaContext context, bool leaveOpen) =>
        CreateMuxer(stream, tracks, MediaMetadata.Empty, context, leaveOpen);

    /// <summary>
    /// Creates a muxer that writes <paramref name="tracks"/> and the file's tags, chapters and attachments. What the format
    /// cannot hold is left out and reported as a "metadata-dropped" <see cref="MediaWarning"/>.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The tracks.</param>
    /// <param name="metadata">The tags, chapters and attachments, such as a demuxer's <see cref="MediaInfo.Metadata"/>.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The format cannot be written, or cannot hold one of the tracks.</exception>
    public virtual IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen) =>
        throw new NotSupportedException($"{Name} cannot be written.");

    /// <inheritdoc/>
    public override string ToString() => Name;
}
