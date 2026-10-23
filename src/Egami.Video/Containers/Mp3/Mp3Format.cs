using Egami.Video.Audio.Codecs.Mp3;
using Egami.Video.Containers.Mp3;

namespace Egami.Video.Containers;

/// <summary>
/// MPEG audio files (.mp3, .mp2, .mp1, .mpa): MP3 and MP2 frames with their tags. The reader takes ID3v2.2, 2.3 and 2.4 tags
/// (text frames into <see cref="MediaInfo.Tags"/>, pictures as attachments with the front cover named cover.*, CHAP frames as
/// chapters), ID3v1 and APEv2 tags at the end, and finds frames by three matching headers, so junk and false syncs are skipped.
/// A Xing, Info or VBRI frame gives the frame count and duration, and its LAME tag the encoder delay and padding, so the audio
/// is gapless: the first packet starts before zero and the last packet's duration stops at the source's end. Seeks are exact
/// by default (<see cref="FastSeek"/> uses the Xing table instead). The writer stores an ID3v2.4 tag and the frames, with a
/// Xing header and LAME-layout tag on a seekable stream, so MP3 can leave Matroska or MP4 without a re-encode.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-mp3"/>
/// </example>
public sealed class Mp3Format : ContainerFormat
{
    /// <summary>Gets the MP3 format, with exact seeks.</summary>
    public static Mp3Format Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "MP3";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".mp3", ".mp2", ".mp1", ".mpa"];

    /// <inheritdoc/>
    public override int SignatureLength => 4096;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <summary>
    /// Gets whether readers seek by the Xing header's table (or VBRI's, or the bit rate) instead of exactly (V5.8 Design Q25).
    /// By default the first seek reads every frame header (4 bytes a frame) into an index, and seeks then land on the exact
    /// sample. Fast seeks read nothing first but land within about a percent of the duration, as FFmpeg and browsers do, and
    /// packet times after them are estimates. Register a format with this set through <see cref="MediaContext.WithContainer"/>.
    /// </summary>
    public bool FastSeek { get; init; }

    /// <summary>Returns whether the data starts with an ID3v2 tag or with an MPEG audio frame whose next header matches it.</summary>
    /// <param name="header">The leading bytes.</param>
    /// <returns>Whether this is an MPEG audio file.</returns>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        if (Id3Tag.Size(header) > 0)
        {
            return true;
        }

        if (!MpegAudioHeader.IsValid(header))
        {
            return false;
        }

        int total = new MpegAudioHeader(header).TotalBytes(0);
        if (total == 0)
        {
            int free = 0;
            return MpegAudioSync.Find(header, ref free, out _, atEnd: header.Length < SignatureLength) == 0;
        }

        return total + MpegAudioHeader.Size <= header.Length ? MpegAudioHeader.Matches(header, header[total..]) : header.Length == total;
    }

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new Mp3Demuxer(stream, context, FastSeek, leaveOpen);
    }

    /// <summary>Creates a muxer for one MPEG audio track (MP3, MP2 or MP1), writing tags, cover art and chapters as ID3v2.4.</summary>
    /// <param name="stream">Where to write. When it can seek, a Xing header with the frame count, seek table, delay and padding is filled in at the end.</param>
    /// <param name="tracks">The one MPEG audio track.</param>
    /// <param name="metadata">The tags, pictures and chapters. Attachments that are not images are left out with a "metadata-dropped" warning.</param>
    /// <param name="context">The context, for warnings.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one MPEG audio track.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new Mp3Muxer(stream, tracks, metadata, context, leaveOpen);
    }
}
