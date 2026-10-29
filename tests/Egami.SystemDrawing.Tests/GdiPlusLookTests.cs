using Egami.SystemDrawing.Drawing2D;

namespace Egami.SystemDrawing.Tests;

/// <summary>
/// The 2.7.6 settings Graphics wires in, against the values measured from GDI+ on Windows (egami-compare
/// GdiAntialiasProbeTests, GdiGradientProbeTests and GdiScalingProbeTests): 8 × 4 antialiasing, gamma-corrected gradients and
/// DrawImage's interpolation modes.
/// </summary>
public class GdiPlusLookTests
{
    [Fact]
    public void AntiAliasSamplesEightByFour()
    {
        using Bitmap b = new(16, 4, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.FillRectangle(Brushes.Black, 4 + (1 / 64f), 0, 8, 4);
        }

        // One of the eight sample columns is uncovered: coverage 7/8, so 255 − 223 = 32.
        Assert.Equal(32, b.GetPixel(4, 1).G);
    }

    [Fact]
    public void GammaCorrectionFollowsGdiPlus()
    {
        using Bitmap b = new(256, 4, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 0), Color.Black, Color.White) { GammaCorrection = true };
            g.FillRectangle(brush, 0, 0, 256, 4);
        }

        Assert.InRange(b.GetPixel(32, 2).G, 99, 101);
        Assert.InRange(b.GetPixel(128, 2).G, 185, 187);
    }

    [Theory]
    [InlineData(InterpolationMode.Bilinear, 64, 159)]
    [InlineData(InterpolationMode.Bicubic, 65, 245)]
    [InlineData(InterpolationMode.HighQualityBilinear, 65, 187)]
    [InlineData(InterpolationMode.HighQualityBicubic, 65, 215)]
    public void DrawImageUsesEachModesKernel(InterpolationMode mode, int x, int gdiPlus)
    {
        using Bitmap source = new(32, 8, PixelFormat.Format32bppArgb);
        for (int y = 0; y < 8; y++)
        {
            for (int i = 0; i < 32; i++)
            {
                source.SetPixel(i, y, i == 16 ? Color.White : Color.Black);
            }
        }

        using Bitmap b = new(128, 8, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = mode;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle(0, 0, 128, 8), 0, 0, 32, 8, GraphicsUnit.Pixel, attributes);
        }

        Assert.InRange(b.GetPixel(x, 4).G, gdiPlus - 3, gdiPlus + 3);
    }
}
