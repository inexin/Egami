using System.Numerics;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// <see cref="CanvasOptions.PixelOffset"/> and <see cref="CanvasOptions.Antialiasing"/>: GDI+'s pixel centres, its 8 × 4 samples a
/// pixel and its thin pens, against the values measured from GDI+ in egami-compare (2.7.6 tasks 8 to 11). White is drawn on
/// black, so a pixel's red channel is its coverage.
/// </summary>
public class GdiPlusShapeTests
{
    private static readonly CanvasOptions GdiPlus = new() { Antialiasing = Antialiasing.GdiPlus };

    /// <summary>A left edge moving across a pixel in 1/64 steps loses one of the 8 columns per eighth, as GDI+ draws it.</summary>
    [Fact]
    public void LeftEdgesStepInEighths()
    {
        int[] gdiPlus = [255, 223, 223, 223, 223, 223, 223, 223, 223, 191];
        for (int k = 0; k < gdiPlus.Length; k++)
        {
            byte[,] c = Coverage(GdiPlus, 16, 4, canvas => canvas.FillRect(new RectF(4 + (k / 64f), 0, 8, 4), White));
            Assert.Equal(gdiPlus[k], c[4, 1]);
        }
    }

    /// <summary>Edges step in quarters down a pixel, a sample on a top edge is inside and one on a bottom edge outside.</summary>
    [Fact]
    public void TopAndBottomEdgesStepInQuarters()
    {
        (float Top, float Bottom, int Coverage)[] cases = [(4, 12, 255), (4 + (1 / 64f), 12, 191), (4.25f, 12, 191), (4.26f, 12, 128), (0, 4 + (1 / 64f), 64), (0, 4.25f, 64), (0, 4.26f, 128)];
        foreach ((float top, float bottom, int coverage) in cases)
        {
            byte[,] c = Coverage(GdiPlus, 4, 16, canvas => canvas.FillRect(RectF.FromEdges(0, top, 4, bottom), White));
            Assert.Equal(coverage, c[1, 4]);
        }
    }

    /// <summary>A corner's coverage is its columns times its rows over 32, rounded: GDI+'s values from its corner probe.</summary>
    [Theory]
    [InlineData(1, 1, 167)]
    [InlineData(5, 1, 120)]
    [InlineData(1, 9, 56)]
    [InlineData(13, 9, 8)]
    [InlineData(7, 5, 64)]
    public void CornersCountSamples(int i, int j, int coverage)
    {
        byte[,] c = Coverage(GdiPlus, 16, 16, canvas => canvas.FillRect(new RectF(4 + (i / 16f), 4 + (j / 16f), 8, 8), White));
        Assert.Equal(coverage, c[4, 4]);
    }

