using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// Where characters the font does not map go: the caller's font families first, matched to the font's style, then a resolver.
/// Without fallback (<see cref="None"/>, the default) shaping depends only on the fonts passed, never on the machine.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="fallback"/>
/// </example>
public sealed class FontFallback
{
    private static readonly Lazy<FontFallback> s_system = new(() => new FontFallback([], SystemFontFallback.Instance));

    /// <summary>Initializes a fallback list.</summary>
    /// <param name="families">The families to try, in order, each matched to the style of the font being shaped.</param>
    /// <param name="resolver">The resolver tried after the families, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="families"/> is null or holds null.</exception>
    public FontFallback(IEnumerable<FontFamily> families, IFontFallbackResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(families);
        Families = [.. families];
        foreach (FontFamily family in Families)
        {
            ArgumentNullException.ThrowIfNull(family, nameof(families));
        }

        Resolver = resolver;
    }

    /// <summary>Gets no fallback: characters the font lacks become its .notdef glyph.</summary>
    public static FontFallback None { get; } = new([]);

    /// <summary>
    /// Gets fallback to the installed fonts (<see cref="SystemFontFallback"/>): fixed per-system, per-script family lists, then any
    /// installed face that maps the characters. Output then depends on the machine's fonts.
    /// </summary>
    public static FontFallback System => s_system.Value;

    /// <summary>Gets the families tried, in order.</summary>
    public IReadOnlyList<FontFamily> Families { get; }

    /// <summary>Gets the resolver tried after the families, or null.</summary>
    public IFontFallbackResolver? Resolver { get; }
}

/// <summary>Finds a face for a grapheme cluster no listed font maps, such as <see cref="SystemFontFallback"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="fallback"/>
/// </example>
public interface IFontFallbackResolver
{
    /// <summary>Finds a face that maps every character of a cluster.</summary>
    /// <param name="cluster">The grapheme cluster's text.</param>
    /// <param name="script">The script of the run the cluster is in.</param>
    /// <param name="language">The BCP 47 language tag of the cluster, or null.</param>
    /// <param name="style">The style of the font being shaped.</param>
    /// <param name="typeface">The face found.</param>
    /// <returns>Whether a face was found.</returns>
    bool TryResolve(ReadOnlySpan<char> cluster, Script script, string? language, FontStyle style, [NotNullWhen(true)] out Typeface? typeface);
}

/// <summary>
/// The installed-font resolver of <see cref="FontFallback.System"/>: fixed families for each script on Windows, macOS and Linux
/// (Segoe UI Emoji, Apple Color Emoji or Noto Color Emoji for emoji; the language picks Japanese, Chinese or Korean faces for Han),
/// then any installed face that maps the cluster, found once and remembered for the process.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="fallback"/>
/// </example>
public sealed class SystemFontFallback : IFontFallbackResolver
{
    private readonly FontCollection _collection;
    private readonly ConcurrentDictionary<(int CodePoint, FontStyle Style), Typeface?> _byCodePoint = new();
    private readonly ConcurrentDictionary<(string Family, FontStyle Style), Typeface?> _families = new();

    /// <summary>Initializes a resolver over a collection of fonts.</summary>
    /// <param name="collection">The fonts to resolve from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> is null.</exception>
    public SystemFontFallback(FontCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _collection = collection;
    }

    /// <summary>Gets the resolver over <see cref="FontCollection.System"/>.</summary>
    public static SystemFontFallback Instance { get; } = new(FontCollection.System);

    /// <inheritdoc/>
    public bool TryResolve(ReadOnlySpan<char> cluster, Script script, string? language, FontStyle style, [NotNullWhen(true)] out Typeface? typeface)
    {
        foreach (string family in FamiliesFor(script, language, TextItemizer.WantsEmoji(cluster)))
        {
            Typeface? face = _families.GetOrAdd((family, style), static (key, collection) => collection.MatchTypeface(key.Family, key.Style), _collection);
            if (face is not null && TextItemizer.Maps(face, cluster))
            {
                typeface = face;
                return true;
            }
        }

        // Any installed face that maps the cluster's first character and then the rest.
        int first = TextSegmenter.CodePointAt(cluster, 0, out _);
        Typeface? found = _byCodePoint.GetOrAdd((first, style), static (key, self) => self.Scan(key.CodePoint, key.Style), this);
        if (found is not null && TextItemizer.Maps(found, cluster))
        {
            typeface = found;
            return true;
        }

        typeface = null;
        return false;
    }

    private Typeface? Scan(int codePoint, FontStyle style)
    {
        foreach (FontFamily family in _collection.Families)
        {
            Typeface face = family.Match(style).Typeface;
            if (TextItemizer.MapsCodePoint(face, codePoint))
            {
                return face;
            }
        }

        return null;
    }

    private static IEnumerable<string> FamiliesFor(Script script, string? language, bool emoji)
    {
        string[] fonts = OperatingSystem.IsWindows() ? s_windows : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? s_mac : s_linux;
        int system = OperatingSystem.IsWindows() ? 0 : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? 1 : 2;
        if (emoji)
        {
            yield return s_emoji[system];
        }

        string code = script.Code;
        if (code == "Hani" || code == "Hira" || code == "Kana" || code == "Hang" || code == "Bopo")
        {
            foreach (string family in Cjk(system, code == "Hang" ? "ko" : code is "Hira" or "Kana" ? "ja" : language))
            {
                yield return family;
            }
        }

        foreach ((string scripts, string family) in s_byScript[system])
        {
            if (scripts.Contains(code, StringComparison.Ordinal))
            {
                yield return family;
            }
        }

        foreach (string family in fonts)
        {
            yield return family;
        }

        if (!emoji)
        {
            yield return s_emoji[system];
        }
    }

