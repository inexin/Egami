using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Formats.Jpeg2000;
using Egami.Formats.Jxl;
using Egami.Formats.Png;
using Egami.Pixels;
using Egami.Tests.Formats.Jpeg2000;

namespace Egami.Tests.Formats;

/// <summary>Stored samples: JPEG, JPEG 2000 and JPEG XL codestreams decoded to their component values, with no conversion.</summary>
public class SampleImageTests
{
    private static readonly string Root = AppContext.BaseDirectory;

    [Theory]
    [InlineData("signed12.j2k", 11, true)]
    [InlineData("signed12-lossy.j2k", 11, true)]
    [InlineData("gray12-lossless.j2k", 12, false)]
    [InlineData("gray12-lossy.jp2", 12, false)]
    public void Jpeg2000GivesTheStoredValuesWithTheirSign(string file, int precision, bool isSigned)
    {
        // The signed files store 11 bits (the PGX references say so too).
        string stem = Path.GetFileNameWithoutExtension(file);
        SampleImage image = SampleImage.Load(Path.Combine(Root, "images", "jpeg2000", file));
        (int width, int height, int[] expected) = Pgx(Path.Combine(Root, "reference", "jpeg2000", $"{stem}_r0_0.pgx.gz"));

        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Equal([new SampleComponent(precision, isSigned)], image.Components);
        Assert.Equal(SampleColor.Gray, image.Color);
        // Reversible files match OpenJPEG exactly; irreversible ones within 1 level, as in the corpus tests.
        int[] actual = image.Samples.ToArray();
        Assert.Equal(expected.Length, actual.Length);
        int worst = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(worst <= (stem.Contains("lossy", StringComparison.Ordinal) ? 1 : 0), $"{file}: off by up to {worst}");
        Assert.Equal(isSigned, image.Samples.Span.ToArray().Any(v => v < 0));
    }

