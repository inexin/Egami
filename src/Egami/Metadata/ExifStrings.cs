using System.Text;

namespace Egami.Metadata;

/// <summary>
/// The EXIF tags whose text is not plain ASCII: the Windows XP tags (UTF-16 little-endian in BYTE values, whatever the block's
/// byte order) and the encoded strings (UNDEFINED values with an 8-byte character-code prefix, CIPA DC-008 4.6.5).
/// </summary>
internal static class ExifStrings
{
    private static ReadOnlySpan<byte> AsciiCode => "ASCII\0\0\0"u8;

    private static ReadOnlySpan<byte> UnicodeCode => "UNICODE\0"u8;

    private static ReadOnlySpan<byte> JisCode => "JIS\0\0\0\0\0"u8;

    public static bool IsXp(ExifKey key) => key.Ifd == ExifIfd.Primary && key.Tag is >= 0x9C9B and <= 0x9C9F;

    public static bool IsEncoded(ExifKey key) =>
        key == ExifKey.UserComment || key == ExifKey.GpsProcessingMethod || key == ExifKey.GpsAreaInformation;

    /// <summary>UTF-16 little-endian up to the first zero character.</summary>
    public static string DecodeXp(ReadOnlySpan<byte> bytes)
    {
        int end = 0;
        while (end + 1 < bytes.Length && (bytes[end] | bytes[end + 1]) != 0)
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(bytes[..end]);
    }

    public static byte[] EncodeXp(string text) => [.. Encoding.Unicode.GetBytes(text), 0, 0];

    /// <summary>The text after the character-code prefix; null when the value has no prefix.</summary>
    public static string? DecodeEncoded(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        if (bytes.Length < 8)
        {
            return null;
        }

        ReadOnlySpan<byte> code = bytes[..8], text = bytes[8..];
        if (code.SequenceEqual(UnicodeCode))
        {
            text = text[..(text.Length & ~1)];
            // Some cameras write UTF-16 in the other byte order; the order whose text reads better wins, the block's on a tie.
            string first = TrimZeros((bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(text));
            string other = TrimZeros((bigEndian ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(text));
            return Score(other) > Score(first) ? other : first;
        }

        if (code.SequenceEqual(JisCode))
        {
            try
            {
                return TrimZeros(Encoding.GetEncoding(932).GetString(text));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                // Shift-JIS needs the app to register CodePagesEncodingProvider; without it the bytes read as the other text does.
            }
        }

        int end = text.IndexOf((byte)0);
        if (end >= 0)
        {
            text = text[..end];
        }

        return System.Text.Unicode.Utf8.IsValid(text) ? Encoding.UTF8.GetString(text) : Encoding.Latin1.GetString(text);
    }

    /// <summary>ASCII when the text is ASCII, else UNICODE in the block's byte order.</summary>
    public static byte[] EncodeEncoded(string text, bool bigEndian) => Ascii.IsValid(text)
        ? [.. AsciiCode, .. Encoding.ASCII.GetBytes(text)]
        : [.. UnicodeCode, .. (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetBytes(text)];

    private static string TrimZeros(string text) => text.TrimEnd('\0');

    // Text in the wrong byte order turns Latin characters into CJK ones, and others into unassigned, private-use or control
    // characters and unpaired surrogates.
    private static int Score(string text)
    {
        int bad = 0, latin = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (c <= 0xFF && !char.IsControl(c))
            {
                latin++;
            }

            if (char.IsSurrogate(c) || (char.IsControl(c) && c is not '\t' and not '\n' and not '\r')
                || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.PrivateUse)
            {
                bad++;
            }
        }

        return latin - (4 * bad);
    }
}
