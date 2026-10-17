using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.7: the byte paths of the fast blur, box blurs, drop shadow, lighting, emboss and shade stay within a level of the float path.</summary>
public class BytePathTests
{
    public static TheoryData<string, string> Cases()
    {
        TheoryData<string, string> data = [];
        foreach (string image in new[] { "duck", "shapes", "translucent" })
        {
            foreach (string chain in Chains.Keys)
            {
                data.Add(image, chain);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WithinALevelOfTheFloatPath(string image, string chain)
    {
        using Raster<Rgba8> source = Input(image);
        int gap = FloatGap(source, Chains[chain]);
        Assert.True(gap <= 1, $"{image} {chain}: {gap} levels from the float path");
    }

    [Theory]
    [InlineData("fast-20")]
    [InlineData("fast-7-linear")]
    [InlineData("blur-150-linear")]
    [InlineData("box-20")]
    [InlineData("shadow")]
    [InlineData("shadow-fraction")]
    [InlineData("light-spot")]
    [InlineData("emboss")]
    public void SameOnOneThreadAndAllOnTranslucentPixels(string chain)
    {
        using Raster<Rgba8> source = Input("translucent");
        using Raster<Rgba8> all = source.Derive(Chains[chain], Ct);
        using Raster<Rgba8> one = Input("translucent", EgamiContext.Default with { MaxParallelism = 1 });
        using Raster<Rgba8> oneResult = one.Derive(Chains[chain], Ct);
        Same(all, oneResult);
    }

    [Theory]
    [InlineData("bgra")]
    [InlineData("argb")]
    [InlineData("rgb")]
    [InlineData("gray")]
    [InlineData("grayalpha")]
    [InlineData("alpha")]
    public void OtherByteTypesWithinALevel(string type)
    {
        using Raster<Rgba8> source = Input("translucent");
        // A shadow grows a transparent canvas, which a type without alpha cannot hold the same way as the float copy.
        bool alpha = type is not ("rgb" or "gray");
        string[] chains = alpha ? ["fast-20", "fast-uneven", "blur-150", "fast-7-linear", "shadow", "shadow-fraction", "shadow-only"] : ["fast-20", "fast-uneven", "blur-150", "fast-7-linear"];
        foreach (string chain in chains)
        {
            int gap = type switch
            {
                "bgra" => TypedGap<Bgra8>(source, Chains[chain]),
                "argb" => TypedGap<Argb8>(source, Chains[chain]),
                "rgb" => TypedGap<Rgb8>(source, Chains[chain]),
                "gray" => TypedGap<Gray8>(source, Chains[chain]),
                "grayalpha" => TypedGap<GrayAlpha8>(source, Chains[chain]),
                _ => TypedGap<Alpha8>(source, Chains[chain]),
            };
            Assert.True(gap <= 1, $"{type} {chain}: {gap} levels from the float path");
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]
    [InlineData(37, 23)]
    [InlineData(300, 1)]
    [InlineData(1, 300)]
    [InlineData(71, 40)]
    public void OddSizesWithinALevel(int width, int height)
    {
        foreach (string image in new[] { "duck", "translucent" })
        {
            using Raster<Rgba8> full = Input(image);
            using Raster<Rgba8> source = full.Derive(o => o.Resize(width, height), Ct);
            foreach (string chain in new[] { "fast-3", "fast-20", "blur-150" })
            {
                int gap = FloatGap(source, Chains[chain]);
                Assert.True(gap <= 1, $"{image} {width}x{height} {chain}: {gap} levels from the float path");
            }
        }
    }

    [Theory]
    [InlineData("fast-20")]
    [InlineData("fast-uneven")]
    [InlineData("blur-150")]
    public void SameOnOneThreadAndAllOnOpaquePixels(string chain)
    {
        SameOnOneThread("egret", Chains[chain]);
    }

    [Theory]
    [InlineData("emboss")]
    [InlineData("emboss-color")]
    public void EmbossBytesAreTheFloatRowsExactly(string chain)
    {
        foreach (string image in new[] { "duck", "translucent" })
        {
            using Raster<Rgba8> source = Input(image);
            using Raster<Rgba8> bytes = source.Derive(Chains[chain], Ct);
            using Raster<RgbaF32> floats = source.As<RgbaF32>();
            using Raster<RgbaF32> floatResult = floats.Derive(Chains[chain], Ct);
            using Raster<Rgba8> reference = floatResult.As<Rgba8>();
            Same(reference, bytes);
        }
    }

    /// <summary><see cref="FilterAssert.FloatGap"/> on <typeparamref name="TPixel"/>: the same pixels as a float copy through the float path.</summary>
    private static int TypedGap<TPixel>(Raster<Rgba8> source, Action<RasterOps> chain)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> typed = source.As<TPixel>();
        using Raster<TPixel> bytes = typed.Derive(chain, Ct);
        using Raster<RgbaF32> floats = typed.As<RgbaF32>();
        using Raster<RgbaF32> floatResult = floats.Derive(chain, Ct);
        using Raster<TPixel> reference = floatResult.As<TPixel>();
        using Raster<Rgba8> a = reference.As<Rgba8>(), b = bytes.As<Rgba8>();
        return PremultipliedDifference(a, b);
    }

    private static readonly Dictionary<string, Action<RasterOps>> Chains = new()
    {
        ["fast-3"] = o => o.Blur(3, ColorSpace.Encoded, BlurMethod.Fast),
        ["fast-20"] = o => o.Blur(20, ColorSpace.Encoded, BlurMethod.Fast),
        ["fast-60"] = o => o.Blur(60, ColorSpace.Encoded, BlurMethod.Fast),
        ["fast-7-linear"] = o => o.Blur(7, ColorSpace.Linear, BlurMethod.Fast),
        ["fast-uneven"] = o => o.Blur(4.37f, ColorSpace.Encoded, BlurMethod.Fast),
        ["fast-30-linear"] = o => o.Blur(30, ColorSpace.Linear, BlurMethod.Fast),
        ["blur-150-linear"] = o => o.Blur(150, ColorSpace.Linear),
        ["box-4"] = o => o.BoxBlur(4),
        ["box-20"] = o => o.BoxBlur(20),
        ["blur-150"] = o => o.Blur(150),
        ["shadow"] = o => o.DropShadow(8, 8, 10, new Rgba8(0, 0, 0, 160)),
        ["shadow-fraction"] = o => o.DropShadow(3.4f, -2.7f, 6, new Rgba8(200, 40, 10, 255)),
        ["shadow-only"] = o => o.DropShadow(5, 5, 4, new Rgba8(0, 0, 255, 128), new DropShadowOptions { ShadowOnly = true, Expand = false }),
        ["light-distant"] = o => o.Lighting(new DistantLight(225, 40), LightingModel.Diffuse(), new LightingOptions { SurfaceScale = 4 }),
        ["light-spot"] = o => o.Lighting(new SpotLight(new Vector3(40, 30, 60), new Vector3(120, 140, 0), 4, 35), LightingModel.Specular(1, 20), new LightingOptions { SurfaceScale = 4 }),
        ["light-point"] = o => o.Lighting(new PointLight(new Vector3(100, 50, 80)), LightingModel.Specular(1.5f, 128), new LightingOptions { Height = HeightSource.Luminance }),
        ["light-colored"] = o => o.Lighting(new DistantLight(60, 20) { Color = new Rgba8(255, 200, 100, 255) }, LightingModel.Diffuse(0.8f)),
        ["emboss"] = o => o.Emboss(),
        ["emboss-color"] = o => o.Emboss(45, 3, 2, grayscale: false),
        ["shade"] = o => o.Shade(),
        ["shade-colored"] = o => o.Shade(120, 45, colored: true),
    };

    /// <summary>One of the operation images, or "translucent": the duck with alpha falling from 255 to 0 across it and a hole of 0.</summary>
    private static Raster<Rgba8> Input(string image, EgamiContext? context = null)
    {
        if (image != "translucent")
        {
            return Load(image, context);
        }

        Raster<Rgba8> raster = Load("duck", context);
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                int a = 255 - (255 * x / Math.Max(1, row.Length - 1));
                if (Math.Abs(x - (row.Length / 2)) < 20 && Math.Abs(y - (raster.Height / 2)) < 20)
                {
                    a = 0;
                }

                row[x] = row[x] with { A = (byte)(a * row[x].A / 255) };
            }
        }

        return raster;
    }
}
