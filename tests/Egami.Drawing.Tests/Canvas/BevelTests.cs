using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

public class BevelTests
{
    private static readonly Rgba8 Gray = new(128, 128, 128, 255);

    private static Raster<Rgba8> Draw(Bevel bevel, Action<RasterCanvas<Rgba8>>? setup = null)
    {
        Raster<Rgba8> raster = Raster.Create(140, 140, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        setup?.Invoke(canvas);
        Path square = Path.Rect(new RectF(20, 20, 100, 100));
        canvas.FillPath(square, Gray);
        canvas.DrawBevel(square, bevel);
        return raster;
    }

    private static int Luma(Rgba8 c) => (c.R + c.G + c.B) / 3;

    [Fact]
    public void RimFacingTheLightIsLighterAndTheFarRimDarker()
    {
        using Raster<Rgba8> lit = Draw(new Bevel { Width = 10, Height = 10, Material = BevelMaterial.Matte });
        Assert.True(Luma(lit.GetPixel(70, 23)) > 150, "top rim");
        Assert.True(Luma(lit.GetPixel(23, 70)) > 150, "left rim");
        Assert.True(Luma(lit.GetPixel(70, 116)) < 110, "bottom rim");
        Assert.True(Luma(lit.GetPixel(116, 70)) < 110, "right rim");
        Assert.Equal(Gray, lit.GetPixel(70, 70));
        Assert.Equal(Rgba8.White, lit.GetPixel(10, 10));
        Assert.Equal(Rgba8.White, lit.GetPixel(70, 130));

        using Raster<Rgba8> flipped = Draw(new Bevel { Width = 10, Height = 10, Material = BevelMaterial.Matte, Light = BevelLight.FromDirection(LightDirection.BottomRight) });
        Assert.True(Luma(flipped.GetPixel(70, 23)) < 110);
        Assert.True(Luma(flipped.GetPixel(70, 116)) > 150);
    }

    [Fact]
    public void NothingToDrawLeavesTheCanvasAlone()
    {
        using Raster<Rgba8> plain = Draw(new Bevel { Width = 0 });
        foreach (Bevel bevel in new[] { new Bevel { Material = BevelMaterial.Flat }, new Bevel { Height = 0 }, new Bevel { Light = new BevelLight(135, 45, 0) } })
        {
            using Raster<Rgba8> drawn = Draw(bevel);
            Assert.Equal(plain.GetPixel(70, 23), drawn.GetPixel(70, 23));
            Assert.Equal(plain.GetPixel(70, 116), drawn.GetPixel(70, 116));
        }

        using Raster<Rgba8> raster = Raster.Create(10, 10, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        canvas.DrawBevel(Path.Empty, new Bevel());
        canvas.DrawBevel(Path.Rect(new RectF(100, 100, 20, 20)), new Bevel());
        Assert.Equal(Rgba8.White, raster.GetPixel(5, 5));
    }

    [Fact]
    public void EveryProfileAndMaterialStaysInsideTheShape()
    {
        foreach (BevelProfile profile in Enum.GetValues<BevelProfile>())
        {
            foreach (BevelMaterial material in Enum.GetValues<BevelMaterial>())
            {
                using Raster<Rgba8> drawn = Draw(new Bevel { Profile = profile, Material = material, Width = 12, Height = 8 });
                Assert.Equal(Rgba8.White, drawn.GetPixel(19, 70));
                Assert.Equal(Rgba8.White, drawn.GetPixel(121, 70));
                Assert.Equal(Gray, drawn.GetPixel(70, 70));
                if (material != BevelMaterial.Flat)
                {
                    bool changed = false;
                    for (int y = 20; y < 32 && !changed; y++)
                    {
                        changed = drawn.GetPixel(70, y) != Gray || drawn.GetPixel(70, 139 - y) != Gray;
                    }

                    Assert.True(changed, $"{profile} {material} draws nothing");
                }
            }
        }
    }

    [Fact]
    public void ProfilesRiseFromTheEdge()
    {
        foreach (BevelProfile profile in Enum.GetValues<BevelProfile>())
        {
            Func<float, float> f = BevelShading.Profile(profile);
            Assert.Equal(0, f(0), 4);
            for (float t = 0; t <= 1; t += 1 / 64f)
            {
                Assert.InRange(f(t), 0, 1.0001f);
            }

            Assert.True(f(1) >= 0.5f, profile.ToString());
        }
    }

    [Fact]
    public void WidthScalesWithTheTransform()
    {
        // At twice the scale the 10-unit bevel reaches 20 device pixels in, so a pixel 15 in is still on the rim.
        using Raster<Rgba8> small = Draw(new Bevel { Width = 10, Height = 10, Material = BevelMaterial.Matte, Profile = BevelProfile.Angle });
        Assert.Equal(Gray, small.GetPixel(70, 35));
        using Raster<Rgba8> raster = Raster.Create(280, 280, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.Scale(2);
            Path square = Path.Rect(new RectF(20, 20, 100, 100));
            canvas.FillPath(square, Gray);
            canvas.DrawBevel(square, new Bevel { Width = 10, Height = 10, Material = BevelMaterial.Matte, Profile = BevelProfile.Angle });
        }

        Assert.True(Luma(raster.GetPixel(140, 55)) > 150);
        Assert.Equal(Gray, raster.GetPixel(140, 70));
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bevel { Width = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bevel { Height = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bevel { Profile = (BevelProfile)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bevel { Material = (BevelMaterial)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bevel { Light = new BevelLight(0, 91) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bevel { Light = new BevelLight(0, 45, -1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => BevelLight.FromDirection((LightDirection)99));
        Assert.Equal(new BevelLight(90, 30, 2), BevelLight.FromDirection(LightDirection.Top, 30, 2));
        using Raster<Rgba8> raster = Raster.Create(10, 10, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBevel(null!, new Bevel()));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBevel(Path.Rect(new RectF(0, 0, 5, 5)), null!));
    }
}
