using System.Diagnostics.CodeAnalysis;

namespace Egami.Fonts.Layout;

/// <summary>Whether words break with hyphens at line ends (CSS <c>hyphens</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="auto-hyphens"/>
/// </example>
public enum Hyphens
{
    /// <summary>Only at soft hyphens (U+00AD) in the text.</summary>
    Manual = 0,

    /// <summary>Never: soft hyphens are not break opportunities either.</summary>
    None = 1,

    /// <summary>At soft hyphens, and where <see cref="ParagraphStyle.Hyphenation"/>'s dictionary for the text's language allows;
    /// as <see cref="Manual"/> without a dictionary.</summary>
    Auto = 2,
}

/// <summary>
/// How <see cref="Hyphens.Auto"/> hyphenates: the dictionaries by language and the limits word processors and CSS put on
/// hyphenation.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="auto-hyphens"/>
/// </example>
public sealed record class HyphenationOptions
{
    private readonly int _minWordLength = 5;
    private readonly int? _minBefore;
    private readonly int? _minAfter;
    private readonly int _maxConsecutiveLines;
    private readonly float _zone;

    /// <summary>Gets the hyphenators by language; each run's <see cref="TextStyle.Language"/> picks one, and text whose language
    /// has none is not hyphenated, as CSS requires.</summary>
    public required HyphenationDictionaries Dictionaries { get; init; }

    /// <summary>Gets the fewest characters a word needs to be hyphenated (CSS <c>hyphenate-limit-chars</c>' first value); 5 by
    /// default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MinWordLength
    {
        get => _minWordLength;
        init => _minWordLength = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "At least 1.");
    }

    /// <summary>Gets the fewest characters kept before a hyphen, or null for the dictionary's own
    /// (<see cref="Hyphenator.MinBefore"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or more than 255.</exception>
    public int? MinBefore
    {
        get => _minBefore;
        init => _minBefore = value is null or (>= 1 and <= 255) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "From 1 to 255.");
    }

    /// <summary>Gets the fewest characters carried after a hyphen, or null for the dictionary's own
    /// (<see cref="Hyphenator.MinAfter"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or more than 255.</exception>
    public int? MinAfter
    {
        get => _minAfter;
        init => _minAfter = value is null or (>= 1 and <= 255) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "From 1 to 255.");
    }

    /// <summary>Gets the most lines in a row that may end with a hyphen, soft or automatic, before automatic hyphenation pauses
    /// for a line; 0 for no limit (Word's <c>consecutiveHyphenLimit</c>, CSS <c>hyphenate-limit-lines</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxConsecutiveLines
    {
        get => _maxConsecutiveLines;
        init => _maxConsecutiveLines = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "0 or more.");
    }

    /// <summary>
    /// Gets the hyphenation zone, in canvas units: a word is hyphenated only when the line without it would leave more than this
    /// much space at its end (Word's <c>hyphenationZone</c>, CSS <c>hyphenate-limit-zone</c>). Word applies it to justified
    /// lines too, and so does Egami. 0 by default: hyphenate whenever a piece of the word fits.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float Zone
    {
        get => _zone;
        init => _zone = float.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "0 or more.");
    }

    /// <summary>Gets whether words in capitals only are hyphenated; false is Word's <c>doNotHyphenateCaps</c>.</summary>
    public bool HyphenateCapitalized { get; init; } = true;
}

/// <summary>
/// Hyphenators by BCP 47 language tag. A lookup falls back a subtag at a time, as CSS matches languages: <c>de-CH-1901</c>,
/// then <c>de-CH</c>, then <c>de</c>. Tags compare without regard to case, and <c>_</c> counts as <c>-</c>. Add the hyphenators
/// before laying text out; reading from several threads is safe while nothing is added.
/// </summary>
/// <remarks>
/// <see cref="BuiltIn"/> holds the hyph-utf8 patterns Egami.Fonts ships, each loaded the first time a lookup finds it. To add
/// languages to them, copy them with <see cref="HyphenationDictionaries(HyphenationDictionaries)"/>.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="built-in-hyphenation"/>
/// </example>
public sealed class HyphenationDictionaries
{
    private const string ResourcePrefix = "Egami.Fonts.Hyphenation.";
    private const string ResourceSuffix = ".ehyp";

    private static readonly Lazy<HyphenationDictionaries> s_builtIn = new(CreateBuiltIn);

    private readonly Dictionary<string, Lazy<Hyphenator>> _byLanguage;
    private readonly Dictionary<string, string>? _aliases;
    private readonly bool _readOnly;

