using System.IO.Compression;
using System.Text.Json;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>
/// hb-shape's references under tests/reference/shaping (tools/make-shaping-references.py): HarfBuzz's own shaping tests and the
/// per-script corpus.
/// </summary>
internal static class ShapingReferences
{
    public static readonly string[] Suites = ["in-house", "aots"];

    public static string Folder => Path.Combine(TestFonts.Root, "tests", "reference", "shaping");

    public static IEnumerable<string> CorpusNames() =>
        Directory.GetFiles(Path.Combine(Folder, "corpus"), "*.json.gz").Select(f => Path.GetFileName(f)[..^".json.gz".Length]).Order(StringComparer.Ordinal);

    public static TheoryData<string> CorpusData()
    {
        var data = new TheoryData<string>();
        foreach (string name in CorpusNames())
        {
            data.Add(name);
        }

        return data;
    }

    public static JsonElement Load(string relative)
    {
        using FileStream file = File.OpenRead(Path.Combine(Folder, relative));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using JsonDocument document = JsonDocument.Parse(gzip);
        return document.RootElement.Clone();
    }

    /// <summary>One hb-shape glyph: id, cluster, flags, advances and offsets, in font units.</summary>
    public readonly record struct Glyph(int Id, int Cluster, int Flags, int XAdvance, int YAdvance, int XOffset, int YOffset);

    public static Glyph[] Glyphs(JsonElement glyphs) =>
    [
        .. glyphs.EnumerateArray().Select(g => new Glyph(
            g.GetProperty("g").GetInt32(),
            g.TryGetProperty("cl", out JsonElement cl) ? cl.GetInt32() : 0,
            g.TryGetProperty("fl", out JsonElement fl) ? fl.GetInt32() : 0,
            g.TryGetProperty("ax", out JsonElement ax) ? ax.GetInt32() : 0,
            g.TryGetProperty("ay", out JsonElement ay) ? ay.GetInt32() : 0,
            g.TryGetProperty("dx", out JsonElement dx) ? dx.GetInt32() : 0,
            g.TryGetProperty("dy", out JsonElement dy) ? dy.GetInt32() : 0)),
    ];
}
