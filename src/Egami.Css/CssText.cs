namespace Egami.Css;

/// <summary>ASCII helpers: CSS compares keywords ASCII case-insensitively, never with Unicode case folding.</summary>
internal static class CssText
{
    public static char ToAsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    public static bool EqualsIgnoreAsciiCase(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && ToAsciiLower(a[i]) != ToAsciiLower(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool StartsWithIgnoreAsciiCase(ReadOnlySpan<char> s, ReadOnlySpan<char> prefix)
    {
        return s.Length >= prefix.Length && EqualsIgnoreAsciiCase(s[..prefix.Length], prefix);
    }

    public static bool EndsWithIgnoreAsciiCase(ReadOnlySpan<char> s, ReadOnlySpan<char> suffix)
    {
        return s.Length >= suffix.Length && EqualsIgnoreAsciiCase(s[(s.Length - suffix.Length)..], suffix);
    }

    public static int IndexOfIgnoreAsciiCase(ReadOnlySpan<char> s, ReadOnlySpan<char> value)
    {
        for (int i = 0; i + value.Length <= s.Length; i++)
        {
            if (EqualsIgnoreAsciiCase(s.Slice(i, value.Length), value))
            {
                return i;
            }
        }

        return -1;
    }

    public static string ToAsciiLower(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] is not (>= 'A' and <= 'Z'))
        {
            i++;
        }

        if (i == s.Length)
        {
            return s;
        }

        return string.Create(s.Length, s, static (span, source) =>
        {
            for (int k = 0; k < source.Length; k++)
            {
                span[k] = ToAsciiLower(source[k]);
            }
        });
    }

    public static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    public static bool IsDigit(char c) => c is >= '0' and <= '9';

    public static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    public static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    public static bool IsIdentStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || c >= 0x80;

    public static bool IsIdentChar(char c) => IsIdentStart(c) || IsDigit(c) || c == '-';
}