    /// <summary>Makes an empty set of hyphenators.</summary>
    public HyphenationDictionaries()
    {
        _byLanguage = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Makes a set that starts with <paramref name="source"/>'s hyphenators (sharing them, loaded or not) and
    /// fallbacks, to add to.</summary>
    /// <param name="source">The hyphenators to start with, such as <see cref="BuiltIn"/>.</param>
    public HyphenationDictionaries(HyphenationDictionaries source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _byLanguage = new(source._byLanguage, StringComparer.OrdinalIgnoreCase);
        _aliases = source._aliases;
    }

    private HyphenationDictionaries(Dictionary<string, Lazy<Hyphenator>> byLanguage, Dictionary<string, string> aliases)
    {
        _byLanguage = byLanguage;
        _aliases = aliases;
        _readOnly = true;
    }

    /// <summary>
    /// Gets the hyphenators Egami.Fonts ships, compiled from hyph-utf8's patterns under their permissive licences (listed in
    /// THIRD-PARTY-NOTICES.md): en-US, en-GB, de-1996, de-1901, de-CH-1901, fr, nb, nn, da, es, pt, gl, it, nl, eu, la, ga,
    /// cy, pl, sk, sl, hr, bg, uk, be, et, lt, sq, el-monoton, af, ka, hi, bn, ta and mn-Cyrl. Each loads the first time a
    /// lookup needs it. Besides the subtag fallback, <c>en</c> finds en-US, <c>de</c> de-1996, <c>no</c> nb, <c>el</c>
    /// el-monoton and <c>mn</c> mn-Cyrl. The set is shared and read-only.
    /// </summary>
    public static HyphenationDictionaries BuiltIn => s_builtIn.Value;

    /// <summary>Gets the language tags hyphenators were added for.</summary>
    public IReadOnlyCollection<string> Languages => _byLanguage.Keys;

    /// <summary>Gets whether <see cref="Add"/> throws: true for <see cref="BuiltIn"/>, which is shared.</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>Adds or replaces the hyphenator for a language.</summary>
    /// <param name="language">A BCP 47 tag, such as <c>en-US</c> or <c>nb</c>.</param>
    /// <param name="hyphenator">The hyphenator.</param>
    /// <exception cref="InvalidOperationException">The set is <see cref="IsReadOnly">read-only</see>.</exception>
    public void Add(string language, Hyphenator hyphenator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(hyphenator);
        if (_readOnly)
        {
            throw new InvalidOperationException(
                "The built-in hyphenators are shared; add to a copy: new HyphenationDictionaries(HyphenationDictionaries.BuiltIn).");
        }

        _byLanguage[language.Trim().Replace('_', '-')] = new Lazy<Hyphenator>(hyphenator);
    }

    /// <summary>Finds the hyphenator for a language, or for the nearest language it falls back to.</summary>
    /// <param name="language">A BCP 47 tag, or null.</param>
    /// <param name="hyphenator">The hyphenator found.</param>
    /// <returns>Whether one was found; never for a null or empty tag.</returns>
    public bool TryGet(string? language, [NotNullWhen(true)] out Hyphenator? hyphenator)
    {
        hyphenator = null;
        if (string.IsNullOrWhiteSpace(language) || _byLanguage.Count == 0)
        {
            return false;
        }

        string tag = language.Trim().Replace('_', '-');
        while (true)
        {
            if (_byLanguage.TryGetValue(tag, out Lazy<Hyphenator>? found)
                || (_aliases is not null && _aliases.TryGetValue(tag, out string? alias) && _byLanguage.TryGetValue(alias, out found)))
            {
                hyphenator = found.Value;
                return true;
            }

            int dash = tag.LastIndexOf('-');
            if (dash <= 0)
            {
                return false;
            }

            tag = tag[..dash];
        }
    }

    private static HyphenationDictionaries CreateBuiltIn()
    {
        System.Reflection.Assembly assembly = typeof(HyphenationDictionaries).Assembly;
        Dictionary<string, Lazy<Hyphenator>> byLanguage = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                string tag = name[ResourcePrefix.Length..^ResourceSuffix.Length];
                byLanguage[tag] = new(() => LoadBuiltIn(tag));
            }
        }

        Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "en-US",
            ["de"] = "de-1996",
            ["no"] = "nb",
            ["el"] = "el-monoton",
            ["mn"] = "mn-Cyrl",
        };
        return new HyphenationDictionaries(byLanguage, aliases);
    }

    /// <summary>Reads the built-in hyphenator of <paramref name="tag"/>, exactly as its resource is named.</summary>
    internal static Hyphenator LoadBuiltIn(string tag)
    {
        using Stream stream = typeof(HyphenationDictionaries).Assembly.GetManifestResourceStream(ResourcePrefix + tag + ResourceSuffix)
            ?? throw new ArgumentException($"No built-in hyphenation patterns for '{tag}'.", nameof(tag));
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        return Hyphenation.CompiledHyphenation.Read(data, trusted: true);
    }
}
