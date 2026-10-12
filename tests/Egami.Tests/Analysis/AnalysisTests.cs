using Egami.Analysis;
using Egami.Formats.Jpeg;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Analysis;

/// <summary>Phase 2.5.5 stage A: the metric math, its properties, colors, flags, histograms, signatures and hash robustness.</summary>
public class AnalysisTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    private static Raster<Rgba8> Noise(int width, int height, int seed, bool alpha = false)
    {
        Random random = new(seed);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), alpha ? (byte)random.Next(256) : (byte)255);
            }
        }

        return raster;
    }

    [Fact]
    public void ComparingAnImageWithItselfIsIdentical()
    {
        using Raster<Rgba8> a = Noise(37, 29, 1, alpha: true);
        CompareResult result = Raster.Compare(a, a, new CompareOptions { Ssim = true });
        Assert.True(result.IsIdentical);
        Assert.Equal(0, result.Overall.Mse);
        Assert.Equal(double.PositiveInfinity, result.Overall.Psnr);
        Assert.Equal(0, result.DifferentPixels);
        Assert.Equal(1, result.Overall.Ssim, 12);
        Assert.Equal(37 * 29, result.PixelCount);
    }

    [Fact]
    public void HandComputedMetricsOnFourByFour()
    {
        using Raster<Gray8> a = Raster.Create<Gray8>(4, 4, new Gray8(100));
        using Raster<Gray8> b = Raster.Create<Gray8>(4, 4, new Gray8(100));
        b.SetPixel(0, 0, new Gray8(110));
        b.SetPixel(3, 3, new Gray8(80));
        CompareResult result = Raster.Compare(a, b);
        double d1 = 10 / 255.0, d2 = 20 / 255.0;
        Assert.Equal((d1 + d2) / 16, result.Red.Mae, 15);
        Assert.Equal(((d1 * d1) + (d2 * d2)) / 16, result.Red.Mse, 15);
        Assert.Equal(d2, result.Red.PeakError, 15);
        Assert.Equal(2, result.DifferentPixels);
        Assert.Equal(1, Raster.Compare(a, b, new CompareOptions { Fuzz = 15 / 255f }).DifferentPixels);
        Assert.Equal(result.Red.Mse, result.Overall.Mse, 15);
        Assert.Equal(0, result.Alpha.Mse);
    }

    [Fact]
    public void CompareIsSymmetricAndRmseIsRootOfMse()
    {
        using Raster<Rgba8> a = Noise(64, 48, 2, alpha: true);
        using Raster<Rgba8> b = Noise(64, 48, 3, alpha: true);
        CompareResult ab = Raster.Compare(a, b, new CompareOptions { Ssim = true });
        CompareResult ba = Raster.Compare(b, a, new CompareOptions { Ssim = true });
        Assert.Equal(ab.Overall.Mse, ba.Overall.Mse, 15);
        Assert.Equal(ab.Overall.Mae, ba.Overall.Mae, 15);
        Assert.Equal(ab.Overall.Ssim, ba.Overall.Ssim, 12);
        Assert.Equal(ab.DifferentPixels, ba.DifferentPixels);
        foreach (ChannelDifference c in new[] { ab.Red, ab.Green, ab.Blue, ab.Alpha, ab.Overall })
        {
            Assert.Equal(c.Mse, c.Rmse * c.Rmse, 12);
            Assert.Equal((1 - c.Ssim) / 2, c.Dssim, 15);
        }
    }

    [Fact]
    public void DifferentPixelTypesOfOneImageAreIdentical()
    {
        using Raster<Rgba8> a = Noise(40, 30, 4);
        using Raster<Rgb8> rgb = a.As<Rgb8>();
        using Raster<Rgba16> wide = a.As<Rgba16>();
        Assert.True(Raster.Compare(a, rgb).IsIdentical);
        Assert.True(Raster.Compare(wide, rgb).IsIdentical);
    }

    [Fact]
    public void HiddenColorsUnderTransparencyCountOnlyWhenStraight()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(8, 8, new Rgba8(10, 20, 30, 0));
        using Raster<Rgba8> b = Raster.Create<Rgba8>(8, 8, new Rgba8(200, 100, 0, 0));
        Assert.True(Raster.Compare(a, b).IsIdentical);
        CompareResult straight = Raster.Compare(a, b, new CompareOptions { Alpha = AlphaCompare.Straight });
        Assert.False(straight.IsIdentical);
        Assert.Equal(64, straight.DifferentPixels);
    }

    [Fact]
    public void RegionAndFrameAreCompared()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(20, 10, Rgba8.White);
        using Raster<Rgba8> b = Raster.Create<Rgba8>(20, 10, Rgba8.White);
        b.SetPixel(15, 5, Rgba8.Black);
        Assert.True(Raster.Compare(a, b, new CompareOptions { Region = new Rect(0, 0, 10, 10) }).IsIdentical);
        CompareResult right = Raster.Compare(a, b, new CompareOptions { Region = new Rect(10, 0, 10, 10) });
        Assert.Equal(1, right.DifferentPixels);
        Assert.Equal(100, right.PixelCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Compare(a, b, new CompareOptions { Region = new Rect(15, 0, 10, 10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Compare(a, b, new CompareOptions { Frame = 1 }));
    }

    [Fact]
    public void DifferentSizesThrowNamingBoth()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(4, 4);
        using Raster<Rgba8> b = Raster.Create<Rgba8>(5, 4);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Raster.Compare(a, b));
        Assert.Contains("4×4", e.Message);
        Assert.Contains("5×4", e.Message);
    }

    [Fact]
    public void OneThreadGivesTheSameNumbersAsAllCores()
    {
        EgamiContext one = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster duck = Raster.Load(Path.Combine(Images, "analysis", "duck.png"));
        using Raster q75 = Raster.Load(Path.Combine(Images, "analysis", "duck-q75.png"));
        using Raster duck1 = Raster.Load(Path.Combine(Images, "analysis", "duck.png"), one);
        CompareResult all = Raster.Compare(duck, q75, new CompareOptions { Ssim = true });
        CompareResult single = Raster.Compare(duck1, q75, new CompareOptions { Ssim = true });
        Assert.Equal(all.Overall, single.Overall);
        Assert.Equal(all.Red, single.Red);
        RasterStatistics s1 = duck.Statistics(), s2 = duck1.Statistics();
        Assert.Equal(s1.Red, s2.Red);
        Assert.Equal(s1.Luma, s2.Luma);
        Assert.Equal(duck.CountColors(), duck1.CountColors());
    }

    [Fact]
    public void AbsoluteDifferenceShowsChannelGaps()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(2, 1, new Rgba8(100, 100, 100, 255));
        using Raster<Rgba8> b = Raster.Create<Rgba8>(2, 1, new Rgba8(100, 100, 100, 255));
        b.SetPixel(1, 0, new Rgba8(110, 90, 100, 255));
        using Raster difference = Raster.Difference(a, b, DifferenceStyle.Absolute, new DifferenceOptions { Amplify = 2 });
        Raster<Rgba8> typed = Assert.IsType<Raster<Rgba8>>(difference);
        Assert.Equal(new Rgba8(0, 0, 0, 255), typed.GetPixel(0, 0));
        Assert.Equal(new Rgba8(20, 20, 0, 255), typed.GetPixel(1, 0));
    }

    [Fact]
    public void HistogramCountsEightBitValuesExactly()
    {
        using Raster<Gray8> ramp = Raster.Create<Gray8>(256, 2);
        for (int x = 0; x < 256; x++)
        {
            ramp.SetPixel(x, 0, new Gray8((byte)x));
            ramp.SetPixel(x, 1, new Gray8((byte)x));
        }

        Histogram h = ramp.Histogram(ColorChannel.Red);
        Assert.Equal(256, h.Bins);
        Assert.Equal(512, h.Total);
        Assert.All(h.Counts, c => Assert.Equal(2, c));
        Assert.Equal(127 / 255.0, h.Median, 12);
        Assert.Equal(0, h.Percentile(0));
        Assert.Equal(1, h.Percentile(1));
        Histogram four = ramp.Histogram(ColorChannel.Luminance, 4);
        Assert.Equal([128L, 128, 128, 128], four.Counts);
        Assert.Throws<ArgumentOutOfRangeException>(() => ramp.Histogram(ColorChannel.Red, 1));
    }

    [Fact]
    public void StatisticsOfAConstantImage()
    {
        using Raster<Rgb8> flat = Raster.Create<Rgb8>(10, 10, new Rgb8(51, 102, 153));
        RasterStatistics s = flat.Statistics();
        Assert.Equal(0.2, s.Red.Mean, 12);
        Assert.Equal(0.6, s.Blue.Max, 12);
        Assert.Equal(0, s.Green.StdDev);
        Assert.Equal(0, s.Green.Skewness);
        Assert.Equal(0, s.Green.Entropy);
        Assert.Equal(1, s.Alpha.Mean);
        Assert.Equal((0.2126 * 0.2) + (0.7152 * 0.4) + (0.0722 * 0.6), s.Luma.Mean, 12);
        Assert.Equal(100, s.PixelCount);
    }

    [Fact]
    public void CountColorsForEveryKeyWidth()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(16, 16, Rgba8.White);
        a.SetPixel(1, 1, Rgba8.Black);
        a.SetPixel(2, 2, new Rgba8(255, 255, 255, 254));
        Assert.Equal(3, a.CountColors());
        using Raster<Rgb8> rgb = a.As<Rgb8>();
        Assert.Equal(2, rgb.CountColors());
        using Raster<Gray8> gray = a.As<Gray8>();
        Assert.Equal(2, gray.CountColors());
        using Raster<Rgba16> wide = a.As<Rgba16>();
        Assert.Equal(3, wide.CountColors());
        using Raster<RgbaF32> floats = a.As<RgbaF32>();
        Assert.Equal(3, floats.CountColors());
        using Raster<Rgba8> noise = Noise(300, 200, 5);
        Assert.Equal(noise.GetColors(1_000_000)!.Count, noise.CountColors());
    }

    [Fact]
    public void GetColorsStopsAtTheLimit()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(4, 4, Rgba8.White);
        a.SetPixel(0, 0, Rgba8.Black);
        IReadOnlyList<Rgba16>? colors = a.GetColors(2);
        Assert.NotNull(colors);
        Assert.Equal([new Rgba16(0, 0, 0, 65535), new Rgba16(65535, 65535, 65535, 65535)], colors);
        Assert.Null(a.GetColors(1));
    }

    [Fact]
    public void IsOpaqueAndIsGray()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(50, 40, new Rgba8(90, 90, 90, 255));
        Assert.True(a.IsOpaque());
        Assert.True(a.IsGray());
        a.SetPixel(49, 39, new Rgba8(90, 92, 90, 254));
        Assert.False(a.IsOpaque());
        Assert.False(a.IsGray());
        Assert.True(a.IsGray(2 / 255f));
        using Raster<Rgb8> rgb = Raster.Create<Rgb8>(3, 3);
        Assert.True(rgb.IsOpaque());
        using Raster<Gray8> gray = Raster.Create<Gray8>(3, 3);
        Assert.True(gray.IsGray());
        Assert.Throws<ArgumentOutOfRangeException>(() => a.IsOpaque(1));
    }

    [Fact]
    public void SignatureFollowsThePixels()
    {
        using Raster<Rgba8> a = Noise(33, 21, 6, alpha: true);
        using Raster<Rgba16> wide = a.As<Rgba16>();
        string signature = a.Signature();
        Assert.Matches("^[0-9a-f]{64}$", signature);
        Assert.Equal(signature, wide.Signature());
        Assert.Equal(signature, a.Signature());
        Rgba8 p = a.GetPixel(5, 5);
        a.SetPixel(5, 5, new Rgba8((byte)(p.R ^ 1), p.G, p.B, p.A));
        Assert.NotEqual(signature, a.Signature());
    }

    [Theory]
    [InlineData("duck.png")]
    [InlineData("egret.png")]
    public void HashesSurviveRecompressionResizeAndBrightness(string name)
    {
        using Raster image = Raster.Load(Path.Combine(Images, "analysis", name));
        ulong dct = PerceptualHash.Dct(image), dhash = PerceptualHash.Difference(image);

        using MemoryStream jpeg = new();
        image.Save(jpeg, "jpg", new JpegWriterOptions { Quality = 75 });
        jpeg.Position = 0;
        using Raster recompressed = Raster.Load(jpeg);
        using Raster half = image.Derive(o => o.Resize(image.Width / 2, 0), TestContext.Current.CancellationToken);
        using Raster brighter = image.Derive(o => o.Brightness(1.03f), TestContext.Current.CancellationToken);
        foreach (Raster changed in new[] { recompressed, half, brighter })
        {
            Assert.InRange(PerceptualHash.Distance(dct, PerceptualHash.Dct(changed)), 0, 6);
            Assert.InRange(PerceptualHash.Distance(dhash, PerceptualHash.Difference(changed)), 0, 6);
        }
    }

    [Fact]
    public void UnrelatedImagesAreFarApart()
    {
        string[] files = [.. Directory.EnumerateFiles(Images, "*.png", SearchOption.AllDirectories)
            .Where(f => !f.Contains("analysis") && !f.Contains("fuzz") && new FileInfo(f).Length > 20_000)
            .Order(StringComparer.Ordinal)
            .Take(48)];
        List<ulong> hashes = [];
        foreach (string file in files)
        {
            try
            {
                using Raster image = Raster.Load(file);
                hashes.Add(PerceptualHash.Dct(image));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Damaged and unsupported files in the corpus are not this test's business.
            }
        }

        // Some corpus files are crops, tiles or copies of one picture; the average over every pair still has to be far.
        List<int> distances = [];
        for (int i = 0; i < hashes.Count; i++)
        {
            for (int j = i + 1; j < hashes.Count; j++)
            {
                distances.Add(PerceptualHash.Distance(hashes[i], hashes[j]));
            }
        }

        Assert.True(distances.Count >= 300, $"Only {distances.Count} pairs.");
        Assert.True(distances.Average() >= 24, $"Unrelated pairs average {distances.Average():F1} bits apart.");
    }
}
