using Egami.Video.Codecs.Av1;
using Egami.Video.Containers.Obu;

namespace Egami.Video.Containers;

/// <summary>
/// A raw AV1 stream of low-overhead OBUs (.obu; AV1 specification section 5.2), as aomenc writes with <c>--obu</c> and aomdec and
/// dav1d read: each temporal unit starts with a temporal delimiter OBU, and every OBU has its size field. There is no header,
/// index or timestamp, so temporal unit n is at n frames of <see cref="FrameRate"/>. Reading a file and writing it back gives the
/// same bytes; the writer adds a temporal delimiter to a packet that has none. The track's size, bit depth, chroma format and
/// av1C come from the first sequence header.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-raw"/>
/// </example>
public sealed class Av1ObuFormat : ContainerFormat
{
    /// <summary>Gets the format at 30 frames a second.</summary>
    public static Av1ObuFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AV1 OBU";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".obu"];

    /// <inheritdoc/>
    public override int SignatureLength => 3;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <summary>
    /// Gets the frame rate readers time the temporal units with, as the file has no timestamps. Default 30 frames a second.
    /// Register a format with another rate through <see cref="MediaContext.WithContainer"/>, or open it directly.
    /// </summary>
    public Rational FrameRate { get; init; } = new(30, 1);

    /// <inheritdoc/>
    /// <remarks>The file must start with a temporal delimiter OBU with its size field.</remarks>
    public override bool IsMatch(ReadOnlySpan<byte> header) =>
        header.StartsWith(Av1Obus.TemporalDelimiterObu) || (header.Length >= 3 && header[0] == 0x16 && (header[1] & 7) == 0 && header[2] == 0);

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="FrameRate"/> is not positive.</exception>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new Av1ObuDemuxer(stream, context, leaveOpen, FrameRate, annexB: false, Name);
    }

    /// <summary>Creates a muxer for one AV1 video track. Non-seekable output works: nothing is patched.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one AV1 track.</param>
    /// <param name="metadata">Ignored: the format has no tags.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one AV1 video track.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen) =>
        new Av1ObuMuxer(stream, tracks, leaveOpen, annexB: false);
}

/// <summary>
/// A raw AV1 stream in the length-delimited format of the AV1 specification's Annex B, as the AV1 conformance streams and aomenc
/// <c>--annexb=1</c> write it: each temporal unit, each frame unit in it and each OBU in that carries its length as leb128, and
/// OBUs have no size field. Readers give each temporal unit as a packet of low-overhead OBUs with their size fields, the form
/// every other container uses; writers turn such packets back, dropping the size fields and adding the lengths, with a temporal
/// delimiter at the start of each unit. There are no timestamps: temporal unit n is at n frames of <see cref="FrameRate"/>.
/// Reading a file and writing it back gives the same bytes. Not detected from the extension, which .obu files share: an Annex B
/// file is recognized by its first temporal delimiter.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-raw"/>
/// </example>
public sealed class Av1AnnexBFormat : ContainerFormat
{
    /// <summary>Gets the format at 30 frames a second.</summary>
    public static Av1AnnexBFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AV1 Annex B";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".obu"];

    /// <inheritdoc/>
    public override int SignatureLength => 16;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <summary>
    /// Gets the frame rate readers time the temporal units with, as the file has no timestamps. Default 30 frames a second.
    /// Register a format with another rate through <see cref="MediaContext.WithContainer"/>, or open it directly.
    /// </summary>
    public Rational FrameRate { get; init; } = new(30, 1);

    /// <inheritdoc/>
    /// <remarks>
    /// The file must start with a temporal unit whose first frame unit starts with a temporal delimiter OBU, each length fitting
    /// in the one around it.
    /// </remarks>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        (ulong unitSize, int a) = Av1Obus.ReadLeb128(header);
        if (a < 0)
        {
            return false;
        }

        (ulong frameSize, int b) = Av1Obus.ReadLeb128(header[a..]);
        if (b < 0 || frameSize < 2 || frameSize + (ulong)b > unitSize)
        {
            return false;
        }

        (ulong obuLength, int c) = Av1Obus.ReadLeb128(header[(a + b)..]);
        int at = a + b + c;
        if (c < 0 || obuLength == 0 || obuLength + (ulong)c > frameSize || at >= header.Length)
        {
            return false;
        }

        // A temporal delimiter: no payload, so its length is its header (and a zero size field when it has one).
        byte obu = header[at];
        int expected = 1 + ((obu & 4) != 0 ? 1 : 0) + ((obu & 2) != 0 ? 1 : 0);
        return (obu & 0x81) == 0 && ((obu >> 3) & 15) == Av1Obus.TemporalDelimiter && obuLength == (ulong)expected;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="FrameRate"/> is not positive.</exception>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new Av1ObuDemuxer(stream, context, leaveOpen, FrameRate, annexB: true, Name);
    }

    /// <summary>Creates a muxer for one AV1 video track. Non-seekable output works: nothing is patched.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one AV1 track.</param>
    /// <param name="metadata">Ignored: the format has no tags.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one AV1 video track.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen) =>
        new Av1ObuMuxer(stream, tracks, leaveOpen, annexB: true);
}
