using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Decode conformance against libjpeg-turbo's djpeg (accurate IDCT, fancy upsampling): every sequential file in the corpus must
/// decode within PSNR 50 dB and 2 levels of djpeg's output, per the JPEG design.
/// </summary>
public class JpegDecodeTests(ITestOutputHelper output)
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg");

    public static TheoryData<string> Sequential()
    {
        return [.. Manifest().Where(e => !e.TryGetProperty("unsupported", out _)).Select(e => e.GetProperty("file").GetString()!)];
    }

    public static TheoryData<string> WithFastReference()
    {
        return [.. Manifest().Where(e => e.TryGetProperty("fast", out _)).Select(e => e.GetProperty("file").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(Sequential))]
    public void MatchesDjpeg(string file)
    {
        JsonElement entry = Entry(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        using Raster raster = Raster.Load(data);
        Assert.Equal(entry.GetProperty("channels").GetInt32() == 1 ? Gray8.Layout : Rgb8.Layout, raster.Layout);
        AssertClose(entry, entry.GetProperty("reference").GetString()!, Bytes(raster), file);
        Assert.Empty(raster.Metadata.Warnings);
        Assert.Equal("JPEG", raster.Metadata.FormatName);
    }

    [Theory]
    [MemberData(nameof(WithFastReference))]
    public void FastUpsamplingMatchesDjpegNoSmooth(string file)
    {
        JsonElement entry = Entry(file);
        using MemoryStream stream = new(File.ReadAllBytes(Path.Combine(Images, file)));
        using EgamiInput input = new(stream, EgamiContext.Default, TestContext.Current.CancellationToken);
        JpegHeader header = JpegSegmentReader.ReadHeader(input, ReaderOptions.Default);
        using Raster<Rgb8> raster = JpegReader.Decode<Rgb8>(input, header, ReaderOptions.Default, fastUpsampling: true);
        AssertClose(entry, entry.GetProperty("fast").GetString()!, Bytes(raster), file + " fast");

        // The public option gives the same pixels.
        using Raster<Rgb8> viaOptions = Raster.Load<Rgb8>(File.ReadAllBytes(Path.Combine(Images, file)), options: new JpegReaderOptions { FastUpsampling = true });
        Assert.Equal(Bytes(raster), Bytes(viaOptions));
    }

    public static TheoryData<string, int> WithScaledReferences()
    {
        TheoryData<string, int> data = [];
        foreach (JsonElement e in Manifest().Where(e => e.TryGetProperty("scaled", out _)))
        {
            foreach (JsonProperty scale in e.GetProperty("scaled").EnumerateObject())
            {
                data.Add(e.GetProperty("file").GetString()!, int.Parse(scale.Name, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WithScaledReferences))]
    public void ScaledDecodeMatchesDjpegScale(string file, int scale)
    {
        JsonElement entry = Entry(file);
        int width = (entry.GetProperty("width").GetInt32() + scale - 1) / scale;
        int height = (entry.GetProperty("height").GetInt32() + scale - 1) / scale;

        // The smallest size still at least the target: asking for exactly the 1/n size gives 1/n.
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, file)), options: new ReaderOptions { TargetSize = new Size(width, height) });
        Assert.Equal((width, height), (raster.Width, raster.Height));
        Assert.Equal(scale, Assert.IsType<JpegDetails>(raster.Metadata.FormatDetails).ScaleDenominator);
        AssertClose(entry, entry.GetProperty("scaled").GetProperty(scale.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetString()!, Bytes(raster), $"{file} 1/{scale}");
    }

    [Theory]
    [MemberData(nameof(Sequential))]
    public void EveryFileDecodesAtEveryScale(string file)
    {
        // Files without djpeg references (other samplings, odd sizes, CMYK, restarts): each scale must look like the full decode
        // averaged over n × n boxes.
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        using Raster<Rgb8> full = Raster.Load<Rgb8>(data);
        foreach (int scale in (int[])[2, 4, 8])
        {
            int width = (full.Width + scale - 1) / scale;
            int height = (full.Height + scale - 1) / scale;
            using Raster<Rgb8> scaled = Raster.Load<Rgb8>(data, options: new ReaderOptions { TargetSize = new Size(width, height) });
            Assert.Equal((width, height), (scaled.Width, scaled.Height));
            Assert.Empty(scaled.Metadata.Warnings);
            double sum = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    (int r, int g, int b, int n) = (0, 0, 0, 0);
                    for (int yy = y * scale; yy < Math.Min(full.Height, (y + 1) * scale); yy++)
                    {
                        for (int xx = x * scale; xx < Math.Min(full.Width, (x + 1) * scale); xx++)
                        {
                            Rgb8 p = full.GetPixel(xx, yy);
                            (r, g, b, n) = (r + p.R, g + p.G, b + p.B, n + 1);
                        }
                    }

                    Rgb8 q = scaled.GetPixel(x, y);
                    sum += Math.Pow((r / (double)n) - q.R, 2) + Math.Pow((g / (double)n) - q.G, 2) + Math.Pow((b / (double)n) - q.B, 2);
                }
            }

            double psnr = 10 * Math.Log10(255.0 * 255 / (sum / (width * height * 3)));
            output.WriteLine($"{file} 1/{scale}: {psnr:F1} dB against the box average");
            Assert.True(psnr >= 25, $"{file} 1/{scale}: {psnr:F1} dB");
        }
    }

    [Fact]
    public void TargetSizePicksTheSmallestScaleThatCovers()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "duck-420.jpg"));
        using Raster full = Raster.Load(data);
        Assert.Equal(1, Assert.IsType<JpegDetails>(full.Metadata.FormatDetails).ScaleDenominator);
        using Raster half = Raster.Load(data, options: new ReaderOptions { TargetSize = new Size(100, 100) });
        Assert.Equal((160, 120), (half.Width, half.Height));
        using Raster eighth = Raster.Load(data, options: new ReaderOptions { TargetSize = new Size(1, 1) });
        Assert.Equal((40, 30), (eighth.Width, eighth.Height));
        using Raster wide = Raster.Load(data, options: new ReaderOptions { TargetSize = new Size(300, 10) });
        Assert.Equal((320, 240), (wide.Width, wide.Height));
        Assert.True(Raster.Probe(data, options: new ReaderOptions { TargetSize = new Size(40, 30) }).EstimatedBytes < Raster.Probe(data).EstimatedBytes / 32);
    }

    [Fact]
    public void AnyPixelTypeMatchesTheNaturalDecode()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "duck-420.jpg"));
        using Raster<Rgb8> natural = Raster.Load<Rgb8>(data);
        using Raster<Rgba8> rgba = Raster.Load<Rgba8>(data);
        using Raster<Bgra8> bgra = Raster.Load<Bgra8>(data);
        using Raster<RgbaF32> floats = Raster.Load<RgbaF32>(data);
        for (int y = 0; y < natural.Height; y += 7)
        {
            for (int x = 0; x < natural.Width; x += 5)
            {
                Rgb8 p = natural.GetPixel(x, y);
                Assert.Equal(new Rgba8(p.R, p.G, p.B, 255), rgba.GetPixel(x, y));
                Assert.Equal(new Rgba8(p.R, p.G, p.B, 255), bgra.GetPixel(x, y).ToRgba8());
                Assert.Equal(new Rgba8(p.R, p.G, p.B, 255), floats.GetPixel(x, y).ToRgba8());
            }
        }

        byte[] grayFile = File.ReadAllBytes(Path.Combine(Images, "duck-gray.jpg"));
        using Raster<Gray8> gray = Raster.Load<Gray8>(grayFile);
        using Raster<Rgb8> grayAsRgb = Raster.Load<Rgb8>(grayFile);
        Rgb8 g = grayAsRgb.GetPixel(10, 10);
        Assert.Equal((g.R, g.R), (g.G, g.B));
        Assert.Equal(gray.GetPixel(10, 10).L, g.R);
    }

    [Fact]
    public void MetadataComesWithThePixels()
    {
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "exif-tags.jpg")));
        Assert.False(raster.Metadata.Exif.IsEmpty);
        JpegDetails details = Assert.IsType<JpegDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(JpegColorSpace.YCbCr, details.ColorSpace);
        Assert.Equal(90, details.EstimatedQuality);
    }

    [Fact]
    public void TruncatedDataIsGrayInLenientModeAndThrowsInStrict()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "duck-420.jpg"));
        byte[] half = data[..(data.Length / 2)];
        using Raster<Rgb8> raster = Raster.Load<Rgb8>(half);
        Assert.Single(raster.Metadata.Warnings);
        Assert.Equal(new Rgb8(128, 128, 128), raster.GetPixel(raster.Width - 1, raster.Height - 1));
        Assert.NotEqual(new Rgb8(128, 128, 128), raster.GetPixel(raster.Width / 2, 2));
        Assert.Throws<CorruptDataException>(() => Raster.Load<Rgb8>(half, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void DamagedRestartIntervalsResynchronize()
    {
        // Corrupt bytes early in the restart-per-row file: the rows after the next restart marker are intact.
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "duck-restart-rows.jpg"));
        using Raster<Rgb8> clean = Raster.Load<Rgb8>(data);
        int scan = data.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        for (int i = scan + 400; i < scan + 420; i++)
        {
            data[i] = (byte)(data[i] == 0xFF ? 0 : data[i] ^ 0x5A);
        }

        using Raster<Rgb8> damaged = Raster.Load<Rgb8>(data);
        Assert.Equal(clean.Row(clean.Height - 1).ToArray(), damaged.Row(damaged.Height - 1).ToArray());
    }

    [Fact]
    public void TruncatedProgressiveKeepsTheScansSoFar()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "duck-progressive.jpg"));
        using Raster<Rgb8> full = Raster.Load<Rgb8>(data);
        byte[] cut = data[..(data.Length * 2 / 3)];
        using Raster<Rgb8> raster = Raster.Load<Rgb8>(cut);
        Assert.Single(raster.Metadata.Warnings);

        // The early scans cover the whole image, so even the bottom row is close to the full decode, not gray.
        int x = raster.Width / 2;
        int y = raster.Height - 1;
        Rgb8 a = full.GetPixel(x, y);
        Rgb8 b = raster.GetPixel(x, y);
        Assert.InRange(Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B), 0, 60);
        Assert.Throws<CorruptDataException>(() => Raster.Load<Rgb8>(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void ProgressiveCoefficientsCountAgainstTheMemoryLimit()
    {
        // The raster alone fits in 300 KB; the progressive file's coefficients (another 230 KB) do not, the baseline file needs none.
        EgamiContext tight = EgamiContext.Default with { Limits = new SafetyLimits { MaxTotalBytes = 300_000 } };
        using Raster<Rgb8> baseline = Raster.Load<Rgb8>(File.ReadAllBytes(Path.Combine(Images, "duck-420.jpg")), tight);
        Assert.Throws<LimitExceededException>(() => Raster.Load<Rgb8>(File.ReadAllBytes(Path.Combine(Images, "duck-progressive.jpg")), tight));
        Assert.True(Raster.Probe(File.ReadAllBytes(Path.Combine(Images, "duck-progressive.jpg"))).EstimatedBytes > 300_000);
    }

    [Fact]
    public void TheScanCountIsLimited()
    {
        using (Raster<Gray8> most = DcOnlyFile(JpegDecoder<Gray8>.MaxScans))
        {
            Assert.Equal(new Gray8(128), most.GetPixel(3, 3));
        }

        System.Diagnostics.Stopwatch time = System.Diagnostics.Stopwatch.StartNew();
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => DcOnlyFile(JpegDecoder<Gray8>.MaxScans + 1));
        Assert.Contains("1,000 scans", e.Message, StringComparison.Ordinal);
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void BadProgressionThrows()
    {
        // An AC band in a scan of several components is not allowed (T.81 G.1.1.1.1).
        using JpegBuilder builder = new JpegBuilder().Quant(0, 75).Frame(0xC2, 8, 8, 8, (1, 1, 1), (2, 1, 1), (3, 1, 1)).Huffman(0, 0).Huffman(1, 0);
        builder.Segment(0xDA, [2, 1, 0, 2, 0, 1, 5, 0]).Raw(0x00, 0xFF, 0xD9);
        Assert.Throws<CorruptDataException>(() => Raster.Load(builder.ToArray()));
    }

    /// <summary>An 8×8 gray progressive file: a DC scan, then DC refinement scans up to <paramref name="scans"/> in all, each one byte.</summary>
    private static Raster<Gray8> DcOnlyFile(int scans)
    {
        using JpegBuilder builder = new JpegBuilder().Quant(0, 75).Frame(0xC2, 8, 8, 8, (1, 1, 1)).Huffman(0, 0);
        builder.Segment(0xDA, [1, 1, 0, 0, 0, 0]).Raw(0x00);
        for (int i = 1; i < scans; i++)
        {
            builder.Segment(0xDA, [1, 1, 0, 0, 0, 0x10]).Raw(0x00);
        }

        return Raster.Load<Gray8>(builder.Raw(0xFF, 0xD9).ToArray());
    }

    [Fact]
    public void MissingTablesThrow()
    {
        Assert.Throws<CorruptDataException>(() => Raster.Load(new JpegBuilder().Frame(0xC0, 8, 8, 8, (1, 1, 1)).Scan(1)));
        Assert.Throws<CorruptDataException>(() => Raster.Load(JpegBuilder.Baseline(8, 8, (1, 1, 1)).Scan(1)));
    }

    private void AssertClose(JsonElement entry, string reference, byte[] actual, string label)
    {
        byte[] expected = Load(reference);
        Assert.Equal(expected.Length, actual.Length);
        double sum = 0;
        int max = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = Math.Abs(expected[i] - actual[i]);
            sum += d * d;
            max = Math.Max(max, d);
        }

        double psnr = sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / (sum / expected.Length));
        output.WriteLine($"{label}: {psnr:F1} dB, max {max}");
        Assert.True(psnr >= 50 && max <= 2, $"{label} ({entry.GetProperty("sampling")}): PSNR {psnr:F2} dB, max difference {max}");
    }

    private static byte[] Bytes(Raster raster)
    {
        return raster switch
        {
            Raster<Rgb8> rgb => Pack(rgb),
            Raster<Gray8> gray => Pack(gray),
            _ => throw new InvalidOperationException(),
        };
    }

    private static byte[] Pack<T>(Raster<T> raster)
        where T : unmanaged, IPixelType<T>
    {
        int bpp = T.Layout.BytesPerPixel;
        byte[] bytes = new byte[raster.Width * raster.Height * bpp];
        for (int y = 0; y < raster.Height; y++)
        {
            MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(bytes.AsSpan(y * raster.Width * bpp));
        }

        return bytes;
    }

    private static byte[] Load(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name)), CompressionMode.Decompress);
        using MemoryStream buffer = new();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static JsonElement Entry(string file) => Manifest().Single(e => e.GetProperty("file").GetString() == file);

    private static JsonElement[] Manifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return [.. json.RootElement.GetProperty("files").EnumerateArray().Select(e => e.Clone())];
    }
}
