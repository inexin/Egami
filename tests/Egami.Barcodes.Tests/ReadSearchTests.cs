using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>The search as a whole: many symbols per image, inverted and mirrored ones, effort levels and thread counts.</summary>
public class ReadSearchTests
{
    private static readonly (Barcode Barcode, string Text, RectF Place)[] s_mixed =
    [
        (Barcode.Qr("QR in a crowd"), "QR in a crowd", new RectF(40, 40, 240, 240)),
        (Barcode.DataMatrix("DM in a crowd"), "DM in a crowd", new RectF(360, 40, 220, 220)),
        (Barcode.Aztec("Aztec in a crowd"), "Aztec in a crowd", new RectF(660, 40, 240, 240)),
        (Barcode.Pdf417("PDF417 in a crowd"), "PDF417 in a crowd", new RectF(40, 360, 520, 160)),
        (Barcode.Code128("CODE128-CROWD"), "CODE128-CROWD", new RectF(620, 360, 440, 140)),
        (Barcode.Ean13("4006381333931"), "4006381333931", new RectF(40, 600, 380, 160)),
    ];

    private static Raster<Gray8> Mixed()
    {
        Raster<Gray8> image = Raster.Create(1100, 820, new Gray8(255));
        using RasterCanvas<Gray8> canvas = image.CreateCanvas();
        foreach ((Barcode barcode, string _, RectF place) in s_mixed)
        {
            canvas.DrawBarcode(barcode, place, Rgba8.Black);
        }

        return image;
    }

    [Fact]
    public void EverySymbolInAnImageIsFoundOnce()
    {
        using Raster<Gray8> image = Mixed();
        IReadOnlyList<BarcodeResult> results = image.ReadBarcodes();
        Assert.Equal(
            s_mixed.Select(m => $"{m.Barcode.Format} {m.Text}").Order(),
            results.Select(r => $"{r.Format} {r.Text}").Order());
    }

    [Fact]
    public void ResultsAreTheSameAtEveryThreadCount()
    {
        using Raster<Gray8> image = Mixed();
        string Read(int threads) => string.Join("\n", image.ReadBarcodes(new BarcodeReaderOptions { Context = new EgamiContext { MaxParallelism = threads } })
            .Select(r => $"{r.Format} {r.Text} {string.Join(" ", r.Corners)}"));
        string sequential = Read(1);
        Assert.NotEmpty(sequential);
        foreach (int threads in (ReadOnlySpan<int>)[2, 3, 8, 32])
        {
            Assert.Equal(sequential, Read(threads));
        }
    }

    [Theory]
    [InlineData("qr")]
    [InlineData("datamatrix")]
    [InlineData("aztec")]
    public void LightSymbolsOnDarkAreReadInverted(string kind)
    {
        Barcode barcode = kind switch
        {
            "qr" => Barcode.Qr("Inverted"),
            "datamatrix" => Barcode.DataMatrix("Inverted"),
            _ => Barcode.Aztec("Inverted"),
        };
        using Raster<Gray8> image = Raster.Create(400, 400, new Gray8(20));
        using (RasterCanvas<Gray8> canvas = image.CreateCanvas())
        {
            canvas.DrawBarcode(barcode, new RectF(80, 80, 240, 240), Rgba8.White);
        }

        BarcodeResult? result = image.ReadBarcode();
        Assert.Equal("Inverted", result?.Text);
        Assert.True(result?.Inverted);
        Assert.Null(image.ReadBarcode(new BarcodeReaderOptions { TryInverted = false }));
    }

    [Theory]
    [InlineData("datamatrix")]
    [InlineData("aztec")]
    public void MirroredSymbolsRead(string kind)
    {
        Barcode barcode = kind == "datamatrix" ? Barcode.DataMatrix("Mirror image") : Barcode.Aztec("Mirror image");
        Vector2[] quad = Photos.Quad(new Vector2(320, 240), 300, 300, 20);
        (quad[0], quad[1], quad[2], quad[3]) = (quad[1], quad[0], quad[3], quad[2]);
        using Raster<Gray8> photo = Photos.Render(barcode, 640, 480, quad);
        BarcodeResult? result = photo.ReadBarcode();
        Assert.Equal("Mirror image", result?.Text);
        Assert.True(result?.Mirrored);
    }

    [Fact]
    public void HigherEffortFindsMore()
    {
        // Code 128 on its side: rows only at effort 0 and 1, columns from effort 3, diagonals from effort 7.
        Barcode barcode = Barcode.Code128("EFFORT");
        float width = 360, height = width * barcode.Modules.Height / (barcode.Modules.Width + (2 * barcode.QuietZone));
        using Raster<Gray8> upright = Photos.Render(barcode, 480, 480, Photos.Quad(new Vector2(240, 240), width, height, 90));
        using Raster<Gray8> slanted = Photos.Render(barcode, 480, 480, Photos.Quad(new Vector2(240, 240), width, height, 45));
        Assert.Null(upright.ReadBarcode(new BarcodeReaderOptions { Effort = 0 }));
        Assert.Equal("EFFORT", upright.ReadBarcode(new BarcodeReaderOptions { Effort = 3 })?.Text);
        Assert.Null(slanted.ReadBarcode(new BarcodeReaderOptions { Effort = 5, Formats = BarcodeFormat.Linear }));
        Assert.Equal("EFFORT", slanted.ReadBarcode(new BarcodeReaderOptions { Effort = 7 })?.Text);
    }

    [Fact]
    public void ManyQrCodesOnAPageAreAllFound()
    {
        using Raster<Gray8> image = Raster.Create(1200, 900, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = image.CreateCanvas())
        {
            for (int i = 0; i < 12; i++)
            {
                canvas.DrawBarcode(Barcode.Qr($"Label {i:00}"), new RectF(30 + (i % 4 * 290), 30 + (i / 4 * 290), 230, 230), Rgba8.Black);
            }
        }

        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"Label {i:00}"), image.ReadBarcodes(new BarcodeReaderOptions { Formats = BarcodeFormat.Qr }).Select(r => r.Text));
    }
}
