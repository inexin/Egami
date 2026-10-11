using System.Security.Cryptography;

using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 2: BlurMethod.Fast, and Blur's existing overloads pinned to their 1.x pixels.</summary>
public class FastBlurTests
{
    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(4.7)]
    [InlineData(20.0)]
    [InlineData(60.0)]
    [InlineData(333.3)]
    public void VarianceIsSigmaSquared(double sigma)
    {
        BoxCascade cascade = BoxCascade.For(sigma);
        Assert.InRange(Math.Abs(cascade.Variance() - (sigma * sigma)), 0, 1e-6 * sigma * sigma);
        foreach (float alpha in cascade.Alphas)
        {
            Assert.InRange(alpha, 0f, 1f);
        }
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(8.5f)]
    public void ImpulseResponseHasTheVariance(float sigma)
    {
        // A white dot in the middle of a float image: the blur's output is its impulse response, whose variance along each axis is σ².
        int size = 161;
        using Raster<RgbaF32> raster = Raster.Create<RgbaF32>(size, size);
        raster.Row(size / 2)[size / 2] = new RgbaF32(1, 1, 1, 1);
        raster.Apply(o => o.Blur(sigma, ColorSpace.Encoded, BlurMethod.Fast), Ct);
        double sum = 0, xx = 0, yy = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double w = raster.Row(y)[x].A;
                sum += w;
                xx += w * (x - (size / 2)) * (x - (size / 2));
                yy += w * (y - (size / 2)) * (y - (size / 2));
            }
        }

        Assert.Equal(1, sum, 1e-4);
        Assert.Equal(sigma * sigma, xx / sum, 1e-3 * sigma * sigma);
        Assert.Equal(sigma * sigma, yy / sum, 1e-3 * sigma * sigma);
    }

    [Theory]
    [InlineData("duck", 3f)]
    [InlineData("duck", 12f)]
    [InlineData("egret", 30f)]
    [InlineData("shapes", 5f)]
    public void CloseToTheExactBlur(string image, float sigma)
    {
        using Raster<Rgba8> source = Load(image);
        using Raster<Rgba8> exact = source.Derive(o => o.Blur(sigma), Ct);
        using Raster<Rgba8> fast = source.Derive(o => o.Blur(sigma, ColorSpace.Encoded, BlurMethod.Fast), Ct);
        double psnr = Psnr(exact, fast);
        Assert.True(psnr >= 50, $"PSNR {psnr:F2} dB");
    }

    [Fact]
    public void ExactIsTheOldBlurAndSmallSigmaIsExact()
    {
        using Raster<Rgba8> source = Load("duck");
        using Raster<Rgba8> old = source.Derive(o => o.Blur(4, ColorSpace.Linear), Ct);
        using Raster<Rgba8> exact = source.Derive(o => o.Blur(4, ColorSpace.Linear, BlurMethod.Exact), Ct);
        using Raster<Rgba8> small = source.Derive(o => o.Blur(1.5f), Ct);
        using Raster<Rgba8> smallFast = source.Derive(o => o.Blur(1.5f, ColorSpace.Encoded, BlurMethod.Fast), Ct);
        Same(old, exact);
        Same(small, smallFast);
    }

    [Fact]
    public void SameOnOneThreadAndAll()
    {
        SameOnOneThread("egret", o => o.Blur(9, ColorSpace.Linear, BlurMethod.Fast));
    }

    [Fact]
    public void InsideARectangleMatchesTheWholeImage()
    {
        SameInsideArea("duck", new Rect(100, 80, 200, 150), o => o.Blur(7, ColorSpace.Encoded, BlurMethod.Fast));
    }

    /// <summary>The 1.x Blur, Sharpen and BoxBlur pixels on the 1.2 corpus, so no 2.5.4 change moves them.</summary>
    [Theory]
    [InlineData("duck", "blur-2")]
    [InlineData("duck", "blur-25-linear")]
    [InlineData("egret", "blur-150")]
    [InlineData("shapes", "blur-0.7")]
    [InlineData("shapes", "sharpen")]
    [InlineData("zone", "box-4")]
    public void OldBlursKeepTheirPixels(string image, string chain)
    {
        // One thread: the 1.x box blurs above σ 100 restart their running sums at each band, so their last bits depend on the thread count.
        using Raster<Rgba8> source = Load(image, EgamiContext.Default with { MaxParallelism = 1 });
        using Raster<Rgba8> result = source.Derive(
            o =>
            {
                _ = chain switch
                {
                    "blur-2" => o.Blur(2),
                    "blur-25-linear" => o.Blur(25, ColorSpace.Linear),
                    "blur-150" => o.Blur(150),
                    "blur-0.7" => o.Blur(0.7f),
                    "sharpen" => o.Sharpen(1.5f, 2),
                    _ => o.BoxBlur(4),
                };
            },
            Ct);
        Assert.Equal(PinnedHashes[$"{image}.{chain}"], Hash(result));
    }

    private static readonly Dictionary<string, string> PinnedHashes = new()
    {
        ["duck.blur-2"] = "98C0AD2C5C8B4F80",
        ["duck.blur-25-linear"] = "64A910FA4FB6B1CF",
        ["egret.blur-150"] = "CC03E845719D52DD",
        ["shapes.blur-0.7"] = "C8D5B5E234895E20",
        ["shapes.sharpen"] = "17539490832DCE75",
        ["zone.box-4"] = "6B8B1D44FC840016",
    };

    private static string Hash(Raster<Rgba8> raster)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int y = 0; y < raster.Height; y++)
        {
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y)));
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }
}
