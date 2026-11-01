namespace Egami.Nal.Tests;

public class NalBitWriterTests
{
    [Fact]
    public void ReaderReadsWhatTheWriterWrote()
    {
        Random random = new(5);
        List<(int Kind, long Value, int Bits)> codes = [];
        NalBitWriter writer = new(4);
        for (int i = 0; i < 5000; i++)
        {
            int kind = random.Next(3);
            switch (kind)
            {
                case 0:
                    {
                        int bits = random.Next(0, 33);
                        uint value = bits == 0 ? 0 : (uint)random.NextInt64(0, 1L << bits);
                        writer.U(bits, value);
                        codes.Add((0, value, bits));
                        break;
                    }

                case 1:
                    {
                        uint value = random.Next(4) == 0 ? uint.MaxValue - 1 - (uint)random.Next(3) : (uint)random.Next(5000);
                        writer.Ue(value);
                        codes.Add((1, value, 0));
                        break;
                    }

                default:
                    {
                        int value = random.Next(-70000, 70000);
                        writer.Se(value);
                        codes.Add((2, value, 0));
                        break;
                    }
            }
        }

        long count = writer.BitCount;
        writer.TrailingBits();
        NalBitReader reader = new(writer.ToArray());
        foreach ((int kind, long value, int bits) in codes)
        {
            long read = kind switch { 0 => reader.U(bits, "u"), 1 => reader.Ue("ue"), _ => reader.Se("se") };
            Assert.Equal(value, read);
        }

        Assert.Equal(count, reader.BitPosition);
        Assert.False(reader.MoreRbspData());
        reader.TrailingBits();
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0 }, new byte[] { 0, 0, 3, 0, 3 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 2, 0, 0, 4 }, new byte[] { 0, 0, 3, 1, 0, 0, 3, 2, 0, 0, 4 })]
    [InlineData(new byte[] { 1, 0, 0, 3, 0, 0, 0, 0 }, new byte[] { 1, 0, 0, 3, 3, 0, 0, 3, 0, 0, 3 })]
    [InlineData(new byte[] { 5, 6 }, new byte[] { 5, 6 })]
    public void EscapeAddsEmulationPrevention(byte[] rbsp, byte[] expected)
    {
        List<int> inserted = [];
        byte[] escaped = NalFraming.Escape(rbsp, inserted);
        Assert.Equal(expected, escaped);
        Assert.Equal(expected.Length - rbsp.Length, inserted.Count);
        byte[] back = NalFraming.Unescape(escaped);
        Assert.Equal(rbsp, back.Length > rbsp.Length ? back[..rbsp.Length] : back);
    }

    [Fact]
    public void EscapedRandomDataUnescapes()
    {
        Random random = new(9);
        byte[] rbsp = new byte[20000];
        for (int i = 0; i < rbsp.Length; i++)
        {
            rbsp[i] = (byte)(random.Next(3) == 0 ? random.Next(4) : random.Next(256));
        }

        rbsp[^1] = 0x80;
        byte[] escaped = NalFraming.Escape(rbsp);
        Assert.Equal(-1, escaped.AsSpan().IndexOf((ReadOnlySpan<byte>)[0, 0, 0]));
        Assert.Equal(-1, escaped.AsSpan().IndexOf((ReadOnlySpan<byte>)[0, 0, 1]));
        Assert.Equal(-1, escaped.AsSpan().IndexOf((ReadOnlySpan<byte>)[0, 0, 2]));
        Assert.Equal(rbsp, NalFraming.Unescape(escaped));
    }
}
