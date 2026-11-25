using Egami.Barcodes.Linear;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

public class Code93AndCodabarTests
{
    [Fact]
    public void Code93AddsBothCheckCharacters()
    {
        // TEST93: C is '+' (41) and K is '6' (6), from the weights 1 to 20 and 1 to 15 of USS-93.
        List<int> values = Code93Encoder.Values("TEST93");
        Code93Encoder.AddCheckCharacters(values);
        Assert.Equal([29, 14, 28, 29, 9, 3, 41, 6], values);

        // Start, 6 characters, C, K and stop of 9 modules each, and the termination bar.
        Barcode barcode = Barcode.Code93("TEST93");
        Assert.Equal((10 * 9) + 1, barcode.Modules.Width);
        Assert.Equal("TEST93", barcode.HumanReadableText);
        Assert.Equal(BarcodeFormat.Code93, barcode.Format);
        Assert.Equal(10, barcode.QuietZone);
    }

    [Fact]
    public void Code93UsesFullAsciiOnlyWhenNeeded()
    {
        Assert.Equal((5 * 9) + 1, Barcode.Code93("A").Modules.Width);
        Assert.Equal((6 * 9) + 1, Barcode.Code93("a").Modules.Width);
        Assert.Equal(Barcode.Code93("A$/").Modules.Runs.ToArray(), Barcode.Code93("A$/", new Code93Options { FullAscii = true }).Modules.Runs.ToArray());
        Assert.Throws<ArgumentException>(() => Barcode.Code93("a", new Code93Options { FullAscii = false }));
        Assert.Throws<ArgumentException>(() => Barcode.Code93("é"));
        Assert.Throws<ArgumentException>(() => Barcode.Code93(""));
        Assert.Equal("A B", Barcode.Code93("A\tB").HumanReadableText);
    }

    [Fact]
    public void CodabarTakesStartAndStopFromTextOrOptions()
    {
        Assert.Equal("A1234A", Barcode.Codabar("1234").HumanReadableText);
        Assert.Equal("C1234D", Barcode.Codabar("1234", new CodabarOptions { Start = 'C', Stop = 'D' }).HumanReadableText);
        Assert.Equal("B12D", Barcode.Codabar("B12D", new CodabarOptions { Start = 'C' }).HumanReadableText);
        Assert.Equal(BarcodeFormat.Codabar, Barcode.Create(BarcodeFormat.Codabar, "B12D").Format);
        Assert.Throws<ArgumentException>(() => Barcode.Codabar("B12"));
        Assert.Throws<ArgumentException>(() => Barcode.Codabar("12D"));
        Assert.Throws<ArgumentException>(() => Barcode.Codabar("1A2"));
        Assert.Throws<ArgumentException>(() => Barcode.Codabar("12X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodabarOptions { Start = 'E' });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodabarOptions { WideRatio = 4 });
    }

    [Fact]
    public void CodabarNeedsFourCharacters()
    {
        Assert.Throws<ArgumentException>(() => Barcode.Codabar("1"));
        Assert.Throws<ArgumentException>(() => Barcode.Codabar("A1B"));
        Assert.Equal(4, Barcode.Codabar("1", new CodabarOptions { CheckDigit = true }).HumanReadableText!.Length);
    }

    [Fact]
    public void CodabarCheckCharacterIsModulo16()
    {
        // A40156B: 16 + 4 + 0 + 1 + 5 + 6 + 17 = 49; 49 + 15 is a multiple of 16, and 15 is '+'.
        Assert.Equal("A40156+B", Barcode.Codabar("A40156B", new CodabarOptions { CheckDigit = true }).HumanReadableText);
    }

    [Fact]
    public void CodabarWideRatioWidensWideElements()
    {
        // Each character has two or three wide elements, each one module wider at 3:1.
        Barcode two = Barcode.Codabar("A12B"), three = Barcode.Codabar("A12B", new CodabarOptions { WideRatio = 3 });
        Assert.Equal(two.Modules.Width + 3 + 2 + 2 + 3, three.Modules.Width);
    }

    [Theory]
    [InlineData("Egami 93!", false)]
    [InlineData("A40156B", true)]
    [InlineData("C-$:/.+0123456789D", true)]
    public void ReadsBackWithTheDefaultFormats(string text, bool codabar)
    {
        Barcode barcode = codabar ? Barcode.Codabar(text) : Barcode.Code93(text);
        using Raster<Gray8> image = barcode.ToRaster(2);
        BarcodeResult? read = image.ReadBarcode();
        Assert.NotNull(read);
        Assert.Equal(barcode.Format, read.Format);
        Assert.Equal(text, read.Text);
    }

    [Fact]
    public void Code93WithAWrongCheckCharacterIsNotRead()
    {
        // The symbol of TEST93 with K changed from '6' to '7'.
        List<int> values = Code93Encoder.Values("TEST93");
        Code93Encoder.AddCheckCharacters(values);
        values[^1] = 7;
        Barcode wrong = Barcode.FromModules(BarcodeFormat.Code93, BarcodeModules.CreateLinear(Code93Encoder.Runs(values), 50));
        using Raster<Gray8> image = wrong.ToRaster(2);
        Assert.Null(image.ReadBarcode());
        Assert.Null(image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true }));
    }

    [Fact]
    public void DefaultFormatsIncludeCode93AndCodabar()
    {
        Assert.True(BarcodeFormat.Linear.HasFlag(BarcodeFormat.Code93 | BarcodeFormat.Codabar));
        Assert.True(BarcodeReaderOptions.Default.Formats.HasFlag(BarcodeFormat.Code93 | BarcodeFormat.Codabar));
    }
}
