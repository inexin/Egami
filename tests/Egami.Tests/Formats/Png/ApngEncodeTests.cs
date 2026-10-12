using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;
using Egami.Tests.Formats.Gif;

namespace Egami.Tests.Formats.Png;

public class ApngEncodeTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files() => [.. AnimCorpus.Files("apng").Concat(AnimCorpus.Files("gif"))];

    /// <summary>Every corpus animation (APNG and GIF) written as APNG and read back gives the same frames, delays and plays.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void RoundTripsAreExact(string file)
    {
        using Raster source = Raster.Load(AnimCorpus.Read(file));
        foreach (PngWriterOptions options in new[] { PngWriterOptions.Default, new PngWriterOptions { Reduce = true }, new PngWriterOptions { CompressionLevel = 1 } })
        {
            byte[] written = Save(source, options);
            using Raster again = Raster.Load(written);
            Assert.Equal(source.FrameCount, again.FrameCount);
            Assert.Empty(again.Metadata.Warnings);
            using Raster<Rgba16> a = source.As<Rgba16>();
            using Raster<Rgba16> b = again.As<Rgba16>();
            for (int i = 0; i < a.FrameCount; i++)
            {
                for (int y = 0; y < a.Height; y++)
                {
                    Assert.True(a.Frames[i].Row(y).SequenceEqual(b.Frames[i].Row(y)), $"frame {i} row {y}");
                }

                Assert.Equal(a.Frames[i].Metadata.Delay, b.Frames[i].Metadata.Delay);
            }

            if (source.FrameCount > 1)
            {
                Assert.Equal(source.Metadata.LoopCount, again.Metadata.LoopCount);
                Assert.True(((PngDetails)again.Metadata.FormatDetails!).DefaultImageIsFrame);
            }
        }
    }

    [Fact]
    public void LaterFramesStoreOnlyWhatChanged()
    {
        using Raster<Rgba8> anim = Raster.Create(40, 30, Rgba8.Blue);
        Frame<Rgba8> second = anim.Frames.Add(anim.Frames[0]);
        second.Row(10).Slice(5, 3).Fill(Rgba8.Red);
        Frame<Rgba8> third = anim.Frames.Add(second);
        third.Row(20)[30] = new Rgba8(1, 2, 3, 128);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(anim, PngWriterOptions.Default));
        Assert.Equal(new Rect(5, 10, 3, 1), again.Frames[1].Metadata.Bounds);
        Assert.Equal(new Rect(30, 20, 1, 1), again.Frames[2].Metadata.Bounds);
        Assert.Equal(new Rgba8(1, 2, 3, 128), again.Frames[2].Row(20)[30]);
        Assert.Equal(Rgba8.Red, again.Frames[2].Row(10)[6]);
    }

    [Fact]
    public void OpaqueChangesAreDrawnOverWithTransparentGaps()
    {
        using Raster<Rgba8> anim = Raster.Create(20, 20, Rgba8.Blue);
        Frame<Rgba8> second = anim.Frames.Add(anim.Frames[0]);
        second.Row(2)[2] = Rgba8.Red;
        second.Row(17)[17] = Rgba8.Red;
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(anim, PngWriterOptions.Default));
        Assert.Equal(FrameBlend.Over, again.Frames[1].Metadata.Blend);
        Assert.Equal(Rgba8.Blue, again.Frames[1].Row(10)[10]);
        Assert.Equal(Rgba8.Red, again.Frames[1].Row(17)[17]);
    }

    [Fact]
    public void FirstFrameOnlyWritesAStill()
    {
        using Raster source = Raster.Load(AnimCorpus.Read("apng/pan-rgb.png"));
        using Raster again = Raster.Load(Save(source, new PngWriterOptions { FirstFrameOnly = true }));
        Assert.Equal(1, again.FrameCount);
        Assert.False(((PngDetails)again.Metadata.FormatDetails!).Animated);
    }

    [Theory]
    [InlineData(1000.0 / 30, 1, 30)]
    [InlineData(100, 1, 10)]
    [InlineData(70, 7, 100)]
    [InlineData(0, 0, 100)]
    [InlineData(40, 1, 25)]
    public void DelaysAreTheSimplestFraction(double milliseconds, int numerator, int denominator)
    {
        TimeSpan delay = TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));
        Assert.Equal(((ushort)numerator, (ushort)denominator), PngWriter.DelayFraction(delay));
    }

    [Fact]
    public void PalettePngOfAPhotoIsQuantized()
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(AnimCorpus.Images, "ops", "egret.png"));
        foreach (DitherMode dither in new[] { DitherMode.None, DitherMode.FloydSteinberg })
        {
            byte[] written = Save(photo, new PngWriterOptions { ColorType = PngColorType.Palette, Palette = new PaletteOptions { Dither = dither } });
            using Raster<Rgb8> again = Raster.Load<Rgb8>(written);
            Assert.Equal(PngColorType.Palette, ((PngDetails)again.Metadata.FormatDetails!).ColorType);
            double psnr = Psnr(photo, again);
            output.WriteLine($"{dither}: {written.Length:N0} bytes, {psnr:F2} dB");
            Assert.True(psnr >= 34, $"{psnr:F2} dB");
        }
    }

    [Fact]
    public void PalettePngKeepsPartialAlpha()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(64, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                image.Row(y)[x] = new Rgba8((byte)(x * 4), (byte)(y * 4), 128, (byte)(x < 32 ? 255 : 100));
            }
        }

        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(image, new PngWriterOptions { ColorType = PngColorType.Palette, Palette = new PaletteOptions { MaxColors = 64 } }));
        Assert.InRange(again.GetPixel(50, 10).A, 90, 110);
        Assert.Equal(255, again.GetPixel(5, 10).A);
        Assert.True(((PngDetails)again.Metadata.FormatDetails!).Palette.Length <= 64);
    }

    private static byte[] Save(Raster raster, PngWriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, PngFormat.Info, options);
        return stream.ToArray();
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b) => Raster.Compare(a, b).Overall.Psnr;
}
