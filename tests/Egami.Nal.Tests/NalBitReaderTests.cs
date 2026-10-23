namespace Egami.Nal.Tests;

public class NalBitReaderTests
{
    [Fact]
    public void ReadsFixedAndExpGolombCodes()
    {
        uint[] unsigned = [0, 1, 2, 3, 7, 254, 255, 65535, 1u << 20, uint.MaxValue - 1];
        int[] signed = [0, 1, -1, 2, -2, 1000, -1000, int.MaxValue, -int.MaxValue];
        BitWriter w = new();
        w.U(3, 5).U(32, 0xDEADBEEF).Flag(true).U(0, 0);
        foreach (uint value in unsigned)
        {
            w.Ue(value);
        }

        foreach (int value in signed)
        {
            w.Se(value);
        }

        NalBitReader r = new(w.TrailingBits().ToArray());
        Assert.Equal(5u, r.U(3, "a"));
        Assert.Equal(0xDEADBEEF, r.U(32, "b"));
        Assert.True(r.Flag("c"));
        Assert.Equal(0u, r.U(0, "d"));
        foreach (uint value in unsigned)
        {
            Assert.Equal(value, r.Ue("ue"));
        }

        foreach (int value in signed)
        {
            Assert.Equal(value, r.Se("se"));
        }

        Assert.False(r.MoreRbspData());
        r.TrailingBits();
        Assert.Equal(0, r.BitsLeft);
    }

    [Fact]
    public void TracesEveryElementByName()
    {
        List<(string Name, long Value)> trace = [];
        NalBitReader r = new(new BitWriter().U(4, 9).Ue(6).Se(-3).TrailingBits().ToArray()) { Trace = trace };
        r.U(4, "four_bits");
        r.Ue("an_ue");
        r.Se("an_se");
        r.TrailingBits();
        List<(string Name, long Value)> expected = [("four_bits", 9), ("an_ue", 6), ("an_se", -3), ("rbsp_stop_one_bit", 1)];
        Assert.Equal(expected, trace.Take(4));
        Assert.All(trace.Skip(4), e => Assert.Equal(("rbsp_alignment_zero_bit", 0L), e));
    }

    [Fact]
    public void MoreRbspDataStopsAtTheStopBit()
    {
        // Two flags, then the stop bit, then a whole zero byte (cabac_zero_words style padding).
        NalBitReader r = new([0b1110_0000, 0]);
        Assert.True(r.MoreRbspData());
        r.Flag("a");
        Assert.True(r.MoreRbspData());
        r.Flag("b");
        Assert.False(r.MoreRbspData());
        Assert.False(new NalBitReader([0, 0]).MoreRbspData());
    }

    [Fact]
    public void ChecksRangesAndTheEnd()
    {
        byte[] data = new BitWriter().Ue(9).Se(-9).TrailingBits().ToArray();
        CorruptDataException tooBig = Assert.Throws<CorruptDataException>(() => new NalBitReader(data).Ue("x", 8));
        Assert.Contains("x is 9, more than 8", tooBig.Message, StringComparison.Ordinal);
        NalBitReader r = new(data);
        r.Ue("x");
        Assert.Throws<CorruptDataException>(() => r.Se("y", -8, 8));

        NalBitReader end = new([0xFF]);
        end.U(6, "a");
        CorruptDataException cut = Assert.Throws<CorruptDataException>(() => end.U(3, "b"));
        Assert.Equal("NAL", cut.FormatName);
        Assert.Equal("Corrupt NAL data: NAL: A NAL unit is cut off.", cut.Message);
        Assert.Throws<CorruptDataException>(() => end.Skip(3));

        // 32 leading zeros is longer than any code ue(v) may have.
        Assert.Throws<CorruptDataException>(() => new NalBitReader([0, 0, 0, 0, 0x80]).Ue("z"));
        Assert.Throws<CorruptDataException>(() => new NalBitReader([0x00]).TrailingBits());
    }

    [Fact]
    public void StartsAtAByteAndTracksPosition()
    {
        NalBitReader r = new([0xAA, 0x80, 0xC0], start: 1);
        Assert.Equal(8, r.BitPosition);
        Assert.Equal(16, r.BitsLeft);
        Assert.True(r.ByteAligned);
        Assert.True(r.Flag("a"));
        Assert.False(r.ByteAligned);
        Assert.Equal(1, r.BytePosition);
        r.Skip(7);
        Assert.Equal(2, r.BytePosition);
        r.ByteAlignment();
        Assert.True(r.ByteAligned);
    }
}
