using System.Buffers.Binary;

using Egami.Video.Codecs.MotionJpeg;

namespace Egami.Video.Codecs;

/// <summary>
/// Motion JPEG: every frame a complete JPEG image, decoded and encoded by Egami's JPEG codec. Decoding fixes frames without
/// a DHT segment, weaves interlaced field pairs, ignores junk around the image and decodes several frames at once. Frames
/// come out as the JPEG's own YUV planes, BT.601 full range unless the container says otherwise.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(videoTrack, new VideoDecoderOptions { ScaleDenominator = 4 }, MediaContext.Default);
/// </code>
/// </example>
public sealed class MotionJpegCodec : VideoCodec
{
    private MotionJpegCodec()
    {
    }

    /// <summary>Gets the Motion JPEG codec.</summary>
    public static MotionJpegCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "Motion JPEG";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.MotionJpeg;

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <summary>Decodes <see cref="CodecIds.MotionJpeg"/> tracks, and Matroska's <see cref="CodecIds.VfwFourCC"/> tracks whose BITMAPINFOHEADER names a Motion JPEG FourCC.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether this codec decodes it.</returns>
    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.CodecId == CodecIds.MotionJpeg)
        {
            return true;
        }

        return track.CodecId == CodecIds.VfwFourCC && track.CodecPrivate.Length >= 20
            && Containers.Avi.AviCodecs.IsMotionJpeg(BinaryPrimitives.ReadUInt32LittleEndian(track.CodecPrivate.Span[16..]));
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-codecs"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new MotionJpegDecoder(track, options, context);
    }

    /// <summary>
    /// Creates an encoder. Quality defaults to 75 and RGB frames to 4:2:0, as Egami's JPEG writer does; frames given as planes
    /// keep their own subsampling.
    /// </summary>
    /// <param name="options">The picture size, frame rate, quality and chroma format.</param>
    /// <param name="context">The context.</param>
    /// <returns>The encoder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-encode"/>
    /// </example>
    public override IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MotionJpegEncoder(options);
    }
}
