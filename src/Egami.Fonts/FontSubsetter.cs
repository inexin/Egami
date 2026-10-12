using Egami.Fonts.Shaping;
using Egami.Fonts.Subsetting;

namespace Egami.Fonts;

/// <summary>What <see cref="FontSubsetter"/> writes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset"/>
/// </example>
public enum SubsetFormat
{
    /// <summary>The input's own kind: TrueType for 'glyf' faces, OpenType CFF for CFF faces, bare CFF for a bare CFF face.</summary>
    Auto,

    /// <summary>A TrueType font (.ttf), as PDF embeds in FontFile2. Needs a face with TrueType outlines.</summary>
    TrueType,

    /// <summary>An OpenType font with a CFF table (.otf), as PDF embeds in FontFile3 with subtype OpenType. Needs CFF outlines.</summary>
    OpenTypeCff,

    /// <summary>A bare CID-keyed CFF stream with an identity charset (CID = new glyph id), as PDF embeds for CIDFontType0C. Needs CFF outlines.</summary>
    BareCff,
}

/// <summary>How <see cref="FontSubsetter.Subset(Typeface, IEnumerable{ushort}, SubsetOptions?)"/> writes a subset.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset"/>
/// </example>
public sealed record SubsetOptions
{
    /// <summary>Gets the default options: the input's format, dense renumbering, a character map, no extra tables, no name prefix.</summary>
    public static SubsetOptions Default { get; } = new();

    /// <summary>Gets the output format. Defaults to <see cref="SubsetFormat.Auto"/>.</summary>
    public SubsetFormat Format { get; init; }

    /// <summary>
    /// Gets whether glyphs keep their ids, with empty glyphs in the gaps, rather than being renumbered densely in ascending order.
    /// Defaults to false.
    /// </summary>
    public bool RetainGlyphIds { get; init; }

    /// <summary>Gets whether a character map for the kept glyphs is written (sfnt output only; PDF CIDFontType2 needs none). Defaults to true.</summary>
    public bool KeepCmap { get; init; } = true;

    /// <summary>Gets tables copied unchanged, such as 'GSUB'; the caller answers for glyph ids inside them.</summary>
    public IReadOnlyList<FontTag> KeepTables { get; init; } = [];

    /// <summary>Gets the prefix put before the PostScript and CFF font names, such as the six-letter PDF subset tag "ABCDEF+".</summary>
    public string? FontNamePrefix { get; init; }

    /// <summary>
    /// Gets the position a variable face's subset is written at, applied over the face's own position, or <see langword="null"/>
    /// (the default) for the face's position. A variable face's subset is always a static font: glyf with gvar's deltas applied and
    /// rounded, hmtx from the varied metrics, OS/2 and post with MVAR's values, CFF2 written as CFF with blends resolved, and the
    /// variation tables left out, so PDF viewers draw the position rather than the default instance.
    /// </summary>
    public IReadOnlyList<FontVariation>? Variations { get; init; }

    /// <summary>
    /// Gets whether the subset can be shaped again as the full face would be: the kept glyphs gain every glyph GSUB can substitute
    /// for them (<see cref="OpenTypeLayout.GetClosure"/> with <see cref="OpenTypeLayout.DefaultClosureFeatures"/>), and GSUB, GPOS
    /// and GDEF are copied unchanged. Copied layout tables hold the old glyph ids, so this needs <see cref="RetainGlyphIds"/> and
    /// sfnt output. Defaults to false, as a PDF needs only the glyphs drawn. To follow other features, pass the glyphs of
    /// <see cref="OpenTypeLayout.GetClosure"/> with them, and keep the three tables with <see cref="KeepTables"/>.
    /// </summary>
    /// <remarks>
    /// The tables of a variable face are copied with their variation data, so in the static subset GPOS positions and feature
    /// variations are those of the default instance, whatever <see cref="Variations"/> says.
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset-layout"/>
    /// </example>
    public bool LayoutClosure { get; init; }
}

/// <summary>A font holding only some glyphs of another, from <see cref="FontSubsetter"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset"/>
/// </example>
public sealed class FontSubset
{
    private readonly ushort[] _oldIds;
    private readonly Dictionary<ushort, ushort> _map;

    internal FontSubset(ReadOnlyMemory<byte> data, SubsetFormat format, ushort[] oldIds, Dictionary<ushort, ushort> map)
    {
        Data = data;
        Format = format;
        _oldIds = oldIds;
        _map = map;
    }

    /// <summary>Gets the subset's bytes: an sfnt, or a bare CFF stream.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the format written, never <see cref="SubsetFormat.Auto"/>.</summary>
    public SubsetFormat Format { get; }

    /// <summary>Gets each kept glyph's new id, by old id; glyphs pulled in by composites and seac accents are included.</summary>
    public IReadOnlyDictionary<ushort, ushort> GlyphMap => _map;

