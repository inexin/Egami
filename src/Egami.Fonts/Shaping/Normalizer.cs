// Translated from HarfBuzz's hb-ot-shape-normalize.cc (MIT); see THIRD-PARTY-NOTICES.md.
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>What the normalizer works with: the plan, buffer and font, and the shaper's decompose and compose overrides.</summary>
internal sealed class NormalizeContext
{
    public required ShapePlan Plan;
    public required LayoutBuffer Buffer;
    public required ShapeFont Font;

    public bool Decompose(int ab, out int a, out int b) =>
        Plan.Shaper.HasDecompose ? Plan.Shaper.Decompose(this, ab, out a, out b) : DecomposeUnicode(ab, out a, out b);

    public bool Compose(int a, int b, out int ab) =>
        Plan.Shaper.HasCompose ? Plan.Shaper.Compose(this, a, b, out ab) : ComposeUnicode(a, b, out ab);

    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
    private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>Unicode canonical decomposition, one step: two code points, or one with <paramref name="b"/> 0.</summary>
    public static bool DecomposeUnicode(int ab, out int a, out int b)
    {
        int si = ab - SBase;
        if ((uint)si < SCount)
        {
            if (si % TCount != 0)
            {
                a = SBase + (si / TCount * TCount);
                b = TBase + (si % TCount);
            }
            else
            {
                a = LBase + (si / NCount);
                b = VBase + (si % NCount / TCount);
            }

            return true;
        }

        int row = UnicodeData.FindPair(UnicodeTables.Decompositions, 3, ab);
        if (row < 0)
        {
            a = 0;
            b = 0;
            return false;
        }

        a = UnicodeTables.Decompositions[row + 1];
        b = UnicodeTables.Decompositions[row + 2];
        return true;
    }

    /// <summary>Unicode canonical composition of a pair, excluding composition exclusions.</summary>
    public static bool ComposeUnicode(int a, int b, out int ab)
    {
        if (a >= SBase && a < SBase + SCount && b > TBase && b < TBase + TCount && (a - SBase) % TCount == 0)
        {
            ab = a + (b - TBase);
            return true;
        }

        if (a >= LBase && a < LBase + LCount && b >= VBase && b < VBase + VCount)
        {
            ab = SBase + ((a - LBase) * NCount) + ((b - VBase) * TCount);
            return true;
        }

        ReadOnlySpan<int> table = UnicodeTables.Compositions;
        int lo = 0, hi = (table.Length / 3) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int ma = table[mid * 3], mb = table[(mid * 3) + 1];
            int cmp = a != ma ? a.CompareTo(ma) : b.CompareTo(mb);
            if (cmp < 0)
            {
                hi = mid - 1;
            }
            else if (cmp > 0)
            {
                lo = mid + 1;
            }
            else
            {
                ab = table[(mid * 3) + 2];
                return true;
            }
        }

        ab = 0;
        return false;
    }
}

/// <summary>
/// Normalization against the font's cmap before GSUB: decompose what the font cannot draw composed, reorder marks by combining
/// class, and recompose what the font has, as HarfBuzz's _hb_ot_shape_normalize.
/// </summary>
internal static class Normalizer
{
    private const int MaxCombiningMarks = 32;

    private static void SetGlyph(ref GlyphInfo info, ShapeFont font)
    {
        font.TryGetNominalGlyph((int)info.Codepoint, out uint glyph);
        info.NormalizerGlyph = glyph;
    }

    private static void OutputChar(LayoutBuffer buffer, int unichar, uint glyph)
    {
        buffer.Cur().NormalizerGlyph = glyph;
        buffer.OutputGlyph((uint)unichar);
        UnicodeProps.Set(ref buffer.Prev(), buffer);
    }

    private static void NextChar(LayoutBuffer buffer, uint glyph)
    {
        buffer.Cur().NormalizerGlyph = glyph;
        buffer.NextGlyph();
    }

    private static int Decompose(NormalizeContext c, bool shortest, int ab)
    {
        LayoutBuffer buffer = c.Buffer;
        ShapeFont font = c.Font;
        uint bGlyph = 0;
        if (!c.Decompose(ab, out int a, out int b) || (b != 0 && !font.TryGetNominalGlyph(b, out bGlyph)))
        {
            return 0;
        }

        bool hasA = font.TryGetNominalGlyph(a, out uint aGlyph);
        if (shortest && hasA)
        {
            OutputChar(buffer, a, aGlyph);
            if (b != 0)
            {
                OutputChar(buffer, b, bGlyph);
                return 2;
            }

            return 1;
        }

        int ret = Decompose(c, shortest, a);
        if (ret != 0)
        {
            if (b != 0)
            {
                OutputChar(buffer, b, bGlyph);
                return ret + 1;
            }

            return ret;
        }

        if (hasA)
        {
            OutputChar(buffer, a, aGlyph);
            if (b != 0)
            {
                OutputChar(buffer, b, bGlyph);
                return 2;
            }

            return 1;
        }

        return 0;
    }

