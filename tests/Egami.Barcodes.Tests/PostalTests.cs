using Egami.Barcodes.Postal;
using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>RM4SCC, KIX, the Intelligent Mail barcode and Australia Post: bars, checks, sizes and bad input.</summary>
public class PostalTests
{
    /// <summary>
    /// Reads a 4-state grid's bars back as letters (F full, A ascender, D descender, T tracker), checking that every bar is one
    /// module wide with a light column after it and the same in each row of its part.
    /// </summary>
    internal static string Bars(BarcodeModules m, int extender, int tracker)
    {
        Assert.Equal((2 * extender) + tracker, m.Height);
        Assert.Equal(1, m.Width % 2);
        var bars = new System.Text.StringBuilder();
        for (int x = 0; x < m.Width; x += 2)
        {
            Assert.All(Enumerable.Range(extender, tracker), y => Assert.True(m[x, y]));
            Assert.True(x + 1 == m.Width || Enumerable.Range(0, m.Height).All(y => !m[x + 1, y]));
            Assert.All(Enumerable.Range(0, extender), y => Assert.Equal(m[x, 0], m[x, y]));
            Assert.All(Enumerable.Range(extender + tracker, extender), y => Assert.Equal(m[x, m.Height - 1], m[x, y]));
            bars.Append((m[x, 0], m[x, m.Height - 1]) switch { (true, true) => 'F', (true, false) => 'A', (false, true) => 'D', _ => 'T' });
        }

        return bars.ToString();
    }

    [Fact]
    public void RoyalMailCharactersFollowTheirTopAndBottomValues()
    {
        // 0 has top and bottom value 1 (weights 1 + 0: the last two bars), B top 2 (2 + 0) and bottom 0 (4 + 2), Z both 0.
        Assert.Equal("TTFF", Bars(Barcode.Kix("0").Modules, 3, 2));
        Assert.Equal("DFTA", Bars(Barcode.Kix("B").Modules, 3, 2));
        Assert.Equal("FFTT", Bars(Barcode.Kix("Z").Modules, 3, 2));
        Assert.Equal(Bars(Barcode.Kix("AZ09").Modules, 3, 2), Bars(Barcode.Kix("az09").Modules, 3, 2));

        // Every character has two ascenders and two descenders, and the 36 differ.
        string[] all = [.. RoyalMailEncoder.Characters.Select(c => Bars(Barcode.Kix(c.ToString()).Modules, 3, 2))];
        Assert.Equal(36, all.Distinct().Count());
        Assert.All(all, b => Assert.Equal(2, b.Count(c => c is 'F' or 'A')));
        Assert.All(all, b => Assert.Equal(2, b.Count(c => c is 'F' or 'D')));
    }

    [Fact]
    public void RoyalMailAddsTheCheckCharacterAndStartAndStopBars()
    {
        // Top values summing to 22 (4 modulo 6) and bottom values to 31 (1) give I, the example of the RM4SCC article.
        Assert.Equal('I', RoyalMailEncoder.Characters[(6 * ((22 + 5) % 6)) + ((31 + 5) % 6)]);
        int[] indices = RoyalMailEncoder.Indices("LU178XE2B", out int bad);
        Assert.Equal(-1, bad);
        int check = RoyalMailEncoder.Check(indices);
        Assert.Equal(indices.Sum(RoyalMailEncoder.Top) % 6, RoyalMailEncoder.Top(check));
        Assert.Equal(indices.Sum(RoyalMailEncoder.Bottom) % 6, RoyalMailEncoder.Bottom(check));

        Barcode barcode = Barcode.RoyalMail("lu178xe2b");
        string bars = Bars(barcode.Modules, 3, 2);
        Assert.Equal(1 + (4 * 10) + 1, bars.Length);
        Assert.Equal('A', bars[0]);
        Assert.Equal('F', bars[^1]);
        Assert.Equal(Bars(Barcode.Kix("LU178XE2B" + RoyalMailEncoder.Characters[check]).Modules, 3, 2), bars[1..^1]);
        Assert.Equal("LU178XE2B", barcode.HumanReadableText);
        Assert.Equal(BarcodeFormat.RoyalMail, barcode.Format);
        Assert.Equal(4, barcode.QuietZone);
        Assert.Equal(BarcodeFormat.Kix, Barcode.Kix("2500GG30X250").Format);
        Assert.Equal(4, Barcode.Kix("2500GG30X250").QuietZone);
        Assert.Equal(Bars(barcode.Modules, 3, 2), Bars(Barcode.Create(BarcodeFormat.RoyalMail, "LU178XE2B").Modules, 3, 2));
        Assert.Equal(4, Barcode.FromModules(BarcodeFormat.Kix, barcode.Modules).QuietZone);
    }