    /// <summary>Gets the old id of each new glyph id; gap glyphs of a <see cref="SubsetOptions.RetainGlyphIds"/> subset map to themselves.</summary>
    public IReadOnlyList<ushort> Glyphs => _oldIds;

    /// <summary>Loads the subset as a face.</summary>
    /// <returns>The face.</returns>
    public Typeface ToTypeface() => Format == SubsetFormat.BareCff ? Typeface.FromCff(Data) : Typeface.FromMemory(Data);
}

/// <summary>
/// Writes fonts that hold only the glyphs a document uses, as PDF embedding and SVG export want: TrueType, OpenType CFF or bare CFF,
/// with composite glyphs and seac accents closed over and unused CFF subroutines dropped.
/// </summary>
/// <remarks>
/// Glyph 0 (.notdef) is always kept. The subsetter reads <see cref="Typeface.Embedding"/> but never enforces it: the caller decides
/// whether a face may be subsetted and embedded.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset"/>
/// </example>
public static class FontSubsetter
{
    /// <summary>Writes a subset of a face.</summary>
    /// <param name="face">The face.</param>
    /// <param name="glyphs">The glyphs to keep; duplicates are fine.</param>
    /// <param name="options">How to write it, or <see langword="null"/> for <see cref="SubsetOptions.Default"/>.</param>
    /// <returns>The subset with its glyph map.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A glyph id is past the face's glyph count.</exception>
    /// <exception cref="ArgumentException">
    /// The format needs outlines of the other kind, such as CFF output from a TrueType face; or <see cref="SubsetOptions.LayoutClosure"/>
    /// is set without <see cref="SubsetOptions.RetainGlyphIds"/> or with bare CFF output.
    /// </exception>
    /// <exception cref="CorruptDataException">A kept glyph's data cannot be read.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset"/>
    /// </example>
    public static FontSubset Subset(Typeface face, IEnumerable<ushort> glyphs, SubsetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(glyphs);
        options ??= SubsetOptions.Default;
        if (options.Variations is { } variations && face.IsVariable)
        {
            face = face.WithVariations([.. variations]);
        }

        if (face.Outlines == FontOutlines.None)
        {
            throw new ArgumentException("A bitmap-only face has no outlines to subset.", nameof(face));
        }

        SubsetFormat format = options.Format;
        if (format == SubsetFormat.Auto)
        {
            format = face.Outlines == FontOutlines.TrueType ? SubsetFormat.TrueType : face.IsBareCff ? SubsetFormat.BareCff : SubsetFormat.OpenTypeCff;
        }

        if ((format == SubsetFormat.TrueType) != (face.Outlines == FontOutlines.TrueType))
        {
            throw new ArgumentException($"{format} output needs {(format == SubsetFormat.TrueType ? "TrueType" : "CFF")} outlines; the face has {face.Outlines}.", nameof(options));
        }

        if (face.IsBareCff && format == SubsetFormat.OpenTypeCff)
        {
            throw new ArgumentException("A bare CFF face has no sfnt tables to write OpenType output from; use BareCff.", nameof(options));
        }

        if (options.LayoutClosure && !options.RetainGlyphIds)
        {
            throw new ArgumentException("LayoutClosure copies the layout tables unchanged, so it needs RetainGlyphIds.", nameof(options));
        }

        if (options.LayoutClosure && format == SubsetFormat.BareCff)
        {
            throw new ArgumentException("LayoutClosure needs sfnt output to hold the layout tables; bare CFF has none.", nameof(options));
        }

        var wanted = new SortedSet<ushort> { 0 };
        foreach (ushort glyph in glyphs)
        {
            if (glyph >= face.GlyphCount)
            {
                throw new ArgumentOutOfRangeException(nameof(glyphs), glyph, $"The face has {face.GlyphCount} glyphs.");
            }

            wanted.Add(glyph);
        }

        if (options.LayoutClosure)
        {
            wanted.UnionWith(face.Layout.GetClosure(wanted));
        }

        return format == SubsetFormat.TrueType
            ? TrueTypeSubsetter.Subset(face, wanted, options)
            : CffSubsetter.Subset(face, wanted, options, format);
    }

    /// <summary>Gives the kept glyphs their new ids: dense in ascending old order, or the old ids with gaps.</summary>
    internal static (ushort[] OldIds, Dictionary<ushort, ushort> Map) Number(SortedSet<ushort> kept, bool retain)
    {
        var map = new Dictionary<ushort, ushort>(kept.Count);
        if (retain)
        {
            int count = kept.Max + 1;
            var old = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                old[i] = (ushort)i;
            }

            foreach (ushort g in kept)
            {
                map[g] = g;
            }

            return (old, map);
        }

        ushort[] ids = [.. kept];
        for (int i = 0; i < ids.Length; i++)
        {
            map[ids[i]] = (ushort)i;
        }

        return (ids, map);
    }
}
