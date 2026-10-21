using System.Text;
using System.Text.RegularExpressions;

using Egami.Cli.Host;

namespace Egami.Cli.Pipeline;

/// <summary>
/// Expands inputs the same way in every shell (CLI design Q11): <c>*</c> and <c>?</c> within a name, <c>[abc]</c> sets and
/// <c>**</c> for any depth of folders; a folder with <c>--recursive</c> means every image below it. Results come sorted, so
/// <c>{index}</c> and the batch order never depend on the file system. <c>-</c> is standard input.
/// </summary>
internal static class Globber
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Expands the inputs; throws <see cref="UsageException"/> for a pattern that matches nothing or a missing file.</summary>
    public static List<InputFile> Expand(CliSession session, IReadOnlyList<string> patterns, bool recursive)
    {
        List<InputFile> files = [];
        HashSet<string> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string pattern in patterns)
        {
            if (pattern == "-")
            {
                files.Add(new InputFile(null, "-", string.Empty, files.Count));
                continue;
            }

            List<(string Path, string Directory)> found = [];
            if (!HasWildcard(pattern))
            {
                string full = session.ResolvePath(pattern);
                if (Directory.Exists(full))
                {
                    if (!recursive)
                    {
                        throw new UsageException($"'{pattern}' is a folder; add --recursive to take every image in it, or give a pattern such as '{pattern.TrimEnd('/', '\\')}/*.jpg'.");
                    }

                    foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    {
                        if (IsImage(session, file))
                        {
                            found.Add((file, Relative(full, Path.GetDirectoryName(file)!)));
                        }
                    }
                }
                else if (File.Exists(full))
                {
                    found.Add((full, string.Empty));
                }
                else
                {
                    throw new FileNotFoundException($"'{pattern}' does not exist.", full);
                }
            }
            else
            {
                (string root, string[] segments) = Split(session, pattern);
                if (Directory.Exists(root))
                {
                    Walk(root, root, segments, 0, found);
                }

                if (found.Count == 0)
                {
                    throw new UsageException($"No files match '{pattern}'.");
                }
            }

            found.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
            foreach ((string path, string directory) in found)
            {
                if (seen.Add(path))
                {
                    files.Add(new InputFile(path, Display(session, path), directory, files.Count));
                }
            }
        }

        return files;
    }

    /// <summary>Whether text holds a glob wildcard.</summary>
    public static bool HasWildcard(string text) => text.AsSpan().IndexOfAny("*?[") >= 0;

    private static bool IsImage(CliSession session, string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length > 0 && session.Formats.Any(f => f.Reader is not null && f.Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)));
    }

    private static (string Root, string[] Segments) Split(CliSession session, string pattern)
    {
        string[] parts = pattern.Replace('\\', '/').Split('/');
        int first = Array.FindIndex(parts, HasWildcard);
        string fixedPart = string.Join('/', parts[..first]);
        if (fixedPart.Length == 0 && pattern.StartsWith('/'))
        {
            fixedPart = "/";
        }

        string root = session.ResolvePath(fixedPart.Length == 0 ? "." : fixedPart + (fixedPart.EndsWith(':') ? "/" : string.Empty));
        return (root, parts[first..].Where(p => p.Length > 0).ToArray());
    }

    private static void Walk(string root, string folder, string[] segments, int index, List<(string Path, string Directory)> found)
    {
        if (index == segments.Length)
        {
            return;
        }

        string segment = segments[index];
        bool last = index == segments.Length - 1;
        if (segment == "**")
        {
            // ** matches no folder, one, or many.
            Walk(root, folder, segments, index + 1, found);
            foreach (string child in SafeDirectories(folder))
            {
                Walk(root, child, segments, index, found);
            }

            return;
        }

        Regex match = ToRegex(segment);
        if (last)
        {
            foreach (string file in SafeFiles(folder))
            {
                if (match.IsMatch(Path.GetFileName(file)))
                {
                    found.Add((file, Relative(root, folder)));
                }
            }
        }
        else
        {
            foreach (string child in SafeDirectories(folder))
            {
                if (match.IsMatch(Path.GetFileName(child)))
                {
                    Walk(root, child, segments, index + 1, found);
                }
            }
        }
    }

    private static string[] SafeFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Regex ToRegex(string segment)
    {
        StringBuilder text = new("^");
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            switch (c)
            {
                case '*': text.Append(".*"); break;
                case '?': text.Append('.'); break;
                case '[':
                    int end = segment.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        text.Append(@"\[");
                        break;
                    }

                    string set = segment[(i + 1)..end];
                    text.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set)).Append(']');
                    i = end;
                    break;
                default: text.Append(Regex.Escape(c.ToString())); break;
            }
        }

        RegexOptions options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(text.Append('$').ToString(), options);
    }

    private static string Relative(string root, string folder)
    {
        string relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }

    private static string Display(CliSession session, string path)
    {
        string relative = Path.GetRelativePath(session.WorkingDirectory, path);
        return relative.StartsWith("..", PathComparison) ? path : relative.Replace('\\', '/');
    }
}
