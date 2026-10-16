namespace Egami.Metafiles.Tests;

/// <summary>Where the test files are: the repository's tests/images/metafiles.</summary>
internal static class TestFiles
{
    private static readonly Lazy<string> s_root = new(() =>
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    });

    /// <summary>Gets the repository root.</summary>
    public static string Root => s_root.Value;

    /// <summary>Gets tests/images/metafiles/synthetic.</summary>
    public static string SyntheticRoot => System.IO.Path.Combine(Root, "tests", "images", "metafiles", "synthetic");

    /// <summary>Gets the synthetic file names, sorted.</summary>
    public static IEnumerable<string> Synthetic() =>
        Directory.EnumerateFiles(SyntheticRoot).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal)!;

    /// <summary>Gets a synthetic file's path.</summary>
    public static string SyntheticPath(string name) => System.IO.Path.Combine(SyntheticRoot, name);
}
