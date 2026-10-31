using Egami.Analysis;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// JPEG XL extras PR C: lossy efforts 8 to 10, which refine the quantization field by Butteraugli (libjxl's
/// FindBestQuantization), spending bits where the distance asked for is exceeded and saving them elsewhere: on the egret
/// photo cjxl's files come out about 6 % smaller than at effort 7 for a Butteraugli distance a few percent higher.
/// </summary>
public class JxlLossyHighEffortTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void ButteraugliRoundsTradeBitsForTheDistance(int effort)
    {
        using Raster<Rgb8> image = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        (double e7, long size7) = Distance(image, 7, 1);
        (double high, long size) = Distance(image, effort, 1);
        Assert.InRange(size, size7 * 85 / 100, size7 * 98 / 100);
        Assert.InRange(high, e7 * 0.8, e7 * 1.2);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void HalfSizeFramesComeBackAtFullSize(int effort)
    {
        using Raster<Rgb8> image = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        using MemoryStream file = new();
        image.Save(file, JxlFormat.Info, new JxlWriterOptions { Distance = 12, Effort = effort });
        file.Position = 0;
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
        Assert.Equal((image.Width, image.Height), (back.Width, back.Height));
        double distance = Raster.Compare(image, back, new CompareOptions { Butteraugli = true }).Butteraugli;
        Assert.InRange(distance, 1, 40);
    }

    [Fact]
    public void ExactAlphaSurvivesLz77()
    {
        // Found by fuzzing: from effort 8 LZ77 rewrites the alpha channel's tokens, which must be the ones written.
        using Raster<Rgba8> image = Raster.Create<Rgba8>(300, 260);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int v = ((x * 7) + (y * 13)) % 29;
                image.Row(y)[x] = new Rgba8((byte)(v * 8), (byte)(255 - (v * 8)), (byte)(x & 0xFF), (byte)(v < 10 ? 0 : v * 8));
            }
        }

        using MemoryStream file = new();
        image.Save(file, JxlFormat.Info, new JxlWriterOptions { Distance = 0.35f, Effort = 8 });
        file.Position = 0;
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal(image.Row(y)[x].A, back.Row(y)[x].A);
            }
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void ProgressiveFramesDecode(int effort)
    {
        // Found by fuzzing: the encoder's own draft of a progressive frame must be coded in three passes too.
        using Raster<Rgba8> image = Raster.Load<Rgba8>(Path.Combine(Images, "ops", "egret.png"));
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                image.Row(y)[x].A = (byte)(128 + ((x + y) % 128));
            }
        }

        using MemoryStream file = new();
        image.Save(file, JxlFormat.Info, new JxlWriterOptions { Distance = 2.5f, Effort = effort, Progressive = true });
        file.Position = 0;
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file);
        Assert.InRange(Raster.Compare(image, back, new CompareOptions { Butteraugli = true }).Butteraugli, 0.5, 8);
    }

    [Fact]
    public void LargeFramesAreCodedWhole()
    {
        // Above distance 0.5 effort 8 codes the frame whole, as libjxl's streaming rules: chunks would not be compared.
        Assert.False(JxlChunkedFrame.Applies(4000, 3000, new JxlLossySettings { Effort = 8, Distance = 1 }, false));
        Assert.True(JxlChunkedFrame.Applies(4000, 3000, new JxlLossySettings { Effort = 8, Distance = 0.5f }, false));
        Assert.False(JxlChunkedFrame.Applies(4000, 3000, new JxlLossySettings { Effort = 10, Distance = 0.5f }, false));
        Assert.True(JxlChunkedFrame.Applies(4000, 3000, new JxlLossySettings { Effort = 7, Distance = 1 }, false));
    }

    private static (double Distance, long Size) Distance(Raster<Rgb8> image, int effort, float distance)
    {
        using MemoryStream file = new();
        image.Save(file, JxlFormat.Info, new JxlWriterOptions { Distance = distance, Effort = effort });
        long size = file.Length;
        file.Position = 0;
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
        return (Raster.Compare(image, back, new CompareOptions { Butteraugli = true }).Butteraugli, size);
    }
}
