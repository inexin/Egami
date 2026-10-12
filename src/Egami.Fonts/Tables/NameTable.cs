using System.Text;

using Egami.Fonts.Parsing;

namespace Egami.Fonts.Tables;

/// <summary>The naming table ('name'), decoded into strings once.</summary>
internal sealed class NameTable
{
    public static readonly NameTable Empty = new([]);

    public NameTable(FontNameRecord[] records)
    {
        Records = records;
    }

    public FontNameRecord[] Records { get; }

    /// <summary>
    /// Returns the English value of <paramref name="nameId"/>: Windows US English first, then any Windows English, then Mac English,
    /// then Unicode platform, then the first record of that id; <see langword="null"/> when there is none.
    /// </summary>
    public string? GetEnglish(int nameId)
    {
        FontNameRecord? best = null;
        int bestRank = int.MaxValue;
        foreach (FontNameRecord record in Records)
        {
            if (record.NameId != nameId || record.Value.Length == 0)
            {
                continue;
            }

            int rank = record switch
            {
                { PlatformId: 3, LanguageId: 0x409 } => 0,
                { PlatformId: 3 } when (record.LanguageId & 0x3FF) == 0x09 => 1,
                { PlatformId: 1, EncodingId: 0, LanguageId: 0 } => 2,
                { PlatformId: 0 } => 3,
                _ => 4,
            };
            if (rank < bestRank)
            {
                best = record;
                bestRank = rank;
            }
        }

        return best?.Value;
    }

    public static NameTable Read(ReadOnlySpan<byte> data, long offset)
    {
        var r = new FontReader(data, offset);
        ushort version = r.ReadUInt16();
        int count = r.ReadUInt16();
        int storage = r.ReadUInt16();
        var records = new List<FontNameRecord>(count);
        for (int i = 0; i < count; i++)
        {
            ushort platform = r.ReadUInt16();
            ushort encoding = r.ReadUInt16();
            ushort language = r.ReadUInt16();
            ushort nameId = r.ReadUInt16();
            int length = r.ReadUInt16();
            int stringOffset = r.ReadUInt16();
            if (storage + stringOffset + length > data.Length)
            {
                // A record pointing past the table is dropped; the rest still read.
                continue;
            }

            string? value = Decode(platform, encoding, data.Slice(storage + stringOffset, length));
            if (value is not null)
            {
                records.Add(new FontNameRecord(nameId, platform, encoding, language, value));
            }
        }

        _ = version;
        return new NameTable([.. records]);
    }

    /// <summary>Decodes one name string: UTF-16BE for the Unicode and Windows platforms, Mac Roman for Mac Roman; others are skipped.</summary>
    private static string? Decode(int platform, int encoding, ReadOnlySpan<byte> bytes)
    {
        if (platform == 0 || (platform == 3 && encoding is 0 or 1 or 10))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[..(bytes.Length & ~1)]);
        }

        if (platform == 1 && encoding == 0)
        {
            return MacRoman.Decode(bytes);
        }

        return null;
    }
}

/// <summary>The Mac OS Roman character set, used by Mac name records and (1,0) cmap subtables.</summary>
internal static class MacRoman
{
    private const string High =
        "ÄÅÇÉÑÖÜáàâäãåçéè" +
        "êëíìîïñóòôöõúùûü" +
        "†°¢£§•¶ß®©™´¨≠ÆØ" +
        "∞±≤≥¥µ∂∑∏π∫ªºΩæø" +
        "¿¡¬√ƒ≈∆«»… ÀÃÕŒœ" +
        "–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ" +
        "‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔ" +
        "ÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, data) =>
        {
            for (int i = 0; i < data.Length; i++)
            {
                chars[i] = ToChar(data[i]);
            }
        });
    }

    public static char ToChar(byte b) => b < 0x80 ? (char)b : High[b - 0x80];

    /// <summary>Returns the Mac Roman byte for <paramref name="codePoint"/>, or -1 when Mac Roman has none.</summary>
    public static int FromCodePoint(int codePoint)
    {
        if (codePoint < 0x80)
        {
            return codePoint;
        }

        int index = codePoint <= 0xFFFF ? High.IndexOf((char)codePoint, StringComparison.Ordinal) : -1;
        return index < 0 ? -1 : 0x80 + index;
    }
}