    [Theory]
    [InlineData("LU17 8XE", "' ' at 4")]
    [InlineData("AB-1", "'-' at 2")]
    [InlineData("1234É", "'É' at 4")]
    [InlineData("ı", "'ı' at 0")]
    public void RoyalMailAndKixNameABadCharacter(string text, string message)
    {
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => Barcode.RoyalMail(text)).Message, StringComparison.Ordinal);
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => Barcode.Kix(text)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Barcode.Kix(""));
        Assert.Throws<ArgumentNullException>(() => Barcode.RoyalMail(null!));
    }

    [Fact]
    public void RoyalMailDrawnAtItsModuleHasRoyalMailsSizes()
    {
        // 0.6 mm modules: bars 0.6 mm wide at 1.2 mm (21.2 per inch), full 4.8 mm, tracker 1.2 mm, ascender 1.8 mm above it.
        Barcode barcode = Barcode.RoyalMail("LU178XE2B");
        Drawing.Path path = barcode.ToPath(0.6f);
        Assert.Equal(2.4f, path.Bounds.X, 3);
        Assert.Equal(4.8f, path.Bounds.Height, 3);
        Assert.Equal(((2 * 42) - 1) * 0.6f, path.Bounds.Width, 3);
        using Raster<Gray8> raster = barcode.ToRaster(1);
        Assert.Equal(83 + 8, raster.Width);
        Assert.Equal(8 + 8, raster.Height);
    }

    [Theory]
    [InlineData(null, "ATTFATTDTTADTAATTDTDTATTDAFDDFADFDFTFFFFFTATFAAAATDFFTDAADFTFDTDT")]
    [InlineData("01234", "DTTAFADDTTFTDTFTFDTDDADADAFADFATDDFTAAAFDTTADFAAATDFDTDFADDDTDFFT")]
    [InlineData("012345678", "ADFTTAFDTTTTFATTADTAAATFTFTATDAAAFDDADATATDTDTTDFDTDATADADTDFFTFA")]
    [InlineData("01234567891", "AADTFFDFTDADTAADAATFDTDDAAADDTDTTDAFADADDDTFFFDDTTTADFAAADFTDAADA")]
    public void IntelligentMailMakesTheSpecsExamples(string? routing, string bars)
    {
        // USPS-B-3200 Appendix C.
        Barcode barcode = Barcode.IntelligentMail("01234567094987654321", routing);
        Assert.Equal(bars, Bars(barcode.Modules, 2, 2));
        Assert.Equal(routing is null ? "01234567094987654321" : "01234567094987654321 " + routing, barcode.HumanReadableText);
        Assert.Equal(BarcodeFormat.IntelligentMail, barcode.Format);
        Assert.Equal(6, barcode.QuietZone);
        Assert.Equal(bars, Bars(Barcode.Create(BarcodeFormat.IntelligentMail, "01234567094987654321" + (routing is null ? "" : "-" + routing)).Modules, 2, 2));
    }

    [Fact]
    public void IntelligentMailFollowsTheSpecsSteps()
    {
        // USPS-B-3200 Appendix C, example 4: the binary number, frame check and codewords.
        UInt128 binary = IntelligentMailEncoder.Binary("01234567094987654321", "01234567891");
        Assert.Equal(UInt128.Parse("016907B2A24ABC16A2E5C004B1", System.Globalization.NumberStyles.HexNumber, null), binary);
        int check = IntelligentMailEncoder.FrameCheck(binary);
        Assert.Equal(0x751, check);
        Assert.Equal([673, 787, 607, 1022, 861, 19, 816, 1294, 35, 602], IntelligentMailEncoder.Codewords(binary, check));
    }

    [Theory]
    [InlineData("0123456709498765432", null, "20 digits, not 19")]
    [InlineData("0153456709498765432 ", null, "' ' at 19")]
    [InlineData("05234567094987654321", null, "'5' at 1")]
    [InlineData("01234567094987654321", "1234", "not 4")]
    [InlineData("01234567094987654321", "1234x", "'x' at 4")]
    public void IntelligentMailNamesABadCode(string tracking, string? routing, string message) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => Barcode.IntelligentMail(tracking, routing)).Message, StringComparison.Ordinal);

    [Fact]
    public void IntelligentMailDrawnAtItsModuleHasUspssSizes()
    {
        // 1/44 inch modules: 65 bars at 22 per inch, 2.909 inches from the first to the last, 0.136 inch tall.
        Barcode barcode = Barcode.IntelligentMail("01234567094987654321", "01234567891");
        Drawing.Path path = barcode.ToPath(1f / 44);
        Assert.Equal(6f / 44, path.Bounds.Height, 4);
        Assert.Equal(129f / 44, path.Bounds.Width, 4);
        Assert.InRange(128f / 44, 2.667f, 3.225f);
        Assert.Equal(6f / 44, path.Bounds.X, 4);
    }
    /// <summary>Gets an Australia Post barcode's bar values: 0 full, 1 ascender, 2 descender, 3 tracker.</summary>
    private static string AustraliaPostValues(Barcode barcode) => Bars(barcode.Modules, 3, 2).Replace('F', '0').Replace('A', '1').Replace('D', '2').Replace('T', '3');

    [Fact]
    public void AustraliaPostMakesTheSpecsStandardExample()
    {
        // The spec's Diagram 9 and 10: FCC 11, DPID 39549554, check symbols 54 17 53 58.
        Barcode barcode = Barcode.AustraliaPost("39549554");
        Assert.Equal("1301011030121130121211331210131132213", AustraliaPostValues(barcode));
        Assert.Equal("11 39549554 54 17 53 58", barcode.HumanReadableText);
        Assert.Equal(BarcodeFormat.AustraliaPost, barcode.Format);
        Assert.Equal(11, barcode.QuietZone);

        // Diagram 14's text representation.
        Assert.Equal("11 96184209 32 57 38 54", Barcode.AustraliaPost("96184209").HumanReadableText);
        Assert.Equal(AustraliaPostValues(barcode), AustraliaPostValues(Barcode.Create(BarcodeFormat.AustraliaPost, "39549554")));
    }

    [Fact]
    public void AustraliaPostMakesTheSpecsCustomerBarcodes()
    {
        // The spec's Diagram 7: Customer Barcode 2 without customer information and Customer Barcode 3 with ABC123 in the C table,
        // filler bars after it.
        Assert.Equal(
            "1312301211120120021201333333333333333310031000312313",
            AustraliaPostValues(Barcode.AustraliaPost("54516251", new AustraliaPostOptions { Format = AustraliaPostFormat.Customer2 })));
        Assert.Equal(
            "1320021211120120021201000001002300301302333333333333313133002021313",
            AustraliaPostValues(Barcode.AustraliaPost("54516251ABC123")));
    }

    [Theory]
    [InlineData("39549554", null, null, 37, "11 39549554")]
    [InlineData("3954955412345678", null, null, 52, "59 39549554 12345678")]
    [InlineData("39549554123456789", null, null, 67, "62 39549554 123456789")]
    [InlineData("39549554Ab #9", null, null, 52, "59 39549554 Ab #9")]
    [InlineData("39549554Ab #95", null, null, 67, "62 39549554 Ab #95")]
    [InlineData("3954955412", null, AustraliaPostEncoding.C, 52, "59 39549554 12")]
    [InlineData("39549554", AustraliaPostFormat.ReplyPaid, null, 37, "45 39549554")]
    [InlineData("39549554", AustraliaPostFormat.Routing, null, 37, "87 39549554")]
    [InlineData("39549554", AustraliaPostFormat.Redirection, null, 37, "92 39549554")]
    [InlineData("3954955412", AustraliaPostFormat.Customer3, null, 67, "62 39549554 12")]
    public void AustraliaPostChoosesItsFormatAndTable(string text, AustraliaPostFormat? format, AustraliaPostEncoding? encoding, int bars, string readable)
    {
        Barcode barcode = Barcode.AustraliaPost(text, new AustraliaPostOptions { Format = format, Encoding = encoding });
        string values = AustraliaPostValues(barcode);
        Assert.Equal(bars, values.Length);
        Assert.StartsWith("13", values, StringComparison.Ordinal);
        Assert.EndsWith("13", values, StringComparison.Ordinal);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(readable) + "( [0-9][0-9]){4}$", barcode.HumanReadableText);
    }

    [Theory]
    [InlineData("3954955", null, null, "not 7")]
    [InlineData("3954x554", null, null, "'x' at 4")]
    [InlineData("39549554AB-", null, null, "'-' at 10")]
    [InlineData("3954955412a", null, AustraliaPostEncoding.N, "'a' at 10")]
    [InlineData("395495541", AustraliaPostFormat.Standard, null, "without customer information")]
    [InlineData("39549554123456789", AustraliaPostFormat.Customer2, null, "takes 18 bars")]
    [InlineData("395495541234567890123456", null, null, "takes 32 bars")]
    [InlineData("3954955412345678901", null, AustraliaPostEncoding.C, "takes 33 bars")]
    public void AustraliaPostNamesABadText(string text, AustraliaPostFormat? format, AustraliaPostEncoding? encoding, string message) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => Barcode.AustraliaPost(text, new AustraliaPostOptions { Format = format, Encoding = encoding })).Message, StringComparison.Ordinal);

    [Fact]
    public void AustraliaPostDrawnAtItsModuleHasAustraliaPostsSizes()
    {
        // 0.55 mm modules: tracker 1.1 mm, ascender and descender 2.75 mm, full 4.4 mm; 37 bars 40.15 mm long, 6.05 mm quiet zone.
        Barcode barcode = Barcode.AustraliaPost("39549554");
        Drawing.Path path = barcode.ToPath(0.55f);
        Assert.Equal(4.4f, path.Bounds.Height, 3);
        Assert.Equal(((2 * 37) - 1) * 0.55f, path.Bounds.Width, 3);
        Assert.InRange(path.Bounds.Width, 37.0f, 42.2f);
        Assert.Equal(6.05f, path.Bounds.X, 3);
    }
}
