using Egami.Drawing.Rasterization;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// TrueType dropout control in GDI+'s LCD sampling (2.7.6 task 29): a horizontal stroke thinner than a pixel that falls between two
/// rows' sample points, as hinted SimSun's bars do, still turns a row on, as GDI+ ClearType draws it.
/// </summary>
public class GdiDropoutTests
{
    private static readonly GlyphStyle Style = new(Fonts.FontEdging.Lcd, false, Fonts.FontSynthesis.None, PixelGeometry.RgbHorizontal)
    {
        Look = new LcdLook(LcdFilter.Gdi, LcdSampling.Gdi, 1),
    };

    // A bar from x 2 to 10, 0.37 px tall, between the sample points of rows 4 (at 4.5) and 5 (at 5.5), nearer row 4's.
    private static readonly Path Bar = Path.Rect(new RectF(2, 4.55f, 8, 0.37f));

    [Fact]
    public void WithoutDropoutTheBarIsLost() => Assert.Equal(0, Lit(Render(null), 4) + Lit(Render(null), 5));

    [Fact]
    public void SmartDropoutTurnsOnTheNearerRow()
    {
        GlyphMask mask = Render((true, false));
        Assert.True(Lit(mask, 4) > 0);
        Assert.Equal(0, Lit(mask, 5));
    }

    [Fact]
    public void SimpleDropoutTurnsOnTheLowerRow()
    {
        GlyphMask mask = Render((false, false));
        Assert.Equal(0, Lit(mask, 4));
        Assert.True(Lit(mask, 5) > 0);
    }

    /// <summary>Without stubs, the bar's end samples, where the stroke does not go on to the next sample column, stay off.</summary>
    [Fact]
    public void NoStubsLeavesTheEndsOff()
    {
        Assert.True(Lit(Render((true, true)), 4) < Lit(Render((true, false)), 4));
        Assert.True(Lit(Render((true, true)), 4) > 0);
    }

    private static GlyphMask Render((bool Smart, bool NoStubs)? dropout)
    {
        using CoverageRasterizer rasterizer = new(EgamiContext.Default.Memory, 1);
        return Lcd.Render(Bar, 0.01f, Style, rasterizer, dropout);
    }

    // The sum of a device row's channel values, or 0 outside the mask.
    private static int Lit(GlyphMask mask, int y)
    {
        int row = y - mask.Bounds.Y;
        if ((uint)row >= (uint)mask.Bounds.Height)
        {
            return 0;
        }

        int sum = 0;
        for (int i = 0; i < mask.Bounds.Width * 3; i++)
        {
            sum += mask.Coverage[(row * mask.Bounds.Width * 3) + i];
        }

        return sum;
    }
}
