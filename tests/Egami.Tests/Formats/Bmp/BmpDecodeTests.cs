using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Bmp;

public class BmpDecodeTests(ITestOutputHelper output)
{
    private static readonly string[] Tools = ["expected", "magick", "pillow"];

    public static TheoryData<string> Files() => [.. SimpleCorpus.Files("bmp")];

    /// <summary>
    /// The script's own pixels decide for hand-made files, else ImageMagick, which follows the alpha rule Egami uses (a 32-bit
    /// file's fourth byte is alpha unless it is 0 everywhere; alpha masks are always used), where Pillow drops alpha. 5 and 6-bit
    /// channels may differ by 1 from ImageMagick, which replicates bits where Egami rounds exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesLikeTheReference(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(SimpleCorpus.Read(file));
        byte[] actual = Bytes(raster);
        foreach (string tool in Tools)
        {
            if (SimpleCorpus.Pixels(file, tool) is byte[] pixels)
            {
                output.WriteLine($"{tool}: largest difference {Largest(pixels, actual)}");
            }
        }

        string reference = Tools.First(t => SimpleCorpus.Pixels(file, t) is not null);
        byte[] expected = SimpleCorpus.Pixels(file, reference)!;
        if (file == "bmp/argb1555.bmp")
        {
            // ImageMagick widens the 1-bit alpha to 128; scaled exactly, 1 is 255.
            for (int i = 3; i < expected.Length; i += 4)
            {
                expected[i] = expected[i] == 128 ? (byte)255 : expected[i];
            }
        }

