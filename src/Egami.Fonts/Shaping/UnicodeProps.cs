// Translated from HarfBuzz's hb-ot-layout.hh, hb-unicode.hh and hb-unicode.cc (MIT); see THIRD-PARTY-NOTICES.md.
using System.Globalization;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// The per-character Unicode facts the shaper keeps in <see cref="GlyphInfo.UnicodeProps"/>: a general category in HarfBuzz's
/// numbering (alphabetical by code, Cc first), the ignorable, hidden and continuation bits, and the modified combining class or
/// a joiner or variation selector marker in the high byte.
/// </summary>
internal static class UnicodeProps
{
    public const int MaskGenCat = 0x001F;
    public const int MaskIgnorable = 0x0020;
    public const int MaskHidden = 0x0040;
    public const int MaskContinuation = 0x0080;
    public const int MaskCfZwj = 0x0100;
    public const int MaskCfZwnj = 0x0200;
    public const int MaskCfVs = 0x0400;

    // General categories, in HarfBuzz's order.
    public const int Control = 0;
    public const int Format = 1;
    public const int Unassigned = 2;
    public const int PrivateUse = 3;
    public const int Surrogate = 4;
    public const int LowercaseLetter = 5;
    public const int ModifierLetter = 6;
    public const int OtherLetter = 7;
    public const int TitlecaseLetter = 8;
    public const int UppercaseLetter = 9;
    public const int SpacingMark = 10;
    public const int EnclosingMark = 11;
    public const int NonspacingMark = 12;
    public const int DecimalNumber = 13;
    public const int LetterNumber = 14;
    public const int OtherNumber = 15;
    public const int ConnectPunctuation = 16;
    public const int DashPunctuation = 17;
    public const int ClosePunctuation = 18;
    public const int FinalPunctuation = 19;
    public const int InitialPunctuation = 20;
    public const int OtherPunctuation = 21;
    public const int OpenPunctuation = 22;
    public const int CurrencySymbol = 23;
    public const int ModifierSymbol = 24;
    public const int MathSymbol = 25;
    public const int OtherSymbol = 26;
    public const int LineSeparator = 27;
    public const int ParagraphSeparator = 28;
    public const int SpaceSeparator = 29;

    // UnicodeCategory order to HarfBuzz's.
    private static readonly byte[] s_fromCategory =
    [
        UppercaseLetter, LowercaseLetter, TitlecaseLetter, ModifierLetter, OtherLetter, NonspacingMark, SpacingMark, EnclosingMark,
        DecimalNumber, LetterNumber, OtherNumber, SpaceSeparator, LineSeparator, ParagraphSeparator, Control, Format, Surrogate,
        PrivateUse, ConnectPunctuation, DashPunctuation, OpenPunctuation, ClosePunctuation, InitialPunctuation, FinalPunctuation,
        OtherPunctuation, MathSymbol, CurrencySymbol, ModifierSymbol, OtherSymbol, Unassigned,
    ];

