using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Formats.Pnm;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Writes the Phase 2.4.3 JPEG files (CMYK, YCCK, 12-bit and progressive), each with the pixels Egami reads back, for
/// tools/check-jpeg-writer-interop.py to read with djpeg, ImageMagick and Pillow; progressive files also get their source as PPM
/// or PGM, for cjpeg -progressive at the same settings. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class JpegWriterInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        Directory.CreateDirectory(directory!);
        foreach (string old in Directory.GetFiles(directory!))
        {
            File.Delete(old);
        }

        using Raster<Rgb8> duck = Raster.Load<Rgb8>(Path.Combine(SimpleCorpus.Images, "ops", "duck.png"));
        using Raster<Rgb8> egret = Raster.Load<Rgb8>(Path.Combine(SimpleCorpus.Images, "ops", "egret.png"));
        using Raster<Gray8> gray = duck.As<Gray8>();
        using Raster<Cmyk8> cmyk = duck.As<Cmyk8>();
        using Raster<Rgb16> rgb16 = duck.As<Rgb16>();
        using Raster<Gray16> gray16 = duck.As<Gray16>();
        using Raster<Cmyk16> cmyk16 = duck.As<Cmyk16>();
        using Raster<Rgb8> odd = duck.Derive(ops => ops.Crop(new Rect(3, 5, 301, 207)), TestContext.Current.CancellationToken);

        Write(directory!, "cmyk", cmyk, new JpegWriterOptions { Quality = 90 });
        Write(directory!, "cmyk-from-rgb", duck, new JpegWriterOptions { ColorType = JpegColorType.Cmyk });
        Write(directory!, "ycck-420", cmyk, new JpegWriterOptions { ColorType = JpegColorType.Ycck, Quality = 90 });
        Write(directory!, "ycck-444", cmyk, new JpegWriterOptions { ColorType = JpegColorType.Ycck, Subsampling = ChromaSubsampling.Subsample444 });
        Write(directory!, "cmyk-progressive", cmyk, new JpegWriterOptions { Progressive = true });
        Write(directory!, "ycck-progressive", cmyk, new JpegWriterOptions { ColorType = JpegColorType.Ycck, Progressive = true, RestartInterval = 4 });
        Write(directory!, "p12-rgb-420", rgb16, new JpegWriterOptions { Precision = 12 });
        Write(directory!, "p12-rgb-444", rgb16, new JpegWriterOptions { Precision = 12, Quality = 95, Subsampling = ChromaSubsampling.Subsample444 });
        Write(directory!, "p12-gray", gray16, new JpegWriterOptions { Precision = 12 });
        Write(directory!, "p12-rgb-progressive", rgb16, new JpegWriterOptions { Precision = 12, Progressive = true, RestartInterval = 7 });
        Write(directory!, "p12-cmyk", cmyk16, new JpegWriterOptions { Precision = 12 });
        Write(directory!, "p12-low-quality", rgb16, new JpegWriterOptions { Precision = 12, Quality = 5 });

        // Progressive files against cjpeg -progressive at the same quality and sampling (-grayscale for gray).
        foreach ((string name, Raster source, ChromaSubsampling sampling, int quality) in ((string, Raster, ChromaSubsampling, int)[])
        [
            ("prog-duck-420", duck, ChromaSubsampling.Subsample420, 75), ("prog-duck-444", duck, ChromaSubsampling.Subsample444, 90),
            ("prog-duck-422", duck, ChromaSubsampling.Subsample422, 50), ("prog-egret-420", egret, ChromaSubsampling.Subsample420, 85),
            ("prog-gray", gray, ChromaSubsampling.Subsample420, 75), ("prog-odd-420", odd, ChromaSubsampling.Subsample420, 75),
        ])
        {
            Write(directory!, name, source, new JpegWriterOptions { Progressive = true, Quality = quality, Subsampling = sampling });
            string extension = source is Raster<Gray8> ? "pgm" : "ppm";
            using (FileStream file = File.Create(Path.Combine(directory!, $"{name}.{extension}")))
            {
                source.Save(file, PnmFormat.Info, PnmWriterOptions.Default);
            }

            string sample = sampling switch { ChromaSubsampling.Subsample444 => "1x1", ChromaSubsampling.Subsample422 => "2x1", _ => "2x2" };
            File.WriteAllText(Path.Combine(directory!, $"{name}.json"), JsonSerializer.Serialize(new { quality, sample, gray = source is Raster<Gray8>, source = $"{name}.{extension}" }));
        }
    }

    /// <summary>
    /// Writes <paramref name="name"/>.jpg and Egami's decode of it: as 8-bit RGB (.rgb), plus the inks for CMYK (.cmyk) and the
    /// natural samples of 12-bit files (.wide, little-endian 16-bit, scaled to full range).
    /// </summary>
    private static void Write(string directory, string name, Raster raster, JpegWriterOptions options)
    {
        string path = Path.Combine(directory, name + ".jpg");
        using (FileStream file = File.Create(path))
        {
            raster.Save(file, JpegFormat.Info, options);
        }

        using Raster<Rgb8> rgb = Raster.Load<Rgb8>(path);
        File.WriteAllBytes(path + ".rgb", Bytes(rgb));
        using Raster natural = Raster.Load(path);
        if (natural is Raster<Cmyk8> or Raster<Cmyk16>)
        {
            using Raster<Cmyk8> inks = Raster.Load<Cmyk8>(path);
            File.WriteAllBytes(path + ".cmyk", Bytes(inks));
        }

        if (options.Precision == 12)
        {
            byte[] wide = natural switch
            {
                Raster<Gray16> g => Bytes(g),
                Raster<Rgb16> c => Bytes(c),
                Raster<Cmyk16> k => Bytes(k),
                _ => throw new InvalidOperationException(natural.GetType().Name),
            };
            File.WriteAllBytes(path + ".wide", wide);
        }
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        byte[] bytes = new byte[raster.Width * raster.Height * TPixel.Layout.BytesPerPixel];
        raster.CopyPixelsTo(bytes.AsSpan());
        return bytes;
    }
}
