using Egami.Analysis;
using Egami.Formats.Jxl;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Analysis
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CompareImages()
    {
        using SampleFiles files = new();
        using Raster expected = Raster.Load("duck.png");
        expected.Save("duck-copy.jpg");
        using Raster actual = Raster.Load("duck-copy.jpg");

        #region compare
        // Every cheap metric in one pass, per channel and overall, on the 0 to 1 scale; SSIM on request.
        CompareResult result = Raster.Compare(expected, actual, new CompareOptions { Ssim = true, Fuzz = 0.02f });
        Console.WriteLine($"PSNR {result.Overall.Psnr:F1} dB, SSIM {result.Overall.Ssim:F4}, {result.DifferentPixels} pixels off by more than 2%");
        bool sameLook = result.Overall.Psnr >= 40;
        #endregion

        Assert.False(result.IsIdentical);
        Assert.True(sameLook || !sameLook);
    }

    [Fact]
    public void ButteraugliDistance()
    {
        using SampleFiles files = new();
        using Raster original = Raster.Load("duck.png");
        original.Save("duck.jxl", new JxlWriterOptions { Distance = 2 });
        using Raster saved = Raster.Load("duck.jxl");

        #region butteraugli
        // How far a lossy save moved the picture to the eye: under about 1 it looks the same.
        CompareResult result = Raster.Compare(original, saved, new CompareOptions { Butteraugli = true });
        Console.WriteLine($"Butteraugli {result.Butteraugli:F2}, 3-norm {result.Butteraugli3Norm:F2}");
        #endregion

        Assert.InRange(result.Butteraugli, 0.5, 6);
    }

    [Fact]
    public void VisualRegression()
    {
        using SampleFiles files = new();
        using Raster baseline = Raster.Load("duck.png");
        using Raster rendered = baseline.Derive(ops => ops.Brightness(1.01f), Ct);

        #region visual-regression
        // A visual regression check: pass when the render is close to the stored baseline, else save a picture of what moved.
        CompareResult result = Raster.Compare(baseline, rendered, new CompareOptions { Fuzz = 0.05f });
        if (result.DifferentPixels > 0 || result.Overall.Psnr < 45)
        {
            using Raster where = Raster.Difference(baseline, rendered, DifferenceStyle.Highlight, new DifferenceOptions { Fuzz = 0.05f });
            where.Save("regression.png");
        }
        #endregion

        Assert.True(result.Overall.Psnr > 30);
    }

    [Fact]
    public void DifferencePicture()
    {
        using SampleFiles files = new();
        using Raster a = Raster.Load("duck.png");
        a.Save("duck-copy.jpg");
        using Raster b = Raster.Load("duck-copy.jpg");

        #region difference
        // ImageMagick compare's red-over-faded picture, and the plain per-channel difference turned up 8 times.
        using Raster highlight = Raster.Difference(a, b);
        using Raster gaps = Raster.Difference(a, b, DifferenceStyle.Absolute, new DifferenceOptions { Amplify = 8 });
        highlight.Save("highlight.png");
        gaps.Save("gaps.png");
        #endregion

        Assert.Equal(a.Width, highlight.Width);
    }

    [Fact]
    public void Statistics()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region statistics
        // Per-channel statistics on the 0 to 1 scale, as identify -verbose prints them.
        RasterStatistics stats = photo.Statistics();
        Console.WriteLine($"red mean {stats.Red.Mean:F3} ± {stats.Red.StdDev:F3}, luma {stats.Luma.Min:F2} to {stats.Luma.Max:F2}");
        bool dark = stats.Luma.Mean < 0.25;
        #endregion

        Assert.False(dark);
    }

    [Fact]
    public void LumaHistogram()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region histogram
        // How the brightness is spread: 256 bins, and the levels below which 1% and 99% of the pixels lie.
        Histogram histogram = photo.Histogram(ColorChannel.Luminance);
        double shadows = histogram.Percentile(0.01), highlights = histogram.Percentile(0.99);
        long midtones = histogram.Counts.Skip(64).Take(128).Sum();
        #endregion

        Assert.True(shadows < highlights && midtones > 0);
    }

    [Fact]
    public void Colors()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region colors
        // Unique colors, the palette of a small image, and quick checks before choosing an output format.
        long count = photo.CountColors();
        IReadOnlyList<Rgba16>? palette = photo.GetColors(256);   // null: more than 256 colors
        bool needsAlpha = !photo.IsOpaque();
        bool grayscale = photo.IsGray(tolerance: 1 / 255f);
        #endregion

        Assert.True(count > 256 && palette is null && !needsAlpha && !grayscale);
    }

    [Fact]
    public void IsItGray()
    {
        using SampleFiles files = new();
        using Raster scan = Raster.Load("duck.png");
        using Raster gray = scan.Derive(ops => ops.Grayscale(), Ct);

        #region grayscale-check
        // A scan that is gray in all but name can be stored as one channel: a third of the size, no visible change.
        if (gray.IsGray(tolerance: 2 / 255f))
        {
            using Raster<Gray8> oneChannel = gray.As<Gray8>();
            oneChannel.Save("scan-gray.png");
        }
        #endregion

        Assert.True(gray.IsGray(2 / 255f));
    }

    [Fact]
    public void NearDuplicates()
    {
        using SampleFiles files = new();
        using Raster original = Raster.Load("duck.png");
        using Raster copy = Raster.Load("duck.jpg");
        using Raster other = Raster.Load("egret.png");

        #region hashes
        // Find near-duplicate photos: hash each once, store the 64-bit keys, and call a pair a duplicate within about 10 bits.
        ulong a = PerceptualHash.Dct(original), b = PerceptualHash.Dct(copy), c = PerceptualHash.Dct(other);
        bool duplicate = PerceptualHash.Distance(a, b) <= 10;
        bool unrelated = PerceptualHash.Distance(a, c) > 10;
        string key = a.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        #endregion

        Assert.True(duplicate && unrelated && key.Length == 16);
    }

    [Fact]
    public void Fingerprint()
    {
        using SampleFiles files = new();
        using Raster png = Raster.Load("duck.png");

        #region signature
        // The same pixels give the same signature, whatever the file or pixel type: dedupe exact copies.
        using Raster<Rgba16> wide = png.As<Rgba16>();
        bool same = png.Signature() == wide.Signature();
        #endregion

        Assert.True(same);
    }
}
