using System.Buffers;
using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Formats;
using Egami.Pixels;

namespace Egami.Video.Codecs.MotionJpeg;

/// <summary>
/// Decodes Motion JPEG through Egami's public planar JPEG decode (<see cref="YuvRaster.Load(ReadOnlyMemory{byte}, EgamiContext?, ReaderOptions?)"/>),
/// after fixing each packet with <see cref="JpegFrame"/>. Frames are independent, so up to
/// <see cref="EgamiContext.MaxParallelism"/> of them decode at once on the thread pool; they come out in order.
/// </summary>
internal sealed class MotionJpegDecoder : IVideoDecoder
{
    /// <summary>JFIF's color: BT.601 matrix, full range, unspecified primaries and transfer (H.273 code points 2, 2, 5).</summary>
    internal static readonly ColorCodePoints JfifColor = new(2, 2, 5, true);

    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _frameContext;
    private readonly ReaderOptions _options;
    private readonly int _parallelism;
    private readonly ColorCodePoints _color;
    private readonly Queue<Task<YuvVideoFrame?>> _inFlight = new();
    private bool _flushing;

    public MotionJpegDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        _track = track;
        options ??= VideoDecoderOptions.Default;
        if (options.ScaleDenominator is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "The scale denominator must be 1, 2, 4 or 8.");
        }

        _parallelism = Math.Max(1, context.Egami.MaxParallelism);

        // With several frames in flight, each frame decodes on one thread.
        _frameContext = _parallelism > 1 ? context.Egami with { MaxParallelism = 1 } : context.Egami;
        int d = options.ScaleDenominator;
        _options = new ReaderOptions
        {
            ReadMetadata = false,
            MinimumSize = d == 1 || track.Width <= 0 || track.Height <= 0 ? null : new Size((track.Width + d - 1) / d, (track.Height + d - 1) / d),
        };
        _color = ColorFor(track);
    }

    /// <summary>The track's color, with JFIF's BT.601 matrix filled in when the container leaves the matrix unspecified.</summary>
    internal static ColorCodePoints ColorFor(VideoTrackInfo track) => track.Color is { } color
        ? color.MatrixCoefficients == 2 ? color with { MatrixCoefficients = JfifColor.MatrixCoefficients } : color
        : JfifColor;

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _flushing = false;

        // Keep a copy: the caller may dispose the packet as soon as Send returns.
        ReadOnlyMemory<byte> data = packet.Data;
        byte[] copy = ArrayPool<byte>.Shared.Rent(data.Length);
        data.Span.CopyTo(copy);
        int length = data.Length;
        MediaTime timestamp = packet.Timestamp;
        MediaTime? duration = packet.Duration;
        if (_parallelism == 1)
        {
            _inFlight.Enqueue(Task.FromResult(DecodeAndReturn(copy, length, timestamp, duration)));
            return;
        }

        if (_inFlight.Count >= _parallelism)
        {
            // Wait for the oldest frame so the work in flight stays bounded; it stays queued for TryReceive.
            ((IAsyncResult)_inFlight.Peek()).AsyncWaitHandle.WaitOne();
        }

        _inFlight.Enqueue(Task.Run(() => DecodeAndReturn(copy, length, timestamp, duration)));
    }

    public void Flush() => _flushing = true;

    public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame)
    {
        while (_inFlight.TryPeek(out Task<YuvVideoFrame?>? head))
        {
            if (!head.IsCompleted && !_flushing && _inFlight.Count < _parallelism)
            {
                break;
            }

            _inFlight.Dequeue();
            YuvVideoFrame? result = head.GetAwaiter().GetResult();
            if (result is not null)
            {
                frame = result;
                return true;
            }
        }

        frame = null;
        return false;
    }

    public void Reset()
    {
        while (_inFlight.TryDequeue(out Task<YuvVideoFrame?>? task))
        {
            try
            {
                task.GetAwaiter().GetResult()?.Dispose();
            }
            catch (EgamiException)
            {
                // Dropped anyway.
            }
        }

        _flushing = false;
    }

    public void Dispose() => Reset();

    private YuvVideoFrame? DecodeAndReturn(byte[] buffer, int length, MediaTime timestamp, MediaTime? duration)
    {
        try
        {
            YuvRaster? planes = Decode(buffer.AsMemory(0, length));
            if (planes is null)
            {
                return null;
            }

            planes = Recolor(planes);
            return new YuvVideoFrame(planes, timestamp, duration, _color) { Hdr = _track.Hdr };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Gives the planes the matrix, range and siting the container states, when they differ from JFIF's, so the color travels
    /// with the frame into RGB conversion and re-encoding. Costs one copy, only for such files.
    /// </summary>
    private YuvRaster Recolor(YuvRaster planes)
    {
        if (!_color.TryGetYuvColor(_track.ChromaSiting ?? planes.Color.Siting, out YuvColor wanted) || wanted == planes.Color || planes is not YuvRaster<byte> source)
        {
            return planes;
        }

        using (planes)
        {
            YuvRaster<byte> target = YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, wanted, source.HasAlpha, _frameContext);
            CopyPlane(source.Y, target.Y);
            if (source.U is { } u && target.U is { } tu)
            {
                CopyPlane(u, tu);
                CopyPlane(source.V!, target.V!);
            }

            return target;
        }

        static void CopyPlane(YuvPlane<byte> from, YuvPlane<byte> to)
        {
            for (int y = 0; y < from.Height; y++)
            {
                from.Row(y).CopyTo(to.Row(y));
            }
        }
    }

    /// <summary>Decodes one packet: one image, or two field images woven into a frame.</summary>
    private YuvRaster? Decode(ReadOnlyMemory<byte> packet)
    {
        Span<JpegFrame.Image> images = stackalloc JpegFrame.Image[2];
        int count = JpegFrame.Find(packet.Span, images);
        if (count == 0)
        {
            throw new CorruptDataException("Motion JPEG", 0, "The packet holds no JPEG image.");
        }

        YuvRaster first = DecodeImage(packet, images[0]);
        bool interlaced = count == 2 && (images[0].Polarity != JpegFrame.Polarity.None || first.Height * 2 <= _track.Height + 1);
        if (!interlaced)
        {
            return first;
        }

        YuvRaster second;
        try
        {
            second = DecodeImage(packet, images[1]);
        }
        catch
        {
            first.Dispose();
            throw;
        }

        using (first)
        using (second)
        {
            bool topFirst = images[0].Polarity != JpegFrame.Polarity.BottomFirst;
            return Weave(topFirst ? first : second, topFirst ? second : first);
        }
    }

    private YuvRaster DecodeImage(ReadOnlyMemory<byte> packet, JpegFrame.Image image)
    {
        if (image.HasDht && image.Start == 0 && image.Length == packet.Length)
        {
            return YuvRaster.Load(packet, _frameContext, _options);
        }

        byte[] fixedBytes = ArrayPool<byte>.Shared.Rent(JpegFrame.FixedLength(image));
        try
        {
            int length = JpegFrame.CopyFixed(packet.Span, image, fixedBytes);
            return YuvRaster.Load(fixedBytes.AsMemory(0, length), _frameContext, _options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fixedBytes);
        }
    }

    /// <summary>Interleaves two field pictures line by line: the top field gives the even rows, the bottom field the odd ones.</summary>
    private YuvRaster<byte> Weave(YuvRaster top, YuvRaster bottom)
    {
        if (top is not YuvRaster<byte> t || bottom is not YuvRaster<byte> b || t.ChromaFormat != b.ChromaFormat || t.Width != b.Width)
        {
            throw new CorruptDataException("Motion JPEG", 0, "The two fields of an interlaced frame differ in size or format.");
        }

        int height = t.Height + b.Height;
        YuvRaster<byte> frame = YuvRaster.Create(t.Width, height, t.ChromaFormat, t.Color, context: _frameContext);
        WeavePlane(t.Y, b.Y, frame.Y);
        if (t.U is { } tu && b.U is { } bu && frame.U is { } fu)
        {
            WeavePlane(tu, bu, fu);
            WeavePlane(t.V!, b.V!, frame.V!);
        }

        return frame;
    }

    private static void WeavePlane(YuvPlane<byte> top, YuvPlane<byte> bottom, YuvPlane<byte> frame)
    {
        int width = Math.Min(frame.Width, Math.Min(top.Width, bottom.Width));
        for (int y = 0; y < frame.Height; y++)
        {
            YuvPlane<byte> field = (y & 1) == 0 ? top : bottom;
            int row = Math.Min(y >> 1, field.Height - 1);
            field.Row(row)[..width].CopyTo(frame.Row(y));
        }
    }
}
