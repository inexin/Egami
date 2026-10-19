using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Barcodes.Tests;

/// <summary>Paths, rasters and canvas drawing of hand-built symbols; golden images are written as rows of # (black) and . (white).</summary>
public class OutputTests
{
    private static readonly BarcodeModules s_grid = BarcodeModules.Create(3, 3,
    [
        true, true, false,
        false, true, false,
        true, false, true,
    ]);

    private static readonly BarcodeModules s_bars = BarcodeModules.CreateLinear([1, 1, 2], 2);

    private static Font TestFont => new(Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf")), 10);

    [Fact]
    public void QuietZonesFollowTheStandards()
    {
        BarcodeModules linear = BarcodeModules.CreateLinear([1], 1);
        Assert.Equal(4, Barcode.FromModules(BarcodeFormat.Qr, s_grid).QuietZone);
        Assert.Equal(1, Barcode.FromModules(BarcodeFormat.DataMatrix, s_grid).QuietZone);
        Assert.Equal(2, Barcode.FromModules(BarcodeFormat.Pdf417, s_grid).QuietZone);
        Assert.Equal(0, Barcode.FromModules(BarcodeFormat.Aztec, s_grid).QuietZone);
        foreach (BarcodeFormat format in new[] { BarcodeFormat.Code128, BarcodeFormat.Code39, BarcodeFormat.Itf, BarcodeFormat.Ean13, BarcodeFormat.Ean8, BarcodeFormat.UpcA, BarcodeFormat.UpcE })
        {
            Assert.Equal(10, Barcode.FromModules(format, linear).QuietZone);
        }

        Assert.Throws<ArgumentException>(() => Barcode.FromModules(BarcodeFormat.Qr, linear));
        Assert.Throws<ArgumentException>(() => Barcode.FromModules(BarcodeFormat.Ean13, s_grid));
        Assert.Throws<ArgumentOutOfRangeException>(() => Barcode.FromModules((BarcodeFormat)99, s_grid));
    }

    [Fact]
    public void ToRasterDrawsWholePixelModulesInTheQuietZone()
    {
        using Raster<Gray8> raster = Barcode.FromModules(BarcodeFormat.DataMatrix, s_grid).ToRaster(2);
        AssertImage(raster,
            "..........",
            "..........",
            "..####....",
            "..####....",
            "....##....",
            "....##....",
            "..##..##..",
            "..##..##..",
            "..........",
            "..........");
    }

    [Fact]
    public void ToRasterDrawsLinearBarsWithSideQuietZonesOnly()
    {
        using Raster<Gray8> raster = Barcode.FromModules(BarcodeFormat.Code128, s_bars).ToRaster(1, new BarcodeDrawOptions { QuietZone = false });
        AssertImage(raster, "#.##", "#.##");
        using Raster<Gray8> zoned = Barcode.FromModules(BarcodeFormat.Code128, s_bars).ToRaster(1);
        Assert.Equal((24, 2), (zoned.Width, zoned.Height));
        Assert.Equal(0, zoned.GetPixel(10, 0).L);
        Assert.Equal(255, zoned.GetPixel(9, 0).L);
    }

    [Fact]
    public void BarWidthReductionTakesWholePixelsFromEachSide()
    {
        using Raster<Gray8> raster = Barcode.FromModules(BarcodeFormat.Aztec, s_grid).ToRaster(4, new BarcodeDrawOptions { BarWidthReduction = 2 });
        AssertImage(raster,
            "............",
            ".######.....",
            ".######.....",
            "............",
            "............",
            ".....##.....",
            ".....##.....",
            "............",
            "............",
            ".##......##.",
            ".##......##.",
            "............");
    }

    [Fact]
    public void DrawBarcodeSnapsModulesToWholePixels()
    {
        // 10 units for 3 modules would be 3.33 pixels a module; it snaps to 3 and centers on the pixel grid.
        using Raster<Gray8> raster = Raster.Create(12, 12, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.FromModules(BarcodeFormat.Aztec, s_grid), new RectF(0.3f, 0.2f, 10, 10), Rgba8.Black);
        }

        AssertImage(raster,
            "............",
            ".######.....",
            ".######.....",
            ".######.....",
            "....###.....",
            "....###.....",
            "....###.....",
            ".###...###..",
            ".###...###..",
            ".###...###..",
            "............",
            "............");
    }

    [Fact]
    public void DrawBarcodeSnapsInDevicePixelsUnderAScale()
    {
        // Twice the scale: 5 units for 4 modules (with the quiet zone) is 2.5 device pixels a module, snapped to 2.
        using Raster<Gray8> raster = Raster.Create(10, 10, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.Scale(2);
            canvas.DrawBarcode(Barcode.FromModules(BarcodeFormat.DataMatrix, BarcodeModules.Create(2, 2, [true, false, false, true])), new RectF(0, 0, 5, 5), Rgba8.Black);
        }

        AssertImage(raster,
            "..........",
            "..........",
            "..........",
            "...##.....",
            "...##.....",
            ".....##...",
            ".....##...",
            "..........",
            "..........",
            "..........");
    }

    [Fact]
    public void DrawBarcodeStretchesLinearBarsToTheHeight()
    {
        using Raster<Gray8> raster = Raster.Create(48, 5, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.FromModules(BarcodeFormat.Code128, s_bars), new RectF(0, 0, 48, 5), Rgba8.Black);
        }

