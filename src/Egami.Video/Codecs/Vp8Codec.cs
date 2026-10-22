using Egami.Video.Codecs.Vp8;

namespace Egami.Video.Codecs;

/// <summary>
/// VP8 (RFC 6386), as WebM, Matroska and IVF carry it, decoded by Egami's <see cref="Egami.Formats.Webp.Vp8StreamDecoder"/>: every
/// frame equals libvpx's sample for sample. Frames come out as 8-bit 4:2:0 planes, BT.601 limited range unless the container says
/// otherwise; hidden frames (alternate references) give no frame. A WebM track with alpha (<see cref="VideoTrackInfo.HasAlpha"/>)
/// decodes its second VP8 stream, carried in each packet's addition 1, into the frame's alpha plane unless
/// <see cref="VideoDecoderOptions.DecodeAlpha"/> is off. Decode only.
/// </summary>
/// <remarks>
/// Broken data follows <see cref="VideoDecoderOptions.Errors"/> and is reported through the Egami context's diagnostics as
/// <c>Vp8.CorruptFrame</c> (the frame's data is broken), <c>Vp8.MissingReference</c> (an inter frame with no keyframe before it) or
/// <c>Vp8.KeyframeFlagMismatch</c> (the container's keyframe flag disagrees with the frame's own bit, which wins).
/// </remarks>
/// <example>
/// <code language="csharp">
/// using var reader = VideoReader.Open("clip.webm");
/// foreach (YuvVideoFrame frame in reader.ReadFrames())
///     using (frame) Console.WriteLine($"{frame.Timestamp}: {frame.Width}x{frame.Height}, alpha {frame.Raster.HasAlpha}");
/// </code>
/// </example>
public sealed class Vp8Codec : VideoCodec
{
    private Vp8Codec()
    {
    }

    /// <summary>Gets the VP8 codec.</summary>
    public static Vp8Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "VP8";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Vp8;

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
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "VP8 decodes at full size only; the scale denominator must be 1.");
        }

        return new Vp8VideoDecoder(track, options, context);
    }
}
