using Egami.Video.Containers.Wav;

namespace Egami.Video.Containers;

/// <summary>
/// WAV (.wav): RIFF WAVE with integer or float PCM, WAVE_FORMAT_EXTENSIBLE (channel mask and valid bits) and the compressed
/// formats AVI also names, plus RF64 and BW64 for files over 4 GB (<see cref="Instance"/>) and Sony Wave64 (.w64,
/// <see cref="Wave64"/>). Streamed files whose sizes were never filled in (0 or 0xFFFFFFFF) are read to the end. LIST/INFO and
/// id3 tags become <see cref="MediaInfo.Tags"/>, and cue points with their labels become chapters. The writer stores PCM and
/// float, INFO tags and chapters as cue points, and turns a WAV file into RF64 by itself when the data passes 4 GB, as EBU Tech
/// 3306 says (a JUNK chunk reserved after the header becomes the ds64 chunk). Seeking is sample exact.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-containers"/>
/// </example>
public sealed class WavFormat : ContainerFormat
{
    private WavFormat(bool wave64)
    {
        IsWave64 = wave64;
        Extensions = wave64 ? [".w64"] : [".wav", ".rf64", ".bw64"];
    }

    /// <summary>Gets WAV, with RF64 and BW64.</summary>
    public static WavFormat Instance { get; } = new(false);

    /// <summary>Gets Sony Wave64.</summary>
    public static WavFormat Wave64 { get; } = new(true);

    /// <inheritdoc/>
    public override string Name => IsWave64 ? "Wave64" : "WAV";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; }

    /// <inheritdoc/>
    public override int SignatureLength => IsWave64 ? 40 : 12;

    /// <inheritdoc/>
    public override bool CanMux => true;

    internal bool IsWave64 { get; }

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) => IsWave64
        ? header.Length >= 40 && header.StartsWith(WavChunks.RiffGuid) && header[24..40].SequenceEqual(WavChunks.WaveGuid)
        : header.Length >= 12 && (header.StartsWith("RIFF"u8) || header.StartsWith("RF64"u8) || header.StartsWith("BW64"u8)) && header[8..12].SequenceEqual("WAVE"u8);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new WavDemuxer(stream, context, leaveOpen, this);
    }

    /// <summary>
    /// Creates a muxer for one audio track of integer PCM (8, 16, 24 or 32 bits, little-endian) or float PCM (32 or 64 bits).
    /// Tags go into a LIST/INFO chunk and chapters into a cue chunk with labels; attachments are left out with a
    /// "metadata-dropped" warning. A non-seekable stream gets the streaming sizes 0xFFFFFFFF, which readers take as "to the end".
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one audio track.</param>
    /// <param name="metadata">The tags and chapters.</param>
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
        return new WavMuxer(stream, tracks, metadata, context, leaveOpen, IsWave64);
    }
}