        int tolerance = file is "bmp/rgb565.bmp" or "bmp/argb1555.bmp" ? 1 : 0;
        Assert.InRange(Largest(expected, actual), 0, tolerance);
        foreach (EgamiDiagnostic warning in raster.Metadata.Warnings)
        {
            output.WriteLine(warning.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeAgreesWithLoad(string file)
    {
        byte[] data = SimpleCorpus.Read(file);
        RasterInfo info = Raster.Probe(data);
        using Raster raster = Raster.Load(data);
        Assert.Equal(BmpFormat.Info, info.Format);
        Assert.Equal((raster.Width, raster.Height, raster.Layout), (info.Width, info.Height, info.Layout));
        Assert.Equal("BMP", raster.Metadata.FormatName);
    }

    [Theory]
    [InlineData("bmp/rgb24.bmp", "Bgr8")]
    [InlineData("bmp/pal8.bmp", "Bgr8")]
    [InlineData("bmp/rle8-magick.bmp", "Bgr8")]
    [InlineData("bmp/rgb565.bmp", "Bgr8")]
    [InlineData("bmp/argb1555.bmp", "Bgra8")]
    [InlineData("bmp/rgba32.bmp", "Bgra8")]
    [InlineData("bmp/rgb32-zero-alpha.bmp", "Bgra8")]
    [InlineData("bmp/embedded-png.bmp", "Rgb8")]
    [InlineData("bmp/embedded-jpeg.bmp", "Rgb8")]
    public void NaturalTypes(string file, string type)
    {
        using Raster raster = Raster.Load(SimpleCorpus.Read(file));
        Assert.Equal(type, raster.GetType().GetGenericArguments()[0].Name);
    }

    [Fact]
    public void ZeroFourthByteLoadsOpaqueInEveryType()
    {
        using Raster<Rgba16> wide = Raster.Load<Rgba16>(SimpleCorpus.Read("bmp/rgb32-zero-alpha.bmp"));
        Assert.All(wide.Row(3).ToArray(), p => Assert.Equal(ushort.MaxValue, p.A));
    }

    [Fact]
    public void DetailsDescribeTheFile()
    {
        using Raster raster = Raster.Load(SimpleCorpus.Read("bmp/top-down-24.bmp"));
        BmpDetails details = (BmpDetails)raster.Metadata.FormatDetails!;
        Assert.True(details.TopDown);
        Assert.Equal(24, details.BitsPerPixel);
        Assert.Equal(new Resolution(2835, 2835, ResolutionUnit.PerMeter), raster.Metadata.Resolution);

        using Raster rle = Raster.Load(SimpleCorpus.Read("bmp/rle8-delta.bmp"));
        Assert.Equal(BmpCompression.Rle8, ((BmpDetails)rle.Metadata.FormatDetails!).Compression);
        Assert.Equal(5, ((BmpDetails)rle.Metadata.FormatDetails!).Palette.Length);
    }

    [Fact]
    public void IndexPastThePaletteIsBlackWithAWarning()
    {
        using Raster raster = Raster.Load(SimpleCorpus.Read("bmp/pal8-index-past-palette.bmp"));
        Assert.Single(raster.Metadata.Warnings);
    }

    [Theory]
    [InlineData("bmp/rgb24.bmp")]
    [InlineData("bmp/pal4-magick.bmp")]
    [InlineData("bmp/rle8-magick.bmp")]
    [InlineData("bmp/rgba32.bmp")]
    [InlineData("bmp/rgb565.bmp")]
    public void TruncatedFilesKeepRowsWithAWarning(string file)
    {
        byte[] data = SimpleCorpus.Read(file);
        ReadOnlyMemory<byte> cut = data.AsMemory(0, (data.Length * 2) / 3);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(cut);
        Assert.NotEmpty(raster.Metadata.Warnings);
        using Raster<Rgba8> full = Raster.Load<Rgba8>(data);

        // Rows are stored bottom-up, so the bottom row is read first and the top one is missing.
        Assert.True(full.Row(full.Height - 1).SequenceEqual(raster.Row(raster.Height - 1)));
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void BadHeadersThrow()
    {
        byte[] data = SimpleCorpus.Read("bmp/rgb24.bmp");
        byte[] zeroWidth = (byte[])data.Clone();
        zeroWidth.AsSpan(18, 4).Clear();
        Assert.Throws<CorruptDataException>(() => Raster.Load(zeroWidth));

        byte[] badBits = (byte[])data.Clone();
        badBits[28] = 7;
        Assert.Throws<CorruptDataException>(() => Raster.Load(badBits));

        // Huffman 1D is 1-bit only.
        byte[] huffman = (byte[])SimpleCorpus.Read("bmp/os2-pal8.bmp").Clone();
        huffman[30] = 3;
        Assert.Throws<CorruptDataException>(() => Raster.Load(huffman));

        byte[] huge = (byte[])data.Clone();
        BitConverter.TryWriteBytes(huge.AsSpan(18), 1_000_000_000);
        BitConverter.TryWriteBytes(huge.AsSpan(22), 1_000_000_000);
        Assert.Throws<LimitExceededException>(() => Raster.Load(huge));
    }

    [Fact]
    public void OverlappingMasksAreCorrupt()
    {
        byte[] data = (byte[])SimpleCorpus.Read("bmp/alphabitfields-4444.bmp").Clone();
        BitConverter.TryWriteBytes(data.AsSpan(54), 0x0FF0u);
        Assert.Throws<CorruptDataException>(() => Raster.Load(data));
    }

    [Theory]
    [InlineData(true, 64)]
    [InlineData(false, 64)]
    [InlineData(true, 40)]
    public void ReadsOs2Huffman1D(bool endOfLine, int headerSize)
    {
        const int width = 37, height = 9;
        int rowBytes = (width + 7) / 8;
        byte[] packed = new byte[rowBytes * height];
        new Random(5).NextBytes(packed);
        for (int y = 0; y < height; y++)
        {
            // Long runs too, and the padding bits clear.
            packed.AsSpan(y * rowBytes, 2).Fill((byte)(y % 2 == 0 ? 0xFF : 0x00));
            packed[(y * rowBytes) + rowBytes - 1] &= 0xF8;
        }

        byte[] coded = Egami.Formats.Tiff.CcittEncoder.Encode(packed, width, height, group4: false, endOfLine, endOfBlock: endOfLine);
        Rgb8 white = new(250, 240, 230), black = new(5, 10, 20);
        int offset = 14 + headerSize + 8;
        byte[] file = new byte[offset + coded.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), offset);
        BitConverter.TryWriteBytes(file.AsSpan(14), headerSize);
        BitConverter.TryWriteBytes(file.AsSpan(18), width);
        BitConverter.TryWriteBytes(file.AsSpan(22), height);
        BitConverter.TryWriteBytes(file.AsSpan(26), (ushort)1);
        BitConverter.TryWriteBytes(file.AsSpan(28), (ushort)1);
        BitConverter.TryWriteBytes(file.AsSpan(30), 3);
        BitConverter.TryWriteBytes(file.AsSpan(34), coded.Length);
        int palette = 14 + headerSize;
        (file[palette], file[palette + 1], file[palette + 2]) = (white.B, white.G, white.R);
        (file[palette + 4], file[palette + 5], file[palette + 6]) = (black.B, black.G, black.R);
        coded.CopyTo(file, offset);

        using Raster raster = Raster.Load(file, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
        Assert.Equal(BmpCompression.Huffman1D, ((BmpDetails)raster.Metadata.FormatDetails!).Compression);
        Assert.Empty(raster.Metadata.Warnings);
        using Raster<Rgb8> pixels = Raster.Load<Rgb8>(file);
        for (int y = 0; y < height; y++)
        {
            // Rows are stored bottom-up.
            ReadOnlySpan<byte> bits = packed.AsSpan((height - 1 - y) * rowBytes, rowBytes);
            for (int x = 0; x < width; x++)
            {
                Assert.Equal((bits[x >> 3] & (0x80 >> (x & 7))) != 0 ? black : white, pixels.Row(y)[x]);
            }
        }
    }

    [Fact]
    public void TruncatedHuffman1DKeepsTheRowsItHas()
    {
        const int width = 16, height = 8;
        byte[] packed = new byte[2 * height];
        byte[] coded = Egami.Formats.Tiff.CcittEncoder.Encode(packed, width, height, group4: false, endOfLine: true, endOfBlock: false);
        byte[] file = new byte[14 + 64 + 8 + (coded.Length / 2)];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(10), 14 + 64 + 8);
        BitConverter.TryWriteBytes(file.AsSpan(14), 64);
        BitConverter.TryWriteBytes(file.AsSpan(18), width);
        BitConverter.TryWriteBytes(file.AsSpan(22), height);
        BitConverter.TryWriteBytes(file.AsSpan(26), (ushort)1);
        BitConverter.TryWriteBytes(file.AsSpan(28), (ushort)1);
        BitConverter.TryWriteBytes(file.AsSpan(30), 3);
        file[78] = file[79] = file[80] = 255;
        coded.AsSpan(0, coded.Length / 2).CopyTo(file.AsSpan(86));
        using Raster raster = Raster.Load(file);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Bmp.RowsMissing");
        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
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
