using System.Buffers;

using Egami.Formats.Gif;

namespace Egami.Tests.Formats.Gif;

public class LzwTests
{
    [Fact]
    public void DecodesTheSpecificationsKwKwKCase()
    {
        // Minimum code size 2: clear 4, end 5. "0 0 0 0 0 0" is 0, then 6 (= 0 0, made as it is used), then 7 (0 0 0).
        byte[] data = Pack((4, 3), (0, 3), (6, 3), (7, 3), (5, 4));
        Assert.Equal(LzwStatus.Complete, Decode(2, data, 10, out byte[] output).Status);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0 }, output);
    }

    [Fact]
    public void CodeSizeGrowsWhenTheTableReachesThePowerOfTwo()
    {
        // Minimum code size 2: codes are 3 bits until entry 7 is made, then 4 bits.
        byte[] data = Pack((4, 3), (1, 3), (2, 3), (3, 3), (0, 4), (1, 4), (5, 4));
        Assert.Equal(LzwStatus.Complete, Decode(2, data, 10, out byte[] output).Status);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 1 }, output);
    }

    [Fact]
    public void ClearResetsTheTableAndCodeSize()
    {
        // After the clear only a root may come, so entry 6 (made before the clear) is bad.
        byte[] data = Pack((4, 3), (1, 3), (1, 3), (4, 3), (6, 3));
        Assert.Equal(LzwStatus.BadCode, Decode(2, data, 16, out byte[] output).Status);
        Assert.Equal(new byte[] { 1, 1 }, output);
    }

    [Fact]
    public void DeferredClearKeepsDecodingWithAFullTable()
    {
        // Each code is the entry the decoder is about to make (the KwKwK case), a run of zeros one longer each time, until all
        // 4,096 codes exist; then codes keep coming at 12 bits with no clear.
        List<(int, int)> codes = [(256, 9), (0, 9)];
        int next = 258;
        int size = 9;
        int total = 1;
        while (next < 4096)
        {
            codes.Add((next, size));
            total += next - 256;
            next++;
            if (next == 1 << size && size < 12)
            {
                size++;
            }
        }

        codes.Add((300, 12));
        codes.Add((65, 12));
        codes.Add((257, 12));
        (LzwStatus status, byte[] output) = Decode(8, Pack([.. codes]), 1 << 24, out _);
        Assert.Equal(LzwStatus.Complete, status);
        Assert.Equal(total + (300 - 256) + 1, output.Length);
        Assert.Equal(65, output[^1]);
        Assert.All(output[..^1], b => Assert.Equal(0, b));
    }

    [Fact]
    public void EarlyEndKeepsWhatWasDecoded()
    {
        // 16 bits exactly, so no padding bits make another code.
        byte[] data = Pack((4, 3), (1, 3), (2, 3), (3, 3), (0, 4));
        (LzwStatus status, byte[] output) = Decode(2, data, 10, out _);
        Assert.Equal(LzwStatus.DataEnded, status);
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, output);
    }

    [Fact]
    public void FullOutputStopsInsideAString()
    {
        byte[] data = Pack((4, 3), (1, 3), (6, 3), (7, 3), (5, 3));
        (LzwStatus status, byte[] output) = Decode(2, data, 4, out _);
        Assert.Equal(LzwStatus.OutputFull, status);
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, output);
    }

    [Fact]
    public void CodesThatCannotExistAreBad()
    {
        // Right after a clear only a root may come; later, nothing past the next entry.
        Assert.Equal(LzwStatus.BadCode, Decode(2, Pack((4, 3), (6, 3)), 16, out _).Status);
        Assert.Equal(LzwStatus.BadCode, Decode(2, Pack((4, 3), (0, 3), (7, 3)), 16, out _).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EncodeThenDecodeIsExactForEveryCodeSize(int minCodeSize)
    {
        Random random = new(minCodeSize);
        int colors = 1 << minCodeSize;
        foreach (int length in new[] { 0, 1, 2, 3, 100, 5000, 70_000 })
        {
            // Random data fills the table quickly (many clears); runs and repeats grow long strings.
            byte[] noise = [.. Enumerable.Range(0, length).Select(_ => (byte)random.Next(colors))];
            byte[] runs = [.. Enumerable.Range(0, length).Select(i => (byte)((i / 37 + i % 3) % colors))];
            foreach (byte[] indices in new[] { noise, runs })
            {
                byte[] encoded = Encode(minCodeSize, indices);
                Assert.Equal(minCodeSize, encoded[0]);
                byte[] data = Unblock(encoded.AsSpan(1));
                (LzwStatus status, byte[] output) = Decode(minCodeSize, data, indices.Length + 10, out _);
                Assert.Equal(LzwStatus.Complete, status);
                Assert.Equal(indices, output);
            }
        }
    }

    [Fact]
    public void EncoderWritesFullSubBlocksAndATerminator()
    {
        byte[] indices = new byte[20_000];
        new Random(3).NextBytes(indices);
        byte[] encoded = Encode(8, indices);
        int at = 1;
        while (encoded[at] == 255)
        {
            at += 256;
        }

        at += encoded[at] + 1;
        Assert.Equal(0, encoded[at]);
        Assert.Equal(encoded.Length - 1, at);
    }

    private static (LzwStatus Status, byte[] Output) Decode(int minCodeSize, byte[] data, int capacity, out byte[] output)
    {
        using LzwDecoder decoder = new();
        byte[] buffer = new byte[capacity];
        LzwStatus status = decoder.Decode(minCodeSize, data, buffer, out int written);
        output = buffer[..written];
        return (status, output);
    }

    private static byte[] Encode(int minCodeSize, byte[] indices)
    {
        using LzwEncoder encoder = new();
        ArrayBufferWriter<byte> output = new();
        encoder.Encode(minCodeSize, indices, output);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Unblock(ReadOnlySpan<byte> blocks)
    {
        List<byte> data = [];
        int at = 0;
        while (blocks[at] != 0)
        {
            data.AddRange(blocks.Slice(at + 1, blocks[at]));
            at += blocks[at] + 1;
        }

        return [.. data];
    }

    /// <summary>Packs (code, size) pairs least significant bit first, as GIF does.</summary>
    private static byte[] Pack(params (int Code, int Size)[] codes)
    {
        List<byte> bytes = [];
        ulong bits = 0;
        int count = 0;
        foreach ((int code, int size) in codes)
        {
            bits |= (ulong)code << count;
            count += size;
            while (count >= 8)
            {
                bytes.Add((byte)bits);
                bits >>= 8;
                count -= 8;
            }
        }

        if (count > 0)
        {
            bytes.Add((byte)bits);
        }

        return [.. bytes];
    }
}
