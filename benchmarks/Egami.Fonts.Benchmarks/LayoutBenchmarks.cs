using BenchmarkDotNet.Attributes;

using Egami.Fonts.Layout;

namespace Egami.Fonts.Benchmarks;

/// <summary>Justified paragraph layout: Latin and Arabic at word spaces, Latin whose spaces may also shrink by a quarter
/// (ParagraphStyle.SpaceShrink), and Arabic with kashida (2.8.3); Latin laid out plain and with every fourth word in
/// another color, which cuts lines into a run per color; Thai and Khmer, which break between dictionary words (2.8.5); and
/// Japanese plain, created and laid out, with ruby over every kanji pair and with emphasis marks, and vertical Japanese with
/// tate-chu-yoko digits (2.8.6).</summary>
[MemoryDiagnoser]
public class LayoutBenchmarks
{
    private const float Width = 300;

    private Paragraph _latin = null!;
    private Paragraph _shrink = null!;
    private Paragraph _arabic = null!;
    private Paragraph _kashida = null!;
    private Paragraph _plain = null!;
    private Paragraph _colored = null!;
    private Paragraph _thai = null!;
    private Paragraph _khmer = null!;
    private Paragraph _japanese = null!;
    private Paragraph _ruby = null!;
    private string _kanji = null!;
    private ParagraphStyle _japaneseStyle = null!;
    private Paragraph _emphasis = null!;
    private Paragraph _combined = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = Root();
        string latin = string.Join(' ', File.ReadAllLines(Path.Combine(root, "tests", "reference", "shaping", "texts", "english.txt")).Skip(3).Take(4));
        string arabic = string.Join(' ', File.ReadAllLines(Path.Combine(root, "tests", "reference", "shaping", "texts", "arabic.txt")).Skip(3).Take(4));
        _latin = Paragraph.Create(latin, Justified(Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"), TextJustify.InterWord));
        _shrink = Paragraph.Create(latin, Justified(Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"), TextJustify.InterWord) with { SpaceShrink = 0.25f });
        string noto = Path.Combine(root, "tests", "fonts", "shaping", "NotoSansArabic-Regular.ttf");
        _arabic = Paragraph.Create(arabic, Justified(noto, TextJustify.InterWord));
        _kashida = Paragraph.Create(arabic, Justified(noto, TextJustify.Kashida));
        ParagraphStyle left = Justified(Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"), TextJustify.InterWord) with { Align = TextAlign.Start };
        _plain = Paragraph.Create(latin, left);
        _colored = Paragraph.Create(Colored(latin), left);
        _thai = Plain(root, "thai", "NotoSansThai-Regular.ttf");
        _khmer = Plain(root, "khmer", "NotoSansKhmer-Regular.ttf");
        FontCollection cjk = new();
        cjk.AddFile(Path.Combine(root, "tests", "fonts", "derived", "NotoSansCJK-layout-subset.otf"));
        ParagraphStyle japanese = new() { DefaultStyle = new TextStyle { FontFamilies = [cjk.Families[0]], Size = 16, Language = "ja" } };
        string kanji = string.Concat(Enumerable.Repeat("諸国間の友好関係の発展を促進することが肝要である", 8));
        _japanese = Paragraph.Create(kanji, japanese);
        List<RubyAnnotation> ruby = [];
        for (int i = 0; i + 1 < kanji.Length; i++)
        {
            if (kanji[i] >= 0x4E00 && kanji[i + 1] >= 0x4E00)
            {
                ruby.Add(new RubyAnnotation(i, 2, "こくさい"));
                i++;
            }
        }

        _ruby = Paragraph.Create(new RichText(kanji, null, null, ruby), japanese);
        _kanji = kanji;
        _japaneseStyle = japanese;
        _emphasis = Paragraph.Create(kanji, japanese with { DefaultStyle = japanese.DefaultStyle with { Emphasis = new TextEmphasis { Mark = EmphasisMark.Custom, Custom = "、" } } });
        string dates = string.Concat(Enumerable.Repeat("諸国間の12年3月の31の友好関係", 12));
        _combined = Paragraph.Create(dates, japanese with { WritingMode = WritingMode.VerticalRl, DefaultStyle = japanese.DefaultStyle with { CombineUpright = TextCombine.Digits(2) } });
    }

    [Benchmark]
    public int JustifyLatinAtSpaces() => _latin.Layout(Width).Lines.Count;

    [Benchmark]
    public int JustifyLatinShrinkingSpaces() => _shrink.Layout(Width).Lines.Count;

    [Benchmark]
    public int JustifyArabicAtSpaces() => _arabic.Layout(Width).Lines.Count;

    [Benchmark]
    public int JustifyArabicWithKashida() => _kashida.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutLatin() => _plain.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutLatinColored() => _colored.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutThai() => _thai.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutKhmer() => _khmer.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutJapanese() => _japanese.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutJapaneseRuby() => _ruby.Layout(Width).Lines.Count;

    [Benchmark]
    public int CreateJapanese() => Paragraph.Create(_kanji, _japaneseStyle).RichText.Text.Length;

    [Benchmark]
    public int LayoutJapaneseEmphasis() => _emphasis.Layout(Width).Lines.Count;

    [Benchmark]
    public int LayoutVerticalTateChuYoko() => _combined.Layout(Width).Lines.Count;

    /// <summary>The repository root.</summary>
    internal static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>Four UDHR lines of a script, left aligned in its Noto font.</summary>
    private static Paragraph Plain(string root, string text, string font)
    {
        string lines = string.Join(' ', File.ReadAllLines(Path.Combine(root, "tests", "reference", "shaping", "texts", text + ".txt")).Skip(3).Take(4));
        ParagraphStyle style = Justified(Path.Combine(root, "tests", "fonts", "shaping", font), TextJustify.InterWord) with { Align = TextAlign.Start };
        return Paragraph.Create(lines, style);
    }

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
}
