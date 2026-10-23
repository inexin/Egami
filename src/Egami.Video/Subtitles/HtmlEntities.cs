using System.Text;

namespace Egami.Video.Subtitles;

/// <summary>HTML character references (<c>&amp;amp;</c>, <c>&amp;#x20;</c>, <c>&amp;notin;</c>), as WebVTT cue text decodes them.</summary>
internal static partial class HtmlEntities
{
    private static Dictionary<string, string>? s_names;
    private static int s_longest;

    // The HTML numeric character reference fix-ups for 0x80-0x9F (Windows-1252), 0 where the code point stays.
    private static readonly ushort[] C1 =
    [
        0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
        0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178,
    ];

    private static Dictionary<string, string> Names
    {
        get
        {
            if (s_names is { } names)
            {
                return names;
            }

            names = new Dictionary<string, string>(2300, StringComparer.Ordinal);
            int longest = 0;
            foreach (Range line in Table.AsSpan().Split('\n'))
            {
                ReadOnlySpan<char> entry = Table.AsSpan()[line];
                int tab = entry.IndexOf('\t');
                if (tab > 0)
                {
                    names[entry[..tab].ToString()] = entry[(tab + 1)..].ToString();
                    longest = Math.Max(longest, tab);
                }
            }

            s_longest = longest;
            return s_names = names;
        }
    }

    /// <summary>
    /// Consumes a character reference after the '&amp;' at <paramref name="at"/> and appends what it stands for, or a plain
    /// '&amp;' when it is none. Returns the position after what was consumed.
    /// </summary>
    public static int Consume(string text, int at, StringBuilder output)
    {
        int i = at + 1;
        if (i < text.Length && text[i] == '#')
        {
            return Numeric(text, at, output);
        }

        int length = 0;
        while (i + length < text.Length && char.IsAsciiLetterOrDigit(text[i + length]))
        {
            length++;
        }

        if (length == 0)
        {
            output.Append('&');
            return i;
        }

        Dictionary<string, string> names = Names;
        bool semicolon = i + length < text.Length && text[i + length] == ';';
        for (int n = Math.Min(length + (semicolon ? 1 : 0), s_longest); n > 0; n--)
        {
            if (names.TryGetValue(text.Substring(i, n), out string? value))
            {
                output.Append(value);
                return i + n;
            }
        }

        // Not a reference: the '&' and the name stay as text.
        output.Append(text, at, 1 + length);
        return i + length;
    }

    private static int Numeric(string text, int at, StringBuilder output)
    {
        int i = at + 2;
        bool hex = i < text.Length && text[i] is 'x' or 'X';
        if (hex)
        {
            i++;
        }

        int start = i;
        long code = 0;
        while (i < text.Length && (hex ? char.IsAsciiHexDigit(text[i]) : char.IsAsciiDigit(text[i])))
        {
            code = Math.Min((code * (hex ? 16 : 10)) + Convert.ToInt32(text[i].ToString(), hex ? 16 : 10), 0x110000);
            i++;
        }

        if (i == start)
        {
            output.Append(text, at, i - at);
            return i;
        }

        if (i < text.Length && text[i] == ';')
        {
            i++;
        }

        if (code == 0 || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
        {
            code = 0xFFFD;
        }
        else if (code is >= 0x80 and <= 0x9F && C1[code - 0x80] != 0)
        {
            code = C1[code - 0x80];
        }

        output.Append(char.ConvertFromUtf32((int)code));
        return i;
    }
}
