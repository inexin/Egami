using System.Security.Cryptography;
using System.Text.Json;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// The Phase 2.9.2 AVIF references made by tools/make-avif-references.py: the committed corpus in images/avif with avifdec's
/// decodes and manifest.json in reference/avif, and, when .cache/avif exists, the YUV decodes of the corpus and the first frames
/// of libaom's test vectors, dav1d's test data and the Argon streams. Until the reader lands these check that the references
/// load and agree with each other; the container, decoder and reader tasks compare Egami against them.
/// </summary>
public class AvifReferenceTests
{
    private const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";

    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "avif");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    /// <summary>libavif's test files that avifdec 1.4.2 rejects on purpose (malformed or unsupported), with its reason.</summary>
    private static readonly Dictionary<string, string> AvifdecRejects = new()
    {
        ["libavif/alpha_noispe.avif"] = "BMFF parsing failed",
        ["libavif/clap_irot_imir_non_essential.avif"] = "BMFF parsing failed",
        ["libavif/color_grid_alpha_grid_tile_shared_in_dimg.avif"] = "Not implemented",
        ["libavif/seine_sdr_gainmap_gammazero.avif"] = "Invalid tone mapped image item",
        ["libavif/sofa_grid1x5_420_dimg_repeat.avif"] = "Invalid image grid",
        ["libavif/supported_gainmap_writer_version_with_extra_bytes.avif"] = "Invalid tone mapped image item",
    };

    /// <summary>Gets the fetched .cache/avif folder, or null.</summary>
    internal static string? Cache()
    {
        if (!LocalCorpus.Enabled("avif"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "avif");
            if (Directory.Exists(Path.Combine(candidate, "reference")))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TheoryData<string> CorpusFiles() => [.. Files()];

    /// <summary>The corpus files avifdec decoded; the overlay files, which libavif cannot read, have their own references.</summary>
    private static IEnumerable<string> Files()
    {
        return Directory.GetFiles(Corpus, "*.avif", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Corpus, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("overlay/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    }

    [Fact]
    public void ManifestListsEveryCorpusFile()
    {
        Assert.Equal(Files(), Manifest.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void AvifdecReferenceLoadsAtThePrimaryItemSize(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        Assert.True(data is [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..], "ftyp first");
        if (!entry.TryGetProperty("reference", out JsonElement reference))
        {
            Assert.True(AvifdecRejects.TryGetValue(file, out string? reason), entry.GetProperty("avifdec_error").GetString());
            Assert.Contains(reason, entry.GetProperty("avifdec_error").GetString(), StringComparison.Ordinal);
            return;
        }

        Assert.DoesNotContain(file, AvifdecRejects.Keys);
        JsonElement[] items = [.. entry.GetProperty("items").EnumerateArray()];
        JsonElement primary = items.Single(i => i.GetProperty("primary").GetBoolean());
        int[] size = [.. Property(primary, "ispe")!.Value.GetProperty("size").EnumerateArray().Select(e => e.GetInt32())];
        (int width, int height) = (size[0], size[1]);

        // avifdec applies clap, then irot, then imir, as HEIF orders them; mirroring keeps the size.
        if (Property(primary, "clap") is JsonElement clap)
        {
            int[] f = [.. clap.GetProperty("fractions").EnumerateArray().Select(e => e.GetInt32())];
            (width, height) = (f[0] / f[1], f[2] / f[3]);
        }

        if (Property(primary, "irot")?.GetProperty("value").GetInt32() is 1 or 3)
        {
            (width, height) = (height, width);
        }

        JsonElement coded = primary.GetProperty("type").GetString() == "grid" ? Item(items, primary.GetProperty("refs").GetProperty("dimg")[0].GetInt32()) : primary;
        bool deep = Property(coded, "av1C")!.Value.GetProperty("high_bitdepth").GetInt32() == 1;
        // An alpha item belongs to the primary item or, in libavif's grid files, to each of its cells.
        HashSet<int> owners = [primary.GetProperty("id").GetInt32()];
        if (primary.GetProperty("refs").TryGetProperty("dimg", out JsonElement cells))
        {
            owners.UnionWith(cells.EnumerateArray().Select(c => c.GetInt32()));
        }

        bool alpha = items.Any(i => i.GetProperty("refs").TryGetProperty("auxl", out JsonElement to) && owners.Contains(to[0].GetInt32())
            && i.GetProperty("properties").EnumerateArray().Any(p => p.TryGetProperty("aux_type", out JsonElement t) && t.GetString() == AlphaUrn));

        string path = Path.Combine(References, Path.GetDirectoryName(file)!, reference.GetString()!);
        byte[] png = File.ReadAllBytes(path);
        Assert.Equal(deep ? 16 : 8, png[24]);
        Assert.Equal(alpha, png[25] is 4 or 6);
        using Raster raster = Raster.Load(path);
        Assert.Equal((width, height), (raster.Width, raster.Height));
    }

    [Fact]
    public void Dav1dAndLibaomAgreeOnEveryAv1Item()
    {
        int count = 0;
        foreach (JsonProperty file in Manifest.Value.EnumerateObject())
        {
            foreach (JsonElement item in file.Value.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "av01"))
            {
                string name = $"{file.Name} item {item.GetProperty("id")}";
                Assert.True(item.TryGetProperty("dav1d_md5", out _), name);
                count++;
                if (file.Name == "libavif/poc_b_506387278.avif")
                {
                    // A fuzzer find that dav1d reads and aomdec rejects.
                    Assert.True(item.TryGetProperty("aomdec_error", out _), name);
                }
                else if (item.TryGetProperty("aomdec_equal", out JsonElement agrees))
                {
                    // In a layered item the two command-line decoders output different layers last; libavif over either
                    // gives the same image (avifdec_aom_equal below).
                    Assert.True(agrees.GetBoolean() || file.Name is "duck-layered-3.avif" or "duck-progressive.avif", name);
                }
                else
                {
                    // Only a layered monochrome (alpha) item goes unchecked: aomdec's raw output has chroma planes.
                    Assert.Equal("mono", item.GetProperty("y4m_colorspace").GetString()![..4]);
                }
            }

            if (file.Value.TryGetProperty("avifdec_aom_equal", out JsonElement equal))
            {
                Assert.True(equal.GetBoolean(), file.Name);
            }
        }

        Assert.InRange(count, 250, 400);
    }

    [Fact]
    public void FilmGrainChangesOnlyTheGrainFiles()
    {
        foreach (JsonProperty file in Manifest.Value.EnumerateObject())
        {
            foreach (JsonElement item in file.Value.GetProperty("items").EnumerateArray().Where(i => i.TryGetProperty("dav1d_md5", out _)))
            {
                bool grain = item.GetProperty("dav1d_md5").GetString() != item.GetProperty("dav1d_no_grain_md5").GetString();
                Assert.True(grain == file.Name.Contains("-grain", StringComparison.Ordinal), file.Name);
            }
        }
    }

    [Fact]
    public void ExifToolReadsTheExifAndXmpItems()
    {
        JsonElement tags = Manifest.Value.GetProperty("duck-exif-xmp.avif").GetProperty("exiftool");
        Assert.Equal(6, tags.GetProperty("IFD0:Orientation").GetInt32());
        Assert.Contains(tags.EnumerateObject(), t => t.Name.StartsWith("XMP-", StringComparison.Ordinal));
        string[] types = [.. Manifest.Value.GetProperty("duck-exif-xmp.avif").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()!)];
        Assert.Equal(["av01", "Exif", "mime"], types);
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CachedYuvDecodeMatchesItsMd5(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        if (Cache() is not string cache || !entry.TryGetProperty("yuv_md5", out JsonElement expected))
        {
            return;
        }

        string path = Path.Combine(cache, "reference", "corpus", file[..^5] + ".y4m");
        Assert.True(File.Exists(path), path);
        Assert.Equal(expected.GetString(), Y4mFirstFrameMd5(path));
    }

    public static TheoryData<string> StreamSets() => ["aom", "dav1d", "argon"];

    [Theory]
    [MemberData(nameof(StreamSets))]
    public void StreamFirstFramesAgree(string set)
    {
        if (Cache() is not string cache)
        {
            throw LocalCorpus.Skip("avif");
        }

        string summary = set == "argon" ? Path.Combine(cache, "argon", "reference", "summary.json") : Path.Combine(cache, "reference", set, "summary.json");
        if (!File.Exists(summary))
        {
            return;
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(summary));
        int decoded = 0;
        foreach (JsonProperty stream in json.RootElement.GetProperty("streams").EnumerateObject())
        {
            if (!stream.Value.TryGetProperty("dav1d_md5", out _))
            {
                Assert.True(set == "argon" && IsArgonErrorOrLargeScaleTile(stream.Name), stream.Name);
                continue;
            }

            decoded++;
            // Argon's own MD5s are the judge for its streams: aomdec's first frame differs from dav1d's on 73 of them, where
            // dav1d's whole-stream decode matches Argon's.
            if (set != "argon" && stream.Value.TryGetProperty("aomdec_equal", out JsonElement equal))
            {
                // In the two-spatial-layer SVC streams aomdec's first frame is the base layer, dav1d's the full one.
                Assert.True(equal.GetBoolean() || stream.Name.Contains("-svc-L2T", StringComparison.Ordinal), stream.Name);
            }

            if (stream.Value.TryGetProperty("dav1d_matches_argon", out JsonElement argon))
            {
                Assert.True(argon.GetBoolean() || IsArgonErrorOrLargeScaleTile(stream.Name), stream.Name);
            }
        }

        Assert.True(decoded > 0, summary);
    }

    private static bool IsArgonErrorOrLargeScaleTile(string name) => name.Contains("_error_", StringComparison.Ordinal) || name.Contains("large_scale_tile", StringComparison.Ordinal);

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

    private static string Y4mFirstFrameMd5(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int header = Array.IndexOf(data, (byte)'\n') + 1;
        string[] fields = System.Text.Encoding.ASCII.GetString(data, 0, header - 1).Split(' ');
        int width = int.Parse(fields.First(f => f[0] == 'W')[1..], System.Globalization.CultureInfo.InvariantCulture);
        int height = int.Parse(fields.First(f => f[0] == 'H')[1..], System.Globalization.CultureInfo.InvariantCulture);
        string colorspace = fields.FirstOrDefault(f => f[0] == 'C')?[1..] ?? "420jpeg";
        int sample = colorspace[^2..] is "10" or "12" or "16" ? 2 : 1;
        int chroma = colorspace switch
        {
            _ when colorspace.StartsWith("mono", StringComparison.Ordinal) => 0,
            _ when colorspace.StartsWith("444", StringComparison.Ordinal) => width * height,
            _ when colorspace.StartsWith("422", StringComparison.Ordinal) => (width + 1) / 2 * height,
            _ => (width + 1) / 2 * ((height + 1) / 2),
        };
        int start = Array.IndexOf(data, (byte)'\n', header) + 1;
#pragma warning disable CA5351 // the references' plane hashes are MD5, as dav1d's; nothing here is security
        return Convert.ToHexStringLower(MD5.HashData(data.AsSpan(start, ((width * height) + (2 * chroma)) * sample)));
#pragma warning restore CA5351
    }
}
