using System.IO.Compression;
using System.Text.Json;

namespace Egami.Fonts.Tests;

/// <summary>The test fonts under tests/fonts and fontTools' references under tests/reference/fonts.</summary>
internal static class TestFonts
{
    private static readonly Lazy<string> s_root = new(() =>
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    });

    public static string Root => s_root.Value;

    /// <summary>Every font with a reference: (file, face index, reference name).</summary>
    public static readonly (string File, int Face, string Reference)[] Referenced =
    [
        ("DejaVuSans.ttf", 0, "DejaVuSans"),
        ("DejaVuSansMono.ttf", 0, "DejaVuSansMono"),
        ("NotoSans-Bold.ttf", 0, "NotoSans-Bold"),
        ("NotoSans-Italic.ttf", 0, "NotoSans-Italic"),
        ("NotoSans-Regular.ttf", 0, "NotoSans-Regular"),
        ("NotoSerif-Regular.ttf", 0, "NotoSerif-Regular"),
        ("Roboto-Regular.ttf", 0, "Roboto-Regular"),
        ("SourceSans3-Regular.otf", 0, "SourceSans3-Regular"),
        ("SourceSerif4-Regular.otf", 0, "SourceSerif4-Regular"),
        ("derived/NotoSans-format12.ttf", 0, "NotoSans-format12"),
        ("derived/NotoSans-kern.ttf", 0, "NotoSans-kern"),
        ("derived/NotoSans-mac.ttf", 0, "NotoSans-mac"),
        ("derived/NotoSans-symbol.ttf", 0, "NotoSans-symbol"),
        ("derived/NotoSansCJK-subset.otf", 0, "NotoSansCJK-subset"),
        ("derived/NotoSansSerif.ttc", 0, "NotoSansSerif.0"),
        ("derived/NotoSansSerif.ttc", 1, "NotoSansSerif.1"),
    ];

    public static TheoryData<string, int, string> ReferencedData()
    {
        var data = new TheoryData<string, int, string>();
        foreach ((string file, int face, string reference) in Referenced)
        {
            data.Add(file, face, reference);
        }

        return data;
    }

    public static string FontPath(string file) => Path.Combine(Root, "tests", "fonts", file);

    public static byte[] Bytes(string file) => File.ReadAllBytes(FontPath(file));

    public static Typeface Load(string file, int face = 0, FontLoadOptions? options = null) => Typeface.FromFile(FontPath(file), face, options);

    public static JsonElement Reference(string name)
    {
        string path = Path.Combine(Root, "tests", "reference", "fonts", name + ".json.gz");
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using JsonDocument doc = JsonDocument.Parse(gzip);
        return doc.RootElement.Clone();
    }
}

/// <summary>Records an outline as fontTools' normalized form: M, L, Q, C and Z entries.</summary>
internal sealed class TextSink : IGlyphOutlineSink
{
    public List<double[]> Ops { get; } = [];

    public List<char> Verbs { get; } = [];

    public void MoveTo(float x, float y) => Add('M', x, y);

    public void LineTo(float x, float y) => Add('L', x, y);

    public void QuadTo(float cx, float cy, float x, float y) => Add('Q', cx, cy, x, y);

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => Add('C', c1x, c1y, c2x, c2y, x, y);

    public void Close() => Add('Z');

    private void Add(char verb, params float[] values)
    {
        Verbs.Add(verb);
        Ops.Add([.. values.Select(v => (double)v)]);
    }
}
