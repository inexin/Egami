using System.Text.RegularExpressions;

namespace Egami.Tests;

/// <summary>Every diagnostic id in the source is in docs/articles/diagnostics.md, and every id there is in the source.</summary>
public partial class DiagnosticsCatalogTests
{
    private const string Sources = "Bmp|Gif|Ico|Jbig2|Jpeg|Jpeg2000|Png|Psd|Pnm|Qoi|Radiance|Exr|Jxl|Tga|Tiff|Webp|Exif|Xmp|Iptc|Icc|Detect|Decode|Limits|Parallel|Streaming|Color|Resize|Operation|Save";

    [Fact]
    public void TheCatalogListsEveryIdOnce()
    {
        string root = RepoRoot();
        SortedSet<string> inSource = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Egami"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in SourceId().Matches(line))
                {
                    inSource.Add(match.Groups[1].Value);
                }
            }
        }

        List<string> inDocs = [];
        foreach (string line in File.ReadLines(Path.Combine(root, "docs", "articles", "diagnostics.md")))
        {
            Match match = DocsId().Match(line);
            // Egami.Fonts' ids are checked by its own tests; Egami.Drawing's are raised outside the core.
            if (match.Success && !match.Groups[1].Value.StartsWith("Font.", StringComparison.Ordinal) && !match.Groups[1].Value.StartsWith("Svg.", StringComparison.Ordinal) && !line.Contains("(Egami.Drawing) |", StringComparison.Ordinal))
            {
                inDocs.Add(match.Groups[1].Value);
            }
        }

        Assert.Equal(inDocs.Count, inDocs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(inSource, new SortedSet<string>(inDocs, StringComparer.Ordinal));
        Assert.Equal(inDocs.Order(StringComparer.OrdinalIgnoreCase), inDocs);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex("\"((?:" + Sources + @")\.[A-Z][A-Za-z0-9]+)""")]
    private static partial Regex SourceId();

    [GeneratedRegex(@"^\| `([A-Za-z][A-Za-z0-9]*\.[A-Za-z0-9]+)` \|")]
    private static partial Regex DocsId();
}
