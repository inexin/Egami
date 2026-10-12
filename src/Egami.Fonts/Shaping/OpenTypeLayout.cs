namespace Egami.Fonts.Shaping;

/// <summary>
/// What a face's OpenType layout tables (GSUB and GPOS) offer: the scripts, language systems and features they list, and the
/// glyphs GSUB can produce from a set of glyphs. A table whose structure is damaged is left out whole, as the shaper leaves it
/// out.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="layout"/>
/// </example>
public sealed class OpenTypeLayout
{
    private readonly OpenTypeLayoutTables _tables;

    internal OpenTypeLayout(OpenTypeLayoutTables tables) => _tables = tables;

    /// <summary>Gets whether the face has a usable GSUB table (substitutions such as ligatures and contextual forms).</summary>
    public bool HasGsub => _tables.Gsub.HasData;

    /// <summary>Gets whether the face has a usable GPOS table (positioning such as kerning and mark placement).</summary>
    public bool HasGpos => _tables.Gpos.HasData;

    /// <summary>Gets whether the face has a usable GDEF table with glyph classes.</summary>
    public bool HasGlyphClasses => _tables.Gdef.HasGlyphClasses;

    /// <summary>Gets the script tags GSUB and GPOS list, such as <c>latn</c>, <c>arab</c> or <c>DFLT</c>, in tag order.</summary>
    public IReadOnlyList<FontTag> Scripts
    {
        get
        {
            SortedSet<uint> tags = [];
            foreach (LayoutTable table in Tables)
            {
                for (int i = 0; i < table.ScriptCount; i++)
                {
                    tags.Add(table.GetScriptTag(i));
                }
            }

            return [.. tags.Select(t => new FontTag(t))];
        }
    }

    /// <summary>Gets the language system tags a script lists, such as <c>URD</c> or <c>SRB</c>; the default system is not listed.</summary>
    /// <param name="script">The script tag.</param>
    /// <returns>The tags, in tag order; empty when no table lists the script.</returns>
    public IReadOnlyList<FontTag> GetLanguageSystems(FontTag script)
    {
        SortedSet<uint> tags = [];
        foreach (LayoutTable table in Tables)
        {
            int scriptIndex = table.FindScript(script.Value);
            int scriptOffset = table.GetScript(scriptIndex);
            if (scriptOffset == 0)
            {
                continue;
            }

            ReadOnlySpan<byte> d = table.Data.Span;
            int count = OtData.U16(d, scriptOffset + 2);
            for (int i = 0; i < count; i++)
            {
                tags.Add(OtData.Tag(d, scriptOffset + 4 + (i * 6)));
            }
        }

        return [.. tags.Select(t => new FontTag(t))];
    }

    /// <summary>Gets the feature tags a language system lists, including its required feature, in tag order.</summary>
    /// <param name="script">The script tag.</param>
    /// <param name="language">The language system tag, or null for the script's default system.</param>
    /// <returns>The tags; empty when no table lists the script.</returns>
    public IReadOnlyList<FontTag> GetFeatures(FontTag script, FontTag? language = null)
    {
        SortedSet<uint> tags = [];
        foreach (LayoutTable table in Tables)
        {
            int scriptIndex = table.FindScript(script.Value);
            if (scriptIndex < 0)
            {
                continue;
            }

            int languageIndex = language is FontTag l ? table.FindLanguage(scriptIndex, l.Value) : -1;
            if (language is not null && languageIndex < 0)
            {
                continue;
            }

            int langSys = table.GetLangSys(scriptIndex, languageIndex);
            int required = table.GetRequiredFeature(langSys);
            if (required >= 0)
            {
                tags.Add(table.GetFeatureTag(required));
            }

            foreach (int index in table.GetLangSysFeatures(langSys))
            {
                tags.Add(table.GetFeatureTag(index));
            }
        }

        return [.. tags.Where(t => t != 0).Select(t => new FontTag(t))];
    }

    /// <summary>
    /// Gets the layout features hb-subset keeps by default, which <see cref="GetClosure"/> follows when no features are given: the
    /// common ones (such as <c>ccmp</c>, <c>liga</c>, <c>locl</c>, <c>calt</c> and <c>kern</c>), fractions, vertical forms,
    /// East Asian spacing, and those the Arabic, Hangul, Tibetan and Indic shapers apply; not discretionary ones such as
    /// <c>smcp</c>, <c>dlig</c> or <c>ss01</c>.
    /// </summary>
    public static IReadOnlyList<FontTag> DefaultClosureFeatures { get; } = [.. GsubClosure.DefaultFeatures.Select(t => new FontTag(t))];

    /// <summary>Gets every feature tag GSUB and GPOS list, in tag order.</summary>
    public IReadOnlyList<FontTag> Features
    {
        get
        {
            SortedSet<uint> tags = [];
            foreach (LayoutTable table in Tables)
            {
                for (int i = 0; i < table.FeatureCount; i++)
                {
                    tags.Add(table.GetFeatureTag(i));
                }
            }

            return [.. tags.Where(t => t != 0).Select(t => new FontTag(t))];
        }
    }

    /// <summary>
    /// Gets the glyphs the face's GSUB substitutions can produce from <paramref name="glyphs"/>: the glyphs themselves and every
    /// glyph a lookup of the given features (in any script and language system, with the lookups their contextual rules call
    /// and their feature variations) can substitute for them, repeated until nothing is added. This is the layout closure
    /// hb-subset computes, so a subset that keeps these glyphs can be shaped again as the full face would be.
    /// </summary>
    /// <param name="glyphs">The glyph ids to start from, such as those of the characters a page uses; ids the face does not have are ignored.</param>
    /// <param name="features">
    /// The feature tags whose lookups are followed, or null for <see cref="DefaultClosureFeatures"/>; pass <see cref="Features"/>
    /// to follow every feature and an empty list to follow none.
    /// </param>
    /// <returns>The glyph ids, ascending when enumerated.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="closure"/>
    /// </example>
    public IReadOnlySet<ushort> GetClosure(IEnumerable<ushort> glyphs, IEnumerable<FontTag>? features = null)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        GlyphBitSet set = new(_tables.GlyphCount);
        foreach (ushort glyph in glyphs)
        {
            set.Add(glyph);
        }

        HashSet<uint> filter = [.. (features ?? DefaultClosureFeatures).Select(t => t.Value)];
        GsubClosure.Close(_tables.Gsub, filter, set);
        return new SortedSet<ushort>(set.Glyphs().Select(g => (ushort)g));
    }

    private IEnumerable<LayoutTable> Tables => [_tables.Gsub, _tables.Gpos];
}