        string row = new string('.', 20) + "##..####" + new string('.', 20);
        AssertImage(raster, row, row, row, row, row);
    }

    [Fact]
    public void DrawBarcodeWithAModuleSizeRoundsToWholePixels()
    {
        using Raster<Gray8> raster = Raster.Create(8, 8, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.FromModules(BarcodeFormat.Aztec, s_grid), new Vector2(0.6f, 1.4f), 2.2f, Rgba8.Black);
        }

        AssertImage(raster,
            "........",
            ".####...",
            ".####...",
            "...##...",
            "...##...",
            ".##..##.",
            ".##..##.",
            "........");
    }

    [Fact]
    public void ARotatedCanvasIsNotSnapped()
    {
        using Raster<Gray8> raster = Raster.Create(20, 20, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.Rotate(10, new Vector2(10, 10));
            canvas.DrawBarcode(Barcode.FromModules(BarcodeFormat.Aztec, s_grid), new RectF(4, 4, 12, 12), Rgba8.Black);
        }

        bool gray = false;
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                gray |= raster.GetPixel(x, y).L is > 20 and < 235;
            }
        }

        Assert.True(gray);
    }

    [Fact]
    public void ARecordingHoldsOnePathPerSymbol()
    {
        var recorder = new PictureRecorder(new RectF(0, 0, 100, 100));
        recorder.DrawBarcode(Barcode.FromModules(BarcodeFormat.Qr, s_grid), new RectF(0, 0, 100, 100), Rgba8.Black);
        recorder.DrawBarcode(Barcode.FromModules(BarcodeFormat.Code128, s_bars), new RectF(0, 0, 100, 30), Rgba8.Black);
        Picture picture = recorder.Finish();
        Assert.Equal(2, picture.CallCount);
    }

    [Fact]
    public void ToPathMergesRunsAndStartsAtTheQuietZone()
    {
        Path path = Barcode.FromModules(BarcodeFormat.DataMatrix, s_grid).ToPath(2);

        // Rows hold the runs ##., .#., #.#: four rectangles.
        Assert.Equal(4, path.Contours.Count);
        Assert.Equal(new RectF(2, 2, 6, 6), path.Bounds);
        Assert.True(path.Contains(new Vector2(4.5f, 3)));
        Assert.False(path.Contains(new Vector2(6.5f, 3)));

        Path bars = Barcode.FromModules(BarcodeFormat.Code128, s_bars).ToPath(1, new BarcodeDrawOptions { BarWidthReduction = 0.5f });
        Assert.Equal(2, bars.Contours.Count);
        Assert.Equal(new RectF(10.25f, 0, 3.5f, 2), bars.Bounds);
    }

    [Fact]
    public void TextIsDrawnOnlyWithAFont()
    {
        Barcode barcode = Barcode.FromModules(BarcodeFormat.Code128, s_bars, "12");
        using Raster<Gray8> plain = barcode.ToRaster(4);
        using Raster<Gray8> text = barcode.ToRaster(4, new BarcodeDrawOptions { Font = TestFont });
        Assert.Equal(8, plain.Height);
        Assert.True(text.Height > plain.Height + 4);
        Assert.Equal(plain.Width, text.Width);
        Assert.Contains(Enumerable.Range(0, text.Width), x => text.GetPixel(x, text.Height - 4).L < 128);

        // Fitted, the bars give up the text's room.
        using Raster<Gray8> fitted = Raster.Create(96, 40, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = fitted.CreateCanvas())
        {
            canvas.DrawBarcode(barcode, new RectF(0, 0, 96, 40), Rgba8.Black, new BarcodeDrawOptions { Font = TestFont });
        }

        Assert.Equal(0, fitted.GetPixel(40, 0).L);
        Assert.Equal(255, fitted.GetPixel(40, 30).L);
        Assert.Contains(Enumerable.Range(0, 96), x => fitted.GetPixel(x, 36).L < 128);

        var recorder = new PictureRecorder(new RectF(0, 0, 100, 100));
        recorder.DrawBarcode(barcode, new RectF(0, 0, 100, 100), Rgba8.Black);
        Assert.Equal(1, recorder.Finish().CallCount);
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        Barcode barcode = Barcode.FromModules(BarcodeFormat.Qr, s_grid);
        Assert.Throws<ArgumentOutOfRangeException>(() => barcode.ToPath(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => barcode.ToRaster(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarcodeDrawOptions { BarWidthReduction = -1 });
        var recorder = new PictureRecorder(new RectF(0, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.DrawBarcode(barcode, Vector2.Zero, float.NaN, Rgba8.Black));

        // A rectangle with no room draws nothing.
        recorder.DrawBarcode(barcode, new RectF(0, 0, 0, 10), Rgba8.Black);
        Assert.Equal(0, recorder.Finish().CallCount);
    }

    private static void AssertImage(Raster<Gray8> raster, params string[] rows)
    {
        var actual = new StringBuilder();
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                byte l = raster.GetPixel(x, y).L;
                actual.Append(l switch { 0 => '#', 255 => '.', _ => '?' });
            }

            actual.Append('\n');
        }

        string[] got = actual.ToString().TrimEnd('\n').Split('\n');
        Assert.Equal(rows, got);
    }
}
