using System.Globalization;
using System.Reflection;

namespace Egami.Formats.Extended.Dicom;

/// <summary>One public data element of PS3.6.</summary>
internal sealed record DicomDictionaryEntry(string Keyword, string Name, DicomVR[] VRs, string Multiplicity, bool Retired);

/// <summary>One UID of PS3.6 table A-1.</summary>
internal sealed record DicomUidEntry(string Uid, string Type, string Keyword, string Name);

/// <summary>
/// PS3.6's data dictionary and UID registry, from DicomDictionary.txt and DicomUids.txt (made by tools/make-dicom-dictionary.py),
/// read on first use.
/// </summary>
internal static class DicomDictionary
{
    private static readonly Lazy<Tables> Data = new(Load);

    /// <summary>Gets the PS3.6 edition the tables were made from, such as <c>DICOM PS3.6 2026d</c>.</summary>
    public static string Edition => Data.Value.Edition;

    /// <summary>Returns the entry of a tag, matching repeating groups such as (60xx,3000), or null.</summary>
    public static DicomDictionaryEntry? Find(DicomTag tag)
    {
        Tables tables = Data.Value;
        if (tables.Exact.TryGetValue(tag.Value, out DicomDictionaryEntry? entry))
        {
            return entry;
        }

        foreach ((uint mask, uint value, DicomDictionaryEntry masked) in tables.Masked)
        {
            if ((tag.Value & mask) == value)
            {
                return masked;
            }
        }

        return null;
    }

    /// <summary>Looks up the tag of a keyword.</summary>
    public static bool TryFind(string? keyword, out DicomTag tag)
    {
        if (keyword is not null && Data.Value.Keywords.TryGetValue(keyword, out uint value))
        {
            tag = new DicomTag((ushort)(value >> 16), (ushort)value);
            return true;
        }

        tag = default;
        return false;
    }

    /// <summary>Returns a UID's registry entry, or null.</summary>
    public static DicomUidEntry? FindUid(string uid)
    {
        return Data.Value.Uids.GetValueOrDefault(uid);
    }

    private static Tables Load()
    {
        Tables tables = new();
        foreach (string line in Lines("DicomDictionary.txt", out string edition))
        {
            tables.Edition = edition;
            string[] f = line.Split('\t');
            DicomVR[] vrs = f[1] == "-" ? [] : [.. f[1].Split('|').Select(v => Enum.TryParse(v, out DicomVR vr) ? vr : DicomVR.UN)];
            DicomDictionaryEntry entry = new(f[4], f[5], vrs, f[2], f[3] == "R");
            string tag = f[0];
            uint value = uint.Parse(tag.Replace('x', '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (tag.Contains('x', StringComparison.Ordinal))
            {
                uint mask = 0;
                for (int i = 0; i < 8; i++)
                {
                    mask = (mask << 4) | (tag[i] == 'x' ? 0u : 0xFu);
                }

                tables.Masked.Add((mask, value, entry));
            }
            else
            {
                tables.Exact[value] = entry;
            }

            tables.Keywords.TryAdd(entry.Keyword, value);
        }

        foreach (string line in Lines("DicomUids.txt", out _))
        {
            string[] f = line.Split('\t');
            tables.Uids[f[0]] = new DicomUidEntry(f[0], f[1], f[2], f[3]);
        }

        return tables;
    }

    private static List<string> Lines(string name, out string edition)
    {
        using Stream stream = typeof(DicomDictionary).Assembly.GetManifestResourceStream("Egami.Formats.Extended.Dicom." + name)
            ?? throw new InvalidOperationException($"The resource {name} is missing.");
        using StreamReader reader = new(stream);
        List<string> lines = [];
        edition = "";
        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith('#'))
            {
                edition = line[2..].Split(',')[0];
            }
            else if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private sealed class Tables
    {
        public string Edition { get; set; } = "";

        public Dictionary<uint, DicomDictionaryEntry> Exact { get; } = [];

        public List<(uint Mask, uint Value, DicomDictionaryEntry Entry)> Masked { get; } = [];

        public Dictionary<string, uint> Keywords { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, DicomUidEntry> Uids { get; } = new(StringComparer.Ordinal);
    }
}
