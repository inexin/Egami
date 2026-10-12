// Translated from HarfBuzz's hb-ot-shaper-syllabic.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>What the syllable-based shapers share, as HarfBuzz's hb-ot-shaper-syllabic.</summary>
internal static class Syllabic
{
    /// <summary>
    /// Inserts a dotted circle at the start of each broken syllable (after a repha, if the shaper has one), as HarfBuzz's
    /// hb_syllabic_insert_dotted_circles; the circle takes the glyph's place before GSUB, so it carries no Unicode properties.
    /// </summary>
    public static bool InsertDottedCircles(ShapeFont font, LayoutBuffer buffer, int brokenSyllableType, byte dottedCircleCategory,
        int rephaCategory = -1, int dottedCirclePosition = -1)
    {
        if (buffer.DoNotInsertDottedCircle || (buffer.ScratchFlags & LayoutBuffer.ScratchHasBrokenSyllable) == 0)
        {
            return false;
        }

        if (!font.TryGetNominalGlyph(0x25CC, out uint dottedCircleGlyph))
        {
            return false;
        }

        GlyphInfo dottedCircle = new()
        {
            Codepoint = dottedCircleGlyph,
            ShaperCategory = dottedCircleCategory,
        };
        if (dottedCirclePosition != -1)
        {
            dottedCircle.ShaperPosition = (byte)dottedCirclePosition;
        }

        buffer.ClearOutput();
        buffer.Idx = 0;
        int lastSyllable = 0;
        while (buffer.Idx < buffer.Len && buffer.Successful)
        {
            int syllable = buffer.Cur().Syllable;
            if (lastSyllable != syllable && (syllable & 0x0F) == brokenSyllableType)
            {
                lastSyllable = syllable;
                GlyphInfo info = dottedCircle;
                info.Cluster = buffer.Cur().Cluster;
                info.Mask = buffer.Cur().Mask;
                info.Syllable = buffer.Cur().Syllable;

                if (rephaCategory != -1)
                {
                    while (buffer.Idx < buffer.Len && buffer.Successful && lastSyllable == buffer.Cur().Syllable &&
                        buffer.Cur().ShaperCategory == rephaCategory)
                    {
                        buffer.NextGlyph();
                    }
                }

                buffer.OutputInfo(info);
            }
            else
            {
                buffer.NextGlyph();
            }
        }

        buffer.Sync();
        return true;
    }

    /// <summary>A GSUB pause that ends the shaper's use of the syllable field; Egami keeps it, as nothing else reuses it.</summary>
    public static bool ClearSyllables(ShapePlan plan, ShapeFont font, LayoutBuffer buffer) => false;
}
