// Translated from the sanitize methods of HarfBuzz's layout tables (MIT); see THIRD-PARTY-NOTICES.md.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

/// <summary>
/// Checks a GSUB, GPOS or GDEF table structure by structure before use, as HarfBuzz's sanitizer does: any structure that runs
/// past the table or breaks a rule fails the whole table, which is then ignored, so a damaged font shapes as if it had no such
/// table, and the engine never meets a structure it cannot read. GPOS anchors and value records are checked lazily when used,
/// as HarfBuzz does.
/// </summary>
internal sealed class LayoutSanitizer
{
    private const int MaxOpsFactor = 64;
    private const int MaxOpsMin = 16384;
    private const int MaxOpsMax = 0x3FFFFFFF;
    private const int MaxSubtables = 0x4000;
    private const int MaxNesting = 64;

    private readonly byte[] _data;
    private readonly int _length;
    private readonly bool _gpos;
    private long _ops;
    private int _subtables;
    private int _depth;

    private LayoutSanitizer(ReadOnlySpan<byte> data, bool gpos)
    {
        _data = data.ToArray();
        _length = data.Length;
        _gpos = gpos;
        long ops = (long)_length * MaxOpsFactor;
        _ops = Math.Clamp(ops, MaxOpsMin, MaxOpsMax);
    }

    private ReadOnlySpan<byte> D => _data;

    /// <summary>Whether a GSUB (<paramref name="gpos"/> false) or GPOS table is sound.</summary>
    public static bool SanitizeLayout(ReadOnlySpan<byte> data, bool gpos) => new LayoutSanitizer(data, gpos).Layout();

    /// <summary>Whether a GDEF table is sound.</summary>
    public static bool SanitizeGdef(ReadOnlySpan<byte> data) => new LayoutSanitizer(data, false).Gdef();

    // ---- Primitives ----

    private bool Point(long p) => p >= 0 && p <= _length;

    private bool Struct(int p, int minSize) => p >= 0 && Point((long)p + minSize);

    private bool Range(long p, long len)
    {
        if (p < 0 || p > _length || len < 0 || _length - p < len)
        {
            return false;
        }

        _ops -= len;
        return _ops > 0;
    }

    private bool Ops(long count)
    {
        if (_ops < 0 || count >= _ops)
        {
            _ops = -1;
            return false;
        }

        _ops -= count;
        return true;
    }

    /// <summary>A 16-bit offset field at <paramref name="at"/> from <paramref name="b"/>: null passes, else the target must pass.</summary>
    private bool Off16(int b, int at, Func<int, bool> target)
    {
        if (!Struct(at, 2))
        {
            return false;
        }

        int off = U16(D, at);
        return off == 0 || target(b + off);
    }

    private bool Off24(int b, int at, Func<int, bool> target)
    {
        if (!Struct(at, 3))
        {
            return false;
        }

        int off = (int)U24(D, at);
        return off == 0 || target(b + off);
    }

    private bool Off32(int b, int at, Func<int, bool> target)
    {
        if (!Struct(at, 4))
        {
            return false;
        }

        uint off = U32(D, at);
        return off == 0 || (b + (long)off <= int.MaxValue && target(b + (int)off));
    }

    /// <summary>An array of 16-bit count then <paramref name="itemSize"/>-byte items.</summary>
    private bool Array16(int p, int itemSize) => Struct(p, 2) && Range(p + 2, (long)U16(D, p) * itemSize);

    /// <summary>An array of 16-bit offsets from <paramref name="b"/>, each to a structure that must pass.</summary>
    private bool Array16Of16(int p, int b, Func<int, bool> target)
    {
        if (!Array16(p, 2))
        {
            return false;
        }

        int count = U16(D, p);
        for (int i = 0; i < count; i++)
        {
            if (!Off16(b, p + 2 + (i * 2), target))
            {
                return false;
            }
        }

        return true;
    }

