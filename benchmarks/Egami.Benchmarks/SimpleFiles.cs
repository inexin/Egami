using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Pnm;
using Egami.Formats.Qoi;
using Egami.Formats.Tga;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>The BMP, TGA, QOI and PNM files of the Phase 1.5 performance scenarios, made with Egami's writers in memory.</summary>
internal static class SimpleFiles
{
    private static readonly Dictionary<string, byte[]> Cache = [];

    internal static string[] DecodeScenarios { get; } =
        ["BMP-24", "BMP-32", "BMP-8", "BMP-RLE8", "TGA-32", "TGA-32-RLE", "QOI-RGB", "QOI-RGBA", "PNM-P6", "PNM-P5-16", "PNM-P3"];

    internal static byte[] File(string scenario)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(scenario, out byte[]? file))
            {
                file = Create(scenario);
                Cache[scenario] = file;
            }

            return file;
        }
    }

    /// <summary>The 1920 × 1080 photo with every pixel opaque.</summary>
    internal static Raster<Rgba8> Photo() => BenchImages.Photo(1920, 1080);

    /// <summary>The 1920 × 1080 screenshot with transparent panels, for 4-channel QOI.</summary>
    internal static Raster<Rgba8> TranslucentScreenshot()
    {
        using Raster<Rgb8> screenshot = BenchImages.Screenshot(1920, 1080);
        Raster<Rgba8> raster = screenshot.As<Rgba8>();
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                if (((x / 480) + (y / 270)) % 3 == 0)
                {
                    row[x].A = (byte)(row[x].R == 20 ? 255 : 0);
                }
            }
        });
        return raster;
    }

    internal static byte[] Save<TPixel>(Raster<TPixel> raster, FormatInfo format, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = new();
        raster.Save(stream, format, options);
        return stream.ToArray();
    }

    private static byte[] Create(string scenario)
    {
        using Raster<Rgba8> photo = Photo();
        switch (scenario)
        {
            case "BMP-24":
                return Save(photo, BmpFormat.Info, BmpWriterOptions.Default);
            case "BMP-32":
                return Save(photo, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Bgra32 });
            case "BMP-8":
                return Save(photo, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed8 });
            case "BMP-RLE8":
                {
                    using Raster<Rgb8> screenshot = BenchImages.Screenshot(1920, 1080);
                    return ToRle8(Save(screenshot, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed8 }));
                }

            case "TGA-32":
                return Save(photo, TgaFormat.Info, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Bgra32, Rle = false });
            case "TGA-32-RLE":
                return Save(photo, TgaFormat.Info, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Bgra32 });
            case "QOI-RGB":
                return Save(photo, QoiFormat.Info, QoiWriterOptions.Default);
            case "QOI-RGBA":
                {
                    using Raster<Rgba8> screenshot = TranslucentScreenshot();
                    return Save(screenshot, QoiFormat.Info, QoiWriterOptions.Default);
                }

            case "PNM-P6":
                return Save(photo, PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.Pixmap });
            case "PNM-P5-16":
                {
                    using Raster<Gray16> gray = photo.As<Gray16>();
                    return Save(gray, PnmFormat.Info, PnmWriterOptions.Default);
                }

            case "PNM-P3":
                {
                    using Raster<Rgba8> small = BenchImages.Photo(512, 512);
                    return Save(small, PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.Pixmap, PlainText = true });
                }

            default:
                throw new ArgumentException(scenario, nameof(scenario));
        }
    }

    /// <summary>Re-encodes an uncompressed 8-bit BMP as RLE8: runs of 2 or more, literal runs of 3 to 255, an end of line per row.</summary>
    private static byte[] ToRle8(byte[] bmp)
    {
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
        int width = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18));
        int height = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22));
        int stride = (width + 3) & ~3;
        using MemoryStream data = new();
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = bmp.AsSpan(offset + (y * stride), width);
            int x = 0;
            while (x < width)
            {
                int run = 1;
                while (x + run < width && run < 255 && row[x + run] == row[x])
                {
                    run++;
                }

                if (run >= 2 || width - x < 3)
                {
                    data.WriteByte((byte)run);
                    data.WriteByte(row[x]);
                    x += run;
                    continue;
                }

                int literal = 1;
                while (x + literal < width && literal < 255 && !(x + literal + 1 < width && row[x + literal] == row[x + literal + 1]))
                {
                    literal++;
                }

                if (literal < 3)
                {
                    data.WriteByte(1);
                    data.WriteByte(row[x]);
                    x++;
                    continue;
                }

                data.WriteByte(0);
                data.WriteByte((byte)literal);
                data.Write(row.Slice(x, literal));
                if ((literal & 1) != 0)
                {
                    data.WriteByte(0);
                }

                x += literal;
            }

            data.WriteByte(0);
            data.WriteByte(0);
        }

        data.WriteByte(0);
        data.WriteByte(1);
        byte[] result = [.. bmp.AsSpan(0, offset), .. data.ToArray()];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(30), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(34), result.Length - offset);
        return result;
    }
}
