using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tiff;

/// <summary>
/// Writes TIFF files in every layout the writer offers, each with Egami's own decode as raw samples next to it, for
/// tools/check-tiff-interop.py to read with libtiff (tiffinfo, and its decoded samples), ImageMagick and Pillow. Runs only when
/// EGAMI_INTEROP_DIR is set.
/// </summary>
public class TiffInteropFiles
{
    /// <summary>
    /// Reads the ZSTD and LZMA files libtiff and GDAL wrote into EGAMI_INTEROP_DIR/foreign (tools/check-tiff-interop.py makes them first);
    /// Egami's samples must equal libtiff's decode beside each.
    /// </summary>
    [Fact]
    public void ReadsLibtiffAndGdalZstdAndLzmaFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        string foreign = Path.Combine(directory ?? "", "foreign");
        Assert.SkipUnless(directory is not null && Directory.Exists(foreign), "EGAMI_INTEROP_DIR/foreign does not exist.");
        List<string> failures = [];
        string[] files = Directory.GetFiles(foreign, "*.tif");
        foreach (string file in files)
        {
            try
            {
                using Raster raster = Raster.Load(File.ReadAllBytes(file));
                byte[] mine = TiffReference.Bytes(raster);
                if (!mine.AsSpan().SequenceEqual(File.ReadAllBytes(file + ".raw")))
                {
                    failures.Add($"{Path.GetFileName(file)}: samples differ from libtiff's");
                }
            }
            catch (Exception e) when (e is EgamiException or NotSupportedException)
            {
                failures.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        Assert.True(files.Length > 0, "No foreign files.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        Directory.CreateDirectory(directory!);
        (string Name, Raster Raster)[] rasters =
        [
            ("gray8", TiffWriterTests.Pattern<Gray8>()), ("gray16", TiffWriterTests.Pattern<Gray16>()),
            ("graya8", TiffWriterTests.Pattern<GrayAlpha8>()), ("graya16", TiffWriterTests.Pattern<GrayAlpha16>()),
            ("grayf32", TiffWriterTests.Pattern<GrayF32>()), ("rgb8", TiffWriterTests.Pattern<Rgb8>()),
            ("rgba8", TiffWriterTests.Pattern<Rgba8>()), ("rgb16", TiffWriterTests.Pattern<Rgb16>()),
            ("rgba16", TiffWriterTests.Pattern<Rgba16>()), ("rgbf32", TiffWriterTests.Pattern<RgbF32>()),
            ("rgbaf32", TiffWriterTests.Pattern<RgbaF32>()), ("rgbaf16", TiffWriterTests.Pattern<RgbaF16>()),
            ("cmyk8", TiffWriterTests.Pattern<Cmyk8>()), ("cmyk16", TiffWriterTests.Pattern<Cmyk16>()),
        ];
        try
        {
            foreach ((string name, Raster raster) in rasters)
            {
                foreach (TiffCompression compression in (TiffCompression[])[TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.Zstd, TiffCompression.Lzma, TiffCompression.PackBits])
                {
                    string c = compression.ToString().ToLowerInvariant();
                    Write(directory!, $"{name}-{c}.tif", raster, new TiffWriterOptions { Compression = compression });
                    Write(directory!, $"{name}-{c}-tiles.tif", raster, new TiffWriterOptions { Compression = compression, TileSize = 16 });
                }

                Write(directory!, $"{name}-deflate-big.tif", raster, new TiffWriterOptions { Layout = TiffLayout.Big, TileSize = 32 });
                Write(directory!, $"{name}-zstd-19.tif", raster, new TiffWriterOptions { Compression = TiffCompression.Zstd, ZstdLevel = 19 });
                Write(directory!, $"{name}-lzma-9.tif", raster, new TiffWriterOptions { Compression = TiffCompression.Lzma, LzmaPreset = 9 });
                Write(directory!, $"{name}-lzma-1-tiles.tif", raster, new TiffWriterOptions { Compression = TiffCompression.Lzma, LzmaPreset = 1, TileSize = 16 });
            }

            // The extras: 1-bit and palette pages (Reduce), CCITT, JPEG and lossless WebP. These compare through RGBA.
            using Raster<Rgb8> photo = Raster.Load<Rgb8>(TiffCorpus.Main.Read("rgb8.tif"));
            using Raster<Gray8> bilevel = photo.As<Gray8>();
            for (int y = 0; y < bilevel.Height; y++)
            {
                foreach (ref Gray8 p in bilevel.Row(y))
                {
                    p = new Gray8(p.L < 110 ? (byte)0 : (byte)255);
                }
            }

            using Raster<Rgb8> few = Raster.Load<Rgb8>(TiffCorpus.Main.Read("palette4.tif"));
            Write(directory!, "rgba-bilevel-g3.tif", bilevel, new TiffWriterOptions { Compression = TiffCompression.CcittGroup3 });
            Write(directory!, "rgba-bilevel-g4.tif", bilevel, new TiffWriterOptions { Compression = TiffCompression.CcittGroup4 });
            Write(directory!, "rgba-bilevel-g4-tiles.tif", bilevel, new TiffWriterOptions { Compression = TiffCompression.CcittGroup4, TileSize = 16 });
            Write(directory!, "rgba-bilevel-packbits.tif", bilevel, new TiffWriterOptions { Compression = TiffCompression.PackBits, Reduce = true });
            Write(directory!, "rgba-palette-lzw.tif", few, new TiffWriterOptions { Compression = TiffCompression.Lzw, Reduce = true });
            Write(directory!, "rgba-jpeg-rgb.tif", photo, new TiffWriterOptions { Compression = TiffCompression.Jpeg });
            Write(directory!, "rgba-jpeg-rgb-tiles.tif", photo, new TiffWriterOptions { Compression = TiffCompression.Jpeg, TileSize = 32 });
            Write(directory!, "rgba-jpeg-gray.tif", bilevel, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 95 });
            Write(directory!, "rgba-webp-rgb.tif", photo, new TiffWriterOptions { Compression = TiffCompression.Webp, Quality = 100 });

            // CMYK JPEG (2.4.3) compares raw inks, within 2 levels.
            using Raster<Cmyk8> inks = photo.As<Cmyk8>();
            using Raster<Cmyk16> inks16 = photo.As<Cmyk16>();
            Write(directory!, "near-jpeg-cmyk8.tif", inks, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 95 });
            Write(directory!, "near-jpeg-cmyk8-tiles.tif", inks, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 95, TileSize = 32 });
            Write(directory!, "near-jpeg-cmyk16.tif", inks16, new TiffWriterOptions { Compression = TiffCompression.Jpeg });
        }
        finally
        {
            foreach ((_, Raster raster) in rasters)
            {
                raster.Dispose();
            }
        }
    }

    private static void Write(string directory, string name, Raster raster, TiffWriterOptions options)
    {
        string path = Path.Combine(directory, name);
        using (FileStream file = File.Create(path))
        {
            raster.Save(file, TiffFormat.Info, options);
        }

        using Raster back = Raster.Load(path);
        File.WriteAllBytes(path + ".raw", TiffReference.Bytes(back));
        using Raster<Rgba8> rgba = Raster.Load<Rgba8>(path);
        File.WriteAllBytes(path + ".rgba", TiffReference.Bytes(rgba));
    }
}
