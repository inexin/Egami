using System.Text.Json;

using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>Every reference symbol, rendered as it is written, reads back as its text.</summary>
public class ReadPureTests
{
    public static TheoryData<string> Cases => ReferenceTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void RendersReadBack(string name)
    {
        JsonElement test = ReferenceTests.Case(name);
        Barcode barcode = ReferenceTests.Make(test);
        string expected = test.GetProperty("text").GetString()!;
        using Raster<Gray8> image = barcode.ToRaster(3);

        BarcodeResult? pure = image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true });
        Assert.NotNull(pure);
        Assert.Equal(barcode.Format, pure.Format);
        Assert.Equal(expected, pure.Text);

        BarcodeResult? searched = image.ReadBarcode();
        Assert.NotNull(searched);
        Assert.Equal(expected, searched.Text);
    }
}
