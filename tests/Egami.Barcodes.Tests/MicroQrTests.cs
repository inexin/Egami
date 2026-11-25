using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Tests;

public class MicroQrTests
{
    [Fact]
    public void FormatWordsMatchTheStandardsTable()
    {
        // ISO/IEC 18004 Table C.2: symbol numbers 0 (M1) to 7 (M4-Q), each with masks 0 to 3.
        int[] table =
        [
            0x4445, 0x4172, 0x4E2B, 0x4B1C, 0x55AE, 0x5099, 0x5FC0, 0x5AF7, 0x6793, 0x62A4, 0x6DFD, 0x68CA, 0x7678, 0x734F, 0x7C16, 0x7921,
            0x06DE, 0x03E9, 0x0CB0, 0x0987, 0x1735, 0x1202, 0x1D5B, 0x186C, 0x2508, 0x203F, 0x2F66, 0x2A51, 0x34E3, 0x31D4, 0x3E8D, 0x3BBA,
        ];
        Assert.Equal(table, Enumerable.Range(0, 32).Select(i => MicroQrEncoder.FormatBits(i >> 2, i & 3)));
    }

    [Fact]
    public void MatchesTheStandardsWorkedExample()
    {
        // "01234567" as M2-L (ISO/IEC 18004 Annex I.3): the data codewords, then 5 check codewords.
        Barcode barcode = Barcode.MicroQr("01234567", new MicroQrOptions { RaiseErrorCorrection = false });
        Assert.Equal(13, barcode.Modules.Width);
        bool[] modules = new bool[13 * 13];
        for (int y = 0; y < 13; y++)
        {
            for (int x = 0; x < 13; x++)
            {
                modules[(y * 13) + x] = barcode.Modules[x, y];
            }
        }

        int mask = Enumerable.Range(0, 4).Single(m => MicroQrEncoder.FormatBits(1, m) == Format(barcode.Modules));
        bool[] function = MicroQrEncoder.FunctionMask(13);
        var bits = new List<bool>();
        foreach (int index in MicroQrEncoder.Order(13, function))
        {
            bits.Add(modules[index] ^ MicroQrEncoder.Masked(mask, index % 13, index / 13));
        }

        byte[] codewords = [.. bits.Take(80).Chunk(8).Select(c => (byte)c.Aggregate(0, (a, b) => (a << 1) | (b ? 1 : 0)))];
        Assert.Equal([0x40, 0x18, 0xAC, 0xC3, 0x00, 0x86, 0x0D, 0x22, 0xAE, 0x30], codewords);
    }

    [Theory]
    [InlineData("12345", 1, 'L')]
    [InlineData("123456", 2, 'M')]
    [InlineData("1234567890", 2, 'L')]
    [InlineData("ABCDE", 2, 'M')]
    [InlineData("ABCDEF", 2, 'L')]
    [InlineData("abcdefg", 3, 'M')]
    [InlineData("abcdefghi", 3, 'L')]
    [InlineData("12345678901234567890123456789012345", 4, 'L')]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", 4, 'L')]
    [InlineData("abcdefghijklmno", 4, 'L')]
    [InlineData("ABCDEFGHIJKLM", 3, 'L')]
    [InlineData("ABCDEFGHIJKLMNO", 4, 'M')]
    [InlineData("123456789012345678901234", 4, 'M')]
    public void PicksTheSmallestVersionAndRaisesTheLevel(string text, int version, char level)
    {
        Barcode barcode = Barcode.MicroQr(text);
        Assert.Equal((version, level), VersionAndLevel(barcode.Modules));
        Assert.Equal(BarcodeFormat.MicroQr, barcode.Format);
        Assert.Equal(2, barcode.QuietZone);
    }

    [Fact]
    public void OptionsAreHonoured()
    {
        Assert.Equal((2, 'M'), VersionAndLevel(Barcode.MicroQr("1", new MicroQrOptions { ErrorCorrection = QrErrorCorrection.M }).Modules));
        Assert.Equal((4, 'Q'), VersionAndLevel(Barcode.MicroQr("1", new MicroQrOptions { ErrorCorrection = QrErrorCorrection.Q }).Modules));
        Assert.Equal((3, 'L'), VersionAndLevel(Barcode.MicroQr("1", new MicroQrOptions { MinVersion = 3, RaiseErrorCorrection = false }).Modules));
        Assert.Equal((3, 'M'), VersionAndLevel(Barcode.MicroQr("1", new MicroQrOptions { MinVersion = 3 }).Modules));
        Assert.Equal((4, 'L'), VersionAndLevel(Barcode.MicroQr("\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57\u6f22").Modules));
        Assert.Equal(ReferenceTests.Grid(Barcode.MicroQr("HELLO").Modules), ReferenceTests.Grid(Barcode.Create(BarcodeFormat.MicroQr, "HELLO").Modules));
        Assert.Equal(2, Barcode.FromModules(BarcodeFormat.MicroQr, Barcode.MicroQr("1").Modules).QuietZone);
        Assert.Throws<ArgumentException>(() => Barcode.MicroQr(new string('1', 36)));
        Assert.Throws<ArgumentException>(() => Barcode.MicroQr(new string('a', 16)));
        Assert.Throws<ArgumentException>(() => Barcode.MicroQr("ABCDEFGHIJKLMN", new MicroQrOptions { ErrorCorrection = QrErrorCorrection.Q }));
        Assert.Throws<ArgumentException>(() => Barcode.MicroQr("\u0e01\u0e02"));
        Assert.Throws<ArgumentException>(() => Barcode.MicroQr(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicroQrOptions { ErrorCorrection = QrErrorCorrection.H });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicroQrOptions { MinVersion = 5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicroQrOptions { MinVersion = 0 });
    }

    [Fact]
    public void EveryVersionAndLevelHoldsItsCapacity()
    {
        // Digits per version and level (ISO/IEC 18004 Table 7); one more does not fit.
        foreach ((int version, QrErrorCorrection level, int digits) in new[]
        {
            (1, QrErrorCorrection.L, 5), (2, QrErrorCorrection.L, 10), (2, QrErrorCorrection.M, 8), (3, QrErrorCorrection.L, 23),
            (3, QrErrorCorrection.M, 18), (4, QrErrorCorrection.L, 35), (4, QrErrorCorrection.M, 30), (4, QrErrorCorrection.Q, 21),
        })
        {
            var options = new MicroQrOptions { ErrorCorrection = level, MinVersion = version, RaiseErrorCorrection = false };
            Assert.Equal((version, level.ToString()[0]), VersionAndLevel(Barcode.MicroQr(new string('7', digits), options).Modules));
            if (version < 4)
            {
                Assert.True(VersionAndLevel(Barcode.MicroQr(new string('7', digits + 1), options).Modules).Version > version);
            }
        }
    }

    /// <summary>Reads a Micro QR Code's version from its size and its level from the format word.</summary>
    internal static (int Version, char Level) VersionAndLevel(BarcodeModules modules)
    {
        int symbol = (Format(modules) ^ 0x4445) >> 12;
        return ((modules.Width - 9) / 2, symbol switch { 0 or 1 or 3 or 5 => 'L', 7 => 'Q', _ => 'M' });
    }

    private static int Format(BarcodeModules modules)
    {
        int bits = 0;
        for (int i = 1; i <= 8; i++)
        {
            bits = (bits << 1) | (modules[i, 8] ? 1 : 0);
        }

        for (int i = 7; i >= 1; i--)
        {
            bits = (bits << 1) | (modules[8, i] ? 1 : 0);
        }

        return bits;
    }
}
