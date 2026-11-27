// After HarfBuzz's hb-subset-plan-layout.cc and the subset methods of hb-ot-layout-common.hh, hb-ot-layout-gsubgpos.hh and
// OT/Layout/GDEF/GDEF.hh (MIT; see THIRD-PARTY-NOTICES.md), with fontTools' subset
// module as a second reference.
using Egami.Fonts.Parsing;
using Egami.Fonts.Shaping;

using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Subsetting;

/// <summary>
/// Writes a subset's GDEF, GSUB and GPOS cut down to the kept glyphs and renumbered through the glyph map, as hb-subset does: the
/// features of <see cref="SubsetOptions.LayoutFeatures"/> in every script and language system, the lookups they and their
/// contextual rules name that still apply to a kept glyph, and in each lookup the entries for kept glyphs.
/// </summary>
internal static class LayoutSubsetter
{
    /// <summary>Adds the layout tables of a <see cref="SubsetOptions.LayoutClosure"/> subset to <paramref name="tables"/>.</summary>
    public static void Write(Typeface face, Dictionary<ushort, ushort> map, SubsetOptions options, SortedDictionary<uint, byte[]> tables)
    {
        var glyphs = new GlyphRemap(map);
        HashSet<uint> filter = [.. (options.LayoutFeatures ?? OpenTypeLayout.DefaultClosureFeatures).Select(t => t.Value)];
        if (Read(face, Tags.Gdef, d => LayoutSanitizer.SanitizeGdef(d), out ReadOnlyMemory<byte> gdef, out long gdefOffset))
        {
            tables[Tags.Gdef.Value] = GdefWriter.Write(gdef, glyphs) ?? throw FontErrors.Corrupt(gdefOffset, "The GDEF table is too large to write.");
        }

        foreach ((FontTag tag, bool gpos) in (ReadOnlySpan<(FontTag, bool)>)[(Tags.Gsub, false), (Tags.Gpos, true)])
        {
            if (Read(face, tag, d => LayoutSanitizer.SanitizeLayout(d, gpos), out ReadOnlyMemory<byte> data, out long offset))
            {
                tables[tag.Value] = LayoutTableWriter.Write(data, gpos, glyphs, filter, options.ExtensionLookups)
                    ?? throw FontErrors.Corrupt(offset, $"The {tag} table is too large to write.");
            }
        }

        // MATH holds glyph ids; it is copied while they stay the same.
        if (options.RetainGlyphIds)
        {
            SfntWriter.CopyTable(face, Tags.Math, tables);
        }
    }

    private delegate bool Sanitizer(ReadOnlySpan<byte> data);

    /// <summary>The face's table when it has one; a table the shaper would leave out as damaged throws.</summary>
    private static bool Read(Typeface face, FontTag tag, Sanitizer sound, out ReadOnlyMemory<byte> data, out long offset)
    {
        offset = 0;
        if (!face.TryGetTable(tag, out data))
        {
            return false;
        }

        face.GetTableSpan(tag, out int at);
        offset = at;
        if (!sound(data.Span))
        {
            throw FontErrors.Corrupt(offset, $"The {tag} table is damaged, so the subset cannot keep it.");
        }

        return true;
    }
}

