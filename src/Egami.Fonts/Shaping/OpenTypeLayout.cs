namespace Egami.Fonts.Shaping;

/// <summary>
/// What a face's OpenType layout tables (GSUB and GPOS) offer: the scripts, language systems and features they list. A table
/// whose structure is damaged is left out whole, as the shaper leaves it out.
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

    private IEnumerable<LayoutTable> Tables => [_tables.Gsub, _tables.Gpos];
}
