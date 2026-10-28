using Egami.Colors;
using Egami.Pixels;

namespace Egami.Video;

/// <summary>
/// A decoded picture with its time: a <see cref="YuvVideoFrame"/> as decoders hand it out (the codec's own planes), or a
/// <see cref="VideoFrame{TPixel}"/> of RGB pixels from <c>VideoReader.ReadFrames</c>. Disposing the frame returns its
/// pixel memory to the memory source it came from.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-planes"/>
/// </example>
public abstract class VideoFrame : IDisposable
{
    private protected VideoFrame(MediaTime timestamp, MediaTime? duration, ColorCodePoints? color)
    {
        Timestamp = timestamp;
        Duration = duration;
        Color = color;
    }

    /// <summary>Gets the presentation timestamp.</summary>
    public MediaTime Timestamp { get; }

    /// <summary>Gets how long the frame is shown, or <see langword="null"/> when the container does not say.</summary>
    public MediaTime? Duration { get; }

    /// <summary>
    /// Gets the frame's color description (H.273 code points), or <see langword="null"/> when neither the stream nor the
    /// container gives one. Motion JPEG frames report BT.601 full range, as JFIF defines, unless the container says otherwise.
    /// </summary>
    public ColorCodePoints? Color { get; }

    /// <summary>
    /// Gets the HDR mastering display and light levels for this frame, or <see langword="null"/> when there are none. Decoders
    /// copy the track's <see cref="VideoTrackInfo.Hdr"/> unless the stream gives its own per frame.
    /// </summary>
    public HdrMetadata? Hdr { get; init; }

    /// <summary>
    /// Gets the frame's HDR10+ dynamic metadata (SMPTE ST 2094-40), or <see langword="null"/> when the stream has none. VP9 reads it
    /// from WebM's BlockAdditional with BlockAddID 4.
    /// </summary>
    public Hdr10PlusMetadata? Hdr10Plus { get; init; }

    /// <summary>
    /// Gets the size the stream asks the frame to be shown at (VP9's render_size), or <see langword="null"/> when it does not say.
    /// It is reported, not applied: the frame keeps its coded size, as libvpx and FFmpeg output it.
    /// </summary>
    public Size? RenderSize { get; init; }

    /// <summary>
    /// Gets whether the frame is two interlaced fields woven together, and which of them comes first in time, or
    /// <see cref="FieldOrder.Progressive"/>. H.264 tells it per frame: from the picture timing SEI's pic_struct, else from how
    /// the frame was coded (two field pictures or an MBAFF frame) and its fields' order counts, as FFmpeg reports it. Other
    /// decoders give <see cref="FieldOrder.Progressive"/>; the track's <see cref="VideoTrackInfo.FieldOrder"/> says what the
    /// container states.
    /// </summary>
    public FieldOrder FieldOrder { get; init; }

    /// <summary>
    /// Gets how many fields longer than its two the frame is shown (H.264 pic_struct): 1 for top, bottom, top or bottom, top,
    /// bottom (3:2 pulldown), 2 for frame doubling, 4 for frame tripling, else 0. A frame lasts (2 + RepeatFields) / 2 frame
    /// periods; <see cref="Duration"/> is the container's and does not count them.
    /// </summary>
    public int RepeatFields { get; init; }

    /// <summary>Gets the width in pixels.</summary>
    public abstract int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public abstract int Height { get; }

    /// <summary>Gets the planes of a <see cref="YuvVideoFrame"/>, or <see langword="null"/> for other frames. Lets encoders take any frame.</summary>
    public virtual YuvRaster? Planes => null;

    /// <summary>Gets the pixels of a <see cref="VideoFrame{TPixel}"/>, or <see langword="null"/> for other frames. Lets encoders take any frame.</summary>
    public virtual Raster? Pixels => null;

    /// <summary>Releases the frame's pixel memory.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the frame's pixel memory.</summary>
    /// <param name="disposing">Whether this is called from <see cref="Dispose()"/>.</param>
    protected abstract void Dispose(bool disposing);
}

