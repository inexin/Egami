using System.IO.Compression;
using System.Text.Json;

namespace Egami.Tests.Formats.Gif;

/// <summary>The GIF and APNG corpus made by tools/make-anim-references.py and its composited references.</summary>
internal static class AnimCorpus
{
    internal static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");
    internal static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "anim");

    private static readonly Lazy<Dictionary<string, JsonElement>> Entries = new(() =>
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    });

    /// <summary>Gets the corpus files, as "gif/name.gif" or "apng/name.png".</summary>
    internal static IEnumerable<string> Files(string folder)
    {
        return Entries.Value.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    }

    internal static JsonElement Entry(string file) => Entries.Value[file];

    internal static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Images, file));

    /// <summary>Returns every frame a tool composited, as raw RGBA, or <see langword="null"/> when that tool has no reference.</summary>
    internal static byte[][]? Frames(string file, string tool)
    {
        string path = Path.Combine(References, $"{Path.GetFileName(file)}.{tool}.rgba.gz");
        if (!File.Exists(path))
        {
            return null;
        }

        JsonElement entry = Entry(file);
        int frameBytes = entry.GetProperty("width").GetInt32() * entry.GetProperty("height").GetInt32() * 4;
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        byte[] all = raw.ToArray();
        return [.. Enumerable.Range(0, all.Length / frameBytes).Select(i => all.AsSpan(i * frameBytes, frameBytes).ToArray())];
    }
}

/// <summary>Every reference loads, one frame per frame the manifest lists, at the file's size.</summary>
public class AnimCorpusTests
{
    public static TheoryData<string> Files() => [.. AnimCorpus.Files("gif").Concat(AnimCorpus.Files("apng"))];

    [Theory]
    [MemberData(nameof(Files))]
    public void ReferencesLoadAtTheRightSize(string file)
    {
        JsonElement entry = AnimCorpus.Entry(file);
        int frames = entry.GetProperty("frames").GetInt32();
        int frameBytes = entry.GetProperty("width").GetInt32() * entry.GetProperty("height").GetInt32() * 4;
        Assert.True(File.Exists(Path.Combine(AnimCorpus.Images, file)));
        foreach (JsonElement reference in entry.GetProperty("references").EnumerateArray())
        {
            byte[][] decoded = AnimCorpus.Frames(file, reference.GetProperty("tool").GetString()!)!;
            Assert.Equal(frames, decoded.Length);
            Assert.All(decoded, f => Assert.Equal(frameBytes, f.Length));
        }
    }
}
