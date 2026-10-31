using System.IO.Compression;

using Egami.Compression.Zstd;

namespace Egami.Tests.Compression;

/// <summary>
/// The managed Zstandard encoder: every level round trips through the decoder on the inputs of the decoder corpus (tests/reference/zstd)
/// and on made-up data; tools/make-zstd-references.py --encoded checks the same outputs with the zstd CLI.
/// </summary>
public class ZstdEncoderTests
{
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "zstd");

    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> data = [];
        foreach (string input in new[] { "text", "egret", "duck", "random", "zeros", "mixed", "empty", "one" })
        {
            for (int level = ZstdEncoder.MinLevel; level <= ZstdEncoder.MaxLevel; level++)
            {
                data.Add(input, level);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrips(string input, int level)
    {
        byte[] data = Input(input);
        byte[] packed = new byte[ZstdEncoder.MaxEncodedLength(data.Length)];
        int size = ZstdEncoder.Encode(data, packed, level);
        byte[] back = new byte[data.Length];
        Assert.Equal(data.Length, ZstdDecoder.Decode(packed.AsSpan(0, size), back));
        int differs = back.AsSpan().CommonPrefixLength(data);
        Assert.True(differs == data.Length, $"The output differs from byte {differs}.");
    }

    [Fact]
    public void WritesOneSingleSegmentFrameWithItsSizeAndNoChecksum()
    {
        byte[] data = Input("text");
        byte[] packed = new byte[ZstdEncoder.MaxEncodedLength(data.Length)];
        int size = ZstdEncoder.Encode(data, packed, 3);
        Assert.Equal(0xFD2FB528u, BitConverter.ToUInt32(packed, 0));

        // Single segment, a two-byte content size (stored less 256), no checksum, no dictionary.
        Assert.Equal(0x60, packed[4]);
        Assert.Equal(data.Length - 256, BitConverter.ToUInt16(packed, 5));
        Assert.True(size < data.Length / 2);
    }

    [Fact]
    public void IncompressibleDataStaysWithinTheBound()
    {
        Random random = new(5);
        foreach (int length in new[] { 0, 1, 15, 16, 17, 1000, (128 * 1024) - 1, 128 * 1024, (128 * 1024) + 1, 300_000 })
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            byte[] packed = new byte[ZstdEncoder.MaxEncodedLength(length)];
            foreach (int level in new[] { 1, 3, 9, 19 })
            {
                int size = ZstdEncoder.Encode(data, packed, level);
                Assert.InRange(size, 1, packed.Length);
                byte[] back = new byte[length];
                Assert.Equal(length, ZstdDecoder.Decode(packed.AsSpan(0, size), back));
                Assert.Equal(data, back);
            }
        }
    }

    [Fact]
    public void RefusesBadLevelsAndShortOutputs()
    {
        byte[] data = new byte[100];
        byte[] packed = new byte[ZstdEncoder.MaxEncodedLength(100)];
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdEncoder.Encode(data, packed, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdEncoder.Encode(data, packed, 23));
        Assert.Throws<ArgumentException>(() => ZstdEncoder.Encode(data, packed.AsSpan(1), 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(15)]
    public void AWarmEncodeAllocatesNothing(int level)
    {
        byte[] data = Input("mixed");
        byte[] packed = new byte[ZstdEncoder.MaxEncodedLength(data.Length)];
        ZstdEncoder.Encode(data, packed, level);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ZstdEncoder.Encode(data, packed, level);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    internal static byte[] Input(string name)
    {
        using GZipStream stream = new(File.OpenRead(Path.Combine(References, name + ".bin.gz")), CompressionMode.Decompress);
        using MemoryStream raw = new();
        stream.CopyTo(raw);
        return raw.ToArray();
    }
}
