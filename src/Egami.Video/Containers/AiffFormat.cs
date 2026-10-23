using Egami.Video.Containers.Aiff;

namespace Egami.Video.Containers;

/// <summary>
/// AIFF and AIFF-C (.aif, .aiff, .aifc; Apple's Audio Interchange File Format 1.3 and its AIFF-C draft): big-endian integer
/// PCM, AIFF-C's little-endian (sowt), 24 and 32-bit (in24, in32), float (fl32, fl64) and G.711 (ulaw, alaw) samples. Other
/// AIFF-C compressions (ima4, MACE, QDesign and so on) pass through undecoded with an "X_AIFC/" codec id. The NAME, AUTH,
/// "(c) " and ANNO chunks and an ID3 chunk become <see cref="MediaInfo.Tags"/> (ID3 pictures become attachments), MARK
/// markers become chapters, and a CHAN chunk gives the channel layout. The writer stores integer PCM as AIFF and float as
/// AIFF-C, the title, artist, copyright and comment in the text chunks, any other tag and cover art in an ID3 chunk, chapters
/// as markers and the layout of more than two channels in a CHAN chunk. A non-seekable output is written through a temporary
/// file, since AIFF's sizes come before the samples. Seeking is sample exact.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-aiff"/>
/// </example>
public sealed class AiffFormat : ContainerFormat
{
    private AiffFormat()
    {
    }

    /// <summary>Gets the AIFF format, which reads and writes AIFF-C too.</summary>
    public static AiffFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AIFF";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".aiff", ".aif", ".aifc", ".afc"];

    /// <inheritdoc/>
    public override int SignatureLength => 12;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header.StartsWith("FORM"u8) && (header[8..12].SequenceEqual("AIFF"u8) || header[8..12].SequenceEqual("AIFC"u8));

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new AiffDemuxer(stream, context, leaveOpen);
    }

    /// <summary>
    /// Creates a muxer for one audio track of integer PCM (8, 16, 24 or 32 bits, either byte order), stored big-endian as AIFF,
    /// or float PCM (32 or 64 bits), stored as AIFF-C fl32 or fl64. Attachments other than images are left out with a
    /// "metadata-dropped" warning. AIFF cannot hold more than 4 GB of samples; write CAF or Wave64 for longer audio.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one audio track.</param>
    /// <param name="metadata">The tags, chapters and cover art.</param>
    /// <param name="context">The context, for warnings.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one PCM audio track.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new AiffMuxer(stream, tracks, metadata, context, leaveOpen);
    }
}