    // ---- Common tables ----

    private bool Coverage(int t)
    {
        if (!Struct(t, 2))
        {
            return false;
        }

        return U16(D, t) switch
        {
            1 => Array16(t + 2, 2),
            2 => Array16(t + 2, 6),
            _ => true,
        };
    }

    private int CoveragePopulation(int t)
    {
        switch (U16(D, t))
        {
            case 1:
                return U16(D, t + 2);
            case 2:
                {
                    int count = U16(D, t + 2), total = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int r = t + 4 + (i * 6);
                        int start = U16(D, r), end = U16(D, r + 2);
                        if (end >= start)
                        {
                            total += end - start + 1;
                        }
                    }

                    return total;
                }

            default:
                return 0;
        }
    }

    private bool ClassDef(int t)
    {
        if (!Struct(t, 2))
        {
            return false;
        }

        return U16(D, t) switch
        {
            1 => Struct(t, 6) && Array16(t + 4, 2),
            2 => Array16(t + 2, 6),
            _ => true,
        };
    }

    private bool Device(int t)
    {
        if (!Struct(t + 4, 2))
        {
            return false;
        }

        int format = U16(D, t + 4);
        switch (format)
        {
            case 1:
            case 2:
            case 3:
                {
                    if (!Struct(t, 6))
                    {
                        return false;
                    }

                    int start = U16(D, t), end = U16(D, t + 2);
                    int size = start > end ? 6 : 2 * (4 + ((end - start) >> (4 - format)));
                    return Range(t, size);
                }

            case 0x8000:
                return Struct(t, 6);
            default:
                return true;
        }
    }

    private bool LangSys(int t) => Struct(t, 6) && Array16(t + 4, 2);

    private bool Script(int t) =>
        Off16(t, t, LangSys) && RecordArray(t + 2, t, (record, _) => Off16(t, record + 4, LangSys));

    private bool RecordArray(int p, int b, Func<int, uint, bool> record)
    {
        if (!Array16(p, 6))
        {
            return false;
        }

        int count = U16(D, p);
        for (int i = 0; i < count; i++)
        {
            int r = p + 2 + (i * 6);
            if (!Struct(r, 6) || !record(r, U32(D, r)))
            {
                return false;
            }
        }

        return true;
    }

    private bool FeatureParams(int t, uint tag)
    {
        if (tag == Tag("size"))
        {
            if (!Struct(t, 10))
            {
                return false;
            }

            int designSize = U16(D, t), subfamilyId = U16(D, t + 2), nameId = U16(D, t + 4), rangeStart = U16(D, t + 6), rangeEnd = U16(D, t + 8);
            if (designSize == 0)
            {
                return false;
            }

            if (subfamilyId == 0 && nameId == 0 && rangeStart == 0 && rangeEnd == 0)
            {
                return true;
            }

            return !(designSize < rangeStart || designSize > rangeEnd || nameId < 256 || nameId > 32767);
        }

        if ((tag & 0xFFFF0000) == (Tag("ss  ") & 0xFFFF0000))
        {
            return Struct(t, 4);
        }

        if ((tag & 0xFFFF0000) == (Tag("cv  ") & 0xFFFF0000))
        {
            return Struct(t, 14) && Struct(t + 12, 2) && Range(t + 14, (long)U16(D, t + 12) * 3);
        }

        return true;
    }

    private bool Feature(int t, uint tag) => Struct(t, 4) && Off16(t, t, p => FeatureParams(p, tag)) && Array16(t + 2, 2);

    private static uint Tag(string s) => ShapeMapBuilder.Tag(s);

    // ---- GSUB and GPOS ----

    private bool Layout()
    {
        if (!Struct(0, 4))
        {
            return false;
        }

        int major = U16(D, 0), minor = U16(D, 2);
        if (major != 1)
        {
            return true;
        }

        if (!Struct(0, 10))
        {
            return false;
        }

        bool ok = Off16(0, 4, list => RecordArray(list, list, (record, _) => Off16(list, record + 4, Script))) &&
            Off16(0, 6, list => RecordArray(list, list, (record, tag) => Off16(list, record + 4, f => Feature(f, tag)))) &&
            Off16(0, 8, list => Array16Of16(list, list, Lookup));
        if (!ok)
        {
            return false;
        }

        return minor < 1 || Off32(0, 10, FeatureVariations);
    }

    private bool Lookup(int t)
    {
        if (!Struct(t, 6) || !Array16(t + 4, 2))
        {
            return false;
        }

        int type = U16(D, t);
        int flag = U16(D, t + 2);
        int count = U16(D, t + 4);
        _subtables += count;
        if (_subtables >= MaxSubtables)
        {
            return false;
        }

        if ((flag & LookupFlag.UseMarkFilteringSet) != 0 && !Struct(t + 6 + (count * 2), 2))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!Off16(t, t + 6 + (i * 2), s => Subtable(type, s)))
            {
                return false;
            }
        }

        int extension = _gpos ? 9 : 7;
        if (type == extension && count > 0)
        {
            int first = ExtensionType(t + U16(D, t + 6));
            for (int i = 1; i < count; i++)
            {
                if (ExtensionType(t + U16(D, t + 6 + (i * 2))) != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private int ExtensionType(int sub) => U16(D, sub) == 1 ? U16(D, sub + 2) : 0;

    private bool Subtable(int type, int t)
    {
        if (!Struct(t, 2))
        {
            return false;
        }

        return _gpos ? GposSubtable(type, t) : GsubSubtable(type, t);
    }

    private bool Extension(int t, Func<int, int, bool> subtable)
    {
        if (U16(D, t) != 1)
        {
            return true;
        }

        int extension = _gpos ? 9 : 7;
        if (!Struct(t, 8) || U16(D, t + 2) == extension)
        {
            return false;
        }

        int type = U16(D, t + 2);
        return Off32(t, t + 4, s => Struct(s, 2) && subtable(type, s));
    }

    private bool GsubSubtable(int type, int t)
    {
        int format = U16(D, t);
        switch (type)
        {
            case 1:
                if (format == 1)
                {
                    return Struct(t, 6) && Off16(t, t + 2, Coverage) && Ops(CoveragePopulation(t + U16(D, t + 2)) >> 1);
                }

                return format != 2 || (Off16(t, t + 2, Coverage) && Array16(t + 4, 2));
            case 2:
                return format != 1 || (Off16(t, t + 2, Coverage) && Array16Of16(t + 4, t, s => Array16(s, 2)));
            case 3:
                return format != 1 || (Off16(t, t + 2, Coverage) && Array16Of16(t + 4, t, s => Array16(s, 2)));
            case 4:
                return format != 1 || (Off16(t, t + 2, Coverage) && Array16Of16(t + 4, t, set => Array16Of16(set, set, Ligature)));
            case 5:
                return Context(t);
            case 6:
                return ChainContext(t);
            case 7:
                return Extension(t, GsubSubtable);
            case 8:
                return format != 1 || ReverseChain(t);
            default:
                return true;
        }
    }

    private bool Ligature(int t)
    {
        if (!Struct(t, 4))
        {
            return false;
        }

        int count = U16(D, t + 2);
        return Range(t + 4, count == 0 ? 0 : (long)(count - 1) * 2);
    }

    private bool ReverseChain(int t)
    {
        if (!Off16(t, t + 2, Coverage) || !Array16Of16(t + 4, t, Coverage))
        {
            return false;
        }

        int lookahead = t + 6 + (U16(D, t + 4) * 2);
        if (!Array16Of16(lookahead, t, Coverage))
        {
            return false;
        }

        int substitute = lookahead + 2 + (U16(D, lookahead) * 2);
        return Array16(substitute, 2);
    }

    private bool Rule(int t)
    {
        if (!Struct(t, 4))
        {
            return false;
        }

        int inputCount = U16(D, t), lookupCount = U16(D, t + 2);
        return Range(t + 4, (2L * (inputCount > 0 ? inputCount - 1 : 0)) + (4L * lookupCount));
    }

    private bool ChainRule(int t)
    {
        if (!Struct(t, 2))
        {
            return false;
        }

        int input = t + 2 + (U16(D, t) * 2);
        if (!Struct(input, 2))
        {
            return false;
        }

        int inputCount = U16(D, input);
        int lookahead = input + 2 + ((inputCount > 0 ? inputCount - 1 : 0) * 2);
        if (!Struct(lookahead, 2))
        {
            return false;
        }

        int lookup = lookahead + 2 + (U16(D, lookahead) * 2);
        return Array16(lookup, 4);
    }

    private bool Context(int t)
    {
        switch (U16(D, t))
        {
            case 1:
                return Off16(t, t + 2, Coverage) && Array16Of16(t + 4, t, set => Array16Of16(set, set, Rule));
            case 2:
                return Off16(t, t + 2, Coverage) && Off16(t, t + 4, ClassDef) && Array16Of16(t + 6, t, set => Array16Of16(set, set, Rule));
            case 3:
                {
                    if (!Struct(t, 6))
                    {
                        return false;
                    }

                    int glyphCount = U16(D, t + 2), lookupCount = U16(D, t + 4);
                    if (glyphCount == 0 || !Range(t + 6, glyphCount * 2L))
                    {
                        return false;
                    }

                    for (int i = 0; i < glyphCount; i++)
                    {
                        if (!Off16(t, t + 6 + (i * 2), Coverage))
                        {
                            return false;
                        }
                    }

                    return Range(t + 6 + (glyphCount * 2), lookupCount * 4L);
                }

            default:
                return true;
        }
    }

    private bool ChainContext(int t)
    {
        switch (U16(D, t))
        {
            case 1:
                return Off16(t, t + 2, Coverage) && Array16Of16(t + 4, t, set => Array16Of16(set, set, ChainRule));
            case 2:
                return Off16(t, t + 2, Coverage) && Off16(t, t + 4, ClassDef) && Off16(t, t + 6, ClassDef) && Off16(t, t + 8, ClassDef) &&
                    Array16Of16(t + 10, t, set => Array16Of16(set, set, ChainRule));
            case 3:
                {
                    int backtrack = t + 2;
                    if (!Array16Of16(backtrack, t, Coverage))
                    {
                        return false;
                    }

                    int input = backtrack + 2 + (U16(D, backtrack) * 2);
                    if (!Array16Of16(input, t, Coverage) || U16(D, input) == 0)
                    {
                        return false;
                    }

                    int lookahead = input + 2 + (U16(D, input) * 2);
                    if (!Array16Of16(lookahead, t, Coverage))
                    {
                        return false;
                    }

                    int lookup = lookahead + 2 + (U16(D, lookahead) * 2);
                    return Array16(lookup, 4);
                }

            default:
                return true;
        }
    }

    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)format & 0xFF);

    private bool GposSubtable(int type, int t)
    {
        int format = U16(D, t);
        switch (type)
        {
            case 1:
                if (format == 1)
                {
                    return Struct(t, 6) && Off16(t, t + 2, Coverage) && Ops(CoveragePopulation(t + U16(D, t + 2)) >> 1) &&
                        Range(t + 6, ValueSize(U16(D, t + 4)));
                }

                return format != 2 || (Struct(t, 8) && Off16(t, t + 2, Coverage) && Range(t + 8, (long)U16(D, t + 6) * ValueSize(U16(D, t + 4))));
            case 2:
                if (format == 1)
                {
                    if (!Struct(t, 10))
                    {
                        return false;
                    }

                    int stride = 2 + ValueSize(U16(D, t + 4)) + ValueSize(U16(D, t + 6));
                    return Off16(t, t + 2, Coverage) && Array16Of16(t + 8, t, set => Struct(set, 2) && Range(set + 2, (long)U16(D, set) * stride));
                }

                if (format == 2)
                {
                    if (!Struct(t, 16) || !Off16(t, t + 2, Coverage) || !Off16(t, t + 8, ClassDef) || !Off16(t, t + 10, ClassDef))
                    {
                        return false;
                    }

                    int stride = ValueSize(U16(D, t + 4)) + ValueSize(U16(D, t + 6));
                    return Range(t + 16, (long)U16(D, t + 12) * U16(D, t + 14) * stride);
                }

                return true;
            case 3:
                return format != 1 || (Off16(t, t + 2, Coverage) && Array16(t + 4, 4));
            case 4:
            case 6:
                return format != 1 || (Struct(t, 12) && Off16(t, t + 2, Coverage) && Off16(t, t + 4, Coverage) &&
                    Off16(t, t + 8, MarkArray) && Off16(t, t + 10, m => AnchorMatrix(m, U16(D, t + 6))));
            case 5:
                return format != 1 || (Struct(t, 12) && Off16(t, t + 2, Coverage) && Off16(t, t + 4, Coverage) &&
                    Off16(t, t + 8, MarkArray) && Off16(t, t + 10, a => Array16Of16(a, a, m => AnchorMatrix(m, U16(D, t + 6)))));
            case 7:
                return Context(t);
            case 8:
                return ChainContext(t);
            case 9:
                return Extension(t, GposSubtable);
            default:
                return true;
        }
    }

    private bool MarkArray(int t)
    {
        if (!Array16(t, 4))
        {
            return false;
        }

        int count = U16(D, t);
        for (int i = 0; i < count; i++)
        {
            int record = t + 2 + (i * 4);
            if (!Struct(record, 4) || !Off16(t, record + 2, Anchor))
            {
                return false;
            }
        }

        return true;
    }

    private bool AnchorMatrix(int t, int cols)
    {
        // Rows of column offsets; the anchors are checked when used.
        return Struct(t, 2) && Range(t + 2, (long)U16(D, t) * cols * 2);
    }

    /// <summary>Whether an anchor is sound, as HarfBuzz checks one before using it.</summary>
    public bool Anchor(int t)
    {
        if (!Struct(t, 2))
        {
            return false;
        }

        return U16(D, t) switch
        {
            1 => Struct(t, 6),
            2 => Struct(t, 8),
            3 => Struct(t, 10) && Off16(t, t + 6, Device) && Off16(t, t + 8, Device),
            _ => true,
        };
    }

    // ---- Feature variations ----

    private bool FeatureVariations(int t)
    {
        if (!Struct(t, 8) || U16(D, t) != 1)
        {
            return false;
        }

        uint count = U32(D, t + 4);
        if (!Range(t + 8, count * 8L))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            int record = t + 8 + (i * 8);
            if (!Off32(t, record, ConditionSet) || !Off32(t, record + 4, FeatureSubstitution))
            {
                return false;
            }
        }

        return true;
    }

    private bool ConditionSet(int t)
    {
        if (!Array16(t, 4))
        {
            return false;
        }

        int count = U16(D, t);
        for (int i = 0; i < count; i++)
        {
            if (!Off32(t, t + 2 + (i * 4), Condition))
            {
                return false;
            }
        }

        return true;
    }

    private bool Condition(int t)
    {
        if (_depth >= MaxNesting || !Struct(t, 2))
        {
            return false;
        }

        _depth++;
        bool ok;
        switch (U16(D, t))
        {
            case 1:
                ok = Struct(t, 8);
                break;
            case 2:
                ok = Struct(t, 8);
                break;
            case 3:
            case 4:
                {
                    ok = Struct(t, 3) && Range(t + 3, d(t) * 3L);
                    for (int i = 0; ok && i < d(t); i++)
                    {
                        ok = Off24(t, t + 3 + (i * 3), Condition);
                    }

                    break;
                }

            case 5:
                ok = Struct(t, 5) && Off24(t, t + 2, Condition);
                break;
            default:
                ok = true;
                break;
        }

        _depth--;
        return ok;

        int d(int at) => D[at + 2];
    }

    private bool FeatureSubstitution(int t)
    {
        if (!Struct(t, 4) || U16(D, t) != 1 || !Array16(t + 4, 6))
        {
            return false;
        }

        int count = U16(D, t + 4);
        for (int i = 0; i < count; i++)
        {
            int record = t + 6 + (i * 6);
            if (!Struct(record, 6) || !Off32(t, record + 2, f => Feature(f, 0)))
            {
                return false;
            }
        }

        return true;
    }

    // ---- GDEF ----

    private bool Gdef()
    {
        if (!Struct(0, 4))
        {
            return false;
        }

        int major = U16(D, 0), minor = U16(D, 2);
        if (major != 1)
        {
            return true;
        }

        bool ok = Off16(0, 4, ClassDef) &&
            Off16(0, 6, t => Off16(t, t, Coverage) && Array16Of16(t + 2, t, p => Array16(p, 2))) &&
            Off16(0, 8, t => Off16(t, t, Coverage) && Array16Of16(t + 2, t, lig => Array16Of16(lig, lig, CaretValue))) &&
            Off16(0, 10, ClassDef);
        if (!ok)
        {
            return false;
        }

        if (minor >= 2 && !Off16(0, 12, MarkGlyphSets))
        {
            return false;
        }

        return minor < 3 || Off32(0, 14, VariationStore);
    }

    private bool CaretValue(int t)
    {
        if (!Struct(t, 2))
        {
            return false;
        }

        return U16(D, t) switch
        {
            1 or 2 => Struct(t, 4),
            3 => Struct(t, 6) && Off16(t, t + 4, Device),
            _ => true,
        };
    }

    private bool MarkGlyphSets(int t)
    {
        if (!Struct(t, 2) || U16(D, t) != 1)
        {
            return Struct(t, 2);
        }

        if (!Struct(t, 4) || !Range(t + 4, U16(D, t + 2) * 4L))
        {
            return false;
        }

        int count = U16(D, t + 2);
        for (int i = 0; i < count; i++)
        {
            if (!Off32(t, t + 4 + (i * 4), Coverage))
            {
                return false;
            }
        }

        return true;
    }

    private bool VariationStore(int t)
    {
        if (!Struct(t, 8) || U16(D, t) != 1)
        {
            return false;
        }

        bool regions = Off32(t, t + 2, r => Struct(r, 4) && Range(r + 4, (long)U16(D, r) * U16(D, r + 2) * 6));
        if (!regions || !Struct(t, 8) || !Range(t + 8, U16(D, t + 6) * 4L))
        {
            return false;
        }

        int count = U16(D, t + 6);
        for (int i = 0; i < count; i++)
        {
            if (!Off32(t, t + 8 + (i * 4), VarData))
            {
                return false;
            }
        }

        return true;
    }

    private bool VarData(int t)
    {
        if (!Struct(t, 6) || !Array16(t + 4, 2))
        {
            return false;
        }

        int itemCount = U16(D, t);
        int wordSizeCount = U16(D, t + 2);
        bool longWords = (wordSizeCount & 0x8000) != 0;
        int wordCount = wordSizeCount & 0x7FFF;
        int regionCount = U16(D, t + 4);
        if (wordCount > regionCount)
        {
            return false;
        }

        long rowSize = longWords ? (4L * wordCount) + (2L * (regionCount - wordCount)) : (2L * wordCount) + (regionCount - wordCount);
        return Range(t + 6 + (regionCount * 2), itemCount * rowSize);
    }
}
