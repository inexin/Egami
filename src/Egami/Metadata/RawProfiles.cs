using System.Globalization;
using System.Text;

namespace Egami.Metadata;

/// <summary>
/// The "Raw profile type" PNG text chunks ImageMagick and old Photoshop wrote before PNG had eXIf (2017): a newline, the profile
/// name, a newline, the byte count right aligned in 8 columns, a newline, then the bytes in hex, 72 digits a line.
/// </summary>
internal static class RawProfiles
{
    public const string Prefix = "Raw profile type ";

    private static ReadOnlySpan<byte> ExifHeader => "Exif\0\0"u8;

    /// <summary>Returns the profile kind a text key names (exif, APP1, iptc, xmp), or null for other keys.</summary>
    public static string? Kind(string key) =>
        key.StartsWith(Prefix, StringComparison.Ordinal) && key[Prefix.Length..] is "exif" or "APP1" or "iptc" or "xmp" ? key[Prefix.Length..] : null;

    /// <summary>Decodes the hex body; returns null and a reason when it is malformed or larger than <paramref name="maxBytes"/>.</summary>
    public static byte[]? Decode(string text, long maxBytes, out string? problem)
    {
        problem = null;
        int at = 0;
        string? name = Line(text, ref at);
        if (name is not null && name.Length == 0)
        {
            name = Line(text, ref at);   // the leading newline
        }

        string? count = Line(text, ref at);
        if (name is null || count is null || !long.TryParse(count.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long length))
        {
            problem = "it has no profile name and byte count";
            return null;
        }

        if (length > maxBytes)
        {
            problem = $"it says it holds {length:N0} bytes, over the metadata limit";
            return null;
        }

        byte[] bytes = new byte[length];
        int filled = 0, high = -1;
        for (; at < text.Length && filled < bytes.Length; at++)
        {
            char c = text[at];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            int digit = char.IsAsciiHexDigit(c) ? Convert.ToInt32(c.ToString(), 16) : -1;
            if (digit < 0)
            {
                problem = "its hex digits are broken";
                return null;
            }

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                bytes[filled++] = (byte)((high << 4) | digit);
                high = -1;
            }
        }

        if (filled < bytes.Length)
        {
            problem = $"it holds {filled:N0} of its {length:N0} bytes";
            return null;
        }

        return bytes;
    }

    /// <summary>Strips the JPEG "Exif\0\0" header an EXIF profile may start with.</summary>
    public static ReadOnlySpan<byte> WithoutExifHeader(ReadOnlySpan<byte> exif) => exif.StartsWith(ExifHeader) ? exif[ExifHeader.Length..] : exif;

    /// <summary>The IIM datasets of a raw IPTC profile, which is either bare datasets or a Photoshop resource block holding them.</summary>
    public static ReadOnlyMemory<byte> Iim(byte[] profile) =>
        profile.AsSpan().StartsWith("8BIM"u8) ? PhotoshopResources.FindIptc(profile, null) : profile;

    /// <summary>The text of a "Raw profile type" chunk for <paramref name="bytes"/>.</summary>
    public static string Encode(string kind, ReadOnlySpan<byte> bytes)
    {
        StringBuilder text = new(bytes.Length * 2 + (bytes.Length / 36) + 32);
        text.Append('\n').Append(kind).Append('\n').Append(bytes.Length.ToString(CultureInfo.InvariantCulture).PadLeft(8)).Append('\n');
        for (int i = 0; i < bytes.Length; i += 36)
        {
            text.Append(Convert.ToHexStringLower(bytes.Slice(i, Math.Min(36, bytes.Length - i)))).Append('\n');
        }

        return text.ToString();
    }

    private static string? Line(string text, ref int at)
    {
        if (at >= text.Length)
        {
            return null;
        }

        int end = text.IndexOf('\n', at);
        if (end < 0)
        {
            return null;
        }

        string line = text[at..end];
        at = end + 1;
        return line;
    }
}
