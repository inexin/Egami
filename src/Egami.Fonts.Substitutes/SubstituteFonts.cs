using System.Collections.Concurrent;

namespace Egami.Fonts.Substitutes;

/// <summary>
/// Open fonts that stand in for the fonts documents name most without embedding them, each metric-compatible with the
/// font it replaces, so text keeps its line breaks: Liberation Sans for Helvetica and Arial, Liberation Serif for Times and
/// Times New Roman, Liberation Mono for Courier and Courier New, Carlito for Calibri and Caladea for Cambria, each in
/// regular, bold, italic and bold italic. All are under the SIL Open Font License 1.1 and ship inside this assembly, so they
/// work whatever fonts are installed; a face is loaded the first time it is asked for.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="substitutes"/>
/// </example>
public static class SubstituteFonts
{
    private static readonly string[] Families = ["LiberationSans", "LiberationSerif", "LiberationMono", "Carlito", "Caladea"];
    private static readonly string[] Styles = ["Regular", "Bold", "Italic", "BoldItalic"];
    private static readonly ConcurrentDictionary<string, Typeface> Loaded = new(StringComparer.Ordinal);
    private static readonly Lazy<FontCollection> All = new(() =>
    {
        FontCollection collection = new();
        foreach (string family in Families)
        {
            foreach (string style in Styles)
            {
                collection.Add(Load(family + "-" + style));
            }
        }

        return collection;
    });

    /// <summary>The five families, four styles each, as an Egami font collection, for matching by family name and style or for text layout.</summary>
    public static FontCollection Collection => All.Value;

    /// <summary>
    /// The face that stands in for a font: by its family name or a common alias, spaces, hyphens and case ignored
    /// (<c>Helvetica</c>, <c>Arial</c>, <c>ArialMT</c>, <c>Times-Roman</c>, <c>TimesNewRoman</c>, <c>Courier New</c>,
    /// <c>Calibri</c>, <c>Cambria</c>, or the Liberation, Carlito and Caladea names themselves), in the style nearest to
    /// <paramref name="style"/>: bold from weight 600, italic for any slant. <see langword="null"/> for any other name.
    /// </summary>
    /// <param name="familyName">The family name or alias.</param>
    /// <param name="style">The style wanted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="familyName"/> is null.</exception>
    public static Typeface? Find(string familyName, FontStyle style)
    {
        ArgumentNullException.ThrowIfNull(familyName);
        string key = new([.. familyName.Where(char.IsLetter).Select(char.ToLowerInvariant)]);
        string? family = key switch
        {
            _ when key.StartsWith("helvetica", StringComparison.Ordinal) || key.StartsWith("arial", StringComparison.Ordinal) || key.StartsWith("liberationsans", StringComparison.Ordinal) => "LiberationSans",
            _ when key.StartsWith("times", StringComparison.Ordinal) || key.StartsWith("liberationserif", StringComparison.Ordinal) => "LiberationSerif",
            _ when key.StartsWith("courier", StringComparison.Ordinal) || key.StartsWith("liberationmono", StringComparison.Ordinal) => "LiberationMono",
            _ when key.StartsWith("calibri", StringComparison.Ordinal) || key.StartsWith("carlito", StringComparison.Ordinal) => "Carlito",
            _ when key.StartsWith("cambria", StringComparison.Ordinal) || key.StartsWith("caladea", StringComparison.Ordinal) => "Caladea",
            _ => null,
        };
        if (family is null)
        {
            return null;
        }

        bool bold = style.Weight >= 600, italic = style.Slant != FontSlant.Upright;
        return Load(family + "-" + (bold, italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        });
    }

    private static Typeface Load(string name)
    {
        return Loaded.GetOrAdd(name, n =>
        {
            using Stream stream = typeof(SubstituteFonts).Assembly.GetManifestResourceStream($"Egami.Fonts.Substitutes.{n}.ttf")
                ?? throw new InvalidOperationException($"The font {n} is missing from Egami.Fonts.Substitutes.");
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            return Typeface.FromMemory(copy.ToArray());
        });
    }
}
