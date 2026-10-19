using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

public class EanUpcTests
{
    private static Font TestFont => new(Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf")), 10);

    [Theory]
    [InlineData("Ean13", "590123412345", "5901234123457")]
    [InlineData("Ean13", "4006381333931", "4006381333931")]
    [InlineData("Ean8", "9638507", "96385074")]
    [InlineData("UpcA", "03600029145", "036000291452")]
    [InlineData("UpcE", "123456", "01234565")]
    [InlineData("UpcE", "01234565", "01234565")]
    [InlineData("UpcE", "042100005264", "04252614")]
    public void AddsOrChecksTheCheckDigit(string call, string digits, string expected) =>
        Assert.Equal(expected, Make(call, digits).HumanReadableText);

    [Theory]
    [InlineData("Ean13", "5901234123458", "should be 7, not 8")]
    [InlineData("Ean8", "96385075", "should be 4, not 5")]
    [InlineData("UpcA", "036000291453", "should be 2, not 3")]
    [InlineData("UpcE", "01234566", "should be 5, not 6")]
    [InlineData("UpcE", "036000291452", "too few zeros")]
    [InlineData("UpcE", "2123456", "number system is 0 or 1")]
    [InlineData("UpcE", "12345", "not 5")]
    [InlineData("Ean13", "59012341234", "not 11")]
    [InlineData("Ean13", "59012341234X", "digits")]
    public void RejectsWrongDigits(string call, string digits, string error)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => Make(call, digits));
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolsHaveTheirStandardSizes()
    {
        Assert.Equal((95, 69), (Barcode.Ean13("590123412345").Modules.Width, Barcode.Ean13("590123412345").Modules.Height));
        Assert.Equal((67, 55), (Barcode.Ean8("9638507").Modules.Width, Barcode.Ean8("9638507").Modules.Height));
        Assert.Equal(95, Barcode.UpcA("03600029145").Modules.Width);
        Assert.Equal(51, Barcode.UpcE("123456").Modules.Width);

        // Add-ons after a gap of 7 (EAN) or 9 (UPC) modules: EAN-2 is 20 modules, EAN-5 47.
        Barcode withAddOn = Barcode.Ean13("590123412345", new EanUpcOptions { AddOn = "12", Height = 40 });
        Assert.Equal(95 + 7 + 20, withAddOn.Modules.Width);
        Assert.Equal(40, withAddOn.Modules.Height);
        Assert.Equal("5901234123457 12", withAddOn.HumanReadableText);
        Assert.Equal(95 + 9 + 47, Barcode.UpcA("03600029145", new EanUpcOptions { AddOn = "12345" }).Modules.Width);
        Assert.Throws<ArgumentException>(() => new EanUpcOptions { AddOn = "123" });
        Assert.Throws<ArgumentException>(() => new EanUpcOptions { AddOn = "1a" });
    }

    [Fact]
    public void WithAFontGuardsReachIntoTheDigits()
    {
        Barcode barcode = Barcode.Ean13("590123412345", new EanUpcOptions { Height = 20 });
        using Raster<Gray8> plain = barcode.ToRaster(2);
        using Raster<Gray8> text = barcode.ToRaster(2, new BarcodeDrawOptions { Font = TestFont });
        Assert.Equal(40, plain.Height);
        int left = 11 * 2;

        // The start guard's first bar is at the quiet zone's end; the first data bar of digit 2 (a 9 in set A, space 3 first) is 6 modules in.
        int guard = left, data = left + (6 * 2);
        Assert.Equal(0, text.GetPixel(guard, 40 + 4).L);
        Assert.Equal(255, text.GetPixel(data, 40 + 4).L);
        Assert.Equal(0, text.GetPixel(data, 39).L);

        // The first digit is drawn left of the start guard, in the quiet zone; the others under the bars.
        Assert.Contains(Enumerable.Range(0, left), x => Enumerable.Range(40, text.Height - 40).Any(y => text.GetPixel(x, y).L < 128));
        Assert.Contains(Enumerable.Range(left + 10, 80), x => text.GetPixel(x, text.Height - 6).L < 128);
    }

    [Fact]
    public void AnAddOnsDigitsGoAboveItsBars()
    {
        Barcode barcode = Barcode.Ean13("590123412345", new EanUpcOptions { AddOn = "12", Height = 30 });
        using Raster<Gray8> raster = barcode.ToRaster(1, new BarcodeDrawOptions { Font = TestFont });
        int addOn = 11 + 95 + 7;

        // The add-on's first bar starts below its text; the main symbol's first bar starts at the top.
        Assert.Equal(255, raster.GetPixel(addOn, 0).L);
        Assert.Equal(0, raster.GetPixel(addOn, 25).L);
        Assert.Equal(0, raster.GetPixel(11, 0).L);
        Assert.Contains(Enumerable.Range(addOn, 20), x => Enumerable.Range(0, 10).Any(y => raster.GetPixel(x, y).L < 128));
    }

    [Fact]
    public void ARecordingHoldsOnePathAndTheTextRuns()
    {
        var recorder = new PictureRecorder(new RectF(0, 0, 300, 120));
        recorder.DrawBarcode(Barcode.UpcA("03600029145"), new RectF(0, 0, 300, 120), Rgba8.Black, new BarcodeDrawOptions { Font = TestFont });

        // One path for the bars, then four pieces of text: first digit, two groups of five, check digit.
        Assert.Equal(5, recorder.Finish().CallCount);
    }

    private static Barcode Make(string call, string digits) => call switch
    {
        "Ean13" => Barcode.Ean13(digits),
        "Ean8" => Barcode.Ean8(digits),
        "UpcA" => Barcode.UpcA(digits),
        _ => Barcode.UpcE(digits),
    };
}
