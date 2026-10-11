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
                foreach (TiffCompression compression in (TiffCompression[])[TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.PackBits])
                {
                    string c = compression.ToString().ToLowerInvariant();
                    Write(directory!, $"{name}-{c}.tif", raster, new TiffWriterOptions { Compression = compression });
                    Write(directory!, $"{name}-{c}-tiles.tif", raster, new TiffWriterOptions { Compression = compression, TileSize = 16 });
                }

                Write(directory!, $"{name}-deflate-big.tif", raster, new TiffWriterOptions { Layout = TiffLayout.Big, TileSize = 32 });
            }
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
    }
}
