using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Decodes a VP8 video stream (RFC 6386) a frame at a time, as WebM and IVF files carry it: key frames and inter frames, with the
/// last, golden and alternate references, motion vectors and hidden frames. The frames equal libvpx's sample for sample. Feed the
/// frames in stream order; decoding starts at a key frame. Containers are not read here: Egami.Video reads WebM and IVF and uses
/// this decoder, and any other source of VP8 frames can too.
/// </summary>
/// <remarks>
/// The decoder keeps four frame buffers from the context's <see cref="EgamiContext.Memory"/> for its life and decodes a frame's
/// macroblock rows on up to <see cref="EgamiContext.MaxParallelism"/> threads (as many as the frame's token partitions allow). Its
/// frame size is checked against the context's <see cref="EgamiContext.Limits"/>. An instance is not thread-safe.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Vp8.cs" region="vp8-stream"/>
/// </example>
public sealed class Vp8StreamDecoder : IDisposable
{
    private readonly Vp8VideoDecoder _video;
    private byte[] _data = [];
    private bool _disposed;

    /// <summary>Starts a stream.</summary>
    /// <param name="context">The memory, threads and limits to use; <see cref="EgamiContext.Default"/> when null.</param>
    public Vp8StreamDecoder(EgamiContext? context = null)
    {
        EgamiContext c = context ?? EgamiContext.Default;
        _video = new Vp8VideoDecoder(c.MaxParallelism, c.Memory, c.Limits);
    }

    /// <summary>
    /// Gets or sets whether broken frames are concealed instead of throwing. When set, a frame whose data runs out has the
    /// macroblocks it could not read copied from the previous frame (mid-gray when there is none), a frame whose header cannot be
    /// read shows the previous frame again, and an inter frame with no key frame before it predicts from mid-gray; the frame's
    /// <see cref="Vp8FrameInfo.IsConcealed"/> says so. Defaults to false: broken data throws <see cref="CorruptDataException"/>.
    /// </summary>
    public bool ConcealErrors
    {
        get => _video.Conceal;
        set => _video.Conceal = value;
    }

    /// <summary>
    /// Decodes one VP8 frame and updates the references. When the result's <see cref="Vp8FrameInfo.IsShown"/> is true,
    /// <see cref="CopyFrameTo"/> then copies the picture out; a hidden frame (typically an alternate reference) only updates the
    /// references.
    /// </summary>
    /// <param name="frame">The frame's data, as a WebM block or an IVF frame holds it.</param>
    /// <returns>What the frame was.</returns>
    /// <exception cref="CorruptDataException">The data is broken, or the stream does not start with a key frame, and <see cref="ConcealErrors"/> is false.</exception>
    /// <exception cref="LimitExceededException">A key frame's size breaks the context's safety limits.</exception>
    public Vp8FrameInfo Decode(ReadOnlySpan<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_data.Length < frame.Length)
        {
            _data = new byte[Math.Max(frame.Length, _data.Length * 2)];
        }

        frame.CopyTo(_data);
        Vp8FrameHeader header = _video.Decode(_data, 0, frame.Length);
        return new Vp8FrameInfo(header.KeyFrame, _video.Shown is not null, _video.Width, _video.Height, header.Version, _video.Concealed);
    }

    /// <summary>
    /// Copies the frame the last <see cref="Decode"/> showed into <paramref name="destination"/>: its Y, U and V planes, which
    /// the decoder's references do not share, so the copy can be changed or kept.
    /// </summary>
    /// <param name="destination">A 4:2:0 8-bit raster of the frame's size; its color is left as it is (VP8 is BT.601, limited range).</param>
    /// <exception cref="InvalidOperationException">The last frame decoded was not shown, or none has been.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not 4:2:0 at the frame's size.</exception>
    public void CopyFrameTo(YuvRaster<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Vp8FrameBuffer frame = _video.Shown ?? throw new InvalidOperationException("No shown frame to copy: the last frame decoded was hidden, concealed with nothing to show, or none has been decoded.");
        if (destination.ChromaFormat != ChromaFormat.Yuv420 || destination.Width != frame.Width || destination.Height != frame.Height)
        {
            throw new ArgumentException($"The destination must be 4:2:0 at {frame.Width}×{frame.Height}; it is {destination.ChromaFormat} at {destination.Width}×{destination.Height}.", nameof(destination));
        }

        Copy(frame.Y, frame.OriginY, frame.StrideY, destination.Y, frame.Width, frame.Height);
        Copy(frame.U, frame.OriginC, frame.StrideC, destination.U!, (frame.Width + 1) / 2, (frame.Height + 1) / 2);
        Copy(frame.V, frame.OriginC, frame.StrideC, destination.V!, (frame.Width + 1) / 2, (frame.Height + 1) / 2);
    }

    /// <summary>Drops the references; the next frame must be a key frame (or, with <see cref="ConcealErrors"/>, is concealed).</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _video.Reset();
    }

    /// <summary>Returns the frame buffers to the memory source and stops the decoder's threads.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _video.Dispose();
        }
    }

    private static void Copy(ReadOnlySpan<byte> plane, int origin, int stride, YuvPlane<byte> to, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            plane.Slice(origin + (y * stride), width).CopyTo(to.Row(y));
        }
    }
}

/// <summary>What a VP8 frame was, as <see cref="Vp8StreamDecoder.Decode"/> returns it.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Vp8.cs" region="vp8-stream"/>
/// </example>
/// <param name="IsKeyFrame">Whether the frame is a key frame, which decodes on its own and resets the references.</param>
/// <param name="IsShown">Whether the frame is shown; a hidden frame (typically an alternate reference) only updates the references.</param>
/// <param name="Width">The frame width, from the last key frame.</param>
/// <param name="Height">The frame height, from the last key frame.</param>
/// <param name="Version">The VP8 version, 0 to 3: 0 uses the six-tap prediction filters and the normal loop filter, 1 to 3 simpler ones.</param>
/// <param name="IsConcealed">Whether <see cref="Vp8StreamDecoder.ConcealErrors"/> concealed broken data in this frame.</param>
public readonly record struct Vp8FrameInfo(bool IsKeyFrame, bool IsShown, int Width, int Height, int Version, bool IsConcealed);
