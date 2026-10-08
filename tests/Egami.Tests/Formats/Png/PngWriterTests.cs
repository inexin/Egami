using System.Buffers.Binary;
using System.Text;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Png;

public class PngWriterTests
{
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    [Fact]
    public void EveryPixelTypeRoundTripsExactlyByDefault()
    {
        RoundTrip<Rgba8>(PngColorType.Rgba, 8);
        RoundTrip<Bgra8>(PngColorType.Rgba, 8);
        RoundTrip<Rgb8>(PngColorType.Rgb, 8);
        RoundTrip<Bgr8>(PngColorType.Rgb, 8);
        RoundTrip<Gray8>(PngColorType.Gray, 8);
        RoundTrip<Gray16>(PngColorType.Gray, 16);
        RoundTrip<GrayAlpha8>(PngColorType.GrayAlpha, 8);
        RoundTrip<Rgba16>(PngColorType.Rgba, 16);

        static void RoundTrip<T>(PngColorType colorType, int depth)
            where T : unmanaged, IPixelType<T>
        {
            using Raster<T> original = Random<T>(37, 23, seed: 1);
            byte[] file = Save(original);
            Assert.Equal((byte)colorType, file[25]);
            Assert.Equal(depth, file[24]);
            using Raster<T> loaded = Raster.Load<T>(file, options: Strict);
            AssertPixelsEqual(original, loaded);
        }
    }

    [Fact]
    public void FloatPixelsAreWrittenAsSixteenBit()
    {
        using Raster<RgbaF32> original = Raster.Create(3, 1, new RgbaF32(0.5f, 2f, -1f, 0.25f));
        byte[] file = Save(original);
        Assert.Equal(16, file[24]);
        using Raster<Rgba16> loaded = Raster.Load<Rgba16>(file);
        Assert.Equal(new Rgba16(32768, 65535, 0, 16384), loaded.GetPixel(2, 0));
    }

    public static TheoryData<PngColorType, int> AllForms()
    {
        return new TheoryData<PngColorType, int>
        {
            { PngColorType.Gray, 1 }, { PngColorType.Gray, 2 }, { PngColorType.Gray, 4 }, { PngColorType.Gray, 8 }, { PngColorType.Gray, 16 },
            { PngColorType.Rgb, 8 }, { PngColorType.Rgb, 16 },
            { PngColorType.Palette, 1 }, { PngColorType.Palette, 2 }, { PngColorType.Palette, 4 }, { PngColorType.Palette, 8 },
            { PngColorType.GrayAlpha, 8 }, { PngColorType.GrayAlpha, 16 },
            { PngColorType.Rgba, 8 }, { PngColorType.Rgba, 16 },
        };
    }

