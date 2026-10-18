using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Writes AVIF files from Egami's encoder for tools/check-avif-interop.py to decode with dav1d and libavif: each file with its
/// color item's AV1 stream (.obu), the planes Egami decodes from it (.yuv, as dav1d writes them: each plane in turn, 16-bit little
/// endian above 8 bits) and the RGBA Egami loads (.rgba, 8 or 16 bits a sample as avifdec's PNG has them, with its .txt giving the
/// size and depth). Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class AvifInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        directory = Path.Combine(directory!, "avif");
        Directory.CreateDirectory(directory);
        foreach ((string name, Raster raster, AvifWriterOptions options) in Cases())
        {
            using (raster)
            {
                byte[] file = AvifWriterTests.Save(raster, options);
                string stem = Path.Combine(directory, name);
                File.WriteAllBytes(stem + ".avif", file);
                HeifFile heif = HeifFile.Parse(file, "AVIF");
                byte[] stream = heif.ItemData(heif.PrimaryItem!).ToArray();
                File.WriteAllBytes(stem + ".obu", stream);
                Av1Frame frame = Av1Decoder.DecodeFirstFrame(stream, "AVIF", cancellationToken: TestContext.Current.CancellationToken);
                File.WriteAllBytes(stem + ".yuv", Planes(frame));
                bool deep = frame.Sequence.BitDepth > 8;
                using Raster<Rgba16> loaded = Raster.Load<Rgba16>(file);

                // avifdec applies irot and imir; Egami turns the image when asked.
                loaded.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
                File.WriteAllBytes(stem + ".rgba", Rgba(loaded, deep));
                File.WriteAllText(stem + ".txt", $"{loaded.Width} {loaded.Height} {(deep ? 16 : 8)} {(options.Lossless == true ? "lossless" : "lossy")}");
            }
        }
    }

    private static IEnumerable<(string Name, Raster Raster, AvifWriterOptions Options)> Cases()
    {
        foreach ((int w, int h) in new[] { (1, 1), (17, 9), (130, 67), (300, 200) })
        {
            yield return ($"rgb-{w}x{h}-lossless", Photo(w, h, alpha: false), new AvifWriterOptions { Lossless = true });
            yield return ($"rgb-{w}x{h}-q60", Photo(w, h, alpha: false), new AvifWriterOptions());
            yield return ($"rgba-{w}x{h}-q30", Photo(w, h, alpha: true), new AvifWriterOptions { Quality = 30, AlphaQuality = 50 });
        }

        yield return ("rgb-q95-444", Photo(97, 61, alpha: false), new AvifWriterOptions { Quality = 95 });
        yield return ("rgb-q50-422", Photo(97, 61, alpha: false), new AvifWriterOptions { Quality = 50, Chroma = ChromaFormat.Yuv422 });
        yield return ("rgba-lossless", Photo(80, 50, alpha: true), new AvifWriterOptions { Lossless = true });
        yield return ("rgb10-lossless", Deep(70, 40), new AvifWriterOptions { Lossless = true, BitDepth = 10 });
        yield return ("rgb12-lossless", Deep(70, 40), new AvifWriterOptions { Lossless = true, BitDepth = 12 });
        yield return ("rgb10-q70", Deep(70, 40), new AvifWriterOptions { Quality = 70 });
        yield return ("rgb12-q40-444", Deep(70, 40), new AvifWriterOptions { Quality = 40, BitDepth = 12, Chroma = ChromaFormat.Yuv444 });
        yield return ("gray-lossless", Gray(64, 64), new AvifWriterOptions { Lossless = true });
        yield return ("gray-q60", Gray(64, 64), new AvifWriterOptions());
        yield return ("screen-q60", AvifWriterTests.Screen(300, 200), new AvifWriterOptions());
        yield return ("screen-lossless", AvifWriterTests.Screen(300, 200), new AvifWriterOptions { Lossless = true });
        yield return ("screen-q40-444", AvifWriterTests.Screen(260, 140), new AvifWriterOptions { Quality = 40, Chroma = ChromaFormat.Yuv444 });
        yield return ("screen10-q75", AvifWriterTests.Screen(200, 120), new AvifWriterOptions { Quality = 75, BitDepth = 10 });

        Raster<Rgba8> tagged = Photo(90, 60, alpha: true);
        tagged.Metadata.Exif = new ExifData { Orientation = ImageOrientation.Rotate90 };
        tagged.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        tagged.Metadata.IccProfile = ColorProfile.AdobeRgb.ToIcc();
        yield return ("metadata-q60", tagged, new AvifWriterOptions());

        Raster<Rgb16> pq = Deep(80, 48);
        pq.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;
        pq.Metadata.Hdr = new HdrMetadata { MaxContentLightLevel = 1000, MaxFrameAverageLightLevel = 400, MasteringPrimaries = ColorPrimaries.Rec2020, MasteringMaxLuminance = 1000 };
        yield return ("hdr-pq-q70", pq, new AvifWriterOptions { Quality = 70 });

        string corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
        yield return ("gainmap-sdr-q80", Raster.Load(Path.Combine(corpus, "gainmap-sdr-base.avif")), new AvifWriterOptions { Quality = 80 });
        yield return ("gainmap-hdr-q80", Raster.Load(Path.Combine(corpus, "gainmap-hdr-base.avif")), new AvifWriterOptions { Quality = 80 });
    }

    private static Raster<Rgba8> Photo(int width, int height, bool alpha) => AvifWriterTests.Pattern<Rgba8>(width, height, (x, y) =>
    {
        double v = Math.Sin(x * 0.07) + Math.Cos(y * 0.05);
        byte r = (byte)Math.Clamp(128 + (90 * v), 0, 255);
        byte g = (byte)(((x * 3) + (y * 2)) & 255);
        byte b = (byte)((((x / 8) + (y / 8)) & 1) * 200);
        return new Rgba8(r, g, b, alpha ? (byte)Math.Clamp((x * 255) / Math.Max(1, width - 1), 0, 255) : (byte)255);
    });

    private static Raster<Rgb16> Deep(int width, int height) =>
        AvifWriterTests.Pattern<Rgb16>(width, height, (x, y) => new Rgb16((ushort)(x * 900), (ushort)(y * 1500), (ushort)((x * y * 37) & 0xFFFF)));

    private static Raster<Gray8> Gray(int width, int height) => AvifWriterTests.Pattern<Gray8>(width, height, (x, y) => new Gray8((byte)((x * y) + x)));

    /// <summary>The visible planes, as dav1d's yuv output has them.</summary>
    private static byte[] Planes(Av1Frame frame)
    {
        using MemoryStream output = new();
        int width = frame.Header.UpscaledWidth, height = frame.Header.FrameHeight;
        for (int plane = 0; plane < frame.Planes!.Length; plane++)
        {
            int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
            int w = (width + ssx) >> ssx, h = (height + ssy) >> ssy;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    ushort v = frame.Planes[plane][(y * frame.Strides[plane]) + x];
                    output.WriteByte((byte)v);
                    if (frame.Sequence.BitDepth > 8)
                    {
                        output.WriteByte((byte)(v >> 8));
                    }
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] Rgba(Raster<Rgba16> raster, bool deep)
    {
        using MemoryStream output = new();
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba16 p in raster.Row(y))
            {
                foreach (ushort v in new[] { p.R, p.G, p.B, p.A })
                {
                    if (deep)
                    {
                        output.WriteByte((byte)(v >> 8));
                        output.WriteByte((byte)v);
                    }
                    else
                    {
                        output.WriteByte((byte)(v >> 8));
                    }
                }
            }
        }

        return output.ToArray();
    }
}
