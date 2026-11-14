using BenchmarkDotNet.Attributes;

using Egami.Fonts.Layout;

namespace Egami.Fonts.Benchmarks;

/// <summary>
/// Hyphenation and optimal line breaking gates: a 300-word justified English paragraph filled greedily, greedily with
/// automatic hyphens (at most 1.25x greedy) and optimally (at most 3x greedy); and loading compiled Norwegian patterns
/// (under 1 ms).
/// </summary>
[MemoryDiagnoser]
public class HyphenationBenchmarks
{
    private const float Width = 300;

    private Paragraph _greedy = null!;
    private Paragraph _greedyAuto = null!;
    private Paragraph _optimal = null!;
    private Paragraph _optimalAuto = null!;
    private byte[] _norwegian = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = Root();
        string[] words = string.Join(' ', File.ReadAllLines(Path.Combine(root, "tests", "reference", "shaping", "texts", "english.txt")).Skip(3))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string text = string.Join(' ', Enumerable.Range(0, 300).Select(i => words[i % words.Length]));

        FontCollection fonts = new();
        fonts.AddFile(Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"));
        HyphenationDictionaries dictionaries = new();
        using (FileStream stream = File.OpenRead(Path.Combine(root, "tests", "hyphenation", "hyph-en-us.tex")))
        {
            dictionaries.Add("en", Hyphenator.Load(stream));
        }

        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [fonts.Families[0]], Size = 16, Language = "en" },
            Align = TextAlign.Justify,
            Hyphenation = new HyphenationOptions { Dictionaries = dictionaries },
        };
        _greedy = Paragraph.Create(text, style);
        _greedyAuto = Paragraph.Create(text, style with { Hyphens = Hyphens.Auto });
        _optimal = Paragraph.Create(text, style with { LineFill = LineFill.Optimal });
        _optimalAuto = Paragraph.Create(text, style with { LineFill = LineFill.Optimal, Hyphens = Hyphens.Auto });

        using FileStream no = File.OpenRead(Path.Combine(root, "tests", "hyphenation", "hyph-no.tex"));
        using MemoryStream compiled = new();
        Hyphenator.Load(no).Save(compiled);
        _norwegian = compiled.ToArray();
    }

    [Benchmark(Baseline = true)]
    public int Greedy() => _greedy.Layout(Width).Lines.Count;

    [Benchmark]
    public int GreedyAutoHyphens() => _greedyAuto.Layout(Width).Lines.Count;

    [Benchmark]
    public int Optimal() => _optimal.Layout(Width).Lines.Count;

    [Benchmark]
    public int OptimalAutoHyphens() => _optimalAuto.Layout(Width).Lines.Count;

    [Benchmark]
    public int LoadCompiledNorwegian() => Hyphenator.Load(new MemoryStream(_norwegian), HyphenationFormat.Compiled).MinBefore;

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

/// <summary>The first use of a built-in hyphenation language: its resource read and its compiled form loaded (Hyphenation Build
/// Plan task 7, under 1 ms). The German ones are the largest.</summary>
[MemoryDiagnoser]
public class BuiltInHyphenationBenchmarks
{
    [Params("de-1996", "nb", "en-US")]
    public string Tag { get; set; } = "";

    [Benchmark]
    public int LoadBuiltIn() => HyphenationDictionaries.LoadBuiltIn(Tag).MinBefore;
}

