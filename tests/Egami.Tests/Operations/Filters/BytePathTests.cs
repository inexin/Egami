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
    [InlineData("box-20")]
    [InlineData("shadow")]
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

    private static readonly Dictionary<string, Action<RasterOps>> Chains = new()
    {
        ["fast-3"] = o => o.Blur(3, ColorSpace.Encoded, BlurMethod.Fast),
        ["fast-20"] = o => o.Blur(20, ColorSpace.Encoded, BlurMethod.Fast),
        ["fast-60"] = o => o.Blur(60, ColorSpace.Encoded, BlurMethod.Fast),
        ["fast-7-linear"] = o => o.Blur(7, ColorSpace.Linear, BlurMethod.Fast),
        ["fast-uneven"] = o => o.Blur(4.37f, ColorSpace.Encoded, BlurMethod.Fast),
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
