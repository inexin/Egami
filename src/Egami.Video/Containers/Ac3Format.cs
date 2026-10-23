using Egami.Video.Audio.Codecs.Ac3;
using Egami.Video.Containers.Ac3;

namespace Egami.Video.Containers;

/// <summary>
/// Raw AC-3 and E-AC-3 (.ac3, .eac3, .ec3; ATSC A/52): sync frames back to back, with no container around them. AC-3 frames
/// (<see cref="CodecIds.Ac3"/>) are read one per packet; E-AC-3 (<see cref="CodecIds.EAc3"/>) is passed through, an
/// independent frame and the substreams that follow it per packet. Writing copies the packets out unchanged, so a track from
/// MP4, Matroska, CAF or AVI can be extracted without decoding. Seeking builds a frame index in one pass.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-ac3"/>
/// </example>
public sealed class Ac3Format : ContainerFormat
{
    private Ac3Format()
    {
    }

    /// <summary>Gets the raw AC-3 format, which reads and writes E-AC-3 too.</summary>
    public static Ac3Format Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AC-3";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".ac3", ".eac3", ".ec3"];

    /// <inheritdoc/>
    public override int SignatureLength => 4096 + Ac3Header.Size;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <summary>Returns whether the file starts with an AC-3 or E-AC-3 sync frame followed by another, or by the end of the file.</summary>
    /// <param name="header">The leading bytes.</param>
    /// <returns>Whether the format matches.</returns>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        if (!Ac3Header.TryRead(header, out Ac3Header first))
        {
            return false;
        }

        int next = first.FrameBytes;
        return header.Length < SignatureLength && header.Length <= next
            ? header.Length == next
            : next + Ac3Header.Size <= header.Length && Ac3Header.TryRead(header[next..], out _);
    }

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new Ac3Demuxer(stream, context, leaveOpen);
    }

    /// <summary>Creates a muxer for one AC-3 or E-AC-3 track. Tags, chapters and attachments are left out with a "metadata-dropped" warning.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one AC-3 or E-AC-3 track.</param>
    /// <param name="metadata">Left out: raw AC-3 holds none.</param>
    /// <param name="context">The context, for warnings.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one AC-3 or E-AC-3 track.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        return new Ac3Muxer(stream, tracks, metadata, context, leaveOpen);
    }
}
