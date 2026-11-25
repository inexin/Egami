using System.Text.Json;

using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>Every reference symbol, rendered as it is written, reads back as its text.</summary>
public class ReadPureTests
{
    // GS1 DataBar reading comes in a later change.
    public static TheoryData<string> Cases => [.. ReferenceTests.Cases.Where(row => !row.Data.StartsWith("databar", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Cases))]
    public void RendersReadBack(string name)
    {
        JsonElement test = ReferenceTests.Case(name);
        Barcode barcode = ReferenceTests.Make(test);
        string expected = Shown(test.GetProperty("text").GetString()!);
        using Raster<Gray8> image = barcode.ToRaster(3);

        BarcodeResult? pure = image.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true });
        Assert.NotNull(pure);
        Assert.Equal(barcode.Format, pure.Format);
        Assert.Equal(expected, Joined(pure));

        BarcodeResult? searched = image.ReadBarcode();
        Assert.NotNull(searched);
        Assert.Equal(expected, Joined(searched));
    }

    /// <summary>The text with an add-on after a space, as the references join them.</summary>
    private static string Joined(BarcodeResult result) => Shown(result.AddOn is null ? result.Text : $"{result.Text} {result.AddOn}");

    /// <summary>Control characters as spaces, as human-readable text shows them.</summary>
    private static string Shown(string text) => string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
}
