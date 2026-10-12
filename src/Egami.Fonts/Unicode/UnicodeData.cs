using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Fonts.Unicode;

/// <summary>
/// The Unicode character properties text shaping needs, from Egami's own tables generated from the Unicode Character Database
/// (version <see cref="UnicodeVersion"/>), so results never move with the .NET runtime's Unicode version.
/// </summary>
/// <remarks>Every lookup is a few array reads and allocates nothing. Code points must be in the range 0 to 0x10FFFF; lone
/// surrogates are code points too (general category <see cref="UnicodeCategory.Surrogate"/>).</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="properties"/>
/// </example>
public static class UnicodeData
{
    /// <summary>The highest code point.</summary>
    internal const int MaxCodePoint = 0x10FFFF;

    /// <summary>Gets the Unicode version of the tables: 17.0.0.</summary>
    public static Version UnicodeVersion => UnicodeTables.Version;

    /// <summary>Gets a code point's General_Category.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The category; <see cref="UnicodeCategory.OtherNotAssigned"/> for unassigned code points.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static UnicodeCategory GetGeneralCategory(int codePoint) => (UnicodeCategory)UnicodeTables.RecordGeneralCategory[Record(Check(codePoint))];

    /// <summary>Gets a code point's Script (UAX #24).</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The script; <see cref="Script.Common"/> or <see cref="Script.Inherited"/> for characters shared by scripts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static Script GetScript(int codePoint) => new(UnicodeTables.RecordScript[Record(Check(codePoint))]);

    /// <summary>Gets a code point's Script_Extensions: the scripts it is used with.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The scripts, in code order; just <see cref="GetScript(int)"/> when the code point has no extensions.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static ReadOnlySpan<Script> GetScriptExtensions(int codePoint) => MemoryMarshal.Cast<byte, Script>(ScriptExtensions(Check(codePoint)));

    /// <summary>Gets a code point's Bidi_Class (UAX #9).</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The class, with the default classes of unassigned code points in right-to-left blocks.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static BidiClass GetBidiClass(int codePoint) => BidiClassOf(Check(codePoint));

    /// <summary>Gets a code point's Bidi_Mirroring_Glyph: the character that draws its mirror image in right-to-left text.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <param name="mirror">The mirroring character, or <paramref name="codePoint"/> when there is none.</param>
    /// <returns><see langword="true"/> when the code point has a mirroring character.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool TryGetBidiMirroringGlyph(int codePoint, out int mirror)
    {
        int index = FindPair(UnicodeTables.Mirroring, 2, Check(codePoint));
        mirror = index < 0 ? codePoint : UnicodeTables.Mirroring[index + 1];
        return index >= 0;
    }

    /// <summary>Gets a code point's Bidi_Paired_Bracket_Type.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>Whether the code point opens or closes a bracket pair, or neither.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static BidiPairedBracketType GetBidiPairedBracketType(int codePoint)
    {
        int index = FindPair(UnicodeTables.Brackets, 3, Check(codePoint));
        return index < 0 ? BidiPairedBracketType.None : (BidiPairedBracketType)UnicodeTables.Brackets[index + 2];
    }

    /// <summary>Gets a code point's Bidi_Paired_Bracket: the other bracket of its pair.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The matching bracket, or <paramref name="codePoint"/> when it is not a paired bracket.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static int GetBidiPairedBracket(int codePoint)
    {
        int index = FindPair(UnicodeTables.Brackets, 3, Check(codePoint));
        return index < 0 ? codePoint : UnicodeTables.Brackets[index + 1];
    }

    /// <summary>Gets a code point's Joining_Type, with marks and format characters transparent.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The joining type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static JoiningType GetJoiningType(int codePoint) => (JoiningType)UnicodeTables.RecordJoiningType[Record(Check(codePoint))];

    /// <summary>Gets a code point's Joining_Group.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The joining group.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static JoiningGroup GetJoiningGroup(int codePoint) => (JoiningGroup)UnicodeTables.RecordJoiningGroup[Record(Check(codePoint))];

    /// <summary>Gets a code point's Canonical_Combining_Class.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The class, 0 to 254; 0 for starters.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static int GetCanonicalCombiningClass(int codePoint) => UnicodeTables.RecordCombiningClass[Record(Check(codePoint))];

    /// <summary>Gets a code point's Indic_Syllabic_Category.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The category.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static IndicSyllabicCategory GetIndicSyllabicCategory(int codePoint) => (IndicSyllabicCategory)UnicodeTables.RecordIndicSyllabicCategory[Record(Check(codePoint))];

    /// <summary>Gets a code point's Indic_Positional_Category.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The category.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static IndicPositionalCategory GetIndicPositionalCategory(int codePoint) => (IndicPositionalCategory)UnicodeTables.RecordIndicPositionalCategory[Record(Check(codePoint))];

    /// <summary>Gets a code point's Vertical_Orientation (UAX #50).</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The orientation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static VerticalOrientation GetVerticalOrientation(int codePoint) => (VerticalOrientation)UnicodeTables.RecordVerticalOrientation[Record(Check(codePoint))];

    /// <summary>Gets a code point's East_Asian_Width (UAX #11).</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns>The width class.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static EastAsianWidth GetEastAsianWidth(int codePoint) => (EastAsianWidth)UnicodeTables.RecordEastAsianWidth[Record(Check(codePoint))];

    /// <summary>Gets whether a code point has the Emoji property (UTS #51).</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns><see langword="true"/> for emoji characters, which include digits, # and *.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool IsEmoji(int codePoint) => HasFlag(Check(codePoint), UnicodeTables.EmojiFlag);