    // HarfBuzz's modified combining classes: Hebrew, Arabic, Syriac, Telugu, Thai, Lao and Tibetan classes reordered.
    private static readonly byte[] s_modifiedClass =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 22, 15, 16, 17, 23, 18, 19, 20, 21, 14, 24, 12, 25, 13, 10, 11, 26, 28, 29, 30, 31, 32, 33, 27,
        34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64,
        65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 4, 85, 86, 87, 88, 89, 90, 5, 92, 93, 94, 95, 96,
        97, 98, 99, 100, 101, 102, 3, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121,
        122, 123, 124, 125, 126, 127, 128, 129, 132, 131, 131, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145,
        146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 169,
        170, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193,
        194, 195, 196, 197, 198, 199, 200, 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217,
        218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 241,
        242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255,
    ];

    /// <summary>A code point's general category in HarfBuzz's numbering.</summary>
    public static int Category(int codePoint) => s_fromCategory[(int)UnicodeData.CategoryOf(codePoint)];

    public static bool IsMarkCategory(int gc) => gc is SpacingMark or EnclosingMark or NonspacingMark;

    public static bool IsLetterCategory(int gc) => gc is LowercaseLetter or ModifierLetter or OtherLetter or TitlecaseLetter or UppercaseLetter;

    /// <summary>HarfBuzz's modified combining class.</summary>
    public static int ModifiedCombiningClass(int codePoint)
    {
        // SAKOT after any tone marks; PADMA after any vowel marks; TSA -PHRU before U+0F74.
        if (codePoint == 0x1A60)
        {
            return 254;
        }

        if (codePoint == 0x0FC6)
        {
            return 254;
        }

        if (codePoint == 0x0F39)
        {
            return 127;
        }

        return s_modifiedClass[UnicodeData.CombiningClassOf(codePoint)];
    }

    /// <summary>HarfBuzz's default-ignorable set, which hides fewer characters than Unicode's Default_Ignorable_Code_Point.</summary>
    public static bool IsDefaultIgnorableCodePoint(int ch)
    {
        int plane = ch >> 16;
        if (plane == 0)
        {
            return (ch >> 8) switch
            {
                0x00 => ch == 0x00AD,
                0x03 => ch == 0x034F,
                0x06 => ch == 0x061C,
                0x17 => ch is >= 0x17B4 and <= 0x17B5,
                0x18 => ch is >= 0x180B and <= 0x180F,
                0x20 => ch is (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F),
                0xFE => ch is (>= 0xFE00 and <= 0xFE0F) or 0xFEFF,
                0xFF => ch is >= 0xFFF0 and <= 0xFFF8,
                _ => false,
            };
        }

        return plane switch
        {
            0x01 => ch is >= 0x1D173 and <= 0x1D17A,
            0x0E => ch is >= 0xE0000 and <= 0xE0FFF,
            _ => false,
        };
    }

    public static bool IsVariationSelector(int cp) => cp is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);

    /// <summary>Sets a character's props, as HarfBuzz's _hb_glyph_info_set_unicode_props.</summary>
    public static void Set(ref GlyphInfo info, LayoutBuffer buffer)
    {
        int u = (int)info.Codepoint;
        int genCat = Category(u);
        int props = genCat;
        if (u >= 0x80)
        {
            if (IsDefaultIgnorableCodePoint(u))
            {
                buffer.ScratchFlags |= LayoutBuffer.ScratchHasDefaultIgnorables;
                props |= MaskIgnorable;
                if (u == 0x200C)
                {
                    props |= MaskCfZwnj;
                }
                else if (u == 0x200D)
                {
                    props |= MaskCfZwj;
                }
                else if (u is (>= 0x180B and <= 0x180D) or 0x180F)
                {
                    props |= MaskHidden;
                }
                else if (u is >= 0xE0020 and <= 0xE007F)
                {
                    props |= MaskHidden;
                }
                else if (u == 0x034F)
                {
                    buffer.ScratchFlags |= LayoutBuffer.ScratchHasCgj;
                    props |= MaskHidden;
                }
            }

            if (IsMarkCategory(genCat))
            {
                buffer.ScratchFlags |= LayoutBuffer.ScratchHasContinuations;
                props |= MaskContinuation;
                props |= ModifiedCombiningClass(u) << 8;
            }
        }

        info.UnicodeProps = (ushort)props;
    }

    public static int GeneralCategory(in GlyphInfo info) => info.UnicodeProps & MaskGenCat;

    public static void SetGeneralCategory(ref GlyphInfo info, int genCat) =>
        info.UnicodeProps = (ushort)(genCat | (info.UnicodeProps & (0xFF & ~MaskGenCat)));

    public static bool IsUnicodeMark(in GlyphInfo info) => IsMarkCategory(info.UnicodeProps & MaskGenCat);

    public static void SetModifiedCombiningClass(ref GlyphInfo info, int modifiedClass)
    {
        if (!IsUnicodeMark(info))
        {
            return;
        }

        info.UnicodeProps = (ushort)((modifiedClass << 8) | (info.UnicodeProps & 0xFF));
    }

    public static int GetModifiedCombiningClass(in GlyphInfo info) => IsUnicodeMark(info) ? info.UnicodeProps >> 8 : 0;

    public static bool IsUnicodeSpace(in GlyphInfo info) => GeneralCategory(info) == SpaceSeparator;

    public static void SetSpaceFallbackType(ref GlyphInfo info, int type)
    {
        if (!IsUnicodeSpace(info))
        {
            return;
        }

        info.UnicodeProps = (ushort)((type << 8) | (info.UnicodeProps & 0xFF));
    }

    public static int GetSpaceFallbackType(in GlyphInfo info) => IsUnicodeSpace(info) ? info.UnicodeProps >> 8 : 0;

    public static bool IsVariationSelector(in GlyphInfo info) => GeneralCategory(info) == Format && (info.UnicodeProps & MaskCfVs) != 0;

    public static void SetVariationSelector(ref GlyphInfo info, bool customize)
    {
        if (customize)
        {
            SetGeneralCategory(ref info, Format);
            info.UnicodeProps |= MaskCfVs;
        }
        else
        {
            SetGeneralCategory(ref info, NonspacingMark);
        }
    }

    public static bool IsDefaultIgnorable(in GlyphInfo info) =>
        (info.UnicodeProps & MaskIgnorable) != 0 && (info.GlyphProps & GlyphProps.Substituted) == 0;

    public static void SetDefaultIgnorable(ref GlyphInfo info) => info.UnicodeProps |= MaskIgnorable;

    public static void ClearDefaultIgnorable(ref GlyphInfo info) => info.UnicodeProps &= unchecked((ushort)~MaskIgnorable);

    public static bool IsHidden(in GlyphInfo info) => (info.UnicodeProps & MaskHidden) != 0;

    public static void Unhide(ref GlyphInfo info) => info.UnicodeProps &= unchecked((ushort)~MaskHidden);

    public static void SetContinuation(ref GlyphInfo info, LayoutBuffer buffer)
    {
        buffer.ScratchFlags |= LayoutBuffer.ScratchHasContinuations;
        info.UnicodeProps |= MaskContinuation;
    }

    public static void ClearContinuation(ref GlyphInfo info) => info.UnicodeProps &= unchecked((ushort)~MaskContinuation);

    public static bool IsContinuation(in GlyphInfo info) => (info.UnicodeProps & MaskContinuation) != 0;

    public static bool IsUnicodeFormat(in GlyphInfo info) => GeneralCategory(info) == Format;

    public static bool IsZwnj(in GlyphInfo info) => IsUnicodeFormat(info) && (info.UnicodeProps & MaskCfZwnj) != 0;

    public static bool IsZwj(in GlyphInfo info) => IsUnicodeFormat(info) && (info.UnicodeProps & MaskCfZwj) != 0;

    /// <summary>The space fallback widths of HarfBuzz's space_fallback_type.</summary>
    public const int NotSpace = 0;
    public const int SpaceEm = 1;
    public const int SpaceEm2 = 2;
    public const int SpaceEm3 = 3;
    public const int SpaceEm4 = 4;
    public const int SpaceEm5 = 5;
    public const int SpaceEm6 = 6;
    public const int SpaceEm16 = 16;
    public const int Space4Em18 = 17;
    public const int Space = 18;
    public const int SpaceFigure = 19;
    public const int SpacePunctuation = 20;
    public const int SpaceNarrow = 21;

    public static int SpaceFallbackType(int u) => u switch
    {
        0x0020 => Space,
        0x00A0 => Space,
        0x2000 => SpaceEm2,
        0x2001 => SpaceEm,
        0x2002 => SpaceEm2,
        0x2003 => SpaceEm,
        0x2004 => SpaceEm3,
        0x2005 => SpaceEm4,
        0x2006 => SpaceEm6,
        0x2007 => SpaceFigure,
        0x2008 => SpacePunctuation,
        0x2009 => SpaceEm5,
        0x200A => SpaceEm16,
        0x202F => SpaceNarrow,
        0x205F => Space4Em18,
        0x3000 => SpaceEm,
        _ => NotSpace,
    };

    /// <summary>A general category from the .NET enum, in HarfBuzz's numbering.</summary>
    public static int FromCategory(UnicodeCategory category) => s_fromCategory[(int)category];
}
