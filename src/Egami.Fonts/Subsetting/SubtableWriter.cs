// After HarfBuzz's subset methods of the GSUB, GPOS and common layout tables (hb-ot-layout-gsubgpos.hh,
// OT/Layout/GSUB/*.hh, OT/Layout/GPOS/*.hh; MIT; see THIRD-PARTY-NOTICES.md), with fontTools'
// subset module as a second reference.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Subsetting;

/// <summary>
/// Writes one GSUB or GPOS lookup subtable cut down to the kept glyphs, with glyph ids, classes and lookup indexes renumbered;
/// null when nothing in it can apply to a kept glyph. Every entry is read as the shaper reads it, so the subset shapes the kept
/// glyphs as the full face does, damaged tables included. With a <see cref="VariationBake"/> (a variable face's static subset),
/// value records and anchors take their variation deltas into their values and lose their device tables.
/// </summary>
internal sealed class SubtableWriter(ReadOnlyMemory<byte> data, OtGraph graph, GlyphRemap glyphs, Func<int, int> lookupMap, bool gpos, VariationBake? bake = null)
{
    private ReadOnlySpan<byte> D => data.Span;

    /// <summary>The subtable of a lookup type (never an extension) at <paramref name="t"/>.</summary>
    public OtObject? Write(int type, int t)
    {
        int format = U16(D, t);
        if (!gpos)
        {
            return (type, format) switch
            {
                (1, 1) or (1, 2) => SingleSubst(t, format),
                (2, 1) => SequenceSubst(t, multiple: true),
                (3, 1) => SequenceSubst(t, multiple: false),
                (4, 1) => LigatureSubst(t),
                (5, _) => Context(t, format),
                (6, _) => ChainContext(t, format),
                (8, 1) => ReverseChainSingleSubst(t),
                _ => null,
            };
        }

        return (type, format) switch
        {
            (1, 1) or (1, 2) => SinglePos(t, format),
            (2, 1) => PairPosFormat1(t),
            (2, 2) => PairPosFormat2(t),
            (3, 1) => CursivePos(t),
            (4, 1) or (6, 1) => MarkToBase(t),
            (5, 1) => MarkToLigature(t),
            (7, _) => Context(t, format),
            (8, _) => ChainContext(t, format),
            _ => null,
        };
    }

    // ---- GSUB ----

    private OtObject? SingleSubst(int t, int format)
    {
        ReadOnlySpan<byte> d = D;
        int deltaOrCount = U16(d, t + 4);
        List<(int In, int Out)> pairs = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            int output = format == 1 ? (old + deltaOrCount) & 0xFFFF : index < deltaOrCount ? U16(d, t + 6 + (index * 2)) : -1;
            if (output >= 0 && glyphs.TryMap(output, out int now))
            {
                pairs.Add((glyphs.Map(old), now));
            }
        }

        if (pairs.Count == 0)
        {
            return null;
        }

        OtObject coverage = LayoutCommon.WriteCoverage(graph, [.. pairs.Select(p => p.In)]);
        int delta = (pairs[0].Out - pairs[0].In) & 0xFFFF;
        OtWriter w = OtGraph.Writer();
        if (pairs.All(p => ((p.Out - p.In) & 0xFFFF) == delta))
        {
            w.U16(1);
            w.Offset16(coverage);
            w.U16(delta);
        }
        else
        {
            w.U16(2);
            w.Offset16(coverage);
            w.U16(pairs.Count);
            foreach ((_, int output) in pairs)
            {
                w.U16(output);
            }
        }