    [Theory]
    [MemberData(nameof(AllForms))]
    public void EveryColorTypeAndBitDepthRoundTrips(PngColorType colorType, int depth)
    {
        // Pixels that the form can hold exactly, decoded from a file built independently of Egami.
        PngBuilder source = PngReaderTests.Build(colorType, depth, false, colorType == PngColorType.Palette, 29, 17, seed: depth);
        using Raster<Rgba16> original = Raster.Load<Rgba16>(source.Build());

        foreach (PngFilterMode filter in Enum.GetValues<PngFilterMode>())
        {
            byte[] file = Save(original, new PngWriterOptions { ColorType = colorType, BitDepth = depth, Filter = filter });
            Assert.Equal((byte)colorType, file[25]);
            Assert.Equal(depth, file[24]);
            using Raster<Rgba16> loaded = Raster.Load<Rgba16>(file, options: Strict);
            AssertPixelsEqual(original, loaded);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void EveryCompressionLevelAndStrategyRoundTrips(int level)
    {
        using Raster<Rgba8> original = Random<Rgba8>(64, 48, seed: level);
        foreach (PngCompressionStrategy strategy in Enum.GetValues<PngCompressionStrategy>())
        {
            byte[] file = Save(original, new PngWriterOptions { CompressionLevel = level, CompressionStrategy = strategy });
            using Raster<Rgba8> loaded = Raster.Load<Rgba8>(file, options: Strict);
            AssertPixelsEqual(original, loaded);
        }
    }

    [Theory]
    [InlineData("gray1", PngColorType.Gray, 1)]
    [InlineData("gray4", PngColorType.Gray, 4)]
    [InlineData("gray8", PngColorType.Gray, 8)]
    [InlineData("palette2", PngColorType.Palette, 2)]
    [InlineData("palette8", PngColorType.Palette, 8)]
    [InlineData("grayAlpha", PngColorType.GrayAlpha, 8)]
    [InlineData("rgb", PngColorType.Rgb, 8)]
    [InlineData("rgba", PngColorType.Rgba, 8)]
    [InlineData("gray16", PngColorType.Gray, 16)]
    [InlineData("rgba16", PngColorType.Rgba, 16)]
    public void ReducePicksTheSmallestExactForm(string content, PngColorType colorType, int depth)
    {
        Random random = new(content.Length);
        using Raster<Rgba16> original = Raster.Create<Rgba16>(40, 30);
        original.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                int n = (x * 7) + (y * 13);
                ushort a = (ushort)((x + y) % 3 == 0 ? 0 : 65535);
                row[x] = content switch
                {
                    "gray1" => Gray((n % 2) * 255),
                    "gray4" => Gray((n % 16) * 17),
                    "gray8" => Gray(n % 256),
                    "palette2" => new[] { Rgba16.FromRgba8(Rgba8.Red), Rgba16.FromRgba8(Rgba8.Blue), Rgba16.FromRgba8(Rgba8.Transparent) }[n % 3],
                    "palette8" => Rgba16.FromRgba8(new Rgba8((byte)(n % 200), 9, 7, 255)),
                    "grayAlpha" => new Rgba16((ushort)(n % 256 * 257), (ushort)(n % 256 * 257), (ushort)(n % 256 * 257), (ushort)(((n * 31) + y) % 256 * 257)),
                    "rgb" => Rgba16.FromRgba8(new Rgba8((byte)n, (byte)(n / 3), (byte)(n / 7), 255)),
                    "rgba" => Rgba16.FromRgba8(new Rgba8((byte)n, (byte)(n / 3), (byte)(n / 7), (byte)(a / 257))),
                    "gray16" => new Rgba16((ushort)(n * 33), (ushort)(n * 33), (ushort)(n * 33), 65535),
                    _ => new Rgba16((ushort)(n * 33), (ushort)n, 7, a),
                };
            }

            static Rgba16 Gray(int v)
            {
                return new Rgba16((ushort)(v * 257), (ushort)(v * 257), (ushort)(v * 257), 65535);
            }
        });

