using System.Runtime.InteropServices;

using Egami.Formats.Jpeg;
using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Formats.Tiff;

/// <summary>
/// JPEG and WebP strips and tiles for the writer, through Egami's own encoders: JPEG as Adobe's TIFF Technical Note 2 has it, the
/// tables shared in JPEGTables and each unit an abbreviated stream; WebP as a whole file per unit.
/// </summary>
internal static class TiffUnitWriter
{
    /// <summary>
    /// Encodes a unit of 8-bit RGB (as YCbCr 4:2:0), gray (photometric 1) or CMYK (photometric 5) rows with fixed Huffman tables,
    /// so every unit shares the first one's tables; returns the unit's stream without them. <paramref name="tables"/> is set from
    /// the first unit.
    /// </summary>
    public static byte[] Jpeg(ReadOnlySpan<byte> unit, int width, int rows, int photometric, int quality, EgamiContext context, ref byte[]? tables)
    {
        using MemoryStream stream = new();
        JpegWriterOptions options = new() { Quality = quality, Subsampling = ChromaSubsampling.Subsample420, OptimizeHuffman = false, StripMetadata = true };
        if (photometric == 1)
        {
            using Raster<Gray8> raster = Raster.Create<Gray8>(width, rows, context);
            Copy(unit, raster);
            raster.Save(stream, JpegFormat.Info, options);
        }
        else if (photometric == 5)
        {
            // libtiff's separated JPEG holds the inks as they are, where a JPEG file holds them inverted as Adobe does: invert
            // here so the writer's inversion gives back the inks.
            using Raster<Cmyk8> raster = Raster.Create<Cmyk8>(width, rows, context);
            Copy(unit, raster);
            raster.ForEachRow((row, _) =>
            {
                foreach (ref byte b in MemoryMarshal.AsBytes(row))
                {
                    b = (byte)~b;
                }
            });
            raster.Save(stream, JpegFormat.Info, new JpegWriterOptions { Quality = quality, ColorType = JpegColorType.Cmyk, StripMetadata = true });
        }
        else
        {
            using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, rows, context);
            Copy(unit, raster);
            raster.Save(stream, JpegFormat.Info, options);
        }

        (byte[] shared, byte[] abbreviated) = Split(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        if (tables is null)
        {
            tables = shared;
            return abbreviated;
        }

        // Fixed tables make every unit's the same; a unit whose tables differ keeps them in its own stream.
        return tables.AsSpan().SequenceEqual(shared) ? abbreviated : stream.ToArray();
    }

    private static void Copy<TPixel>(ReadOnlySpan<byte> unit, Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int rowBytes = raster.Width * TPixel.Layout.BytesPerPixel;
        for (int y = 0; y < raster.Height; y++)
        {
            unit.Slice(y * rowBytes, rowBytes).CopyTo(MemoryMarshal.AsBytes(raster.Row(y)));
        }
    }

    /// <summary>
    /// Splits a JPEG stream into its tables (SOI, every DQT and DHT, EOI) and the abbreviated stream that refers to them (SOI,
    /// the frame, any DRI, the scan to EOI); application and comment segments are dropped.
    /// </summary>
    internal static (byte[] Tables, byte[] Abbreviated) Split(ReadOnlySpan<byte> jpeg)
    {
        using MemoryStream tables = new();
        using MemoryStream rest = new();
        tables.Write([0xFF, 0xD8]);
        rest.Write([0xFF, 0xD8]);
        int at = 2;
        while (at + 4 <= jpeg.Length)
        {
            byte marker = jpeg[at + 1];
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            ReadOnlySpan<byte> segment = jpeg.Slice(at, length + 2);
            if (marker == JpegMarker.Sos)
            {
                rest.Write(jpeg[at..]);
                break;
            }

            if (marker is JpegMarker.Dqt or JpegMarker.Dht)
            {
                tables.Write(segment);
            }
            else if (marker is not ((>= JpegMarker.App0 and <= JpegMarker.App15) or JpegMarker.Com))
            {
                rest.Write(segment);
            }

            at += length + 2;
        }

        tables.Write([0xFF, 0xD9]);
        return (tables.ToArray(), rest.ToArray());
    }

    /// <summary>Encodes a unit of 8-bit RGB or RGBA rows as a WebP file, lossless or at <paramref name="quality"/>.</summary>
    public static byte[] Webp(ReadOnlySpan<byte> unit, int width, int rows, bool alpha, bool lossless, int quality, EgamiContext context)
    {
        using MemoryStream stream = new();
        WebpWriterOptions options = new() { Lossless = lossless, Quality = quality, StripMetadata = true };
        if (alpha)
        {
            using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, rows, context);
            Copy(unit, raster);
            raster.Save(stream, WebpFormat.Info, options);
        }
        else
        {
            using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, rows, context);
            Copy(unit, raster);
            raster.Save(stream, WebpFormat.Info, options);
        }

        return stream.ToArray();
    }
}
