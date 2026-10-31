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

    internal static byte[] Input(string name)
    {
        using GZipStream stream = new(File.OpenRead(Path.Combine(References, name + ".bin.gz")), CompressionMode.Decompress);
        using MemoryStream raw = new();
        stream.CopyTo(raw);
        return raw.ToArray();
    }
}
