namespace Egami.Fonts.Layout;

/// <summary>
/// How text may break into lines, as CSS Text 3 sets it: <c>word-break</c>, <c>overflow-wrap</c>, <c>line-break</c> and the
/// content language. The default is CSS's default: Unicode's line breaking algorithm (UAX #14) with breaks before small kana,
/// as browsers break.
/// </summary>
/// <param name="WordBreak">Whether letters break like ideographs (<see cref="WordBreak.BreakAll"/>) or ideographs keep
/// together like words (<see cref="WordBreak.KeepAll"/>).</param>
/// <param name="OverflowWrap">Whether a piece too wide for its line may break between any two grapheme clusters; line filling
/// uses it, <see cref="LineBreaks.Enumerate"/> does not.</param>
/// <param name="Strictness">How strictly Chinese and Japanese punctuation, small kana and iteration marks keep with their
/// neighbours, or <see cref="LineBreakStrictness.Anywhere"/> to break between any two grapheme clusters.</param>
/// <param name="Language">The BCP 47 language of the text, such as <c>ja</c>; Chinese and Japanese turn on CSS's
/// language-specific strictness rules.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
/// </example>
public readonly record struct LineBreakOptions(
    WordBreak WordBreak = WordBreak.Normal,
    OverflowWrap OverflowWrap = OverflowWrap.Normal,
    LineBreakStrictness Strictness = LineBreakStrictness.Auto,
    string? Language = null);

/// <summary>Where breaks may fall inside words (CSS <c>word-break</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
/// </example>
public enum WordBreak
{
    /// <summary>Unicode's rules: words break at spaces and punctuation, ideographs between any two.</summary>
    Normal = 0,

    /// <summary>Letters and digits of any script break like ideographs, between any two.</summary>
    BreakAll = 1,

    /// <summary>Ideographs, kana and Hangul keep together like the letters of a word, as Korean text with spaces wants.</summary>
    KeepAll = 2,
}

/// <summary>Whether a piece wider than its line may break anywhere (CSS <c>overflow-wrap</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
/// </example>
public enum OverflowWrap
{
    /// <summary>An unbreakable piece overflows its line.</summary>
    Normal = 0,

    /// <summary>
    /// An overflowing piece breaks between grapheme clusters, and the narrowest piece the text can be broken into counts that
    /// way too.
    /// </summary>
    Anywhere = 1,

    /// <summary>An overflowing piece breaks between grapheme clusters; the narrowest width still counts whole pieces.</summary>
    BreakWord = 2,
}

/// <summary>
/// How strictly line breaks follow Chinese and Japanese rules (CSS <c>line-break</c>): breaks before small kana, hyphens,
/// iteration marks and some punctuation.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
/// </example>
public enum LineBreakStrictness
{
    /// <summary>CSS's default, the same as <see cref="Normal"/>, as browsers break.</summary>
    Auto = 0,

    /// <summary>The most lenient rules: also breaks before iteration marks, centred punctuation, suffixes and between
    /// inseparable characters in Chinese and Japanese.</summary>
    Loose = 1,

    /// <summary>The common rules: breaks before small kana, and before the hyphens U+2010, U+2013, U+301C and U+30A0 in Chinese
    /// and Japanese.</summary>
    Normal = 2,

    /// <summary>
    /// The strictest rules, which are UAX #14's own: never breaks before small kana (class CJ) or the hyphens <see cref="Normal"/>
    /// lets start a line.
    /// </summary>
    Strict = 3,

    /// <summary>A break between any two grapheme clusters, ignoring every rule except the hard breaks.</summary>
    Anywhere = 4,
}

/// <summary>A place where a line may or must end.</summary>
/// <param name="Index">The UTF-16 index the next line would start at.</param>
/// <param name="IsMandatory">Whether the line must end here: after a hard line break (LF, CR, CR LF, NEL, VT, FF, U+2028 or
/// U+2029) or at the end of text after one.</param>
/// <param name="IsSoftHyphen">Whether the break follows a soft hyphen (U+00AD), which shows a hyphen when the line ends
/// there.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
/// </example>
public readonly record struct LineBreak(int Index, bool IsMandatory, bool IsSoftHyphen);
