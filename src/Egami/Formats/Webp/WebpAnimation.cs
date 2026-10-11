using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Walks an animated WebP's ANMF chunks and composes each frame on a shared <see cref="AnimationCanvas{TCanvas}"/>, as libwebp's
/// WebPAnimDecoder does: the canvas starts transparent, a frame replaces its rectangle or is alpha-blended over it with libwebp's
/// integer arithmetic, and a frame disposed of clears its rectangle before the next one. Key frames (the first, a full opaque or
/// replacing frame, or one after a cleared full or key frame) start from a cleared canvas, which keeps the pixels identical to
/// libwebp's. With <see cref="WebpReaderOptions.UseBackgroundColor"/> the canvas starts from, and disposal returns to, the ANIM
/// background color instead.
/// </summary>
internal sealed class WebpAnimation : IDisposable
{
    private readonly EgamiInput _input;
    private readonly ReaderOptions _options;
    private readonly WebpParser _parser;
    private readonly bool _strict;
    private readonly bool _useBackground;
    private readonly Rgba8 _background;
    private bool _started;
    private Rect _previous;
    private bool _previousClears;
    private bool _previousKey;
    private bool _pendingDisposal;

    internal WebpAnimation(EgamiInput input, ReaderOptions options, WebpParser parser)
    {
        _input = input;
        _options = options;
        _parser = parser;
        _strict = options.Recovery == RecoveryMode.Strict;
        _useBackground = options is WebpReaderOptions { UseBackgroundColor: true };
        _background = parser.Details.BackgroundColor;
        Canvas = new AnimationCanvas<Rgba8>(parser.CanvasWidth, parser.CanvasHeight, input.Memory);
        if (_useBackground)
        {
            for (int y = 0; y < Canvas.Height; y++)
            {
                Canvas.Row(y).Fill(_background);
            }
        }
    }

    /// <summary>Gets the canvas, holding the frame composed last.</summary>
    internal AnimationCanvas<Rgba8> Canvas { get; }

    /// <summary>Gets how many frames have been read.</summary>
    internal int FramesSeen { get; private set; }

    /// <summary>Gets whether the walk has reached the end of the file, so the metadata is complete.</summary>
    internal bool Finished { get; private set; }

    /// <summary>Returns the bytes a frame-at-a-time read holds for a canvas of this size: the canvas and one decoded frame.</summary>
    internal static long WorkingBytes(int width, int height) => (long)width * height * 8;

    /// <summary>
    /// Reads the next frame and composes it on the canvas (after disposing of the frame before it); returns its metadata, or
    /// <see langword="null"/> after the last frame, with the chunks after the frames read.
    /// </summary>
    internal FrameMetadata? Next()
    {
        if (!MoveToFrame(out long size))
        {
            return null;
        }

        long at = _input.Position;
        _input.CheckMemory(size);
        byte[]? payload = _parser.ReadPayload(size, "ANMF chunk");
        if (payload is null)
        {
            Finished = true;
            return null;
        }

        FramesSeen++;
        _input.Limits.CheckFrameCount(FramesSeen);
        if (payload.Length < 16)
        {
            _parser.Problem(at, $"The ANMF chunk has {payload.Length} bytes, too few for its header; the frame was skipped.");
            return Next();
        }

        int x = 2 * U24(payload, 0), y = 2 * U24(payload, 3);
        int width = U24(payload, 6) + 1, height = U24(payload, 9) + 1;
        int duration = U24(payload, 12);
        bool clears = (payload[15] & 1) != 0;
        bool blends = (payload[15] & 2) == 0;
        (Raster<Rgba8> frame, bool lossless, bool hasAlpha) = DecodeFrame(payload, at, width, height);
        using (frame)
        {
            Compose(frame, x, y, width, height, blends, hasAlpha, clears, at);
        }

        return new FrameMetadata
        {
            Delay = TimeSpan.FromMilliseconds(duration),
            Disposal = clears ? FrameDisposal.ClearToBackground : FrameDisposal.None,
            Blend = blends ? FrameBlend.Over : FrameBlend.Source,
            Bounds = new Rect(x, y, width, height),
            FormatDetails = new WebpFrameDetails { Lossless = lossless },
        };
    }