    private static string[] Cjk(int system, string? language)
    {
        string lang = language?.ToLowerInvariant() ?? "";
        bool ja = lang.StartsWith("ja", StringComparison.Ordinal), ko = lang.StartsWith("ko", StringComparison.Ordinal);
        bool traditional = lang.StartsWith("zh-hant", StringComparison.Ordinal) || lang is "zh-tw" or "zh-hk" or "zh-mo";
        return system switch
        {
            0 => ja ? ["Yu Gothic UI", "Meiryo", "Microsoft YaHei"] : ko ? ["Malgun Gothic", "Microsoft YaHei"] : traditional ? ["Microsoft JhengHei", "Microsoft YaHei"] : ["Microsoft YaHei", "Yu Gothic UI"],
            1 => ja ? ["Hiragino Sans", "PingFang SC"] : ko ? ["Apple SD Gothic Neo", "PingFang SC"] : traditional ? ["PingFang TC", "PingFang SC"] : ["PingFang SC", "Hiragino Sans"],
            _ => ja ? ["Noto Sans CJK JP", "Noto Sans JP"] : ko ? ["Noto Sans CJK KR", "Noto Sans KR"] : traditional ? ["Noto Sans CJK TC", "Noto Sans TC"] : ["Noto Sans CJK SC", "Noto Sans SC", "WenQuanYi Zen Hei"],
        };
    }

    private static readonly string[] s_emoji = ["Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji"];

    private static readonly string[] s_windows = ["Segoe UI", "Arial", "Times New Roman", "Segoe UI Symbol", "Segoe UI Historic", "Microsoft Sans Serif"];

    private static readonly string[] s_mac = ["Helvetica Neue", "Lucida Grande", "Arial Unicode MS", "Apple Symbols", "STIX Two Text"];

    private static readonly string[] s_linux = ["Noto Sans", "DejaVu Sans", "Liberation Sans", "Noto Sans Symbols", "Noto Sans Symbols 2", "FreeSans"];

    // Script codes (several in one string) and the family to try for them, per system.
    private static readonly (string Scripts, string Family)[][] s_byScript =
    [
        [
            ("Arab Syrc Thaa Nkoo", "Segoe UI"), ("Hebr", "Segoe UI"), ("Deva Beng Guru Gujr Orya Taml Telu Knda Mlym Sinh Olck", "Nirmala UI"),
            ("Thai Laoo Khmr Bugi", "Leelawadee UI"), ("Mymr", "Myanmar Text"), ("Ethi Vaii Tfng Adlm", "Ebrima"), ("Mong Phag Tale Yiii", "Mongolian Baiti"),
            ("Tibt", "Microsoft Himalaya"), ("Java", "Javanese Text"), ("Geor Armn", "Sylfaen"), ("Cher Cans", "Gadugi"),
        ],
        [
            ("Arab", "Geeza Pro"), ("Hebr", "Arial Hebrew"), ("Deva", "Kohinoor Devanagari"), ("Beng", "Kohinoor Bangla"), ("Guru", "Gurmukhi MN"),
            ("Gujr", "Gujarati Sangam MN"), ("Orya", "Oriya Sangam MN"), ("Taml", "Tamil Sangam MN"), ("Telu", "Kohinoor Telugu"), ("Knda", "Kannada Sangam MN"),
            ("Mlym", "Malayalam Sangam MN"), ("Sinh", "Sinhala Sangam MN"), ("Thai", "Thonburi"), ("Laoo", "Lao Sangam MN"), ("Khmr", "Khmer Sangam MN"),
            ("Mymr", "Myanmar Sangam MN"), ("Tibt", "Kailasa"), ("Ethi", "Kefa"), ("Geor", "Helvetica Neue"), ("Armn", "Mshtakan"), ("Syrc Thaa Nkoo", "Noto Sans Syriac"),
        ],
        [
            ("Arab", "Noto Sans Arabic"), ("Hebr", "Noto Sans Hebrew"), ("Deva", "Noto Sans Devanagari"), ("Beng", "Noto Sans Bengali"), ("Guru", "Noto Sans Gurmukhi"),
            ("Gujr", "Noto Sans Gujarati"), ("Orya", "Noto Sans Oriya"), ("Taml", "Noto Sans Tamil"), ("Telu", "Noto Sans Telugu"), ("Knda", "Noto Sans Kannada"),
            ("Mlym", "Noto Sans Malayalam"), ("Sinh", "Noto Sans Sinhala"), ("Thai", "Noto Sans Thai"), ("Laoo", "Noto Sans Lao"), ("Khmr", "Noto Sans Khmer"),
            ("Mymr", "Noto Sans Myanmar"), ("Tibt", "Noto Serif Tibetan"), ("Ethi", "Noto Sans Ethiopic"), ("Geor", "Noto Sans Georgian"), ("Armn", "Noto Sans Armenian"),
            ("Syrc", "Noto Sans Syriac"), ("Thaa", "Noto Sans Thaana"), ("Nkoo", "Noto Sans NKo"), ("Mong", "Noto Sans Mongolian"), ("Java", "Noto Sans Javanese"),
        ],
    ];
}
