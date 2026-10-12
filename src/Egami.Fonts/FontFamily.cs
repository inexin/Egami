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
    private readonly FontStyle[] _styles;

    internal FontFamily(string name, FaceEntry[] entries, FontLoadOptions options)
    {
        Name = name;
        _entries = entries;
        _options = options;
        _styles = [.. entries.Select(e => e.Style)];
    }

    /// <summary>Gets the family's English name.</summary>
    public string Name { get; }

    /// <summary>Gets the styles of the family's faces, without loading them.</summary>
    public IReadOnlyList<FontStyle> Styles => _styles.AsReadOnly();

    /// <summary>Gets the faces, loading every one that is not loaded yet.</summary>
    public IReadOnlyList<Typeface> Faces => [.. _entries.Select(e => e.GetTypeface(_options))];

    /// <summary>
    /// Returns the face that best matches a style by the CSS Fonts Level 4 steps (nearest width, then slant, then weight), and the
    /// bold or italic it lacks. Only the matched face is loaded. A variable face whose weight, width and slant or italic axes cover
    /// the request matches it exactly and comes back at that position; its named instances are matched as faces of their own.
    /// </summary>
    /// <param name="style">The style wanted.</param>
    /// <returns>The face and its synthesis, wanted only when no face or axis range reaches the request.</returns>
    public FontMatch Match(FontStyle style)
    {
        var effective = new FontStyle[_entries.Length];
        var variations = new FontVariation[_entries.Length][];
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Range is VariableRange range)
            {
                (effective[i], variations[i]) = range.Fit(style);
            }
            else
            {
                effective[i] = _entries[i].Style;
            }
        }

        int best = Math.Max(0, StyleMatcher.Match(effective, style));
        Typeface face = _entries[best].GetTypeface(_options);
        if (variations[best] is FontVariation[] at)
        {
            face = face.WithVariations(at);
        }

        return new FontMatch(face, StyleMatcher.SynthesisFor(style, effective[best]));
    }

    /// <summary>Returns the family's name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}
