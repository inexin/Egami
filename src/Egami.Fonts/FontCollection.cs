using System.Xml;

using Egami.Fonts.Matching;

namespace Egami.Fonts;

/// <summary>A generic family name, matched against fixed per-platform preference lists.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
/// </example>
public enum GenericFamily
{
    /// <summary>A sans-serif face, such as Segoe UI, Helvetica Neue or DejaVu Sans.</summary>
    SansSerif,

    /// <summary>A serif face, such as Times New Roman or DejaVu Serif.</summary>
    Serif,

    /// <summary>A monospaced face, such as Consolas, Menlo or DejaVu Sans Mono.</summary>
    Monospace,

    /// <summary>A script face, such as Comic Sans MS or Apple Chancery.</summary>
    Cursive,

    /// <summary>A decorative face, such as Impact or Papyrus.</summary>
    Fantasy,
}

/// <summary>A matched face and the styling it lacks, from <see cref="FontFamily.Match(FontStyle)"/>.</summary>
/// <param name="Typeface">The face.</param>
/// <param name="Synthesis">The bold or italic the face lacks compared with the style asked for.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
/// </example>
public readonly record struct FontMatch(Typeface Typeface, FontSynthesis Synthesis)
{
    /// <summary>Returns the matched face at a size, carrying the synthesis.</summary>
    /// <param name="size">The em size in canvas units.</param>
    /// <returns>The font.</returns>
    public Font ToFont(float size) => new Font(Typeface, size) { Synthesis = Synthesis };
}

/// <summary>
/// A set of faces grouped into families: the caller's own, or <see cref="System"/> for the fonts installed on the machine. Finds
/// families by name and faces by style with the CSS Fonts Level 4 matching steps.
/// </summary>
/// <remarks>
/// <para>
/// Faces group under their typographic family (name ID 16, else 1). A name lookup tries, in order, the typographic family, the
/// legacy family (ID 1), the full name (ID 4) and the PostScript name (ID 6), comparing case-insensitively in every language the
/// fonts hold. A name not found returns <see langword="null"/> or <see langword="false"/>: nothing is swapped in silently.
/// </para>
/// <para>
/// Files added by path are indexed from their name and OS/2 tables only and loaded when a face is matched. Adding is thread-safe;
/// lookups see the faces added before they started.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
/// </example>
public sealed class FontCollection : IReadOnlyCollection<FontFamily>
{
    private static readonly Lazy<FontCollection> s_system = new(() => new FontCollection(isSystem: true));

    private readonly Lock _lock = new();
    private readonly bool _isSystem;
    private List<FaceEntry> _entries = [];
    private NameIndex? _index;
    private bool _indexed;

    /// <summary>Initializes an empty collection.</summary>
    /// <param name="options">How faces are loaded when matched, or <see langword="null"/> for <see cref="FontLoadOptions.Default"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
    /// </example>
    public FontCollection(FontLoadOptions? options = null)
    {
        Options = options ?? FontLoadOptions.Default;
        _indexed = true;
    }

    private FontCollection(bool isSystem)
    {
        _isSystem = isSystem;
        Options = FontLoadOptions.Default;
    }

    /// <summary>
    /// Gets the fonts installed on this machine, found without native calls by scanning the platform's font directories (Windows:
    /// the Windows and per-user Fonts folders; macOS: the System, Library and user Fonts folders; Linux: the fontconfig dir
    /// entries and the usual share/fonts folders). Indexed on first use; <see cref="Refresh"/> rescans.
    /// </summary>
    public static FontCollection System => s_system.Value;

    /// <summary>Gets the options faces are loaded with.</summary>
    public FontLoadOptions Options { get; }

    /// <summary>Gets the families, sorted by name.</summary>
    public IReadOnlyList<FontFamily> Families
    {
        get
        {
            List<FaceEntry> entries = Entries;
            return [.. entries.GroupBy(e => e.Family, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new FontFamily(g.First().Family, [.. g], Options))];
        }
    }

    /// <summary>Gets the number of families.</summary>
    public int Count => Families.Count;

    private List<FaceEntry> Entries
    {
        get
        {
            if (!Volatile.Read(ref _indexed))
            {
                lock (_lock)
                {
                    if (!_indexed)
                    {
                        _entries = IndexSystem(Options);
                        Volatile.Write(ref _indexed, true);
                    }
                }
            }

            return Volatile.Read(ref _entries);
        }
    }

    /// <summary>Returns the families, sorted by name.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<FontFamily> GetEnumerator() => Families.GetEnumerator();

    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds a loaded face.</summary>
    /// <param name="typeface">The face.</param>
    public void Add(Typeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        Append([new FaceEntry(typeface)]);
    }

