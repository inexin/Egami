namespace Egami.Barcodes.Tests;

public class Gs1Tests
{
    [Fact]
    public void ParsesElementStrings()
    {
        IReadOnlyList<Gs1ElementString> elements = Gs1.Parse("(01)09501101530003(17)261231(10)AB12(3103)000123");
        Assert.Equal(["01", "17", "10", "3103"], elements.Select(e => e.Ai));
        Assert.Equal(["09501101530003", "261231", "AB12", "000123"], elements.Select(e => e.Value));
        Assert.Equal("GTIN", elements[0].Title);
        Assert.Equal("BATCH/LOT", elements[2].Title);
        Assert.True(elements[0].PredefinedLength);
        Assert.False(elements[2].PredefinedLength);
        Assert.Equal("(01)09501101530003", elements[0].ToString());
        Assert.Equal(Gs1ElementString.Create("10", "AB12"), elements[2]);
    }

    [Fact]
    public void BackslashEscapesAParenthesis()
    {
        Gs1ElementString element = Assert.Single(Gs1.Parse(@"(10)A\(1\)"));
        Assert.Equal("A(1)", element.Value);
    }

    [Theory]
    [InlineData("(01)09501101530004", "check digit should be 3, not 4")]
    [InlineData("(00)106141411234567890", "check digit should be 7, not 0")]
    [InlineData("(01)0950110153000", "too short")]
    [InlineData("(01)095011015300031", "too long")]
    [InlineData("(10)ABCDEFGHIJKLMNOPQRSTU", "too long")]
    [InlineData("(10)AB€", "'€' at position 3 is not allowed")]
    [InlineData("(01)0950110153000A", "'A' at position 14 is not allowed")]
    [InlineData("(17)261331", "month 13")]
    [InlineData("(17)260230", "day 30")]
    [InlineData("(11)240229", null)]
    [InlineData("(15)261200", null)]
    [InlineData("(7007)240101240132", "day 32")]
    [InlineData("(8008)24010125", "hour 25")]
    [InlineData("(4321)2", "should be 0 or 1")]
    [InlineData("(99)", "empty")]
    [InlineData("(23)123", "not a GS1 Application Identifier")]
    [InlineData("(1)2", "not an AI of 2 to 4 digits")]
    [InlineData("01)2", "Expected an AI")]
    [InlineData("", "no element strings")]
    public void ChecksEachField(string text, string? error)
    {
        bool ok = Gs1.TryParse(text, out IReadOnlyList<Gs1ElementString>? elements);
        if (error is null)
        {
            Assert.True(ok);
            Assert.NotNull(elements);
            return;
        }

        Assert.False(ok);
        FormatException exception = Assert.Throws<FormatException>(() => Gs1.Parse(text));
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RangesOfAisAreKnown()
    {
        // 3100 to 3105 share one entry; 3106 is not defined.
        Assert.True(Gs1.TryParse("(3105)000100", out _));
        Assert.False(Gs1.TryParse("(3106)000100", out _));
        Assert.Throws<FormatException>(() => Gs1ElementString.Create("3106", "000100"));
    }
}
