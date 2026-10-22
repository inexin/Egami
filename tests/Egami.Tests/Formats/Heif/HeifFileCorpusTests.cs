using System.Text.Json;

using Egami.Formats.Heif;

namespace Egami.Tests.Formats.Heif;

/// <summary>
/// Phase 2.9.2 task 2's gate: the HEIF container reader's item lists equal libavif 1.4.2's (its read.c, dumped by
/// tools/avifref/avifitems.c into reference/avif/manifest.json and, for the files that stay in .cache, the extra manifest) on every
/// file libavif parses: brands, primary item, every item's type, extents and properties in association order, the item references
/// libavif keeps, entity groups, and which items cite an essential property libavif does not support.
/// </summary>
public class HeifFileCorpusTests
{
    /// <summary>The properties libavif 1.4.2 understands; an essential property of any other type makes an item unusable.</summary>
    private static readonly HashSet<uint> LibavifProperties =
        [.. new[] { "ispe", "auxC", "colr", "av1C", "pasp", "clap", "irot", "imir", "pixi", "a1op", "lsel", "a1lx", "clli" }.Select(t => IsoTypes.Of(t))];

    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> CorpusFiles() => [.. Manifest.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void ItemsEqualLibavifs(string file)
    {
        JsonElement libavif = Manifest.Value.GetProperty(file).GetProperty("libavif");
        Compare(File.ReadAllBytes(Path.Combine(Corpus, file)), libavif, file);
    }

    public static TheoryData<string> ExtraFiles()
    {
        string? cache = Avif.AvifReferenceTests.Cache();
        return cache is null || !File.Exists(Path.Combine(cache, "reference", "extra", "manifest.json"))
            ? ["(none)"]
            : [.. JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", "extra", "manifest.json"))).RootElement.GetProperty("files").EnumerateObject().Select(p => p.Name)];
    }

    [Theory]
    [MemberData(nameof(ExtraFiles))]
    public void CachedItemsEqualLibavifs(string file)
    {
        if (Avif.AvifReferenceTests.Cache() is not string cache || file == "(none)")
        {
            throw LocalCorpus.Skip("avif");
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", "extra", "manifest.json")));
        JsonElement libavif = manifest.RootElement.GetProperty("files").GetProperty(file).GetProperty("libavif");
        Compare(File.ReadAllBytes(Path.Combine(cache, file)), libavif, file);
    }

    private static void Compare(byte[] bytes, JsonElement libavif, string file)
    {
        if (libavif.GetProperty("result").GetString() != "OK")
        {
            // libavif rejects these on purpose (an AVIF rule, or a case it has not implemented); the container must still read
            // them, or fail as corrupt, never otherwise.
            try
            {
                HeifFile.Parse(bytes, "AVIF");
            }
            catch (CorruptDataException)
            {
            }

            return;
        }

        HeifFile heif = HeifFile.Parse(bytes, "AVIF");
        Assert.Equal(libavif.GetProperty("major_brand").GetString(), IsoTypes.ToString(heif.MajorBrand));
        Assert.Equal(libavif.GetProperty("compatible_brands").EnumerateArray().Select(b => b.GetString()), heif.CompatibleBrands.Select(IsoTypes.ToString));
        Assert.Equal(libavif.GetProperty("primary_item").GetUInt32(), heif.PrimaryItemId);
        Assert.Equal(libavif.GetProperty("idat_size").GetInt32(), heif.Idat.Length);
        heif.MarkUnsupportedEssential(LibavifProperties.Contains);

        // libavif also makes items for ids that only iloc, ipma or iref name, which this reader lists apart. It also adds a grid item of its own, with no extents, for alpha (or a gain map) split over a color grid's cells.
        JsonElement[] all = [.. libavif.GetProperty("items").EnumerateArray()];
        HashSet<uint> synthetic = [.. all.Where(i => heif.Item(i.GetProperty("id").GetUInt32()) is null && i.GetProperty("type").GetString() == "grid"
            && i.GetProperty("extents").GetArrayLength() == 0).Select(i => i.GetProperty("id").GetUInt32())];
        JsonElement[] theirs = [.. all.Where(i => !synthetic.Contains(i.GetProperty("id").GetUInt32()))];
        Assert.Equal(theirs.Select(i => i.GetProperty("id").GetUInt32()).Order(), heif.Items.Select(i => i.Id).Concat(heif.UnlistedItemIds).Distinct().Order());
        foreach (JsonElement expected in theirs)
        {
            uint id = expected.GetProperty("id").GetUInt32();
            if (heif.Item(id) is not HeifItem item)
            {
                continue;
            }

            string at = $"{file} item {id}";
            Assert.True(expected.GetProperty("type").GetString() == IsoTypes.ToString(item.Type), at);
            Assert.True(expected.GetProperty("idat").GetBoolean() == (item.ConstructionMethod == 1), at);
            long[][] extents = [.. expected.GetProperty("extents").EnumerateArray().Select(e => new[] { e[0].GetInt64(), e[1].GetInt64() })];
            Assert.Equal(extents, item.Extents.Select(e => new[] { e.Offset, e.Length }));
            Assert.True(expected.GetProperty("size").GetInt64() == item.Extents.Sum(e => e.Length), at);
            if (item.Extents.Count > 0)
            {
                Assert.True(heif.ItemData(item).Length == item.Extents.Sum(e => e.Length), at);
            }

            List<string> properties = [.. heif.PropertiesOf(item).Select(p => IsoTypes.ToString(p.Property.Type))];
            string[] theirProperties = [.. expected.GetProperty("properties").EnumerateArray().Select(p => p.GetString()!)];
            if (item.Type == IsoTypes.Of("grid") && !properties.Contains("av1C") && theirProperties.Contains("av1C"))
            {
                // libavif gives a color or alpha grid its first cell's av1C.
                properties.Add("av1C");
            }

            Assert.True(theirProperties.SequenceEqual(properties), $"{at}: libavif [{string.Join(' ', theirProperties)}], Egami [{string.Join(' ', properties)}]");
            Assert.True(expected.GetProperty("content_type").GetString() == (item.ContentType ?? ""), at);
            Assert.True(expected.GetProperty("thumbnail_for").GetUInt32() == First(item, "thmb"), at);
            Assert.True(expected.GetProperty("aux_for").GetUInt32() == First(item, "auxl"), at);
            Assert.True(expected.GetProperty("desc_for").GetUInt32() == First(item, "cdsc"), at);
            Assert.True(expected.GetProperty("prem_by").GetUInt32() == First(item, "prem"), at);
            Assert.True(expected.GetProperty("has_dimg_from").GetBoolean() == (item.ReferencesOf(IsoTypes.Of("dimg")).Length > 0), at);
            (uint derived, int index) = DerivedFrom(heif, id);
            if (!synthetic.Contains(expected.GetProperty("dimg_for").GetUInt32()))
            {
                Assert.True(expected.GetProperty("dimg_for").GetUInt32() == derived, at);
                Assert.True(expected.GetProperty("dimg_index").GetInt32() == index, at);
            }

            Assert.True(expected.GetProperty("unsupported_essential").GetBoolean() == (item.UnsupportedEssential != 0), at);
        }

        JsonElement[] groups = [.. libavif.GetProperty("groups").EnumerateArray()];
        Assert.Equal(groups.Length, heif.Groups.Count);
        for (int g = 0; g < groups.Length; g++)
        {
            Assert.Equal(groups[g].GetProperty("type").GetString(), IsoTypes.ToString(heif.Groups[g].Type));
            Assert.Equal(groups[g].GetProperty("id").GetUInt32(), heif.Groups[g].Id);
            Assert.Equal(groups[g].GetProperty("entities").EnumerateArray().Select(e => e.GetUInt32()), heif.Groups[g].Entities);
        }
    }

    private static uint First(HeifItem item, string type) => item.ReferencesOf(IsoTypes.Of(type)) is [uint to, ..] ? to : 0;

    /// <summary>The last item whose dimg references name <paramref name="id"/>, and where, as libavif keeps it.</summary>
    private static (uint Item, int Index) DerivedFrom(HeifFile heif, uint id)
    {
        (uint, int) found = (0, 0);
        foreach (HeifItem item in heif.Items)
        {
            int index = Array.IndexOf(item.ReferencesOf(IsoTypes.Of("dimg")), id);
            if (index >= 0)
            {
                found = (item.Id, index);
            }
        }

        return found;
    }
}
