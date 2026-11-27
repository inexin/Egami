using System.Runtime.CompilerServices;

using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Layout;

/// <summary>
/// A face's ideographic em box in horizontal lines, from its BASE table (OpenType BASE, the horizontal axis' <c>ideo</c> and
/// <c>idtp</c> baselines, as CSS Inline 3 reads the ideographic baselines): its under and over edges in font units above the
/// alphabetic baseline. A face without them has none, and <see cref="Edges"/> centres the em between the ascent and descent.
/// </summary>
internal sealed class IdeographicEmBox
{
    private static readonly ConditionalWeakTable<Typeface, IdeographicEmBox> s_boxes = new();

    private static readonly uint[] Scripts = [new FontTag("hani").Value, new FontTag("kana").Value, new FontTag("DFLT").Value];

    private IdeographicEmBox(Typeface face)
    {
        // Reads past a truncated table give 0; an em box that is not one (over at or under under) is ignored.
        if (face.TryGetTable(new FontTag("BASE"), out ReadOnlyMemory<byte> table) && table.Length >= 8 && U16(table.Span, 0) == 1)
        {
            Read(table.Span, face.UnitsPerEm);
            if (Over <= Under)
            {
                Under = Over = null;
            }
        }
    }

    /// <summary>The em box's under edge (the <c>ideo</c> baseline) in font units above the alphabetic baseline, or null.</summary>
    public int? Under { get; private set; }

    /// <summary>The em box's over edge (<c>idtp</c>, else <c>ideo</c> plus the em) in font units, or null.</summary>
    public int? Over { get; private set; }

    public static IdeographicEmBox For(Typeface face) => s_boxes.GetValue(face.Root, f => new IdeographicEmBox(f));

    /// <summary>
    /// A font's ideographic em box in horizontal lines: its over edge above the alphabetic baseline and its under edge below it,
    /// in pixels. Without BASE baselines the em is centred between the rounded ascent and descent, as browsers do.
    /// </summary>
    public static (float Over, float Under) Edges(Font font)
    {
        IdeographicEmBox box = For(font.Typeface);
        if (box.Under is int under && box.Over is int over)
        {
            return (over * font.Scale, -under * font.Scale);
        }

        (float a, float d, _) = LineBuilder.Rounded(font);
        float centre = (a - d) / 2;
        return (centre + (font.Size / 2), (font.Size / 2) - centre);
    }

    private void Read(ReadOnlySpan<byte> d, int unitsPerEm)
    {
        int axis = U16(d, 4);
        if (axis == 0)
        {
            return;
        }

        int tagList = U16(d, axis), scriptList = U16(d, axis + 2);
        if (tagList == 0 || scriptList == 0)
        {
            return;
        }

        tagList += axis;
        scriptList += axis;
        int tags = U16(d, tagList);
        int ideo = -1, idtp = -1;
        for (int i = 0; i < tags; i++)
        {
            uint tag = Tag(d, tagList + 2 + (4 * i));
            ideo = tag == new FontTag("ideo").Value ? i : ideo;
            idtp = tag == new FontTag("idtp").Value ? i : idtp;
        }

        if (ideo < 0)
        {
            return;
        }

        // The first of hani, kana and DFLT with baseline values, else the first script that has them.
        int records = U16(d, scriptList);
        int values = -1;
        foreach (uint wanted in Scripts)
        {
            for (int i = 0; i < records && values < 0; i++)
            {
                int record = scriptList + 2 + (6 * i);
                if (Tag(d, record) == wanted)
                {
                    values = BaseValues(d, scriptList + U16(d, record + 4));
                }
            }
        }

        for (int i = 0; i < records && values < 0; i++)
        {
            values = BaseValues(d, scriptList + U16(d, scriptList + 2 + (6 * i) + 4));
        }

        if (values < 0 || U16(d, values + 2) <= ideo)
        {
            return;
        }

        // Every BaseCoord format starts with its format and coordinate.
        int under = S16(d, values + U16(d, values + 4 + (2 * ideo)) + 2);
        Under = under;
        Over = idtp >= 0 && idtp < U16(d, values + 2) ? S16(d, values + U16(d, values + 4 + (2 * idtp)) + 2) : under + unitsPerEm;
    }

    /// <summary>A BaseScript table's BaseValues table offset, or -1 when it has none.</summary>
    private static int BaseValues(ReadOnlySpan<byte> d, int script)
    {
        int offset = U16(d, script);
        return offset == 0 ? -1 : script + offset;
    }
}
