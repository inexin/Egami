using Egami.Barcodes.Postal;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

public class JapanPostTests
{
    /// <summary>The bars of each value 0 to 18 as top-to-bottom thirds (A ascender, D descender, F full, T tracker).</summary>
    private const string Patterns = "FTT FFT FDA DFA FAD FTF DAF AFD ADF TFF TFT DAT DTA ADT TDA ATD TAD TTF FFF";

    /// <summary>The values a symbol holds, read back from its grid: the 20 data characters and the check character.</summary>
    private static int[] Values(Barcode barcode)
    {
        BarcodeModules m = barcode.Modules;
        Assert.Equal(6, m.Height);
        Assert.Equal(133, m.Width);
        string bars = string.Concat(Enumerable.Range(0, 67).Select(i =>
        {
            int x = 2 * i;
            Assert.True(m[x, 2] && m[x, 3]);
            Assert.False(x + 1 < m.Width && Enumerable.Range(0, 6).Any(y => m[x + 1, y]));
            Assert.Equal(m[x, 0], m[x, 1]);
            Assert.Equal(m[x, 4], m[x, 5]);
            return (m[x, 0], m[x, 5]) switch { (true, true) => 'F', (true, false) => 'A', (false, true) => 'D', _ => 'T' };
        }));
        Assert.Equal("FD", bars[..2]);
        Assert.Equal("DF", bars[^2..]);
        string[] patterns = Patterns.Split(' ');
        return [.. Enumerable.Range(0, 21).Select(i => Array.IndexOf(patterns, bars.Substring(2 + (3 * i), 3)))];
    }

    private static int[] Expected(string data, int check)
    {
        var values = new List<int>();
        foreach (string token in data.Split(' '))
        {
            values.Add(token switch { "-" => 10, ['C', 'C', char n] => 10 + (n - '0'), _ => int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) });
        }

        while (values.Count < 20)
        {
            values.Add(14);
        }

