namespace Egami.Nal.Tests;

public class NalFramingTests
{
    [Fact]
    public void RemovesEmulationPreventionBytes()
    {
        List<int> removed = [];
        byte[] rbsp = NalFraming.Unescape([0x65, 0, 0, 3, 1, 0, 0, 3, 0, 3, 0, 0, 3], removed);
        Assert.Equal([0x65, 0, 0, 1, 0, 0, 0, 3, 0, 0], rbsp);
        Assert.Equal([3, 7, 12], removed);

        // 00 03 is not a pattern, and a lone unit without one comes back as it is.
        Assert.Equal([0, 3, 0, 1], NalFraming.Unescape([0, 3, 0, 1]));
    }

    [Fact]
    public void SplitsAnnexBStreams()
    {
        byte[] stream = [9, 9, 0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 0, 1, 0x65, 4, 5, 0, 0, 1, 0, 0, 0, 1, 0x06, 0, 0];
        List<ReadOnlyMemory<byte>> units = NalFraming.SplitAnnexB(stream);
        Assert.Equal(4, units.Count);
        Assert.Equal([0x67, 1, 2], units[0].ToArray());
        Assert.Equal([0x68, 3], units[1].ToArray());
        Assert.Equal([0x65, 4, 5], units[2].ToArray());
        Assert.Equal([0x06], units[3].ToArray());
        Assert.Empty(NalFraming.SplitAnnexB(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SplitsLengthPrefixedData(int lengthSize)
    {
        byte[][] units = [[0x65, 1, 2, 3], [], [0x41, 7]];
        List<byte> data = [];
        foreach (byte[] unit in units)
        {
            for (int i = lengthSize - 1; i >= 0; i--)
            {
                data.Add((byte)(unit.Length >> (8 * i)));
            }

            data.AddRange(unit);
        }

        List<ReadOnlyMemory<byte>> split = NalFraming.SplitLengthPrefixed(data.ToArray(), lengthSize);
        Assert.Equal(2, split.Count);
        Assert.Equal(units[0], split[0].ToArray());
        Assert.Equal(units[2], split[1].ToArray());

        Assert.Throws<CorruptDataException>(() => NalFraming.SplitLengthPrefixed(data.ToArray().AsMemory(0, data.Count - 1), lengthSize));
    }

    [Fact]
    public void RefusesACutSizeField()
    {
        Assert.Throws<CorruptDataException>(() => NalFraming.SplitLengthPrefixed(new byte[] { 0, 0, 0, 1, 0x65, 0, 0 }, 4));
    }

    [Fact]
    public void FindsStartCodes()
    {
        byte[] data = [5, 0, 0, 1, 7, 0, 0, 0, 1, 8];
        Assert.Equal(1, NalFraming.IndexOfStartCode(data));
        Assert.Equal(6, NalFraming.IndexOfStartCode(data, 2));
        Assert.Equal(-1, NalFraming.IndexOfStartCode(data, 7));
        Assert.True(NalFraming.StartsWithStartCode([0, 0, 1, 0x67]));
        Assert.True(NalFraming.StartsWithStartCode([0, 0, 0, 1, 0x67]));
        Assert.False(NalFraming.StartsWithStartCode([0, 0, 0, 0x1C, 0x67]));
        Assert.False(NalFraming.StartsWithStartCode([0, 0]));
    }
}
