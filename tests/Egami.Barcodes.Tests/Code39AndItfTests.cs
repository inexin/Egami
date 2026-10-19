using Egami.Pixels;

namespace Egami.Barcodes.Tests;

public class Code39AndItfTests
{
    [Fact]
    public void Code39AddsTheCheckCharacterOnlyWhenAsked()
    {
        Barcode plain = Barcode.Code39("AB1");
        Barcode checkedCode = Barcode.Code39("AB1", new Code39Options { CheckDigit = true });
        Assert.Equal("AB1", plain.HumanReadableText);

        // A = 10, B = 11, 1 = 1: 22 is M.
        Assert.Equal("AB1M", checkedCode.HumanReadableText);
        Assert.Equal(plain.Modules.Width + 13, checkedCode.Modules.Width);
        Assert.Equal(BarcodeFormat.Code39, plain.Format);
    }

    [Fact]
    public void Code39UsesFullAsciiOnlyWhenNeeded()
    {
        // Each character is 12 modules with its gap at ratio 2, plus 25 for start and stop.
        Assert.Equal((3 * 13) + 25, Barcode.Code39("AB1").Modules.Width);
        Assert.Equal((4 * 13) + 25, Barcode.Code39("ab").Modules.Width);
        Assert.Equal("ab", Barcode.Code39("ab").HumanReadableText);
        Assert.Throws<ArgumentException>(() => Barcode.Code39("ab", new Code39Options { FullAscii = false }));
        Assert.Throws<ArgumentException>(() => Barcode.Code39("é"));

        // Full ASCII on purpose spells $ as /D.
        Assert.Equal(Barcode.Code39("/D").Modules.Runs.ToArray(), Barcode.Code39("$", new Code39Options { FullAscii = true }).Modules.Runs.ToArray());
    }

    [Fact]
    public void WideRatioWidensWideElements()
    {
        // Three wide elements per character, start and stop.
        Assert.Equal(Barcode.Code39("A").Modules.Width + 9, Barcode.Code39("A", new Code39Options { WideRatio = 3 }).Modules.Width);
        Assert.Equal(Barcode.Itf("12").Modules.Width - 5, Barcode.Itf("12", new ItfOptions { WideRatio = 2 }).Modules.Width);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Code39Options { WideRatio = 4 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItfOptions { WideRatio = 1 });
    }

    [Fact]
    public void ItfPadsOddDigits()
    {
        Assert.Equal("012345", Barcode.Itf("12345").HumanReadableText);
        Assert.Equal(Barcode.Itf("012345").Modules.Runs.ToArray(), Barcode.Itf("12345").Modules.Runs.ToArray());
        Assert.Throws<ArgumentException>(() => Barcode.Itf("12a4"));
        Assert.Throws<ArgumentException>(() => Barcode.Itf(""));
    }

    [Fact]
    public void Itf14AddsOrChecksTheCheckDigit()
    {
        Assert.Equal("12345678901231", Barcode.Itf14("1234567890123").HumanReadableText);
        Assert.Equal("15400141288763", Barcode.Itf14("15400141288763").HumanReadableText);
        ArgumentException wrong = Assert.Throws<ArgumentException>(() => Barcode.Itf14("15400141288764"));
        Assert.Contains("should be 3, not 4", wrong.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Barcode.Itf14("123"));
    }

    [Fact]
    public void Itf14HasBearerBarsAroundItsQuietZone()
    {
        Barcode barcode = Barcode.Itf14("1234567890123", new ItfOptions { Height = 10 });
        using Raster<Gray8> raster = barcode.ToRaster();

        // 5-module bearers outside the 10-module quiet zones: the frame is dark, the zone inside it light.
        Assert.Equal((barcode.Modules.Width + 30, 20), (raster.Width, raster.Height));
        Assert.Equal(0, raster.GetPixel(raster.Width / 2, 0).L);
        Assert.Equal(0, raster.GetPixel(raster.Width / 2, 19).L);
        Assert.Equal(0, raster.GetPixel(2, 10).L);
        Assert.Equal(0, raster.GetPixel(raster.Width - 3, 10).L);
        Assert.Equal(255, raster.GetPixel(10, 10).L);
        Assert.Equal(0, raster.GetPixel(15, 10).L);

        using Raster<Gray8> plain = Barcode.Itf("12", new ItfOptions { Height = 10 }).ToRaster();
        Assert.Equal(10, plain.Height);
        using Raster<Gray8> framed = Barcode.Itf("12", new ItfOptions { Height = 10, BearerBars = true }).ToRaster();
        Assert.Equal(20, framed.Height);
    }
}
