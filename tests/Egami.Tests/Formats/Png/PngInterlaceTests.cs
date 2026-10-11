using Egami.Formats;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Png;

/// <summary>Phase 2.4.1 task 4: PngWriterOptions.Interlace writes Adam7 for every form, size, palette, APNG and streamed encode.</summary>
public class PngInterlaceTests
{
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    [Theory]
    [MemberData(nameof(PngWriterTests.AllForms), MemberType = typeof(PngWriterTests))]
    public void EveryColorTypeAndBitDepthRoundTrips(PngColorType colorType, int depth)
    {
        PngBuilder source = PngReaderTests.Build(colorType, depth, false, colorType == PngColorType.Palette, 29, 17, seed: depth);
        using Raster<Rgba16> original = Raster.Load<Rgba16>(source.Build());
        foreach (PngFilterMode filter in Enum.GetValues<PngFilterMode>())
        {
            byte[] file = PngWriterTests.Save(original, new PngWriterOptions { ColorType = colorType, BitDepth = depth, Filter = filter, Interlace = true });
            Assert.Equal(1, file[28]);
            using Raster<Rgba16> loaded = Raster.Load<Rgba16>(file, options: Strict);
            Assert.True(((PngDetails)loaded.Metadata.FormatDetails!).Interlaced);
            AssertSame(original, loaded);
        }
    }

    /// <summary>Sizes where some passes are empty (fewer than 5 columns or rows) or end mid-block.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(5, 3)]
    [InlineData(8, 8)]
    [InlineData(33, 17)]
    public void EverySizeRoundTrips(int width, int height)
    {
        foreach ((PngColorType? colorType, int? depth, PngFilterMode filter) in new (PngColorType?, int?, PngFilterMode)[]
        {
            (null, null, PngFilterMode.Adaptive),
            (PngColorType.Gray, 1, PngFilterMode.Adaptive),
            (PngColorType.Rgb, 16, PngFilterMode.Paeth),
        })
        {
            PngWriterOptions plain = new() { ColorType = colorType, BitDepth = depth, Filter = filter };
            PngWriterOptions interlaced = new() { ColorType = colorType, BitDepth = depth, Filter = filter, Interlace = true };
            using Raster<Rgba16> noise = Noise<Rgba16>(width, height, seed: (width * 100) + height);
            using Raster<Rgba16> original = Raster.Load<Rgba16>(PngWriterTests.Save(noise, plain));
            using Raster<Rgba16> loaded = Raster.Load<Rgba16>(PngWriterTests.Save(original, interlaced), options: Strict);
            AssertSame(original, loaded);
        }
    }

    [Fact]
    public void InterlacedFileHoldsTheSamePixelsAsAPlainOne()
    {
        using Raster<Rgba8> original = Noise<Rgba8>(61, 47, seed: 3);
        byte[] plain = PngWriterTests.Save(original);
        byte[] interlaced = PngWriterTests.Save(original, new PngWriterOptions { Interlace = true });
        Assert.NotEqual(plain, interlaced);
        Assert.Equal(0, plain[28]);
        using Raster<Rgba8> a = Raster.Load<Rgba8>(plain, options: Strict);
        using Raster<Rgba8> b = Raster.Load<Rgba8>(interlaced, options: Strict);
        AssertSame(a, b);
    }

    [Fact]
    public void InterlacedSourceIsWrittenPlainUnlessAsked()
    {
        using Raster<Rgba8> original = Noise<Rgba8>(20, 20, seed: 4);
        using Raster loaded = Raster.Load(PngWriterTests.Save(original, new PngWriterOptions { Interlace = true }));
        Assert.Equal(0, PngWriterTests.Save(loaded)[28]);
    }

    [Fact]
    public void ReducedAndQuantizedPalettesInterlace()
    {
        using Raster<Rgba8> few = Raster.Create(30, 20, Rgba8.Blue);
        few.Row(7).Slice(3, 11).Fill(Rgba8.Red);
        few.Row(12)[29] = new Rgba8(1, 2, 3, 100);
        byte[] reduced = PngWriterTests.Save(few, new PngWriterOptions { Reduce = true, Interlace = true });
        Assert.Equal((byte)PngColorType.Palette, reduced[25]);
        using Raster<Rgba8> reducedBack = Raster.Load<Rgba8>(reduced, options: Strict);
        AssertSame(few, reducedBack);

        using Raster<Rgba8> many = Noise<Rgba8>(40, 30, seed: 5);
        using Raster<Rgba8> plain = Raster.Load<Rgba8>(PngWriterTests.Save(many, new PngWriterOptions { ColorType = PngColorType.Palette }));
        using Raster<Rgba8> interlaced = Raster.Load<Rgba8>(PngWriterTests.Save(many, new PngWriterOptions { ColorType = PngColorType.Palette, Interlace = true }), options: Strict);
        AssertSame(plain, interlaced);
    }

    [Fact]
    public void ApngFramesInterlace()
    {
        using Raster<Rgba8> anim = Raster.Create(40, 30, Rgba8.Blue);
        Frame<Rgba8> second = anim.Frames.Add(anim.Frames[0]);
        second.Row(10).Slice(5, 13).Fill(Rgba8.Red);
        Frame<Rgba8> third = anim.Frames.Add(second);
        third.Row(20)[30] = new Rgba8(1, 2, 3, 128);
        byte[] file = PngWriterTests.Save(anim, new PngWriterOptions { Interlace = true });
        Assert.Equal(1, file[28]);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(file, options: Strict);
        Assert.Equal(3, again.FrameCount);
        for (int i = 0; i < 3; i++)
        {
            for (int y = 0; y < anim.Height; y++)
            {
                Assert.True(anim.Frames[i].Row(y).SequenceEqual(again.Frames[i].Row(y)), $"frame {i} row {y}");
            }
        }
    }

    [Fact]
    public void StreamedEncodeWritesWhatSaveWrites()
    {
        using Raster<Rgba8> original = Noise<Rgba8>(45, 38, seed: 6);
        PngWriterOptions options = new() { Interlace = true };
        byte[] saved = PngWriterTests.Save(original, options);
        using MemoryStream streamed = new();
        using (RasterEncoder<Rgba8> encoder = RasterEncoder.Create<Rgba8>(streamed, PngFormat.Info, original.Width, original.Height, options))
        {
            encoder.WriteRows(original);
            encoder.Complete();
        }

        Assert.Equal(saved, streamed.ToArray());
    }

    private static Raster<T> Noise<T>(int width, int height, int seed)
        where T : unmanaged, IPixelType<T>
    {
        Random random = new(seed);
        Raster<T> raster = Raster.Create<T>(width, height);
        byte[] bytes = new byte[width * height * T.Layout.BytesPerPixel];
        random.NextBytes(bytes);
        raster.CopyPixelsFrom(bytes);
        return raster;
    }

    private static void AssertSame<T>(Raster<T> expected, Raster<T> actual)
        where T : unmanaged, IPixelType<T>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"row {y}");
        }
    }
}
