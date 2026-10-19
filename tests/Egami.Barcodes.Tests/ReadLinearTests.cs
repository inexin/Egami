using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>Linear symbols in photo-like images: rotated, tilted, blurred and noisy, among other things.</summary>
public class ReadLinearTests
{
    public static TheoryData<string, float, float> Cases => new()
    {
        { "ean13", 0, 0 },
        { "ean13", 180, 0 },
        { "ean13", 90, 0 },
        { "ean13", 270, 0.1f },
        { "ean13", 4, 0.15f },
        { "code128", 0, 0.2f },
        { "code128", 180, 0.1f },
        { "code128", 90, 0 },
        { "code39", 0, 0 },
        { "code39", 270, 0 },
        { "itf14", 0, 0.1f },
        { "upce", 0, 0 },
        { "ean8", 90, 0 },
        { "upca", 180, 0.1f },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReadsPhotos(string kind, float degrees, float tilt)
    {
        (Barcode barcode, string text) = Make(kind);
        float width = 420, height = width * barcode.Modules.Height / (barcode.Modules.Width + (2 * barcode.QuietZone));
        using Raster<Gray8> photo = Photos.Render(barcode, 640, 480, Photos.Quad(new Vector2(320, 240), width, height, degrees, tilt), seed: (int)degrees + 3);
        IReadOnlyList<BarcodeResult> results = photo.ReadBarcodes();
        Assert.Contains(results, r => r.Text == text && r.Format == barcode.Format);
    }

    [Fact]
    public void DiagonalSymbolsNeedMoreEffort()
    {
        Barcode barcode = Barcode.Code128("DIAGONAL-42");
        using Raster<Gray8> photo = Photos.Render(barcode, 600, 600, Photos.Quad(new Vector2(300, 300), 420, 130, 45));
        Assert.Contains(photo.ReadBarcodes(new BarcodeReaderOptions { Effort = 8 }), r => r.Text == "DIAGONAL-42");
    }

    [Fact]
    public void TwoSymbolsAreBothFound()
    {
        using Raster<Gray8> photo = Raster.Create(640, 480, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = photo.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.Ean13("590123412345"), new RectF(20, 20, 300, 150), Rgba8.Black);
            canvas.DrawBarcode(Barcode.Code128("SECOND"), new RectF(320, 300, 300, 150), Rgba8.Black);
        }

        IReadOnlyList<BarcodeResult> results = photo.ReadBarcodes();
        Assert.Equal(["5901234123457", "SECOND"], results.Select(r => r.Text));
    }

    [Fact]
    public void NoiseIsNotABarcode()
    {
        using Raster<Gray8> noise = Raster.Create(320, 240, new Gray8(128));
        Assert.True(noise.TryGetContiguous(out Span<Gray8> pixels));
        var random = new Random(7);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Gray8((byte)random.Next(256));
        }

        Assert.Empty(noise.ReadBarcodes(new BarcodeReaderOptions { Effort = 10 }));
    }

    private static (Barcode, string) Make(string kind) => kind switch
    {
        "ean13" => (Barcode.Ean13("590123412345"), "5901234123457"),
        "code128" => (Barcode.Code128("Egami-128"), "Egami-128"),
        "code39" => (Barcode.Code39("CODE-39"), "CODE-39"),
        "itf14" => (Barcode.Itf14("1540014128876"), "15400141288763"),
        "upce" => (Barcode.UpcE("123456"), "01234565"),
        "ean8" => (Barcode.Ean8("9638507"), "96385074"),
        _ => (Barcode.UpcA("03600029145"), "036000291452"),
    };
}