    private static void DecomposeCurrentCharacter(NormalizeContext c, bool shortest)
    {
        LayoutBuffer buffer = c.Buffer;
        int u = (int)buffer.Cur().Codepoint;
        uint glyph = buffer.NotFound;
        if (shortest && c.Font.TryGetNominalGlyph(u, out uint g))
        {
            NextChar(buffer, g);
            return;
        }

        if (Decompose(c, shortest, u) != 0)
        {
            buffer.SkipGlyph();
            return;
        }

        if (!shortest && c.Font.TryGetNominalGlyph(u, out g))
        {
            NextChar(buffer, g);
            return;
        }

        if (UnicodeProps.IsUnicodeSpace(buffer.Cur()))
        {
            int spaceType = UnicodeProps.SpaceFallbackType(u);
            if (spaceType != UnicodeProps.NotSpace)
            {
                uint spaceGlyph = 0;
                if (c.Font.TryGetNominalGlyph(0x0020, out spaceGlyph) || (spaceGlyph = buffer.Invisible) != 0)
                {
                    UnicodeProps.SetSpaceFallbackType(ref buffer.Cur(), spaceType);
                    NextChar(buffer, spaceGlyph);
                    buffer.ScratchFlags |= LayoutBuffer.ScratchHasSpaceFallback;
                    return;
                }
            }
        }

        if (u == 0x2011)
        {
            // U+2011 is the only sensible no-break version of another character that is not a space.
            if (c.Font.TryGetNominalGlyph(0x2010, out uint other))
            {
                NextChar(buffer, other);
                return;
            }
        }

        NextChar(buffer, glyph);
    }

    private static void HandleVariationSelectorCluster(NormalizeContext c, int end)
    {
        LayoutBuffer buffer = c.Buffer;
        ShapeFont font = c.Font;
        while (buffer.Idx < end - 1 && buffer.Successful)
        {
            if (UnicodeProps.IsVariationSelector((int)buffer.Cur(1).Codepoint))
            {
                if (font.TryGetVariationGlyph((int)buffer.Cur().Codepoint, (int)buffer.Cur(1).Codepoint, out uint glyph))
                {
                    buffer.Cur().NormalizerGlyph = glyph;
                    uint unicode = buffer.Cur().Codepoint;
                    buffer.ReplaceGlyphs(2, [unicode]);
                }
                else
                {
                    // Pass on the two characters separately; GSUB may handle them.
                    SetGlyph(ref buffer.Cur(), font);
                    buffer.NextGlyph();
                    buffer.ScratchFlags |= LayoutBuffer.ScratchHasVariationSelectorFallback;
                    UnicodeProps.SetVariationSelector(ref buffer.Cur(), true);
                    if (buffer.NotFoundVariationSelector != uint.MaxValue)
                    {
                        UnicodeProps.ClearDefaultIgnorable(ref buffer.Cur());
                    }

                    SetGlyph(ref buffer.Cur(), font);
                    buffer.NextGlyph();
                }

                // Skip any further variation selectors.
                while (buffer.Idx < end && buffer.Successful && UnicodeProps.IsVariationSelector((int)buffer.Cur().Codepoint))
                {
                    SetGlyph(ref buffer.Cur(), font);
                    buffer.NextGlyph();
                }
            }
            else
            {
                SetGlyph(ref buffer.Cur(), font);
                buffer.NextGlyph();
            }
        }

        if (buffer.Idx < end)
        {
            SetGlyph(ref buffer.Cur(), font);
            buffer.NextGlyph();
        }
    }

    private static void DecomposeMultiCharCluster(NormalizeContext c, int end, bool shortCircuit)
    {
        LayoutBuffer buffer = c.Buffer;
        for (int i = buffer.Idx; i < end && buffer.Successful; i++)
        {
            if (UnicodeProps.IsVariationSelector((int)buffer.Info[i].Codepoint))
            {
                HandleVariationSelectorCluster(c, end);
                return;
            }
        }

        while (buffer.Idx < end && buffer.Successful)
        {
            DecomposeCurrentCharacter(c, shortCircuit);
        }
    }

    private static int InfoCc(in GlyphInfo info) => UnicodeProps.GetModifiedCombiningClass(info);

