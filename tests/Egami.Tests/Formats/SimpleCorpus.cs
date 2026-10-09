using System.IO.Compression;
using System.Text.Json;

namespace Egami.Tests.Formats;

/// <summary>The BMP, TGA, QOI and PNM corpus made by tools/make-simple-references.py and its references.</summary>
internal static class SimpleCorpus
{
    internal static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");
    internal static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "simple");

    private static readonly Lazy<Dictionary<string, JsonElement>> Entries = new(() =>
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    });

    /// <summary>Gets the corpus files in a folder, as "bmp/name.bmp".</summary>
    internal static IEnumerable<string> Files(string folder)
    {
        return Entries.Value.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    }

    internal static JsonElement Entry(string file) => Entries.Value[file];

    internal static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Images, file));

    /// <summary>
    /// Returns the pixels a tool decoded as raw RGBA, or <see langword="null"/> when that tool has no reference; "expected" is the
    /// pixels the script wrote into a hand-made file.
    /// </summary>
    internal static byte[]? Pixels(string file, string tool)
    {
        string path = Path.Combine(References, $"{Path.GetFileName(file)}.{tool}.rgba.gz");
        if (!File.Exists(path))
        {
            return null;
        }

        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return raw.ToArray();
    }
}

/// <summary>Every reference loads, at the file's size.</summary>
public class SimpleCorpusTests
{
    private static readonly string[] Folders = ["bmp", "tga", "qoi", "pnm"];

    public static TheoryData<string> Files() => [.. Folders.SelectMany(SimpleCorpus.Files)];

    [Theory]
    [MemberData(nameof(Files))]
    public void ReferencesLoadAtTheRightSize(string file)
    {
        JsonElement entry = SimpleCorpus.Entry(file);
        int bytes = entry.GetProperty("width").GetInt32() * entry.GetProperty("height").GetInt32() * 4;
        Assert.True(File.Exists(Path.Combine(SimpleCorpus.Images, file)));
        JsonElement references = entry.GetProperty("references");
        Assert.NotEqual(0, references.GetArrayLength());
        foreach (JsonElement reference in references.EnumerateArray())
        {
            Assert.Equal(bytes, SimpleCorpus.Pixels(file, reference.GetString()!)!.Length);
        }
    }
}
