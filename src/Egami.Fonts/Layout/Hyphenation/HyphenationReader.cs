using System.Text;

namespace Egami.Fonts.Layout.Hyphenation;

/// <summary>
/// Reads hyphenation patterns from text: hyph-utf8's <c>.tex</c> and <c>.pat.txt</c>/<c>.hyp.txt</c> files (as the TeXbook,
/// appendix H, defines <c>\patterns</c> and <c>\hyphenation</c>) and LibreOffice's libhyphen <c>.dic</c> files (as the hyphen
/// package's README describes them).
/// </summary>
internal static class HyphenationReader
{
    public static HyphenationFormat Detect(ReadOnlySpan<byte> data)
    {
        if (CompiledHyphenation.HasSignature(data))
        {
            return HyphenationFormat.Compiled;
        }

        if (CharsetName(data) is not null)
        {
            return HyphenationFormat.LibHyphen;
        }

        string text = Utf8(data);
        return text.Contains("\\patterns", StringComparison.Ordinal) || text.Contains("\\hyphenation", StringComparison.Ordinal)
            || text.Contains("\\input", StringComparison.Ordinal)
            ? HyphenationFormat.TeX
            : HyphenationFormat.PatternList;
    }

    public static string Utf8(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("\xEF\xBB\xBF"u8))
        {
            data = data[3..];
        }