    [Fact]
    public void Jpeg2000ComponentsMatchTheDetails()
    {
        SampleImage image = SampleImage.Load(Path.Combine(Root, "images", "jpeg2000", "signed12.j2k"));
        Jpeg2000Details details = Assert.IsType<Jpeg2000Details>(image.Metadata.FormatDetails);

        Assert.Equal(details.Components.Select(c => new SampleComponent(c.Precision, c.IsSigned)), image.Components);
        Assert.Equal("JPEG 2000", image.Metadata.FormatName);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 8)]
    [InlineData(1, 12)]
    [InlineData(1, 16)]
    [InlineData(3, 4)]
    [InlineData(3, 8)]
    [InlineData(3, 12)]
    [InlineData(3, 16)]
    public void LosslessJpegGivesTheCodedSamples(int components, int precision)
    {
        const int width = 23, height = 17;
        ushort[] coded = new ushort[width * height * components];
        Random random = new(precision * 7 + components);
        for (int i = 0; i < coded.Length; i++)
        {
            coded[i] = (ushort)random.Next(1 << precision);
        }

        using MemoryStream stream = new();
        JpegLosslessWriter.Write(stream, coded, components, width, precision, predictor: null);

        SampleImage image = SampleImage.Load(stream.ToArray(), JpegFormat.Info);

        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.All(image.Components, c => Assert.Equal(new SampleComponent(precision, false), c));
        Assert.Equal(components == 1 ? SampleColor.Gray : SampleColor.Rgb, image.Color);
        Assert.Equal(coded.Select(v => (int)v), image.Samples.ToArray());
    }

    public static TheoryData<string> DepthFiles()
    {
        // Lossless files that store RGB or gray at full resolution give djpeg's samples exactly; 12-bit DCT files within 1 level.
        return [.. DepthManifest()
            .Where(e => !e.TryGetProperty("unsupported", out _) && !e.TryGetProperty("note", out _))
            .Where(e => e.GetProperty("file").GetString()!.StartsWith("p12-", StringComparison.Ordinal) && e.GetProperty("channels").GetInt32() == 1
                || e.GetProperty("file").GetString()!.StartsWith("ll", StringComparison.Ordinal) && e.GetProperty("sampling").GetString() is null or "1x1" or "1x1,1x1,1x1")
            .Select(e => e.GetProperty("file").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(DepthFiles))]
    public void JpegDepthFilesGiveTheirSamplesAtTheirPrecision(string file)
    {
        JsonElement entry = DepthManifest().Single(e => e.GetProperty("file").GetString() == file);
        int precision = entry.GetProperty("precision").GetInt32();
        bool lossless = entry.GetProperty("lossless").GetBoolean();
        SampleImage image = SampleImage.Load(Path.Combine(Root, "images", "jpeg", "depth", file));
        byte[] raw = Gunzip(Path.Combine(Root, "reference", "jpeg", "depth", entry.GetProperty("reference").GetString()!));
        int[] expected = precision > 8 ? [.. MemoryMarshal.Cast<byte, ushort>(raw).ToArray().Select(v => (int)v)] : [.. raw.Select(v => (int)v)];

        Assert.All(image.Components, c => Assert.Equal(new SampleComponent(precision, false), c));
        Assert.Equal(expected.Length, image.Samples.Length);
        int[] actual = image.Samples.ToArray();
        int worst = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(worst <= (lossless ? 0 : 1), $"{file}: off by up to {worst}");
    }

    [Fact]
    public void YCbCrJpegKeepsItsComponents()
    {
        using Raster<Rgb8> source = Photo();
        using MemoryStream stream = new();
        source.Save(stream, JpegFormat.Info, new JpegWriterOptions { Quality = 90, Subsampling = ChromaSubsampling.Subsample444 });

        SampleImage image = SampleImage.Load(stream.ToArray());
        using YuvRaster yuv = YuvRaster.Load(stream.ToArray());
        YuvRaster<byte> planes = Assert.IsType<YuvRaster<byte>>(yuv);

        Assert.Equal(SampleColor.YCbCr, image.Color);
        Assert.All(image.Components, c => Assert.Equal(new SampleComponent(8, false), c));
        for (int y = 0; y < image.Height; y++)
        {
            Span<int> row = image.Row(y);
            ReadOnlySpan<byte> l = planes.Y.Row(y), u = planes.U!.Row(y), v = planes.V!.Row(y);
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal((l[x], u[x], v[x]), ((byte)row[3 * x], (byte)row[(3 * x) + 1], (byte)row[(3 * x) + 2]));
            }
        }
    }

    [Fact]
    public void GrayJpegMatchesTheRaster()
    {
        using Raster<Gray8> source = Raster.Create<Gray8>(40, 30);
        for (int y = 0; y < 30; y++)
        {
            Span<byte> row = MemoryMarshal.AsBytes(source.Row(y));
            for (int x = 0; x < 40; x++)
            {
                row[x] = (byte)((x * 6) + y);
            }
        }

        using MemoryStream stream = new();
        source.Save(stream, JpegFormat.Info);
        SampleImage image = SampleImage.Load(stream.ToArray());
        using Raster<Gray8> raster = Raster.Load<Gray8>(stream.ToArray());

        Assert.Equal(SampleColor.Gray, image.Color);
        for (int y = 0; y < 30; y++)
        {
            Assert.Equal(MemoryMarshal.AsBytes(raster.Row(y)).ToArray().Select(v => (int)v), image.Row(y).ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LosslessJxlGivesTheCodedSamples(bool wide)
    {
        const int width = 31, height = 19;
        using MemoryStream stream = new();
        int[] expected;
        if (wide)
        {
            using Raster<Rgb16> source = Raster.Create<Rgb16>(width, height);
            Random random = new(5);
            for (int y = 0; y < height; y++)
            {
                random.NextBytes(MemoryMarshal.AsBytes(source.Row(y)));
            }

            expected = [.. Enumerable.Range(0, height).SelectMany(y => MemoryMarshal.Cast<Rgb16, ushort>(source.Row(y)).ToArray().Select(v => (int)v))];
            source.Save(stream, JxlFormat.Info, new JxlWriterOptions { Lossless = true });
        }
        else
        {
            using Raster<Rgb8> source = Photo(width, height);
            expected = [.. Enumerable.Range(0, height).SelectMany(y => MemoryMarshal.AsBytes(source.Row(y)).ToArray().Select(v => (int)v))];
            source.Save(stream, JxlFormat.Info, new JxlWriterOptions { Lossless = true });
        }

        SampleImage image = SampleImage.Load(stream.ToArray());

        Assert.Equal(SampleColor.Rgb, image.Color);
        Assert.All(image.Components, c => Assert.Equal(new SampleComponent(wide ? 16 : 8, false), c));
        Assert.Equal(expected, image.Samples.ToArray());
    }

    [Fact]
    public void FormatsWithoutStoredSamplesAreNotSupported()
    {
        using Raster<Rgb8> source = Photo();
        using MemoryStream stream = new();
        source.Save(stream, PngFormat.Info);

        NotSupportedException e = Assert.Throws<NotSupportedException>(() => SampleImage.Load(stream.ToArray()));
        Assert.Contains("PNG", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CroppingScalingAndConvertingAreNotSupported()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Root, "images", "jpeg2000", "signed12.j2k"));

        Assert.Throws<NotSupportedException>(() => SampleImage.Load(data, options: new ReaderOptions { Region = new Rect(0, 0, 4, 4) }));
        Assert.Throws<NotSupportedException>(() => SampleImage.Load(data, options: new ReaderOptions { MinimumSize = new Size(4, 4) }));
    }

    [Fact]
    public void FourComponentJpegIsNotSupported()
    {
        ushort[] coded = new ushort[8 * 8 * 4];
        using MemoryStream stream = new();
        JpegLosslessWriter.Write(stream, coded, 4, 8, 8, predictor: 1);

        Assert.Throws<NotSupportedException>(() => SampleImage.Load(stream.ToArray(), JpegFormat.Info));
    }

    [Fact]
    public void RowsAreChecked()
    {
        SampleImage image = SampleImage.Load(Path.Combine(Root, "images", "jpeg2000", "gray12-lossless.j2k"));

        Assert.Throws<ArgumentOutOfRangeException>(() => image.Row(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Row(image.Height));
        Assert.Equal(image.Width, image.Row(image.Height - 1).Length);
    }

    private static Raster<Rgb8> Photo(int width = 48, int height = 32)
    {
        Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            for (int x = 0; x < width; x++)
            {
                row[3 * x] = (byte)(x * 5);
                row[(3 * x) + 1] = (byte)(y * 7);
                row[(3 * x) + 2] = (byte)((x * y) + 40);
            }
        }

        return raster;
    }

    private static JsonElement[] DepthManifest()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "reference", "jpeg", "depth", "manifest.json")));
        return [.. document.RootElement.GetProperty("files").EnumerateArray().Select(e => e.Clone())];
    }

    private static byte[] Gunzip(string path)
    {
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream memory = new();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    private static (int Width, int Height, int[] Samples) Pgx(string path)
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pgx");
        File.WriteAllBytes(temp, Gunzip(path));
        try
        {
            return Jpeg2000ConformanceTests.ReadPgx(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
