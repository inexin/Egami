using System.Text.RegularExpressions;

namespace Egami.Metafiles.Tests;

/// <summary>Every Metafile.* diagnostic id in Egami.Metafiles is in docs/articles/diagnostics.md, and every Metafile.* id there is in the source.</summary>
public partial class DiagnosticsCatalogTests
{
    [Fact]
    public void TheCatalogListsEveryMetafileId()
    {
        SortedSet<string> inSource = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(System.IO.Path.Combine(TestFiles.Root, "src", "Egami.Metafiles"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)))
            {
                foreach (Match match in SourceId().Matches(line))
                {
                    inSource.Add(match.Groups[1].Value);
                }
            }
        }

        SortedSet<string> inDocs = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(System.IO.Path.Combine(TestFiles.Root, "docs", "articles", "diagnostics.md")))
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

    [GeneratedRegex(@"""(Metafile\.[A-Z][A-Za-z0-9]+)""")]
    private static partial Regex SourceId();

    [GeneratedRegex(@"^\| `(Metafile\.[A-Za-z0-9]+)` \|")]
    private static partial Regex DocsId();
}
