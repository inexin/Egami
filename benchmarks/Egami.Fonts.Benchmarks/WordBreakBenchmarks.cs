using BenchmarkDotNet.Attributes;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Benchmarks;

/// <summary>Word boundaries over whole UDHR texts: UAX #29 alone, and with the built-in word dictionaries (2.8.5), which split
/// Thai and Khmer runs into words.</summary>
[MemoryDiagnoser]
public class WordBreakBenchmarks
{
    private string _latin = null!;
    private string _thai = null!;
    private string _khmer = null!;

    [GlobalSetup(Targets = [nameof(Latin), nameof(LatinWithDictionaries), nameof(Thai)])]
    public void Setup()
    {
        string root = LayoutBenchmarks.Root();
        _latin = File.ReadAllText(Path.Combine(root, "tests", "reference", "shaping", "texts", "english.txt"));
        _thai = File.ReadAllText(Path.Combine(root, "tests", "reference", "shaping", "texts", "thai.txt"));
        _khmer = File.ReadAllText(Path.Combine(root, "tests", "reference", "shaping", "texts", "khmer.txt"));
    }

    /// <summary>Loads the dictionaries once, outside the measurements; only for the cases that use them, so that the
    /// profile-guided JIT does not tune the Latin cases on Thai text.</summary>
    [GlobalSetup(Targets = [nameof(ThaiWithDictionaries), nameof(KhmerWithDictionaries)])]
    public void SetupDictionaries()
    {
        Setup();
        CountWithDictionaries(_thai);
        CountWithDictionaries(_khmer);
    }

    [Benchmark]
    public int Latin() => Count(_latin);

    [Benchmark]
    public int LatinWithDictionaries() => CountWithDictionaries(_latin);

    [Benchmark]
    public int Thai() => Count(_thai);

    [Benchmark]
    public int ThaiWithDictionaries() => CountWithDictionaries(_thai);

    [Benchmark]
    public int KhmerWithDictionaries() => CountWithDictionaries(_khmer);

    private static int Count(string text)
    {
        int count = 0;
        foreach (TextSegment w in TextSegmenter.Words(text))
        {
            count += w.Length & 1;
        }

        return count;
    }

    private static int CountWithDictionaries(string text)
    {
        int count = 0;
        foreach (TextSegment w in TextSegmenter.Words(text, null))
        {
            count += w.Length & 1;
        }

        return count;
    }
}
