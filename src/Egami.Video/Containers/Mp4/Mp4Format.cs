using System.Buffers.Binary;

using Egami.Video.Containers.Mp4;

namespace Egami.Video.Containers;

/// <summary>
/// MP4 and QuickTime (ISO/IEC 14496-12 and 14496-14, and Apple's QuickTime File Format): .mp4, .m4v, .m4a and .mov files, flat or
/// fragmented (CMAF, DASH segments). Reads every track: AV1, VP9, VP8, Motion JPEG, Opus, FLAC, PCM and mov_text map to their
/// codec ids, and any other track (H.264, HEVC, AAC, timecode) is passed through so a remux keeps it. Edit lists are applied, the
/// display matrix becomes <see cref="VideoTrackInfo.Orientation"/>, and tags, chapters and cover art are read.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = Mp4Format.Instance.OpenDemuxer(File.OpenRead("phone.mov"), MediaContext.Default, leaveOpen: false);
/// foreach (TrackInfo track in demuxer.Tracks)
///     Console.WriteLine($"{track.Number}: {track.Kind} {track.CodecId}");
/// </code>
/// </example>
public sealed class Mp4Format : ContainerFormat
{
    /// <summary>Gets the MP4 format, which applies edit lists.</summary>
    public static Mp4Format Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "MP4";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".mp4", ".m4v", ".m4a", ".mov", ".3gp", ".m4s", ".cmfv", ".cmfa"];

    /// <inheritdoc/>
    public override int SignatureLength => 12;

    /// <summary>
    /// Gets whether readers ignore edit lists (V4.1 Design Q5). By default an edit list is applied: packet timestamps follow it,
    /// an empty edit delays the track, and packets wholly outside the edit are marked <see cref="EncodedPacket.IsDiscardable"/>.
    /// Ignored, timestamps are the media's own. Register a format with this set through <see cref="MediaContext.WithContainer"/>.
    /// </summary>
    public bool IgnoreEditLists { get; init; }

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
        {
            return false;
        }

        // A file starts with ftyp, or in old QuickTime files with moov, mdat, free, skip, wide or pnot.
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        ReadOnlySpan<byte> type = header[4..8];
        return (type.SequenceEqual("ftyp"u8) && size >= 8)
            || ((type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8)
                || type.SequenceEqual("wide"u8) || type.SequenceEqual("pnot"u8)) && (size is 0 or 1 or >= 8));
    }

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new Mp4Demuxer(stream, context, leaveOpen, IgnoreEditLists);
    }
}
