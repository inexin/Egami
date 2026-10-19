using Egami.Barcodes.Reading;
using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

public class ReaderCoreTests
{
    [Fact]
    public void AnyPixelTypeReads()
    {
        Barcode qr = Barcode.Qr("Pixel types");
        using Raster<Rgba8> rgba = Raster.Create(qr.Modules.Width * 4 + 40, qr.Modules.Width * 4 + 40, new Rgba8(250, 240, 220, 255));
        using (RasterCanvas<Rgba8> canvas = rgba.CreateCanvas())
        {
            canvas.DrawBarcode(qr, new RectF(0, 0, rgba.Width, rgba.Height), new Rgba8(20, 30, 90, 255));
        }

        Assert.Equal("Pixel types", rgba.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true })?.Text);
        using Raster<RgbaF32> floats = Raster.Create(rgba.Width, rgba.Height, new RgbaF32(1, 1, 1, 1));
        PixelConvert.Convert<Rgba8, RgbaF32>(Pixels(rgba), Pixels(floats));
        Assert.Equal("Pixel types", floats.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true })?.Text);
    }

    [Fact]
    public void InvertedSymbolsReadFromEffortFive()
    {
        using Raster<Gray8> image = Barcode.DataMatrix("Light on dark").ToRaster(4);
        Span<Gray8> pixels = Pixels(image);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Gray8((byte)(255 - pixels[i].L));
        }

        BarcodeResult? result = image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true });
        Assert.NotNull(result);
        Assert.True(result.Inverted);
        Assert.Equal("Light on dark", result.Text);
        Assert.Null(image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true, TryInverted = false }));
    }

    [Fact]
    public void RegionAndFormatsNarrowTheSearch()
    {
        using Raster<Gray8> image = Raster.Create(400, 200, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = image.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.Aztec("Right half"), new RectF(250, 50, 100, 100), Rgba8.Black);
        }

        BarcodeResult? result = image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true, Region = new Rect(200, 0, 200, 200) });
        Assert.NotNull(result);
        Assert.Equal("Right half", result.Text);
        Assert.True(result.Corners[0].X >= 250);
        Assert.Null(image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true, Formats = BarcodeFormats.Qr }));
    }

    [Fact]
    public void DamageIsCorrected()
    {
        Barcode qr = Barcode.Qr("Error correction at work", new QrOptions { ErrorCorrection = QrErrorCorrection.H });
        bool[] cells = new bool[qr.Modules.Width * qr.Modules.Height];
        for (int y = 0; y < qr.Modules.Height; y++)
        {
            for (int x = 0; x < qr.Modules.Width; x++)
            {
                // A light patch over part of the data, away from the finders.
                cells[(y * qr.Modules.Width) + x] = qr.Modules[x, y] && !(x is >= 9 and < 14 && y is >= 9 and < 14);
            }
        }

        BarcodeModules damaged = BarcodeModules.Create(qr.Modules.Width, qr.Modules.Height, cells);
        DecodedData? data = QrDecoder.Decode(damaged);
        Assert.NotNull(data);
        Assert.Equal("Error correction at work", data.Text);
        Assert.True(data.ErrorsCorrected > 0);
    }

    [Fact]
    public void MirroredQrReads()
    {
        Barcode qr = Barcode.Qr("Mirror");
        DecodedData? data = QrDecoder.Decode(QrDecoder.Transpose(qr.Modules));
        Assert.NotNull(data);
        Assert.True(data.Mirrored);
        Assert.Equal("Mirror", data.Text);
    }

    [Fact]
    public void ResultsCarryGs1AndEci()
    {
        using Raster<Gray8> gs1 = Barcode.DataMatrix(Gs1.Parse("(01)09501101530003(10)AB12")).ToRaster(3);
        BarcodeResult? result = gs1.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true });
        Assert.NotNull(result);
        Assert.Equal(["01", "10"], result.Gs1!.Select(e => e.Ai));
        Assert.Equal("0109501101530003" + "10AB12", System.Text.Encoding.ASCII.GetString(result.Bytes.Span));

        using Raster<Gray8> utf8 = Barcode.Qr("Ελληνικά").ToRaster(3);
        result = utf8.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true });
        Assert.Equal(26, result?.Eci);
        Assert.Equal("Ελληνικά", result?.Text);
    }

    [Fact]
    public void OptionsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarcodeReaderOptions { Effort = 11 });
        using Raster<Gray8> blank = Raster.Create(50, 50, new Gray8(255));
        Assert.Empty(blank.ReadBarcodes());
        Assert.Null(blank.ReadBarcode());
    }

    private static Span<TPixel> Pixels<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel> =>
        raster.TryGetContiguous(out Span<TPixel> pixels) ? pixels : throw new InvalidOperationException();
}
