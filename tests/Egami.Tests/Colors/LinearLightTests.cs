using Egami.Colors;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>
/// Phase 2.1.2 task 10: linear-light operations decode with the raster's own curve, so they equal converting to linear, operating
/// and converting back.
/// </summary>
public class LinearLightTests
{
    private static readonly string Duck = Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png");

    public static TheoryData<string> Operations() => ["resize", "blur", "grayscale"];

    [Theory]
    [MemberData(nameof(Operations))]
    public void GammaRastersMatchConvertOperateConvertBack(string operation)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(Duck);
        raster.Apply(ops => ops.Fit(160, 160), TestContext.Current.CancellationToken);
        raster.Metadata.ColorSpace = ColorSpaceHint.Gamma;
        raster.Metadata.Gamma = 1.8;
        ColorProfile gamma = raster.Metadata.GetColorProfile();
        ColorProfile linear = ColorProfile.Create(ColorPrimaries.Rec709, TransferFunction.Linear, "Linear");

        using Raster<Rgba8> direct = raster.Derive(ops => Apply(ops, operation, ColorSpace.Linear), TestContext.Current.CancellationToken);
        using Raster<RgbaF32> lit = raster.ConvertColor<RgbaF32>(linear);
        lit.Apply(ops => Apply(ops, operation, ColorSpace.Encoded), TestContext.Current.CancellationToken);
        using Raster<Rgba8> expected = lit.ConvertColor<Rgba8>(gamma);
        AssertClose(expected, direct, 1);

        // And it is not what the sRGB curve gives.
        raster.Metadata.ColorSpace = ColorSpaceHint.Srgb;
        using Raster<Rgba8> srgb = raster.Derive(ops => Apply(ops, operation, ColorSpace.Linear), TestContext.Current.CancellationToken);
        Assert.True(Differences(srgb, direct) > 0);
    }

    [Fact]
    public void LinearRastersAreUsedAsTheyAre()
    {
        using Raster<RgbaF32> raster = Raster.Load<RgbaF32>(Duck);
        raster.Metadata.ColorSpace = ColorSpaceHint.Linear;
        using Raster<RgbaF32> linear = raster.Derive(ops => ops.Blur(2, ColorSpace.Linear), TestContext.Current.CancellationToken);
        using Raster<RgbaF32> encoded = raster.Derive(ops => ops.Blur(2, ColorSpace.Encoded), TestContext.Current.CancellationToken);
        for (int y = 0; y < raster.Height; y += 9)
        {
            for (int x = 0; x < raster.Width; x += 9)
            {
                RgbaF32 a = linear.GetPixel(x, y), b = encoded.GetPixel(x, y);
                Assert.True(Math.Abs(a.R - b.R) < 1e-5 && Math.Abs(a.G - b.G) < 1e-5, $"({x}, {y}): {a} against {b}");
            }
        }
    }

    [Fact]
    public void PqRastersBlurInLinearLight()
    {
        using Raster<RgbaF32> raster = Raster.Load<RgbaF32>(Path.Combine(AppContext.BaseDirectory, "images", "hdr", "duck-pq.png"));
        Assert.True(raster.Metadata.GetColorProfile().IsHdr);
        ColorTransformOptions keep = new() { ToneMapping = ToneMapping.Clip };
        ColorProfile linear2020 = ColorProfile.Create(ColorPrimaries.Rec2020, TransferFunction.Linear, "Linear Rec. 2020");

        using Raster<RgbaF32> direct = raster.Derive(ops => ops.Blur(1.5f, ColorSpace.Linear), TestContext.Current.CancellationToken);
        using Raster<RgbaF32> lit = raster.ConvertColor<RgbaF32>(linear2020, keep);
        lit.Apply(ops => ops.Blur(1.5f, ColorSpace.Encoded), TestContext.Current.CancellationToken);
        using Raster<RgbaF32> expected = lit.ConvertColor<RgbaF32>(ColorProfile.Rec2100Pq, keep);
        for (int y = 0; y < raster.Height; y += 5)
        {
            for (int x = 0; x < raster.Width; x += 5)
            {
                RgbaF32 a = direct.GetPixel(x, y), b = expected.GetPixel(x, y);
                Assert.True(Math.Abs(a.R - b.R) < 2e-4 && Math.Abs(a.G - b.G) < 2e-4 && Math.Abs(a.B - b.B) < 2e-4, $"({x}, {y}): {a} against {b}");
            }
        }
    }

    [Fact]
    public void TheCurveFollowsAConversionInTheChain()
    {
        // After ConvertColor to linear, a linear-light resize is an encoded one.
        using Raster<RgbaF32> raster = Raster.Load<RgbaF32>(Duck);
        using Raster<RgbaF32> chained = raster.Derive(ops => ops.ConvertColor(ColorProfile.LinearSrgb).Resize(100, 75, colorSpace: ColorSpace.Linear), TestContext.Current.CancellationToken);
        using Raster<RgbaF32> plain = raster.Derive(ops => ops.ConvertColor(ColorProfile.LinearSrgb).Resize(100, 75, colorSpace: ColorSpace.Encoded), TestContext.Current.CancellationToken);
        Assert.Equal(plain.GetPixel(50, 40).R, chained.GetPixel(50, 40).R, 5);
    }

    private static void Apply(RasterOps ops, string operation, ColorSpace space)
    {
        _ = operation switch
        {
            "resize" => ops.Resize(97, 61, ResampleFilter.Bicubic, space),
            "blur" => ops.Blur(2.5f, space),
            _ => ops.Grayscale(LumaWeights.Rec709, 0.8f, space),
        };
    }

    private static void AssertClose(Raster<Rgba8> expected, Raster<Rgba8> actual, int levels)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba8 a = expected.GetPixel(x, y), b = actual.GetPixel(x, y);
                Assert.True(Math.Abs(a.R - b.R) <= levels && Math.Abs(a.G - b.G) <= levels && Math.Abs(a.B - b.B) <= levels && a.A == b.A, $"({x}, {y}): {b} against {a}");
            }
        }
    }

    private static int Differences(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                n += a.GetPixel(x, y) == b.GetPixel(x, y) ? 0 : 1;
            }
        }

        return n;
    }
}