/// <summary>
/// A decoded picture in the codec's own planar YUV layout, as decoders return it: for Motion JPEG the JPEG's Y, Cb and Cr
/// planes at their own subsampling, with no color conversion.
/// </summary>
/// <example>
/// <code language="csharp">
/// using var decoder = VideoCodecs.CreateDecoder(videoTrack);
/// decoder.Send(packet);
/// while (decoder.TryReceive(out YuvVideoFrame? frame))
/// {
///     using (frame)
///         Console.WriteLine($"{frame.Raster.ChromaFormat} {frame.Width}x{frame.Height} at {frame.Timestamp}");
/// }
/// </code>
/// </example>
public sealed class YuvVideoFrame : VideoFrame
{
    /// <summary>Creates a frame that takes ownership of <paramref name="raster"/>.</summary>
    /// <param name="raster">The planes. Disposed with the frame.</param>
    /// <param name="timestamp">The presentation timestamp.</param>
    /// <param name="duration">How long the frame is shown, if known.</param>
    /// <param name="color">The color description, if known.</param>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is <see langword="null"/>.</exception>
    public YuvVideoFrame(YuvRaster raster, MediaTime timestamp, MediaTime? duration = null, ColorCodePoints? color = null)
        : base(timestamp, duration, color)
    {
        ArgumentNullException.ThrowIfNull(raster);
        Raster = raster;
    }

    /// <summary>Gets the planes.</summary>
    public YuvRaster Raster { get; }

    /// <inheritdoc/>
    public override int Width => Raster.Width;

    /// <inheritdoc/>
    public override int Height => Raster.Height;

    /// <inheritdoc/>
    public override YuvRaster Planes => Raster;

