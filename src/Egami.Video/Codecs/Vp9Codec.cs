using Egami.Video.Codecs.Vp9;

namespace Egami.Video.Codecs;

/// <summary>
/// VP9, as WebM, Matroska and IVF carry it, decoded in all four profiles: 8, 10 and 12 bit, 4:2:0, 4:2:2, 4:4:4 and RGB, with
/// every frame equal to libvpx's sample for sample. Frames come out at their own coded size (a stream may change size between
/// keyframes) as 8-bit planes, or 16-bit planes holding 10 or 12 bits; hidden frames (alternate references) give no frame. A
/// WebM track with alpha (<see cref="VideoTrackInfo.HasAlpha"/>) decodes its second VP9 stream, carried in each packet's
/// addition 1, into the frame's alpha plane unless <see cref="VideoDecoderOptions.DecodeAlpha"/> is off. 4:4:0 streams are not
/// decoded. Decode only.
/// </summary>
/// <remarks>
/// The frame's color takes the matrix and range from the VP9 header and the primaries and transfer from the container
/// (<see cref="VideoTrackInfo.Color"/>). Broken data follows <see cref="VideoDecoderOptions.Errors"/> and is reported through the
/// Egami context's diagnostics as <c>Vp9.CorruptFrame</c> (the frame's data is broken), <c>Vp9.MissingReference</c> (an inter
/// frame with no keyframe before it), <c>Vp9.UnsupportedSubsampling</c> (a 4:4:0 stream, which gives no frames),
/// <c>Vp9.KeyframeFlagMismatch</c> (the container's keyframe flag disagrees with the frame's own bit, which wins) or
/// <c>Vp9.CodecPrivateMismatch</c> (the track's CodecPrivate disagrees with the stream, whose header wins). In
/// <see cref="DecodeErrorMode.Conceal"/> mode a packet that cannot be decoded shows the last good frame again, until the next
/// keyframe.
/// </remarks>
/// <example>
/// <code language="csharp">
/// using var reader = VideoReader.Open("clip.webm");
/// foreach (YuvVideoFrame frame in reader.ReadFrames())
///     using (frame) Console.WriteLine($"{frame.Timestamp}: {frame.Width}x{frame.Height}, {frame.Raster.BitDepth} bit {frame.Raster.ChromaFormat}");
/// </code>
/// </example>
public sealed class Vp9Codec : VideoCodec
{
    private Vp9Codec()
    {
    }

    /// <summary>Gets the VP9 codec.</summary>
    public static Vp9Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "VP9";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Vp9;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-codecs"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        options ??= VideoDecoderOptions.Default;
        if (options.ScaleDenominator != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "VP9 decodes at full size only; the scale denominator must be 1.");
        }

        return new Vp9VideoDecoder(track, options, context);
    }
}
