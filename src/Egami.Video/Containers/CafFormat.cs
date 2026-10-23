using Egami.Video.Containers.Caf;

namespace Egami.Video.Containers;

/// <summary>
/// Apple's Core Audio Format (.caf): one audio track of PCM (integer or float, either byte order), G.711, ALAC, Opus, AAC, MP3,
/// AC-3 or FLAC, with 64-bit chunk sizes, so no 4 GB limit. The packet table (pakt) cuts variable-size packets and gives the
/// priming frames (<see cref="TrackInfo.CodecDelay"/>) and the padding at the end; the magic cookie (kuki) becomes
/// <see cref="TrackInfo.CodecPrivate"/>; the info chunk becomes <see cref="MediaInfo.Tags"/>; markers and regions, named from the
/// strg chunk, become chapters; and a chan chunk gives the channel layout. Other formats pass through undecoded with an
/// "X_CAF/" codec id. The writer streams PCM and G.711 (a data chunk sized -1 on a non-seekable output) and puts the packet table
/// of other codecs after the samples, or before them through a temporary file when the output is not seekable. Seeking is
/// sample exact for PCM and packet exact for the rest.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-caf"/>
/// </example>
public sealed class CafFormat : ContainerFormat
{
    private CafFormat()
    {
    }

    /// <summary>Gets the CAF format.</summary>
    public static CafFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "CAF";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".caf"];

    /// <inheritdoc/>
    public override int SignatureLength => 8;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header.StartsWith("caff"u8) && header[4] == 0 && header[5] == 1;

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new CafDemuxer(stream, context, leaveOpen);
    }

    /// <summary>
    /// Creates a muxer for one audio track: integer PCM (8, 16, 24 or 32 bits), float PCM (32 or 64 bits), G.711, ALAC, Opus,
    /// AAC, MP3, MP2, AC-3 or FLAC. Attachments and cover art are left out with a "metadata-dropped" warning.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one audio track.</param>
    /// <param name="metadata">The tags and chapters.</param>
    /// <param name="context">The context, for warnings.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one audio track of a codec CAF holds.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new CafMuxer(stream, tracks, metadata, context, leaveOpen);
    }
}
