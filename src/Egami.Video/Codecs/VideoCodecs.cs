using System.Diagnostics.CodeAnalysis;

using Egami.Pixels;

namespace Egami.Video.Codecs;

/// <summary>Creates decoders and encoders from the codecs registered in a <see cref="MediaContext"/>.</summary>
/// <example>
/// <code language="csharp">
/// using IVideoDecoder&lt;Rgb8&gt; decoder = VideoCodecs.CreateDecoder&lt;Rgb8&gt;(videoTrack);
/// decoder.Send(packet);
/// while (decoder.TryReceive(out VideoFrame&lt;Rgb8&gt;? frame))
///     using (frame) Process(frame);
/// </code>
/// </example>
public static class VideoCodecs
{
    /// <summary>Gets the Motion JPEG codec.</summary>
    public static VideoCodec MotionJpeg => MotionJpegCodec.Instance;

    /// <summary>Gets the VP8 codec.</summary>
    public static VideoCodec Vp8 => Vp8Codec.Instance;

    /// <summary>Gets the VP9 codec.</summary>
    public static VideoCodec Vp9 => Vp9Codec.Instance;

    /// <summary>Creates a decoder for a track, which hands out frames in the codec's own planar YUV layout.</summary>
    /// <param name="track">The track.</param>
    /// <param name="options">How to decode, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered codec decodes the track.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-decode"/>
    /// </example>
    public static IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        context ??= MediaContext.Default;
        VideoCodec codec = context.FindCodec(track)
            ?? throw new UnsupportedFormatException($"No registered codec decodes '{track.CodecId}' (track {track.Number}).");
        return codec.CreateDecoder(track, options, context);
    }

    /// <summary>Creates a decoder for a track that converts its frames to <typeparamref name="TPixel"/> pixels.</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="track">The track.</param>
    /// <param name="options">How to decode, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered codec decodes the track.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-decode"/>
    /// </example>
    public static IVideoDecoder<TPixel> CreateDecoder<TPixel>(VideoTrackInfo track, VideoDecoderOptions? options = null, MediaContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        context ??= MediaContext.Default;
        return new ConvertingDecoder<TPixel>(CreateDecoder(track, options, context), context.Egami);
    }

    /// <summary>Finds the registered codec with a codec id.</summary>
    /// <param name="codecId">The Matroska codec id, such as <see cref="CodecIds.MotionJpeg"/>.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The codec.</returns>
    /// <exception cref="UnsupportedFormatException">No codec with that id is registered.</exception>
    public static VideoCodec Get(string codecId, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(codecId);
        context ??= MediaContext.Default;
        return context.Codecs.FirstOrDefault(c => string.Equals(c.CodecId, codecId, StringComparison.Ordinal))
            ?? throw new UnsupportedFormatException($"No codec '{codecId}' is registered.");
    }

    private sealed class ConvertingDecoder<TPixel>(IVideoDecoder inner, EgamiContext egami) : IVideoDecoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        public void Send(EncodedPacket packet) => inner.Send(packet);

        public void Flush() => inner.Flush();

        public void Reset() => inner.Reset();

        public bool TryReceive([NotNullWhen(true)] out VideoFrame<TPixel>? frame)
        {
            if (!inner.TryReceive(out YuvVideoFrame? yuv))
            {
                frame = null;
                return false;
            }

            using (yuv)
            {
                frame = new VideoFrame<TPixel>(yuv.ToRaster<TPixel>(egami), yuv.Timestamp, yuv.Duration, yuv.Color) { Hdr = yuv.Hdr, Hdr10Plus = yuv.Hdr10Plus, RenderSize = yuv.RenderSize };
                return true;
            }
        }

        public void Dispose() => inner.Dispose();
    }
}