        return Encoding.UTF8.GetString(data);
    }

    public static Hyphenator ReadPatternList(string patterns, string? exceptions, int minBefore, int minAfter)
    {
        PatternTrieBuilder builder = new();
        foreach (string token in Tokens(patterns))
        {
            builder.Add(token);
        }

        Dictionary<string, int[]> exceptionTable = new(StringComparer.Ordinal);
        if (exceptions is not null)
        {
            foreach (string token in Tokens(exceptions))
            {
                Hyphenator.AddException(exceptionTable, token);
            }
        }

        return new Hyphenator(builder.Build(), exceptionTable, [], minBefore, minAfter, []);
    }

    // White-space separated words, skipping lines that start with %.
    private static IEnumerable<string> Tokens(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '%')
            {
                continue;
            }

            foreach (string token in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return token;
            }
        }
    }

    public static Hyphenator ReadTeX(string text)
    {
        (int minBefore, int minAfter) = TeXMinimums(text);
        PatternTrieBuilder builder = new();
        Dictionary<string, int[]> exceptions = new(StringComparer.Ordinal);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '%')
            {
                i = SkipComment(text, i);
            }
            else if (c == '\\')
            {
                string name = ControlName(text, ref i);
                if (name is "patterns" or "hyphenation")
                {
                    while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '%'))
                    {
                        i = text[i] == '%' ? SkipComment(text, i) : i + 1;
                    }

                    if (i < text.Length && text[i] == '{')
                    {
                        i = ReadGroup(text, i + 1, name == "patterns" ? builder : null, exceptions);
                    }
                }
                else if (name == "input")
                {
                    while (i < text.Length && text[i] is ' ' or '\t' or '{')
                    {
                        i++;
                    }

                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '%' and not '\\')
                    {
                        i++;
                    }

                    throw new NotSupportedException(
                        $"The patterns load '{text[start..i].TrimEnd('}')}' with \\input; load that file instead and add this file's exceptions with WithExceptions.");
                }
                else if (name == "endinput")
                {
                    break;
                }
            }
            else
            {
                i++;
            }
        }

        if (builder.Count == 0 && exceptions.Count == 0)
        {
            throw new InvalidDataException("The TeX file has no \\patterns or \\hyphenation group.");
        }

        return new Hyphenator(builder.Build(), exceptions, [], minBefore, minAfter, []);
    }

    // Reads a group's words up to its closing brace; returns the index after it.
    private static int ReadGroup(string text, int i, PatternTrieBuilder? patterns, Dictionary<string, int[]> exceptions)
    {
        StringBuilder token = new();
        while (true)
        {
            if (i >= text.Length)
            {
                throw new InvalidDataException("A \\patterns or \\hyphenation group has no closing brace.");
            }

            char c = text[i];
            if (c == '}' || char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    if (patterns is not null)
                    {
                        patterns.Add(token.ToString());
                    }
                    else
                    {
                        Hyphenator.AddException(exceptions, token.ToString());
                    }

                    token.Clear();
                }

                i++;
                if (c == '}')
                {
                    return i;
                }
            }
            else if (c == '%')
            {
                // As in TeX, a comment also takes its line end, so the word goes on after it.
                i = SkipComment(text, i);
            }
            else if (c == '^' && i + 2 < text.Length && text[i + 1] == '^')
            {
                token.Append(Escape(text, ref i));
            }
            else if (c == '\\')
            {
                ControlName(text, ref i);
            }
            else
            {
                if (c != '{')
                {
                    token.Append(c);
                }

                i++;
            }
        }
    }

    // TeX's ^^ notation: ^^^^xxxx (four hex digits), ^^xx (two lower-case hex digits) or ^^c (c ± 64).
    private static char Escape(string text, ref int i)
    {
        if (i + 8 <= text.Length && text[i + 2] == '^' && text[i + 3] == '^' && IsHex(text.AsSpan(i + 4, 4)))
        {
            char result = (char)Convert.ToInt32(text.Substring(i + 4, 4), 16);
            i += 8;
            return result;
        }

        if (i + 4 <= text.Length && IsHex(text.AsSpan(i + 2, 2)))
        {
            char result = (char)Convert.ToInt32(text.Substring(i + 2, 2), 16);
            i += 4;
            return result;
        }

        char c = text[i + 2];
        i += 3;
        return (char)(c < 64 ? c + 64 : c - 64);
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string ControlName(string text, ref int i)
    {
        int start = ++i;
        if (i < text.Length && char.IsAsciiLetter(text[i]))
        {
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }
        }
        else if (i < text.Length)
        {
            i++;
        }

        return text[start..i];
    }

    private static int SkipComment(string text, int i)
    {
        int end = text.IndexOf('\n', i);
        return end < 0 ? text.Length : end + 1;
    }

    // hyph-utf8's header: "% hyphenmins:" then "%     typesetting:" with "left: N" and "right: N" under it.
    private static (int Before, int After) TeXMinimums(string text)
    {
        int before = 2;
        int after = 3;
        int minsIndent = -1;
        int typesettingIndent = -1;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!line.StartsWith('%'))
            {
                if (line.Trim().Length > 0)
                {
                    break;
                }

                continue;
            }

            string body = line[1..];
            int indent = body.Length - body.TrimStart(' ').Length;
            string item = body.Trim().TrimStart('-', ' ');
            if (item == "hyphenmins:")
            {
                minsIndent = indent;
                typesettingIndent = -1;
                continue;
            }

            if (minsIndent < 0)
            {
                continue;
            }

            if (indent <= minsIndent)
            {
                minsIndent = -1;
                continue;
            }

            if (item.EndsWith(':'))
            {
                typesettingIndent = item == "typesetting:" ? indent : -1;
                continue;
            }

            if (typesettingIndent >= 0 && indent > typesettingIndent)
            {
                if (TryValue(item, "left:", out int left))
                {
                    before = left;
                }
                else if (TryValue(item, "right:", out int right))
                {
                    after = right;
                }
            }
        }

        return (before, after);
    }

    private static bool TryValue(string item, string key, out int value)
    {
        value = 0;
        return item.StartsWith(key, StringComparison.Ordinal) && int.TryParse(item.AsSpan(key.Length).Trim(), out value)
            && value is >= 1 and <= 255;
    }

    public static Hyphenator ReadLibHyphen(byte[] data)
    {
        string? charset = CharsetName(data) ?? throw new InvalidDataException("A LibreOffice hyphenation file starts with its character set.");
        Encoding encoding = CharsetEncoding(charset);
        string text = encoding.GetString(data);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        PatternTrieBuilder builder = new();
        List<string> noHyphen = [];
        int before = 2;
        int after = 2;
        bool first = true;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (first)
            {
                first = false;
                continue;
            }

            if (line.Length == 0 || line[0] is '%' or '#')
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "LEFTHYPHENMIN":
                    before = Minimum(parts);
                    break;
                case "RIGHTHYPHENMIN":
                    after = Minimum(parts);
                    break;
                case "COMPOUNDLEFTHYPHENMIN" or "COMPOUNDRIGHTHYPHENMIN":
                    break;
                case "NOHYPHEN":
                    if (parts.Length > 1)
                    {
                        foreach (string s in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            noHyphen.Add(s.ToLowerInvariant());
                        }
                    }

                    break;
                case "NEXTLEVEL":
                    // The patterns before NEXTLEVEL break compound words into parts and the ones after it hyphenate the parts;
                    // the second level alone hyphenates whole words.
                    builder = new PatternTrieBuilder();
                    break;
                default:
                    if (parts[0].Contains('/'))
                    {
                        throw new NotSupportedException(
                            $"The pattern '{parts[0]}' is a replacement rule (spelling changes at the break), which Egami does not read yet.");
                    }

                    builder.Add(parts[0]);
                    break;
            }
        }

        return new Hyphenator(builder.Build(), new Dictionary<string, int[]>(StringComparer.Ordinal), [.. noHyphen], before, after, []);
    }

    private static int Minimum(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out int value) || value is < 1 or > 255)
        {
            throw new InvalidDataException($"{parts[0]} needs a count from 1 to 255.");
        }

        return value;
    }

    // The first line of a libhyphen file, when it names a character set.
    private static string? CharsetName(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("\xEF\xBB\xBF"u8))
        {
            data = data[3..];
        }

        int end = data.IndexOfAny((byte)'\n', (byte)'\r');
        ReadOnlySpan<byte> line = (end < 0 ? data : data[..end]).Trim((byte)' ');
        if (line.Length is 0 or > 32)
        {
            return null;
        }

        string name = Encoding.ASCII.GetString(line);
        string upper = name.ToUpperInvariant();
        bool known = upper is "UTF-8" or "KOI8-R" or "KOI8-U" or "TIS620-2533" || upper.StartsWith("ISO8859-", StringComparison.Ordinal)
            || upper.StartsWith("ISO-8859-", StringComparison.Ordinal) || upper.StartsWith("MICROSOFT-CP", StringComparison.Ordinal)
            || upper.StartsWith("CP", StringComparison.Ordinal) && upper.Length > 2 && upper[2..].All(char.IsAsciiDigit);
        return known ? name : null;
    }

    private static Encoding CharsetEncoding(string charset)
    {
        string upper = charset.ToUpperInvariant();
        if (upper == "UTF-8")
        {
            return new UTF8Encoding(false);
        }

        string name = upper switch
        {
            _ when upper.StartsWith("ISO8859-", StringComparison.Ordinal) => "iso-8859-" + upper[8..],
            _ when upper.StartsWith("MICROSOFT-CP", StringComparison.Ordinal) => "windows-" + upper[12..],
            _ when upper.StartsWith("CP", StringComparison.Ordinal) => "windows-" + upper[2..],
            "TIS620-2533" => "windows-874",
            _ => upper,
        };

        try
        {
            return name == "ISO-8859-1" || name == "iso-8859-1"
                ? Encoding.Latin1
                : CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            throw new NotSupportedException($"The character set '{charset}' is not supported.");
        }
    }
}
