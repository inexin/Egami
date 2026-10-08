using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// The encoder against libjpeg-turbo's cjpeg at the same settings (JPEG design, Testing): our file must decode, be within 3% of
/// cjpeg's size and no more than 0.2 dB below its PSNR against the source. With the standard tables it is in fact the same file.
/// </summary>
public class JpegEncodeTests(ITestOutputHelper output)
{
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg");

    public static TheoryData<string> Encodes()
    {
        return [.. Manifest().Select(e => e.GetProperty("file").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(Encodes))]
    public void EncodesLikeCjpeg(string file)
    {
        JsonElement entry = Manifest().Single(e => e.GetProperty("file").GetString() == file);
        using Raster source = Raster.Load(File.ReadAllBytes(Path.Combine(References, entry.GetProperty("source").GetString()!)));
        JpegWriterOptions options = new()
        {
            Quality = entry.GetProperty("quality").GetInt32(),
            Subsampling = entry.GetProperty("sampling").GetString() switch { "1x1" => ChromaSubsampling.Full, "2x1" => ChromaSubsampling.HalfWidth, _ => ChromaSubsampling.Half },
            OptimizeHuffman = entry.GetProperty("optimize").GetBoolean(),
            RestartInterval = entry.GetProperty("restart").GetInt32(),
            ColorType = entry.GetProperty("gray").GetBoolean() ? JpegColorType.Gray : JpegColorType.Auto,
        };
        byte[] ours = Encode(source, options);
        byte[] theirs = File.ReadAllBytes(Path.Combine(References, file));

        double ourPsnr = Psnr(source, ours);
        double theirPsnr = Psnr(source, theirs);
        output.WriteLine($"{file}: {ours.Length:N0} bytes (cjpeg {theirs.Length:N0}), {ourPsnr:F3} dB (cjpeg {theirPsnr:F3})");
        Assert.InRange(ours.Length, theirs.Length * 0.97, theirs.Length * 1.03);
        Assert.True(ourPsnr >= theirPsnr - 0.2, $"{ourPsnr:F3} dB against cjpeg's {theirPsnr:F3} dB");
        // The same color conversion, downsampling, DCT, quantization and Huffman tables (standard or optimized) as cjpeg give the
        // same bytes, except where cjpeg does two things Egami does not: below quality 20 its tables pass 255 and it writes an
        // extended (16-bit table) file, while Egami clamps them to stay baseline; and turning color into one gray component, it
        // keeps the sampling factors given for color, which mean nothing for a single component (always coded block by block),
        // where Egami writes 1×1.
        if (options.Quality >= 20 && !entry.GetProperty("gray").GetBoolean())
        {
            Assert.Equal(theirs, ours);
        }
    }

    [Theory]
    [InlineData(ChromaSubsampling.Half)]
    [InlineData(ChromaSubsampling.HalfWidth)]
    [InlineData(ChromaSubsampling.Full)]
    public void RoundTripsEveryPixelTypeAndOddSize(ChromaSubsampling subsampling)
    {
        foreach ((int w, int h) in ((int, int)[])[(1, 1), (7, 3), (16, 16), (17, 9), (33, 65), (100, 75)])
        {
            using Raster<Rgb8> rgb = Gradient(w, h);
            byte[] file = Encode(rgb, new JpegWriterOptions { Quality = 95, Subsampling = subsampling });
            RasterInfo info = Raster.Probe(file);
            Assert.Equal((w, h, Rgb8.Layout), (info.Width, info.Height, info.Layout));
            using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
            Assert.True(Psnr(rgb, back) > 30, $"{w}×{h} {subsampling}: {Psnr(rgb, back):F1} dB");
            Assert.Empty(back.Metadata.Warnings);

            // Other pixel types give the same file as their Rgb8 conversion.
            using Raster<Rgba8> rgba = rgb.As<Rgba8>();
            using Raster<RgbaF32> floats = rgb.As<RgbaF32>();
            Assert.Equal(file, Encode(rgba, new JpegWriterOptions { Quality = 95, Subsampling = subsampling }));
            Assert.Equal(file, Encode(floats, new JpegWriterOptions { Quality = 95, Subsampling = subsampling }));
        }
    }

    [Fact]
    public void ParallelEncodingWritesTheSameFile()
    {
        // Over a megapixel, rows are transformed on several threads while the coder runs; the file must not change.
        using Raster<Rgb8> serial = Raster.Create<Rgb8>(1201, 999, EgamiContext.Default with { MaxParallelism = 1 });
        serial.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgb8((byte)(x ^ y), (byte)((x * 3) + y), (byte)(x * y >> 4));
            }
        });
        foreach (JpegWriterOptions options in (JpegWriterOptions[])[new(), new() { OptimizeHuffman = true, Subsampling = ChromaSubsampling.Full }, new() { RestartInterval = 7 }])
        {
            byte[] one = Encode(serial, options);
            using MemoryStream stream = new();
            using Raster<Rgb8> wide = Raster.Create<Rgb8>(1201, 999, EgamiContext.Default with { MaxParallelism = 4 });
            wide.CopyPixelsFrom(Bytes(serial).AsSpan());
            wide.Save(stream, JpegFormat.Info, options);
            Assert.Equal(one, stream.ToArray());
        }
    }

    [Fact]
    public void GrayPixelsWriteOneComponent()
    {
        using Raster<Rgb8> rgb = Gradient(40, 30);
        using Raster<Gray8> gray = rgb.As<Gray8>();
        byte[] file = Encode(gray, JpegWriterOptions.Default);
        Assert.Equal(Gray8.Layout, Raster.Probe(file).Layout);
        using Raster loaded = Raster.Load(file);
        Assert.Equal(JpegColorSpace.Gray, Assert.IsType<JpegDetails>(loaded.Metadata.FormatDetails).ColorSpace);
        Assert.Equal(Gray8.Layout, Raster.Probe(Encode(rgb, new JpegWriterOptions { ColorType = JpegColorType.Gray })).Layout);
    }

    [Fact]
    public void RestartIntervalsAndOptimizedTablesDecode()
    {
        using Raster<Rgb8> rgb = Gradient(100, 75);
        byte[] restart = Encode(rgb, new JpegWriterOptions { RestartInterval = 2 });
        using Raster<Rgb8> fromRestart = Raster.Load<Rgb8>(restart);
        Assert.Equal(2, Assert.IsType<JpegDetails>(fromRestart.Metadata.FormatDetails).RestartInterval);
        using Raster<Rgb8> plain = Raster.Load<Rgb8>(Encode(rgb, JpegWriterOptions.Default));
        Assert.Equal(Bytes(plain), Bytes(fromRestart));

        byte[] optimized = Encode(rgb, new JpegWriterOptions { OptimizeHuffman = true });
        using Raster<Rgb8> fromOptimized = Raster.Load<Rgb8>(optimized);
        Assert.Equal(Bytes(plain), Bytes(fromOptimized));
        Assert.True(optimized.Length < Encode(rgb, JpegWriterOptions.Default).Length);
    }

    [Fact]
    public void AlphaIsIgnoredUnlessABackgroundIsSet()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(16, 16);
        image.ForEachRow((row, y) => row.Fill(new Rgba8(200, 40, 40, 0)));
        using Raster<Rgb8> ignored = Raster.Load<Rgb8>(Encode(image, new JpegWriterOptions { Quality = 100, Subsampling = ChromaSubsampling.Full }));
        Assert.InRange(ignored.GetPixel(8, 8).R, 195, 205);

        using Raster<Rgb8> composited = Raster.Load<Rgb8>(Encode(image, new JpegWriterOptions { Quality = 100, Subsampling = ChromaSubsampling.Full, Background = new Rgba8(0, 0, 255, 255) }));
        Rgb8 p = composited.GetPixel(8, 8);
        Assert.True(p.R < 5 && p.B > 250, p.ToString());
    }

    [Fact]
    public void MetadataRoundTrips()
    {
        using Raster<Rgb8> image = Gradient(32, 32);
        byte[] exif = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "exif-tags.jpg"));
        RasterMetadata source = Raster.Load(exif).Metadata;
        byte[] icc = new byte[150_000];
        new Random(3).NextBytes(icc);
        string longXmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" + new string('a', 100_000) + "</x:xmpmeta>";
        image.Metadata.Exif = source.Exif;
        image.Metadata.IccProfile = icc;
        image.Metadata.Xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
        image.Metadata.Resolution = new Resolution(300, 300, ResolutionUnit.PerInch);
        image.Metadata.Text.Add(new TextEntry("Comment", "Made by Egami — ÆØÅ"));
        image.Metadata.Text.Add(new TextEntry("Title", "not a comment"));

        using Raster back = Raster.Load(Encode(image, JpegWriterOptions.Default));
        Assert.Equal(source.Exif.ToArray(), back.Metadata.Exif.ToArray());
        Assert.Equal(icc, back.Metadata.IccProfile.ToArray());
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, back.Metadata.ColorSpace);
        Assert.Equal(image.Metadata.Xmp.ToArray(), back.Metadata.Xmp.ToArray());
        Assert.Equal(new Resolution(300, 300, ResolutionUnit.PerInch), back.Metadata.Resolution);
        Assert.Equal([new TextEntry("Comment", "Made by Egami — ÆØÅ")], back.Metadata.Text);

        // A packet too big for one segment goes whole into extended XMP.
        image.Metadata.Xmp = Encoding.UTF8.GetBytes(longXmp);
        using Raster extended = Raster.Load(Encode(image, JpegWriterOptions.Default));
        Assert.Equal(longXmp, Encoding.UTF8.GetString(Assert.IsType<JpegDetails>(extended.Metadata.FormatDetails).ExtendedXmp.Span));
        Assert.Contains("HasExtendedXMP", Encoding.UTF8.GetString(extended.Metadata.Xmp.Span), StringComparison.Ordinal);

        // Written back from the file it came in, the extended part keeps its GUID and bytes.
        using Raster again = Raster.Load(Encode(extended, JpegWriterOptions.Default));
        Assert.Equal(extended.Metadata.Xmp.ToArray(), again.Metadata.Xmp.ToArray());
        Assert.Equal(longXmp, Encoding.UTF8.GetString(Assert.IsType<JpegDetails>(again.Metadata.FormatDetails).ExtendedXmp.Span));

        using Raster stripped = Raster.Load(Encode(image, new JpegWriterOptions { StripMetadata = true }));
        Assert.True(stripped.Metadata.Exif.IsEmpty && stripped.Metadata.IccProfile.IsEmpty && stripped.Metadata.Xmp.IsEmpty);
        Assert.Null(stripped.Metadata.Resolution);
        Assert.Empty(stripped.Metadata.Text);
    }

    [Fact]
    public void SavingByExtensionAndInvalidOptions()
    {
        using Raster<Rgb8> image = Gradient(8, 8);
        string path = Path.Combine(Path.GetTempPath(), $"egami-{Guid.NewGuid():N}.jpg");
        try
        {
            image.Save(path);
            Assert.Equal("JPEG", Raster.Probe(File.ReadAllBytes(path)).Format.Name);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegWriterOptions { Quality = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegWriterOptions { Quality = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegWriterOptions { RestartInterval = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegWriterOptions { RestartInterval = 65536 });
        using Raster<Gray8> wide = Raster.Create<Gray8>(65536, 1);
        Assert.Throws<InvalidOperationException>(() => Encode(wide, JpegWriterOptions.Default));
    }

    private static byte[] Encode(Raster raster, WriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info, options);
        return stream.ToArray();
    }

    private static Raster<Rgb8> Gradient(int width, int height)
    {
        Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                // Smooth at every size, so chroma subsampling loses little even on tiny images.
                row[x] = new Rgb8((byte)(40 + (x * 2)), (byte)(60 + (y * 2)), (byte)(200 - x - y));
            }
        });
        return raster;
    }

    /// <summary>PSNR of a JPEG file against its source, both as 8-bit samples (gray output against the source's luma).</summary>
    private static double Psnr(Raster source, byte[] jpeg)
    {
        using Raster decoded = Raster.Load(jpeg);
        if (decoded.Layout == Gray8.Layout && source.Layout != Gray8.Layout)
        {
            Raster<Rgb8> rgb = (Raster<Rgb8>)source;
            using Raster<Gray8> luma = Raster.Create<Gray8>(source.Width, source.Height);
            for (int y = 0; y < source.Height; y++)
            {
                Span<Rgb8> s = rgb.Row(y);
                Span<Gray8> l = luma.Row(y);
                for (int x = 0; x < s.Length; x++)
                {
                    l[x] = new Gray8((byte)(((19595 * s[x].R) + (38470 * s[x].G) + (7471 * s[x].B) + 32768) >> 16));
                }
            }

            return Psnr(Bytes(luma), Bytes(decoded));
        }

        return Psnr(Bytes(source), Bytes(decoded));
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b) => Psnr(Bytes(a), Bytes(b));

    private static double Psnr(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / (sum / a.Length));
    }

    private static byte[] Bytes(Raster raster)
    {
        return raster switch
        {
            Raster<Rgb8> rgb => Bytes(rgb),
            Raster<Gray8> gray => Bytes(gray),
            _ => throw new InvalidOperationException(),
        };
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        byte[] bytes = new byte[raster.Width * raster.Height * TPixel.Layout.BytesPerPixel];
        raster.CopyPixelsTo(bytes.AsSpan());
        return bytes;
    }

    private static JsonElement[] Manifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return json.RootElement.TryGetProperty("encodes", out JsonElement encodes) ? [.. encodes.EnumerateArray().Select(e => e.Clone())] : [];
    }
}
