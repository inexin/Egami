using Egami.Pixels;

namespace Egami.Tests.Pixels;

public class Rgba8ColorsTests
{
    [Theory]
    [InlineData("#FF8000", 255, 128, 0, 255)]
    [InlineData("ff8000", 255, 128, 0, 255)]
    [InlineData("#FF800080", 255, 128, 0, 128)]
    [InlineData("#F80", 255, 136, 0, 255)]
    [InlineData("F808", 255, 136, 0, 136)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    public void FromHexParsesEveryForm(string hex, byte r, byte g, byte b, byte a)
    {
        Assert.Equal(new Rgba8(r, g, b, a), Rgba8.FromHex(hex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#GG0000")]
    [InlineData("##FF0000")]
    [InlineData("+FF0000")]
    [InlineData(" FF0000")]
    public void FromHexRejectsInvalidInput(string hex)
    {
        Assert.False(Rgba8.TryFromHex(hex, out Rgba8 color));
        Assert.Equal(default, color);
        Assert.Throws<FormatException>(() => Rgba8.FromHex(hex));
    }

    [Fact]
    public void ToHexRoundTrips()
    {
        Rgba8 color = new(1, 171, 205, 239);
        Assert.Equal("#01ABCDEF", color.ToHex());
        Assert.Equal(color, Rgba8.FromHex(color.ToHex()));
    }

    [Fact]
    public void NamedColorsMatchTheirHex()
    {
        Assert.Equal(Rgba8.FromHex("#00000000"), Rgba8.Transparent);
        Assert.Equal(Rgba8.FromHex("#000000"), Rgba8.Black);
        Assert.Equal(Rgba8.FromHex("#FFFFFF"), Rgba8.White);
        Assert.Equal(Rgba8.FromHex("#808080"), Rgba8.Gray);
        Assert.Equal(Rgba8.FromHex("#C0C0C0"), Rgba8.Silver);
        Assert.Equal(Rgba8.FromHex("#FF0000"), Rgba8.Red);
        Assert.Equal(Rgba8.FromHex("#800000"), Rgba8.Maroon);
        Assert.Equal(Rgba8.FromHex("#00FF00"), Rgba8.Lime);
        Assert.Equal(Rgba8.FromHex("#008000"), Rgba8.Green);
        Assert.Equal(Rgba8.FromHex("#0000FF"), Rgba8.Blue);
        Assert.Equal(Rgba8.FromHex("#000080"), Rgba8.Navy);
        Assert.Equal(Rgba8.FromHex("#FFFF00"), Rgba8.Yellow);
        Assert.Equal(Rgba8.FromHex("#808000"), Rgba8.Olive);
        Assert.Equal(Rgba8.FromHex("#00FFFF"), Rgba8.Cyan);
        Assert.Equal(Rgba8.FromHex("#008080"), Rgba8.Teal);
        Assert.Equal(Rgba8.FromHex("#FF00FF"), Rgba8.Magenta);
        Assert.Equal(Rgba8.FromHex("#800080"), Rgba8.Purple);
    }

    [Fact]
    public void EqualityAndToString()
    {
        Assert.True(new Rgba8(1, 2, 3, 4) == new Rgba8(1, 2, 3, 4));
        Assert.True(new Rgba8(1, 2, 3, 4) != new Rgba8(1, 2, 3, 5));
        Assert.Equal(new Rgba8(1, 2, 3, 4).GetHashCode(), new Rgba8(1, 2, 3, 4).GetHashCode());
        Assert.Equal("Rgba8(1, 2, 3, 4)", new Rgba8(1, 2, 3, 4).ToString());
        Assert.Equal("RgbaF32(0.5, 1, 0, 0.25)", new RgbaF32(0.5f, 1f, 0f, 0.25f).ToString());
    }
}
