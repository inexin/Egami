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

    [Fact]
    public void MakeCode128()
    {
        #region barcode-code128
        // The shortest mix of code sets A, B and C; the digits go in pairs.
        Barcode code = Barcode.Code128("ORDER-1180042", new Code128Options { Height = 40 });
        using Raster<Gray8> image = code.ToRaster(pixelsPerModule: 2);
        #endregion
        Assert.Equal("ORDER-1180042", code.HumanReadableText);
    }

    [Fact]
    public void MakeGs1128()
    {
        #region barcode-gs1
        // Each element string is checked: the AI, the length, the GTIN's check digit, the date.
        IReadOnlyList<Gs1ElementString> elements = Gs1.Parse("(01)09501101530003(17)261231(10)AB12");
        Barcode label = Barcode.Gs1_128(elements);

        // Mistakes say what is wrong.
        if (!Gs1.TryParse("(01)09501101530004", out _))
        {
            Console.WriteLine("(01) has a wrong check digit");
        }
        #endregion
        Assert.Equal("(01)09501101530003(17)261231(10)AB12", label.HumanReadableText);
    }

    [Fact]
    public void MakeCode39()
    {
        #region barcode-code39
        // Capitals, digits and a few symbols; lowercase switches to Full ASCII on its own.
        Barcode part = Barcode.Code39("PART-0042", new Code39Options { CheckDigit = true });
        Console.WriteLine(part.HumanReadableText); // PART-0042 and its mod 43 check character
        #endregion
        Assert.StartsWith("PART-0042", part.HumanReadableText, StringComparison.Ordinal);
    }

    [Fact]
    public void MakeItf14()
    {
        #region barcode-itf
        // A GTIN-14 on a carton: the check digit is added, and bearer bars frame the symbol.
        Barcode carton = Barcode.Itf14("1540014128876");
        using Raster<Gray8> image = carton.ToRaster(pixelsPerModule: 3);

        // Plain Interleaved 2 of 5 with a 2:1 wide ratio and no bearers.
        Barcode itf = Barcode.Itf("0123456789", new ItfOptions { WideRatio = 2 });
        #endregion
        Assert.Equal("15400141288763", carton.HumanReadableText);
        Assert.Equal(BarcodeFormat.Itf, itf.Format);
    }

    [Fact]
    public void MakeEan13()
    {
        Font font = new(Typeface.FromFile(FontFile), 9);
        #region barcode-ean
        // A book: the ISBN as EAN-13, its check digit added, and the price as an EAN-5 add-on.
        Barcode book = Barcode.Ean13("978020137962", new EanUpcOptions { AddOn = "52495" });

        // Fitted into a rectangle, with the digits between the guard bars.
        using Raster<Rgba8> label = Raster.Create(400, 200, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = label.CreateCanvas())
        {
            canvas.DrawBarcode(book, new RectF(10, 10, 380, 180), Rgba8.Black, new BarcodeDrawOptions { Font = font });
        }

        // A UPC-A number written in its short UPC-E form.
        Barcode small = Barcode.UpcE("042100005264");
        #endregion
        Assert.Equal("9780201379624 52495", book.HumanReadableText);
        Assert.Equal("04252614", small.HumanReadableText);
    }
}
