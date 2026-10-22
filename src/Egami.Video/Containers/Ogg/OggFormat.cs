using Egami.Video.Containers.Ogg;

namespace Egami.Video.Containers;

/// <summary>
/// Ogg (.ogg, .opus, .oga), RFC 3533, with the Opus (RFC 7845), Vorbis and FLAC mappings. Pages are CRC-checked and packets
/// rebuilt across them; timestamps come from granule positions, with Opus's pre-skip as the track's codec delay and the last
/// page's granule position trimming the end. Seeking uses an index of granule positions built in one pass over the page headers
/// on open. Chained files (one stream after another, as internet radio dumps are) read as one track when the codec settings stay
/// the same, each link's TITLE a chapter; a link that changes them ends the track with an "ogg-chained-stream" warning. The
/// writer stores Opus, Vorbis and FLAC with tags, so a WebM's Opus track remuxes to .opus without decoding.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-ogg"/>
/// </example>
public sealed class OggFormat : ContainerFormat
{
    private OggFormat()
    {
    }

    /// <summary>Gets the Ogg format.</summary>
    public static OggFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "Ogg";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".ogg", ".opus", ".oga"];

    /// <inheritdoc/>
    public override int SignatureLength => 4;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) => header.StartsWith("OggS"u8);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new OggDemuxer(stream, context, leaveOpen);
    }

    /// <summary>Creates a muxer for Opus, Vorbis and FLAC tracks, writing the tags as Vorbis comments in each stream's headers.</summary>
    /// <param name="stream">Where to write; it need not be seekable.</param>
    /// <param name="tracks">The audio tracks.</param>
    /// <param name="metadata">The tags. Chapters and attachments are left out with a "metadata-dropped" warning.</param>
    /// <param name="context">The context, for warnings.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">A track is not Opus, Vorbis or FLAC.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new OggMuxer(stream, tracks, metadata, context, leaveOpen);
    }
}