    /// <summary>Skips the frames left without decoding them and reads the chunks after them.</summary>
    internal void SkipRest()
    {
        while (MoveToFrame(out long size))
        {
            FramesSeen++;
            _parser.SkipPayload(size);
        }
    }

    private bool MoveToFrame(out long size)
    {
        size = 0;
        if (Finished)
        {
            return false;
        }

        if (!_started)
        {
            _started = true;
            size = _parser.ImageSize;
            return true;
        }

        if (!_parser.NextFrame(out size))
        {
            Finished = true;
            return false;
        }

        return true;
    }

    /// <summary>Decodes a frame's image data (ALPH and VP8, or VP8L) into a raster of the frame's size.</summary>
    private (Raster<Rgba8> Frame, bool Lossless, bool HasAlpha) DecodeFrame(byte[] payload, long at, int width, int height)
    {
        byte[]? alpha = null;
        int offset = 16;
        while (offset + 8 <= payload.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 4));
            int start = offset + 8;
            int length = (int)Math.Min(size, payload.Length - start);
            if (length < size)
            {
                _parser.Problem(at + start, $"The frame's \"{WebpParser.FourCC(type)}\" chunk runs past its ANMF chunk.", truncation: true);
            }

            switch (type)
            {
                case WebpContainer.Alph:
                    alpha = payload.AsSpan(start, length).ToArray();
                    break;
                case WebpContainer.Vp8:
                    return (DecodeLossy(payload, start, length, alpha, at + start, width, height), false, alpha is not null);
                case WebpContainer.Vp8L:
                    return DecodeLossless(payload, start, length, at + start, width, height);
            }

