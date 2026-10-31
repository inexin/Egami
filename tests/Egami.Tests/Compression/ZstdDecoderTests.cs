using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Egami.Compression;
using Egami.Compression.Zstd;

namespace Egami.Tests.Compression;

/// <summary>
/// The managed Zstandard decoder against the corpus tools/make-zstd-references.py makes with the zstd CLI (tests/images/zstd, and
/// with EGAMI_CORPUS=zstd the larger files in .cache/zstd), byte for byte, and against frames written by hand.
/// </summary>
public class ZstdDecoderTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "zstd");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "zstd");

    private static readonly byte[] Magic = [0x28, 0xB5, 0x2F, 0xFD];

    public static TheoryData<string> CorpusFiles() => [.. Manifest(References).Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void DecodesTheCorpus(string name)
    {
        JsonElement entry = Manifest(References)[name];
        byte[] expected = [.. entry.GetProperty("inputs").EnumerateArray().SelectMany(i => Input(References, i.GetString()!, gzip: true))];
        CheckFile(File.ReadAllBytes(Path.Combine(Images, name + ".zst")), expected, entry);
    }

    [Fact]
    public void DecodesTheLocalCorpus()
    {
        string cache = LocalCache() ?? throw LocalCorpus.Skip("zstd");
        Dictionary<string, byte[]> inputs = [];
        foreach ((string name, JsonElement entry) in Manifest(cache).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            string input = entry.GetProperty("inputs")[0].GetString()!;
            if (!inputs.TryGetValue(input, out byte[]? expected))
            {
                inputs[input] = expected = Input(Path.Combine(cache, "inputs"), input, gzip: false);
            }

            CheckFile(File.ReadAllBytes(Path.Combine(cache, name + ".zst")), expected, entry);
        }
    }

    [Fact]
    public void ReadsRawAndRleBlocksWrittenByHand()
    {
        // Single-segment frames with a one-byte content size: a raw block of "hello", then an RLE block of ten x's.
        byte[] raw = [.. Magic, 0x20, 5, (5 << 3) | 1, 0, 0, .. "hello"u8];
        byte[] rle = [.. Magic, 0x20, 10, (10 << 3) | 2 | 1, 0, 0, (byte)'x'];
        byte[] output = new byte[32];
        Assert.Equal(5, ZstdDecoder.Decode(raw, output));
        Assert.Equal("hello"u8.ToArray(), output[..5]);
        Assert.Equal(15, ZstdDecoder.Decode([.. raw, .. rle], output));
        Assert.Equal("helloxxxxxxxxxx"u8.ToArray(), output[..15]);
    }

    [Fact]
    public void EmptyInputAndSkippableFramesDecodeToNothing()
    {
        Assert.Equal(0, ZstdDecoder.Decode([], new byte[4]));
        byte[] skippable = [0x5F, 0x2A, 0x4D, 0x18, 3, 0, 0, 0, 1, 2, 3];
        Assert.Equal(0, ZstdDecoder.Decode(skippable, []));
        Assert.Throws<CorruptDataException>(() => ZstdDecoder.Decode(skippable.AsSpan(0, 10), []));
    }

    [Fact]
    public void RefusesFramesThatNeedADictionary()
    {
        byte[] frame = [.. Magic, 0x21, 7, 5, (5 << 3) | 1, 0, 0, .. "hello"u8];
        Assert.Throws<UnsupportedFormatException>(() => ZstdDecoder.Decode(frame, new byte[8]));

        // A dictionary id of 0 is the same as none.
        frame[5] = 0;
        Assert.Equal(5, ZstdDecoder.Decode(frame, new byte[8]));
    }

    [Theory]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x28, 5, (5 << 3) | 1, 0, 0, 1, 2, 3, 4, 5 })]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 5, (5 << 3) | 6 | 1, 0, 0, 1, 2, 3, 4, 5 })]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 4, (5 << 3) | 1, 0, 0, 1, 2, 3, 4, 5 })]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 6, (5 << 3) | 1, 0, 0, 1, 2, 3, 4, 5 })]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 5, (5 << 3) | 1, 0, 0, 1, 2, 3, 4, 5, 0 })]
    [InlineData(new byte[] { 0x29, 0xB5, 0x2F, 0xFD, 0x20, 5, (5 << 3) | 1, 0, 0, 1, 2, 3, 4, 5 })]
    public void RejectsBrokenFrames(byte[] frame)
    {
        // The reserved header bit, the reserved block type, a block bigger and one smaller than the content size, a stray byte
        // after the frame, and a wrong magic number.
        Assert.Throws<CorruptDataException>(() => ZstdDecoder.Decode(frame, new byte[16]));
    }

    [Fact]
    public void FramesThatClaimMoreThanTheOutputAreCorrupt()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "text-19.zst"));
        int length = Manifest(References)["text-19"].GetProperty("length").GetInt32();
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => ZstdDecoder.Decode(file, new byte[length - 1]));
        Assert.Contains("room", e.Message, StringComparison.Ordinal);

        // Without a content size in the header, it fails where the output runs out.
        file = File.ReadAllBytes(Path.Combine(Images, "mixed-stdin.zst"));
        length = Manifest(References)["mixed-stdin"].GetProperty("length").GetInt32();
        Assert.Throws<CorruptDataException>(() => ZstdDecoder.Decode(file, new byte[length / 2]));
    }

    [Fact]
    public void ChecksTheContentChecksum()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "egret-19.zst"));
        file[^2] ^= 1;
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => ZstdDecoder.Decode(file, new byte[64 * 1024]));
        Assert.Contains("checksum", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCutIsCorrupt()
    {
        foreach (string name in new[] { "text-19", "duck-3", "zeros-3", "text-rawliterals" })
        {
            byte[] file = File.ReadAllBytes(Path.Combine(Images, name + ".zst"));
            byte[] output = new byte[400 * 1024];
            for (int length = 1; length < file.Length; length++)
            {
                Assert.Throws<CorruptDataException>(() => ZstdDecoder.Decode(file.AsSpan(0, length), output));
            }
        }
    }

    [Fact]
    public void DamageOnlyEverGivesEgamiExceptions()
    {
        // Every fifth bit of a few files flipped in turn: each decode must finish or fail with an EgamiException.
        byte[] output = new byte[64 * 1024];
        foreach (string name in new[] { "text-22", "egret-1", "mixed-16", "text-stdin" })
        {
            byte[] file = File.ReadAllBytes(Path.Combine(Images, name + ".zst"));
            for (int bit = 0; bit < Math.Min(file.Length * 8, 40_000); bit += 5)
            {
                file[bit >> 3] ^= (byte)(1 << (bit & 7));
                try
                {
                    int written = ZstdDecoder.Decode(file, output);
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
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "mixed-3.zst"));
        byte[] output = new byte[Manifest(References)["mixed-3"].GetProperty("length").GetInt32()];
        ZstdDecoder.Decode(file, output);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ZstdDecoder.Decode(file, output);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData("", 0xEF46DB3751D8E999)]
    [InlineData("a", 0xD24EC4F1A98C6E5B)]
    [InlineData("abc", 0x44BC2CF5AD770999)]
    public void XxHash64MatchesKnownValues(string text, ulong expected)
    {
        Assert.Equal(expected, XxHash64.Hash(Encoding.ASCII.GetBytes(text)));
    }

    private static void CheckFile(byte[] file, byte[] expected, JsonElement entry)
    {
        Assert.Equal(entry.GetProperty("length").GetInt32(), expected.Length);
        Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(expected)));

        // Into exactly the room it needs, into more, and into one byte too few.
        byte[] output = new byte[expected.Length];
        Assert.Equal(expected.Length, ZstdDecoder.Decode(file, output));
        int differs = output.AsSpan().CommonPrefixLength(expected);
        Assert.True(differs == expected.Length, $"The output differs from byte {differs}.");

        byte[] larger = new byte[expected.Length + 100];
        Assert.Equal(expected.Length, ZstdDecoder.Decode(file, larger));
        Assert.True(larger.AsSpan(0, expected.Length).SequenceEqual(expected));
        if (expected.Length > 0)
        {
            Assert.Throws<CorruptDataException>(() => ZstdDecoder.Decode(file, new byte[expected.Length - 1]));
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

    /// <summary>Gets .cache/zstd from tools/make-zstd-references.py --local, or null.</summary>
    private static string? LocalCache()
    {
        if (!LocalCorpus.Enabled("zstd"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "zstd");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        return null;
    }
}
