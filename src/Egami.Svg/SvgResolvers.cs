namespace Egami.Svg;

/// <summary>Ready resolvers for <see cref="SvgLoadOptions.Resolver"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="resolver"/>
/// </example>
public static class SvgResolvers
{
    /// <summary>
    /// A resolver that opens files at or below one folder and nothing else: no other folders, no network, no file whose link
    /// points outside the folder.
    /// </summary>
    /// <param name="folder">The folder, usually the SVG file's own.</param>
    /// <returns>The resolver.</returns>
    public static Func<Uri, Stream?> SameDirectory(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return uri =>
        {
            if (!uri.IsAbsoluteUri || !uri.IsFile)
            {
                return null;
            }

            string path = System.IO.Path.GetFullPath(uri.LocalPath);
            if (!path.StartsWith(root, comparison) || !File.Exists(path))
            {
                return null;
            }

            if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is FileSystemInfo target &&
                !System.IO.Path.GetFullPath(target.FullName).StartsWith(root, comparison))
            {
                return null;
            }

            return File.OpenRead(path);
        };
    }
}
