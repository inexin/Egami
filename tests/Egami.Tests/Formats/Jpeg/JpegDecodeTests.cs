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
        return [.. Manifest().Where(e => !e.TryGetProperty("unsupported", out _) && !e.GetProperty("progressive").GetBoolean()).Select(e => e.GetProperty("file").GetString()!)];
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
