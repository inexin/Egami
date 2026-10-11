using System.Runtime.InteropServices;
using System.Text;

using Egami.Formats;
using Egami.Formats.Pnm;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Pnm;

public class PnmTests(ITestOutputHelper output)
{
    private static readonly string[] Preferred = ["expected", "netpbm", "magick"];

    public static TheoryData<string> Files() => [.. SimpleCorpus.Files("pnm")];

    [Theory]
    [InlineData(".pbm", "P4")]
    [InlineData(".pgm", "P5")]
    [InlineData(".ppm", "P6")]
    [InlineData(".pam", "P7")]
    [InlineData(".pnm", "P7")]
    public void ExtensionChoosesTheKind(string extension, string magic)
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 2, new Rgba8(200, 100, 50, 128));
        string path = Path.Combine(Path.GetTempPath(), $"egami-{Guid.NewGuid():N}{extension}");
        try
        {
            image.Save(path);
            Assert.Equal(magic, Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 2));

            // Options that name a kind, or options of another kind, win over the extension; plain text is kept.
            image.Save(path, new PnmWriterOptions { Kind = PnmKind.Pam });
            Assert.Equal("P7", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 2));
            if (extension == ".pgm")
            {
                image.Save(path, new PnmWriterOptions { PlainText = true });
                Assert.Equal("P2", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 2));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The script's own pixels decide where it wrote the file by hand; otherwise Netpbm, which reads every variant. Pillow scales
    /// maxvals other than 255 and 16-bit gray wrongly and cannot read PAM, so it is only reported.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesLikeTheReference(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(SimpleCorpus.Read(file));
        byte[] actual = Bytes(raster);
        string tool = Preferred.First(t => SimpleCorpus.Pixels(file, t) is not null);
        foreach (string other in new[] { "expected", "netpbm", "magick", "pillow" })
        {
            if (SimpleCorpus.Pixels(file, other) is byte[] pixels)
            {
                output.WriteLine($"{other}: largest difference {Largest(pixels, actual)}");
            }
        }

        Assert.Equal(0, Largest(SimpleCorpus.Pixels(file, tool)!, actual));
    }

    [Theory]
    [InlineData("pnm/bits.pbm", PixelChannelOrder.Gray, PixelChannelType.Unorm8)]
    [InlineData("pnm/bits-plain.pbm", PixelChannelOrder.Gray, PixelChannelType.Unorm8)]
    [InlineData("pnm/gray.pgm", PixelChannelOrder.Gray, PixelChannelType.Unorm8)]
    [InlineData("pnm/gray16.pgm", PixelChannelOrder.Gray, PixelChannelType.Unorm16)]
    [InlineData("pnm/maxval-1000.pgm", PixelChannelOrder.Gray, PixelChannelType.Unorm16)]
    [InlineData("pnm/rgb.ppm", PixelChannelOrder.Rgb, PixelChannelType.Unorm8)]
    [InlineData("pnm/rgb16.ppm", PixelChannelOrder.Rgba, PixelChannelType.Unorm16)]
    [InlineData("pnm/rgba.pam", PixelChannelOrder.Rgba, PixelChannelType.Unorm8)]
    [InlineData("pnm/gray-alpha.pam", PixelChannelOrder.GrayAlpha, PixelChannelType.Unorm8)]
    [InlineData("pnm/rgb-netpbm.pam", PixelChannelOrder.Rgb, PixelChannelType.Unorm8)]
    public void NaturalTypeHoldsTheFileWithoutLoss(string file, PixelChannelOrder order, PixelChannelType type)
    {
        byte[] data = SimpleCorpus.Read(file);
        using Raster raster = Raster.Load(data);
        Assert.Equal((order, type), (raster.Layout.ChannelOrder, raster.Layout.ChannelType));
        RasterInfo info = Raster.Probe(data);
        Assert.Equal(raster.Layout, info.Layout);
        Assert.Equal((raster.Width, raster.Height), (info.Width, info.Height));
        Assert.Equal(PnmFormat.Info, info.Format);
    }

    [Fact]
    public void SixteenBitSamplesKeepEveryBit()
    {
        byte[] data = [.. "P5\n3 1\n65535\n"u8, 0x00, 0x01, 0x80, 0x00, 0xFF, 0xFE];
        using Raster<Gray16> raster = Raster.Load<Gray16>(data);
        Assert.Equal([1, 0x8000, 0xFFFE], raster.Row(0).ToArray().Select(p => (int)p.L));
    }

    [Fact]
    public void OddMaxvalsScaleExactly()
    {
        byte[] data = [.. "P5\n4 1\n1000\n"u8, 0, 0, 0, 1, 0x01, 0xF4, 0x03, 0xE8];
        using Raster<Gray16> raster = Raster.Load<Gray16>(data);
        Assert.Equal([0, 66, 32768, 65535], raster.Row(0).ToArray().Select(p => (int)p.L));
    }

    [Fact]
    public void CommentsBecomeTextEntries()
    {
        using Raster raster = Raster.Load(SimpleCorpus.Read("pnm/p1-packed-digits.pbm"));
        Assert.Equal(["a comment", "width"], raster.Metadata.Text.Where(t => t.Key == "Comment").Select(t => t.Value));
        Assert.Equal(PnmKind.Bitmap, ((PnmDetails)raster.Metadata.FormatDetails!).Kind);
        Assert.True(((PnmDetails)raster.Metadata.FormatDetails!).PlainText);
    }

    [Fact]
    public void OnlyTheFirstImageIsReadWithAWarning()
    {
        byte[] data = SimpleCorpus.Read("pnm/two-images.pgm");
        using MemoryStream stream = new(data);
        using Raster raster = Raster.Load(stream);
        Assert.Single(raster.Metadata.Warnings);
        Assert.Equal(15, stream.Position);
    }

    [Fact]
    public void UnknownTupleTypeIsReadByDepth()
    {
        using Raster raster = Raster.Load(SimpleCorpus.Read("pnm/pam-unknown-tupltype.pam"));
        Assert.Equal(Rgb8.Layout, raster.Layout);
        Assert.Contains(raster.Metadata.Warnings, w => w.Message.Contains("EGAMI_TEST", StringComparison.Ordinal));
    }

    public static TheoryData<string, bool> RoundTripCases() => new()
    {
        { "Gray8", false }, { "Gray8", true }, { "Gray16", false }, { "Gray16", true }, { "Rgb8", false }, { "Rgb8", true },
        { "Rgba8", false }, { "GrayAlpha8", false }, { "Rgba16", false }, { "Bgra8", false },
    };

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTripsWithoutLoss(string type, bool plain)
    {
        switch (type)
        {
            case "Gray8": RoundTrip<Gray8>(plain); break;
            case "Gray16": RoundTrip<Gray16>(plain); break;
            case "Rgb8": RoundTrip<Rgb8>(plain); break;
            case "Rgba8": RoundTrip<Rgba8>(plain); break;
            case "GrayAlpha8": RoundTrip<GrayAlpha8>(plain); break;
            case "Rgba16": RoundTrip<Rgba16>(plain); break;
            default: RoundTrip<Bgra8>(plain); break;
        }
    }

    [Theory]
    [InlineData(PnmKind.Auto, "Gray8", "P5")]
    [InlineData(PnmKind.Auto, "Rgb8", "P6")]
    [InlineData(PnmKind.Auto, "Rgba8", "P7")]
    [InlineData(PnmKind.Bitmap, "Rgb8", "P4")]
    [InlineData(PnmKind.Graymap, "Rgba8", "P5")]
    [InlineData(PnmKind.Pixmap, "Rgba8", "P6")]
    [InlineData(PnmKind.Pam, "Rgb8", "P7")]
    public void KindPicksTheVariant(PnmKind kind, string type, string magic)
    {
        using Raster<Gray8> gray = Raster.Create<Gray8>(3, 2);
        using Raster<Rgb8> rgb = Raster.Create<Rgb8>(3, 2);
        using Raster<Rgba8> rgba = Raster.Create<Rgba8>(3, 2);
        PnmWriterOptions options = new() { Kind = kind };
        byte[] data = type switch { "Gray8" => Save(gray, options), "Rgb8" => Save(rgb, options), _ => Save(rgba, options) };
        Assert.Equal(magic, Encoding.ASCII.GetString(data, 0, 2));
        using Raster back = Raster.Load(data);
        Assert.Equal((3, 2), (back.Width, back.Height));
    }

    [Fact]
    public void BitmapsThresholdAtHalfGray()
    {
        using Raster<Gray8> source = Raster.Create<Gray8>(10, 1);
        Span<Gray8> row = source.Row(0);
        for (int x = 0; x < 10; x++)
        {
            row[x] = new Gray8((byte)(x * 28));
        }

        foreach (bool plain in new[] { false, true })
        {
            byte[] data = Save(source, new PnmWriterOptions { Kind = PnmKind.Bitmap, PlainText = plain });
            using Raster<Gray8> back = Raster.Load<Gray8>(data);
            Assert.Equal(row.ToArray().Select(p => p.L < 128 ? 0 : 255), back.Row(0).ToArray().Select(p => (int)p.L));
        }
    }

    [Fact]
    public void PlainLinesStayWithinSeventyCharacters()
    {
        using Raster<Rgb8> source = Raster.Create<Rgb8>(50, 3, new Rgb8(255, 128, 7));
        byte[] data = Save(source, new PnmWriterOptions { PlainText = true });
        string[] lines = Encoding.ASCII.GetString(data).Split('\n');
        Assert.Equal("P3", lines[0]);
        Assert.All(lines, l => Assert.True(l.Length <= 70));
    }

    [Fact]
    public void CommentsAreWritten()
    {
        using Raster<Gray8> source = Raster.Create<Gray8>(2, 2);
        source.Metadata.Text.Add(new TextEntry("Comment", "made by\nEgami"));
        byte[] data = Save(source);
        Assert.StartsWith("P5\n# made by\n# Egami\n2 2\n255\n", Encoding.ASCII.GetString(data), StringComparison.Ordinal);
        using Raster back = Raster.Load(data);
        Assert.Equal(["made by", "Egami"], back.Metadata.Text.Select(t => t.Value));
        Assert.DoesNotContain("#", Encoding.ASCII.GetString(Save(source, new PnmWriterOptions { StripMetadata = true })), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pnm/rgb.ppm")]
    [InlineData("pnm/rgb-plain.ppm")]
    [InlineData("pnm/rgb16.ppm")]
    [InlineData("pnm/bits.pbm")]
    [InlineData("pnm/rgba.pam")]
    public void TruncatedFilesKeepRowsWithAWarning(string file)
    {
        byte[] data = SimpleCorpus.Read(file);
        ReadOnlyMemory<byte> cut = data.AsMemory(0, (data.Length * 2) / 3);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(cut);
        Assert.Single(raster.Metadata.Warnings);
        using Raster<Rgba8> full = Raster.Load<Rgba8>(data);
        Assert.True(full.Row(0).SequenceEqual(raster.Row(0)));
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Theory]
    [InlineData("P5\n2 1\n100\n\x00\x65")]
    [InlineData("P2\n2 1\n100\n0 101\n")]
    public void SamplesAboveMaxvalAreClamped(string text)
    {
        byte[] data = Encoding.Latin1.GetBytes(text);
        using Raster<Gray8> raster = Raster.Load<Gray8>(data);
        Assert.Equal(255, raster.Row(0)[1].L);
        Assert.Single(raster.Metadata.Warnings);
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Theory]
    [InlineData("P5\n99999999999 1\n255\n")]
    [InlineData("P5\n0 1\n255\n")]
    [InlineData("P5\n1 1\n70000\n")]
    [InlineData("P5\nx 1\n255\n")]
    [InlineData("P7\nWIDTH 1\nHEIGHT 1\nENDHDR\n")]
    public void BadHeadersThrow(string text)
    {
        Assert.Throws<CorruptDataException>(() => Raster.Load(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void HugeSizeIsRejectedBeforeAllocating()
    {
        Assert.Throws<LimitExceededException>(() => Raster.Load(Encoding.ASCII.GetBytes("P6\n2000000000 2000000000\n255\n")));
    }

    [Fact]
    public void PamDepthAboveFourIsUnsupported()
    {
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(Encoding.ASCII.GetBytes("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 5\nMAXVAL 255\nENDHDR\n12345")));
    }

    private static void RoundTrip<TPixel>(bool plain)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Random random = new(7);
        using Raster<TPixel> source = Raster.Create<TPixel>(37, 5);
        for (int y = 0; y < source.Height; y++)
        {
            random.NextBytes(MemoryMarshal.AsBytes(source.Row(y)));
        }

        byte[] data = Save(source, new PnmWriterOptions { PlainText = plain });
        using Raster<TPixel> back = Raster.Load<TPixel>(data);
        Assert.Equal(Bytes(source), Bytes(back));
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, PnmWriterOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = new();
        raster.Save(stream, PnmFormat.Info, options ?? PnmWriterOptions.Default);
        return stream.ToArray();
    }

    private static int Largest(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int largest = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            largest = Math.Max(largest, Math.Abs(expected[i] - actual[i]));
        }

        return largest;
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<byte> bytes = [];
        for (int y = 0; y < raster.Height; y++)
        {
            bytes.AddRange(MemoryMarshal.AsBytes(raster.Row(y)).ToArray());
        }

        return [.. bytes];
    }
}
