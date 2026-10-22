namespace Egami.Cli.Hosting;

/// <summary>
/// The folders a tool's <c>mcp</c> server may read and write: a path is allowed when it lies inside one of them after
/// every symbolic link and junction on the way has been followed, so a link inside an allowed folder cannot lead out of it.
/// </summary>
internal sealed class PathPolicy
{
    private const int MaxLinks = 40;

    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string[] _roots;
    private readonly string _toolName;

    /// <summary>Allows the folders given, made absolute against <paramref name="workingDirectory"/>, for the tool <paramref name="toolName"/>.</summary>
    public PathPolicy(IEnumerable<string> folders, string workingDirectory, string toolName)
    {
        _toolName = toolName;
        List<string> roots = [];
        foreach (string folder in folders)
        {
            string real = RealPath(Path.GetFullPath(folder, workingDirectory));
            if (!roots.Any(r => string.Equals(r, real, Comparison)))
            {
                roots.Add(real);
            }
        }

        _roots = [.. roots];
    }

    /// <summary>Gets the allowed folders, with links followed.</summary>
    public IReadOnlyList<string> Folders => _roots;

    /// <summary>Whether a full path lies inside an allowed folder.</summary>
    public bool Allows(string fullPath)
    {
        string real = RealPath(fullPath);
        foreach (string root in _roots)
        {
            if (real.Equals(root, Comparison) || real.StartsWith(WithSeparator(root), Comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws <see cref="UsageException"/> when a full path lies outside every allowed folder.</summary>
    public void Check(string fullPath)
    {
        if (!Allows(fullPath))
        {
            throw new UsageException($"'{fullPath}' is outside the folders {_toolName} may use here ({string.Join(", ", _roots)}). "
                + $"Open the folder in your assistant, or start {_toolName} mcp with --allow <folder>.");
        }
    }

    /// <summary>
    /// The path with every link along it followed; the part that does not exist yet is kept as written. Links that loop, or go
    /// more than 40 deep, leave the path as it stands, which then fails the check unless it is inside a root anyway.
    /// </summary>
    internal static string RealPath(string fullPath)
    {
        string current = fullPath;
        for (int links = 0; links < MaxLinks; links++)
        {
            string root = Path.GetPathRoot(current) ?? string.Empty;
            string[] parts = current[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            string walked = root;
            bool followed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string next = Path.Combine(walked, parts[i]);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.Combine([Path.GetFullPath(target.FullName), .. parts[(i + 1)..]]);
                    followed = true;
                    break;
                }

                if (!info.Exists)
                {
                    return Path.Combine([next, .. parts[(i + 1)..]]);
                }

                walked = next;
            }

            if (!followed)
            {
                return walked.Length > root.Length ? walked.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : walked;
            }
        }

        return current;
    }

    private static string WithSeparator(string folder)
    {
        return folder.EndsWith(Path.DirectorySeparatorChar) || folder.EndsWith(Path.AltDirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
    }
}
