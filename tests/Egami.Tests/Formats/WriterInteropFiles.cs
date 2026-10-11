using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Png;
using Egami.Formats.Pnm;
using Egami.Formats.Qoi;
using Egami.Formats.Tga;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// Writes BMP, TGA, QOI and PNM files in every layout the writers offer, and PNG files interlaced and compressed in bands, each
/// with the pixels Egami reads back, for tools/check-writer-interop.py to read with Pillow, ImageMagick, Netpbm and qoiconv. Runs
/// only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class WriterInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        Directory.CreateDirectory(directory!);
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(SimpleCorpus.Images, "ops", "duck.png"));
        using Raster<Rgba8> rgba = photo.Derive(ops => ops.Fit(61, 45), TestContext.Current.CancellationToken) is Raster<Rgba8> r ? WithAlpha(r) : throw new InvalidOperationException();
        using Raster<Rgb8> rgb = rgba.As<Rgb8>();
        using Raster<Gray8> gray = rgba.As<Gray8>();
        using Raster<Gray16> gray16 = rgba.As<Gray16>();
        using Raster<Rgba16> rgba16 = rgba.As<Rgba16>();
        using Raster<GrayAlpha8> grayAlpha = rgba.As<GrayAlpha8>();
        using Raster<Rgb8> few = FewColors(rgb, 4);
        using Raster<Rgb8> two = FewColors(rgb, 2);

        Write(directory!, "bmp-24.bmp", rgb, BmpFormat.Info, BmpWriterOptions.Default);
        Write(directory!, "bmp-32.bmp", rgba, BmpFormat.Info, BmpWriterOptions.Default);
        Write(directory!, "bmp-1.bmp", two, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed1 });
        Write(directory!, "bmp-2.bmp", few, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed2 });
        Write(directory!, "bmp-4.bmp", rgb, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed4 });
        Write(directory!, "bmp-8.bmp", rgb, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed8 });
        Write(directory!, "tga-24-rle.tga", rgb, TgaFormat.Info, TgaWriterOptions.Default);
        Write(directory!, "tga-24-raw.tga", rgb, TgaFormat.Info, new TgaWriterOptions { Rle = false });
        Write(directory!, "tga-32-rle.tga", rgba, TgaFormat.Info, TgaWriterOptions.Default);
        Write(directory!, "tga-32-raw.tga", rgba, TgaFormat.Info, new TgaWriterOptions { Rle = false });
        Write(directory!, "tga-gray-rle.tga", gray, TgaFormat.Info, TgaWriterOptions.Default);
        Write(directory!, "tga-pal-rle.tga", rgb, TgaFormat.Info, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Indexed8 });
        Write(directory!, "tga-pal-raw.tga", rgb, TgaFormat.Info, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Indexed8, Rle = false });
        Write(directory!, "qoi-3.qoi", rgb, QoiFormat.Info, QoiWriterOptions.Default);
        Write(directory!, "qoi-4.qoi", rgba, QoiFormat.Info, QoiWriterOptions.Default);
        foreach (bool plain in new[] { false, true })
        {
            string suffix = plain ? "plain" : "raw";
            Write(directory!, $"pnm-bits-{suffix}.pbm", gray, PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.Bitmap, PlainText = plain });
            Write(directory!, $"pnm-gray-{suffix}.pgm", gray, PnmFormat.Info, new PnmWriterOptions { PlainText = plain });
            Write(directory!, $"pnm-gray16-{suffix}.pgm", gray16, PnmFormat.Info, new PnmWriterOptions { PlainText = plain });
            Write(directory!, $"pnm-rgb-{suffix}.ppm", rgb, PnmFormat.Info, new PnmWriterOptions { PlainText = plain });
            Write(directory!, $"pnm-rgb16-{suffix}.ppm", rgba16, PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.Pixmap, PlainText = plain });
        }

        Write(directory!, "pnm-rgba.pam", rgba, PnmFormat.Info, PnmWriterOptions.Default);
        Write(directory!, "pnm-gray-alpha.pam", grayAlpha, PnmFormat.Info, PnmWriterOptions.Default);
        Write(directory!, "pnm-rgb.pam", rgb, PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.Pam });

        // Phase 2.4.1: Adam7 and banded Deflate, which libpng's own tests (LibpngTests) also cover.
        foreach ((string mode, bool interlace, bool parallel) in new[] { ("interlace", true, false), ("parallel", false, true), ("interlace-parallel", true, true) })
        {
            PngWriterOptions Options(PngColorType? colorType = null, int? depth = null) => new()
            {
                ColorType = colorType, BitDepth = depth, Interlace = interlace, ParallelCompression = parallel, ParallelThreshold = 0, ParallelBandSize = 1500,
            };
            Write(directory!, $"png-{mode}-rgba.png", rgba, PngFormat.Info, Options());
            Write(directory!, $"png-{mode}-rgb.png", rgb, PngFormat.Info, Options());
            Write(directory!, $"png-{mode}-gray.png", gray, PngFormat.Info, Options());
            Write(directory!, $"png-{mode}-gray-alpha.png", grayAlpha, PngFormat.Info, Options());
            Write(directory!, $"png-{mode}-rgba16.png", rgba16, PngFormat.Info, Options());
            Write(directory!, $"png-{mode}-gray1.png", two, PngFormat.Info, Options(PngColorType.Gray, 1));
            Write(directory!, $"png-{mode}-palette2.png", few, PngFormat.Info, Options(PngColorType.Palette, 2));
            Write(directory!, $"png-{mode}-palette8.png", rgb, PngFormat.Info, Options(PngColorType.Palette, 8));
        }
    }

    /// <summary>Writes a file and, next to it, the pixels Egami reads back from it as raw RGBA.</summary>
    private static void Write<TPixel>(string directory, string name, Raster<TPixel> raster, FormatInfo format, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        string path = Path.Combine(directory, name);
        using (FileStream file = File.Create(path))
        {
            raster.Save(file, format, options);
        }

        using Raster<Rgba8> back = Raster.Load<Rgba8>(path);
        using FileStream pixels = File.Create(path + ".rgba");
        for (int y = 0; y < back.Height; y++)
        {
            pixels.Write(MemoryMarshal.AsBytes(back.Row(y)));
        }
    }

    private static Raster<Rgba8> WithAlpha(Raster<Rgba8> raster)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x].A = x < 8 && y < 8 ? (byte)0 : (byte)Math.Min(255, 40 + (x * 4));
            }
        }

        return raster;
    }

    private static Raster<Rgb8> FewColors(Raster<Rgb8> source, int colors)
    {
        Raster<Rgb8> result = source.Derive(_ => { }, TestContext.Current.CancellationToken) as Raster<Rgb8> ?? throw new InvalidOperationException();
        for (int y = 0; y < result.Height; y++)
        {
            Span<Rgb8> row = result.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                int level = (row[x].G * colors / 256) * 255 / Math.Max(1, colors - 1);
                row[x] = new Rgb8((byte)level, (byte)(255 - level), (byte)(level / 2));
            }
        }

        return result;
    }
}
