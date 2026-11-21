using BenchmarkDotNet.Attributes;

using Egami.Fonts.Layout;

namespace Egami.Fonts.Benchmarks;

/// <summary>Justified paragraph layout: Latin and Arabic at word spaces, and Arabic with kashida (2.8.3); and Latin laid out
/// plain and with every fourth word in another color, which cuts lines into a run per color.</summary>
[MemoryDiagnoser]
public class LayoutBenchmarks
{
    private const float Width = 300;

    private Paragraph _latin = null!;
    private Paragraph _arabic = null!;
    private Paragraph _kashida = null!;
    private Paragraph _plain = null!;
    private Paragraph _colored = null!;

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
        ParagraphStyle left = Justified(Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"), TextJustify.InterWord) with { Align = TextAlign.Start };
        _plain = Paragraph.Create(latin, left);
        _colored = Paragraph.Create(Colored(latin), left);
    }

    [Benchmark]
    public int JustifyLatinAtSpaces() => _latin.Layout(Width).Lines.Count;

    [Benchmark]
    public int JustifyArabicAtSpaces() => _arabic.Layout(Width).Lines.Count;

    [Benchmark]
    public int JustifyArabicWithKashida() => _kashida.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutLatin() => _plain.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutLatinColored() => _colored.Layout(Width).Lines.Count;

    /// <summary>The text with every fourth word in red, as syntax highlighting or a link colors words.</summary>
    private static RichText Colored(string text)
    {
        TextStyle red = new() { Color = new Egami.Pixels.Rgba8(200, 0, 0, 255) };
        List<StyleSpan> spans = [];
        int word = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (i == 0 || text[i - 1] == ' ')
            {
                int end = text.IndexOf(' ', i);
                end = end < 0 ? text.Length : end;
                if (word++ % 4 == 3)
                {
                    spans.Add(new StyleSpan(i, end - i, red));
                }
            }
        }

        return new RichText(text, spans);
    }

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
