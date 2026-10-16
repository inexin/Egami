using System.IO.Compression;
using System.Text.Json;

namespace Egami.Tests.Formats.Tiff;

/// <summary>
/// The TIFF corpus made by tools/make-tiff-references.py and libtiff's decodes of it, or, with <see cref="Pics"/>, libtiff's sample
/// images and their references in the local .cache folder (never committed).
/// </summary>
internal sealed class TiffCorpus
{
    private readonly Lazy<Dictionary<string, JsonElement>> _entries;

    private TiffCorpus(string images, string references)
    {
        Images = images;
        References = references;
        _entries = new(() =>
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
            return json.RootElement.GetProperty("files").EnumerateArray().ToDictionary(e => e.GetProperty("file").GetString()!, e => e.Clone());
        });
    }

    /// <summary>Gets the committed corpus.</summary>
    internal static TiffCorpus Main { get; } = new(
        Path.Combine(AppContext.BaseDirectory, "images", "tiff"), Path.Combine(AppContext.BaseDirectory, "reference", "tiff"));

    /// <summary>Gets libtiff's sample images, or <see langword="null"/> when `make-tiff-references.py --pics` has not been run.</summary>
    internal static TiffCorpus? Pics { get; } = FindPics();

    internal string Images { get; }

    internal string References { get; }

    /// <summary>Gets every file, as "name.tif".</summary>
    internal IEnumerable<string> Files => _entries.Value.Keys.Order(StringComparer.Ordinal);

    internal JsonElement Entry(string file) => _entries.Value[file];

    /// <summary>Gets libtiff's reading of every directory and SubIFD of a file.</summary>
    internal IEnumerable<JsonElement> Pages(string file) => Entry(file).GetProperty("info").GetProperty("pages").EnumerateArray();

    /// <summary>Gets libtiff's reading of a main-chain directory.</summary>
    internal JsonElement Page(string file, int index) => Pages(file).First(p => p.GetProperty("index").GetInt32() == index && p.GetProperty("sub").GetInt32() < 0);

    internal byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Images, file));

    /// <summary>Returns a reference of a page ("raw", "rgba" or "magick"), or <see langword="null"/> when libtiff or ImageMagick made none.</summary>
    internal byte[]? Reference(string file, int index, string kind, int sub = -1)
    {
        JsonElement page = Pages(file).First(p => p.GetProperty("index").GetInt32() == index && p.GetProperty("sub").GetInt32() == sub);
        if (!page.GetProperty("references").TryGetProperty(kind, out JsonElement name))
        {
            return null;
        }

        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name.GetString()!)), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return raw.ToArray();
    }

    private static TiffCorpus? FindPics()
    {
        if (!LocalCorpus.Enabled("tiff"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string references = Path.Combine(dir.FullName, ".cache", "libtiff-pics-ref");
            if (File.Exists(Path.Combine(references, "manifest.json")))
            {
                return new TiffCorpus(Path.Combine(dir.FullName, ".cache", "libtiff-pics"), references);
            }
        }

        return null;
    }
}

/// <summary>Every reference loads at the size the manifest gives.</summary>
public class TiffCorpusTests
{
    public static TheoryData<string> Files() => [.. TiffCorpus.Main.Files];

    [Theory]
    [MemberData(nameof(Files))]
    public void ReferencesLoadAtTheRightSize(string file)
    {
        TiffCorpus corpus = TiffCorpus.Main;
        Assert.True(File.Exists(Path.Combine(corpus.Images, file)));
        foreach (JsonElement page in corpus.Pages(file))
        {
            int width = page.GetProperty("width").GetInt32();
            int height = page.GetProperty("height").GetInt32();
            int index = page.GetProperty("index").GetInt32();
            int sub = page.GetProperty("sub").GetInt32();
            byte[]? rgba = corpus.Reference(file, index, "rgba", sub);
            Assert.True(rgba is null || rgba.Length == width * height * 4);
            byte[]? raw = corpus.Reference(file, index, "raw", sub);
            Assert.True(raw is null || raw.Length > 0);
            Assert.True(rgba is not null || raw is not null || page.GetProperty("compression").GetInt32() == 50000, $"{file} page {index} has no reference.");
        }
    }
}
