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
        BarcodeResult? result = photo.ReadBarcode(new BarcodeReaderOptions { Formats = BarcodeFormats.Qr });
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

        Assert.Equal(["Code 0", "Code 1", "Code 2"], image.ReadBarcodes(new BarcodeReaderOptions { Formats = BarcodeFormats.Qr }).Select(r => r.Text));
    }

    [Fact]
    public void CornersFollowTheSymbol()
    {
        Barcode qr = Barcode.Qr("Corners", new QrOptions { Version = 3 });
        Vector2[] quad = Photos.Quad(new Vector2(320, 240), 290, 290, 0);
        using Raster<Gray8> photo = Photos.Render(qr, 640, 480, quad, blur: 0, noise: 0);
        BarcodeResult? result = photo.ReadBarcode(new BarcodeReaderOptions { Formats = BarcodeFormats.Qr });
        Assert.NotNull(result);

        // The quiet zone is 4 of the 37 modules across the quad on each side.
        float inset = 290f * 4 / 37;
        Assert.InRange(result.Corners[0].X, quad[0].X + inset - 4, quad[0].X + inset + 4);
        Assert.InRange(result.Corners[2].Y, quad[2].Y - inset - 4, quad[2].Y - inset + 4);
    }
}
