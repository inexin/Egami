using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>2D symbols in photo-like images: turned, in perspective, blurred, noisy, mirrored, several at once.</summary>
public class ReadMatrixTests
{
    public static TheoryData<string, float, float> QrCases => new()
    {
        { "short", 0, 0 },
        { "short", 30, 0 },
        { "short", 135, 0.15f },
        { "long", 10, 0.1f },
        { "long", 250, 0.2f },
        { "large", 5, 0.05f },
    };

    [Theory]
    [MemberData(nameof(QrCases))]
    public void ReadsQrPhotos(string kind, float degrees, float tilt)
    {
        string text = kind switch
        {
            "short" => "Egami",
            "long" => "https://inexin.github.io/Egami/v1/articles/guides/barcodes.html",
            _ => string.Concat(Enumerable.Repeat("Large QR Code with many modules. ", 12)),
        };
        Barcode qr = Barcode.Qr(text);
        int size = kind == "large" ? 1000 : 640;
        using Raster<Gray8> photo = Photos.Render(qr, size, size, Photos.Quad(new Vector2(size / 2f, size / 2f), size * 0.6f, size * 0.6f, degrees, tilt), seed: (int)degrees);
        Assert.Contains(photo.ReadBarcodes(), r => r.Format == BarcodeFormat.Qr && r.Text == text);
    }

    [Fact]
    public void MirroredQrReads()
    {
        Barcode qr = Barcode.Qr("Mirrored");
        Vector2[] quad = Photos.Quad(new Vector2(320, 240), 300, 300, 12);
        (quad[0], quad[1], quad[2], quad[3]) = (quad[1], quad[0], quad[3], quad[2]);
        using Raster<Gray8> photo = Photos.Render(qr, 640, 480, quad);
        BarcodeResult? result = photo.ReadBarcode(new BarcodeReaderOptions { Formats = BarcodeFormat.Qr });
        Assert.Equal("Mirrored", result?.Text);
        Assert.True(result?.Mirrored);
    }

    [Fact]
    public void SeveralQrCodesAreAllFound()
    {
        using Raster<Gray8> image = Raster.Create(900, 400, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = image.CreateCanvas())
        {
            for (int i = 0; i < 3; i++)
            {
                canvas.DrawBarcode(Barcode.Qr($"Code {i}"), new RectF(20 + (i * 300), 60, 260, 260), Rgba8.Black);
            }
        }

        Assert.Equal(["Code 0", "Code 1", "Code 2"], image.ReadBarcodes(new BarcodeReaderOptions { Formats = BarcodeFormat.Qr }).Select(r => r.Text));
    }

    [Fact]
    public void CornersFollowTheSymbol()
    {
        Barcode qr = Barcode.Qr("Corners", new QrOptions { Version = 3 });
        Vector2[] quad = Photos.Quad(new Vector2(320, 240), 290, 290, 0);
        using Raster<Gray8> photo = Photos.Render(qr, 640, 480, quad, blur: 0, noise: 0);
        BarcodeResult? result = photo.ReadBarcode(new BarcodeReaderOptions { Formats = BarcodeFormat.Qr });
        Assert.NotNull(result);

        // The quiet zone is 4 of the 37 modules across the quad on each side.
        float inset = 290f * 4 / 37;
        Assert.InRange(result.Corners[0].X, quad[0].X + inset - 4, quad[0].X + inset + 4);
        Assert.InRange(result.Corners[2].Y, quad[2].Y - inset - 4, quad[2].Y - inset + 4);
    }

    public static TheoryData<string, float, float> DataMatrixCases => new()
    {
        { "square", 0, 0 },
        { "square", 20, 0.1f },
        { "square", 200, 0.15f },
        { "rectangle", 90, 0.05f },
        { "large", 7, 0.05f },
        { "dmre", 0, 0 },
        { "dmre", 175, 0 },
        { "dmre", 175, 0.08f },
    };

