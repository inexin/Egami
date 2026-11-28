using Egami.Video.Containers.Mp4;

namespace Egami.Video.Containers;

/// <summary>
/// QuickTime movies (Apple's QuickTime File Format): .mov and .qt files. Reading is <see cref="Mp4Format"/>'s, so a .mov reads
/// exactly as it does there. Writing makes a QuickTime movie rather than ISO MP4: the qt brand, QuickTime's handler, sound and
/// video descriptions, and the uncompressed layouts editing applications use. Registered ahead of <see cref="Mp4Format"/> in
/// <see cref="MediaContext.Default"/>, so a .mov output is QuickTime; <see cref="Mp4Format"/> chosen by name still writes MP4.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-mov"/>
/// </example>
public sealed class MovFormat : ContainerFormat
{
    /// <summary>Gets the QuickTime format, which writes moov before the media.</summary>
    public static MovFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "QuickTime";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".mov", ".qt"];

    /// <inheritdoc/>
    public override int SignatureLength => Mp4Format.Instance.SignatureLength;

    /// <summary>
    /// Gets whether writers put moov after the media. By default moov comes first, so the movie plays while it downloads: on a
    /// stream that can seek and read, the media is written in place and moved forward at the end, else it waits in a temporary
    /// file. With moov at the end the media goes straight to the output, which must be able to seek.
    /// </summary>
    public bool MoovAtEnd { get; init; }

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) => Mp4Format.Instance.IsMatch(header);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen) => Mp4Format.Instance.OpenDemuxer(stream, context, leaveOpen);

    /// <summary>
    /// Creates a muxer that writes a QuickTime movie: ftyp with the qt brand, moov, a wide atom and one mdat; QuickTime media and
    /// data handlers (mhlr and dhlr); sound descriptions of version 0, 1 or 2 (PCM as sowt, twos, raw, in24, in32, fl32, fl64 or
    /// lpcm; AAC, MP3 and ALAC with their setup in a wave atom) with a chan layout; video descriptions with colr (nclc), fiel,
    /// pasp and, for uncompressed video, clap. Holds H.264, HEVC, Motion JPEG (as jpeg), uncompressed 4:2:2 (2vuy from 8-bit
    /// and v210 from 10-bit packets) and RGB (raw), AAC, MP3, ALAC, PCM, mov_text (tx3g on a subtitle track), and tracks read
    /// from MP4 or QuickTime with any other codec, whose sample descriptions are written back byte for byte. Chapters, cover art,
    /// tags, rotation and edit lists are written as <see cref="Mp4Format"/> writes them. There is no fragmented QuickTime: on an
    /// output that cannot seek and read back, the media waits in a temporary file.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The tracks.</param>
    /// <param name="metadata">The tags, chapters and cover art.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">
    /// QuickTime cannot hold one of the tracks (AV1, VP9, VP8, FFV1, Opus, FLAC, Vorbis and the rest: the message names MP4 or
    /// Matroska instead), or <see cref="MoovAtEnd"/> is set and the stream cannot seek.
    /// </exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new Mp4Muxer(stream, tracks, metadata, context, leaveOpen, MoovAtEnd, quickTime: true);
    }
}
