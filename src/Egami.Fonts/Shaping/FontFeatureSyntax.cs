// The grammar follows HarfBuzz's hb_feature_from_string (MIT); see THIRD-PARTY-NOTICES.md.
using System.Globalization;

namespace Egami.Fonts.Shaping;

/// <summary>Parses HarfBuzz's feature syntax: <c>kern</c>, <c>+kern</c>, <c>-liga</c>, <c>aalt=2</c>, <c>kern[3:5]=0</c>, <c>"ss01"=on</c>.</summary>
internal static class FontFeatureSyntax
{
    public static FeatureSetting Parse(string text)
    {
        if (TryParse(text, out FeatureSetting feature))
        {
            return feature;
        }

        throw new FormatException($"\"{text}\" is not a feature setting.");
    }

    public static bool TryParse(string text, out FeatureSetting feature)
    {
        feature = default;
        int p = 0;
        string s = text;
        SkipSpaces(s, ref p);

        // Prefix: '-' turns the feature off, '+' on.
        uint value = 1;
        if (p < s.Length && (s[p] == '-' || s[p] == '+'))
        {
            value = s[p] == '-' ? 0u : 1u;
            p++;
            SkipSpaces(s, ref p);
        }

        // Tag, optionally quoted.
        char quote = '\0';
        if (p < s.Length && (s[p] == '\'' || s[p] == '"'))
        {
            quote = s[p++];
        }

        int tagStart = p;
        while (p < s.Length && s[p] != ' ' && s[p] != '=' && s[p] != '[' && s[p] != quote)
        {
            p++;
        }

        int tagLength = p - tagStart;
        if (tagLength is < 1 or > 4)
        {
            return false;
        }

        if (quote != '\0')
        {
            if (p >= s.Length || s[p] != quote)
            {
                return false;
            }

            p++;
        }

        uint tag = ShapeMapBuilder.Tag(s.Substring(tagStart, tagLength).PadRight(4));
        SkipSpaces(s, ref p);

        // Indices: [start:end], [start:], [:end], [n].
        int start = 0;
        int end = int.MaxValue;
        if (p < s.Length && s[p] == '[')
        {
            p++;
            SkipSpaces(s, ref p);
            bool hasStart = TryParseUInt(s, ref p, out uint startValue);
            if (hasStart)
            {
                start = (int)Math.Min(startValue, int.MaxValue);
            }

            SkipSpaces(s, ref p);
            if (p < s.Length && (s[p] == ':' || s[p] == ';'))
            {
                p++;
                SkipSpaces(s, ref p);
                if (TryParseUInt(s, ref p, out uint endValue))
                {
                    end = (int)Math.Min(endValue, int.MaxValue);
                }
            }
            else
            {
                if (!hasStart)
                {
                    return false;
                }

                end = start == int.MaxValue ? int.MaxValue : start + 1;
            }

            SkipSpaces(s, ref p);
            if (p >= s.Length || s[p] != ']')
            {
                return false;
            }

            p++;
            SkipSpaces(s, ref p);
        }

        // Postfix value: =N, =on, =off.
        if (p < s.Length && s[p] == '=')
        {
            p++;
            SkipSpaces(s, ref p);
            if (TryParseUInt(s, ref p, out uint parsed))
            {
                value = parsed;
            }
            else if (TryParseBool(s, ref p, out bool on))
            {
                value = on ? 1u : 0u;
            }
            else
            {
                return false;
            }

            SkipSpaces(s, ref p);
        }

        if (p != s.Length)
        {
            return false;
        }

        feature = new FeatureSetting(tag, value, start, end);
        return true;
    }

    private static void SkipSpaces(string s, ref int p)
    {
        while (p < s.Length && char.IsWhiteSpace(s[p]))
        {
            p++;
        }
    }

    private static bool TryParseUInt(string s, ref int p, out uint value)
    {
        int start = p;
        while (p < s.Length && char.IsAsciiDigit(s[p]))
        {
            p++;
        }

        if (p == start)
        {
            value = 0;
            return false;
        }

        return uint.TryParse(s.AsSpan(start, p - start), NumberStyles.None, CultureInfo.InvariantCulture, out value) || Fail(out value);
    }

    private static bool Fail(out uint value)
    {
        value = uint.MaxValue;
        return true;
    }

    private static bool TryParseBool(string s, ref int p, out bool value)
    {
        int start = p;
        while (p < s.Length && char.IsAsciiLetter(s[p]))
        {
            p++;
        }

        string word = s[start..p].ToLowerInvariant();
        value = word == "on";
        if (word is "on" or "off")
        {
            return true;
        }

        p = start;
        return false;
    }
}