    [Theory]
    [MemberData(nameof(DataMatrixCases))]
    public void ReadsDataMatrixPhotos(string kind, float degrees, float tilt)
    {
        (Barcode barcode, string text) = kind switch
        {
            "square" => (Barcode.DataMatrix("Data Matrix 4711"), "Data Matrix 4711"),
            "rectangle" => (Barcode.DataMatrix("RECT-12", new DataMatrixOptions { Shape = DataMatrixShape.Rectangle }), "RECT-12"),
            "dmre" => (Barcode.DataMatrix("DMRE 0123456789 ABCDEFGHIJ", new DataMatrixOptions { Shape = DataMatrixShape.Rectangle, AllowDmre = true }), "DMRE 0123456789 ABCDEFGHIJ"),
            _ => (Barcode.DataMatrix(new string('A', 300)), new string('A', 300)),
        };
        float width = 360, height = width * (barcode.Modules.Height + 2) / (barcode.Modules.Width + 2);
        using Raster<Gray8> photo = Photos.Render(barcode, 640, 640, Photos.Quad(new Vector2(320, 320), width, height, degrees, tilt), seed: (int)degrees + 11);
        Assert.Contains(photo.ReadBarcodes(), r => r.Format == BarcodeFormat.DataMatrix && r.Text == text);
    }

    [Fact]
    public void SeveralDataMatrixSymbolsAreAllFound()
    {
        using Raster<Gray8> image = Raster.Create(800, 300, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = image.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.DataMatrix("FIRST"), new RectF(40, 40, 200, 200), Rgba8.Black);
            canvas.DrawBarcode(Barcode.DataMatrix("SECOND"), new RectF(500, 60, 180, 180), Rgba8.Black);
        }

        Assert.Equal(["FIRST", "SECOND"], image.ReadBarcodes(new BarcodeReaderOptions { Formats = BarcodeFormat.DataMatrix }).Select(r => r.Text));
    }

    public static TheoryData<string, float, float> Pdf417Cases => new()
    {
        { "short", 0, 0 },
        { "short", 12, 0.05f },
        { "short", 180, 0 },
        { "long", 95, 0.1f },
        { "long", 230, 0.05f },
        { "compact", 30, 0 },
    };

    [Theory]
    [MemberData(nameof(Pdf417Cases))]
    public void ReadsPdf417Photos(string kind, float degrees, float tilt)
    {
        string text = kind == "long" ? string.Concat(Enumerable.Repeat("PDF417 stacked barcode 0123456789. ", 6)) : "PDF417 photo";
        Barcode barcode = Barcode.Pdf417(text, new Pdf417Options { Compact = kind == "compact" });
        float width = 420, height = width * (barcode.Modules.Height + (2 * barcode.QuietZone)) / (barcode.Modules.Width + (2 * barcode.QuietZone));
        using Raster<Gray8> photo = Photos.Render(barcode, 640, 640, Photos.Quad(new Vector2(320, 320), width, height, degrees, tilt), seed: (int)degrees + 5);
        Assert.Contains(photo.ReadBarcodes(), r => r.Format == BarcodeFormat.Pdf417 && r.Text == text);
    }

    public static TheoryData<string, float, float> AztecCases => new()
    {
        { "compact", 0, 0 },
        { "compact", 33, 0.1f },
        { "full", 0, 0 },
        { "full", 160, 0.08f },
        { "large", 280, 0.05f },
        { "rune", 15, 0 },
    };

    [Theory]
    [MemberData(nameof(AztecCases))]
    public void ReadsAztecPhotos(string kind, float degrees, float tilt)
    {
        (Barcode barcode, string text) = kind switch
        {
            "compact" => (Barcode.Aztec("Aztec 2D", new AztecOptions { Compact = true }), "Aztec 2D"),
            "full" => (Barcode.Aztec("Full-range Aztec Code", new AztecOptions { Compact = false }), "Full-range Aztec Code"),
            "rune" => (Barcode.AztecRune(42), "42"),
            _ => (Barcode.Aztec(string.Concat(Enumerable.Repeat("Aztec Code with many layers. ", 12))), string.Concat(Enumerable.Repeat("Aztec Code with many layers. ", 12))),
        };
        float side = kind == "rune" ? 200 : 380;
        using Raster<Gray8> photo = Photos.Render(barcode, 640, 640, Photos.Quad(new Vector2(320, 320), side, side, degrees, tilt), seed: (int)degrees + 3);
        Assert.Contains(photo.ReadBarcodes(), r => r.Format == BarcodeFormat.Aztec && r.Text == text);
    }
}
