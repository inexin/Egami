namespace Egami.Nal.Tests;

public class NalSeiTests
{
    [Fact]
    public void WalksEveryMessage()
    {
        // payloadType 300 (255 + 45) with 2 bytes, then type 5 with 260 bytes (255 + 5), of which the reader reads one.
        BitWriter w = new();
        w.U(8, 255).U(8, 45).U(8, 2).U(16, 0xBEEF);
        w.U(8, 5).U(8, 255).U(8, 5).U(8, 0x42);
        for (int i = 1; i < 260; i++)
        {
            w.U(8, i & 0xFF);
        }

        List<(int Type, int Size, long First)> seen = [];
        NalSei.Read(new NalBitReader(w.TrailingBits().ToArray()), seen, static (r, type, size, list) => list.Add((type, size, r.U(8, "first"))));
        List<(int Type, int Size, long First)> expected = [(300, 2, 0xBE), (5, 260, 0x42)];
        Assert.Equal(expected, seen);
    }

    [Fact]
    public void RefusesAMessagePastTheEnd()
    {
        byte[] data = new BitWriter().U(8, 4).U(8, 10).U(8, 1).TrailingBits().ToArray();
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => NalSei.Read(new NalBitReader(data), 0, static (_, _, _, _) => { }));
        Assert.Contains("runs past the end", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAReaderThatReadsPastItsPayload()
    {
        byte[] data = new BitWriter().U(8, 4).U(8, 1).U(8, 1).U(8, 2).TrailingBits().ToArray();
        Assert.Throws<CorruptDataException>(() => NalSei.Read(new NalBitReader(data), 0, static (r, _, _, _) => r.U(16, "too_much")));
    }
}
