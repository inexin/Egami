using Egami.Formats.Avif;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>Phase 2.1.6: 4:1:1 and 4:1:0 planes wrap, resize, convert and save like the other chroma formats.</summary>
public class YuvQuarterChromaTests
{
    [Fact]
    public void WrapTakesQuarterWidthPlanes()
    {
        // 18 × 9 at 4:1:0: chroma is 5 × 3, here with rows padded to 8 samples.
        byte[] y = new byte[18 * 9], u = new byte[(2 * 8) + 5], v = new byte[(2 * 8) + 5];
        using YuvRaster<byte> yuv = YuvRaster.Wrap<byte>(y, 0, u, 8, v, 8, 18, 9, ChromaFormat.Yuv410, 8, YuvColor.Jpeg);
        Assert.Equal((5, 3), (yuv.U!.Width, yuv.U.Height));
        yuv.V!.Row(2)[4] = 9;
        Assert.Equal(9, v[(2 * 8) + 4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<byte>(y, 0, u, 0, v.AsMemory(0, 14), 0, 18, 9, ChromaFormat.Yuv410, 8, YuvColor.Jpeg));
        using YuvRaster<byte> dv = YuvRaster.Wrap<byte>(y, 0, new byte[5 * 9], 0, new byte[5 * 9], 0, 18, 9, ChromaFormat.Yuv411, 8, YuvColor.Bt601Limited);
        Assert.Equal((5, 9), (dv.U!.Width, dv.U.Height));
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv411, 31, 77)]
    [InlineData(ChromaFormat.Yuv410, 31, 20)]
    public void ResizeKeepsTheFormat(ChromaFormat format, int chromaWidth, int chromaHeight)
    {
        using YuvRaster<ushort> yuv = YuvRaster.Create(97, 61, format, 10, YuvColor.Bt709Limited);
        YuvConversionTests.Fill(yuv, new Random(5));
        using YuvRaster<ushort> large = yuv.Resize(123, 77);
        Assert.Equal(format, large.ChromaFormat);
        Assert.Equal((chromaWidth, chromaHeight), (large.U!.Width, large.U.Height));
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv411, ChromaSiting.Left)]
    [InlineData(ChromaFormat.Yuv410, ChromaSiting.Center)]
    [InlineData(ChromaFormat.Yuv410, ChromaSiting.TopLeft)]
    public void PhotosRoundTripClosely(ChromaFormat format, ChromaSiting siting)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-444.jpg"));
        using YuvRaster<byte> yuv = YuvRaster.FromRaster(photo, format, new YuvColor(YuvMatrix.Bt601, YuvRange.Limited, siting));
        using Raster<Rgb8> back = yuv.ToRaster<Rgb8>();
        Assert.True(MeanError(photo, back) < 6, $"Mean error {MeanError(photo, back)}.");
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv411, ChromaFormat.Yuv422)]
    [InlineData(ChromaFormat.Yuv410, ChromaFormat.Yuv420)]
    public void JpegGetsTheNearestFormatItStores(ChromaFormat format, ChromaFormat written)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-444.jpg"));
        using YuvRaster<byte> yuv = YuvRaster.FromRaster(photo, format, YuvColor.Jpeg);
        using MemoryStream output = new();
        yuv.Save(output, JpegFormat.Info, new JpegWriterOptions { Quality = 95 });
        output.Position = 0;
        using YuvRaster loaded = YuvRaster.Load(output);
        Assert.Equal(written, loaded.ChromaFormat);
        using Raster<Rgb8> expected = yuv.ToRaster<Rgb8>();
        using Raster<Rgb8> back = loaded.ToRaster<Rgb8>();
        Assert.True(MeanError(expected, back) < 4, $"Mean error {MeanError(expected, back)}.");
    }

    [Fact]
    public void AvifIsSavedFromRgb()
    {
        using YuvRaster<byte> yuv = YuvRaster.Create(40, 24, ChromaFormat.Yuv411, YuvColor.Bt601Limited);
        YuvConversionTests.Fill(yuv, new Random(2));
        using MemoryStream output = new();
        yuv.Save(output, AvifFormat.Info);
        output.Position = 0;
        using YuvRaster loaded = YuvRaster.Load(output);
        Assert.Equal((40, 24), (loaded.Width, loaded.Height));
        Assert.NotEqual(ChromaFormat.Yuv411, loaded.ChromaFormat);

        // AV1 has no 4:1:1, so the option chooses as if unset.
        using Raster<Rgb8> rgb = yuv.ToRaster<Rgb8>();
        using MemoryStream chosen = new();
        rgb.Save(chosen, AvifFormat.Info, new AvifWriterOptions { Chroma = ChromaFormat.Yuv411 });
        chosen.Position = 0;
        using YuvRaster reloaded = YuvRaster.Load(chosen);
        Assert.Equal(ChromaFormat.Yuv420, reloaded.ChromaFormat);
    }

    private static double MeanError(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        double error = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgb8 p = a.Row(y)[x], q = b.Row(y)[x];
                error += (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B)) / 3.0;
            }
        }

        return error / (a.Width * a.Height);
    }
}
