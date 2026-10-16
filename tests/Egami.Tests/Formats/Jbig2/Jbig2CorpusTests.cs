using System.IO.Compression;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jbig2;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jbig2;

/// <summary>
/// A JBIG2 corpus with PDFium's and jbig2dec's pages, as tools/make-jbig2-references.py writes it: the committed one made with
/// jbig2enc, or the local one of third-party streams (the T.88 Annex H datastream, PDFBox's test streams) under .cache.
/// </summary>
internal sealed class Jbig2Corpus
{
    internal static readonly Jbig2Corpus Main = new(Path.Combine(AppContext.BaseDirectory, "images", "jbig2"), Path.Combine(AppContext.BaseDirectory, "reference", "jbig2"));

    internal static readonly Jbig2Corpus? Local = FindLocal();

    private readonly Lazy<Dictionary<string, JsonElement>> _entries;

    private Jbig2Corpus(string images, string references)
    {
        Images = images;
        References = references;
        _entries = new(() =>
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
            return json.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        });
    }

    internal string Images { get; }

    internal string References { get; }

    private static Jbig2Corpus? FindLocal()
    {
        if (!LocalCorpus.Enabled("jbig2"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "jbig2-local");
            if (File.Exists(Path.Combine(candidate, "ref", "manifest.json")))
            {
                return new Jbig2Corpus(candidate, Path.Combine(candidate, "ref"));
            }
        }

        return null;
    }

    internal IEnumerable<string> Files() => _entries.Value.Where(e => e.Value.TryGetProperty("pages", out JsonElement pages) && pages.GetArrayLength() > 0).Select(e => e.Key).Order(StringComparer.Ordinal);

    internal JsonElement Entry(string name) => _entries.Value[name];

    internal byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Images, name));

    /// <summary>Reads a reference page: a gzipped binary PBM, 1 black.</summary>
    internal (int Width, int Height, byte[] Bits) Page(string reference)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, reference)), CompressionMode.Decompress);
        using MemoryStream memory = new();
        gzip.CopyTo(memory);
        byte[] pbm = memory.ToArray();
        int at = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)pbm[at]))
            {
                at++;
            }

            int start = at;
            while (!char.IsWhiteSpace((char)pbm[at]))
            {
                at++;
            }

            return System.Text.Encoding.ASCII.GetString(pbm, start, at - start);
        }

        Assert.Equal("P4", Token());
        int width = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture);
        int height = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture);
        at++;
        return (width, height, pbm[at..]);
    }

    /// <summary>Loads every page of a corpus file the way its manifest entry says: standalone, or embedded with globals.</summary>
    internal RasterList Load(string name, ReaderOptions? options = null)
    {
        JsonElement entry = Entry(name);
        byte[] data = Read(name);
        if (entry.TryGetProperty("globals", out JsonElement globals))
        {
            Jbig2Globals parsed = Jbig2Globals.Parse(Read(globals.GetString()!));
            return Raster.LoadAll(data, Jbig2Format.Info, options: new Jbig2ReaderOptions { Globals = parsed, Recovery = options?.Recovery ?? RecoveryMode.Lenient });
        }

        return Raster.LoadAll(data, Jbig2Format.Info, options: options);
    }

    /// <summary>
    /// The pages where PDFium and jbig2dec part ways and T.88 decides for jbig2dec. T.88 H.1 says the Annex H datastream's first
    /// two pages are identical, one coded with Huffman and MMR, the other arithmetically; PDFium's Huffman page differs from both.
    /// </summary>
    private static readonly HashSet<(string, int)> Jbig2decDecides = [("annex-h.jb2", 0)];

    /// <summary>Asserts that every page loads exactly as PDFium decoded it, or jbig2dec where T.88 sides with it.</summary>
    internal void AssertPagesMatch(string name)
    {
        JsonElement[] pages = [.. Entry(name).GetProperty("pages").EnumerateArray()];
        using RasterList loaded = Load(name, new ReaderOptions { Recovery = RecoveryMode.Strict });
        Assert.Equal(pages.Length, loaded.Count);
        for (int i = 0; i < pages.Length; i++)
        {
            string reference = Jbig2decDecides.Contains((name, i)) ? "jbig2dec_reference" : "reference";
            (int width, int height, byte[] bits) = Page(pages[i].GetProperty(reference).GetString()!);
            Raster<Gray8> page = Assert.IsType<Raster<Gray8>>(loaded[i]);
            Assert.Equal((width, height), (page.Width, page.Height));
            Assert.Empty(page.Metadata.Warnings);
            int rowBytes = (width + 7) / 8;
            long differ = 0;
            for (int y = 0; y < height; y++)
            {
                Span<Gray8> row = page.Row(y);
                for (int x = 0; x < width; x++)
                {
                    bool black = ((bits[(y * rowBytes) + (x >> 3)] >> (7 - (x & 7))) & 1) != 0;
                    if (black != (row[x].L == 0) || row[x].L is not (0 or 255))
                    {
                        differ++;
                    }
                }
            }

            Assert.True(differ == 0, $"{name} page {i}: {differ} pixels differ from the {reference}.");
        }
    }
}

public class Jbig2CorpusTests
{
    public static TheoryData<string> Files() => [.. Jbig2Corpus.Main.Files()];

    public static TheoryData<string> LocalFiles() => Jbig2Corpus.Local is null ? ["(none)"] : [.. Jbig2Corpus.Local.Files()];

    [Theory]
    [MemberData(nameof(Files))]
    public void PagesMatchPdfium(string name) => Jbig2Corpus.Main.AssertPagesMatch(name);

    [Theory]
    [MemberData(nameof(LocalFiles))]
    public void ThirdPartyPagesMatchPdfium(string name)
    {
        if (Jbig2Corpus.Local is null)
        {
            throw LocalCorpus.Skip("jbig2");
        }

        Jbig2Corpus.Local.AssertPagesMatch(name);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeMatchesTheLoadedPages(string name)
    {
        JsonElement entry = Jbig2Corpus.Main.Entry(name);
        if (entry.TryGetProperty("globals", out _) || !name.EndsWith(".jb2", StringComparison.Ordinal))
        {
            return;
        }

        RasterInfo info = Raster.Probe(Jbig2Corpus.Main.Read(name));
        JsonElement first = entry.GetProperty("pages")[0];
        Assert.Equal(Jbig2Format.Info, info.Format);
        Assert.Equal((first.GetProperty("width").GetInt32(), first.GetProperty("height").GetInt32()), (info.Width, info.Height));
        Assert.Equal(entry.GetProperty("pages").GetArrayLength(), info.ImageCount);
        Assert.Equal(Gray8.Layout, info.Layout);
    }
}
