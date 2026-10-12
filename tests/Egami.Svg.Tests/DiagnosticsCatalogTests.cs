using System.Text.RegularExpressions;

namespace Egami.Svg.Tests;

/// <summary>Every Svg.* diagnostic id in Egami.Svg is in docs/articles/diagnostics.md, and every Svg.* id there is in the source.</summary>
public partial class DiagnosticsCatalogTests
{
    [Fact]
    public void TheCatalogListsEverySvgId()
    {
        var inSource = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(TestSvg.Root, "src", "Egami.Svg"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)))
            {
                foreach (Match match in SourceId().Matches(line))
                {
                    inSource.Add(match.Groups[1].Value);
                }
            }
        }

        var inDocs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(TestSvg.Root, "docs", "articles", "diagnostics.md")))
        {
            Match match = DocsId().Match(line);
            if (match.Success)
            {
                inDocs.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(inSource);
        Assert.Equal(inSource, inDocs);
    }

    [GeneratedRegex(@"""(Svg\.[A-Z][A-Za-z0-9]+)""")]
    private static partial Regex SourceId();

    [GeneratedRegex(@"^\| `(Svg\.[A-Za-z0-9]+)` \|")]
    private static partial Regex DocsId();
}