        return graph.Add(w);
    }

    /// <summary>
    /// Multiple substitution (a sequence kept only when all its glyphs are, a missing one read as the empty sequence that deletes
    /// the glyph) or alternate substitution (the kept alternates).
    /// </summary>
    private OtObject? SequenceSubst(int t, bool multiple)
    {
        ReadOnlySpan<byte> d = D;
        int count = U16(d, t + 4);
        List<int> covered = [];
        List<OtObject?> lists = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            int list = index < count ? Off16(d, t, t + 6 + (index * 2)) : 0;
            int n = list == 0 ? 0 : U16(d, list);
            List<int> mapped = [];
            bool all = true;
            for (int i = 0; i < n; i++)
            {
                if (glyphs.TryMap(U16(d, list + 2 + (i * 2)), out int now))
                {
                    mapped.Add(now);
                }
                else
                {
                    all = false;
                }
            }

            if (multiple ? !all : mapped.Count == 0)
            {
                continue;
            }

            OtWriter lw = OtGraph.Writer();
            lw.U16(mapped.Count);
            foreach (int g in mapped)
            {
                lw.U16(g);
            }

            covered.Add(glyphs.Map(old));
            lists.Add(graph.Add(lw));
        }

        return WriteCoveredOffsets(1, covered, lists);
    }

    private OtObject? LigatureSubst(int t)
    {
        ReadOnlySpan<byte> d = D;
        int count = U16(d, t + 4);
        List<int> covered = [];
        List<OtObject?> sets = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            int set = index < count ? Off16(d, t, t + 6 + (index * 2)) : 0;
            int n = set == 0 ? 0 : U16(d, set);
            List<OtObject?> ligatures = [];
            for (int i = 0; i < n; i++)
            {
                int ligature = Off16(d, set, set + 2 + (i * 2));
                int components = ligature == 0 ? 0 : U16(d, ligature + 2);
                if (components == 0 || !glyphs.TryMap(U16(d, ligature), out int ligGlyph))
                {
                    continue;
                }

                OtWriter lw = OtGraph.Writer();
                lw.U16(ligGlyph);
                lw.U16(components);
                bool all = true;
                for (int k = 0; k < components - 1 && all; k++)
                {
                    all = glyphs.TryMap(U16(d, ligature + 4 + (k * 2)), out int component);
                    lw.U16(component);
                }

                if (all)
                {
                    ligatures.Add(graph.Add(lw));
                }
            }

            if (ligatures.Count > 0)
            {
                covered.Add(glyphs.Map(old));
                sets.Add(OffsetList(ligatures));
            }
        }

        return WriteCoveredOffsets(1, covered, sets);
    }

    private OtObject? ReverseChainSingleSubst(int t)
    {
        ReadOnlySpan<byte> d = D;
        int backtrackCount = U16(d, t + 4);
        int lookaheadAt = t + 6 + (backtrackCount * 2);
        int lookaheadCount = U16(d, lookaheadAt);
        int substitutesAt = lookaheadAt + 2 + (lookaheadCount * 2);
        int count = U16(d, substitutesAt);
        List<OtObject?>? backtrack = Coverages(t, t + 6, backtrackCount);
        List<OtObject?>? lookahead = Coverages(t, lookaheadAt + 2, lookaheadCount);
        if (backtrack is null || lookahead is null)
        {
            return null;
        }

        List<(int In, int Out)> pairs = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            if (index < count && glyphs.TryMap(U16(d, substitutesAt + 2 + (index * 2)), out int now))
            {
                pairs.Add((glyphs.Map(old), now));
            }
        }

        if (pairs.Count == 0)
        {
            return null;
        }

        OtObject coverage = LayoutCommon.WriteCoverage(graph, [.. pairs.Select(p => p.In)]);
        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.Offset16(coverage);
        WriteOffsets(w, backtrack);
        WriteOffsets(w, lookahead);
        w.U16(pairs.Count);
        foreach ((_, int output) in pairs)
        {
            w.U16(output);
        }

        return graph.Add(w);
    }

    // ---- Context and chain context (GSUB 5 and 6, GPOS 7 and 8) ----

    private OtObject? Context(int t, int format)
    {
        ReadOnlySpan<byte> d = D;
        switch (format)
        {
            case 1:
                {
                    int count = U16(d, t + 4);
                    List<int> covered = [];
                    List<OtObject?> sets = [];
                    foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
                    {
                        if (RuleSet(index < count ? Off16(d, t, t + 6 + (index * 2)) : 0, false, null, MapGlyph, null) is { } set)
                        {
                            covered.Add(glyphs.Map(old));
                            sets.Add(set);
                        }
                    }

                    return WriteCoveredOffsets(1, covered, sets);
                }

            case 2:
                {
                    List<(int Old, int Index)> covered = LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs);
                    int classDef = Off16(d, t, t + 4);
                    var classes = new ClassRemap(LayoutCommon.Classes(d, classDef, glyphs));
                    List<OtObject?> sets = ClassSets(t + 6, t, classes, false, null, classes.Map, null);
                    if (covered.Count == 0 || sets.Count == 0)
                    {
                        return null;
                    }

                    OtObject coverage = LayoutCommon.WriteCoverage(graph, [.. covered.Select(c => glyphs.Map(c.Old))]);
                    OtObject? classObject = classDef == 0 ? null : LayoutCommon.WriteClassDef(graph, d, classDef, glyphs, classes);
                    OtWriter w = OtGraph.Writer();
                    w.U16(2);
                    w.Offset16(coverage);
                    w.Offset16(classObject);
                    WriteOffsets(w, sets);
                    return graph.Add(w);
                }

            case 3:
                {
                    int glyphCount = U16(d, t + 2);
                    int lookupCount = U16(d, t + 4);
                    List<OtObject?>? coverages = glyphCount == 0 ? null : Coverages(t, t + 6, glyphCount);
                    if (coverages is null)
                    {
                        return null;
                    }

                    List<(int, int)> records = LookupRecords(t + 6 + (glyphCount * 2), lookupCount);
                    OtWriter w = OtGraph.Writer();
                    w.U16(3);
                    w.U16(glyphCount);
                    w.U16(records.Count);
                    foreach (OtObject? c in coverages)
                    {
                        w.Offset16(c);
                    }

                    WriteRecords(w, records);
                    return graph.Add(w);
                }

            default:
                return null;
        }
    }

    private OtObject? ChainContext(int t, int format)
    {
        ReadOnlySpan<byte> d = D;
        switch (format)
        {
            case 1:
                {
                    int count = U16(d, t + 4);
                    List<int> covered = [];
                    List<OtObject?> sets = [];
                    foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
                    {
                        if (RuleSet(index < count ? Off16(d, t, t + 6 + (index * 2)) : 0, true, MapGlyph, MapGlyph, MapGlyph) is { } set)
                        {
                            covered.Add(glyphs.Map(old));
                            sets.Add(set);
                        }
                    }

                    return WriteCoveredOffsets(1, covered, sets);
                }

            case 2:
                {
                    List<(int Old, int Index)> covered = LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs);
                    int backtrackDef = Off16(d, t, t + 4);
                    int inputDef = Off16(d, t, t + 6);
                    int lookaheadDef = Off16(d, t, t + 8);
                    var backtrack = new ClassRemap(LayoutCommon.Classes(d, backtrackDef, glyphs));
                    var input = new ClassRemap(LayoutCommon.Classes(d, inputDef, glyphs));
                    var lookahead = new ClassRemap(LayoutCommon.Classes(d, lookaheadDef, glyphs));
                    List<OtObject?> sets = ClassSets(t + 10, t, input, true, backtrack.Map, input.Map, lookahead.Map);
                    if (covered.Count == 0 || sets.Count == 0)
                    {
                        return null;
                    }

                    OtObject coverage = LayoutCommon.WriteCoverage(graph, [.. covered.Select(c => glyphs.Map(c.Old))]);
                    OtWriter w = OtGraph.Writer();
                    w.U16(2);
                    w.Offset16(coverage);
                    w.Offset16(backtrackDef == 0 ? null : LayoutCommon.WriteClassDef(graph, d, backtrackDef, glyphs, backtrack));
                    w.Offset16(inputDef == 0 ? null : LayoutCommon.WriteClassDef(graph, d, inputDef, glyphs, input));
                    w.Offset16(lookaheadDef == 0 ? null : LayoutCommon.WriteClassDef(graph, d, lookaheadDef, glyphs, lookahead));
                    WriteOffsets(w, sets);
                    return graph.Add(w);
                }

            case 3:
                {
                    int backtrackCount = U16(d, t + 2);
                    int inputAt = t + 4 + (backtrackCount * 2);
                    int inputCount = U16(d, inputAt);
                    int lookaheadAt = inputAt + 2 + (inputCount * 2);
                    int lookaheadCount = U16(d, lookaheadAt);
                    int recordsAt = lookaheadAt + 2 + (lookaheadCount * 2);
                    List<OtObject?>? backtrack = Coverages(t, t + 4, backtrackCount);
                    List<OtObject?>? inputs = inputCount == 0 ? null : Coverages(t, inputAt + 2, inputCount);
                    List<OtObject?>? lookahead = Coverages(t, lookaheadAt + 2, lookaheadCount);
                    if (backtrack is null || inputs is null || lookahead is null)
                    {
                        return null;
                    }

                    List<(int, int)> records = LookupRecords(recordsAt + 2, U16(d, recordsAt));
                    OtWriter w = OtGraph.Writer();
                    w.U16(3);
                    WriteOffsets(w, backtrack);
                    WriteOffsets(w, inputs);
                    WriteOffsets(w, lookahead);
                    w.U16(records.Count);
                    WriteRecords(w, records);
                    return graph.Add(w);
                }

            default:
                return null;
        }
    }

    private int MapGlyph(int old) => glyphs.TryMap(old, out int now) ? now : -1;

    /// <summary>
    /// The rule sets of a class-based context subtable, by new input class (the old count at <paramref name="countAt"/>, offsets
    /// after it); trailing empty sets are left out, and an empty list means no rule can apply.
    /// </summary>
    private List<OtObject?> ClassSets(int countAt, int t, ClassRemap input, bool chain, Func<int, int>? backtrack, Func<int, int> inputMap, Func<int, int>? lookahead)
    {
        ReadOnlySpan<byte> d = D;
        int count = U16(d, countAt);
        int[] oldClasses = input.OldClasses();
        List<OtObject?> sets = [];
        for (int c = 0; c < oldClasses.Length; c++)
        {
            int set = oldClasses[c] < count ? Off16(d, t, countAt + 2 + (oldClasses[c] * 2)) : 0;
            sets.Add(RuleSet(set, chain, backtrack, inputMap, lookahead));
        }

        while (sets.Count > 0 && sets[^1] is null)
        {
            sets.RemoveAt(sets.Count - 1);
        }

        return sets;
    }

    /// <summary>A rule set with the rules whose every value maps; null when none does.</summary>
    private OtObject? RuleSet(int set, bool chain, Func<int, int>? backtrack, Func<int, int> input, Func<int, int>? lookahead)
    {
        if (set == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        int count = U16(d, set);
        List<OtObject?> rules = [];
        for (int i = 0; i < count; i++)
        {
            int rule = Off16(d, set, set + 2 + (i * 2));
            if (rule != 0 && (chain ? ChainRule(rule, backtrack!, input, lookahead!) : Rule(rule, input)) is { } written)
            {
                rules.Add(written);
            }
        }

        return rules.Count == 0 ? null : OffsetList(rules);
    }

    private OtObject? Rule(int rule, Func<int, int> map)
    {
        ReadOnlySpan<byte> d = D;
        int inputCount = U16(d, rule);
        int lookupCount = U16(d, rule + 2);
        List<int>? values = Values(rule + 4, Math.Max(inputCount - 1, 0), map);
        if (values is null)
        {
            return null;
        }

        List<(int, int)> records = LookupRecords(rule + 4 + (values.Count * 2), lookupCount);
        OtWriter w = OtGraph.Writer();
        w.U16(inputCount);
        w.U16(records.Count);
        foreach (int v in values)
        {
            w.U16(v);
        }

        WriteRecords(w, records);
        return graph.Add(w);
    }

    private OtObject? ChainRule(int rule, Func<int, int> backtrackMap, Func<int, int> inputMap, Func<int, int> lookaheadMap)
    {
        ReadOnlySpan<byte> d = D;
        int backtrackCount = U16(d, rule);
        int inputAt = rule + 2 + (backtrackCount * 2);
        int inputCount = U16(d, inputAt);
        int lookaheadAt = inputAt + 2 + (Math.Max(inputCount - 1, 0) * 2);
        int lookaheadCount = U16(d, lookaheadAt);
        int recordsAt = lookaheadAt + 2 + (lookaheadCount * 2);
        List<int>? backtrack = Values(rule + 2, backtrackCount, backtrackMap);
        List<int>? input = Values(inputAt + 2, Math.Max(inputCount - 1, 0), inputMap);
        List<int>? lookahead = Values(lookaheadAt + 2, lookaheadCount, lookaheadMap);
        if (backtrack is null || input is null || lookahead is null)
        {
            return null;
        }

        List<(int, int)> records = LookupRecords(recordsAt + 2, U16(d, recordsAt));
        OtWriter w = OtGraph.Writer();
        w.U16(backtrack.Count);
        backtrack.ForEach(w.U16);
        w.U16(inputCount);
        input.ForEach(w.U16);
        w.U16(lookahead.Count);
        lookahead.ForEach(w.U16);
        w.U16(records.Count);
        WriteRecords(w, records);
        return graph.Add(w);
    }

    /// <summary>Glyph ids or classes, each mapped; null when one has no kept glyph, so the rule cannot match.</summary>
    private List<int>? Values(int at, int count, Func<int, int> map)
    {
        ReadOnlySpan<byte> d = D;
        List<int> values = new(count);
        for (int i = 0; i < count; i++)
        {
            int now = map(U16(d, at + (i * 2)));
            if (now < 0)
            {
                return null;
            }

            values.Add(now);
        }

        return values;
    }

    /// <summary>Sequence lookup records with their lookups renumbered; those naming a lookup the subset leaves out (an empty one) go.</summary>
    private List<(int Sequence, int Lookup)> LookupRecords(int at, int count)
    {
        ReadOnlySpan<byte> d = D;
        List<(int, int)> records = [];
        for (int i = 0; i < count; i++)
        {
            int lookup = lookupMap(U16(d, at + (i * 4) + 2));
            if (lookup >= 0)
            {
                records.Add((U16(d, at + (i * 4)), lookup));
            }
        }

        return records;
    }

    private static void WriteRecords(OtWriter w, List<(int Sequence, int Lookup)> records)
    {
        foreach ((int sequence, int lookup) in records)
        {
            w.U16(sequence);
            w.U16(lookup);
        }
    }

    /// <summary>The coverages at 16-bit offsets from <paramref name="t"/>, each cut to the kept glyphs; null when one ends empty.</summary>
    private List<OtObject?>? Coverages(int t, int at, int count)
    {
        ReadOnlySpan<byte> d = D;
        List<OtObject?> result = new(count);
        for (int i = 0; i < count; i++)
        {
            List<(int Old, int Index)> covered = LayoutCommon.Covered(d, Off16(d, t, at + (i * 2)), glyphs);
            if (covered.Count == 0)
            {
                return null;
            }

            result.Add(LayoutCommon.WriteCoverage(graph, [.. covered.Select(c => glyphs.Map(c.Old))]));
        }

        return result;
    }

    // ---- GPOS ----

    private OtObject? SinglePos(int t, int format)
    {
        ReadOnlySpan<byte> d = D;
        int valueFormat = U16(d, t + 4) & 0xFF;
        int length = ValueLength(valueFormat);
        int count = format == 2 ? U16(d, t + 6) : 0;
        List<(int New, int Values)> kept = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            if (format == 1 || index < count)
            {
                kept.Add((glyphs.Map(old), format == 1 ? t + 6 : t + 8 + (index * length * 2)));
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        OtObject coverage = LayoutCommon.WriteCoverage(graph, [.. kept.Select(k => k.New)]);
        OtWriter w = OtGraph.Writer();
        w.U16(format);
        w.Offset16(coverage);
        w.U16(Baked(valueFormat));
        if (format == 1)
        {
            WriteValue(w, valueFormat, t, t + 6);
        }
        else
        {
            w.U16(kept.Count);
            foreach ((_, int values) in kept)
            {
                WriteValue(w, valueFormat, t, values);
            }
        }

        return graph.Add(w);
    }

    private OtObject? PairPosFormat1(int t)
    {
        ReadOnlySpan<byte> d = D;
        int format1 = U16(d, t + 4) & 0xFF;
        int format2 = U16(d, t + 6) & 0xFF;
        int length1 = ValueLength(format1);
        int length2 = ValueLength(format2);
        int recordSize = 2 + ((length1 + length2) * 2);
        int count = U16(d, t + 8);
        List<int> covered = [];
        List<OtObject?> sets = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            int set = index < count ? Off16(d, t, t + 10 + (index * 2)) : 0;
            int n = set == 0 ? 0 : U16(d, set);
            OtWriter sw = OtGraph.Writer();
            sw.U16(0);
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                int record = set + 2 + (i * recordSize);
                if (glyphs.TryMap(U16(d, record), out int second))
                {
                    // A PairSet's value records count their device offsets from the PairSet.
                    sw.U16(second);
                    WriteValue(sw, format1, set, record + 2);
                    WriteValue(sw, format2, set, record + 2 + (length1 * 2));
                    kept++;
                }
            }

            if (kept > 0)
            {
                sw.PatchU16(0, kept);
                covered.Add(glyphs.Map(old));
                sets.Add(graph.Add(sw));
            }
        }

        if (covered.Count == 0)
        {
            return null;
        }

        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.Offset16(LayoutCommon.WriteCoverage(graph, covered));
        w.U16(Baked(format1));
        w.U16(Baked(format2));
        WriteOffsets(w, sets);
        return graph.Add(w);
    }

    private OtObject? PairPosFormat2(int t)
    {
        ReadOnlySpan<byte> d = D;
        List<(int Old, int Index)> covered = LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs);
        if (covered.Count == 0)
        {
            return null;
        }

        int format1 = U16(d, t + 4) & 0xFF;
        int format2 = U16(d, t + 6) & 0xFF;
        int length1 = ValueLength(format1);
        int length2 = ValueLength(format2);
        int classDef1 = Off16(d, t, t + 8);
        int classDef2 = Off16(d, t, t + 10);
        int class1Count = U16(d, t + 12);
        int class2Count = U16(d, t + 14);
        var classes1 = new ClassRemap(LayoutCommon.Classes(d, classDef1, glyphs), class1Count);
        var classes2 = new ClassRemap(LayoutCommon.Classes(d, classDef2, glyphs), class2Count);
        if (class1Count == 0 || class2Count == 0)
        {
            return null;
        }

        OtWriter w = OtGraph.Writer();
        w.U16(2);
        w.Offset16(LayoutCommon.WriteCoverage(graph, [.. covered.Select(c => glyphs.Map(c.Old))]));
        w.U16(Baked(format1));
        w.U16(Baked(format2));
        w.Offset16(classDef1 == 0 ? null : LayoutCommon.WriteClassDef(graph, d, classDef1, glyphs, classes1));
        w.Offset16(classDef2 == 0 ? null : LayoutCommon.WriteClassDef(graph, d, classDef2, glyphs, classes2));
        w.U16(classes1.Count);
        w.U16(classes2.Count);
        foreach (int c1 in classes1.OldClasses())
        {
            foreach (int c2 in classes2.OldClasses())
            {
                int record = t + 16 + ((((c1 * class2Count) + c2) * (length1 + length2)) * 2);
                WriteValue(w, format1, t, record);
                WriteValue(w, format2, t, record + (length1 * 2));
            }
        }

        return graph.Add(w);
    }

    private OtObject? CursivePos(int t)
    {
        ReadOnlySpan<byte> d = D;
        int count = U16(d, t + 4);
        List<(int New, OtObject? Entry, OtObject? Exit)> kept = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            int record = t + 6 + (index * 4);
            OtObject? entry = index < count ? Anchor(Off16(d, t, record), sound: true) : null;
            OtObject? exit = index < count ? Anchor(Off16(d, t, record + 2), sound: true) : null;
            if (entry is not null || exit is not null)
            {
                kept.Add((glyphs.Map(old), entry, exit));
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.Offset16(LayoutCommon.WriteCoverage(graph, [.. kept.Select(k => k.New)]));
        w.U16(kept.Count);
        foreach ((_, OtObject? entry, OtObject? exit) in kept)
        {
            w.Offset16(entry);
            w.Offset16(exit);
        }

        return graph.Add(w);
    }

    /// <summary>
    /// MarkBasePos and MarkMarkPos: the kept marks with their classes renumbered, and every kept base (it steers the search for a
    /// base even where it has no anchor) with the anchors of the kept classes.
    /// </summary>
    private OtObject? MarkToBase(int t)
    {
        ReadOnlySpan<byte> d = D;
        int classCount = U16(d, t + 6);
        int baseArray = Off16(d, t, t + 10);
        (List<int> marks, OtObject? markArray, ClassRemap classes) = MarkArray(t);
        List<(int Old, int Index)> bases = LayoutCommon.Covered(d, Off16(d, t, t + 4), glyphs);
        if (marks.Count == 0 || bases.Count == 0 || baseArray == 0)
        {
            return null;
        }

        int rows = U16(d, baseArray);
        int[] oldClasses = classes.OldClasses();
        OtWriter bw = OtGraph.Writer();
        bw.U16(bases.Count);
        foreach ((_, int index) in bases)
        {
            foreach (int c in oldClasses)
            {
                bw.Offset16(index < rows ? Anchor(Off16(d, baseArray, baseArray + 2 + (((index * classCount) + c) * 2)), sound: true) : null);
            }
        }

        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.Offset16(LayoutCommon.WriteCoverage(graph, marks));
        w.Offset16(LayoutCommon.WriteCoverage(graph, [.. bases.Select(b => glyphs.Map(b.Old))]));
        w.U16(classes.Count);
        w.Offset16(markArray);
        w.Offset16(graph.Add(bw));
        return graph.Add(w);
    }

    private OtObject? MarkToLigature(int t)
    {
        ReadOnlySpan<byte> d = D;
        int classCount = U16(d, t + 6);
        int ligatureArray = Off16(d, t, t + 10);
        (List<int> marks, OtObject? markArray, ClassRemap classes) = MarkArray(t);
        List<(int Old, int Index)> ligatures = LayoutCommon.Covered(d, Off16(d, t, t + 4), glyphs);
        if (marks.Count == 0 || ligatures.Count == 0 || ligatureArray == 0)
        {
            return null;
        }

        int count = U16(d, ligatureArray);
        int[] oldClasses = classes.OldClasses();
        OtWriter lw = OtGraph.Writer();
        lw.U16(ligatures.Count);
        foreach ((_, int index) in ligatures)
        {
            int attach = index < count ? Off16(d, ligatureArray, ligatureArray + 2 + (index * 2)) : 0;
            int components = attach == 0 ? 0 : U16(d, attach);
            if (components == 0)
            {
                lw.Offset16(null);
                continue;
            }

            OtWriter aw = OtGraph.Writer();
            aw.U16(components);
            for (int k = 0; k < components; k++)
            {
                foreach (int c in oldClasses)
                {
                    aw.Offset16(Anchor(Off16(d, attach, attach + 2 + (((k * classCount) + c) * 2)), sound: true));
                }
            }

            lw.Offset16(graph.Add(aw));
        }

        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.Offset16(LayoutCommon.WriteCoverage(graph, marks));
        w.Offset16(LayoutCommon.WriteCoverage(graph, [.. ligatures.Select(l => glyphs.Map(l.Old))]));
        w.U16(classes.Count);
        w.Offset16(markArray);
        w.Offset16(graph.Add(lw));
        return graph.Add(w);
    }

    /// <summary>
    /// The kept marks (new ids), their MarkArray and the classes they keep. A mark past the array's end reads as class 0 with no
    /// anchor, as the shaper reads it.
    /// </summary>
    private (List<int> Marks, OtObject? Array, ClassRemap Classes) MarkArray(int t)
    {
        ReadOnlySpan<byte> d = D;
        int classCount = U16(d, t + 6);
        int markArray = Off16(d, t, t + 8);
        int count = markArray == 0 ? 0 : U16(d, markArray);
        List<(int New, int Class, int Anchor)> marks = [];
        foreach ((int old, int index) in LayoutCommon.Covered(d, Off16(d, t, t + 2), glyphs))
        {
            int record = markArray + 2 + (index * 4);
            bool listed = index < count;
            marks.Add((glyphs.Map(old), listed ? U16(d, record) : 0, listed ? Off16(d, markArray, record + 2) : 0));
        }

        var classes = new ClassRemap(marks.Select(m => m.Class), classCount, keepZero: false);
        if (marks.Count == 0)
        {
            return ([], null, classes);
        }

        OtWriter w = OtGraph.Writer();
        w.U16(marks.Count);
        foreach ((_, int klass, int anchor) in marks)
        {
            w.U16(classes.Map(klass));
            w.Offset16(Anchor(anchor, sound: false));
        }

        return ([.. marks.Select(m => m.New)], graph.Add(w), classes);
    }

    /// <summary>
    /// An anchor as the shaper reads it; null for a null offset, or for an unsound one where the shaper checks soundness (base,
    /// ligature, mark-to-mark and cursive anchors, not a mark's own).
    /// </summary>
    private OtObject? Anchor(int a, bool sound)
    {
        ReadOnlySpan<byte> d = D;
        if (a == 0 || (sound && !AnchorSound(a)))
        {
            return null;
        }

        int format = U16(d, a);
        OtWriter w = OtGraph.Writer();
        switch (format)
        {
            case 2:
                w.U16(2);
                w.U16(U16(d, a + 2));
                w.U16(U16(d, a + 4));
                w.U16(U16(d, a + 6));
                break;
            case 3 when bake is not null:
                w.U16(1);
                w.U16(U16(d, a + 2) + bake.Delta(d, Off16(d, a, a + 6)));
                w.U16(U16(d, a + 4) + bake.Delta(d, Off16(d, a, a + 8)));
                break;
            case 3:
                w.U16(3);
                w.U16(U16(d, a + 2));
                w.U16(U16(d, a + 4));
                w.Offset16(LayoutCommon.Device(graph, d, Off16(d, a, a + 6)));
                w.Offset16(LayoutCommon.Device(graph, d, Off16(d, a, a + 8)));
                break;
            default:
                // Format 1, and any other format, which the shaper reads as (0, 0).
                w.U16(1);
                w.U16(format == 1 ? U16(d, a + 2) : 0);
                w.U16(format == 1 ? U16(d, a + 4) : 0);
                break;
        }

        return graph.Add(w);
    }

    /// <summary>Whether an anchor fits in the table, as the shaper checks base, ligature and cursive anchors.</summary>
    private bool AnchorSound(int anchor)
    {
        ReadOnlySpan<byte> d = D;
        if (anchor <= 0 || anchor + 2 > d.Length)
        {
            return false;
        }

        return U16(d, anchor) switch
        {
            1 => anchor + 6 <= d.Length,
            2 => anchor + 8 <= d.Length,
            3 => anchor + 10 <= d.Length && DeviceSound(anchor, anchor + 6) && DeviceSound(anchor, anchor + 8),
            _ => true,
        };
    }

    private bool DeviceSound(int baseOffset, int at)
    {
        ReadOnlySpan<byte> d = D;
        int off = U16(d, at);
        if (off == 0)
        {
            return true;
        }

        int t = baseOffset + off;
        if (t + 6 > d.Length)
        {
            return false;
        }

        int format = U16(d, t + 4);
        if (format is >= 1 and <= 3)
        {
            int start = U16(d, t), end = U16(d, t + 2);
            int size = start > end ? 6 : 2 * (4 + ((end - start) >> (4 - format)));
            return t + size <= d.Length;
        }

        return true;
    }

    /// <summary>A value record's fields; its device offsets count from <paramref name="baseOffset"/> in the source and the holder in the subset.</summary>
    private void WriteValue(OtWriter w, int format, int baseOffset, int at)
    {
        ReadOnlySpan<byte> d = D;
        if (bake is not null)
        {
            // Each device's delta goes into its value: XPlaDevice into XPlacement, and so on.
            Span<int> values = stackalloc int[4];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((format & (1 << bit)) == 0)
                {
                    continue;
                }

                if (bit < 4)
                {
                    values[bit] += S16(d, at);
                }
                else
                {
                    values[bit - 4] += bake.Delta(d, Off16(d, baseOffset, at));
                }

                at += 2;
            }

            for (int bit = 0; bit < 4; bit++)
            {
                if ((Baked(format) & (1 << bit)) != 0)
                {
                    w.U16(values[bit]);
                }
            }

            return;
        }

        for (int bit = 0; bit < 8; bit++)
        {
            if ((format & (1 << bit)) == 0)
            {
                continue;
            }

            if (bit < 4)
            {
                w.U16(U16(d, at));
            }
            else
            {
                w.Offset16(LayoutCommon.Device(graph, d, Off16(d, baseOffset, at)));
            }

            at += 2;
        }
    }

    /// <summary>A value format as written: with a bake, each device bit becomes its value's bit.</summary>
    private int Baked(int format) => bake is null ? format : (format | (format >> 4)) & 0x0F;

    private static int ValueLength(int format) => System.Numerics.BitOperations.PopCount((uint)format & 0xFF);

    // ---- Writing helpers ----

    /// <summary>A format word, a coverage of <paramref name="covered"/>, a count and an offset per covered glyph; null when empty.</summary>
    private OtObject? WriteCoveredOffsets(int format, List<int> covered, List<OtObject?> targets)
    {
        if (covered.Count == 0)
        {
            return null;
        }

        OtWriter w = OtGraph.Writer();
        w.U16(format);
        w.Offset16(LayoutCommon.WriteCoverage(graph, covered));
        WriteOffsets(w, targets);
        return graph.Add(w);
    }

    /// <summary>A table of a count and 16-bit offsets.</summary>
    private OtObject OffsetList(List<OtObject?> targets)
    {
        OtWriter w = OtGraph.Writer();
        WriteOffsets(w, targets);
        return graph.Add(w);
    }

    private static void WriteOffsets(OtWriter w, List<OtObject?> targets)
    {
        w.U16(targets.Count);
        foreach (OtObject? target in targets)
        {
            w.Offset16(target);
        }
    }
}
