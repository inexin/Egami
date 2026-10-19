using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Tests;

public class Pdf417Tests
{
    [Fact]
    public void CompactionMatchesTheStandardsExamples()
    {
        // Numeric compaction of 000213298174000 (ISO/IEC 15438 5.4.4) and byte compaction of six bytes (5.4.3).
        Assert.Equal([902, 1, 624, 434, 632, 282, 200], Pdf417Encoder.HighLevel(Encoding.ASCII.GetBytes("000213298174000"), 0));
        var bytes = new List<int>();
        Pdf417Encoder.Bytes(Encoding.ASCII.GetBytes("alcool"), bytes);
        Assert.Equal([924, 163, 238, 432, 766, 244], bytes);
    }

    [Fact]
    public void TextUsesSubModes()
    {
        // "PDF417": P D F in Alpha, a latch to Mixed, 4 1 7: (15, 3), (5, 28), (4, 1), (7, padding 29).
        Assert.Equal([15 * 30 + 3, 5 * 30 + 28, 4 * 30 + 1, 7 * 30 + 29], Pdf417Encoder.HighLevel(Encoding.ASCII.GetBytes("PDF417"), 0));

        // A single byte inside text is shifted with 913 rather than latched.
        List<int> shifted = Pdf417Encoder.HighLevel(Encoding.Latin1.GetBytes("Hello é world"), 0);
        Assert.Contains(913, shifted);
        Assert.DoesNotContain(901, shifted);
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(100, 3)]
    [InlineData(250, 4)]
    [InlineData(500, 5)]
    public void ErrorLevelFollowsTheStandardsTable(int bytes, int level)
    {
        // Byte compaction: 6 bytes in 5 codewords, so the data codewords grow with the bytes.
        byte[] data = [.. Enumerable.Range(0, bytes).Select(i => (byte)(128 + (i % 100)))];
        Pdf417Encoder.Result result = Pdf417Encoder.Encode(data, 0, null, null, false)!.Value;
        Assert.Equal(level, result.Level);
    }

    [Fact]
    public void ShapeAndCompactForm()
    {
        Barcode full = Barcode.Pdf417("Shape test 123", new Pdf417Options { Columns = 4 });
        Barcode compact = Barcode.Pdf417("Shape test 123", new Pdf417Options { Columns = 4, Compact = true });

        // Start, left indicator, 4 data columns, right indicator and stop; compact drops the right indicator and ends with one bar.
        Assert.Equal((8 * 17) + 1, full.Modules.Width);
        Assert.Equal((6 * 17) + 1, compact.Modules.Width);
        Assert.Equal(0, full.Modules.Height % Pdf417Encoder.RowHeight);
        Assert.Equal(2, full.QuietZone);

        // Automatic columns aim for about 3:1.
        Barcode auto = Barcode.Pdf417(new string('A', 300));
        double ratio = (double)auto.Modules.Width / auto.Modules.Height;
        Assert.InRange(ratio, 2, 4.5);
    }

    [Fact]
    public void BadInputThrows()
    {
        Assert.Throws<ArgumentException>(() => Barcode.Pdf417(""));
        Assert.Throws<ArgumentException>(() => Barcode.Pdf417(new string('é', 1900)));
        Assert.Throws<ArgumentException>(() => Barcode.Pdf417(new string('A', 1000), new Pdf417Options { ErrorLevel = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pdf417Options { Columns = 31 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pdf417Options { ErrorLevel = 9 });
        Assert.Equal(BarcodeFormat.Pdf417, Barcode.Pdf417([1, 2, 3]).Format);
    }
}
