using BenchmarkDotNet.Attributes;

using Egami.Fonts.Layout;

namespace Egami.Fonts.Benchmarks;

/// <summary>Line break opportunities (UAX #14) over whole UDHR texts: Latin and Chinese, and Thai and Khmer, which break between
/// the words of the built-in dictionaries (2.8.5) or, with <see cref="WordDictionaries.None"/>, only at spaces.</summary>
[MemoryDiagnoser]
public class LineBreakBenchmarks
{
    private static readonly LineBreakOptions NoDictionaries = new() { Dictionaries = WordDictionaries.None };

    private string _latin = null!;
    private string _chinese = null!;
    private string _thai = null!;
    private string _khmer = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = LayoutBenchmarks.Root();
        _latin = File.ReadAllText(Path.Combine(root, "tests", "reference", "shaping", "texts", "english.txt"));
        _chinese = File.ReadAllText(Path.Combine(root, "tests", "reference", "layout", "texts", "chinese.txt"));
        _thai = File.ReadAllText(Path.Combine(root, "tests", "reference", "shaping", "texts", "thai.txt"));
        _khmer = File.ReadAllText(Path.Combine(root, "tests", "reference", "shaping", "texts", "khmer.txt"));

        // Load the dictionaries once, outside the measurements.
        Count(_thai, default);
        Count(_khmer, default);
    }

    [Benchmark]
    public int Latin() => Count(_latin, default);

    [Benchmark]
    public int Chinese() => Count(_chinese, default);

    [Benchmark]
    public int Thai() => Count(_thai, default);

    [Benchmark]
    public int ThaiNoDictionary() => Count(_thai, NoDictionaries);

    [Benchmark]
    public int Khmer() => Count(_khmer, default);

    [Benchmark]
    public int KhmerNoDictionary() => Count(_khmer, NoDictionaries);

    private static int Count(string text, LineBreakOptions options)
    {
        int count = 0;
        foreach (LineBreak b in LineBreaks.Enumerate(text, options))
        {
            count += b.Index & 1;
        }

        return count;
    }
}
