using BenchmarkDotNet.Attributes;

using Egami.Fonts.Layout;

namespace Egami.Fonts.Benchmarks;

/// <summary>Justified paragraph layout: Latin and Arabic at word spaces, and Arabic with kashida (2.8.3).</summary>
[MemoryDiagnoser]
public class LayoutBenchmarks
{
    private const float Width = 300;

    private Paragraph _latin = null!;
    private Paragraph _arabic = null!;
    private Paragraph _kashida = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = Root();
        string latin = string.Join(' ', File.ReadAllLines(Path.Combine(root, "tests", "reference", "shaping", "texts", "english.txt")).Skip(3).Take(4));
        string arabic = string.Join(' ', File.ReadAllLines(Path.Combine(root, "tests", "reference", "shaping", "texts", "arabic.txt")).Skip(3).Take(4));
        _latin = Paragraph.Create(latin, Justified(Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"), TextJustify.InterWord));
        string noto = Path.Combine(root, "tests", "fonts", "shaping", "NotoSansArabic-Regular.ttf");
        _arabic = Paragraph.Create(arabic, Justified(noto, TextJustify.InterWord));
        _kashida = Paragraph.Create(arabic, Justified(noto, TextJustify.Kashida));
    }

    [Benchmark]
    public int JustifyLatinAtSpaces() => _latin.Layout(Width).Lines.Count;

    [Benchmark]
    public int JustifyArabicAtSpaces() => _arabic.Layout(Width).Lines.Count;

    [Benchmark]
    public int JustifyArabicWithKashida() => _kashida.Layout(Width).Lines.Count;

    private static ParagraphStyle Justified(string font, TextJustify justify)
    {
        FontCollection fonts = new();
        fonts.AddFile(font);
        return new ParagraphStyle
        {
            DefaultStyle = new TextStyle { FontFamilies = [fonts.Families[0]], Size = 16 },
            Align = TextAlign.Justify,
            Justify = justify,
        };
    }

    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
