// The lookup follows HarfBuzz's hb-ot-tag.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>Maps a BCP 47 language tag to the OpenType language system tags to try, best first, as HarfBuzz does.</summary>
internal static partial class LanguageTags
{
    private const int MaxTags = 3;

    public static uint T(string tag) => ShapeMapBuilder.Tag(tag.PadRight(4));

    /// <summary>
    /// The OpenType language tags for a BCP 47 tag such as <c>ur</c>, <c>zh-Hant-HK</c> or <c>x-hbot-ARA</c>; empty when it maps
    /// to none (the font's default language system).
    /// </summary>
    public static uint[] FromBcp47(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return [];
        }

        // HarfBuzz lowercases language strings, and canonicalizes '_' to '-'.
        string s = language.Replace('_', '-').ToLowerInvariant();
        List<uint> tags = [];

        int limit = -1;
        int privateUse = -1;
        if (s.StartsWith("x-", StringComparison.Ordinal))
        {
            privateUse = 0;
            limit = 0;
        }
        else
        {
            int i;
            for (i = 1; i < s.Length; i++)
            {
                if (s[i - 1] == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    if (s[i] == 'x')
                    {
                        privateUse = i;
                        if (limit < 0)
                        {
                            limit = i - 1;
                        }

                        break;
                    }

                    if (limit < 0)
                    {
                        limit = i - 1;
                    }
                }
            }

            if (limit < 0)
            {
                limit = s.Length;
            }
        }

        if (privateUse >= 0 && TryParsePrivateUse(language.Replace('_', '-'), privateUse, "-hbot", out uint privateTag))
        {
            return [privateTag];
        }

        FromLanguage(s, limit, tags);
        return [.. tags.Take(MaxTags)];
    }

    private static bool TryParsePrivateUse(string original, int start, string prefix, out uint tag)
    {
        tag = 0;
        int at = original.IndexOf(prefix, start, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return false;
        }

        at += prefix.Length;
        char[] chars = new char[4];
        if (at < original.Length && original[at] == '-')
        {
            at++;
            int i;
            for (i = 0; i < 8 && at + i < original.Length && Uri.IsHexDigit(original[at + i]); i++)
            {
            }

            if (i != 8)
            {
                return false;
            }

            tag = Convert.ToUInt32(original.Substring(at, 8), 16);
        }
        else
        {
            int i;
            for (i = 0; i < 4 && at + i < original.Length && char.IsAsciiLetterOrDigit(original[at + i]); i++)
            {
                chars[i] = char.ToUpperInvariant(original[at + i]);
            }

            if (i == 0)
            {
                return false;
            }

            for (; i < 4; i++)
            {
                chars[i] = ' ';
            }

            tag = T(new string(chars));
        }

        if ((tag & 0xDFDFDFDF) == ShapeMapBuilder.Tag("DFLT"))
        {
            tag ^= ~0xDFDFDFDFu;
        }

        return true;
    }

    private static void FromLanguage(string s, int limit, List<uint> tags)
    {
        if (FromComplexLanguage(s, limit, tags))
        {
            return;
        }

        int dashAt = s.IndexOf('-');
        int start = 0;
        if (dashAt >= 0 && limit >= 6)
        {
            int extlangEnd = s.IndexOf('-', dashAt + 1);
            int extlangLength = extlangEnd >= 0 ? extlangEnd - dashAt - 1 : s.Length - dashAt - 1;
            if (extlangLength == 3 && char.IsAsciiLetter(s[dashAt + 1]))
            {
                start = dashAt + 1;
            }
        }

        int dash = s.IndexOf('-', start);
        int firstLength = dash >= 0 ? dash - start : limit - start;
        string first = s.Substring(start, Math.Max(firstLength, 0));
        if (firstLength == 2)
        {
            int index = Array.BinarySearch(s_languages2, (first, ""), Comparer<(string Language, string Tag)>.Create((a, b) => string.CompareOrdinal(a.Language, b.Language)));
            if (index >= 0)
            {
                while (index > 0 && s_languages2[index - 1].Language == first)
                {
                    index--;
                }

                for (; index < s_languages2.Length && s_languages2[index].Language == first && tags.Count < MaxTags; index++)
                {
                    tags.Add(T(s_languages2[index].Tag));
                }

                return;
            }
        }
        else if (firstLength == 3)
        {
            int index = Array.BinarySearch(s_languages3, (first, ""), Comparer<(string Language, string Tag)>.Create((a, b) => string.CompareOrdinal(a.Language, b.Language)));
            if (index >= 0)
            {
                tags.Add(T(s_languages3[index].Tag));
                return;
            }

            index = Array.BinarySearch(s_languages3Multi, (first, 0, 0), Comparer<(string Language, int Offset, int Count)>.Create((a, b) => string.CompareOrdinal(a.Language, b.Language)));
            if (index >= 0)
            {
                (_, int offset, int count) = s_languages3Multi[index];
                for (int i = 0; i < count && tags.Count < MaxTags; i++)
                {
                    tags.Add(T(s_multiValues[offset + i]));
                }

                return;
            }

            if (Array.BinarySearch(s_blocked, first, StringComparer.Ordinal) >= 0)
            {
                return;
            }

            // Assume it is ISO 639-3 and use it upper-cased.
            tags.Add(T(first.ToUpperInvariant()));
            return;
        }

        int end = dashAt >= 0 ? dashAt : s.Length;
        if (end == 3)
        {
            tags.Add(T(s[..3].ToUpperInvariant()));
        }
    }

    /// <summary>Whether <paramref name="subtag"/> (starting with '-') appears in s[from..limit] followed by a non-alphanumeric.</summary>
    private static bool SubtagMatches(string s, int from, int limit, string subtag)
    {
        if (limit - from < subtag.Length)
        {
            return false;
        }

        while (true)
        {
            int at = s.IndexOf(subtag, from, StringComparison.Ordinal);
            if (at < 0 || at >= limit)
            {
                return false;
            }

            int next = at + subtag.Length;
            if (next >= s.Length || !char.IsAsciiLetterOrDigit(s[next]))
            {
                return true;
            }

            from = next;
        }
    }

    /// <summary>Whether s starting at <paramref name="from"/> is <paramref name="spec"/> followed by the end or a '-'.</summary>
    private static bool LangMatches(string s, int from, int limit, string spec)
    {
        if (limit - from < spec.Length || string.CompareOrdinal(s, from, spec, 0, spec.Length) != 0)
        {
            return false;
        }

        int next = from + spec.Length;
        return next >= s.Length || s[next] == '-';
    }

    private static string Rest(string s, int from) => from <= s.Length ? s[from..] : "";

    private static bool Starts(string s, int from, string prefix) =>
        s.Length - from >= prefix.Length && string.CompareOrdinal(s, from, prefix, 0, prefix.Length) == 0;
}
