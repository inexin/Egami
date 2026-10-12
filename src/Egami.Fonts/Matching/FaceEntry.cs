using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;
using Egami.Fonts.Variations;

using Microsoft.Win32.SafeHandles;

namespace Egami.Fonts.Matching;

/// <summary>One indexed face: its names and style, read without loading glyphs, and the face itself once matched.</summary>
internal sealed class FaceEntry
{
    private readonly FontFileSource? _source;
    private Typeface? _face;

    public FaceEntry(Typeface face)
    {
        _face = face;
        Index = face.FaceIndex;
        Family = face.FamilyName;
        Style = face.Style;
        Names = NameSet.From(face.Name, face.FamilyName, face.PostScriptName);
    }

    public FaceEntry(Typeface face, string path)
        : this(face)
    {
        _source = new FontFileSource(path);
    }

    private FaceEntry(FaceEntry face, FontStyle style, NameSet names, VariableRange? range, int instance)
    {
        _source = face._source;
        _face = face._face;
        Index = face.Index;
        Family = face.Family;
        Style = style;
        Names = names;
        Range = range;
        Instance = instance;
    }

    /// <summary>Gets the axis ranges of a variable face's range entry, which matching can place anywhere on its axes.</summary>
    public VariableRange? Range { get; }

    /// <summary>Gets the named instance this entry stands for, or -1.</summary>
    public int Instance { get; } = -1;

    /// <summary>
    /// Returns the entries a face indexes as: itself, or for a variable face one range entry (first) and one per named instance, as
    /// CSS Fonts 4 lists them.
    /// </summary>
    public static List<FaceEntry> Expand(FaceEntry entry, VariationSpace? space, NameTable name, FontStyle defaultStyle)
    {
        if (space is null)
        {
            return [entry];
        }

        var range = VariableRange.Of(space, defaultStyle);
        var list = new List<FaceEntry> { new(entry, defaultStyle, entry.Names, range, -1) };
        for (int i = 0; i < space.Instances.Length; i++)
        {
            FvarInstance instance = space.Instances[i];
            string subfamily = VariationSpace.NameOf(name, instance.SubfamilyNameId, "Instance");
            string full = subfamily == "Regular" ? entry.Family : $"{entry.Family} {subfamily}";
            string? postScript = instance.PostScriptNameId is > 0 and < 0xFFFF ? name.GetEnglish(instance.PostScriptNameId) : null;
            FontStyle style = VariableRange.StyleAt(space, instance.Coordinates, defaultStyle);
            list.Add(new FaceEntry(entry, style, entry.Names.With(full, postScript), null, i));
        }

        return list;
    }

    /// <summary>Returns the entries of a loaded face: itself, or its range and named instances when it is variable.</summary>
    public static List<FaceEntry> Of(Typeface face, string? path = null)
    {
        FaceEntry entry = path is null ? new FaceEntry(face) : new FaceEntry(face, path);
        Typeface root = face.Root;
        return root == face ? Expand(entry, face.VariationData?.Space, face.Name, face.Style) : [entry];
    }
    public FaceEntry(FontFileSource source, int index, string family, FontStyle style, NameSet names)
    {
        _source = source;
        Index = index;
        Family = family;
        Style = style;
        Names = names;
    }

    public int Index { get; }

    /// <summary>Gets the English typographic family the face groups under.</summary>
    public string Family { get; }

    public FontStyle Style { get; }

    public NameSet Names { get; }

    public string? Path => _source?.Path;

    /// <summary>Loads the face on first use; faces of one file share its bytes.</summary>
    public Typeface GetTypeface(FontLoadOptions options)
    {
        Typeface? face = Volatile.Read(ref _face);
        if (face is null)
        {
            face = Typeface.FromMemory(_source!.GetBytes(options), Index, options);
            face = Interlocked.CompareExchange(ref _face, face, null) ?? face;
        }

        return Instance >= 0 ? face.WithNamedInstance(Instance) : face;
    }
}

/// <summary>A font file read once, shared by the faces it holds.</summary>
internal sealed class FontFileSource(string path)
{
    private byte[]? _bytes;

    public string Path { get; } = path;

    public byte[] GetBytes(FontLoadOptions options)
    {
        byte[]? bytes = Volatile.Read(ref _bytes);
        if (bytes is null)
        {
            bytes = Typeface.ReadFile(Path, options);
            bytes = Interlocked.CompareExchange(ref _bytes, bytes, null) ?? bytes;
        }

        return bytes;
    }
}

/// <summary>Every family, full and PostScript name of a face in every language, for case-insensitive lookup.</summary>
internal sealed class NameSet
{
    public NameSet(string[] typographic, string[] legacy, string[] full, string[] postScript)
    {
        Typographic = typographic;
        Legacy = legacy;
        Full = full;
        PostScript = postScript;
    }

    public string[] Typographic { get; }

    public string[] Legacy { get; }

    public string[] Full { get; }

    public string[] PostScript { get; }

    public static NameSet From(NameTable name, string family, string postScript)
    {
        var typographic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { family };
        var legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { postScript };
        bool hasTypographic = false;
        foreach (FontNameRecord r in name.Records)
        {
            hasTypographic |= r.NameId == 16;
        }

        foreach (FontNameRecord r in name.Records)
        {
            switch (r.NameId)
            {
                case 1:
                    legacy.Add(r.Value);
                    if (!hasTypographic)
                    {
                        typographic.Add(r.Value);
                    }

                    break;
                case 4:
                    full.Add(r.Value);
                    break;
                case 6:
                    ps.Add(r.Value);
                    break;
                case 16:
                    typographic.Add(r.Value);
                    break;
            }
        }

        return new NameSet([.. typographic], [.. legacy], [.. full], [.. ps]);
    }

