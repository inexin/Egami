using Egami.Fonts.Shaping;
using Egami.Fonts.Subsetting;
using Egami.Fonts.Unicode;

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
    /// for them (<see cref="OpenTypeLayout.GetClosure"/> with <see cref="LayoutFeatures"/>), the dotted circle (U+25CC) the shaper
    /// puts before a mark that starts a run, and the larger variants and assembly parts MATH gives them; and GDEF, GSUB and GPOS are
    /// written cut down to the kept glyphs. They keep every script and language system, the features of
    /// <see cref="LayoutFeatures"/>, the lookups those features (and their contextual rules) name that still apply to a kept glyph,
    /// and in each lookup only the entries for kept glyphs, with glyph ids, classes and lookup indexes renumbered. Defaults to
    /// false, as a PDF needs only the glyphs drawn. Needs sfnt output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MATH is cut down to the kept glyphs and renumbered the same way, the legacy kern table keeps the format 0 pairs of kept
    /// glyphs (its other formats only with <see cref="RetainGlyphIds"/>), and vhea, vmtx and VORG keep the kept glyphs' vertical
    /// metrics. With <see cref="RetainGlyphIds"/> the tables are cut down without renumbering.
    /// </para>
    /// <para>
    /// A variable face's subset is static, so its layout is baked in at the position written: GPOS values and anchors, GDEF caret
    /// positions and MATH values take their rounded variation deltas, the feature variations that hold there replace their
    /// features, and vertical advances and origins are those of the position.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset-layout"/>
    /// </example>
    public bool LayoutClosure { get; init; }

    /// <summary>
    /// Gets the layout features a <see cref="LayoutClosure"/> subset keeps, or <see langword="null"/> (the default) for
    /// <see cref="OpenTypeLayout.DefaultClosureFeatures"/>. The glyph closure follows the same features. To keep more, such as
    /// stylistic sets or old-style figures, pass the default list with the extra tags.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset-layout-features"/>
    /// </example>
    public IReadOnlyList<FontTag>? LayoutFeatures { get; init; }

    /// <summary>
    /// Gets the characters the subset is for, as passed to <see cref="FontSubsetter.GlyphsFor"/>, or <see langword="null"/> (the
    /// default). When set, the character map holds only them and the characters the shaper draws in their place, as
    /// <see cref="FontSubsetter.GlyphsFor"/> finds them; otherwise it maps every character whose glyph the subset keeps, such as the
    /// presentation forms and ligature characters of glyphs that only a layout closure reached.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset-characters"/>
    /// </example>
    public IReadOnlyCollection<int>? Characters { get; init; }

    /// <summary>Gets whether every GSUB and GPOS lookup is written as an extension lookup, as when 16-bit offsets overflow (for tests).</summary>
    internal bool ExtensionLookups { get; init; }

    /// <summary>
    /// Gets whether TrueType hinting is left out: the fpgm, prep and cvt tables and every glyph's instructions, with maxp's
    /// hinting limits set to none, so the subset is smaller and renderers rasterize it unhinted (gasp is kept). Defaults to false.
    /// TrueType only: the hints of CFF and CFF2 charstrings are kept.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset-unhinted"/>
    /// </example>
    public bool DropHinting { get; init; }
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
    /// <summary>
    /// Gets the glyphs that draw characters as the shaper does: the glyph of each one the face maps; for one the face lacks, the
    /// glyphs of its canonical decomposition when the face draws every part (the shaper draws a missing "é" as "e" and a combining
    /// acute); and the glyph of each precomposed character the face maps whose decomposition the characters hold (the shaper
    /// composes "e" and a combining acute to "é"). The shaper's other stand-ins are added too: the decompositions that Indic,
    /// Khmer, Myanmar and Universal Shaping Engine scripts always make (split vowels, Thai and Lao sara am), mirrored brackets for
    /// right-to-left text, vertical forms when the face has no vert feature, the space for default-ignorable characters and for
    /// spaces the face lacks, U+2010 for a missing U+2011, the Arabic presentation forms when the face has no Arabic GSUB, and
    /// the glyphs cmap format 14 gives the variation sequences the characters can make. Characters the face cannot draw add
    /// nothing; .notdef is not included.
    /// </summary>
    /// <param name="face">The face.</param>
    /// <param name="codePoints">The characters, as Unicode code points; duplicates are fine.</param>
    /// <returns>The glyphs, in ascending order, ready for <see cref="Subset"/>.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="subset-text"/>
    /// </example>
    public static IReadOnlyList<ushort> GlyphsFor(Typeface face, IEnumerable<int> codePoints)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(codePoints);
        HashSet<int> codes = CharactersFor(face, codePoints);
        var glyphs = new SortedSet<ushort>();
        foreach (int code in codes)
        {
            if (face.TryGetGlyph(code, out ushort glyph) && glyph != 0)
            {
                glyphs.Add(glyph);
            }
        }

        // The glyphs cmap format 14 gives a character followed by a variation selector the characters hold.
        if (face.Cmap.Variations is { } variations)
        {
            foreach (int selector in variations.Records.Select(r => r.Selector).Where(codes.Contains))
            {
                foreach (int code in codes)
                {
                    int glyph = face.Cmap.MapVariation(code, selector);
                    if (glyph > 0 && glyph < face.GlyphCount)
                    {
                        glyphs.Add((ushort)glyph);
                    }
                }
            }
        }

        return [.. glyphs];
    }

    /// <summary>The characters <see cref="GlyphsFor"/> takes the glyphs of: the given ones with the shaper's stand-ins for them.</summary>
    internal static HashSet<int> CharactersFor(Typeface face, IEnumerable<int> codePoints)
    {
        var codes = new HashSet<int>();
        var pending = new Queue<int>(codePoints);
        while (pending.TryDequeue(out int code))
        {
            // Decompose to a fixed point where the shaper decomposes characters the face maps: U+0DDD to U+0DDC and U+0DCA, and
            // U+0DDC in turn.
            List<int> parts = [];
            if (codes.Add(code) && (!Maps(face, code) || AlwaysDecomposes(code)) && Decomposes(face, code, parts))
            {
                foreach (int part in parts)
                {
                    pending.Enqueue(part);
                }
            }
        }

        AddStandIns(face, codes);

        // Compose to a fixed point: a two-step composition such as U+0390 needs U+03CA, itself a composition.
        ReadOnlySpan<int> table = UnicodeTables.Compositions;
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int i = 0; i + 2 < table.Length; i += 3)
            {
                if (!codes.Contains(table[i + 2]) && codes.Contains(table[i]) && codes.Contains(table[i + 1]) && face.TryGetGlyph(table[i + 2], out ushort composed) && composed != 0)
                {
                    codes.Add(table[i + 2]);
                    grew = true;
                }
            }
        }

        return codes;
    }

    // The scripts whose shapers decompose every character they can before shaping (no short circuit for mapped characters).
    private static bool AlwaysDecomposes(int code) =>
        ShaperSelector.Select(UnicodeData.GetScript(code), LayoutDirection.LeftToRight, 0).NormalizationPreference ==
        NormalizationMode.ComposedDiacriticsNoShortCircuit;

    // The characters the shaper draws in place of others: see GlyphsFor.
    private static void AddStandIns(Typeface face, HashSet<int> codes)
    {
        List<int> added = [];
        bool arabicFallback = false;
        bool verticalForms = !face.Layout.Features.Contains(new FontTag("vert"));
        foreach (int code in codes)
        {

            if (UnicodeData.IsDefaultIgnorable(code) ||
                (UnicodeProps.SpaceFallbackType(code) != UnicodeProps.NotSpace && !Maps(face, code)))
            {
                added.Add(0x0020);
            }

            if (code == 0x2011 && !Maps(face, code))
            {
                added.Add(0x2010);
            }

            if (UnicodeData.TryGetBidiMirroringGlyph(code, out int mirror))
            {
                added.Add(mirror);
            }

            if ((code & ~0x80) == 0x0E33)
            {
                // Thai and Lao sara am: nikhahit (niggahita) and sara aa.
                added.Add(code - 0x0E33 + 0x0E4D);
                added.Add(code - 1);
            }

            if (code is 0x17BE or 0x17BF or 0x17C0 or 0x17C4 or 0x17C5)
            {
                // Khmer split vowels: the shaper adds the pre-base part.
                added.Add(0x17C1);
            }

            arabicFallback |= code is >= ArabicTables.ShapingTableFirst and <= ArabicTables.ShapingTableLast;
        }

        codes.UnionWith(added);
        if (verticalForms)
        {
            // Of the mirrored brackets too: bottom-to-top text mirrors them.
            codes.UnionWith([.. codes.Select(OtShaper.VerticalCharFor)]);
        }

        if (arabicFallback && !HasArabicGsub(face))
        {
            AddArabicPresentationForms(codes);
        }
    }

    // As the Arabic shaper, which shapes with presentation forms only when the script it picks has none of the Arabic joining
    // features: the script 'arab', or the one the shaper falls back to.
    private static bool HasArabicGsub(Typeface face)
    {
        OpenTypeLayout layout = face.Layout;
        if (!layout.HasGsub)
        {
            return false;
        }

        foreach (FontTag script in s_arabicScripts)
        {
            if (layout.Scripts.Contains(script))
            {
                IReadOnlyList<FontTag> features = layout.GetFeatures(script);
                return s_joiningFeatures.Any(features.Contains);
            }
        }

        return false;
    }

    private static readonly FontTag[] s_arabicScripts = [new("arab"), new("DFLT"), new("dflt"), new("latn")];

    private static readonly FontTag[] s_joiningFeatures = [new("isol"), new("fina"), new("medi"), new("init")];

    // As the Arabic shaper's fallback shaping: each character's presentation forms, and the ligatures of forms the text holds.
    private static void AddArabicPresentationForms(HashSet<int> codes)
    {
        ReadOnlySpan<ushort> table = ArabicTables.ShapingTable;
        foreach (int code in codes.ToArray())
        {
            if (code is >= ArabicTables.ShapingTableFirst and <= ArabicTables.ShapingTableLast)
            {
                foreach (ushort form in table.Slice((code - ArabicTables.ShapingTableFirst) * 4, 4))
                {
                    if (form != 0)
                    {
                        codes.Add(form);
                    }
                }
            }
        }

        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var ligatures in (ReadOnlySpan<(ushort First, (ushort[] Components, ushort Ligature)[] Ligatures)[]>)[ArabicTables.Ligatures, ArabicTables.Ligatures3, ArabicTables.MarkLigatures])
            {
                foreach ((ushort first, (ushort[] Components, ushort Ligature)[] list) in ligatures)
                {
                    if (!codes.Contains(first))
                    {
                        continue;
                    }

                    foreach ((ushort[] components, ushort ligature) in list)
                    {
                        if (!codes.Contains(ligature) && components.All(c => codes.Contains(c)))
                        {
                            codes.Add(ligature);
                            grew = true;
                        }
                    }
                }
            }
        }
    }

    private static bool Maps(Typeface face, int code) => face.TryGetGlyph(code, out ushort glyph) && glyph != 0;

    // As the shaper decomposes: only when the mark maps and the base maps or decomposes in turn, else the character stays .notdef.
    private static bool Decomposes(Typeface face, int code, List<int> parts)
    {
        if (!NormalizeContext.DecomposeUnicode(code, out int a, out int b) || (b != 0 && !Maps(face, b)))
        {
            return false;
        }

        if (!Maps(face, a) && !Decomposes(face, a, parts))
        {
            return false;
        }

        parts.Add(a);
        if (b != 0)
        {
            parts.Add(b);
        }

        return true;
    }

    /// <summary>Writes a subset of a face.</summary>
    /// <param name="face">The face.</param>
    /// <param name="glyphs">The glyphs to keep; duplicates are fine.</param>
    /// <param name="options">How to write it, or <see langword="null"/> for <see cref="SubsetOptions.Default"/>.</param>
    /// <returns>The subset with its glyph map.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A glyph id is past the face's glyph count.</exception>
    /// <exception cref="ArgumentException">
    /// The format needs outlines of the other kind, such as CFF output from a TrueType face; or <see cref="SubsetOptions.LayoutClosure"/>
    /// is set with bare CFF output.
    /// </exception>
    /// <exception cref="CorruptDataException">
    /// A kept glyph's data cannot be read; or, with <see cref="SubsetOptions.LayoutClosure"/>, a GDEF, GSUB or GPOS table is damaged.
    /// </exception>
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
            throw new ArgumentException("A face without outlines (bitmap-only, or without glyph data) has none to subset.", nameof(face));
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
            // The shaper puts a dotted circle before a mark that starts a run, so the subset keeps it to shape as the face does.
            if (face.TryGetGlyph(0x25CC, out ushort dottedCircle))
            {
                wanted.Add(dottedCircle);
            }

            IReadOnlyList<FontTag> features = options.LayoutFeatures ?? OpenTypeLayout.DefaultClosureFeatures;
            wanted.UnionWith(face.Layout.GetClosure(wanted, features));
            if (face.Math is { } math && AddMathGlyphs(math, wanted, face.GlyphCount))
            {
                wanted.UnionWith(face.Layout.GetClosure(wanted, features));
            }
        }

        return format == SubsetFormat.TrueType
            ? TrueTypeSubsetter.Subset(face, wanted, options)
            : CffSubsetter.Subset(face, wanted, options, format);
    }

    /// <summary>Adds the larger variants and assembly parts MATH gives the kept glyphs; true when any were added.</summary>
    private static bool AddMathGlyphs(TypefaceMath math, SortedSet<ushort> wanted, int glyphCount)
    {
        var added = new List<ushort>();
        foreach (ushort glyph in wanted)
        {
            foreach (MathDirection direction in (ReadOnlySpan<MathDirection>)[MathDirection.Vertical, MathDirection.Horizontal])
            {
                foreach (MathGlyphVariant variant in math.GetVariants(glyph, direction))
                {
                    added.Add(variant.Glyph);
                }

                if (math.GetAssembly(glyph, direction) is { } assembly)
                {
                    foreach (MathGlyphPart part in assembly.Parts)
                    {
                        added.Add(part.Glyph);
                    }
                }
            }
        }

        int before = wanted.Count;
        wanted.UnionWith(added.Where(g => g < glyphCount));
        return wanted.Count > before;
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