    public static void Normalize(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        if (buffer.Len == 0)
        {
            return;
        }

        NormalizationMode mode = plan.Shaper.NormalizationPreference;
        if (mode == NormalizationMode.Auto)
        {
            mode = NormalizationMode.ComposedDiacritics;
        }

        NormalizeContext c = new() { Plan = plan, Buffer = buffer, Font = font };
        bool alwaysShortCircuit = mode == NormalizationMode.None;
        bool mightShortCircuit = alwaysShortCircuit ||
            (mode != NormalizationMode.Decomposed && mode != NormalizationMode.ComposedDiacriticsNoShortCircuit);

        // First round: decompose.
        bool allSimple = true;
        buffer.ClearOutput();
        int count = buffer.Len;
        buffer.Idx = 0;
        do
        {
            int end;
            for (end = buffer.Idx + 1; end < count; end++)
            {
                if (UnicodeProps.IsUnicodeMark(buffer.Info[end]))
                {
                    break;
                }
            }

            if (end < count)
            {
                end--; // Leave one base for the marks to cluster with.
            }

            // From idx to end are simple clusters.
            if (mightShortCircuit)
            {
                int done = 0;
                while (buffer.Idx + done < end && font.TryGetNominalGlyph((int)buffer.Info[buffer.Idx + done].Codepoint, out uint g))
                {
                    buffer.Info[buffer.Idx + done].NormalizerGlyph = g;
                    done++;
                }

                buffer.NextGlyphs(done);
            }

            while (buffer.Idx < end && buffer.Successful)
            {
                DecomposeCurrentCharacter(c, mightShortCircuit);
            }

            if (buffer.Idx == count || !buffer.Successful)
            {
                break;
            }

            allSimple = false;

            // Find all the marks now.
            for (end = buffer.Idx + 1; end < count; end++)
            {
                if (!UnicodeProps.IsUnicodeMark(buffer.Info[end]))
                {
                    break;
                }
            }

            // Idx to end is one non-simple cluster.
            DecomposeMultiCharCluster(c, end, alwaysShortCircuit);
        }
        while (buffer.Idx < count && buffer.Successful);
        buffer.Sync();

        // Second round: reorder marks by combining class (in place).
        if (!allSimple)
        {
            count = buffer.Len;
            GlyphInfo[] info = buffer.Info;
            for (int i = 0; i < count; i++)
            {
                if (InfoCc(info[i]) == 0)
                {
                    continue;
                }

                int end;
                for (end = i + 1; end < count; end++)
                {
                    if (InfoCc(info[end]) == 0)
                    {
                        break;
                    }
                }

                // Sorting is O(n^2); only short sequences.
                if (end - i > MaxCombiningMarks)
                {
                    i = end;
                    continue;
                }

                buffer.Sort(i, end, static (a, b) => InfoCc(a).CompareTo(InfoCc(b)));
                info = buffer.Info;
                if (plan.Shaper.HasReorderMarks)
                {
                    plan.Shaper.ReorderMarks(plan, buffer, i, end);
                }

                i = end;
            }
        }

        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasCgj) != 0)
        {
            // A CGJ that did not block any reordering becomes skippable.
            count = buffer.Len;
            GlyphInfo[] info = buffer.Info;
            for (int i = 1; i + 1 < count; i++)
            {
                if (info[i].Codepoint == 0x034F && (InfoCc(info[i + 1]) == 0 || InfoCc(info[i - 1]) <= InfoCc(info[i + 1])))
                {
                    UnicodeProps.Unhide(ref info[i]);
                }
            }
        }

        // Third round: recompose.
        if (!allSimple && buffer.Successful &&
            mode is NormalizationMode.ComposedDiacritics or NormalizationMode.ComposedDiacriticsNoShortCircuit)
        {
            buffer.ClearOutput();
            count = buffer.Len;
            int starter = 0;
            buffer.NextGlyph();
            while (buffer.Idx < count)
            {
                if (UnicodeProps.IsUnicodeMark(buffer.Cur()) &&
                    (starter == buffer.OutLen - 1 || InfoCc(buffer.Prev()) < InfoCc(buffer.Cur())) &&
                    c.Compose((int)buffer.OutInfo[starter].Codepoint, (int)buffer.Cur().Codepoint, out int composed) &&
                    font.TryGetNominalGlyph(composed, out uint glyph))
                {
                    // Composes.
                    buffer.NextGlyph();
                    buffer.MergeOutClusters(starter, buffer.OutLen);
                    buffer.OutLen--;
                    buffer.OutInfo[starter].Codepoint = (uint)composed;
                    buffer.OutInfo[starter].NormalizerGlyph = glyph;
                    UnicodeProps.Set(ref buffer.OutInfo[starter], buffer);
                    continue;
                }

                // Blocked, or does not compose.
                buffer.NextGlyph();
                if (InfoCc(buffer.Prev()) == 0)
                {
                    starter = buffer.OutLen - 1;
                }
            }

            buffer.Sync();
        }
    }
}
