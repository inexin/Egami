// Translated from HarfBuzz's hb-ot-shaper-hebrew.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>
/// The Hebrew shaper: the presentation forms old fonts want for a letter and its point when the font has no GPOS mark feature,
/// and the meteg order fix; GPOS applies only under the 'hebr' script.
/// </summary>
internal sealed class HebrewShaper : ComplexShaper
{
    public static readonly HebrewShaper Instance = new();

    // The dagesh presentation forms of U+05D0 to U+05EA; 0 where none is encoded.
    private static readonly int[] s_dageshForms =
    [
        0xFB30, 0xFB31, 0xFB32, 0xFB33, 0xFB34, 0xFB35, 0xFB36, 0x0000, 0xFB38, 0xFB39, 0xFB3A, 0xFB3B, 0xFB3C, 0x0000, 0xFB3E, 0x0000,
        0xFB40, 0xFB41, 0x0000, 0xFB43, 0xFB44, 0x0000, 0xFB46, 0xFB47, 0xFB48, 0xFB49, 0xFB4A,
    ];

    // HarfBuzz's modified combining classes for patah, qamats, sheva, hiriq and meteg.
    private const int Ccc17 = 20, Ccc18 = 21, Ccc10 = 22, Ccc14 = 23, Ccc22 = 25, Below = 220;

    public override bool HasCompose => true;

    public override bool Compose(NormalizeContext c, int a, int b, out int ab)
    {
        bool found = NormalizeContext.ComposeUnicode(a, b, out ab);
        if (found || c.Plan.HasGposMark)
        {
            return found;
        }

        // Presentation forms excluded from normalization, but wanted by old fonts.
        ab = b switch
        {
            0x05B4 when a == 0x05D9 => 0xFB1D, // Hiriq: yod.
            0x05B7 when a == 0x05F2 => 0xFB1F, // Patah: yiddish yod yod.
            0x05B7 when a == 0x05D0 => 0xFB2E, // Patah: alef.
            0x05B8 when a == 0x05D0 => 0xFB2F, // Qamats: alef.
            0x05B9 when a == 0x05D5 => 0xFB4B, // Holam: vav.
            0x05BC when a is >= 0x05D0 and <= 0x05EA => s_dageshForms[a - 0x05D0], // Dagesh.
            0x05BC when a == 0xFB2A => 0xFB2C, // Dagesh: shin with shin dot.
            0x05BC when a == 0xFB2B => 0xFB2D, // Dagesh: shin with sin dot.
            0x05BF when a == 0x05D1 => 0xFB4C, // Rafe: bet.
            0x05BF when a == 0x05DB => 0xFB4D, // Rafe: kaf.
            0x05BF when a == 0x05E4 => 0xFB4E, // Rafe: pe.
            0x05C1 when a == 0x05E9 => 0xFB2A, // Shin dot: shin.
            0x05C1 when a == 0xFB49 => 0xFB2C, // Shin dot: shin with dagesh.
            0x05C2 when a == 0x05E9 => 0xFB2B, // Sin dot: shin.
            0x05C2 when a == 0xFB49 => 0xFB2D, // Sin dot: shin with dagesh.
            _ => 0,
        };
        return ab != 0;
    }

    public override bool HasReorderMarks => true;

    public override void ReorderMarks(ShapePlan plan, LayoutBuffer buffer, int start, int end)
    {
        // Patah or qamats, then sheva or hiriq, then meteg or a below mark: the meteg goes before the sheva or hiriq.
        GlyphInfo[] info = buffer.Info;
        for (int i = start + 2; i < end; i++)
        {
            int c0 = UnicodeProps.GetModifiedCombiningClass(info[i - 2]);
            int c1 = UnicodeProps.GetModifiedCombiningClass(info[i - 1]);
            int c2 = UnicodeProps.GetModifiedCombiningClass(info[i]);
            if (c0 is Ccc17 or Ccc18 && c1 is Ccc10 or Ccc14 && c2 is Ccc22 or Below)
            {
                buffer.MergeClusters(i - 1, i + 1);
                (info[i - 1], info[i]) = (info[i], info[i - 1]);
                break;
            }
        }
    }

    public override uint GposTag => ShapeMapBuilder.Tag("hebr");
}
