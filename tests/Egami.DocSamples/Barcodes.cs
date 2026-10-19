using System.Numerics;

using Egami.Barcodes;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Barcodes
{
    [Fact]
    public void ReadTheModules()
    {
        #region barcode-modules
        // A 2D grid, row by row from the top; true is a dark module.
        BarcodeModules grid = BarcodeModules.Create(3, 3,
        [
            true, false, true,
            false, true, false,
            true, false, true,
        ]);

        // A linear symbol: bar and space widths in modules, starting and ending with a bar.
        BarcodeModules bars = BarcodeModules.CreateLinear([2, 1, 1, 1, 3], height: 20);

        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                Console.Write(grid[x, y] ? '#' : '.');
            }

            Console.WriteLine();
        }

        Console.WriteLine($"{bars.Width} modules wide, bars {string.Join(' ', bars.Runs.ToArray())}");
        #endregion
        Assert.Equal(8, bars.Width);
        Assert.True(grid[1, 1]);
    }

    private static string FontFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf");

    private static BarcodeModules Grid => BarcodeModules.Create(3, 3, [true, true, false, false, true, false, true, false, true]);

    [Fact]
    public void WrapModules()
    {
        BarcodeModules modules = Grid;
        #region barcode-from-modules
        // Modules from another encoder become a barcode with the quiet zone of their standard: 4 modules for QR.
        Barcode qr = Barcode.FromModules(BarcodeFormat.Qr, modules);
        Console.WriteLine($"{qr.Format}: {qr.Modules.Width} modules plus {qr.QuietZone} on each side");
        #endregion
        Assert.Equal(4, qr.QuietZone);
    }

    [Fact]
    public void DrawOnACanvas()
    {
        Barcode qr = Barcode.FromModules(BarcodeFormat.Qr, Grid);
        #region barcode-draw
        using Raster<Rgba8> label = Raster.Create(300, 200, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = label.CreateCanvas())
        {
            // Fitted into the rectangle and centered, quiet zone inside it; on a raster, modules snap to whole pixels.
            canvas.DrawBarcode(qr, new RectF(20, 20, 160, 160), Rgba8.Black);
        }
        #endregion
        Assert.Equal(Rgba8.White, label.GetPixel(10, 10));
    }

    [Fact]
    public void PrintAtAModuleSize()
    {
        Barcode code = Barcode.FromModules(BarcodeFormat.Code128, BarcodeModules.CreateLinear([2, 1, 1, 2, 3, 1, 1], 40), "ORDER-118");
        Font font = new(Typeface.FromFile(FontFile), 9);
        var recorder = new PictureRecorder(new RectF(0, 0, 400, 100));
        Canvas page = recorder;
        #region barcode-print
        // A 0.33 mm module (the X dimension) in points, with the text under the bars and 0.05 mm taken off each bar
        // for ink spread. The quiet zone's top-left corner goes at (36, 36).
        float module = 0.33f / 25.4f * 72;
        page.DrawBarcode(code, new Vector2(36, 36), module, Rgba8.Black, new BarcodeDrawOptions
        {
            Font = font,
            BarWidthReduction = 0.05f / 25.4f * 72,
        });
        #endregion
        Assert.Equal(2, recorder.Finish().CallCount);
    }

    [Fact]
    public void BuildAPath()
    {
        Barcode qr = Barcode.FromModules(BarcodeFormat.Qr, Grid);
        #region barcode-path
        // One path, a rectangle per run of dark modules; the quiet zone's corner is at the origin.
        Path path = qr.ToPath(moduleSize: 4);
        string svg = path.ToSvg();
        #endregion
        Assert.False(string.IsNullOrEmpty(svg));
    }

    [Fact]
    public void RenderToARaster()
    {
        Barcode qr = Barcode.FromModules(BarcodeFormat.Qr, Grid);
        #region barcode-raster
        // Black on white, 4 pixels a module, quiet zone included.
        using Raster<Gray8> image = qr.ToRaster(pixelsPerModule: 4);
        #endregion
        Assert.Equal(44, image.Width);
    }
}
