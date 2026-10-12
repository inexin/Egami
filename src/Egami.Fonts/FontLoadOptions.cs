namespace Egami.Fonts;

/// <summary>How a font file is read: leniently with diagnostics (the default), or strictly.</summary>
/// <remarks>
/// Lenient loading needs only the tables an operation uses: glyph data ('glyf' and 'loca', or CFF) to load at all, and nothing
/// else. A missing or damaged name, OS/2, hhea, hmtx, post or cmap table gets a documented default and a Warning diagnostic with the
/// id <c>Font.MissingTable</c> or <c>Font.BadTable</c>; a glyph that cannot be read draws empty with <c>Font.BadGlyph</c>.
/// <see cref="Strict"/> turns the first such problem into a <see cref="CorruptDataException"/>.
/// </remarks>
public sealed record FontLoadOptions
{
    /// <summary>Gets the default options: lenient, in <see cref="EgamiContext.Default"/>.</summary>
    public static FontLoadOptions Default { get; } = new();

    /// <summary>Gets whether any damage throws <see cref="CorruptDataException"/> instead of being repaired. Defaults to false.</summary>
    public bool Strict { get; init; }

    /// <summary>
    /// Gets the context whose diagnostics receive repairs and whose <see cref="SafetyLimits.MaxTotalBytes"/> caps the file size, or
    /// <see langword="null"/> for <see cref="EgamiContext.Default"/> at the time of each use.
    /// </summary>
    public EgamiContext? Context { get; init; }

    internal EgamiContext EffectiveContext => Context ?? EgamiContext.Default;
}

/// <summary>What a bare CFF stream lacks and an sfnt would supply, for <see cref="Typeface.FromCff(ReadOnlyMemory{byte}, CffOptions?)"/>.</summary>
public sealed record CffOptions
{
    /// <summary>Gets the default options: units per em from the FontMatrix and widths from the charstrings.</summary>
    public static CffOptions Default { get; } = new();

    /// <summary>
    /// Gets the units per em, or <see langword="null"/> to take it from the CFF FontMatrix (1000 when the matrix is absent).
    /// </summary>
    public int? UnitsPerEm { get; init; }

    /// <summary>
    /// Gets the advance widths by glyph id in font units, such as the widths a PDF font dictionary gives, or <see langword="null"/>
    /// to take them from the charstrings. Glyphs past the end use their charstring width.
    /// </summary>
    public IReadOnlyList<float>? Widths { get; init; }

    /// <summary>Gets the options for loading leniently or strictly.</summary>
    public FontLoadOptions LoadOptions { get; init; } = FontLoadOptions.Default;
}
