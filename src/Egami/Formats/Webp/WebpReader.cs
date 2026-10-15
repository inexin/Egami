using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Reads WebP, written from RFC 9649 (the container and VP8L) and RFC 6386 (VP8). Lossless images decode a row at a time through
/// <see cref="Vp8lDecoder"/>, converting each row to the target pixel type as it finishes.
/// </summary>
internal sealed partial class WebpReader : IFormatReader
{
    internal static readonly WebpReader Instance = new();

    /// <summary>Returns whether the data starts with RIFF, a size and WEBP.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header) => WebpParser.Matches(header);

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        return Info(WebpParser.Open(input, options with { Recovery = RecoveryMode.Lenient }));
    }

    /// <summary>Finishes reading the container after the header (frame count, metadata after the image) and returns what Probe reports.</summary>
    private static RasterInfo Info(WebpParser parser)
    {
        if (parser.Animated)
        {
            parser.CountFrames();
        }
        else
        {
            parser.ReadToEnd(0);
        }

        PixelLayoutInfo layout = parser.HasAlpha ? Rgba8.Layout : Rgb8.Layout;
        long frame = (long)parser.Width * parser.Height * layout.BytesPerPixel;
        int frames = parser.FrameCount ?? 1;
        return new RasterInfo
        {
            Format = WebpFormat.Info,
            Width = parser.Width,
            Height = parser.Height,
            Layout = layout,
            FrameCount = frames,
            HasMetadata = parser.HasMetadata,
            EstimatedBytes = frame * frames,
            Orientation = parser.Metadata.Exif?.Orientation ?? ImageOrientation.Normal,
            LoopCount = parser.Animated ? parser.Metadata.LoopCount : null,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytes(parser),
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        WebpParser parser = WebpParser.Open(input, options);
        if (parser.Animated)
        {
            return ReadAnimation<TPixel>(input, options, parser);
        }

        Raster<TPixel> raster = input.CreateRaster<TPixel>(parser.Width, parser.Height);
        try
        {
            (long consumed, EgamiDiagnostic? warning) = DecodeStill(input, options, parser, raster);
            parser.ReadToEnd(consumed);
            CopyMetadata(parser.Metadata, raster.Metadata);
            if (warning is not null)
            {
                raster.Metadata.WarningList.Add(warning.Value);
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads an animation: every frame is composed on the canvas, and those in <see cref="ReaderOptions.Frames"/> are copied out at
    /// the canvas size. Frames after the range are skipped undecoded; the chunks after them are still read for their metadata.
    /// </summary>
    private static Raster<TPixel> ReadAnimation<TPixel>(EgamiInput input, ReaderOptions options, WebpParser parser)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Range range = options.Frames;
        bool fromEnd = range.Start.IsFromEnd || range.End.IsFromEnd;
        int first = fromEnd ? 0 : range.Start.Value, end = fromEnd ? int.MaxValue : range.End.Value;
        List<Frame<TPixel>> frames = [];
        using WebpAnimation animation = new(input, options, parser);
        try
        {
            while (animation.Next() is FrameMetadata metadata)
            {
                int index = animation.FramesSeen - 1;
                if (index >= first && index < end)
                {
                    input.CheckMemory((long)parser.CanvasWidth * parser.CanvasHeight * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
                    PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(parser.CanvasWidth, parser.CanvasHeight, input.Memory);
                    animation.Canvas.CopyTo(plane);
                    frames.Add(new Frame<TPixel>(plane, metadata));
                }

                if (animation.FramesSeen >= end)
                {
                    animation.SkipRest();
                    break;
                }
            }

            if (fromEnd)
            {
                // A range counted from the end is only known once every frame is read.
                (int offset, int length) = AnimationCanvasRange.Clamp(range, frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i < offset || i >= offset + length)
                    {
                        frames[i].Plane.Dispose();
                    }
                }

                frames = frames.GetRange(offset, length);
            }

            if (frames.Count == 0)
            {
                throw input.Corrupt(-1, animation.FramesSeen == 0 ? "The animation has no frame." : "No frame is in the requested range.");
            }

            RasterMetadata rasterMetadata = new();
            CopyMetadata(parser.Metadata, rasterMetadata);
            return new Raster<TPixel>(frames, rasterMetadata, input.Context);
        }
        catch
        {
            foreach (Frame<TPixel> frame in frames)
            {
                frame.Plane.Dispose();
            }

            throw;
        }
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        // The natural type needs the header; a second look costs only the bytes up to the image chunk.
        ReadOnlySpan<byte> peek = input.Peek(64 * 1024);
        bool alpha = PeekAlpha(peek);
        return alpha ? Read<Rgba8>(input, options) : Read<Rgb8>(input, options);
    }

    /// <summary>Returns the bytes a streaming decode holds.</summary>
    private static long WorkingBytes(WebpParser parser)
    {
        if (parser.Animated)
        {
            return WebpAnimation.WorkingBytes(parser.CanvasWidth, parser.CanvasHeight);
        }

        if (parser.ImageChunk == WebpContainer.Vp8L)
        {
            return Vp8lDecoder.WorkingBytes(parser.Width, parser.Height);
        }

        // Lossy: the payload, the rings of planes and alpha rows, and the alpha's own decoder.
        long width = parser.Width;
        long rings = (width * 64 * (parser.Alpha is null ? 1 : 2)) + (2 * ((width + 1) / 2) * 32);
        long alpha = parser.Alpha is null ? 0 : WebpAlpha.WorkingBytes(parser.Width, parser.Height);
        return Math.Max(parser.ImageSize, 16) + rings + alpha;
    }

    /// <summary>
    /// Decodes a still image's chunk into <paramref name="raster"/>; returns how many payload bytes were consumed and the warning a
    /// lenient decode recovered with, if any.
    /// </summary>
    private static (long Consumed, EgamiDiagnostic? Warning) DecodeStill<TPixel>(EgamiInput input, ReaderOptions options, WebpParser parser, Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (parser.ImageChunk == WebpContainer.Vp8)
        {
            (YuvRaster<byte> planes, EgamiDiagnostic? lossyWarning) = DecodePlanes(input, options, parser);
            using (planes)
            {
                Vp8StageTimes.Lap(Vp8Stage.Setup);
                WebpYuvToRgb.ToRaster(planes, raster, options is WebpReaderOptions { FastUpsampling: true }, input.CancellationToken);
                Vp8StageTimes.Lap(Vp8Stage.Output);
            }

            return (parser.ImageSize, lossyWarning);
        }

        bool strict = options.Recovery == RecoveryMode.Strict;
        long start = input.Position;
        using Vp8lBitReader reader = new(input, parser.ImageSize);
        using Vp8lDecoder decoder = new(input, reader, strict);
        bool direct = typeof(TPixel) == typeof(Bgra8);
        uint[]? rented = direct ? null : ArrayPool<uint>.Shared.Rent(raster.Width);
        try
        {
            for (int y = 0; y < raster.Height; y++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                Span<uint> row = direct ? MemoryMarshal.Cast<TPixel, uint>(raster.Row(y)) : rented.AsSpan(0, raster.Width);
                decoder.DecodeRow(row);
                if (!direct)
                {
                    PixelConvert.Convert<Bgra8, TPixel>(MemoryMarshal.Cast<uint, Bgra8>(row), raster.Row(y));
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<uint>.Shared.Return(rented);
            }
        }

        reader.SkipRest();
        return (input.Position - start, decoder.Warning);
    }

    /// <summary>Copies what the parser read into the raster's metadata, keeping warnings the decode added.</summary>
    internal static void CopyMetadata(RasterMetadata source, RasterMetadata target)
    {
        target.FormatName = WebpFormat.Info.Name;
        target.FormatDetails = source.FormatDetails;
        target.IccProfile = source.IccProfile;
        target.Exif = source.Exif;
        target.Xmp = source.Xmp;
        target.Resolution = source.Resolution;
        target.LoopCount = source.LoopCount;
        target.ColorSpace = ColorSpaceHint.Srgb;
        target.WarningList.AddRange(source.WarningList);
    }

    /// <summary>Returns whether the header in <paramref name="data"/> says the image has alpha (VP8X flag, VP8L hint or ALPH chunk).</summary>
    private static bool PeekAlpha(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
        {
            return true;
        }

        int at = 12;
        bool alpha = false;
        while (at + 8 <= data.Length)
        {
            uint type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            switch (type)
            {
                case WebpContainer.Vp8X:
                    if (at + 9 <= data.Length)
                    {
                        alpha |= (data[at + 8] & 0x10) != 0;
                    }

                    break;
                case WebpContainer.Alph:
                    return true;
                case WebpContainer.Vp8L:
                    return alpha || (at + 13 <= data.Length && (data[at + 12] & 0x10) != 0);
                case WebpContainer.Vp8:
                    return alpha;
                case WebpContainer.Anmf:
                    return true;
            }

            at += 8 + (int)Math.Min(size + (size & 1), int.MaxValue - at);
        }

        return alpha;
    }
}
