using Egami.Video.Containers.Avi;

namespace Egami.Video.Containers;

/// <summary>
/// AVI, the RIFF-based container from Video for Windows, with the OpenDML extensions for files over 1 GB. Reads any AVI and
/// writes Motion JPEG video with audio and other tracks copied through.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = AviFormat.Instance.OpenDemuxer(File.OpenRead("camera.avi"), MediaContext.Default, leaveOpen: false);
/// </code>
/// </example>
public sealed class AviFormat : ContainerFormat
{
    private AviFormat()
    {
    }

    /// <summary>Gets the AVI format.</summary>
    public static AviFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AVI";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".avi"];

    /// <inheritdoc/>
    public override int SignatureLength => 12;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVI "u8);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new AviDemuxer(stream, context, leaveOpen);
    }

    /// <summary>
    /// Creates an AVI muxer. The output must be seekable: sizes and indexes are patched when the file is finished. Video must
    /// be Motion JPEG or a Video for Windows codec (<see cref="CodecIds.VfwFourCC"/>); audio may be PCM, MP3, MP2, AC-3, AAC or
    /// any ACM codec (<see cref="CodecIds.AcmAudio"/>). AVI has a fixed frame rate: video gaps become empty drop frames, and
    /// timestamps off the frame grid (variable frame rate) are rounded to it, reported once per track as an "avi-vfr"
    /// <see cref="MediaWarning"/>.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The tracks; AVI numbers them from 0 in this order.</param>
    /// <param name="metadata">The tags, written as an INFO list; chapters and attachments are left out with a warning.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The stream is not seekable, or AVI cannot hold a track's codec.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        return new AviMuxer(stream, tracks, leaveOpen, context, metadata);
    }
}
