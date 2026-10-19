namespace Egami.Barcodes.Tests;

/// <summary>Where the test files are.</summary>
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
}
