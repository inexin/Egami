using System.Runtime.InteropServices;

using Egami.SystemDrawing.Drawing2D;

namespace Egami.SystemDrawing.Tests;

/// <summary>
/// Graphics, Matrix and Region, checked against what GDI+ gives on Windows (egami-compare, sysdrawing GraphicsProbes and
/// RenderProbes): the expected values here are copied from those measurements.
/// </summary>
public class GraphicsTests
{
    private static readonly Color Ink = Color.FromArgb(255, 20, 60, 200);

    private static Bitmap White(PixelFormat format = PixelFormat.Format32bppArgb)
    {
        Bitmap b = new(64, 48, format);
        using Graphics g = Graphics.FromImage(b);
        g.Clear(Color.White);
        return b;
    }

    [Theory]
    [InlineData(PixelFormat.Format1bppIndexed, typeof(ArgumentException))]
    [InlineData(PixelFormat.Format8bppIndexed, typeof(ArgumentException))]
    [InlineData(PixelFormat.Format16bppGrayScale, typeof(ExternalException))]
    [InlineData(PixelFormat.Format16bppArgb1555, typeof(ExternalException))]
    public void FromImageThrowsWhereGdiPlusThrows(PixelFormat format, Type exception)
    {
        using Bitmap b = new(4, 4, format);
        Assert.IsType(exception, Record.Exception(() => Graphics.FromImage(b)));
    }

    [Fact]
    public void QualitySettingsReadBackAsGdiPlusNormalizesThem()
    {
        using Bitmap b = White();
        using Graphics g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.HighQuality;
        Assert.Equal(SmoothingMode.AntiAlias, g.SmoothingMode);
        g.SmoothingMode = SmoothingMode.HighSpeed;
        Assert.Equal(SmoothingMode.None, g.SmoothingMode);
        g.InterpolationMode = InterpolationMode.Low;
        Assert.Equal(InterpolationMode.Bilinear, g.InterpolationMode);
        g.InterpolationMode = InterpolationMode.High;
        Assert.Equal(InterpolationMode.HighQualityBicubic, g.InterpolationMode);
        Assert.Throws<ArgumentException>(() => g.PageUnit = GraphicsUnit.World);
        Assert.Throws<ArgumentException>(() => g.PageScale = 0);
        Assert.Throws<ArgumentException>(() => g.Transform = new Matrix(1, 2, 2, 4, 0, 0));
    }

