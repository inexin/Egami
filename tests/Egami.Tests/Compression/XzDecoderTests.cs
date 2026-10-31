using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Egami.Compression;
using Egami.Compression.Lzma;

namespace Egami.Tests.Compression;

/// <summary>
/// The managed xz and LZMA2 decoder against the corpus tools/make-xz-references.py makes with the xz CLI (tests/images/xz, and
/// with EGAMI_CORPUS=xz the larger files in .cache/xz), byte for byte.
/// </summary>
public class XzDecoderTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "xz");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "xz");

    public static TheoryData<string> CorpusFiles() => [.. Manifest(References).Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void DecodesTheCorpus(string name)
    {
        JsonElement entry = Manifest(References)[name];
        byte[] expected = [.. entry.GetProperty("inputs").EnumerateArray().SelectMany(i => Input(References, i.GetString()!, gzip: true))];
        CheckFile(File.ReadAllBytes(Path.Combine(Images, name + ".xz")), expected, entry);
    }

    [Fact]
    public void DecodesTheLocalCorpus()
    {
        string cache = LocalCache() ?? throw LocalCorpus.Skip("xz");
        Dictionary<string, byte[]> inputs = [];
        foreach ((string name, JsonElement entry) in Manifest(cache).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            string input = entry.GetProperty("inputs")[0].GetString()!;
            if (!inputs.TryGetValue(input, out byte[]? expected))
            {
                inputs[input] = expected = Input(Path.Combine(cache, "inputs"), input, gzip: false);
            }

            CheckFile(File.ReadAllBytes(Path.Combine(cache, name + ".xz")), expected, entry);
        }
    }

    [Fact]
    public void ChecksEveryCheckType()
    {
        foreach (string name in new[] { "text-crc32", "text-6", "text-sha256" })
        {
            byte[] file = File.ReadAllBytes(Path.Combine(Images, name + ".xz"));

            // The check sits just before the index, which is followed by the 12-byte footer; the index of one block is 12 bytes here.
            byte[] damaged = (byte[])file.Clone();
            damaged[^25] ^= 1;
            Assert.Throws<CorruptDataException>(() => XzDecoder.Decode(damaged, new byte[64 * 1024]));
        }
    }

    [Fact]
    public void RefusesOtherFilters()
    {
        // A block header naming the x86 branch converter (filter 4) before LZMA2.
        byte[] stream = File.ReadAllBytes(Path.Combine(Images, "one-6.xz"));
        int blockAt = 12;
        Span<byte> header = stream.AsSpan(blockAt, (stream[blockAt] + 1) * 4);
        header[1] = 1;
        header[2] = 0x04;
        header[3] = 0;
        header[4] = 0x21;
        header[5] = 1;
        header[6] = 0x10;
        header[7..^4].Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[^4..], Egami.Formats.Crc32.Compute(header[..^4]));
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => XzDecoder.Decode(stream, new byte[16]));
        Assert.Contains("filter 0x4", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCutIsCorrupt()
    {
        foreach (string name in new[] { "text-6", "egret-delta3", "zeros-6", "random-6" })
        {
            byte[] file = File.ReadAllBytes(Path.Combine(Images, name + ".xz"));
            byte[] output = new byte[400 * 1024];
            for (int length = 0; length < file.Length; length++)
            {
                Assert.Throws<CorruptDataException>(() => XzDecoder.Decode(file.AsSpan(0, length), output));
            }
        }
    }

    [Fact]
    public void DamageOnlyEverGivesEgamiExceptions()
    {
        byte[] output = new byte[64 * 1024];
        foreach (string name in new[] { "text-9e", "egret-6", "mixed-lc0lp2pb0", "text-stdin" })
        {
            byte[] file = File.ReadAllBytes(Path.Combine(Images, name + ".xz"));
            for (int bit = 0; bit < Math.Min(file.Length * 8, 40_000); bit += 5)
            {
                file[bit >> 3] ^= (byte)(1 << (bit & 7));
                try
                {
                    int written = XzDecoder.Decode(file, output);
                    Assert.InRange(written, 0, output.Length);
                }
                catch (EgamiException)
                {
                }

                file[bit >> 3] ^= (byte)(1 << (bit & 7));
            }
        }
    }

    [Fact]
    public void AWarmDecodeAllocatesNothing()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "mixed-delta1.xz"));
        byte[] output = new byte[Manifest(References)["mixed-delta1"].GetProperty("length").GetInt32()];
        XzDecoder.Decode(file, output);
        long before = GC.GetAllocatedBytesForCurrentThread();
        XzDecoder.Decode(file, output);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData("", 0x0000000000000000UL)]
    [InlineData("123456789", 0x995DC9BBDF1939FAUL)]
    public void Crc64MatchesKnownValues(string text, ulong expected)
    {
        Assert.Equal(expected, Crc64.Compute(Encoding.ASCII.GetBytes(text)));
    }

    private static void CheckFile(byte[] file, byte[] expected, JsonElement entry)
    {
        Assert.Equal(entry.GetProperty("length").GetInt32(), expected.Length);
        Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(expected)));
        byte[] output = new byte[expected.Length];
        Assert.Equal(expected.Length, XzDecoder.Decode(file, output));
        int differs = output.AsSpan().CommonPrefixLength(expected);
        Assert.True(differs == expected.Length, $"The output differs from byte {differs}.");
        byte[] larger = new byte[expected.Length + 100];
        Assert.Equal(expected.Length, XzDecoder.Decode(file, larger));
        if (expected.Length > 0)
        {
            Assert.Throws<CorruptDataException>(() => XzDecoder.Decode(file, new byte[expected.Length - 1]));
        }
    }

    private static Dictionary<string, JsonElement> Manifest(string folder)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        return json.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static byte[] Input(string folder, string name, bool gzip)
    {
        if (!gzip)
        {
            return File.ReadAllBytes(Path.Combine(folder, name + ".bin"));
        }

        using GZipStream stream = new(File.OpenRead(Path.Combine(folder, name + ".bin.gz")), CompressionMode.Decompress);
        using MemoryStream raw = new();
        stream.CopyTo(raw);
        return raw.ToArray();
    }

    /// <summary>Gets .cache/xz from tools/make-xz-references.py --local, or null.</summary>
    private static string? LocalCache()
    {
        if (!LocalCorpus.Enabled("xz"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "xz");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        return null;
    }
}
