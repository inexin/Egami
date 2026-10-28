namespace Egami.SystemDrawing.Tests;

/// <summary>Where the test files are: the repository's tests/images.</summary>
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

    /// <summary>Returns a file under tests/images.</summary>
    public static string Image(string folder, string name) => System.IO.Path.Combine(s_root.Value, "tests", "images", folder, name);
}
