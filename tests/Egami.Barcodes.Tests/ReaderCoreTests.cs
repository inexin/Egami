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
        Assert.Null(image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true, Formats = BarcodeFormat.Qr }));
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

    [Theory]
    [InlineData(BarcodeFormat.Qr)]
    [InlineData(BarcodeFormat.DataMatrix)]
    [InlineData(BarcodeFormat.Aztec)]
    [InlineData(BarcodeFormat.Pdf417)]
    public void RandomTextRoundTrips(BarcodeFormat format)
    {
        // Mixed case, digits and punctuation in every length up to 40 move the encoders through all their modes and ends.
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789      .,-:/!?*+";
        var random = new Random(7);
        var failures = new List<string>();
        for (int n = 0; n < 160; n++)
        {
            string text = new([.. Enumerable.Range(0, 1 + (n % 40)).Select(_ => Alphabet[random.Next(Alphabet.Length)])]);
            Barcode barcode = Barcode.Create(format, text);
            int scale = 3, quiet = 4;
            using Raster<Gray8> image = Raster.Create((barcode.Modules.Width + (2 * quiet)) * scale, (barcode.Modules.Height + (2 * quiet)) * scale, new Gray8(255));
            using (RasterCanvas<Gray8> canvas = image.CreateCanvas())
            {
                float at = (quiet - barcode.QuietZone) * scale;
                canvas.DrawBarcode(barcode, new System.Numerics.Vector2(at, at), scale, Rgba8.Black);
            }

            if (image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true })?.Text != text)
            {
                failures.Add(text);
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void LuminanceBuffersReadLikeRasters()
    {
        using Raster<Gray8> image = Raster.Create(400, 300, new Gray8(255));
        using (RasterCanvas<Gray8> canvas = image.CreateCanvas())
        {
            canvas.DrawBarcode(Barcode.Qr("Camera frame"), new RectF(30, 40, 200, 200), Rgba8.Black);
            canvas.DrawBarcode(Barcode.Code128("Y-PLANE"), new RectF(250, 60, 140, 80), Rgba8.Black);
        }

        // A camera-style Y plane: rows padded to 448 bytes, the padding mid-gray.
        const int Stride = 448;
        byte[] plane = new byte[(Stride * image.Height) - (Stride - image.Width)];
        Array.Fill(plane, (byte)128);
        for (int y = 0; y < image.Height; y++)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Row(y)).CopyTo(plane.AsSpan(y * Stride));
        }

        static string Describe(IReadOnlyList<BarcodeResult> results) => string.Join("; ", results.Select(r => $"{r.Format} {r.Text} {string.Join(" ", r.Corners)}"));
        Assert.Equal(Describe(image.ReadBarcodes()), Describe(BarcodeReader.ReadBarcodes(plane, image.Width, image.Height, Stride)));
        Assert.Equal(2, BarcodeReader.ReadBarcodes(plane, image.Width, image.Height, Stride).Count);

        var region = new BarcodeReaderOptions { Region = new Rect(20, 30, 220, 220) };
        Assert.Equal(Describe(image.ReadBarcodes(region)), Describe(BarcodeReader.ReadBarcodes(plane, image.Width, image.Height, Stride, region)));
        Assert.Equal("Camera frame", BarcodeReader.ReadBarcode(plane, image.Width, image.Height, Stride, region)?.Text);
    }

    [Fact]
    public void LuminanceBuffersAreChecked()
    {
        byte[] plane = new byte[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeReader.ReadBarcodes(plane, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeReader.ReadBarcodes(plane, 10, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeReader.ReadBarcodes(plane, 10, 10, 9));
        Assert.Throws<ArgumentException>(() => BarcodeReader.ReadBarcodes(plane, 10, 10, 11));
        Assert.Empty(BarcodeReader.ReadBarcodes(plane, 10, 10, 10));
    }

    [Fact]
    public void AddOnsComeApart()
    {
        Barcode book = Barcode.Ean13("978044101498", new EanUpcOptions { AddOn = "51299" });
        using Raster<Gray8> image = book.ToRaster(pixelsPerModule: 3);
        BarcodeResult? result = image.ReadBarcode();
        Assert.Equal("9780441014989", result?.Text);
        Assert.Equal("51299", result?.AddOn);
        Assert.Null(Barcode.Ean13("978044101498").ToRaster(pixelsPerModule: 3).ReadBarcode()?.AddOn);
    }

    [Theory]
    [InlineData("ße oaus OslG")]
    [InlineData("ab")]
    public void Code128ExtendedCharactersAfterAShiftReadBack(string text)
    {
        // FNC4 before the shift: a character above 127 reached through Shift keeps its code set.
        using Raster<Gray8> image = Barcode.Code128(text).ToRaster(pixelsPerModule: 2);
        Assert.Equal(text, image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true })?.Text);
    }

    [Theory]
    [InlineData("h    ;")]
    [InlineData("ht s:")]
    public void AztecRenderedWithoutAMarginReads(string text)
    {
        // Light modules on the edge of an Aztec Code (which has no quiet zone) must not hide the symbol's size.
        using Raster<Gray8> image = Barcode.Aztec(text, new AztecOptions { Compact = true, ErrorCorrectionPercent = 5 }).ToRaster(pixelsPerModule: 2);
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes(text), image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true })?.Bytes.ToArray());
        Assert.NotNull(image.ReadBarcode());
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
