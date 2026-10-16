using System.Text;

using Egami.Fonts;
using Egami.Metafiles.Reading;

namespace Egami.Metafiles.Playback;

/// <summary>
/// The faces text records name, found as design question 8 decided: the options' fonts (the installed fonts by default), then a
/// built-in table of metric-compatible open fonts, then the caller's substitutions, then the generic family. Every glyph is
/// still placed by the record's own advances, so lines keep their length. One warning per face that was substituted.
/// </summary>
internal sealed class MetafileFonts(MetafileRenderOptions options, MetafileReport report)
{
    /// <summary>Metric-compatible open fonts for the Office and Windows core fonts, best first.</summary>
    private static readonly Dictionary<string, string[]> s_builtIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["Liberation Sans", "Arimo"],
        ["Helvetica"] = ["Liberation Sans", "Arimo", "Arial"],
        ["Times New Roman"] = ["Liberation Serif", "Tinos"],
        ["Times"] = ["Liberation Serif", "Tinos", "Times New Roman"],
        ["Courier New"] = ["Liberation Mono", "Cousine"],
        ["Courier"] = ["Liberation Mono", "Cousine", "Courier New"],
        ["Calibri"] = ["Carlito"],
        ["Cambria"] = ["Caladea"],
        ["Arial Narrow"] = ["Liberation Sans Narrow"],
        ["MS Sans Serif"] = ["Microsoft Sans Serif", "Liberation Sans", "Arimo"],
        ["MS Serif"] = ["Liberation Serif", "Tinos"],
        ["System"] = ["Microsoft Sans Serif", "Liberation Sans", "Arimo"],
    };

    private readonly FontCollection _fonts = options.Fonts ?? FontCollection.System;
    private readonly Dictionary<(string, int, bool), Resolved?> _cache = [];
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A face found for a requested name: the typeface, whether it is the face asked for, and synthesis needed.</summary>
    internal sealed record Resolved(Typeface Typeface, bool Exact, FontSynthesis Synthesis);

    /// <summary>Finds a typeface for a face name, weight and slant; null when no font is available at all.</summary>
    public Resolved? Resolve(string face, int weight, bool italic)
    {
        face = face.Trim();
        if (_cache.TryGetValue((face, weight, italic), out Resolved? cached))
        {
            return cached;
        }

        FontStyle style = new(weight <= 0 ? 400 : Math.Clamp(weight, 1, 1000), 5, italic ? FontSlant.Italic : FontSlant.Upright);
        Resolved? found = null;
        if (face.Length > 0 && _fonts.Match(face, style) is FontMatch exact)
        {
            found = new Resolved(exact.Typeface, true, exact.Synthesis);
        }
        else
        {
            IEnumerable<string> candidates = (s_builtIn.TryGetValue(face, out string[]? table) ? table : [])
                .Concat(options.FontSubstitutions is { } subs && subs.TryGetValue(face, out string? sub) ? [sub] : []);
            foreach (string candidate in candidates)
            {
                if (_fonts.Match(candidate, style) is FontMatch m)
                {
                    found = new Resolved(m.Typeface, false, m.Synthesis);
                    break;
                }
            }

            if (found is null && options.FontSubstitutions is { } map)
            {
                // The caller's table may also name the face case-insensitively.
                foreach ((string from, string to) in map)
                {
                    if (string.Equals(from, face, StringComparison.OrdinalIgnoreCase) && _fonts.Match(to, style) is FontMatch m)
                    {
                        found = new Resolved(m.Typeface, false, m.Synthesis);
                        break;
                    }
                }
            }

            if (found is null && _fonts.Match(Generic(face), style) is FontMatch g)
            {
                found = new Resolved(g.Typeface, false, g.Synthesis);
            }

            if (_warned.Add(face))
            {
                report.Warn(
                    "Metafile.FontSubstituted",
                    found is null
                        ? $"No font is available for the face '{face}'; its text is not drawn."
                        : $"The face '{face}' is not installed; '{found.Typeface.FamilyName}' is used, with the record's own advances (design question 8).");
            }
        }

        _cache[(face, weight, italic)] = found;
        return found;
    }

    /// <summary>The generic family a face name suggests: monospaced, serif or sans-serif.</summary>
    private static GenericFamily Generic(string face) =>
        face.Contains("Courier", StringComparison.OrdinalIgnoreCase) || face.Contains("Mono", StringComparison.OrdinalIgnoreCase) || face.Contains("Consol", StringComparison.OrdinalIgnoreCase)
            ? GenericFamily.Monospace
            : face.Contains("Times", StringComparison.OrdinalIgnoreCase) || face.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !face.Contains("Sans", StringComparison.OrdinalIgnoreCase)
                || face.Contains("Roman", StringComparison.OrdinalIgnoreCase) || face.Contains("Cambria", StringComparison.OrdinalIgnoreCase) || face.Contains("Georgia", StringComparison.OrdinalIgnoreCase)
                || face.Contains("Garamond", StringComparison.OrdinalIgnoreCase) || face.Contains("Book", StringComparison.OrdinalIgnoreCase)
                ? GenericFamily.Serif
                : GenericFamily.SansSerif;

    /// <summary>
    /// The text of an 8-bit string in the font's character set (its Windows code page); symbol fonts keep their codes, which
    /// <see cref="SymbolGlyph"/> maps.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, int charset)
    {
        int codePage = charset switch
        {
            2 => 0, // SYMBOL_CHARSET: the codes as they are
            77 => 10000,
            128 => 932,
            129 => 949,
            130 => 1361,
            134 => 936,
            136 => 950,
            161 => 1253,
            162 => 1254,
            163 => 1258,
            177 => 1255,
            178 => 1256,
            186 => 1257,
            204 => 1251,
            222 => 874,
            238 => 1250,
            _ => 1252,
        };
        if (codePage == 0)
        {
            return Encoding.Latin1.GetString(bytes);
        }

        Encoding? encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage);
        return (encoding ?? Encoding.Latin1).GetString(bytes);
    }

    /// <summary>
    /// The glyph for a character of a symbol font: Symbol and Wingdings have a (3,0) cmap whose codes are U+F020 to U+F0FF;
    /// a substitute takes the Unicode character the symbol stands for.
    /// </summary>
    public static ushort SymbolGlyph(Typeface typeface, string face, char c, bool exact)
    {
        if (exact)
        {
            ushort g = typeface.FindCharacterMap(3, 0)?.GetGlyph(0xF000 | (c & 0xFF)) ?? 0;
            if (g != 0)
            {
                return g;
            }

            g = typeface.GetGlyph(c < 0x100 ? 0xF000 | c : c);
            if (g != 0)
            {
                return g;
            }
        }

        int mapped = SymbolToUnicode(face, c & 0xFF);
        return typeface.GetGlyph(mapped > 0 ? mapped : c);
    }

    /// <summary>The Unicode character a Symbol or Wingdings code stands for, or 0 when the table has none.</summary>
    internal static int SymbolToUnicode(string face, int code)
    {
        if (face.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase))
        {
            return code < 0x20 || code > 0xFF ? 0 : s_wingdings[code - 0x20];
        }

        if (face.Equals("Symbol", StringComparison.OrdinalIgnoreCase))
        {
            return code < 0x20 || code > 0xFF ? 0 : s_symbol[code - 0x20];
        }

        return 0;
    }

    /// <summary>The Symbol font's codes 0x20 to 0xFF as Unicode (the Adobe Symbol encoding); 0 where nothing is assigned.</summary>
    private static readonly int[] s_symbol =
    [
        0x0020, 0x0021, 0x2200, 0x0023, 0x2203, 0x0025, 0x0026, 0x220B, 0x0028, 0x0029, 0x2217, 0x002B, 0x002C, 0x2212, 0x002E, 0x002F,
        0x0030, 0x0031, 0x0032, 0x0033, 0x0034, 0x0035, 0x0036, 0x0037, 0x0038, 0x0039, 0x003A, 0x003B, 0x003C, 0x003D, 0x003E, 0x003F,
        0x2245, 0x0391, 0x0392, 0x03A7, 0x0394, 0x0395, 0x03A6, 0x0393, 0x0397, 0x0399, 0x03D1, 0x039A, 0x039B, 0x039C, 0x039D, 0x039F,
        0x03A0, 0x0398, 0x03A1, 0x03A3, 0x03A4, 0x03A5, 0x03C2, 0x03A9, 0x039E, 0x03A8, 0x0396, 0x005B, 0x2234, 0x005D, 0x22A5, 0x005F,
        0xF8E5, 0x03B1, 0x03B2, 0x03C7, 0x03B4, 0x03B5, 0x03C6, 0x03B3, 0x03B7, 0x03B9, 0x03D5, 0x03BA, 0x03BB, 0x03BC, 0x03BD, 0x03BF,
        0x03C0, 0x03B8, 0x03C1, 0x03C3, 0x03C4, 0x03C5, 0x03D6, 0x03C9, 0x03BE, 0x03C8, 0x03B6, 0x007B, 0x007C, 0x007D, 0x223C, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0x20AC, 0x03D2, 0x2032, 0x2264, 0x2044, 0x221E, 0x0192, 0x2663, 0x2666, 0x2665, 0x2660, 0x2194, 0x2190, 0x2191, 0x2192, 0x2193,
        0x00B0, 0x00B1, 0x2033, 0x2265, 0x00D7, 0x221D, 0x2202, 0x2022, 0x00F7, 0x2260, 0x2261, 0x2248, 0x2026, 0x23D0, 0x23AF, 0x21B5,
        0x2135, 0x2111, 0x211C, 0x2118, 0x2297, 0x2295, 0x2205, 0x2229, 0x222A, 0x2283, 0x2287, 0x2284, 0x2282, 0x2286, 0x2208, 0x2209,
        0x2220, 0x2207, 0x00AE, 0x00A9, 0x2122, 0x220F, 0x221A, 0x22C5, 0x00AC, 0x2227, 0x2228, 0x21D4, 0x21D0, 0x21D1, 0x21D2, 0x21D3,
        0x25CA, 0x2329, 0x00AE, 0x00A9, 0x2122, 0x2211, 0x239B, 0x239C, 0x239D, 0x23A1, 0x23A2, 0x23A3, 0x23A7, 0x23A8, 0x23A9, 0x23AA,
        0, 0x232A, 0x222B, 0x2320, 0x23AE, 0x2321, 0x239E, 0x239F, 0x23A0, 0x23A4, 0x23A5, 0x23A6, 0x23AB, 0x23AC, 0x23AD, 0,
    ];

    /// <summary>
    /// The Wingdings font's codes 0x20 to 0xFF as Unicode where Unicode has the same symbol (Unicode added the Wingdings and
    /// Webdings sets in 7.0); 0 where the table has none, and that character draws as its own code.
    /// </summary>
    private static readonly int[] s_wingdings =
    [
        0x0020, 0x1F589, 0x2702, 0x2701, 0x1F453, 0x1F56D, 0x1F56E, 0x1F56F, 0x1F57F, 0x2706, 0x1F582, 0x1F583, 0x1F4EA, 0x1F4EB, 0x1F4EC, 0x1F4ED,
        0x1F4C1, 0x1F4C2, 0x1F4C4, 0x1F5CF, 0x1F5D0, 0x1F5C4, 0x231B, 0x1F5AE, 0x1F5B0, 0x1F5B2, 0x1F5B3, 0x1F5B4, 0x1F5AB, 0x1F5AC, 0x2707, 0x270D,
        0x1F58E, 0x270C, 0x1F44C, 0x1F44D, 0x1F44E, 0x261C, 0x261E, 0x261D, 0x261F, 0x1F590, 0x263A, 0x1F610, 0x2639, 0x1F4A3, 0x2620, 0x1F3F3,
        0x1F3F1, 0x2708, 0x263C, 0x1F4A7, 0x2744, 0x1F546, 0x271E, 0x1F548, 0x2720, 0x2721, 0x262A, 0x262F, 0x0950, 0x2638, 0x2648, 0x2649,
        0x264A, 0x264B, 0x264C, 0x264D, 0x264E, 0x264F, 0x2650, 0x2651, 0x2652, 0x2653, 0x1F670, 0x1F675, 0x25CF, 0x1F53E, 0x25A0, 0x25A1,
        0x1F790, 0x2751, 0x2752, 0x2B27, 0x29EB, 0x25C6, 0x2756, 0x2B25, 0x2327, 0x2BB9, 0x2318, 0x1F3F5, 0x1F3F6, 0x1F676, 0x1F677, 0,
        0x24EA, 0x2460, 0x2461, 0x2462, 0x2463, 0x2464, 0x2465, 0x2466, 0x2467, 0x2468, 0x2469, 0x24FF, 0x2776, 0x2777, 0x2778, 0x2779,
        0x277A, 0x277B, 0x277C, 0x277D, 0x277E, 0x277F, 0x1F662, 0x1F660, 0x1F661, 0x1F663, 0x1F65E, 0x1F65C, 0x1F65D, 0x1F65F, 0x2219, 0x2022,
        0x2B1D, 0x2B58, 0x1F786, 0x1F788, 0x1F78A, 0x1F78B, 0x1F53F, 0x25AA, 0x1F79E, 0x1F7C1, 0x2605, 0x1F7CB, 0x1F7CF, 0x1F7D3, 0x1F7D1, 0x2BD0,
        0x2316, 0x2BCE, 0x2BCF, 0x2BD1, 0x272A, 0x2730, 0x1F550, 0x1F551, 0x1F552, 0x1F553, 0x1F554, 0x1F555, 0x1F556, 0x1F557, 0x1F558, 0x1F559,
        0x1F55A, 0x1F55B, 0x2BB0, 0x2BB1, 0x2BB2, 0x2BB3, 0x2BB4, 0x2BB5, 0x2BB6, 0x2BB7, 0x1F66A, 0x1F66B, 0x1F655, 0x1F654, 0x1F657, 0x1F656,
        0x1F650, 0x1F651, 0x1F652, 0x1F653, 0x232B, 0x2326, 0x2B98, 0x2B9A, 0x2B99, 0x2B9B, 0x2B88, 0x2B8A, 0x2B89, 0x2B8B, 0x1F868, 0x1F86A,
        0x1F869, 0x1F86B, 0x1F86C, 0x1F86D, 0x1F86F, 0x1F86E, 0x1F878, 0x1F87A, 0x1F879, 0x1F87B, 0x1F87C, 0x1F87D, 0x1F87F, 0x1F87E, 0x21E6, 0x21E8,
        0x21E7, 0x21E9, 0x2B04, 0x21F3, 0x2B01, 0x2B00, 0x2B03, 0x2B02, 0x1F8AC, 0x1F8AD, 0x1F5F6, 0x2714, 0x1F5F7, 0x1F5F9, 0, 0,
    ];
}
