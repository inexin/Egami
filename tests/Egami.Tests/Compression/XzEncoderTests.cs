using System.IO.Compression;

using Egami.Compression.Lzma;

namespace Egami.Tests.Compression;

/// <summary>
/// The managed xz encoder: every preset, with and without the delta filter, round trips through the decoder on the inputs of the
/// xz corpus (tests/reference/xz); the xz CLI checks the same outputs (tools/make-xz-references.py --check).
/// </summary>
public class XzEncoderTests
{
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "xz");

    public static TheoryData<string, int, int> Cases()
    {
        TheoryData<string, int, int> data = [];
        foreach (string input in new[] { "text", "egret", "duck", "random", "zeros", "mixed", "empty", "one" })
        {
            for (int preset = XzEncoder.MinPreset; preset <= XzEncoder.MaxPreset; preset++)
            {
                data.Add(input, preset, 0);
            }

            data.Add(input, 6, input == "duck" ? 4 : 3);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrips(string input, int preset, int delta)
    {
        byte[] data = Input(input);
        byte[] packed = new byte[XzEncoder.MaxEncodedLength(data.Length)];
        int size = XzEncoder.Encode(data, packed, preset, delta);
        byte[] back = new byte[data.Length];
        Assert.Equal(data.Length, XzDecoder.Decode(packed.AsSpan(0, size), back));
        int differs = back.AsSpan().CommonPrefixLength(data);
        Assert.True(differs == data.Length, $"The output differs from byte {differs}.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void AWarmEncodeAllocatesNothing(int preset)
    {
        byte[] data = Input("mixed");
        byte[] packed = new byte[XzEncoder.MaxEncodedLength(data.Length)];
        XzEncoder.Encode(data, packed, preset, 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        XzEncoder.Encode(data, packed, preset, 2);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void IncompressibleDataGoesOutAsUncompressedChunks()
    {
        Random random = new(9);
        foreach (int length in new[] { 1, 100, 65535, 65536, 65537, 200_000 })
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            byte[] packed = new byte[XzEncoder.MaxEncodedLength(length)];
            int size = XzEncoder.Encode(data, packed, 6);
            Assert.InRange(size, length, packed.Length);
            byte[] back = new byte[length];
            Assert.Equal(length, XzDecoder.Decode(packed.AsSpan(0, size), back));
            Assert.Equal(data, back);
        }
    }

    [Fact]
    public void RefusesBadPresetsAndShortOutputs()
    {
        byte[] data = new byte[100];
        byte[] packed = new byte[XzEncoder.MaxEncodedLength(100)];
        Assert.Throws<ArgumentOutOfRangeException>(() => XzEncoder.Encode(data, packed, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => XzEncoder.Encode(data, packed, 10));
        Assert.Throws<ArgumentException>(() => XzEncoder.Encode(data, packed.AsSpan(1), 6));
    }

    internal static byte[] Input(string name)
    {
        using GZipStream stream = new(File.OpenRead(Path.Combine(References, name + ".bin.gz")), CompressionMode.Decompress);
        using MemoryStream raw = new();
        stream.CopyTo(raw);
        return raw.ToArray();
    }
}