    /// <summary>Gets whether a code point has the Emoji_Presentation property: drawn as emoji by default.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns><see langword="true"/> for default emoji presentation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool IsEmojiPresentation(int codePoint) => HasFlag(Check(codePoint), UnicodeTables.EmojiPresentationFlag);

    /// <summary>Gets whether a code point has the Emoji_Modifier property (the skin tone modifiers).</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns><see langword="true"/> for emoji modifiers.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool IsEmojiModifier(int codePoint) => HasFlag(Check(codePoint), UnicodeTables.EmojiModifierFlag);

    /// <summary>Gets whether a code point has the Emoji_Modifier_Base property: it takes a skin tone modifier.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns><see langword="true"/> for emoji modifier bases.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool IsEmojiModifierBase(int codePoint) => HasFlag(Check(codePoint), UnicodeTables.EmojiModifierBaseFlag);

    /// <summary>Gets whether a code point has the Emoji_Component property: it appears inside emoji sequences.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns><see langword="true"/> for emoji components.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool IsEmojiComponent(int codePoint) => HasFlag(Check(codePoint), UnicodeTables.EmojiComponentFlag);

    /// <summary>Gets whether a code point has the Extended_Pictographic property, which UAX #29 uses for emoji sequences.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns><see langword="true"/> for pictographs, including ones not yet assigned.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool IsExtendedPictographic(int codePoint) => HasFlag(Check(codePoint), UnicodeTables.ExtendedPictographicFlag);

    /// <summary>Gets whether a code point has the Default_Ignorable_Code_Point property: drawn as nothing unless a font asks.</summary>
    /// <param name="codePoint">A code point, 0 to 0x10FFFF.</param>
    /// <returns><see langword="true"/> for default ignorables such as ZWJ, variation selectors and bidi controls.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codePoint"/> is outside 0 to 0x10FFFF.</exception>
    public static bool IsDefaultIgnorable(int codePoint) => HasFlag(Check(codePoint), UnicodeTables.DefaultIgnorableFlag);

    // ---- Unchecked lookups for Egami's own algorithms; the code point must be valid. ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Record(int codePoint)
    {
        int block = UnicodeTables.Stage1[codePoint >> UnicodeTables.Shift];
        return UnicodeTables.Stage2[(block << UnicodeTables.Shift) | (codePoint & ((1 << UnicodeTables.Shift) - 1))];
    }

    internal static BidiClass BidiClassOf(int codePoint) => (BidiClass)UnicodeTables.RecordBidiClass[Record(codePoint)];

    internal static int ScriptIndex(int codePoint) => UnicodeTables.RecordScript[Record(codePoint)];

    internal static GraphemeClusterBreak GraphemeBreakOf(int codePoint) => (GraphemeClusterBreak)UnicodeTables.RecordGraphemeClusterBreak[Record(codePoint)];

    internal static WordBreak WordBreakOf(int codePoint) => (WordBreak)UnicodeTables.RecordWordBreak[Record(codePoint)];

    internal static IndicConjunctBreak IndicConjunctBreakOf(int codePoint) => (IndicConjunctBreak)UnicodeTables.RecordIndicConjunctBreak[Record(codePoint)];

    internal static bool HasFlag(int codePoint, byte flag) => (UnicodeTables.RecordFlags[Record(codePoint)] & flag) != 0;

    internal static int CombiningClassOf(int codePoint) => UnicodeTables.RecordCombiningClass[Record(codePoint)];

    internal static UnicodeCategory CategoryOf(int codePoint) => (UnicodeCategory)UnicodeTables.RecordGeneralCategory[Record(codePoint)];

    /// <summary>The script indexes of a code point's Script_Extensions, or its one script.</summary>
    internal static ReadOnlySpan<byte> ScriptExtensions(int codePoint)
    {
        ReadOnlySpan<int> ranges = UnicodeTables.ScriptExtensionRanges;
        int lo = 0;
        int hi = (ranges.Length / 3) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (codePoint < ranges[mid * 3])
            {
                hi = mid - 1;
            }
            else if (codePoint > ranges[(mid * 3) + 1])
            {
                lo = mid + 1;
            }
            else
            {
                int set = ranges[(mid * 3) + 2];
                int start = UnicodeTables.ScriptExtensionSetStarts[set];
                return UnicodeTables.ScriptExtensionSets[start..UnicodeTables.ScriptExtensionSetStarts[set + 1]];
            }
        }

        return Script.IndexSpan(ScriptIndex(codePoint));
    }

    /// <summary>Finds a code point in a table of fixed-width rows sorted by their first value.</summary>
    /// <returns>The row's first index, or -1.</returns>
    internal static int FindPair(ReadOnlySpan<int> table, int width, int codePoint)
    {
        int lo = 0;
        int hi = (table.Length / width) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int value = table[mid * width];
            if (value == codePoint)
            {
                return mid * width;
            }

            if (value < codePoint)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private static int Check(int codePoint)
    {
        if ((uint)codePoint > MaxCodePoint)
        {
            ThrowOutOfRange(codePoint);
        }

        return codePoint;
    }

    private static void ThrowOutOfRange(int codePoint) =>
        throw new ArgumentOutOfRangeException(nameof(codePoint), codePoint, "A code point is in the range 0 to 0x10FFFF.");
}

/// <summary>A code point's Bidi_Paired_Bracket_Type.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="properties"/>
/// </example>
public enum BidiPairedBracketType
{
    /// <summary>Not a paired bracket.</summary>
    None = 0,

    /// <summary>An opening bracket.</summary>
    Open = 1,

    /// <summary>A closing bracket.</summary>
    Close = 2,
}