        values.Add(check);
        return [.. values];
    }

    /// <summary>The examples of the verification sheet on page 25 of Japan Post's manual, with the check character it prints.</summary>
    [Theory]
    [InlineData("263-0023 3-30-8-403", "2 6 3 0 0 2 3 3 - 3 0 - 8 - 4 0 3", 5)]
    [InlineData("110-0016 5-6-3-10", "1 1 0 0 0 1 6 5 - 6 - 3 - 1 0", 9)]
    [InlineData("060-0906 4-6", "0 6 0 0 9 0 6 4 - 6", 9)]
    [InlineData("065-0006 8-10", "0 6 5 0 0 0 6 8 - 1 0", 9)]
    [InlineData("407-0033 400", "4 0 7 0 0 3 3 4 0 0", 10)]
    [InlineData("273-0102 3-20-5B604", "2 7 3 0 1 0 2 3 - 2 0 - 5 CC1 1 6 0 4", 0)]
    [InlineData("198-0036 11-6-1-601", "1 9 8 0 0 3 6 1 1 - 6 - 1 - 6 0 1", 18)]
    [InlineData("027-0203 21-480", "0 2 7 0 2 0 3 2 1 - 4 8 0", 15)]
    [InlineData("590-0016 4-6-19", "5 9 0 0 0 1 6 4 - 6 - 1 9", 12)]
    [InlineData("080-0831 7-28", "0 8 0 0 8 3 1 7 - 2 8", 17)]
    [InlineData("317-0055 6-7-14-2", "3 1 7 0 0 5 5 6 - 7 - 1 4 - 2", 11)]
    [InlineData("650-0046 9-7-6A1-1", "6 5 0 0 0 4 6 9 - 7 - 6 CC1 0 1 - 1", 5)]
    [InlineData("623-0011 6-7LB106", "6 2 3 0 0 1 1 6 - 7 CC2 1 CC1 1 1 0 6", 4)]
    [InlineData("100-0013 1-3-2-503", "1 0 0 0 0 1 3 1 - 3 - 2 - 5 0 3", 9)]
    [InlineData("910-0067 3-80-25J1-2B", "9 1 0 0 0 6 7 3 - 8 0 - 2 5 CC1 9 1 - 2 CC1", 9)]
    public void ManualExamples(string data, string expected, int check)
    {
        Barcode barcode = Barcode.JapanPost(data);
        Assert.Equal(Expected(expected, check), Values(barcode));
        Assert.Equal(BarcodeFormat.JapanPost, barcode.Format);
        Assert.Equal(4, barcode.QuietZone);
        Assert.Equal(data, barcode.HumanReadableText);
    }

    [Fact]
    public void LongAddressLosesItsEndAsTheManualSays()
    {
        // Page 20: 064-0804 with 29-1524-23-2-501 keeps 13 characters of the address number.
        Assert.Equal(Expected("0 6 4 0 8 0 4 2 9 - 1 5 2 4 - 2 3 - 2 -", 3), Values(Barcode.JapanPost("064-0804 29-1524-23-2-501")));
        Assert.Equal("064-0804 29-1524-23-2-501", Barcode.JapanPost("064-0804 29-1524-23-2-501").HumanReadableText);
        Assert.Throws<ArgumentException>(() => Barcode.JapanPost("064-0804 29-1524-23-2-501", new JapanPostOptions { TruncateAddress = false }));
        Assert.Throws<ArgumentException>(() => Barcode.JapanPost("910-0067 3-80-25J1-2B", new JapanPostOptions { TruncateAddress = false }));

        // 13 characters fit exactly, a letter as the 12th and 13th too.
        Assert.Equal(Expected("1 0 0 0 0 1 3 1 2 3 4 5 6 7 8 9 0 1 2 3", 1), Values(Barcode.JapanPost("1000013 1234567890123", new JapanPostOptions { TruncateAddress = false })));
        Assert.Equal(Expected("1 0 0 0 0 1 3 1 2 3 4 5 6 7 8 9 0 1 CC3 5", 7), Values(Barcode.JapanPost("1000013 12345678901Z", new JapanPostOptions { TruncateAddress = false })));
    }

    [Fact]
    public void EveryLetterIsAControlCodeAndADigit()
    {
        for (char c = 'A'; c <= 'Z'; c++)
        {
            int[] values = Values(Barcode.JapanPost($"1000013 1{c}2"));
            Assert.Equal([11 + ((c - 'A') / 10), (c - 'A') % 10], values[8..10]);
            Assert.Equal(values, Values(Barcode.JapanPost($"1000013 1{char.ToLowerInvariant(c)}2")));
        }
    }

    [Fact]
    public void CheckCharacterMakesTheSumAMultipleOf19()
    {
        foreach (string data in (string[])["0000000", "9999999 9999999999999", "1234567 ZZZZZZ-", "0000000-A", "5550000 --"])
        {
            Assert.Equal(0, Values(Barcode.JapanPost(data)).Sum() % 19);
        }

        Assert.Equal(0, JapanPostEncoder.CheckValue([.. Enumerable.Repeat((byte)0, 20)]));
        Assert.Equal(18, JapanPostEncoder.CheckValue([1, .. Enumerable.Repeat((byte)0, 19)]));
    }

    [Fact]
    public void PostalCodeSeparatorsAreDropped()
    {
        int[] plain = Values(Barcode.JapanPost("1000013 1-3-2-503"));
        foreach (string data in (string[])["100-0013 1-3-2-503", "1000013-1-3-2-503", "100-0013  -  1-3-2-503", "1000013 1-3-2-503"])
        {
            Assert.Equal(plain, Values(Barcode.JapanPost(data)));
            Assert.Equal("100-0013 1-3-2-503", Barcode.JapanPost(data).HumanReadableText);
        }

        Assert.Equal("100-0013", Barcode.JapanPost("100-0013").HumanReadableText);
        Assert.Equal("100-0013 3B", Barcode.JapanPost("100-0013 3b").HumanReadableText);
        Assert.Equal(Expected("1 0 0 0 0 1 3", 3), Values(Barcode.JapanPost("1000013")));
    }

    [Theory]
    [InlineData("", "the data ends early")]
    [InlineData("100001", "the data ends early")]
    [InlineData("10-00013", "'-' at 2")]
    [InlineData("100--0013", "'-' at 4")]
    [InlineData("A000013", "'A' at 0")]
    [InlineData("1000013 1 3", "' ' at 9")]
    [InlineData("1000013 1/3", "'/' at 9")]
    [InlineData("1000013 1-３", "'３' at 10")]
    [InlineData("1000013 Ä", "'Ä' at 8")]
    public void BadDataNamesTheCharacter(string data, string message)
    {
        if (data.Length == 0)
        {
            Assert.Throws<ArgumentException>(() => Barcode.JapanPost(data));
            return;
        }

        ArgumentException e = Assert.Throws<ArgumentException>(() => Barcode.JapanPost(data));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.Equal("data", e.ParamName);
    }

    [Fact]
    public void FormatsAndFactories()
    {
        Assert.Equal(BarcodeFormat.JapanPost, BarcodeFormat.Postal & BarcodeFormat.JapanPost);
        Assert.Equal(BarcodeFormat.None, BarcodeFormat.All & BarcodeFormat.Postal);
        Barcode barcode = Barcode.Create(BarcodeFormat.JapanPost, "100-0013 1-3-2-503");
        Assert.Equal(Values(Barcode.JapanPost("100-0013 1-3-2-503")), Values(barcode));
        Assert.Equal(4, Barcode.FromModules(BarcodeFormat.JapanPost, barcode.Modules).QuietZone);
        Assert.Throws<ArgumentException>(() => Barcode.FromModules(BarcodeFormat.JapanPost, BarcodeModules.CreateLinear([1, 1, 1], 6)));
        Assert.Throws<ArgumentNullException>(() => Barcode.JapanPost(null!));
    }

    [Fact]
    public void DrawnAtTheManualsModuleItHasTheManualsSize()
    {
        // 0.6 mm modules: 67 bars 0.6 mm wide at a 1.2 mm pitch (79.8 mm) and 3.6 mm tall, inside a 2.4 mm quiet zone.
        Barcode barcode = Barcode.JapanPost("100-0013 1-3-2-503");
        Drawing.Path path = barcode.ToPath(0.6f);
        Assert.Equal(2.4f, path.Bounds.X, 3);
        Assert.Equal(2.4f, path.Bounds.Y, 3);
        Assert.Equal(79.8f, path.Bounds.Width, 3);
        Assert.Equal(3.6f, path.Bounds.Height, 3);

        using Raster<Gray8> raster = barcode.ToRaster(2);
        Assert.Equal((133 + 8) * 2, raster.Width);
        Assert.Equal((6 + 8) * 2, raster.Height);
        Assert.Equal(0, raster.GetPixel(8, 8).L);
        Assert.Equal(255, raster.GetPixel(8, 26).L);
        Assert.Equal(255, raster.GetPixel(10, 12).L);
        Assert.Equal(255, raster.GetPixel(12, 8).L);
        Assert.Equal(0, raster.GetPixel(12, 18).L);
    }

    [Fact]
    public void JapanPostIsNotRead()
    {
        using Raster<Gray8> raster = Barcode.JapanPost("100-0013 1-3-2-503").ToRaster(4);
        Assert.Empty(raster.ReadBarcodes());
        Assert.Empty(raster.ReadBarcodes(new BarcodeReaderOptions { Formats = BarcodeFormat.Postal }));
    }
}
