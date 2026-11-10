using Egami.Video.Audio.Codecs.Aac;
using Egami.Video.Containers.Adts;
using Egami.Video.Containers.Mp3;

namespace Egami.Video.Containers;

/// <summary>
/// Raw AAC in ADTS frames (.aac, .adts; ISO/IEC 13818-7 and 14496-3 Annex 1.A): each frame a short header and one raw data
/// block, with no container around them. Reading gives one <see cref="CodecIds.Aac"/> packet per frame, the header and CRC
/// stripped as MP4 and Matroska store AAC, and the AudioSpecificConfig built from the first header; ID3v2 tags at the start are
/// read into the tags. Writing adds a header without a CRC to each packet, so an AAC track from MP4, Matroska, CAF or AVI can
/// be extracted without decoding. Seeking builds a frame index in one pass.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-adts"/>
/// </example>
public sealed class AdtsFormat : ContainerFormat
{
    private AdtsFormat()
    {
    }

    /// <summary>Gets the ADTS format.</summary>
    public static AdtsFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "ADTS";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".aac", ".adts"];

    /// <inheritdoc/>
    public override int SignatureLength => 4096;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <summary>
    /// Returns whether the data starts, after any ID3v2 tag that fits in the signature, with an ADTS header followed by a matching
    /// one, or by the end of the data.
    /// </summary>
    /// <param name="header">The leading bytes.</param>
    /// <returns>Whether the format matches.</returns>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        int tag = Id3Tag.Size(header);
        if (tag > 0)
        {
            if (tag >= header.Length)
            {
                return false;
            }

            header = header[tag..];
        }

        if (!AdtsHeader.TryRead(header, out AdtsHeader first))
        {
            return false;
        }

        int next = first.FrameBytes;
        if (next + AdtsHeader.Size <= header.Length)
        {
            return AdtsHeader.TryRead(header[next..], out AdtsHeader second) && first.Matches(second);
        }

        // The frame runs past the bytes given: the end of a short file, or a frame longer than the signature.
        return true;
    }

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new AdtsDemuxer(stream, context, leaveOpen);
    }

    /// <summary>
    /// Creates a muxer for one AAC track. HE-AAC is written as its AAC LC core at the core rate. Tags, chapters and attachments
    /// are left out with a "metadata-dropped" warning.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one AAC track, with its AudioSpecificConfig in <see cref="TrackInfo.CodecPrivate"/>.</param>
    /// <param name="metadata">Left out: ADTS holds none.</param>
    /// <param name="context">The context, for warnings.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one AAC track, or its object type, rate or channel configuration has no ADTS header.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new AdtsMuxer(stream, tracks, metadata, context, leaveOpen);
    }
}
