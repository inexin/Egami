using Egami.Video.Containers.Flac;

namespace Egami.Video.Containers;

/// <summary>
/// Native FLAC files (.flac): "fLaC", the metadata blocks and the frames. STREAMINFO gives the track; Vorbis comments become
/// <see cref="MediaInfo.Tags"/> (a WAVEFORMATEXTENSIBLE_CHANNEL_MASK comment the track's layout) and pictures become
/// attachments, the front cover named cover.*. Frames are found by their sync code, header CRC-8 and footer CRC-16, so each
/// packet is one whole frame; seeking uses the seek table, or one pass over the frames when there is none. The writer stores
/// STREAMINFO, tags, cover art and padding; on a seekable stream it fills in the frame sizes, total samples and MD5 at the end
/// and turns part of the padding into a seek table with a point every 10 seconds.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-containers"/>
/// </example>
public sealed class FlacFormat : ContainerFormat
{
    private FlacFormat()
    {
    }

    /// <summary>Gets the FLAC format.</summary>
    public static FlacFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "FLAC";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".flac"];

    /// <inheritdoc/>
    public override int SignatureLength => 4;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) => header.StartsWith("fLaC"u8);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new FlacDemuxer(stream, context, leaveOpen);
    }

    /// <summary>Creates a muxer for one FLAC track, writing tags as Vorbis comments and image attachments as pictures.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one FLAC track; its codec private data holds STREAMINFO.</param>
    /// <param name="metadata">The tags and cover art. Chapters and other attachments are left out with a "metadata-dropped" warning.</param>
    /// <param name="context">The context, for warnings.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one FLAC track with STREAMINFO.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new FlacMuxer(stream, tracks, metadata, context, leaveOpen);
    }
}
