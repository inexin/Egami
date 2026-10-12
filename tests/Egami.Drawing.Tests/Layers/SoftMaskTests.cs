using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tests.Layers;

/// <summary>
/// Soft masks, rebuilt from the transparency examples of the PDF reference (ISO 32000-1 section 11.5): a luminosity mask from a
/// gradient fades a group, an alpha mask cuts it to a shape, the backdrop color fills what the mask group leaves undrawn, and a
/// transfer function reshapes the mask.
/// </summary>
public class SoftMaskTests
{
    private static readonly Rgba8 Red = new(200, 20, 20, 255);

    [Fact]
    public void ALuminosityMaskOfAWhiteShapeOnBlackShowsTheGroupOnlyInside()
    {
        Rgba8[] p = Draw(new SoftMask(m => m.FillRect(new RectF(10, 10, 20, 20), Rgba8.White)));
        Assert.Equal(Red, p[(20 * 40) + 20]);
        Assert.Equal(Rgba8.White, p[(5 * 40) + 5]);
        Assert.Equal(Rgba8.White, p[(35 * 40) + 35]);
    }

    [Fact]
    public void AGradientLuminosityMaskFadesTheGroup()
    {
        // Black to white across: the group shows from nothing on the left to fully on the right, as the PDF example's fade.
        SoftMask mask = new(m => m.DrawPaint(new LinearGradient(new Vector2(0.5f, 0), new Vector2(39.5f, 0), new RgbaF32(0, 0, 0, 1), new RgbaF32(1, 1, 1, 1))));
        Rgba8[] p = Draw(mask);
        Assert.Equal(Rgba8.White, p[(20 * 40) + 0]);
        Assert.Equal(Red, p[(20 * 40) + 39]);
        Rgba8 mid = p[(20 * 40) + 20];
        Assert.InRange(mid.G, 125, 145);
        for (int x = 1; x < 40; x++)
        {
            Assert.True(p[(20 * 40) + x].G <= p[(20 * 40) + x - 1].G);
        }
    }

    [Fact]
    public void AnAlphaMaskCutsTheGroupToAShape()
    {
        Rgba8[] p = Draw(new SoftMask(m => m.FillCircle(new Vector2(20, 20), 10, new RgbaF32(0, 0, 0, 0.5f)), SoftMaskKind.Alpha));
        Rgba8 inside = p[(20 * 40) + 20];
        Assert.InRange(inside.G, 135, 140);
        Assert.Equal(Rgba8.White, p[(2 * 40) + 2]);
    }

    [Fact]
    public void TheBackdropColorFillsWhatTheMaskLeavesUndrawn()
    {
        // A white backdrop shows the group wherever the mask draws nothing; a black shape hides it there.
        Rgba8[] p = Draw(new SoftMask(m => m.FillRect(new RectF(10, 10, 20, 20), Rgba8.Black), backdrop: new RgbaF32(1, 1, 1, 1)));
        Assert.Equal(Red, p[(5 * 40) + 5]);
        Assert.Equal(Rgba8.White, p[(20 * 40) + 20]);
    }

    [Fact]
    public void ATransferFunctionReshapesTheMask()
    {
        Rgba8[] p = Draw(new SoftMask(m => m.FillRect(new RectF(10, 10, 20, 20), Rgba8.White), transfer: v => 1 - v));
        Assert.Equal(Rgba8.White, p[(20 * 40) + 20]);
        Assert.Equal(Red, p[(5 * 40) + 5]);
    }

    [Fact]
    public void AMaskedLayerInsideAnotherLayerComposes()
    {
        using Raster<Rgba8> raster = Raster.Create(40, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.SaveLayer(0.5f);
            canvas.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.FillRect(new RectF(0, 0, 20, 40), Rgba8.White)) });
            canvas.Clear(Red);
            canvas.Restore();
            canvas.Restore();
        }

        Rgba8 left = raster.GetPixel(10, 20), right = raster.GetPixel(30, 20);
        Assert.InRange(left.G, 135, 140);
        Assert.Equal(Rgba8.White, right);
    }

    private static Rgba8[] Draw(SoftMask mask)
    {
        using Raster<Rgba8> raster = Raster.Create(40, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.SaveLayer(new LayerOptions { Mask = mask });
            canvas.Clear(Red);
            canvas.Restore();
        }

        Rgba8[] pixels = new Rgba8[40 * 40];
        for (int y = 0; y < 40; y++)
        {
            raster.Row(y).CopyTo(pixels.AsSpan(y * 40, 40));
        }

        return pixels;
    }
}
