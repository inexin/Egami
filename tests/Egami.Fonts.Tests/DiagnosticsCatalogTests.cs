using System.Text.RegularExpressions;

namespace Egami.Fonts.Tests;

/// <summary>Every Font.* diagnostic id in Egami.Fonts is in docs/articles/diagnostics.md, and every Font.* id there is in the source.</summary>
public partial class DiagnosticsCatalogTests
{
    [Fact]
    public void TheCatalogListsEveryFontId()
    {
        string root = TestFonts.Root;
        var inSource = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Egami.Fonts"), "*.cs", SearchOption.AllDirectories))
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
        foreach (string line in File.ReadLines(Path.Combine(root, "docs", "articles", "diagnostics.md")))
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

    [GeneratedRegex(@"""(Font\.[A-Z][A-Za-z0-9]+)""")]
    private static partial Regex SourceId();

    [GeneratedRegex(@"^\| `(Font\.[A-Za-z0-9]+)` \|")]
    private static partial Regex DocsId();
}
