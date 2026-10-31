using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Formats.Pnm;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Writes the Phase 2.4.3 JPEG files (CMYK, YCCK, 12-bit and progressive) and the arithmetic-coded ones (SOF9 and SOF10, named
/// ac-), each with the pixels Egami reads back, for tools/check-jpeg-writer-interop.py to read with djpeg, ImageMagick and Pillow;
/// an arithmetic file comes with its Huffman twin (.huffman), written with the same options, whose pixels it must decode to.
/// Progressive and arithmetic size cases also get their source as PPM or PGM, for cjpeg at the same settings. Runs only when
/// EGAMI_INTEROP_DIR is set.
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
            File.WriteAllText(Path.Combine(directory!, $"{name}.json"), JsonSerializer.Serialize(new { quality, sample, gray = source is Raster<Gray8>, source = $"{name}.{extension}", progressive = true, arithmetic = false, restart = 0, limit = 1.0 }));
        }

        // Arithmetic coding in every mode, each checked against its Huffman twin.
        Write(directory!, "ac-cmyk", cmyk, new JpegWriterOptions { Quality = 90, Arithmetic = true });
        Write(directory!, "ac-ycck-420", cmyk, new JpegWriterOptions { ColorType = JpegColorType.Ycck, RestartInterval = 3, Arithmetic = true });
        Write(directory!, "ac-cmyk-progressive", cmyk, new JpegWriterOptions { Progressive = true, Arithmetic = true });
        Write(directory!, "ac-ycck-progressive", cmyk, new JpegWriterOptions { ColorType = JpegColorType.Ycck, Progressive = true, RestartInterval = 4, Arithmetic = true });
        Write(directory!, "ac-p12-rgb-420", rgb16, new JpegWriterOptions { Precision = 12, Arithmetic = true });
        Write(directory!, "ac-p12-rgb-444", rgb16, new JpegWriterOptions { Precision = 12, Quality = 95, Subsampling = ChromaSubsampling.Subsample444, Arithmetic = true });
        Write(directory!, "ac-p12-gray", gray16, new JpegWriterOptions { Precision = 12, RestartInterval = 9, Arithmetic = true });
        Write(directory!, "ac-p12-rgb-progressive", rgb16, new JpegWriterOptions { Precision = 12, Progressive = true, RestartInterval = 7, Arithmetic = true });
        Write(directory!, "ac-p12-cmyk", cmyk16, new JpegWriterOptions { Precision = 12, Arithmetic = true });
        Write(directory!, "ac-p12-low-quality", rgb16, new JpegWriterOptions { Precision = 12, Quality = 5, Arithmetic = true });

        // Arithmetic files against cjpeg -arithmetic (with -progressive and -restart Nb where set) at the same settings.
        foreach ((string name, Raster source, ChromaSubsampling sampling, int quality, bool progressive, int restart) in ((string, Raster, ChromaSubsampling, int, bool, int)[])
        [
            ("ac-duck-420", duck, ChromaSubsampling.Subsample420, 75, false, 0), ("ac-duck-444", duck, ChromaSubsampling.Subsample444, 90, false, 0),
            ("ac-duck-422", duck, ChromaSubsampling.Subsample422, 50, false, 5), ("ac-egret-420", egret, ChromaSubsampling.Subsample420, 85, false, 0),
            ("ac-egret-q100", egret, ChromaSubsampling.Subsample444, 100, false, 0), ("ac-gray", gray, ChromaSubsampling.Subsample420, 75, false, 0),
            ("ac-odd-420", odd, ChromaSubsampling.Subsample420, 75, false, 2), ("ac-prog-duck-420", duck, ChromaSubsampling.Subsample420, 75, true, 0),
            ("ac-prog-egret-444", egret, ChromaSubsampling.Subsample444, 90, true, 6), ("ac-prog-gray", gray, ChromaSubsampling.Subsample420, 75, true, 0),
            ("ac-prog-odd-420", odd, ChromaSubsampling.Subsample420, 60, true, 0),
        ])
        {
            Write(directory!, name, source, new JpegWriterOptions { Progressive = progressive, Quality = quality, Subsampling = sampling, RestartInterval = restart, Arithmetic = true });
            string extension = source is Raster<Gray8> ? "pgm" : "ppm";
            using (FileStream file = File.Create(Path.Combine(directory!, $"{name}.{extension}")))
            {
                source.Save(file, PnmFormat.Info, PnmWriterOptions.Default);
            }

            string sample = sampling switch { ChromaSubsampling.Subsample444 => "1x1", ChromaSubsampling.Subsample422 => "2x1", _ => "2x2" };
            File.WriteAllText(Path.Combine(directory!, $"{name}.json"), JsonSerializer.Serialize(new { quality, sample, gray = source is Raster<Gray8>, source = $"{name}.{extension}", progressive, arithmetic = true, restart, limit = 0.5 }));
        }

        // A Huffman file repacked with arithmetic coding by JpegTransform; its twin is the same transform to Huffman.
        string original = Path.Combine(directory!, "transform-source.jpeg");
        using (FileStream file = File.Create(original))
        {
            duck.Save(file, JpegFormat.Info, new JpegWriterOptions { Quality = 85 });
        }

        foreach ((string name, JpegTransformOptions options) in ((string, JpegTransformOptions)[])
        [
            ("ac-transform", new JpegTransformOptions { Arithmetic = true }),
            ("ac-transform-rotate90", new JpegTransformOptions { Operation = JpegTransformOperation.Rotate90, Arithmetic = true }),
            ("ac-transform-progressive", new JpegTransformOptions { Progressive = true, Arithmetic = true }),
        ])
        {
            string path = Path.Combine(directory!, name + ".jpg");
            JpegTransform.Apply(original, path, options);
            JpegTransform.Apply(original, path + ".huffman", new JpegTransformOptions { Operation = options.Operation, Progressive = options.Progressive });
            using Raster<Rgb8> rgb = Raster.Load<Rgb8>(path + ".huffman");
            File.WriteAllBytes(path + ".rgb", Bytes(rgb));
        }
    }

    /// <summary>
    /// Writes <paramref name="name"/>.jpg and Egami's decode of it: as 8-bit RGB (.rgb), plus the inks for CMYK (.cmyk) and the
    /// natural samples of 12-bit files (.wide, little-endian 16-bit, scaled to full range). An arithmetic-coded file also gets
    /// its Huffman twin (.huffman), and the decodes are of the twin, the pixels the arithmetic file must give.
    /// </summary>
    private static void Write(string directory, string name, Raster raster, JpegWriterOptions options)
    {
        string path = Path.Combine(directory, name + ".jpg");
        using (FileStream file = File.Create(path))
        {
            raster.Save(file, JpegFormat.Info, options);
        }

        if (options.Arithmetic)
        {
            using FileStream file = File.Create(path + ".huffman");
            raster.Save(file, JpegFormat.Info, new JpegWriterOptions
            {
                Quality = options.Quality,
                Subsampling = options.Subsampling,
                Progressive = options.Progressive,
                RestartInterval = options.RestartInterval,
                Precision = options.Precision,
                ColorType = options.ColorType,
            });
        }

        string decoded = options.Arithmetic ? path + ".huffman" : path;
        using Raster<Rgb8> rgb = Raster.Load<Rgb8>(decoded);
        File.WriteAllBytes(path + ".rgb", Bytes(rgb));
        using Raster natural = Raster.Load(decoded);
        if (natural.Metadata.FormatDetails is JpegDetails { ColorSpace: JpegColorSpace.Cmyk or JpegColorSpace.Ycck })
        {
            using Raster<Cmyk8> inks = Raster.Load<Cmyk8>(decoded);
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