/// <summary>Writes a GSUB or GPOS table cut down to the kept glyphs.</summary>
internal sealed class LayoutTableWriter
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly bool _gpos;
    private readonly GlyphRemap _glyphs;
    private readonly LayoutTable _table;
    private readonly SortedSet<int> _features;
    private readonly Dictionary<int, int> _featureMap = [];
    private readonly Dictionary<int, int> _lookupMap = [];

    private LayoutTableWriter(ReadOnlyMemory<byte> data, bool gpos, GlyphRemap glyphs, IReadOnlySet<uint> filter)
    {
        _data = data;
        _gpos = gpos;
        _glyphs = glyphs;
        _table = new LayoutTable(data, gpos);
        _features = GsubClosure.CollectFeatures(_table, filter);
        foreach (int feature in _features)
        {
            _featureMap[feature] = _featureMap.Count;
        }
    }

    private ReadOnlySpan<byte> D => _data.Span;

    /// <summary>The table, or null when it cannot be packed with every offset in range.</summary>
    public static byte[]? Write(ReadOnlyMemory<byte> data, bool gpos, GlyphRemap glyphs, IReadOnlySet<uint> filter, bool extension = false)
    {
        var writer = new LayoutTableWriter(data, gpos, glyphs, filter);
        writer.KeepLookups();

        // As hb-repacker: first plainly; then with every lookup an extension lookup, each subtable packed on its own.
        return (extension ? null : OtGraph.Pack(writer.Build(extension: false), breadthFirst: true))
            ?? OtGraph.Pack(writer.Build(extension: true), breadthFirst: true)
            ?? OtGraph.Pack(writer.Build(extension: true), breadthFirst: false);
    }

    /// <summary>
    /// The lookups the subset keeps: those of the kept features and their feature variations, and those the contextual rules
    /// that can still match call, less any left with no subtable that applies to a kept glyph.
    /// </summary>
    private void KeepLookups()
    {
        SortedSet<int> roots = [];
        foreach (int feature in _features)
        {
            roots.UnionWith(_table.GetFeatureLookups(feature, -1));
        }

        _table.CollectFeatureVariationLookups(_features, roots);
        var seen = new HashSet<int>(roots);
        var pending = new Queue<int>(roots);
        var kept = new SortedSet<int>();
        var probe = new SubtableWriter(_data, new OtGraph(), _glyphs, index =>
        {
            if (seen.Add(index))
            {
                pending.Enqueue(index);
            }

            return index;
        }, _gpos);
        while (pending.Count > 0)
        {
            int index = pending.Dequeue();
            if (_table.GetLookup(index) is not LayoutLookup lookup)
            {
                continue;
            }

            bool any = false;
            for (int i = 0; i < lookup.Subtables.Length; i++)
            {
                any |= probe.Write(lookup.SubtableTypes[i], lookup.Subtables[i]) is not null;
            }

            if (any)
            {
                kept.Add(index);
            }
        }

        foreach (int index in kept)
        {
            _lookupMap[index] = _lookupMap.Count;
        }
    }

    private int MapLookup(int old) => _lookupMap.TryGetValue(old, out int now) ? now : -1;

    private OtObject Build(bool extension)
    {
        var graph = new OtGraph();
        var subtables = new SubtableWriter(_data, graph, _glyphs, MapLookup, _gpos);
        int extensionType = _gpos ? 9 : 7;
        int group = 1;
        List<OtObject> lookups = [];
        foreach (int index in _lookupMap.Keys.Order())
        {
            LayoutLookup lookup = _table.GetLookup(index)!;
            List<(int Type, OtObject Subtable)> written = [];
            for (int i = 0; i < lookup.Subtables.Length; i++)
            {
                if (extension)
                {
                    graph.PushScope();
                }

                graph.Group = extension ? group++ : 1;
                if (subtables.Write(lookup.SubtableTypes[i], lookup.Subtables[i]) is { } subtable)
                {
                    written.Add((lookup.SubtableTypes[i], subtable));
                }

                if (extension)
                {
                    graph.PopScope();
                }
            }

            graph.Group = 0;

            // A lookup whose subtables differ in type (only possible through extension subtables) stays an extension lookup.
            bool asExtension = extension || written.Any(s => s.Type != lookup.Type);
            OtWriter w = OtGraph.Writer();
            w.U16(asExtension ? extensionType : lookup.Type);
            int flag = lookup.Props & 0xFFFF;
            w.U16(flag);
            w.U16(written.Count);
            foreach ((int type, OtObject subtable) in written)
            {
                if (asExtension)
                {
                    OtWriter e = OtGraph.Writer();
                    e.U16(1);
                    e.U16(type);
                    e.Offset32(subtable);
                    w.Offset16(graph.Add(e));
                }
                else
                {
                    w.Offset16(subtable);
                }
            }

            if ((flag & LookupFlag.UseMarkFilteringSet) != 0)
            {
                w.U16(lookup.Props >>> 16);
            }

            lookups.Add(graph.Add(w));
        }

        graph.Group = 0;
        OtWriter list = OtGraph.Writer();
        list.U16(lookups.Count);
        lookups.ForEach(list.Offset16);
        OtObject lookupList = graph.Add(list);

        ReadOnlySpan<byte> d = D;
        int scriptList = Off16(d, 0, 4);
        int featureList = Off16(d, 0, 6);
        int featureVariations = U16(d, 2) >= 1 ? Off32(d, 0, 10) : 0;
        OtObject? variations = featureVariations != 0 && U16(d, featureVariations) == 1 ? FeatureVariations(graph, featureVariations) : null;

        OtWriter header = OtGraph.Writer();
        header.U16(1);
        header.U16(variations is null ? 0 : 1);
        header.Offset16(ScriptList(graph, scriptList));
        header.Offset16(FeatureList(graph, featureList));
        header.Offset16(lookupList);
        if (variations is not null)
        {
            header.Offset32(variations);
        }

        return graph.Add(header);
    }

    /// <summary>Every script and language system, each with the kept features it lists.</summary>
    private OtObject ScriptList(OtGraph graph, int list)
    {
        ReadOnlySpan<byte> d = D;
        int count = list == 0 ? 0 : U16(d, list);
        OtWriter w = OtGraph.Writer();
        w.U16(count);
        for (int i = 0; i < count; i++)
        {
            int record = list + 2 + (i * 6);
            int script = Off16(d, list, record + 4);
            OtObject? written = null;
            if (script != 0)
            {
                int languages = U16(d, script + 2);
                OtWriter sw = OtGraph.Writer();
                sw.Offset16(LangSys(graph, Off16(d, script, script)));
                sw.U16(languages);
                for (int j = 0; j < languages; j++)
                {
                    int langRecord = script + 4 + (j * 6);
                    sw.U32(Tag(d, langRecord));
                    sw.Offset16(LangSys(graph, Off16(d, script, langRecord + 4)));
                }

                written = graph.Add(sw);
            }

            w.U32(Tag(d, record));
            w.Offset16(written);
        }

        return graph.Add(w);
    }

    private OtObject? LangSys(OtGraph graph, int langSys)
    {
        if (langSys == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        int required = U16(d, langSys + 2);
        int count = U16(d, langSys + 4);
        List<int> features = [];
        for (int i = 0; i < count; i++)
        {
            if (_featureMap.TryGetValue(U16(d, langSys + 6 + (i * 2)), out int now))
            {
                features.Add(now);
            }
        }

        OtWriter w = OtGraph.Writer();
        w.U16(0);
        w.U16(_featureMap.TryGetValue(required, out int requiredNow) ? requiredNow : 0xFFFF);
        w.U16(features.Count);
        features.ForEach(w.U16);
        return graph.Add(w);
    }

    /// <summary>The kept features, each with the kept lookups it lists (kept even when none is left, as the shapers ask whether a feature is there).</summary>
    private OtObject FeatureList(OtGraph graph, int list)
    {
        ReadOnlySpan<byte> d = D;
        OtWriter w = OtGraph.Writer();
        w.U16(_features.Count);
        foreach (int index in _features)
        {
            int record = list + 2 + (index * 6);
            uint tag = Tag(d, record);
            w.U32(tag);
            w.Offset16(Feature(graph, Off16(d, list, record + 4), tag));
        }

        return graph.Add(w);
    }

    private OtObject Feature(OtGraph graph, int feature, uint tag)
    {
        ReadOnlySpan<byte> d = D;
        int count = feature == 0 ? 0 : U16(d, feature + 2);
        List<int> lookups = [];
        for (int i = 0; i < count; i++)
        {
            int now = MapLookup(U16(d, feature + 4 + (i * 2)));
            if (now >= 0)
            {
                lookups.Add(now);
            }
        }

        OtWriter w = OtGraph.Writer();
        w.Offset16(feature == 0 ? null : FeatureParams(graph, Off16(d, feature, feature), tag));
        w.U16(lookups.Count);
        lookups.ForEach(w.U16);
        return graph.Add(w);
    }

    /// <summary>The parameters of a 'size', stylistic set or character variant feature; other features have none.</summary>
    private OtObject? FeatureParams(OtGraph graph, int at, uint tag)
    {
        if (at == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        string name = new FontTag(tag).ToString();
        int size = name switch
        {
            "size" => 10,
            _ when name.StartsWith("ss", StringComparison.Ordinal) && int.TryParse(name.AsSpan(2), out int n) && n is >= 1 and <= 20 => 4,
            _ when name.StartsWith("cv", StringComparison.Ordinal) && int.TryParse(name.AsSpan(2), out int n) && n is >= 1 and <= 99 => 14 + (3 * U16(d, at + 12)),
            _ => 0,
        };
        return size == 0 ? null : LayoutCommon.Blob(graph, d, at, size);
    }

    /// <summary>
    /// The feature variations with each substitution's kept features and their kept lookups. Every record stays, as the first
    /// whose conditions hold wins, except one whose conditions can never hold.
    /// </summary>
    private OtObject FeatureVariations(OtGraph graph, int fv)
    {
        ReadOnlySpan<byte> d = D;
        uint count = U32(d, fv + 4);
        List<(OtObject? Conditions, OtObject? Substitution)> records = [];
        for (int i = 0; i < count && i < 65536; i++)
        {
            int record = fv + 8 + (i * 8);
            int set = Off32(d, fv, record);
            OtObject? conditions = null;
            if (set != 0)
            {
                int n = U16(d, set);
                OtWriter cw = OtGraph.Writer();
                cw.U16(n);
                bool holds = true;
                for (int j = 0; j < n && holds; j++)
                {
                    int condition = Off32(d, set, set + 2 + (j * 4));
                    holds = condition != 0 && U16(d, condition) == 1;
                    cw.Offset32(holds ? LayoutCommon.Blob(graph, d, condition, 8) : null);
                }

                if (!holds)
                {
                    continue;
                }

                conditions = graph.Add(cw);
            }

            records.Add((conditions, Substitution(graph, Off32(d, fv, record + 4))));
        }

        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.U16(0);
        w.U32((uint)records.Count);
        foreach ((OtObject? conditions, OtObject? substitution) in records)
        {
            w.Offset32(conditions);
            w.Offset32(substitution);
        }

        return graph.Add(w);
    }

    private OtObject? Substitution(OtGraph graph, int table)
    {
        ReadOnlySpan<byte> d = D;
        if (table == 0 || U16(d, table) != 1)
        {
            return null;
        }

        int count = U16(d, table + 4);
        List<(int Feature, OtObject Alternate)> kept = [];
        for (int i = 0; i < count; i++)
        {
            int record = table + 6 + (i * 6);
            if (_featureMap.TryGetValue(U16(d, record), out int now))
            {
                kept.Add((now, Feature(graph, Off32(d, table, record + 2), _table.GetFeatureTag(U16(d, record)))));
            }
        }

        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.U16(0);
        w.U16(kept.Count);
        foreach ((int feature, OtObject alternate) in kept)
        {
            w.U16(feature);
            w.Offset32(alternate);
        }

        return graph.Add(w);
    }
}

/// <summary>Writes a GDEF table cut down to the kept glyphs: glyph and mark attachment classes, attach points, ligature carets and mark glyph sets.</summary>
internal static class GdefWriter
{
    public static byte[]? Write(ReadOnlyMemory<byte> data, GlyphRemap glyphs)
    {
        ReadOnlySpan<byte> d = data.Span;
        var graph = new OtGraph();
        int minor = U16(d, 2);
        int glyphClassDef = Off16(d, 0, 4);
        int attachList = Off16(d, 0, 6);
        int ligCaretList = Off16(d, 0, 8);
        int markAttachClassDef = Off16(d, 0, 10);
        int markGlyphSets = minor >= 2 ? Off16(d, 0, 12) : 0;
        int varStore = minor >= 3 ? Off32(d, 0, 14) : 0;

        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.U16(Math.Min(minor, 3));
        w.Offset16(glyphClassDef == 0 ? null : LayoutCommon.WriteClassDef(graph, d, glyphClassDef, glyphs, null));
        w.Offset16(AttachList(graph, d, attachList, glyphs));
        w.Offset16(LigCaretList(graph, d, ligCaretList, glyphs));
        w.Offset16(markAttachClassDef == 0 ? null : LayoutCommon.WriteClassDef(graph, d, markAttachClassDef, glyphs, null));
        if (minor >= 2)
        {
            w.Offset16(MarkGlyphSets(graph, d, markGlyphSets, glyphs));
        }

        if (minor >= 3)
        {
            w.Offset32(VariationStore(graph, d, varStore));
        }

        OtObject root = graph.Add(w);
        return OtGraph.Pack(root, breadthFirst: true) ?? OtGraph.Pack(root, breadthFirst: false);
    }

    private static OtObject? AttachList(OtGraph graph, ReadOnlySpan<byte> d, int list, GlyphRemap glyphs)
    {
        if (list == 0)
        {
            return null;
        }

        int count = U16(d, list + 2);
        List<int> covered = [];
        List<OtObject> points = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, list, list), glyphs))
        {
            int point = index < count ? Off16(d, list, list + 4 + (index * 2)) : 0;
            if (point != 0)
            {
                covered.Add(glyphs.Map(old));
                points.Add(LayoutCommon.Blob(graph, d, point, 2 + (2 * U16(d, point)))!);
            }
        }

        return covered.Count == 0 ? null : CoveredOffsets(graph, covered, points);
    }

    private static OtObject? LigCaretList(OtGraph graph, ReadOnlySpan<byte> d, int list, GlyphRemap glyphs)
    {
        if (list == 0)
        {
            return null;
        }

        int count = U16(d, list + 2);
        List<int> covered = [];
        List<OtObject> ligatures = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, list, list), glyphs))
        {
            int ligGlyph = index < count ? Off16(d, list, list + 4 + (index * 2)) : 0;
            if (ligGlyph == 0)
            {
                continue;
            }

            int carets = U16(d, ligGlyph);
            OtWriter lw = OtGraph.Writer();
            lw.U16(carets);
            for (int i = 0; i < carets; i++)
            {
                int caret = Off16(d, ligGlyph, ligGlyph + 2 + (i * 2));
                OtObject? written = null;
                if (caret != 0 && U16(d, caret) == 3)
                {
                    OtWriter cw = OtGraph.Writer();
                    cw.U16(3);
                    cw.U16(U16(d, caret + 2));
                    cw.Offset16(LayoutCommon.Device(graph, d, Off16(d, caret, caret + 4)));
                    written = graph.Add(cw);
                }
                else if (caret != 0)
                {
                    written = LayoutCommon.Blob(graph, d, caret, 4);
                }

                lw.Offset16(written);
            }

            covered.Add(glyphs.Map(old));
            ligatures.Add(graph.Add(lw));
        }

        return covered.Count == 0 ? null : CoveredOffsets(graph, covered, ligatures);
    }

    /// <summary>Every mark glyph set (lookups name them by index), each cut to the kept glyphs.</summary>
    private static OtObject? MarkGlyphSets(OtGraph graph, ReadOnlySpan<byte> d, int sets, GlyphRemap glyphs)
    {
        if (sets == 0 || U16(d, sets) != 1)
        {
            return null;
        }

        int count = U16(d, sets + 2);
        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.U16(count);
        for (int i = 0; i < count; i++)
        {
            int coverage = Off32(d, sets, sets + 4 + (i * 4));
            w.Offset32(coverage == 0 ? null : LayoutCommon.WriteCoverage(graph, [.. LayoutCommon.Covered(d, coverage, glyphs).Select(c => glyphs.Map(c.Old))]));
        }

        return graph.Add(w);
    }

    /// <summary>The item variation store, copied: it holds no glyph ids, and GPOS device tables name its entries.</summary>
    private static OtObject? VariationStore(OtGraph graph, ReadOnlySpan<byte> d, int store)
    {
        if (store == 0)
        {
            return null;
        }

        int regions = Off32(d, store, store + 2);
        int count = U16(d, store + 6);
        OtWriter w = OtGraph.Writer();
        w.U16(U16(d, store));
        w.Offset32(regions == 0 ? null : LayoutCommon.Blob(graph, d, regions, 4 + (U16(d, regions) * U16(d, regions + 2) * 6)));
        w.U16(count);
        for (int i = 0; i < count; i++)
        {
            int data = Off32(d, store, store + 8 + (i * 4));
            OtObject? written = null;
            if (data != 0)
            {
                int items = U16(d, data);
                int wordDeltaCount = U16(d, data + 2);
                int regionIndexes = U16(d, data + 4);
                int words = wordDeltaCount & 0x7FFF;
                bool longWords = (wordDeltaCount & 0x8000) != 0;
                int rowSize = longWords ? (4 * words) + (2 * (regionIndexes - words)) : (2 * words) + (regionIndexes - words);
                written = LayoutCommon.Blob(graph, d, data, 6 + (2 * regionIndexes) + (items * Math.Max(rowSize, 0)));
            }

            w.Offset32(written);
        }

        return graph.Add(w);
    }

    private static OtObject CoveredOffsets(OtGraph graph, List<int> covered, List<OtObject> targets)
    {
        OtWriter w = OtGraph.Writer();
        w.Offset16(LayoutCommon.WriteCoverage(graph, covered));
        w.U16(targets.Count);
        targets.ForEach(w.Offset16);
        return graph.Add(w);
    }
}
