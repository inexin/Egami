using Egami.Formats;
using Egami.Formats.Jbig2;

namespace Egami.Tests.Formats.Jbig2;

/// <summary>The standard Huffman tables, the code assignment of T.88 B.3, table segments and symbol ID tables.</summary>
public class Jbig2HuffmanTests
{
    /// <summary>Each standard table's lines as T.88 B.5 prints them: the range low (or OOB) and the prefix code.</summary>
    public static TheoryData<int, string> Tables() => new()
    {
        { 1, "0:0 16:10 272:110 65808:111" },
        { 2, "0:0 1:10 2:110 3:1110 11:11110 75:111110 OOB:111111" },
        { 3, "-256:11111110 0:0 1:10 2:110 3:1110 11:11110 -257:11111111 75:1111110 OOB:111110" },
        { 4, "1:0 2:10 3:110 4:1110 12:11110 76:11111" },
        { 5, "-255:1111110 1:0 2:10 3:110 4:1110 12:11110 -256:1111111 76:111110" },
        { 6, "-2048:11100 -1024:1000 -512:1001 -256:1010 -128:11101 -64:11110 -32:1011 0:00 128:010 256:011 512:1100 1024:1101 -2049:111110 2048:111111" },
        { 7, "-1024:1000 -512:000 -256:1001 -128:11010 -64:11011 -32:1010 0:1011 32:11100 64:11101 128:1100 256:001 512:010 1024:011 -1025:11110 2048:11111" },
        { 8, "-15:11111100 -7:111111100 -5:11111101 -3:111111101 -2:1111100 -1:1010 0:00 2:11010 3:111010 4:100 20:111011 22:1011 38:1100 70:11011 134:11100 262:111100 390:1111101 646:111101 -16:111111110 1670:111111111 OOB:01" },
        { 9, "-31:11111100 -15:111111100 -11:11111101 -7:111111101 -5:1111100 -3:1010 -1:010 1:011 3:11010 5:111010 7:100 39:111011 43:1011 75:1100 139:11011 267:11100 523:111100 779:1111101 1291:111101 -32:111111110 3339:111111111 OOB:00" },
        { 10, "-21:1111010 -5:11111100 -4:1111011 -3:11000 -2:00 2:11001 3:110110 4:1111100 5:11111101 6:01 70:11010 102:110111 134:111000 198:111001 326:111010 582:111011 1094:111100 2118:1111101 -22:11111110 4166:11111111 OOB:10" },
        { 11, "1:0 2:10 4:1100 5:1101 7:11100 9:11101 13:111100 17:1111010 21:1111011 29:1111100 45:1111101 77:1111110 141:1111111" },
        { 12, "1:0 2:10 3:110 5:11100 6:11101 8:111100 10:1111010 11:1111011 13:1111100 17:1111101 25:1111110 41:11111110 73:11111111" },
        { 13, "1:0 2:100 3:1100 4:11100 5:1101 7:101 15:111010 17:111011 21:111100 29:111101 45:111110 77:1111110 141:1111111" },
        { 14, "-2:100 -1:101 0:0 1:110 2:111" },
        { 15, "-24:1111100 -8:111100 -4:11100 -2:1100 -1:100 0:0 1:101 2:1101 3:11101 5:111101 9:1111101 -25:1111110 25:1111111" },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void StandardTablesHaveThePrintedCodes(int number, string printed)
    {
        Dictionary<string, string> expected = printed.Split(' ').Select(p => p.Split(':')).ToDictionary(p => p[1], p => p[0]);
        Dictionary<string, string> actual = Jbig2HuffmanTable.Standard(number).Codes()
            .ToDictionary(c => c.Code, c => c.IsOob ? "OOB" : c.Low.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
    }

    [Fact]
    public void ReadsTheB2ExampleTableSegment()
    {
        // T.88 B.4's example: Table B.1 coded as a table segment.
        byte[] segment = [0x42, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x10, 0x49, 0x23, 0x81, 0x80];
        Jbig2HuffmanTable table = Jbig2HuffmanTable.Read(segment, 0, segment.Length);
        Assert.Equal(Jbig2HuffmanTable.Standard(1).Codes().Select(c => (c.Low, c.Code)), table.Codes().Select(c => (c.Low, c.Code)));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("10 00000000", 16)]
    [InlineData("10 00010000", 32)]
    [InlineData("110 0000000000000001", 273)]
    [InlineData("111 00000000000000000000000000000010", 65810)]
    public void DecodesValuesWithTheirRanges(string bits, long value)
    {
        string compact = bits.Replace(" ", string.Empty, StringComparison.Ordinal);
        byte[] data = new byte[(compact.Length + 7) / 8];
        for (int i = 0; i < compact.Length; i++)
        {
            data[i >> 3] |= (byte)((compact[i] - '0') << (7 - (i & 7)));
        }

        Assert.Equal(value, Jbig2HuffmanTable.Standard(1).Decode(new Jbig2BitReader(data, 0, data.Length)));
    }

    [Fact]
    public void ReadsTheT88SymbolIdTableExample()
    {
        // T.88 7.4.3.1.7 Example 1: 32 symbol ID code lengths, run-length coded.
        byte[] data =
        [
            0x50, 0x03, 0x35, 0x32, 0x53, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x35, 0x0F, 0x8B, 0x30,
            0x9E, 0xB8, 0x5F, 0x1D, 0xD2, 0x83, 0x00,
        ];
        int[] lengths = [0, 0, 0, 9, 6, 6, 6, 6, 3, 4, 4, 4, 4, 4, 4, 0, 7, 9, 8, 7, 5, 5, 5, 5, 5, 5, 3, 6, 7, 4, 7, 7];
        Jbig2BitReader bits = new(data, 0, data.Length);
        Jbig2HuffmanTable table = Jbig2Text.ReadSymbolCodes(bits, 32);
        Assert.Equal(data.Length, bits.BytePosition);
        Dictionary<long, int> byId = table.Codes().Where(c => !c.Lower && c.Low < 32).ToDictionary(c => c.Low, c => c.Code.Length);
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(lengths[i], byId.GetValueOrDefault(i));
        }
    }
}
