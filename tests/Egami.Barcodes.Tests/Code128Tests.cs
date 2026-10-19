using Egami.Barcodes.Linear;

namespace Egami.Barcodes.Tests;

public class Code128Tests
{
    [Theory]
    [InlineData("123456", new[] { 105, 12, 34, 56 })]
    [InlineData("AB", new[] { 104, 33, 34 })]
    [InlineData("\u0001A", new[] { 103, 65, 33 })]
    [InlineData("a\u0001b", new[] { 104, 65, 98, 65, 66 })]
    [InlineData("1234567", new[] { 105, 12, 34, 56, 100, 23 })]
    [InlineData("é", new[] { 104, 100, 73 })]
    public void PicksTheShortestCodeSets(string text, int[] expected)
    {
        List<int> values = Code128Encoder.Encode([.. text.Select(c => (int)c)]);

        // The last value is the check character.
        Assert.Equal(expected, values[..^1]);
        int check = values[0];
        for (int i = 1; i < values.Count - 1; i++)
        {
            check += values[i] * i;
        }

        Assert.Equal(check % 103, values[^1]);
    }

    [Fact]
    public void Gs1PutsFnc1FirstAndAfterVariableLengthFields()
    {
        // (10) has no predefined length, so an FNC1 (102) ends it; (01) and (17) do not need one.
        Barcode barcode = Barcode.Gs1_128("(01)09501101530003(10)AB(17)261231");
        Assert.Equal("(01)09501101530003(10)AB(17)261231", barcode.HumanReadableText);
        int[] input = [Code128Encoder.Fnc1, .. "0109501101530003".Select(c => (int)c), .. "10AB".Select(c => (int)c), Code128Encoder.Fnc1, .. "17261231".Select(c => (int)c)];
        Assert.Equal(Code128Encoder.Runs(Code128Encoder.Encode(input)), barcode.Modules.Runs.ToArray());

        List<int> values = Code128Encoder.Encode(input);
        Assert.Equal(105, values[0]);
        Assert.Equal(102, values[1]);
        Assert.Equal(2, values.Count(v => v == 102));
    }

    [Fact]
    public void SymbolsHaveQuietZonesAndHeight()
    {
        Barcode barcode = Barcode.Code128("Egami", new Code128Options { Height = 30 });
        Assert.Equal(BarcodeFormat.Code128, barcode.Format);
        Assert.Equal(10, barcode.QuietZone);
        Assert.Equal(30, barcode.Modules.Height);
        Assert.Equal(50, Barcode.Code128("Egami").Modules.Height);

        // Start, five characters, check and stop: 11 modules each, 13 for the stop.
        Assert.Equal((7 * 11) + 13, barcode.Modules.Width);
        Assert.Equal("A B", Barcode.Code128("A\tB").HumanReadableText);
    }

    [Fact]
    public void BadTextThrows()
    {
        Assert.Throws<ArgumentException>(() => Barcode.Code128(""));
        Assert.Throws<ArgumentException>(() => Barcode.Code128("€"));
        Assert.Throws<ArgumentException>(() => Barcode.Code128(new string('A', 1001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Code128Options { Height = 0 });
        Assert.Throws<ArgumentException>(() => Barcode.Gs1_128(Array.Empty<Gs1ElementString>()));
        Assert.Throws<FormatException>(() => Barcode.Gs1_128("(01)09501101530004"));
    }
}
