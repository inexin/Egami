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
        Barcode label = Barcode.Gs1Code128(elements);

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

    [Fact]
    public void MakeQr()
    {
        #region barcode-qr
        // Level M, raised to Q or H when that fits in the same size; segments chosen for the fewest bits.
        Barcode link = Barcode.Qr("https://inexin.github.io/Egami/");

        // A fixed size and level, for a layout that must not change.
        Barcode fixedSize = Barcode.Qr("ORDER-1180042", new QrOptions { Version = 3, ErrorCorrection = QrErrorCorrection.H });

        // Bytes as they are, and GS1 element strings with FNC1.
        Barcode bytes = Barcode.Qr([0x45, 0x67, 0x61, 0x6D, 0x69]);
        Barcode gs1 = Barcode.Qr(Gs1.Parse("(01)09501101530003(10)AB12"));

        using Raster<Gray8> image = link.ToRaster(pixelsPerModule: 4);
        #endregion
        Assert.Equal(29, fixedSize.Modules.Width);
        Assert.Equal("(01)09501101530003(10)AB12", gs1.HumanReadableText);
        Assert.Equal(BarcodeFormat.Qr, bytes.Format);
    }

    [Fact]
    public void MakeDataMatrix()
    {
        #region barcode-datamatrix
        // Square by default; the encodation modes are switched for the fewest codewords.
        Barcode part = Barcode.DataMatrix("PN 4711-0815 / LOT 2026-10");

        // A narrow label: rectangular sizes, the DMRE ones too.
        Barcode narrow = Barcode.DataMatrix("PN 4711-0815 / LOT 2026-10", new DataMatrixOptions
        {
            Shape = DataMatrixShape.Rectangle,
            AllowDmre = true,
        });

        // GS1 Data Matrix, as on medicine packs.
        Barcode pack = Barcode.DataMatrix(Gs1.Parse("(01)09501101530003(17)261231(10)AB12(21)XYZ"));
        #endregion
        Assert.Equal(part.Modules.Width, part.Modules.Height);
        Assert.True(narrow.Modules.Width > narrow.Modules.Height);
        Assert.Equal(BarcodeFormat.DataMatrix, pack.Format);
    }

    [Fact]
    public void MakePdf417()
    {
        #region barcode-pdf417
        // About three times as wide as tall, error level from the standard's table for the amount of data.
        Barcode ticket = Barcode.Pdf417("PASSENGER DOE/JANE  FLIGHT EG1234  SEAT 12A  GATE B17");

        // Six data columns and error level 5, or the compact form for a narrow space.
        Barcode wide = Barcode.Pdf417("PASSENGER DOE/JANE", new Pdf417Options { Columns = 6, ErrorLevel = 5 });
        Barcode compact = Barcode.Pdf417("PASSENGER DOE/JANE", new Pdf417Options { Compact = true });
        #endregion
        Assert.True(ticket.Modules.Width > ticket.Modules.Height);
        Assert.True(compact.Modules.Width < wide.Modules.Width);
    }

    [Fact]
    public void MakeAztec()
    {
        #region barcode-aztec
        // The smallest compact or full-range symbol with at least 23 % error correction; no quiet zone needed.
        Barcode ticket = Barcode.Aztec("EG1234 BER-MUC 2026-10-19 SEAT 12A DOE/JANE");

        // More error correction for a ticket that gets folded, or a fixed compact size.
        Barcode robust = Barcode.Aztec("EG1234 BER-MUC", new AztecOptions { ErrorCorrectionPercent = 50 });
        Barcode compact = Barcode.Aztec("EG1234", new AztecOptions { Compact = true, Layers = 2 });

        // A rune: one value from 0 to 255 in an 11 x 11 core.
        Barcode rune = Barcode.AztecRune(42);
        #endregion
        Assert.Equal(0, ticket.QuietZone);
        Assert.Equal(19, compact.Modules.Width);
        Assert.Equal(11, rune.Modules.Width);
        Assert.True(robust.Modules.Width >= 15);
    }

    [Fact]
    public void CreateFromData()
    {
        #region barcode-create
        // Labels from a table that stores each barcode's format with its data.
        (BarcodeFormat Format, string Data)[] rows =
        [
            (BarcodeFormat.Ean13, "590123412345"),
            (BarcodeFormat.Code128, "SHIP-0042"),
            (BarcodeFormat.Qr, "https://inexin.github.io/Egami/"),
        ];

        foreach ((BarcodeFormat format, string data) in rows)
        {
            Barcode barcode = Barcode.Create(format, data);
            using Raster<Gray8> image = barcode.ToRaster(pixelsPerModule: 3);
        }
        #endregion
        Assert.Equal(BarcodeFormat.Qr, Barcode.Create(BarcodeFormat.Qr, "x").Format);
    }

    [Fact]
    public void LabelWithQrAndText()
    {
        Font font = new(Typeface.FromFile(FontFile), 18);
        #region barcode-label
        // A 600 x 300 label: a QR code on the left, the address beside it.
        using Raster<Rgba8> label = Raster.Create(600, 300, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = label.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.Qr("https://example.com/track/4711"), new RectF(20, 20, 260, 260), Rgba8.Black);
            canvas.DrawText("Parcel 4711", new Vector2(300, 80), font, Rgba8.Black);
            canvas.DrawText("Track at example.com", new Vector2(300, 120), font, Rgba8.Black);
        }
        #endregion
        Assert.Equal(Rgba8.White, label.GetPixel(10, 10));
    }

    [Fact]
    public void SaveAsSvg()
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.svg");
        try
        {
            #region barcode-svg
            // The symbol as one path, 4 units a module, drawn on Egami.Svg's canvas over a white quiet zone.
            Barcode barcode = Barcode.DataMatrix("PN 4711-0815");
            Path path = barcode.ToPath(moduleSize: 4);
            float width = (barcode.Modules.Width + (2 * barcode.QuietZone)) * 4, height = (barcode.Modules.Height + (2 * barcode.QuietZone)) * 4;
            using (FileStream stream = File.Create(file))
            using (var canvas = new Egami.Svg.SvgCanvas(stream, width, height))
            {
                canvas.FillRect(new RectF(0, 0, width, height), Rgba8.White);
                canvas.FillPath(path, Rgba8.Black);
            }
            #endregion
            Assert.Contains("<path", File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ShippingLabel()
    {
        Font font = new(Typeface.FromFile(FontFile), 8);
        var recorder = new PictureRecorder(new RectF(0, 0, 288, 432));
        Canvas page = recorder;
        #region barcode-shipping
        // A 4 x 6 inch label in points: an SSCC as GS1-128 at 0.5 mm a module with its text, and a GS1 Data Matrix.
        float module = 0.5f / 25.4f * 72;
        Barcode sscc = Barcode.Gs1Code128("(00)106141411234567897");
        page.DrawBarcode(sscc, new Vector2(18, 300), module, Rgba8.Black, new BarcodeDrawOptions { Font = font });
        page.DrawBarcode(Barcode.DataMatrix(Gs1.Parse("(01)09501101530003(10)AB12")), new RectF(18, 18, 100, 100), Rgba8.Black);
        #endregion
        Assert.True(recorder.Finish().CallCount >= 3);
    }

    [Fact]
    public void ReadBarcodes()
    {
        using Raster<Gray8> scan = Barcode.Qr("https://inexin.github.io/Egami/").ToRaster(pixelsPerModule: 4);
        #region barcode-read
        // Every barcode in the image, of any format.
        foreach (BarcodeResult result in scan.ReadBarcodes())
        {
            Console.WriteLine($"{result.Format}: {result.Text} at {result.Corners[0]}");
        }
        #endregion
        Assert.Single(scan.ReadBarcodes());
    }

    [Fact]
    public void ReadWithOptions()
    {
        using Raster<Gray8> label = Barcode.DataMatrix(Gs1.Parse("(01)09501101530003(10)AB12")).ToRaster(pixelsPerModule: 4);
        #region barcode-read-options
        // Only the formats you expect, searched harder; a rendered barcode can skip the search.
        BarcodeResult? result = label.ReadBarcode(new BarcodeReaderOptions
        {
            Formats = BarcodeFormat.DataMatrix | BarcodeFormat.Qr,
            Effort = 7,
        });

        if (result?.Gs1 is { } elements)
        {
            foreach (Gs1ElementString element in elements)
            {
                Console.WriteLine($"{element.Title}: {element.Value}");
            }
        }

        BarcodeResult? rendered = label.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true });
        #endregion
        Assert.Equal("(01)09501101530003(10)AB12", result?.Text);
        Assert.NotNull(rendered);
    }

    [Fact]
    public void ReadMany()
    {
        using Raster<Gray8> page = Raster.Create(900, 400, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = page.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.Qr("Parcel 1"), new RectF(30, 60, 260, 260), Rgba8.Black);
            canvas.DrawBarcode(Barcode.DataMatrix("Parcel 2"), new RectF(330, 70, 240, 240), Rgba8.Black);
            canvas.DrawBarcode(Barcode.Aztec("Parcel 3"), new RectF(610, 60, 260, 260), Rgba8.Black);
        }

        #region barcode-read-many
        // All symbols on a page, top to bottom and left to right, each once; at most four threads.
        IReadOnlyList<BarcodeResult> parcels = page.ReadBarcodes(new BarcodeReaderOptions
        {
            Context = EgamiContext.Default with { MaxParallelism = 4 },
        });
        foreach (BarcodeResult parcel in parcels)
        {
            Console.WriteLine($"{parcel.Text} (inverted: {parcel.Inverted}, mirrored: {parcel.Mirrored})");
        }
        #endregion
        Assert.Equal(["Parcel 1", "Parcel 2", "Parcel 3"], parcels.Select(p => p.Text));
    }

    [Fact]
    public void ReadLuminance()
    {
        using Raster<Gray8> frame = Barcode.Qr("Y plane").ToRaster(pixelsPerModule: 4);
        int width = frame.Width, height = frame.Height, stride = width + 32;
        byte[] yPlane = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Row(y)).CopyTo(yPlane.AsSpan(y * stride));
        }

        #region barcode-read-luminance
        // A camera frame's Y plane (8-bit luminance, rows padded to the stride), read in place.
        BarcodeResult? code = BarcodeReader.ReadBarcode(yPlane, width, height, stride);
        #endregion
        Assert.Equal("Y plane", code?.Text);
    }

    [Fact]
    public void ReadGs1()
    {
        using Raster<Gray8> label = Barcode.Gs1Code128("(01)09501101530003(17)261231(10)AB12").ToRaster(pixelsPerModule: 3);
        #region barcode-read-gs1
        // A GS1-128 label: the element strings by AI, and the text with the AIs in parentheses.
        BarcodeResult? code = label.ReadBarcode(new BarcodeReaderOptions { Formats = BarcodeFormat.Code128 });
        string? expiry = code?.Gs1?.FirstOrDefault(e => e.Ai == "17")?.Value;   // "261231"
        string? batch = code?.Gs1?.FirstOrDefault(e => e.Ai == "10")?.Value;    // "AB12"
        #endregion
        Assert.Equal("261231", expiry);
        Assert.Equal("AB12", batch);
        Assert.Equal("(01)09501101530003(17)261231(10)AB12", code?.Text);
    }
}
