using System.Buffers;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <content>
/// Lossy stills: the VP8 decoder's 4:2:0 planes, with the ALPH chunk's alpha as a fourth plane, given as they are to
/// <see cref="YuvRaster.Load(Stream, EgamiContext?, ReaderOptions?)"/> or converted to RGB through 2.1.4's kernel, which
/// interpolates chroma bilinearly from centre-sited samples as libwebp's default upsampling does.
/// </content>
internal sealed partial class WebpReader : IYuvFormatReader
{
    /// <summary>Gets the color of VP8's planes: BT.601, limited range, chroma sited in the centre (RFC 6386 section 9.2).</summary>
    internal static YuvColor Vp8Color => new(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Center);

    public YuvRaster ReadYuv(EgamiInput input, ReaderOptions options)
    {
        WebpParser parser = WebpParser.Open(input, options);
        if (parser.ImageChunk != WebpContainer.Vp8)
        {
            throw new NotSupportedException(parser.Animated
                ? "This WebP is animated, which has no single set of YUV planes; load it with Raster.Load."
                : "This WebP is lossless, which has no YUV planes; load it with Raster.Load.");
        }

        (YuvRaster<byte> planes, EgamiDiagnostic? warning) = DecodePlanes(input, options, parser);
        try
        {
            parser.ReadToEnd(parser.ImageSize);
            CopyMetadata(parser.Metadata, planes.Metadata);
            if (warning is not null)
            {
                planes.Metadata.WarningList.Add(warning.Value);
            }

            return planes;
        }
        catch
        {
            planes.Dispose();
            throw;
        }
    }

    public (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options)
    {
        WebpParser parser = WebpParser.Open(input, options with { Recovery = RecoveryMode.Lenient });
        bool lossy = parser.ImageChunk == WebpContainer.Vp8;
        return (Info(parser), lossy ? ChromaFormat.Yuv420 : null, 8, Vp8Color);
    }

    /// <summary>
    /// Reads the VP8 payload whole and decodes it, with the ALPH chunk if any, into planes; returns them and the warning a lenient
    /// decode recovered with. The payload is held in memory: VP8's first partition (the modes) and its token partitions are read
    /// side by side.
    /// </summary>
    private static (YuvRaster<byte> Planes, EgamiDiagnostic? Warning) DecodePlanes(EgamiInput input, ReaderOptions options, WebpParser parser)
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        (byte[] payload, int length, EgamiDiagnostic? warning) = ReadPayload(input, parser, strict);
        try
        {
            return DecodePlanes(input, options, payload, 0, length, parser.Alpha, parser.Width, parser.Height, parser.ImageStart, warning);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    /// <summary>
    /// Decodes VP8 data held in memory (<paramref name="length"/> bytes at <paramref name="offset"/>), with its ALPH payload if any,
    /// into planes of the given size; <paramref name="warning"/> is a problem met while reading the data, kept unless a later one
    /// replaces it.
    /// </summary>
    internal static (YuvRaster<byte> Planes, EgamiDiagnostic? Warning) DecodePlanes(
        EgamiInput input, ReaderOptions options, byte[] payload, int offset, int length, byte[]? alphaData, int width, int height, long at, EgamiDiagnostic? warning)
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        YuvRaster<byte>? planes = null;
        WebpAlpha? alpha = null;
        try
        {
            using Vp8Decoder decoder = Vp8Decoder.Create(payload, offset, length);
            if (decoder.Width != width || decoder.Height != height)
            {
                throw new CorruptDataException(WebpFormat.Info.Name, at, "The VP8 frame size differs from the one its header gave.");
            }

            planes = YuvRaster<byte>.Allocate(width, height, ChromaFormat.Yuv420, 8, Vp8Color, alphaData is not null, new RasterMetadata(), input.Context, ClearMode.None);
            YuvRaster<byte> target = planes;
            while (decoder.DecodeRow((d, from, to, chromaFrom, chromaTo) =>
            {
                for (int y = from; y < to; y++)
                {
                    d.RowY(y).CopyTo(target.Y.Row(y));
                }

                for (int y = chromaFrom; y < chromaTo; y++)
                {
                    d.RowU(y).CopyTo(target.U!.Row(y));
                    d.RowV(y).CopyTo(target.V!.Row(y));
                }
            }))
            {
                input.CancellationToken.ThrowIfCancellationRequested();
            }

            if (decoder.PastEnd && warning is null)
            {
                if (strict)
                {
                    throw new CorruptDataException(WebpFormat.Info.Name, at, "The lossy image data ends unexpectedly.") { IsTruncation = true };
                }

                warning = EgamiDiagnostic.Create(DiagnosticLevel.Warning, "Webp.LossyDataEnded", "The lossy image data ends early; the rest of the image is decoded from zeros.");
            }

            if (alphaData is not null)
            {
                alpha = new WebpAlpha(input, alphaData, width, height, strict);
                for (int y = 0; y < height; y++)
                {
                    alpha.DecodeRow(planes.A!.Row(y));
                }

                warning ??= alpha.Warning;
            }

            return (planes, warning);
        }
        catch
        {
            planes?.Dispose();
            throw;
        }
        finally
        {
            alpha?.Dispose();
        }
    }

    /// <summary>Reads the VP8 chunk's payload into a pooled array; a short file is an error (strict) or zero-filled with a warning.</summary>
    private static (byte[] Payload, int Length, EgamiDiagnostic? Warning) ReadPayload(EgamiInput input, WebpParser parser, bool strict)
    {
        if (parser.ImageSize > int.MaxValue)
        {
            throw new LimitExceededException("MaxTotalBytes", int.MaxValue, parser.ImageSize);
        }

        int size = (int)parser.ImageSize;
        input.CheckMemory(size);
        byte[] payload = ArrayPool<byte>.Shared.Rent(Math.Max(size, 16));
        int read = input.Read(payload.AsSpan(0, size));
        EgamiDiagnostic? warning = null;
        if (read < size)
        {
            if (strict)
            {
                ArrayPool<byte>.Shared.Return(payload);
                throw new CorruptDataException(WebpFormat.Info.Name, input.Position, "The lossy image data ends unexpectedly.") { IsTruncation = true };
            }

            payload.AsSpan(read, size - read).Clear();
            warning = EgamiDiagnostic.Create(DiagnosticLevel.Warning, "Webp.LossyDataEnded", "The lossy image data ends early; the rest of the image is decoded from zeros.");
        }

        return (payload, size, warning);
    }
}