            offset = start + length + (length & 1);
        }

        throw _input.Corrupt(at, $"Frame {FramesSeen - 1} has no image data.");
    }

    private Raster<Rgba8> DecodeLossy(byte[] payload, int start, int length, byte[]? alpha, long at, int width, int height)
    {
        if (length < 10)
        {
            throw _input.Corrupt(at, "The frame's VP8 data is cut short.");
        }

        if ((payload[start] & 1) != 0)
        {
            throw _input.Corrupt(at, "The frame's VP8 data is not a key frame.");
        }

        (YuvRaster<byte> planes, string? warning) = WebpReader.DecodePlanes(_input, _options, payload, start, length, alpha, width, height, at, null);
        using (planes)
        {
            Raster<Rgba8> frame = _input.CreateRaster<Rgba8>(width, height);
            try
            {
                WebpYuvToRgb.ToRaster(planes, frame, _options is WebpReaderOptions { FastUpsampling: true }, _input.CancellationToken);
            }
            catch
            {
                frame.Dispose();
                throw;
            }

            Warn(warning);
            return frame;
        }
    }

    private (Raster<Rgba8> Frame, bool Lossless, bool HasAlpha) DecodeLossless(byte[] payload, int start, int length, long at, int width, int height)
    {
        using Vp8lBitReader reader = new(payload, start, length);
        using Vp8lDecoder decoder = new(_input, reader, _strict);
        if (decoder.Width != width || decoder.Height != height)
        {
            throw _input.Corrupt(at, $"The frame's VP8L image is {decoder.Width}×{decoder.Height}, not the {width}×{height} its ANMF header gives.");
        }

        Raster<Rgba8> frame = _input.CreateRaster<Rgba8>(width, height);
        try
        {
            uint[] row = new uint[width];
            for (int y = 0; y < height; y++)
            {
                _input.CancellationToken.ThrowIfCancellationRequested();
                decoder.DecodeRow(row);
                PixelConvert.Convert<Bgra8, Rgba8>(MemoryMarshal.Cast<uint, Bgra8>(row.AsSpan()), frame.Row(y));
            }
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        Warn(decoder.Warning);
        return (frame, true, decoder.AlphaHint);
    }

    /// <summary>Disposes of the frame before, then draws <paramref name="frame"/> on the canvas at (x, y), as WebPAnimDecoder does.</summary>
    private void Compose(Raster<Rgba8> frame, int x, int y, int width, int height, bool blends, bool hasAlpha, bool clears, long at)
    {
        int canvasWidth = Canvas.Width, canvasHeight = Canvas.Height;
        if (_pendingDisposal)
        {
            Fill(_previous, _useBackground ? _background : default);
            _pendingDisposal = false;
        }

        if (x + width > canvasWidth || y + height > canvasHeight)
        {
            _parser.Problem(at, $"Frame {FramesSeen - 1} ({x}, {y}, {width}×{height}) reaches past the {canvasWidth}×{canvasHeight} canvas; it was clipped.");
        }

        Rect bounds = x >= canvasWidth || y >= canvasHeight ? default
            : new Rect(x, y, Math.Min(width, canvasWidth - x), Math.Min(height, canvasHeight - y));
        bool full = x == 0 && y == 0 && width == canvasWidth && height == canvasHeight;
        bool first = FramesSeen == 1;
        bool key = !_useBackground && (first
            || ((!hasAlpha || !blends) && full)
            || (_previousClears && (IsFull(_previous) || _previousKey)));
        if (key)
        {
            Fill(new Rect(0, 0, canvasWidth, canvasHeight), default);
        }

        bool blend = blends && !key && (!first || _useBackground);
        for (int row = 0; row < bounds.Height; row++)
        {
            int cy = bounds.Y + row;
            ReadOnlySpan<Rgba8> source = frame.Row(row)[..bounds.Width];
            Span<Rgba8> target = Canvas.Row(cy).Slice(bounds.X, bounds.Width);
            if (!blend)
            {
                source.CopyTo(target);
                continue;
            }

            // After a cleared frame, its rectangle is transparent: pixels there are taken as they are, not blended (libwebp).
            bool inCleared = !_useBackground && _previousClears && cy >= _previous.Y && cy < _previous.Y + _previous.Height;
            for (int i = 0; i < source.Length; i++)
            {
                Rgba8 s = source[i];
                if (s.A == 255)
                {
                    target[i] = s;
                }
                else if (inCleared && bounds.X + i >= _previous.X && bounds.X + i < _previous.X + _previous.Width)
                {
                    target[i] = s;
                }
                else
                {
                    target[i] = BlendNonPremultiplied(s, target[i]);
                }
            }
        }

        _previous = new Rect(x, y, width, height);
        _previousClears = clears;
        _previousKey = key;
        _pendingDisposal = clears;

        bool IsFull(Rect r) => r.X == 0 && r.Y == 0 && r.Width == canvasWidth && r.Height == canvasHeight;
    }

    private void Fill(Rect area, Rgba8 color)
    {
        int right = Math.Min(area.X + area.Width, Canvas.Width), bottom = Math.Min(area.Y + area.Height, Canvas.Height);
        for (int y = Math.Max(area.Y, 0); y < bottom; y++)
        {
            if (right > area.X)
            {
                Canvas.Row(y)[area.X..right].Fill(color);
            }
        }
    }

    /// <summary>libwebp's blend of a straight-alpha pixel over another, in integers, so the results match it exactly.</summary>
    internal static Rgba8 BlendNonPremultiplied(Rgba8 src, Rgba8 dst)
    {
        int srcA = src.A;
        if (srcA == 0)
        {
            return dst;
        }

        int dstFactor = (dst.A * (256 - srcA)) >> 8;
        int blendA = srcA + dstFactor;
        uint scale = (1u << 24) / (uint)blendA;
        return new Rgba8(Channel(src.R, dst.R), Channel(src.G, dst.G), Channel(src.B, dst.B), (byte)blendA);

        byte Channel(int s, int d) => (byte)(((uint)((s * srcA) + (d * dstFactor)) * scale) >> 24);
    }

    private void Warn(string? warning)
    {
        if (warning is not null)
        {
            _parser.Problem(-1, warning);
        }
    }

    private static int U24(byte[] data, int at) => data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);

    public void Dispose() => Canvas.Dispose();
}