    /// <summary>Adds every face of a font file, indexed from its names and loaded when matched.</summary>
    /// <param name="path">A .ttf, .otf, .ttc, .otc, .woff or .woff2 file; a web font is decompressed and loaded at once.</param>
    /// <returns>The number of faces added.</returns>
    /// <exception cref="UnsupportedFormatException">The file is not a TrueType or OpenType font or collection.</exception>
    /// <exception cref="CorruptDataException">The file's headers cannot be read.</exception>
    public int AddFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var found = new List<FaceEntry>();
        FaceIndexer.Index(path, found, Options);
        Append(found);
        return found.Count;
    }

    /// <summary>Adds every font file in a directory; files that cannot be read are skipped with a <c>Font.FileSkipped</c> diagnostic.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="recursive">Whether to look in subdirectories too.</param>
    /// <returns>The number of faces added.</returns>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public int AddDirectory(string path, bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The directory '{path}' does not exist.");
        }

        List<FaceEntry> found = IndexFiles(FontFiles(path, recursive), Options);
        Append(found);
        return found.Count;
    }

    /// <summary>Rescans the installed fonts (for <see cref="System"/>); a collection of the caller's own is left as it is.</summary>
    public void Refresh()
    {
        if (!_isSystem)
        {
            return;
        }

        lock (_lock)
        {
            _entries = IndexSystem(Options);
            Volatile.Write(ref _indexed, true);
        }
    }

    /// <summary>Finds a family by name: typographic family, legacy family, full name or PostScript name, in that order.</summary>
    /// <param name="name">The name, compared case-insensitively.</param>
    /// <param name="family">The family, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a face has the name.</returns>
    public bool TryGetFamily(string name, out FontFamily family)
    {
        ArgumentNullException.ThrowIfNull(name);
        List<FaceEntry> entries = Entries;
        NameIndex? index = Volatile.Read(ref _index);
        if (index is null || !ReferenceEquals(index.Entries, entries))
        {
            index = new NameIndex(entries, Options);
            Volatile.Write(ref _index, index);
        }

        return index.TryGet(name, out family);
    }

    /// <summary>Finds a family by name and matches a style in it.</summary>
    /// <param name="familyName">The family, full or PostScript name.</param>
    /// <param name="style">The style wanted.</param>
    /// <returns>The best face and the synthesis it wants, or <see langword="null"/> when no face has the name.</returns>
    public FontMatch? Match(string familyName, FontStyle style) => TryGetFamily(familyName, out FontFamily family) ? family.Match(style) : null;

    /// <summary>Matches a generic family through the platform's preference list, then the other platforms' lists.</summary>
    /// <param name="generic">The generic family.</param>
    /// <param name="style">The style wanted.</param>
    /// <returns>The best face of the first listed family present, or <see langword="null"/> when none is present.</returns>
    public FontMatch? Match(GenericFamily generic, FontStyle style)
    {
        foreach (string name in GenericNames(generic))
        {
            if (Match(name, style) is FontMatch match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Finds a family by name and returns the face that best matches a style.</summary>
    /// <param name="familyName">The family, full or PostScript name.</param>
    /// <param name="style">The style wanted.</param>
    /// <returns>The face, or <see langword="null"/> when no face has the name.</returns>
    public Typeface? MatchTypeface(string familyName, FontStyle style) => Match(familyName, style)?.Typeface;

    /// <summary>The families of one snapshot of the faces, by every name in lookup order, built on first lookup.</summary>
    private sealed class NameIndex
    {
        private readonly Dictionary<string, FontFamily>[] _byKind;

        public NameIndex(List<FaceEntry> entries, FontLoadOptions options)
        {
            Entries = entries;
            Func<NameSet, string[]>[] kinds = [n => n.Typographic, n => n.Legacy, n => n.Full, n => n.PostScript];
            _byKind = new Dictionary<string, FontFamily>[kinds.Length];
            for (int k = 0; k < kinds.Length; k++)
            {
                var groups = new Dictionary<string, List<FaceEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (FaceEntry entry in entries)
                {
                    foreach (string name in kinds[k](entry.Names).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!groups.TryGetValue(name, out List<FaceEntry>? list))
                        {
                            groups[name] = list = [];
                        }

                        list.Add(entry);
                    }
                }

                _byKind[k] = groups.ToDictionary(g => g.Key, g => new FontFamily(g.Value[0].Family, [.. g.Value], options), StringComparer.OrdinalIgnoreCase);
            }
        }

        public List<FaceEntry> Entries { get; }

        public bool TryGet(string name, out FontFamily family)
        {
            foreach (Dictionary<string, FontFamily> kind in _byKind)
            {
                if (kind.TryGetValue(name, out FontFamily? found))
                {
                    family = found;
                    return true;
                }
            }

            family = null!;
            return false;
        }
    }

    internal static IEnumerable<string> GenericNames(GenericFamily generic)
    {
        string[][] lists = generic switch
        {
            GenericFamily.Serif => [["Times New Roman", "Georgia", "Cambria"], ["Times", "Times New Roman", "New York"], ["DejaVu Serif", "Liberation Serif", "Noto Serif"]],
            GenericFamily.Monospace => [["Consolas", "Cascadia Mono", "Courier New"], ["Menlo", "SF Mono", "Courier"], ["DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono"]],
            GenericFamily.Cursive => [["Comic Sans MS", "Segoe Script"], ["Apple Chancery", "Snell Roundhand"], ["URW Chancery L", "Comic Neue"]],
            GenericFamily.Fantasy => [["Impact", "Gabriola"], ["Papyrus", "Chalkduster"], ["URW Bookman", "Cantarell"]],
            _ => [["Segoe UI", "Arial"], ["Helvetica Neue", "Helvetica"], ["DejaVu Sans", "Liberation Sans", "Noto Sans"]],
        };
        int first = OperatingSystem.IsWindows() ? 0 : OperatingSystem.IsMacOS() ? 1 : 2;
        foreach (string name in lists[first])
        {
            yield return name;
        }

        for (int i = 0; i < lists.Length; i++)
        {
            if (i != first)
            {
                foreach (string name in lists[i])
                {
                    yield return name;
                }
            }
        }
    }

    /// <summary>Gets the directories the installed fonts are read from on this platform, existing or not.</summary>
    internal static List<string> SystemDirectories()
    {
        var dirs = new List<string>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string windows = Environment.GetEnvironmentVariable("WINDIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            dirs.Add(Path.Combine(windows, "Fonts"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dirs.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/System/Library/Fonts");
            dirs.Add("/Library/Fonts");
            dirs.Add(Path.Combine(home, "Library", "Fonts"));
        }
        else
        {
            ReadFontconfig("/etc/fonts/fonts.conf", dirs, home, 0);
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            dirs.Add(Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "fonts"));
            dirs.Add(Path.Combine(home, ".fonts"));
        }

        return [.. dirs.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Adds the dir entries of a fontconfig file and of the files its include entries name, a few levels deep.</summary>
    internal static void ReadFontconfig(string file, List<string> dirs, string home, int depth)
    {
        if (depth > 3 || !File.Exists(file))
        {
            return;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(file, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name is not ("dir" or "include"))
                {
                    continue;
                }

                bool include = reader.Name == "include";
                string? prefix = reader.GetAttribute("prefix");
                string value = reader.ReadElementContentAsString().Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                string path = Expand(value, prefix, home, Path.GetDirectoryName(file) ?? "/");
                if (!include)
                {
                    dirs.Add(path);
                }
                else if (Directory.Exists(path))
                {
                    foreach (string conf in Directory.EnumerateFiles(path, "*.conf").Order(StringComparer.Ordinal))
                    {
                        ReadFontconfig(conf, dirs, home, depth + 1);
                    }
                }
                else
                {
                    ReadFontconfig(path, dirs, home, depth + 1);
                }
            }
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable configuration adds nothing; the default directories still apply.
        }
    }

    private static string Expand(string value, string? prefix, string home, string relativeTo)
    {
        if (prefix == "xdg")
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, value);
        }

        if (value.StartsWith('~'))
        {
            return home + value[1..];
        }

        return Path.IsPathRooted(value) ? value : Path.Combine(relativeTo, value);
    }

    private static IEnumerable<string> FontFiles(string directory, bool recursive)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (string file in Directory.EnumerateFiles(directory, "*", options))
        {
            string ext = Path.GetExtension(file);
            if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otc", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".woff", StringComparison.OrdinalIgnoreCase) || ext.Equals(".woff2", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static List<FaceEntry> IndexSystem(FontLoadOptions options)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string dir in SystemDirectories())
        {
            if (Directory.Exists(dir))
            {
                foreach (string file in FontFiles(dir, recursive: true))
                {
                    if (seen.Add(Path.GetFullPath(file)))
                    {
                        files.Add(file);
                    }
                }
            }
        }

        return IndexFiles(files, options);
    }

    private static List<FaceEntry> IndexFiles(IEnumerable<string> files, FontLoadOptions options)
    {
        string[] list = [.. files];
        var results = new List<FaceEntry>[list.Length];
        Parallel.For(0, list.Length, i =>
        {
            var found = new List<FaceEntry>();
            try
            {
                FaceIndexer.Index(list[i], found, options);
            }
            catch (Exception e) when (e is EgamiException or IOException or UnauthorizedAccessException)
            {
                found.Clear();
                Typeface.Report(options with { Strict = false }, "Font.FileSkipped", $"The font file '{list[i]}' cannot be indexed ({e.Message}); it is skipped.", -1);
            }

            results[i] = found;
        });
        return [.. results.SelectMany(r => r)];
    }

    private void Append(List<FaceEntry> added)
    {
        _ = Entries;
        lock (_lock)
        {
            Volatile.Write(ref _entries, [.. _entries, .. added]);
        }
    }
}