    [Fact]
    public void DisplayUnitsIgnoreThePageScale()
    {
        using Bitmap b = White();
        using Graphics g = Graphics.FromImage(b);
        g.PageScale = 2.5f;
        PointF[] display = [new(10, -3.5f)];
        g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, display);
        Assert.Equal(new PointF(10, -3.5f), display[0]);
        g.PageUnit = GraphicsUnit.Pixel;
        PointF[] pixel = [new(10, -3.5f)];
        g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, pixel);
        Assert.Equal(new PointF(25, -8.75f), pixel[0]);
    }

    [Fact]
    public void ClipIsKeptAsWholeDevicePixels()
    {
        using Bitmap b = White();
        using Graphics g = Graphics.FromImage(b);
        g.SetClip(new RectangleF(0.5f, 0.5f, 10.25f, 4.75f));
        Assert.Equal(new RectangleF(1, 1, 10, 5), g.ClipBounds);
        g.SetClip(new Rectangle(10, 10, 20, 15));
        Assert.True(g.IsVisible(9.5f, 12));
        Assert.False(g.IsVisible(29.9f, 12));
        g.TranslateTransform(5, 5);
        Assert.Equal(new RectangleF(5, 5, 20, 15), g.ClipBounds);
    }

    [Fact]
    public void RegionsRasterizeOnWholeCoordinates()
    {
        using Bitmap b = White();
        using Graphics g = Graphics.FromImage(b);
        using Region r = new(new RectangleF(1.5f, 2.25f, 10, 5));
        Assert.Equal(new RectangleF(1.5f, 2.25f, 10, 5), r.GetBounds(g));
        Assert.Equal([new RectangleF(2, 3, 10, 5)], r.GetRegionScans(new Matrix()));
        r.Union(new Rectangle(5, 5, 10, 10));
        Assert.Equal(new RectangleF(2, 3, 13, 12), r.GetBounds(g));
        Assert.Equal([new RectangleF(2, 3, 10, 2), new RectangleF(2, 5, 13, 3), new RectangleF(5, 8, 10, 7)], r.GetRegionScans(new Matrix()));
    }

    [Fact]
    public void ContainersStartFromDefaultsAndMapTheirSourceInPixels()
    {
        using Bitmap b = White();
        using Graphics g = Graphics.FromImage(b);
        g.PageUnit = GraphicsUnit.Point;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        GraphicsContainer outer = g.BeginContainer();
        Assert.Equal(GraphicsUnit.Display, g.PageUnit);
        Assert.Equal(SmoothingMode.None, g.SmoothingMode);
        g.EndContainer(outer);
        Assert.Equal(GraphicsUnit.Point, g.PageUnit);

        g.PageUnit = GraphicsUnit.Display;
        GraphicsContainer mapped = g.BeginContainer(new RectangleF(10, 10, 40, 20), new RectangleF(0, 0, 4, 4), GraphicsUnit.Pixel);
        g.BeginContainer(new RectangleF(0, 0, 2, 2), new RectangleF(0, 0, 1, 1), GraphicsUnit.Inch);
        PointF[] p = [new(1, 1)];
        g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, p);
        Assert.Equal(10.208334, p[0].X, 5);
        Assert.Equal(10.104166, p[0].Y, 5);
        g.EndContainer(mapped);
    }

    [Fact]
    public void NearestColorCutsLowBitsOfSixteenBitFormats()
    {
        using Bitmap b = new(4, 4, PixelFormat.Format16bppRgb565);
        using Graphics g = Graphics.FromImage(b);
        Assert.Equal(unchecked((int)0xFF204060), g.GetNearestColor(Color.FromArgb(200, 0x21, 0x42, 0x63)).ToArgb());
    }

    [Fact]
    public void AliasedFillsHoldThePixelsWhoseWholeCoordinatesAreInside()
    {
        using Bitmap b = White();
        using (Graphics g = Graphics.FromImage(b))
        {
            using SolidBrush brush = new(Ink);
            g.FillRectangle(brush, 8.3f, 6.7f, 20.5f, 20.2f);
        }

        Assert.Equal(Color.White.ToArgb(), b.GetPixel(8, 10).ToArgb());
        Assert.Equal(Ink.ToArgb(), b.GetPixel(9, 10).ToArgb());
        Assert.Equal(Ink.ToArgb(), b.GetPixel(28, 10).ToArgb());
        Assert.Equal(Color.White.ToArgb(), b.GetPixel(29, 10).ToArgb());
    }

    [Fact]
    public void TranslucentCopiesGoThroughPremultipliedArgb()
    {
        using Bitmap argb = White();
        using Bitmap rgb = White(PixelFormat.Format24bppRgb);
        foreach (Bitmap b in new[] { argb, rgb })
        {
            using Graphics g = Graphics.FromImage(b);
            g.CompositingMode = CompositingMode.SourceCopy;
            using SolidBrush glass = new(Color.FromArgb(128, 200, 30, 10));
            g.FillRectangle(glass, 0, 0, 10, 10);
        }

        Assert.Equal(unchecked((int)0x80C71D09), argb.GetPixel(5, 5).ToArgb());
        Assert.Equal(unchecked((int)0xFF640F05), rgb.GetPixel(5, 5).ToArgb());
    }

    [Fact]
    public void DrawImageAtAPointUsesTheImageResolution()
    {
        using Bitmap source = new(8, 8);
        source.SetResolution(48, 48);
        using (Graphics s = Graphics.FromImage(source))
        {
            s.Clear(Ink);
        }

        using Bitmap b = White();
        using (Graphics g = Graphics.FromImage(b))
        {
            g.DrawImage(source, 0, 0);
        }

        Assert.Equal(Ink.ToArgb(), b.GetPixel(14, 14).ToArgb());
        Assert.Equal(Color.White.ToArgb(), b.GetPixel(17, 17).ToArgb());
    }

    [Fact]
    public void AbortedDrawImageThrows()
    {
        using Bitmap source = new(8, 8);
        using Bitmap b = White();
        using Graphics g = Graphics.FromImage(b);
        Assert.Throws<ExternalException>(() => g.DrawImage(source, new Rectangle(0, 0, 8, 8), 0, 0, 8, 8, GraphicsUnit.Pixel, null, _ => true));
    }

    [Fact]
    public void FullArcsAndRoundedCornersDoNotWrap()
    {
        using Bitmap b = White();
        using (Graphics g = Graphics.FromImage(b))
        {
            using SolidBrush brush = new(Ink);
            g.FillRoundedRectangle(brush, new RectangleF(6, 6, 50, 34), new SizeF(20, 10));
        }

        Assert.Equal(Ink.ToArgb(), b.GetPixel(50, 12).ToArgb());
        Assert.Equal(Color.White.ToArgb(), b.GetPixel(6, 6).ToArgb());
    }

    [Fact]
    public void MatricesPrependByDefault()
    {
        using Matrix m = new();
        m.Translate(3, 4);
        m.Scale(2, 0.5f);
        Assert.Equal([2f, 0, 0, 0.5f, 3, 4], m.Elements);
        m.Scale(3, 3, MatrixOrder.Append);
        Assert.Equal([6f, 0, 0, 1.5f, 9, 12], m.Elements);
        using Matrix singular = new(1, 2, 2, 4, 0, 0);
        Assert.False(singular.IsInvertible);
    }
}
