using Egami.Video.Containers.Matroska;

namespace Egami.Video.Containers;

/// <summary>
/// Matroska (RFC 9559) and its WebM subset. Both instances read either; they differ in the DocType and extension a muxer
/// writes: <see cref="Instance"/> writes Matroska (.mkv), <see cref="WebM"/> writes WebM (.webm).
/// </summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = MatroskaFormat.Instance.OpenDemuxer(File.OpenRead("in.mkv"), MediaContext.Default, leaveOpen: false);
/// </code>
/// </example>
public sealed class MatroskaFormat : ContainerFormat
{
    private MatroskaFormat(string name, string docType, IReadOnlyList<string> extensions)
    {
        Name = name;
        DocType = docType;
        Extensions = extensions;
    }

    /// <summary>Gets the Matroska format.</summary>
    public static MatroskaFormat Instance { get; } = new("Matroska", "matroska", [".mkv", ".mka", ".mk3d"]);

    /// <summary>Gets the WebM format: Matroska with the "webm" DocType.</summary>
    public static MatroskaFormat WebM { get; } = new("WebM", "webm", [".webm"]);

    /// <inheritdoc/>
    public override string Name { get; }

    /// <summary>Gets the EBML DocType a muxer writes: "matroska" or "webm".</summary>
    public string DocType { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; }

    /// <inheritdoc/>
    public override int SignatureLength => 4;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) => header.Length >= 4 && header[..4].SequenceEqual((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new MatroskaDemuxer(stream, context, leaveOpen);
    }

    /// <summary>
    /// Creates a muxer. Tracks are numbered from 1 in the order given, timestamps are stored in milliseconds, and a Cluster
    /// starts on a video keyframe once about a second or 5 MB has passed. On a non-seekable stream the Segment has an unknown
    /// size and there are no Cues. WebM holds only VP8, VP9, AV1, Opus, Vorbis and WebVTT.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The tracks.</param>
    /// <param name="metadata">The tags, chapters and attachments.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The format cannot hold one of the tracks.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        return new MatroskaMuxer(stream, tracks, DocType, leaveOpen, metadata);
    }
}
