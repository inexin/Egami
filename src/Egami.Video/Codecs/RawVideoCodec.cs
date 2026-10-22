using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs.Raw;

namespace Egami.Video.Codecs;

/// <summary>
/// Uncompressed video (<see cref="CodecIds.Uncompressed"/>): I420, YV12, Y42B, 444P, Y800, NV12, NV21, YUY2, UYVY and v210 by
/// FourCC (<see cref="VideoTrackInfo.FourCC"/>), plain planar YUV at 8 to 12 bits with optional alpha as in YUV4MPEG2, and
/// AVI's bottom-up 24-bit BI_RGB (as <see cref="CodecIds.VfwFourCC"/>), whose frames come out as G, B and R planes
/// (<see cref="YuvMatrix.Identity"/>). Decoding and encoding copy samples, so they are lossless.
/// </summary>
/// <example>
/// <code language="csharp">
/// using var reader = VideoReader.Open("clip.y4m");
/// foreach (YuvVideoFrame frame in reader.ReadFrames())
///     using (frame) Console.WriteLine($"{frame.Raster.ChromaFormat} {frame.Raster.BitDepth}-bit");
/// </code>
/// </example>
public sealed class RawVideoCodec : VideoCodec
{
    private RawVideoCodec()
    {
    }

    /// <summary>Gets the uncompressed video codec.</summary>
    public static RawVideoCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "Uncompressed video";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Uncompressed;

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <inheritdoc/>
    public override bool CanDecode(TrackInfo track) => track is VideoTrackInfo video && RawLayout.ForTrack(video) is not null;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-codecs"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        RawLayout layout = RawLayout.ForTrack(track) ?? throw new UnsupportedFormatException($"Track {track.Number} is not uncompressed video this codec knows.");
        return new Decoder(track, layout, context);
    }

    /// <summary>
    /// Creates an encoder for planar frames. The layout is the first frame's chroma format, bit depth and alpha, named by
    /// FourCC when one exists (I420, Y42B, 444P, Y800); RGB frames are converted to 4:2:0 or <see cref="VideoEncoderOptions.ChromaFormat"/>.
    /// </summary>
    /// <param name="options">The picture size and frame rate.</param>
    /// <param name="context">The context.</param>
    /// <returns>The encoder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-encode"/>
    /// </example>
    public override IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new Encoder(options, context);
    }

    private sealed class Decoder(VideoTrackInfo track, RawLayout layout, MediaContext context) : IVideoDecoder
    {
        private readonly Queue<YuvVideoFrame> _ready = new();
        private readonly ColorCodePoints? _color = track.Color;

        public void Send(EncodedPacket packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            long size = layout.FrameSize(track.Width, track.Height);
            if (track.Width <= 0 || track.Height <= 0 || packet.Data.Length < size)
            {
                throw new CorruptDataException("Uncompressed video", 0, $"The packet holds {packet.Data.Length} bytes; a {track.Width}x{track.Height} frame needs {size}.");
            }

            SafetyLimits limits = context.Egami.Limits;
            if (track.Width > limits.MaxWidth || track.Height > limits.MaxHeight || (long)track.Width * track.Height > limits.MaxPixels)
            {
                throw new LimitExceededException("MaxPixels", limits.MaxPixels, (long)track.Width * track.Height);
            }

            YuvColor color = _color is { } c && c.TryGetYuvColor(track.ChromaSiting ?? ChromaSiting.Center, out YuvColor mapped)
                ? mapped
                : new YuvColor(YuvMatrix.Bt601, _color is { FullRange: true } ? YuvRange.Full : YuvRange.Limited, track.ChromaSiting ?? ChromaSiting.Center);
            YuvRaster planes = layout.Create(track.Width, track.Height, color, context.Egami);
            try
            {
                layout.Read(packet.Data.Span, planes);
            }
            catch
            {
                planes.Dispose();
                throw;
            }

            _ready.Enqueue(new YuvVideoFrame(planes, packet.Timestamp, packet.Duration, _color) { Hdr = track.Hdr });
        }

        public void Flush()
        {
        }

        public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame) => _ready.TryDequeue(out frame);

        public void Reset()
        {
            while (_ready.TryDequeue(out YuvVideoFrame? frame))
            {
                frame.Dispose();
            }
        }

        public void Dispose() => Reset();
    }

    private sealed class Encoder(VideoEncoderOptions options, MediaContext context) : IVideoEncoder
    {
        private readonly Queue<EncodedPacket> _ready = new();
        private RawLayout? _layout;
        private VideoTrackInfo? _track;

        public VideoTrackInfo Track => _track ?? Describe(RawLayout.Planar(options.ChromaFormat ?? ChromaFormat.Yuv420, 8, false));

        public void Send(VideoFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Width != options.Width || frame.Height != options.Height)
            {
                throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}; the encoder makes {options.Width}x{options.Height}.", nameof(frame));
            }

            YuvRaster? converted = null;
            YuvRaster planes = frame.Planes ?? (converted = YuvRaster.FromRaster(frame.Pixels!, options.ChromaFormat ?? ChromaFormat.Yuv420, new YuvColor(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Left)));
            try
            {
                _layout ??= RawLayout.Planar(planes.ChromaFormat, planes.BitDepth, planes.HasAlpha);
                _track ??= Describe(_layout) with { Color = frame.Color };
                if (!_layout.Matches(planes))
                {
                    throw new ArgumentException("Every frame must have the first frame's chroma format, bit depth and alpha.", nameof(frame));
                }

                int size = checked((int)_layout.FrameSize(planes.Width, planes.Height));
                EncodedPacket packet = EncodedPacket.Rent(1, size, frame.Timestamp.Rescale(Track.TimeBase));
                _layout.Write(planes, packet.GetWritableData().Span);
                packet.IsKeyframe = true;
                packet.Duration = new MediaTime(1, Track.TimeBase);
                _ready.Enqueue(packet);
            }
            finally
            {
                converted?.Dispose();
            }
        }

        public void Flush()
        {
        }

        public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet) => _ready.TryDequeue(out packet);

        public void Dispose()
        {
            while (_ready.TryDequeue(out EncodedPacket? packet))
            {
                packet.Dispose();
            }

            _ = context;
        }

        private VideoTrackInfo Describe(RawLayout layout) => new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.Uncompressed,
            TimeBase = options.FrameRate.Reciprocal(),
            Width = options.Width,
            Height = options.Height,
            FrameRate = options.FrameRate,
            FourCC = layout.FourCC,
            ChromaFormat = layout.Chroma,
            BitDepth = layout.BitDepth,
            HasAlpha = layout.Alpha,
        };
    }
}
