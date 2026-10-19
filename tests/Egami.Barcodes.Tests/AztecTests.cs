using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Tests;

public class AztecTests
{
    [Theory]
    [InlineData(true, 1, 15)]
    [InlineData(true, 4, 27)]
    [InlineData(false, 4, 31)]
    [InlineData(false, 15, 79)]
    [InlineData(false, 32, 151)]
    public void SizesFollowTheLayers(bool compact, int layers, int size) =>
        Assert.Equal(size, Barcode.Aztec("A", new AztecOptions { Compact = compact, Layers = layers }).Modules.Width);

    [Fact]
    public void StuffingKeepsWordsOffAllZerosAndAllOnes()
    {
        var bits = new BitBuffer();
        bits.Append(0b000000, 6);
        bits.Append(0b111111, 6);

        // Six bits per word: 00000 takes a 1, 0 11111 is fine, then 1 1111 and its padding become 11110.
        int[] words = AztecEncoder.Stuff(bits, 6);
        Assert.Equal([0b000001, 0b011111, 0b111110], words);
    }

    [Theory]
    [InlineData("ABC", 15)]
    [InlineData("abc", 20)]
    [InlineData("A b", 20)]
    [InlineData("12345", 25)]
    [InlineData("a. b", 25)]
    public void ModesGiveTheFewestBits(string text, int bits) =>
        Assert.Equal(bits, AztecEncoder.HighLevel(Encoding.ASCII.GetBytes(text), 0).Count);

    [Fact]
    public void BinaryShiftCarriesOtherBytes()
    {
        // B/S, a 5-bit length and the bytes. Past 31 bytes the length takes 16 bits, so up to 62 bytes two short runs are
        // shorter, and past that one long run.
        Assert.Equal(10 + 8, AztecEncoder.HighLevel([0xE9], 0).Count);
        Assert.Equal(20 + (8 * 40), AztecEncoder.HighLevel([.. Enumerable.Repeat((byte)0xE9, 40)], 0).Count);
        Assert.Equal(21 + (8 * 100), AztecEncoder.HighLevel([.. Enumerable.Repeat((byte)0xE9, 100)], 0).Count);

        // ECI 26 first: P/S, FLG(2), the digits 2 and 6.
        Assert.Equal(5 + 5 + 3 + 8 + 5, AztecEncoder.HighLevel(Encoding.ASCII.GetBytes("A"), 26).Count);
    }

    [Fact]
    public void ErrorCorrectionShareGrowsTheSymbol()
    {
        string text = new('A', 60);
        int normal = Barcode.Aztec(text).Modules.Width;
        Assert.True(Barcode.Aztec(text, new AztecOptions { ErrorCorrectionPercent = 80 }).Modules.Width > normal);
        Assert.Equal(0, Barcode.Aztec(text).QuietZone);
    }

    [Fact]
    public void RunesAreElevenModules()
    {
        Barcode rune = Barcode.AztecRune(25);
        Assert.Equal((11, 11), (rune.Modules.Width, rune.Modules.Height));
        Assert.NotEqual(ReferenceTests.Grid(rune.Modules), ReferenceTests.Grid(Barcode.AztecRune(26).Modules));
        Assert.Throws<ArgumentOutOfRangeException>(() => Barcode.AztecRune(256));
        Assert.Throws<ArgumentOutOfRangeException>(() => Barcode.AztecRune(-1));
    }

    [Fact]
    public void BadInputThrows()
    {
        Assert.Throws<ArgumentException>(() => Barcode.Aztec(""));
        Assert.Throws<ArgumentException>(() => Barcode.Aztec(new string('A', 200), new AztecOptions { Compact = true }));
        Assert.Throws<ArgumentException>(() => Barcode.Aztec("A", new AztecOptions { Compact = true, Layers = 5 }));
        Assert.Throws<ArgumentException>(() => Barcode.Aztec(new string('é', 2000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AztecOptions { Layers = 33 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AztecOptions { ErrorCorrectionPercent = 99 });
    }
}
