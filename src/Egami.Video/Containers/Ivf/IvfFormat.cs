using Egami.Video.Containers.Ivf;

namespace Egami.Video.Containers;

/// <summary>
/// IVF (.ivf): the bare frame container libvpx's tools and the VP8, VP9 and AV1 test vectors use. A 32-byte file header (FourCC,
/// size, time base, frame count) and a 12-byte header per frame (size, timestamp), with no index. Reads and writes VP8
/// (<c>VP80</c>), VP9 (<c>VP90</c>) and AV1 (<c>AV01</c>) tracks; seeking scans the frame headers once. Reading a file and
/// writing it back gives the same bytes.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = MediaFile.OpenDemuxer("in.webm");
/// using IMuxer muxer = IvfFormat.Instance.CreateMuxer(File.Create("out.ivf"), [demuxer.Info.VideoTrack!], MediaContext.Default, leaveOpen: false);
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///     {
///         if (packet.TrackNumber == demuxer.Info.VideoTrack!.Number)
///             muxer.WritePacket(packet);
///     }
/// }
/// </code>
/// </example>
public sealed class IvfFormat : ContainerFormat
{
    private IvfFormat()
    {
    }

    /// <summary>Gets the IVF format.</summary>
    public static IvfFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "IVF";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".ivf"];

    /// <inheritdoc/>
    public override int SignatureLength => 4;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) => header.StartsWith("DKIF"u8);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new IvfDemuxer(stream, context, leaveOpen);
    }

    /// <summary>
    /// Creates a muxer for one VP8, VP9 or AV1 video track. On a seekable stream the frame count is patched when the muxer
    /// finishes; on a non-seekable one it stays 0, which libvpx's tools accept.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one video track.</param>
    /// <param name="metadata">Ignored: IVF has no tags, chapters or attachments.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one VP8, VP9 or AV1 video track.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        return new IvfMuxer(stream, tracks, leaveOpen);
    }
}
