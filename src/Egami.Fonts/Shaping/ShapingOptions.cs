using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// How <see cref="TextShaper.Shape(string, Font, ShapingOptions?)"/> shapes text: the paragraph direction, the language, script and
/// features of the whole text, the fonts that take characters the font lacks, the cluster level, and styled spans.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="shape"/>
/// </example>
public sealed record class ShapingOptions
{
    /// <summary>Gets the default options: direction from the text, no language, the script from the text, the font's default
    /// features, no fallback beyond the font, monotone grapheme clusters.</summary>
    public static ShapingOptions Default { get; } = new();

    /// <summary>Gets the paragraph direction; <see cref="TextDirection.Auto"/> takes it from the first strong character of each
    /// paragraph, and <see cref="TextDirection.TopToBottom"/> shapes each line as one vertical column.</summary>
    public TextDirection Direction { get; init; }

    /// <summary>Gets the BCP 47 language tag of the text, such as <c>ur</c> or <c>sr-Latn</c>, or null for the fonts' default forms
    /// (never the machine's culture).</summary>
    public string? Language { get; init; }

    /// <summary>Gets the script every run is shaped as, or null to itemize the text into script runs.</summary>
    public Script? Script { get; init; }

    /// <summary>Gets the feature settings applied to the whole text, in order; a span's features follow them.</summary>
    public IReadOnlyList<FontFeature>? Features { get; init; }

    /// <summary>Gets where characters the font does not map go: <see cref="FontFallback.None"/> (the default) keeps them in the
    /// font, as .notdef.</summary>
    public FontFallback Fallback { get; init; } = FontFallback.None;

    /// <summary>Gets how characters group into clusters.</summary>
    public ClusterLevel ClusterLevel { get; init; }

    /// <summary>Gets whether glyphs say where a tatweel can lengthen a word (<see cref="GlyphFlags.SafeToInsertTatweel"/>), as
    /// HarfBuzz's <c>HB_BUFFER_FLAG_PRODUCE_SAFE_TO_INSERT_TATWEEL</c>.</summary>
    public bool ProduceSafeToInsertTatweel { get; init; }

    /// <summary>Gets styled ranges of the text, each overriding the font, language or features for its range; they must not
    /// overlap.</summary>
    public IReadOnlyList<TextSpan>? Spans { get; init; }
}

/// <summary>A range of text with its own font, language or features, for <see cref="ShapingOptions.Spans"/>.</summary>
/// <param name="Start">The index of the span's first UTF-16 unit.</param>
/// <param name="Length">The number of UTF-16 units.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="spans"/>
/// </example>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>Gets the span's font, or null for the font the text is shaped with.</summary>
    public Font? Font { get; init; }

    /// <summary>Gets the span's BCP 47 language tag, or null for the options' language.</summary>
    public string? Language { get; init; }

    /// <summary>Gets feature settings applied after the options' features, or null.</summary>
    public IReadOnlyList<FontFeature>? Features { get; init; }
}
