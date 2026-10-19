using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Tests;

public class QrTests
{
    [Fact]
    public void MatchesTheStandardsWorkedExample()
    {
        // "HELLO WORLD" as version 1-M: one alphanumeric segment, then terminator and padding, then the check codewords.
        var input = new QrEncoder.Input(Encoding.ASCII.GetBytes("HELLO WORLD"), false, 0, false);
        byte[] codewords = QrEncoder.Codewords(QrEncoder.Bits(input, 0), 1, 1);
        Assert.Equal(
            [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17, 196, 35, 39, 119, 235, 215, 231, 226, 93, 23],
            codewords.Select(b => (int)b));
    }

    [Theory]
    [InlineData(2, new[] { 6, 18 })]
    [InlineData(7, new[] { 6, 22, 38 })]
    [InlineData(16, new[] { 6, 26, 50, 74 })]
    [InlineData(32, new[] { 6, 34, 60, 86, 112, 138 })]
    [InlineData(36, new[] { 6, 24, 50, 76, 102, 128, 154 })]
    [InlineData(40, new[] { 6, 30, 58, 86, 114, 142, 170 })]
    public void AlignmentPatternsAreWhereTheStandardPutsThem(int version, int[] positions) =>
        Assert.Equal(positions, QrMatrix.AlignmentPositions(version));

    [Fact]
    public void FormatAndVersionBitsAreInPlace()
    {
        // Level L with mask 0 is 111011111000100 (ISO/IEC 18004 Table C.1), first bit next to the top-left finder.
        Barcode barcode = Barcode.Qr("1", new QrOptions { ErrorCorrection = QrErrorCorrection.L, Mask = 0 });
        string format = string.Concat(new[] { (0, 8), (1, 8), (2, 8), (3, 8), (4, 8), (5, 8), (7, 8), (8, 8), (8, 7), (8, 5), (8, 4), (8, 3), (8, 2), (8, 1), (8, 0) }
            .Select(p => barcode.Modules[p.Item1, p.Item2] ? '1' : '0'));
        Assert.Equal("111011111000100", format);

        // Version 7's information is 000111110010010100 (Table D.1), bit 0 at the top left of the block above the bottom-left finder.
        Barcode v7 = Barcode.Qr("1", new QrOptions { Version = 7 });
        int size = v7.Modules.Width, bits = 0;
        for (int i = 17; i >= 0; i--)
        {
            bits = (bits << 1) | (v7.Modules[i / 3, size - 11 + (i % 3)] ? 1 : 0);
        }

        Assert.Equal(0b000111110010010100, bits);
        Assert.Equal(45, size);
    }

    [Fact]
    public void OptionsAreHonoured()
    {
        Assert.Equal((1, 'M'), ReferenceTests.QrVersionAndLevel(Barcode.Qr("HELLO WORLD", new QrOptions { ErrorCorrection = QrErrorCorrection.M }).Modules));
        Assert.Equal((1, 'Q'), ReferenceTests.QrVersionAndLevel(Barcode.Qr("HELLO WORLD").Modules));
        Assert.Equal((5, 'H'), ReferenceTests.QrVersionAndLevel(Barcode.Qr("HELLO WORLD", new QrOptions { Version = 5 }).Modules));
        Assert.Throws<ArgumentException>(() => Barcode.Qr(new string('A', 100), new QrOptions { Version = 1 }));
        Assert.Throws<ArgumentException>(() => Barcode.Qr(new string('9', 7090)));
        Assert.Throws<ArgumentException>(() => Barcode.Qr(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QrOptions { Version = 41 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new QrOptions { Mask = 8 });

        Barcode bytes = Barcode.Qr([0x00, 0xFF, 0x80]);
        Assert.Equal(BarcodeFormat.Qr, bytes.Format);
        Assert.Equal(4, bytes.QuietZone);
        Assert.Null(bytes.HumanReadableText);
    }

    [Fact]
    public void TheLargestSymbolsFit()
    {
        // Version 40-L holds 7089 digits, 4296 alphanumeric characters or 2953 bytes.
        Assert.Equal(177, Barcode.Qr(new string('7', 7089), new QrOptions { ErrorCorrection = QrErrorCorrection.L }).Modules.Width);
        Assert.Equal(177, Barcode.Qr(new string('A', 4296), new QrOptions { ErrorCorrection = QrErrorCorrection.L }).Modules.Width);
        Assert.Equal(177, Barcode.Qr(new string('a', 2953), new QrOptions { ErrorCorrection = QrErrorCorrection.L }).Modules.Width);
        Assert.Throws<ArgumentException>(() => Barcode.Qr(new string('a', 2954), new QrOptions { ErrorCorrection = QrErrorCorrection.L }));
    }

    [Fact]
    public void SegmentsAreShortest()
    {
        // 16 digits between letters: numeric mode pays for its header.
        var mixed = new QrEncoder.Input(Encoding.ASCII.GetBytes("abc1234567890123456def"), false, 0, false);
        var bytesOnly = 4 + 8 + (22 * 8);
        Assert.True(QrEncoder.Bits(mixed, 0).Count < bytesOnly);

        // Kanji takes 13 bits a character where bytes would take 16.
        byte[] shiftJis = [0x93, 0xFA, 0x96, 0x7B];
        Assert.Equal(4 + 8 + 26, QrEncoder.Bits(new QrEncoder.Input(shiftJis, true, 0, false), 0).Count);
        Assert.Equal(4 + 8 + 32, QrEncoder.Bits(new QrEncoder.Input(shiftJis, false, 0, false), 0).Count);
    }
}
