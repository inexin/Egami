using Egami.Video.Containers.Y4m;

namespace Egami.Video.Containers;

/// <summary>
/// YUV4MPEG2 (.y4m): uncompressed planar YUV frames behind a one-line text header, the common exchange format of video
/// tools and codec test suites. Reads 4:2:0, 4:2:2, 4:4:4 and gray at 8, 10 and 12 bits, and 4:4:4 with alpha; writes the
/// same. Reading a file and writing it back gives the same bytes.
/// </summary>
/// <example>
/// <code language="csharp">
/// using var writer = VideoWriter.Create("out.y4m", new VideoWriterOptions { Codec = RawVideoCodec.Instance, FrameRate = new Rational(25, 1) });
/// writer.WriteFrame(planes);
/// </code>
/// </example>
public sealed class Y4mFormat : ContainerFormat
{
    private Y4mFormat()
    {
    }

    /// <summary>Gets the YUV4MPEG2 format.</summary>
    public static Y4mFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "YUV4MPEG2";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".y4m"];

    /// <inheritdoc/>
    public override int SignatureLength => 10;

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header) => header.StartsWith("YUV4MPEG2 "u8);

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new Y4mDemuxer(stream, context, leaveOpen);
    }

    /// <summary>Creates a muxer for one planar uncompressed video track. Non-seekable output works: nothing is patched.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The one video track.</param>
    /// <param name="metadata">Ignored: YUV4MPEG2 has no tags.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">The tracks are not one planar uncompressed video track.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen) =>
        new Y4mMuxer(stream, tracks, leaveOpen);
}
