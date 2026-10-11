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
        WebpParser parser = WebpParser.Open(input, options with { Recovery = RecoveryMode.Lenient });
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
            throw new NotSupportedException("Animated WebP is not supported yet.");
        }

        Raster<TPixel> raster = input.CreateRaster<TPixel>(parser.Width, parser.Height);
        try
        {
            long consumed = DecodeStill(input, options, parser, raster);
            parser.ReadToEnd(consumed);
            CopyMetadata(parser.Metadata, raster.Metadata);
            return raster;
        }
        catch
        {
            raster.Dispose();
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
        return parser.ImageChunk == WebpContainer.Vp8L ? Vp8lDecoder.WorkingBytes(parser.Width, parser.Height) : (long)parser.Width * 64;
    }

    /// <summary>Decodes a still image's chunk into <paramref name="raster"/> and returns how many payload bytes were consumed.</summary>
    private static long DecodeStill<TPixel>(EgamiInput input, ReaderOptions options, WebpParser parser, Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (parser.ImageChunk != WebpContainer.Vp8L)
        {
            throw new NotSupportedException("Lossy WebP is not supported yet.");
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

        if (decoder.Warning is not null)
        {
            parser.Metadata.WarningList.Add(decoder.Warning);
        }

        reader.SkipRest();
        return input.Position - start;
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
        foreach (string warning in source.Warnings)
        {
            target.WarningList.Add(warning);
        }
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
                case WebpContainer.Anmf:
                    return alpha;
            }

            at += 8 + (int)Math.Min(size + (size & 1), int.MaxValue - at);
        }

        return alpha;
    }
}
