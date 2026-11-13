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
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="auto-hyphens"/>
/// </example>
public sealed class HyphenationDictionaries
{
    private readonly Dictionary<string, Hyphenator> _byLanguage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or replaces the hyphenator for a language.</summary>
    /// <param name="language">A BCP 47 tag, such as <c>en-US</c> or <c>nb</c>.</param>
    /// <param name="hyphenator">The hyphenator.</param>
    public void Add(string language, Hyphenator hyphenator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(hyphenator);
        _byLanguage[language.Trim().Replace('_', '-')] = hyphenator;
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
            if (_byLanguage.TryGetValue(tag, out hyphenator))
            {
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
}
