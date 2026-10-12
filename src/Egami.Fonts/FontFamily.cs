using Egami.Fonts.Matching;

namespace Egami.Fonts;

/// <summary>The faces of one family in a <see cref="FontCollection"/>, matched by style.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
/// </example>
public sealed class FontFamily
{
    private readonly FaceEntry[] _entries;
    private readonly FontLoadOptions _options;

    internal FontFamily(string name, FaceEntry[] entries, FontLoadOptions options)
    {
        Name = name;
        _entries = entries;
        _options = options;
    }

    /// <summary>Gets the family's English name.</summary>
    public string Name { get; }

    /// <summary>Gets the styles of the family's faces, without loading them.</summary>
    public IReadOnlyList<FontStyle> Styles => [.. _entries.Select(e => e.Style)];

    /// <summary>Gets the faces, loading every one that is not loaded yet.</summary>
    public IReadOnlyList<Typeface> Faces => [.. _entries.Select(e => e.GetTypeface(_options))];

    /// <summary>
    /// Returns the face that best matches a style by the CSS Fonts Level 4 steps (nearest width, then slant, then weight), and the
    /// bold or italic it lacks. Only the matched face is loaded.
    /// </summary>
    /// <param name="style">The style wanted.</param>
    /// <returns>The face and its synthesis.</returns>
    public FontMatch Match(FontStyle style)
    {
        int best = StyleMatcher.Match(_entries.Select(e => e.Style).ToArray(), style);
        FaceEntry entry = _entries[Math.Max(0, best)];
        return new FontMatch(entry.GetTypeface(_options), StyleMatcher.SynthesisFor(style, entry.Style));
    }

    /// <summary>Returns the family's name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}
