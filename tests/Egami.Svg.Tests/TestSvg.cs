namespace Egami.Svg.Tests;

/// <summary>The SVG test files under tests/svg: resvg's suite and the real-file corpus.</summary>
internal static class TestSvg
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

    public static string SuiteRoot => Path.Combine(Root, "tests", "svg", "resvg", "tests");

    public static string CorpusRoot => Path.Combine(Root, "tests", "svg", "corpus");

    /// <summary>Every test of resvg's suite, as chapter/feature/name.</summary>
    public static IReadOnlyList<string> Suite(string? chapter = null) =>
        [.. Directory.EnumerateFiles(chapter is null ? SuiteRoot : Path.Combine(SuiteRoot, chapter), "*.svg", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(SuiteRoot, f)[..^4].Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>Every corpus file name without .svg.</summary>
    public static IReadOnlyList<string> Corpus() =>
        [.. Directory.EnumerateFiles(CorpusRoot, "*.svg").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal)];

    public static string SuiteFile(string test) => Path.Combine(SuiteRoot, test + ".svg");

    public static string CorpusFile(string name) => Path.Combine(CorpusRoot, name + ".svg");

    public static TheoryData<string> CorpusData() => [.. Corpus()];
}
