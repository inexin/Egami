using Egami.Colors;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// An image knocks out every plate it covers with overprint off, as a solid fill does: plates the image has no ink for (spots
/// under an RGB photo, or a spot under an ink image of process plates only) go to none; with overprint on they keep what is
/// below.
/// </summary>
public class InkImageKnockoutTests
{
    private static readonly InkSet Set = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));

    private static byte SpotUnderImage(Action<InkCanvas> drawImage)
    {
        using InkRaster plates = new(Set, 40, 40);
        using (InkCanvas canvas = new(plates))
        {
            canvas.Clear(InkColor.None);
            canvas.FillRect(new RectF(0, 0, 40, 40), new SolidPaint(InkColor.Spot(Set[4], 1)));
            drawImage(canvas);
        }

        return plates.Plate("PANTONE 186 C")!.Row8(20)[20];
    }

    [Theory]
    [InlineData(ResampleFilter.Nearest)]
    [InlineData(ResampleFilter.Bilinear)]
    public void AnRgbImageKnocksOutTheSpotsBelow(ResampleFilter filter)
    {
        using Raster<Rgba8> photo = Raster.Create(8, 8, new Rgba8(40, 120, 200, 255));
        SamplingOptions sampling = new() { Filter = filter };

        Assert.Equal(0, SpotUnderImage(c => c.DrawImage(photo, new RectF(10, 10, 20, 20), sampling)));
        Assert.Equal(255, SpotUnderImage(c => c.DrawImage(photo, new RectF(10, 10, 20, 20), sampling, new SolidPaint(new RgbaF32(1, 1, 1, 1)) { Overprint = Overprint.On })));
    }

    [Fact]
    public void AnInkImageOfProcessPlatesKnocksOutTheSpotsBelow()
    {
        using InkRaster image = new(InkSet.FromProfile(ColorProfile.Fogra39), 8, 8);
        for (int y = 0; y < 8; y++)
        {
            image.Alpha.Row8(y).Fill(255);
            image.Plate("Cyan")!.Row8(y).Fill(128);
        }

        Assert.Equal(0, SpotUnderImage(c => c.DrawImage(image, new RectF(10, 10, 20, 20))));
        Assert.Equal(255, SpotUnderImage(c => c.DrawImage(image, new RectF(10, 10, 20, 20), paint: new SolidPaint(new RgbaF32(1, 1, 1, 1)) { Overprint = Overprint.On })));
    }

    [Fact]
    public void AHalfTransparentImageLeavesHalfTheSpot()
    {
        using Raster<Rgba8> photo = Raster.Create(8, 8, new Rgba8(40, 120, 200, 128));

        byte spot = SpotUnderImage(c => c.DrawImage(photo, new RectF(10, 10, 20, 20), new SamplingOptions { Filter = ResampleFilter.Nearest }));

        Assert.InRange(spot, 125, 129);
    }
}
