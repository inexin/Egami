using Egami.Colors;
using Egami.Drawing.Rasterization;

namespace Egami.Drawing.Tests.Inks;

/// <summary>Phase 2.6.5 task 15: the link grid an ink canvas converts images and shaded paints through stays close to the transform.</summary>
public class LinkGridTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheGridIsWithinAThirdOfALevelOfTheTransform(bool bpc)
    {
        ColorTransform transform = ColorTransform.Create(ColorProfile.Srgb, ColorProfile.Fogra39, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = bpc });
        Random random = new(15);
        float[] rgb = new float[3 * 20_000];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (float)random.NextDouble();
        }

        rgb[0] = rgb[1] = rgb[2] = 1;      // white, the grid's last corner
        rgb[3] = -0.1f;                    // outside the grid: the transform itself
        float[] exact = new float[4 * 20_000], grid = new float[4 * 20_000];
        transform.Apply(rgb, exact);
        LinkGrid.For(transform).Apply(rgb, grid);

        float worst = exact.Zip(grid, (a, b) => MathF.Abs(a - b)).Max();
        Assert.True(worst * 255 < 1f / 3, $"worst {worst * 255:F3} levels");
        Assert.Same(LinkGrid.For(transform), LinkGrid.For(transform));
    }
}
