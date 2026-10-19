using System.Security.Cryptography;
using System.Text.Json;

namespace Egami.Tests.Formats.Heic;

/// <summary>
/// The Phase 2.9.4 HEIC references made by tools/make-heic-references.py: the committed corpus in images/heic with heif-dec's
/// decodes and manifest.json in reference/heic, and, when .cache/heic exists, HM's YUV decodes of every HEVC item and of the
/// first picture of every ITU-T H.265.1 conformance stream and Nokia bitstream. Until the decoder lands these check that the
/// references load and agree with each other; the package, decoder and reader tasks compare Egami against them.
/// </summary>
public class HeicReferenceTests
{
    private const string AlphaUrn = "urn:mpeg:hevc:2015:auxid:1";
    private const string MpegBAlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";

    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "heic");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "heic");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    /// <summary>The files libheif rejects on purpose, with its reason.</summary>
    private static readonly Dictionary<string, string> HeifDecRejects = new()
    {
        ["broken-hidden-primary.heic"] = "Non-existing item ID referenced",
        ["broken-ispe-mismatch.heic"] = "Decoded image does not have the size signaled in the file",
        ["broken-truncated-item.heic"] = "Unexpected end of file",
        ["broken-unknown-essential.heic"] = "Unsupported essential item property",
    };

    /// <summary>Gets the fetched .cache/heic folder, or null.</summary>
    internal static string? Cache()
    {
        if (!LocalCorpus.Enabled("heic"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "heic");
            if (Directory.Exists(Path.Combine(candidate, "reference")))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TheoryData<string> CorpusFiles() => [.. Files()];

    private static IEnumerable<string> Files()
    {
        return Directory.GetFiles(Corpus, "*.heic", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Corpus, f).Replace('\\', '/')).Order(StringComparer.Ordinal);
    }

    [Fact]
    public void ManifestListsEveryCorpusFile()
    {
        Assert.Equal(Files(), Manifest.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void HeifDecReferenceLoadsAtTheDisplayedSize(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        Assert.True(data is [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..], "ftyp first");
        if (!entry.TryGetProperty("reference", out JsonElement reference))
        {
            Assert.True(HeifDecRejects.TryGetValue(file, out string? reason), entry.GetProperty("heif_dec_error").GetString());
            Assert.Contains(reason, entry.GetProperty("heif_dec_error").GetString(), StringComparison.Ordinal);
            return;
        }

        Assert.DoesNotContain(file, HeifDecRejects.Keys);
        JsonElement[] items = [.. entry.GetProperty("items").EnumerateArray()];
        JsonElement primary = Item(items, entry.GetProperty("primary").GetInt32());
        int[] size = [.. Property(primary, "ispe")!.Value.GetProperty("size").EnumerateArray().Select(e => e.GetInt32())];
        (int width, int height) = (size[0], size[1]);

        // heif-dec applies clap, then irot, then imir, as HEIF orders them; mirroring keeps the size.
        if (Property(primary, "clap") is JsonElement clap)
        {
            int[] f = [.. clap.GetProperty("fractions").EnumerateArray().Select(e => e.GetInt32())];
            (width, height) = (f[0] / f[1], f[2] / f[3]);
        }

        if (Property(primary, "irot")?.GetProperty("value").GetInt32() is 1 or 3)
        {
            (width, height) = (height, width);
        }

        // heif-dec writes 16-bit PNGs for anything deeper than 8 bits.
        JsonElement coded = primary.GetProperty("refs").TryGetProperty("dimg", out JsonElement cells) ? Item(items, cells[0].GetInt32()) : primary;
        bool deep = Property(coded, "hvcC") is JsonElement hvcc
            ? hvcc.GetProperty("luma_depth").GetInt32() > 8
            : Property(coded, "pixi")?.GetProperty("depths").EnumerateArray().Any(d => d.GetInt32() > 8) == true;
        HashSet<int> owners = [primary.GetProperty("id").GetInt32()];
        if (primary.GetProperty("refs").TryGetProperty("dimg", out cells))
        {
            owners.UnionWith(cells.EnumerateArray().Select(c => c.GetInt32()));
        }

        bool alpha = file.StartsWith("duck-uncompressed-alpha", StringComparison.Ordinal) || items.Any(i => i.GetProperty("refs").TryGetProperty("auxl", out JsonElement to)
            && owners.Contains(to[0].GetInt32()) && Property(i, "auxC")?.GetProperty("aux_type").GetString() is AlphaUrn or MpegBAlphaUrn);

        string path = Path.Combine(References, Path.GetDirectoryName(file)!, reference.GetString()!);
        byte[] png = File.ReadAllBytes(path);
        Assert.Equal(deep ? 16 : 8, png[24]);
        Assert.Equal(alpha, png[25] is 4 or 6);
        using Raster raster = Raster.Load(path);
        Assert.Equal((width, height), (raster.Width, raster.Height));
    }

    [Fact]
    public void HmAndDec265AgreeOnEveryHevcItem()
    {
        int count = 0;
        foreach (JsonProperty file in Manifest.Value.EnumerateObject())
        {
            foreach (JsonElement item in file.Value.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "hvc1"))
            {
                string name = $"{file.Name} item {item.GetProperty("id")}";
                if (file.Name == "broken-truncated-item.heic")
                {
                    Assert.True(item.TryGetProperty("hm_error", out _), name);
                    continue;
                }

                Assert.True(item.TryGetProperty("hm_md5", out _), name);
                Assert.True(item.GetProperty("slices").EnumerateArray().All(s => s.GetString()!.EndsWith(" I", StringComparison.Ordinal)), name);
                count++;

                // dec265 1.0.15 decodes slices with slice_loop_filter_across_slices_enabled_flag 0 differently from HM, the
                // reference, at the slice edges.
                Assert.True(item.GetProperty("dec265_equal").GetBoolean() || file.Name == "hm-slices.heic", name);
            }
        }

        Assert.InRange(count, 250, 600);
    }

    [Fact]
    public void HmStreamsPassTheirDecodedPictureHash()
    {
        foreach (JsonProperty file in Manifest.Value.EnumerateObject().Where(f => f.Name.StartsWith("hm-", StringComparison.Ordinal)))
        {
            string[] hashes = [.. file.Value.GetProperty("items")[0].GetProperty("hm_hash").EnumerateArray().Select(h => h.GetString()!)];
            if (file.Name == "hm-no-hash.heic")
            {
                Assert.Empty(hashes);
                continue;
            }

            string kind = file.Name switch { "hm-crc-hash.heic" => "CRC", "hm-checksum-hash.heic" => "Checksum", _ => "MD5" };
            Assert.Equal([$"{kind} OK"], hashes);
        }
    }

    [Fact]
    public void ExifToolReadsTheExifAndXmpItems()
    {
        JsonElement tags = Manifest.Value.GetProperty("duck-exif-xmp.heic").GetProperty("exiftool");
        Assert.Equal(6, tags.GetProperty("IFD0:Orientation").GetInt32());
        Assert.Contains(tags.EnumerateObject(), t => t.Name.StartsWith("XMP-", StringComparison.Ordinal));
        string[] types = [.. Manifest.Value.GetProperty("duck-exif-xmp.heic").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()!)];
        Assert.Equal(["hvc1", "Exif", "mime"], types);
    }

    [Fact]
    public void PhonePhotosKeepTheirGridExifAndAuxiliaryItems()
    {
        int gainMaps = 0;
        foreach (JsonProperty file in Manifest.Value.EnumerateObject().Where(f => f.Name.StartsWith("phone/", StringComparison.Ordinal)))
        {
            JsonElement[] items = [.. file.Value.GetProperty("items").EnumerateArray()];
            JsonElement primary = Item(items, file.Value.GetProperty("primary").GetInt32());
            Assert.Equal("grid", primary.GetProperty("type").GetString());
            Assert.Contains(items, i => i.GetProperty("type").GetString() == "Exif");
            Assert.Equal("Apple", file.Value.GetProperty("exiftool").GetProperty("IFD0:Make").GetString());
            gainMaps += items.Count(i => Property(i, "auxC")?.GetProperty("aux_type").GetString() == "urn:com:apple:photo:2020:aux:hdrgainmap");
        }

        Assert.True(gainMaps >= 5, $"{gainMaps} Apple gain maps");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CachedYuvDecodesMatchTheirMd5(string file)
    {
        if (Cache() is not string cache)
        {
            return;
        }

        foreach (JsonElement item in Manifest.Value.GetProperty(file).GetProperty("items").EnumerateArray().Where(i => i.TryGetProperty("hm_md5", out _)))
        {
            string path = Path.Combine(cache, "reference", "corpus", Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}.{item.GetProperty("id")}.yuv");
            Assert.True(File.Exists(path), path);
            Assert.Equal(item.GetProperty("hm_md5").GetString(), Md5(File.ReadAllBytes(path)));
        }
    }

    public static TheoryData<string> StreamSets() => ["HEVC_v1", "RExt", "nokia"];

    [Theory]
    [MemberData(nameof(StreamSets))]
    public void HmMatchesTheConformanceSuites(string set)
    {
        if (Cache() is not string cache)
        {
            throw LocalCorpus.Skip("heic");
        }

        string summary = Path.Combine(cache, "reference", set, "summary.json");
        Assert.True(File.Exists(summary), summary);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(summary));
        int intra = 0;
        foreach (JsonProperty stream in json.RootElement.GetProperty("streams").EnumerateObject())
        {
            // HM 18.0 stops on the 16-bit and High Throughput 4:4:4 streams (profile 5), profiles 2.9.4 leaves out (design
            // question 4), and on Nokia's layered ones.
            if (!stream.Value.TryGetProperty("hm_md5", out _))
            {
                Assert.True(set == "nokia" || stream.Value.GetProperty("sps") is var sps
                    && (sps.GetProperty("luma_depth").GetInt32() == 16 || sps.GetProperty("profile_idc").GetInt32() == 5), stream.Name);
                continue;
            }

            Assert.Equal(0, stream.Value.GetProperty("hm_hash_errors").GetInt32());

            // The extended precision streams' MD5s are of another output depth; they are 16-bit intra profiles, outside 2.9.4.
            if (stream.Value.TryGetProperty("hm_matches_suite", out JsonElement matches) && !stream.Name.StartsWith("EXTPREC_", StringComparison.Ordinal))
            {
                Assert.True(matches.GetBoolean(), stream.Name);
            }

            if (stream.Value.GetProperty("all_intra").GetBoolean())
            {
                intra++;
                string first = Path.Combine(cache, "reference", set, stream.Name + ".first.yuv");
                Assert.Equal(stream.Value.GetProperty("first_md5").GetString(), Md5(File.ReadAllBytes(first)));
            }
        }

        Assert.True(intra > 0, summary);
    }

    private static JsonElement? Property(JsonElement item, string type)
    {
        foreach (JsonElement property in item.GetProperty("properties").EnumerateArray())
        {
            if (property.GetProperty("type").GetString() == type)
            {
                return property;
            }
        }

        return null;
    }

    private static JsonElement Item(JsonElement[] items, int id) => items.First(i => i.GetProperty("id").GetInt32() == id);

#pragma warning disable CA5351 // the references' plane hashes are MD5, as HM's; nothing here is security
    private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));
#pragma warning restore CA5351
}