    /// <summary>The sampled fill gives the same pixels in one band or many.</summary>
    [Fact]
    public void BandsAreBitIdentical()
    {
        Path star = Path.Star(new Vector2(300.3f, 240.7f), 230, 60, 9, 7);
        byte[]? first = null;
        foreach (int bands in new[] { 1, 3, 8, 37 })
        {
            using Raster<Rgba8> raster = Raster.Create(640, 480, new Rgba8(0, 0, 0, 255));
            using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas(GdiPlus))
            {
                canvas.ForcedBands = bands;
                canvas.FillPath(star, White);
                canvas.StrokePath(Path.Circle(new Vector2(320, 240), 200), new Stroke(0.75f), White);
            }

            byte[] bytes = Bytes(raster);
            first ??= bytes;
            Assert.Equal(first, bytes);
        }
    }

    /// <summary>PixelOffset.None moves paths half a pixel right and down, and leaves images drawn 1:1 where they are.</summary>
    [Fact]
    public void PixelOffsetNoneMovesPathsNotImages()
    {
        var none = new CanvasOptions { PixelOffset = PixelOffset.None };
        byte[,] moved = Coverage(none, 24, 24, canvas => canvas.FillPath(Path.Circle(new Vector2(10.2f, 11.7f), 6.3f), White));
        byte[,] shifted = Coverage(null, 24, 24, canvas => canvas.FillPath(Path.Circle(new Vector2(10.7f, 12.2f), 6.3f), White));
        Assert.Equal(shifted, moved);

        using Raster<Rgba8> image = Raster.Create(4, 4, new Rgba8(255, 255, 255, 255));
        byte[,] imageNone = Coverage(none, 16, 16, canvas => canvas.DrawImage(image, new RectF(3.25f, 5, 4, 4)));
        byte[,] imageHalf = Coverage(null, 16, 16, canvas => canvas.DrawImage(image, new RectF(3.25f, 5, 4, 4)));
        Assert.Equal(imageHalf, imageNone);
    }

    /// <summary>
    /// With PixelOffset.None a scaled image sits on GDI+'s grid, source pixel centres on destination pixel centres: an impulse at
    /// source column 16 enlarged 4× peaks at destination column 64 exactly, as GDI+'s DrawImage measured (64 128 191 255 191 128 64).
    /// </summary>
    [Fact]
    public void PixelOffsetNonePutsScaledImagesOnGdiPlusGrid()
    {
        using Raster<Rgba8> image = Raster.Create(32, 8, new Rgba8(0, 0, 0, 255));
        for (int y = 0; y < 8; y++)
        {
            image.SetPixel(16, y, Rgba8.White);
        }

        var sampling = new Operations.SamplingOptions { Filter = Operations.ResampleFilter.GdiPlusBilinear, Edges = Operations.EdgeMode.Mirror };
        byte[,] none = Coverage(new CanvasOptions { PixelOffset = PixelOffset.None }, 128, 8, canvas => canvas.DrawImage(image, new RectF(0, 0, 32, 8), new RectF(0, 0, 128, 32), sampling));
        int[] gdiPlus = [64, 128, 191, 255, 191, 128, 64];
        for (int i = 0; i < gdiPlus.Length; i++)
        {
            Assert.InRange(none[61 + i, 2], gdiPlus[i] - 1, gdiPlus[i] + 1);
        }

        // Egami's own grid puts the peak between columns 65 and 66.
        byte[,] half = Coverage(null, 128, 8, canvas => canvas.DrawImage(image, new RectF(0, 0, 32, 8), new RectF(0, 0, 128, 32), sampling));
        Assert.Equal(half[65, 2], half[66, 2]);
        Assert.True(half[64, 2] < 255);
    }

    /// <summary>Like GDI+'s grid-fitted text, hinted text does not move with PixelOffset.None; unhinted text does.</summary>
    [Fact]
    public void PixelOffsetNoneMovesOnlyUnhintedText()
    {
        Typeface face = TextTests.Noto.Value;
        var none = new CanvasOptions { PixelOffset = PixelOffset.None };
        var hinted = new Font(face, 16) { Hinting = FontHinting.Full };
        Assert.Equal(
            Coverage(null, 48, 24, canvas => canvas.DrawText("Hg", new Vector2(4.3f, 17), hinted, Rgba8.White)),
            Coverage(none, 48, 24, canvas => canvas.DrawText("Hg", new Vector2(4.3f, 17), hinted, Rgba8.White)));

        var plain = new Font(face, 16) { Hinting = FontHinting.None, Subpixel = true };
        Assert.Equal(
            Coverage(null, 48, 24, canvas => canvas.DrawText("Hg", new Vector2(4.8f, 17.5f), plain, Rgba8.White)),
            Coverage(none, 48, 24, canvas => canvas.DrawText("Hg", new Vector2(4.3f, 17), plain, Rgba8.White)));
    }

    /// <summary>Pens to 1.5 device pixels draw as GDI+'s thin line, one pixel thick with pointed ends; wider pens keep their width.</summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    [InlineData(1)]
    [InlineData(1.5f)]
    public void ThinPensAreOnePixelThick(float width)
    {
        foreach (LineCap cap in new[] { LineCap.Butt, LineCap.Round, LineCap.Square })
        {
            byte[,] c = Coverage(GdiPlus, 32, 20, canvas => canvas.DrawLine(new Vector2(8, 10.5f), new Vector2(20, 10.5f), new Stroke(width) { Cap = cap }, White));
            Assert.Equal(255, c[14, 10]);
            Assert.Equal(0, c[14, 9]);
            Assert.Equal(64, c[7, 10]);
            Assert.Equal(64, c[20, 10]);
        }

        // At y = 10, the band [9.5, 10.5) covers half of rows 9 and 10.
        byte[,] mid = Coverage(GdiPlus, 32, 20, canvas => canvas.DrawLine(new Vector2(4, 10), new Vector2(28, 10), new Stroke(width), White));
        Assert.Equal(128, mid[16, 9]);
        Assert.Equal(128, mid[16, 10]);
    }

    /// <summary>A thin slanted line covers one pixel per column in all, and a pen just over 1.5 px has its own width.</summary>
    [Fact]
    public void ThinPenSlantsAndThreshold()
    {
        byte[,] slant = Coverage(GdiPlus, 32, 20, canvas => canvas.DrawLine(new Vector2(4, 4), new Vector2(28, 12), new Stroke(0.5f), White));
        for (int x = 8; x <= 20; x++)
        {
            int sum = 0;
            for (int y = 0; y < 20; y++)
            {
                sum += slant[x, y];
            }

            Assert.Equal(255, sum);
        }

        byte[,] wide = Coverage(GdiPlus, 32, 20, canvas => canvas.DrawLine(new Vector2(4, 10.375f), new Vector2(28, 10.375f), new Stroke(1.53f), White));
        Assert.Equal(64, wide[16, 9]);
        Assert.Equal(255, wide[16, 10]);
        Assert.Equal(64, wide[16, 11]);
    }

    /// <summary>The defaults are Egami's own: half-pixel centres and exact coverage.</summary>
    [Fact]
    public void DefaultsAreExactAndHalf()
    {
        Assert.Equal(PixelOffset.Half, CanvasOptions.Default.PixelOffset);
        Assert.Equal(Antialiasing.Exact, CanvasOptions.Default.Antialiasing);
        Assert.Equal(PixelOffset.None, CanvasOptions.GdiPlusClearType.PixelOffset);
        Assert.Equal(Antialiasing.GdiPlus, CanvasOptions.GdiPlusClearType.Antialiasing);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasOptions { PixelOffset = (PixelOffset)7 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasOptions { Antialiasing = (Antialiasing)7 });
    }

    private static readonly SolidPaint White = new(Rgba8.White);

    private static byte[,] Coverage(CanvasOptions? options, int width, int height, Action<RasterCanvas<Rgba8>> draw)
    {
        using Raster<Rgba8> raster = Raster.Create(width, height, new Rgba8(0, 0, 0, 255));
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas(options))
        {
            draw(canvas);
        }

        byte[,] c = new byte[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                c[x, y] = raster.GetPixel(x, y).R;
            }
        }

        return c;
    }

    private static byte[] Bytes(Raster<Rgba8> raster)
    {
        byte[] bytes = new byte[raster.Width * raster.Height * 4];
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                Rgba8 p = raster.GetPixel(x, y);
                int i = ((y * raster.Width) + x) * 4;
                (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]) = (p.R, p.G, p.B, p.A);
            }
        }

        return bytes;
    }
}