        byte[] file = Save(original, new PngWriterOptions { Reduce = true });
        Assert.Equal((byte)colorType, file[25]);
        Assert.Equal(depth, file[24]);
        using Raster<Rgba16> loaded = Raster.Load<Rgba16>(file, options: Strict);
        AssertPixelsEqual(original, loaded);
    }

    [Fact]
    public void DefaultCompressionLevelIsFive()
    {
        Assert.Equal(5, new PngWriterOptions().CompressionLevel);
        Assert.Equal(5, PngWriterOptions.Default.CompressionLevel);
    }

    [Fact]
    public void LowerBitDepthRoundsToTheNearestValue()
    {
        using Raster<Gray8> original = Raster.Create(4, 1, new Gray8(100));
        original.SetPixel(1, 0, new Gray8(200));
        byte[] file = Save(original, new PngWriterOptions { ColorType = PngColorType.Gray, BitDepth = 2 });
        using Raster<Gray8> loaded = Raster.Load<Gray8>(file);
        Assert.Equal(85, loaded.GetPixel(0, 0).L);
        Assert.Equal(170, loaded.GetPixel(1, 0).L);
    }

    [Fact]
    public void InvalidOptionsThrow()
    {
        using Raster<Rgba8> raster = Random<Rgba8>(20, 20, seed: 3);
        Assert.Throws<ArgumentException>(() => Save(raster, new PngWriterOptions { ColorType = PngColorType.Rgb, BitDepth = 4 }));
        Assert.Throws<ArgumentException>(() => Save(raster, new PngWriterOptions { BitDepth = 2 }));
        Assert.Throws<InvalidOperationException>(() => Save(raster, new PngWriterOptions { ColorType = PngColorType.Palette }));
        Assert.Throws<InvalidOperationException>(SavePaletteWithTooManyColors);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngWriterOptions { CompressionLevel = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngWriterOptions { MaxIdatSize = 0 });

        static byte[] SavePaletteWithTooManyColors()
        {
            using Raster<Rgba8> three = Raster.Create(3, 1, Rgba8.Red);
            three.SetPixel(1, 0, Rgba8.Lime);
            three.SetPixel(2, 0, Rgba8.Blue);
            return Save(three, new PngWriterOptions { ColorType = PngColorType.Palette, BitDepth = 1 });
        }
    }

    [Fact]
    public void ImageDataIsSplitIntoIdatChunks()
    {
        using Raster<Rgba8> raster = Random<Rgba8>(50, 50, seed: 4);
        byte[] file = Save(raster, new PngWriterOptions { MaxIdatSize = 1000, CompressionLevel = 0 });
        List<(string Type, int Length)> chunks = Chunks(file);
        Assert.True(chunks.Count(c => c.Type == "IDAT") > 5);
        Assert.All(chunks.Where(c => c.Type == "IDAT"), c => Assert.True(c.Length <= 1000));
        Assert.Equal(["IHDR", "IDAT"], chunks.Select(c => c.Type).Distinct().Take(2));
        Assert.Equal("IEND", chunks[^1].Type);
    }

    [Fact]
    public void MetadataRoundTripsInTheRightPositions()
    {
        PngBuilder builder = PngReaderTests.Build(PngColorType.Palette, 8, false, true, 6, 5, seed: 7);
        builder.ChunksBeforePalette.Add(("gAMA", BigEndian(45455)));
        builder.ChunksBeforePalette.Add(("cHRM", new byte[32]));
        builder.ChunksBeforePalette.Add(("iCCP", [.. "Profile"u8, 0, 0, .. PngBuilder.Compress(new byte[200])]));
        builder.ChunksBeforePalette.Add(("sBIT", [8, 8, 8]));
        builder.ChunksBeforeImageData.Add(("pHYs", [.. BigEndian(2835), .. BigEndian(2835), 1]));
        builder.ChunksBeforeImageData.Add(("tEXt", PngBuilder.Latin1("Title", "Café")));
        builder.ChunksBeforeImageData.Add(("zTXt", [.. "Comment"u8, 0, 0, .. PngBuilder.Compress("zipped"u8.ToArray())]));
        builder.ChunksBeforeImageData.Add(("bKGD", [3]));
        builder.ChunksAfterImageData.Add(("iTXt", [.. "Author"u8, 0, 0, 0, .. "nb"u8, 0, 0, .. "Ødegård"u8]));
        builder.ChunksAfterImageData.Add(("eXIf", [0x4D, 0x4D, 0, 42]));
        builder.ChunksAfterImageData.Add(("tIME", [7, 234, 10, 8, 3, 0, 0]));
        builder.ChunksAfterImageData.Add(("prVu", [1, 2]));

        using Raster original = Raster.Load(builder.Build(), options: Strict);
        using MemoryStream stream = new();
        original.Save(stream, PngFormat.Info, new PngWriterOptions { ColorType = PngColorType.Palette, BitDepth = 8 });
        byte[] file = stream.ToArray();

        Assert.Equal(
            ["IHDR", "iCCP", "gAMA", "cHRM", "pHYs", "eXIf", "tEXt", "zTXt", "iTXt", "sBIT", "PLTE", "tRNS", "bKGD", "IDAT", "tIME", "prVu", "IEND"],
            Chunks(file).Select(c => c.Type));

        using Raster reloaded = Raster.Load(file, options: Strict);
        RasterMetadata a = original.Metadata;
        RasterMetadata b = reloaded.Metadata;
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Resolution, b.Resolution);
        Assert.Equal(a.IccProfile.ToArray(), b.IccProfile.ToArray());
        Assert.Equal(a.Exif.ToArray(), b.Exif.ToArray());
        PngDetails pa = (PngDetails)a.FormatDetails!;
        PngDetails pb = (PngDetails)b.FormatDetails!;
        Assert.Equal(pa.Gamma, pb.Gamma);
        Assert.Equal(pa.IccProfileName, pb.IccProfileName);
        Assert.Equal(pa.TextSources, pb.TextSources);
        Assert.Equal(pa.OtherChunks.Select(c => (c.Type, c.Position, Convert.ToHexString(c.Data.Span))), pb.OtherChunks.Select(c => (c.Type, c.Position, Convert.ToHexString(c.Data.Span))));
    }

    [Fact]
    public void ChunksTiedToThePixelFormatAreDroppedWhenItChanges()
    {
        PngBuilder builder = PngReaderTests.Build(PngColorType.Rgb, 8, false, false, 4, 4, seed: 8);
        builder.ChunksBeforeImageData.Add(("bKGD", [0, 1, 0, 2, 0, 3]));
        builder.ChunksAfterImageData.Add(("prVu", [1]));
        using Raster original = Raster.Load(builder.Build());
        using MemoryStream stream = new();
        original.Save(stream, PngFormat.Info, new PngWriterOptions { ColorType = PngColorType.Rgba });
        Assert.Equal(["IHDR", "IDAT", "prVu", "IEND"], Chunks(stream.ToArray()).Select(c => c.Type));
    }

    [Fact]
    public void StripMetadataWritesOnlyTheImage()
    {
        using Raster<Rgba8> raster = Raster.Create(3, 3, Rgba8.Red);
        raster.Metadata.Text.Add(new TextEntry("Title", "x"));
        raster.Metadata.Resolution = new Resolution(72, 72, ResolutionUnit.PerInch);
        raster.Metadata.IccProfile = new byte[10];
        Assert.Equal(["IHDR", "IDAT", "IEND"], Chunks(Save(raster, new PngWriterOptions { StripMetadata = true })).Select(c => c.Type));
        Assert.Equal(["IHDR", "IDAT", "IEND"], Chunks(Save(raster, new WriterOptions { StripMetadata = true })).Select(c => c.Type));
    }

    [Fact]
    public void MetadataFromOtherSourcesIsMapped()
    {
        using Raster<Rgba8> raster = Raster.Create(2, 2, Rgba8.Red);
        raster.Metadata.Resolution = new Resolution(300, 300, ResolutionUnit.PerInch);
        raster.Metadata.ColorSpace = ColorSpaceHint.Srgb;
        raster.Metadata.Text.Add(new TextEntry("Title", "日本語"));
        raster.Metadata.Text.Add(new TextEntry("Language", "Hei", "nb"));
        byte[] file = Save(raster);
        Assert.Equal(["IHDR", "sRGB", "pHYs", "iTXt", "iTXt", "IDAT", "IEND"], Chunks(file).Select(c => c.Type));

        using Raster loaded = Raster.Load(file, options: Strict);
        Assert.Equal(300, loaded.Metadata.Resolution!.Value.ToPerInch().X, 1);
        Assert.Equal(ColorSpaceHint.Srgb, loaded.Metadata.ColorSpace);
        Assert.Equal(raster.Metadata.Text, loaded.Metadata.Text);
    }

    [Fact]
    public void InvalidTextKeywordThrows()
    {
        using Raster<Rgba8> raster = Raster.Create(2, 2, Rgba8.Red);
        raster.Metadata.Text.Add(new TextEntry(" leading space", "x"));
        Assert.Throws<ArgumentException>(() => Save(raster));
    }

    [Fact]
    public void WritingDoesNotChangeTheRaster()
    {
        using Raster<Rgba8> raster = Random<Rgba8>(20, 10, seed: 9);
        using Raster<Rgba8> before = raster.Copy();
        Save(raster, new PngWriterOptions { Reduce = true });
        Save(raster, new PngWriterOptions { ColorType = PngColorType.Gray, BitDepth = 2 });
        AssertPixelsEqual(before, raster);
    }

    [Fact]
    public void SaveToPathUsesPng()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-{Guid.NewGuid():N}.png");
        try
        {
            using Raster<Rgb8> raster = Random<Rgb8>(9, 9, seed: 10);
            raster.Save(path);
            using Raster<Rgb8> loaded = Raster.Load<Rgb8>(path);
            AssertPixelsEqual(raster, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<byte, int, int> FiltersStridesAndWidths()
    {
        return PngFilterTests.FiltersStridesAndWidths();
    }

    [Theory]
    [MemberData(nameof(FiltersStridesAndWidths))]
    public void SimdFilteringMatchesScalarAndReverses(byte filter, int stride, int bits)
    {
        Random random = new((filter * 1000) + (stride * 10) + bits);
        foreach (int pixels in new[] { 1, 2, 5, 16, 17, 33, 200 })
        {
            int length = pixels * stride;
            byte[] row = new byte[length];
            byte[] previous = new byte[length];
            random.NextBytes(row);
            random.NextBytes(previous);
            byte[] expected = new byte[length + 4];
            byte[] actual = new byte[length + 4];
            PngFilterEncoder.FilterScalar(filter, row, previous, stride, expected.AsSpan(0, length));
            PngFilterEncoder.Filter(filter, row, previous, stride, actual.AsSpan(0, length), bits);
            Assert.Equal(expected, actual);
            Assert.Equal(expected.Take(length).Sum(b => Math.Abs((int)(sbyte)b)), PngFilterEncoder.SumOfAbsolutes(actual.AsSpan(0, length), bits));

            PngFilter.Unfilter(filter, actual.AsSpan(0, length), previous, stride);
            Assert.Equal(row, actual.Take(length));
        }
    }

    internal static byte[] Save(Raster raster, WriterOptions? options = null)
    {
        using MemoryStream stream = new();
        raster.Save(stream, PngFormat.Info, options);
        return stream.ToArray();
    }

    private static Raster<T> Random<T>(int width, int height, int seed)
        where T : unmanaged, IPixelType<T>
    {
        Random random = new(seed);
        Raster<T> raster = Raster.Create<T>(width, height);
        byte[] bytes = new byte[width * height * T.Layout.BytesPerPixel];
        random.NextBytes(bytes);
        raster.CopyPixelsFrom(bytes);
        return raster;
    }

    private static void AssertPixelsEqual<T>(Raster<T> expected, Raster<T> actual)
        where T : unmanaged, IPixelType<T>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.Equal(expected.Row(y).ToArray(), actual.Row(y).ToArray());
        }
    }

    private static List<(string Type, int Length)> Chunks(byte[] file)
    {
        List<(string, int)> chunks = [];
        for (int i = 8; i + 8 <= file.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(i));
            chunks.Add((Encoding.ASCII.GetString(file, i + 4, 4), length));
            Assert.Equal(Crc32.Compute(file.AsSpan(i + 4, 4 + length)), BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(i + 8 + length)));
            i += 12 + length;
        }

        return chunks;
    }

    private static byte[] BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