    /// <summary>Returns the names with a named instance's full and PostScript names added.</summary>
    public NameSet With(string full, string? postScript)
        => new(Typographic, Legacy, [.. Full, full], postScript is null ? PostScript : [.. PostScript, postScript]);

    public static bool Contains(string[] names, string name)
    {
        foreach (string n in names)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reads the names and style of every face of a font file from its table directory, name, OS/2 and head tables only.</summary>
internal static class FaceIndexer
{
    private const int MaxNameTable = 1 << 20;

    public static void Index(string path, List<FaceEntry> into, FontLoadOptions options)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        long length = RandomAccess.GetLength(handle);
        Span<byte> header = stackalloc byte[12];
        if (length < 12 || RandomAccess.Read(handle, header, 0) < 12)
        {
            throw FontErrors.Truncated(0);
        }

        var source = new FontFileSource(path);
        uint magic = BigEndian.UInt32(header, 0);
        if (magic is SfntDirectory.WoffTag or SfntDirectory.Woff2Tag)
        {
            // Web fonts are compressed: their names are only reachable by decoding, so the faces load now.
            foreach (Typeface face in FontLoader.LoadAll(path, options))
            {
                into.AddRange(FaceEntry.Of(face, path));
            }

            return;
        }

        if (magic == SfntDirectory.CollectionTag)
        {
            int count = (int)Math.Min(BigEndian.UInt32(header, 8), 1024);
            byte[] offsets = Read(handle, 12, count * 4, length);
            for (int i = 0; i < count; i++)
            {
                IndexFace(handle, length, BigEndian.UInt32(offsets, i * 4), source, i, into);
            }
        }
        else
        {
            IndexFace(handle, length, 0, source, 0, into);
        }
    }

    private static void IndexFace(SafeFileHandle handle, long length, long offset, FontFileSource source, int index, List<FaceEntry> into)
    {
        byte[] head = Read(handle, offset, 12, length);
        uint version = BigEndian.UInt32(head, 0);
        if (version is not (SfntDirectory.TrueTypeVersion or SfntDirectory.OpenTypeVersion or SfntDirectory.AppleTrueVersion))
        {
            throw new UnsupportedFormatException("Not a TrueType or OpenType face.");
        }

        int tables = BigEndian.UInt16(head, 4);
        byte[] directory = Read(handle, offset + 12, tables * 16, length);
        NameTable name = NameTable.Empty;
        Os2Table? os2 = null;
        int macStyle = 0;
        bool hasGlyphs = false;
        byte[]? fvar = null;
        for (int t = 0; t < tables; t++)
        {
            var tag = new FontTag(BigEndian.UInt32(directory, t * 16));
            uint at = BigEndian.UInt32(directory, (t * 16) + 8), size = BigEndian.UInt32(directory, (t * 16) + 12);
            if (tag == Tags.Name)
            {
                name = NameTable.Read(Read(handle, at, (int)Math.Min(size, MaxNameTable), length), at);
            }
            else if (tag == Tags.OS2)
            {
                os2 = Os2Table.Read(Read(handle, at, (int)Math.Min(size, 128), length));
            }
            else if (tag == Tags.Head && size >= 46)
            {
                macStyle = BigEndian.UInt16(Read(handle, at + 44, 2, length), 0);
            }
            else if (tag == Tags.Glyf || tag == Tags.Cff || tag == Tags.Cff2)
            {
                hasGlyphs = true;
            }
            else if (tag == Tags.Fvar && size <= MaxNameTable)
            {
                fvar = Read(handle, at, (int)size, length);
            }
        }

        if (!hasGlyphs)
        {
            // Faces without outlines are left out.
            return;
        }

        string family = name.GetEnglish(16) ?? name.GetEnglish(1) ?? "Unknown";
        string postScript = name.GetEnglish(6) ?? family.Replace(" ", string.Empty, StringComparison.Ordinal);
        FontStyle style = StyleOf(os2, macStyle);
        var entry = new FaceEntry(source, index, family, style, NameSet.From(name, family, postScript));
        VariationSpace? space = null;
        if (fvar is not null)
        {
            try
            {
                space = VariationSpace.Read(fvar, 0, default, 0, (_, _, _) => { });
            }
            catch (CorruptDataException)
            {
                // A damaged fvar indexes the face as static; loading reports it.
            }
        }

        into.AddRange(FaceEntry.Expand(entry, space, name, style));
    }

    internal static FontStyle StyleOf(Os2Table? os2, int macStyle)
    {
        int weight = os2?.WeightClass ?? 0;
        if (weight is >= 1 and <= 9)
        {
            weight *= 100;
        }

        if (weight == 0)
        {
            weight = (macStyle & 1) != 0 ? 700 : 400;
        }

        int width = os2 is null || os2.WidthClass == 0 ? 5 : Math.Clamp((int)os2.WidthClass, 1, 9);
        FontSlant slant = os2 is not null && (os2.FsSelection & 0x200) != 0 ? FontSlant.Oblique
            : (os2 is not null && (os2.FsSelection & 1) != 0) || (macStyle & 2) != 0 ? FontSlant.Italic
            : FontSlant.Upright;
        return new FontStyle(Math.Clamp(weight, 1, 1000), width, slant);
    }

    private static byte[] Read(SafeFileHandle handle, long offset, int count, long length)
    {
        if (count < 0 || offset < 0 || offset + count > length)
        {
            throw FontErrors.Truncated(offset);
        }

        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = RandomAccess.Read(handle, buffer.AsSpan(read), offset + read);
            if (n <= 0)
            {
                throw FontErrors.Truncated(offset + read);
            }

            read += n;
        }

        return buffer;
    }
}