    /// <summary>
    /// Creates a frame from an interleaved YUV layout such as NV12, P010 or YUY2, as operating system decoders, GPUs and
    /// webcams hand them out. The planes are copied, so the caller keeps <paramref name="data"/>.
    /// </summary>
    /// <param name="format">The layout.</param>
    /// <param name="data">The samples: the luma plane, followed by the chroma plane unless <paramref name="chroma"/> is given.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="color">The matrix, range and chroma siting.</param>
    /// <param name="timestamp">The presentation timestamp.</param>
    /// <param name="bitDepth">The bit depth for <see cref="YuvPackedFormat.P016"/> (10 or 12); other layouts imply theirs.</param>
    /// <param name="stride">The row length of <paramref name="data"/> in bytes, or 0 for packed rows.</param>
    /// <param name="chroma">The chroma plane of a two-plane layout when it is not right after the luma plane.</param>
    /// <param name="chromaStride">The row length of <paramref name="chroma"/> in bytes, or 0 for packed rows.</param>
    /// <param name="context">The context the planes are allocated from, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The frame. The caller must dispose it.</returns>
    public static YuvVideoFrame FromPacked(YuvPackedFormat format, ReadOnlySpan<byte> data, int width, int height, YuvColor color, MediaTime timestamp,
        int bitDepth = 12, int stride = 0, ReadOnlySpan<byte> chroma = default, int chromaStride = 0, EgamiContext? context = null)
    {
        YuvRaster raster = format switch
        {
            YuvPackedFormat.Nv12 or YuvPackedFormat.Nv21 => YuvRaster.Create(width, height, ChromaFormat.Yuv420, color, context: context),
            YuvPackedFormat.P010 => YuvRaster.Create(width, height, ChromaFormat.Yuv420, 10, color, context: context),
            YuvPackedFormat.P016 => YuvRaster.Create(width, height, ChromaFormat.Yuv420, bitDepth, color, context: context),
            YuvPackedFormat.Yuyv or YuvPackedFormat.Uyvy => YuvRaster.Create(width, height, ChromaFormat.Yuv422, color, context: context),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown packed layout."),
        };
        try
        {
            raster.CopyFromPacked(format, data, stride, chroma, chromaStride);
            return new YuvVideoFrame(raster, timestamp, color: null);
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Copies the planes into an interleaved YUV layout. See <see cref="YuvRaster.CopyToPacked"/>.</summary>
    /// <param name="format">The layout. Its chroma format and bit depth must match the planes'.</param>
    /// <param name="data">Where to write the luma plane, followed by the chroma plane unless <paramref name="chroma"/> is given.</param>
    /// <param name="stride">The row length of <paramref name="data"/> in bytes, or 0 for packed rows.</param>
    /// <param name="chroma">Where to write the chroma plane of a two-plane layout, or empty to follow the luma plane.</param>
    /// <param name="chromaStride">The row length of <paramref name="chroma"/> in bytes, or 0 for packed rows.</param>
    public void CopyToPacked(YuvPackedFormat format, Span<byte> data, int stride = 0, Span<byte> chroma = default, int chromaStride = 0) =>
        Raster.CopyToPacked(format, data, stride, chroma, chromaStride);

    /// <summary>
    /// Converts the planes to RGB pixels in a new raster, with the planes' matrix and range (<see cref="YuvRaster.Color"/>).
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="context">The context whose memory source the raster comes from, or <see langword="null"/> for the planes' own.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    public Raster<TPixel> ToRaster<TPixel>(EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> target = Egami.Raster.Create<TPixel>(Width, Height, context);
        try
        {
            CopyTo(target);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Converts the planes to RGB in <paramref name="target"/>'s encoding: the frame's <see cref="VideoFrame.Color"/> says what it
    /// is coded in, and a PQ or HLG frame going to an SDR target is tone mapped (<see cref="ColorTransformOptions.ToneMapping"/>,
    /// BT.2390 by default) from its static <see cref="VideoFrame.Hdr"/> metadata, through Egami's color pipeline. The frame's own
    /// values are left as they are; <see cref="ToRaster{TPixel}(EgamiContext?)"/> gives them unconverted.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="target">The encoding to convert to, such as <see cref="ColorProfile.Srgb"/>.</param>
    /// <param name="options">The rendering intent and tone mapping, or <see langword="null"/> for <see cref="ColorTransformOptions.Default"/>.</param>
    /// <returns>The raster, tagged with <paramref name="target"/>. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp">
    /// using var reader = VideoReader.Open("hdr10.webm");
    /// foreach (YuvVideoFrame frame in reader.ReadFrames())
    ///     using (frame)
    ///     using (Raster&lt;Rgba8&gt; sdr = frame.ToRaster&lt;Rgba8&gt;(ColorProfile.Srgb))
    ///         sdr.Save($"frame-{frame.Timestamp.Ticks}.png");
    /// </code>
    /// </example>
    public Raster<TPixel> ToRaster<TPixel>(ColorProfile target, ColorTransformOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(target);
        using Raster<Rgba16> coded = Raster.ToRaster<Rgba16>();
        if (Color is { } color)
        {
            coded.Metadata.CodePoints = color with { MatrixCoefficients = 0, FullRange = true };
        }

        coded.Metadata.Hdr = Hdr ?? coded.Metadata.Hdr;
        return coded.ConvertColor<TPixel>(target, options);
    }

    /// <summary>Converts the planes to RGB pixels into an existing raster of the same size, so a frame loop reuses its memory.</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="target">The raster to fill.</param>
    /// <exception cref="ArgumentException"><paramref name="target"/> has a different size.</exception>
    public void CopyTo<TPixel>(Raster<TPixel> target)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(target);
        Raster.CopyTo(target);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Raster.Dispose();
        }
    }
}

/// <summary>A decoded picture converted to <typeparamref name="TPixel"/> pixels, as <c>VideoReader.ReadFrames</c> returns it.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <example>
/// <code language="csharp">
/// using var reader = VideoReader.Open("camera.avi");
/// foreach (VideoFrame&lt;Rgb8&gt; frame in reader.ReadFrames&lt;Rgb8&gt;())
/// {
///     using (frame)
///         frame.Raster.Save($"frame-{frame.Timestamp.Ticks}.png");
/// }
/// </code>
/// </example>
public sealed class VideoFrame<TPixel> : VideoFrame
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>Creates a frame that takes ownership of <paramref name="raster"/>.</summary>
    /// <param name="raster">The pixels. Disposed with the frame.</param>
    /// <param name="timestamp">The presentation timestamp.</param>
    /// <param name="duration">How long the frame is shown, if known.</param>
    /// <param name="color">The color description, if known.</param>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is <see langword="null"/>.</exception>
    public VideoFrame(Raster<TPixel> raster, MediaTime timestamp, MediaTime? duration = null, ColorCodePoints? color = null)
        : base(timestamp, duration, color)
    {
        ArgumentNullException.ThrowIfNull(raster);
        Raster = raster;
    }

    /// <summary>Gets the pixels.</summary>
    public Raster<TPixel> Raster { get; }

    /// <inheritdoc/>
    public override int Width => Raster.Width;

    /// <inheritdoc/>
    public override int Height => Raster.Height;

    /// <inheritdoc/>
    public override Raster Pixels => Raster;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Raster.Dispose();
        }
    }
}
