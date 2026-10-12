using Egami.Operations;
using Egami.Pixels;
using Egami.Svg.Filters;
using Egami.Svg.Rendering;

namespace Egami.Svg.Tests;

/// <summary>Task 11: the filter kernels Egami.Svg runs itself (resvg's suite, gated in egami-compare, covers the rest).</summary>
public class FilterTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    [Theory]
    [InlineData(1.5f)]
    [InlineData(6f)]
    public void BlurKeepsTheTotalAndSpreadsEvenly(float sigma)
    {
        const int size = 61;
        RgbaF32[] pixels = new RgbaF32[size * size];
        pixels[(30 * size) + 30] = new RgbaF32(1, 0.5f, 0.25f, 1);
        SvgGaussianBlur.Blur(pixels, size, size, sigma, sigma);

        double alpha = pixels.Sum(p => (double)p.A);
        Assert.Equal(1, alpha, 3);
        Assert.Equal(pixels[(30 * size) + 25].A, pixels[(30 * size) + 35].A, 5);
        Assert.Equal(pixels[(25 * size) + 30].A, pixels[(35 * size) + 30].A, 5);

        // Colors are straight again: the spot's color holds wherever it reached.
        RgbaF32 near = pixels[(31 * size) + 31];
        Assert.Equal(0.5f, near.G / near.R, 3);
    }

    [Fact]
    public void AnIdentityKernelChangesNothing()
    {
        RgbaF32[] pixels = [new(1, 0, 0, 1), new(0, 1, 0, 0.5f), new(0, 0, 1, 0.25f), new(1, 1, 1, 0)];
        SvgConvolve identity = new(default, false, SvgFilterInput.Graphic, 3, 3, [0, 0, 0, 0, 1, 0, 0, 0, 0], 1, 0, 1, 1, EdgeMode.Clamp, false);
        RgbaF32[] result = SvgFilterKernels.Convolve(pixels, 2, 2, identity);
        for (int i = 0; i < pixels.Length; i++)
        {
            Assert.Equal(pixels[i].A, result[i].A, 5);
            if (pixels[i].A > 0)
            {
                Assert.Equal(pixels[i].R, result[i].R, 5);
                Assert.Equal(pixels[i].G, result[i].G, 5);
                Assert.Equal(pixels[i].B, result[i].B, 5);
            }
        }
    }

    [Fact]
    public void AnEvenKernelUsesItsTarget()
    {
        // A 2×1 kernel [0 1] with its target on the right cell reads the pixel to the left (the kernel turns 180°).
        RgbaF32[] pixels = [new(1, 0, 0, 1), new(0, 0, 1, 1), new(0, 1, 0, 1)];
        SvgConvolve shift = new(default, false, SvgFilterInput.Graphic, 2, 1, [0, 1], 1, 0, 1, 0, EdgeMode.Transparent, false);
        RgbaF32[] result = SvgFilterKernels.Convolve(pixels, 3, 1, shift);
        Assert.Equal(0, result[0].A);
        Assert.Equal(new RgbaF32(1, 0, 0, 1), result[1]);
        Assert.Equal(new RgbaF32(0, 0, 1, 1), result[2]);
    }

    [Fact]
    public void AFlatSurfaceUnderALightOverheadTakesTheLightsColor()
    {
        RgbaF32[] pixels = Enumerable.Repeat(new RgbaF32(0, 0, 0, 1), 9).ToArray();
        SvgLighting diffuse = new(default, false, SvgFilterInput.Graphic, false, 1, 0.5f, 1, new RgbaF32(1, 1, 1, 1), new SvgDistantLight(0, 90));
        RgbaF32[] result = SvgFilterKernels.Light(pixels, 3, 3, diffuse, diffuse.Light, new RgbaF32(1, 0.5f, 0, 1));
        Assert.All(result, p =>
        {
            Assert.Equal(0.5f, p.R, 4);
            Assert.Equal(0.25f, p.G, 4);
            Assert.Equal(1, p.A);
        });
    }

    [Fact]
    public void ASpotLightAtItsTargetLightsNothing()
    {
        RgbaF32[] pixels = Enumerable.Repeat(new RgbaF32(0, 0, 0, 1), 4).ToArray();
        SvgSpotLight spot = new(new System.Numerics.Vector3(1, 1, 5), new System.Numerics.Vector3(1, 1, 5), 1, null);
        SvgLighting diffuse = new(default, false, SvgFilterInput.Graphic, false, 1, 1, 1, new RgbaF32(1, 1, 1, 1), spot);
        Assert.All(SvgFilterKernels.Light(pixels, 2, 2, diffuse, spot, new RgbaF32(1, 1, 1, 1)), p => Assert.Equal(new RgbaF32(0, 0, 0, 1), p));
    }

    [Fact]
    public void AFilterThatDrawsItsOwnElementIsIgnored()
    {
        SvgDocument document = SvgDocument.Parse($"""
            <svg {Ns} width='20' height='20'>
              <filter id='f'><feImage xlink:href='#r'/></filter>
              <rect id='r' width='20' height='20' fill='#f00' filter='url(#f)'/>
            </svg>
            """);
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Assert.Equal(new Rgba8(255, 0, 0, 255), RenderTreeTests.Pixel(raster, 10, 10));
        Assert.Contains(document.Warnings, w => w.Id == "Svg.BadReference");
    }

    [Fact]
    public void AnInvalidFilterFunctionLeavesTheElementUnfiltered()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='20' height='20'><rect width='20' height='20' fill='#00f' filter='blur(4 2)'/></svg>");
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Assert.Equal(new Rgba8(0, 0, 255, 255), RenderTreeTests.Pixel(raster, 0, 0));
    }
}
