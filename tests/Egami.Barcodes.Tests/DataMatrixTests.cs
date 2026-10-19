using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Tests;

public class DataMatrixTests
{
    private static readonly DataMatrixEncoder.Shapes s_square = new(true, false, false);

    [Fact]
    public void MatchesTheStandardsExample()
    {
        // "123456" is three digit pairs in ASCII, 142 164 186, in a 10 x 10 symbol (ISO/IEC 16022 Annex O).
        (DataMatrixTables.Size size, BarcodeModules modules) = DataMatrixEncoder.Encode(new(Encoding.ASCII.GetBytes("123456"), 0, false), s_square)!.Value;
        Assert.Equal((10, 10), (size.Rows, size.Columns));
        Assert.Equal(10, modules.Width);

        // The finder: solid left column and bottom row, alternating top row and right column.
        for (int i = 0; i < 10; i++)
        {
            Assert.True(modules[0, i]);
            Assert.True(modules[i, 9]);
            Assert.Equal(i % 2 == 0, modules[i, 0]);
            Assert.Equal(i % 2 == 1, modules[9, i]);
        }
    }

    [Fact]
    public void ShapesPickTheirSizes()
    {
        Assert.Equal((12, 12), Size(Barcode.DataMatrix("ABC123")));
        Assert.Equal((8, 18), Size(Barcode.DataMatrix("ABC123", new DataMatrixOptions { Shape = DataMatrixShape.Rectangle })));
        Assert.Equal((12, 12), Size(Barcode.DataMatrix("ABC123", new DataMatrixOptions { Shape = DataMatrixShape.Any })));

        // 86 digits are 43 codewords: 12 x 64 with DMRE, else 26 x 26 square or 16 x 48; 100 digits overflow the standard rectangles.
        string digits = string.Concat(Enumerable.Range(0, 86).Select(i => (char)('0' + (i % 10))));
        Assert.Equal((26, 26), Size(Barcode.DataMatrix(digits)));
        Assert.Equal((12, 64), Size(Barcode.DataMatrix(digits, new DataMatrixOptions { Shape = DataMatrixShape.Rectangle, AllowDmre = true })));
        Assert.Equal((16, 48), Size(Barcode.DataMatrix(digits, new DataMatrixOptions { Shape = DataMatrixShape.Rectangle })));
        Assert.Throws<ArgumentException>(() => Barcode.DataMatrix(new string('1', 100), new DataMatrixOptions { Shape = DataMatrixShape.Rectangle }));
    }

    [Fact]
    public void LimitsAndOverloads()
    {
        Assert.Equal((144, 144), Size(Barcode.DataMatrix(new string('7', 3116))));
        Assert.Throws<ArgumentException>(() => Barcode.DataMatrix(new string('7', 3117)));
        Assert.Throws<ArgumentException>(() => Barcode.DataMatrix(new string('é', 1600)));
        Assert.Throws<ArgumentException>(() => Barcode.DataMatrix(""));

        Barcode bytes = Barcode.DataMatrix([0, 1, 2, 255]);
        Assert.Equal(BarcodeFormat.DataMatrix, bytes.Format);
        Assert.Equal(1, bytes.QuietZone);
        Assert.Equal("(01)09501101530003", Barcode.DataMatrix(Gs1.Parse("(01)09501101530003")).HumanReadableText);
    }

    private static (int Rows, int Columns) Size(Barcode barcode) => (barcode.Modules.Height, barcode.Modules.Width);
}
