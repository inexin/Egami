using System.Buffers;
using System.Runtime.CompilerServices;

using Egami.Fonts.Layout;
using Egami.Fonts.Layout.WordBreaking;

namespace Egami.Fonts.Unicode;

/// <summary>A range of UTF-16 units: a grapheme cluster, a word or a run.</summary>
/// <param name="Start">The index of the first UTF-16 unit.</param>
/// <param name="Length">The number of UTF-16 units.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="segments"/>
/// </example>
public readonly record struct TextSegment(int Start, int Length)
{
    /// <summary>Gets the index just past the segment.</summary>
    public int End => Start + Length;
}

/// <summary>
/// Text boundaries from UAX #29: extended grapheme clusters (what a reader sees as one character) and words, with Unicode's default
/// rules, or for words optionally with word dictionaries for Thai, Lao, Khmer and Myanmar.
/// </summary>
/// <remarks>Unpaired surrogates count as one character each. Enumerators walk the text once and allocate nothing.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="segments"/>
/// </example>
public static class TextSegmenter
{
    /// <summary>Enumerates the extended grapheme clusters of a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>An enumerator of the clusters in order.</returns>
    public static GraphemeEnumerator Graphemes(ReadOnlySpan<char> text) => new(text);

    /// <summary>
    /// Enumerates the segments between word boundaries: words, and the spaces, punctuation and symbols between them, each as its
    /// own segment.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>An enumerator of the segments in order.</returns>
    public static WordEnumerator Words(ReadOnlySpan<char> text) => new(text);

    /// <summary>
    /// Enumerates the segments between word boundaries as <see cref="Words(ReadOnlySpan{char})"/> does, except inside a run of
    /// complex-context (line breaking class SA) letters of one script, such as Thai, Lao, Khmer or Myanmar, which has no
    /// spaces between words: there a boundary falls only where the script's dictionary finds a word end, as ICU and browsers
    /// segment words. As in ICU's word rules these letters count as letters, so a word joins the digits and other letters
    /// next to it. A run of a script with no dictionary is one segment.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="dictionaries">The word dictionaries: <see langword="null"/> for <see cref="WordDictionaries.BuiltIn"/>, or
    /// <see cref="WordDictionaries.None"/> to keep each run whole.</param>
    /// <returns>An enumerator of the segments in order.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="dictionary-words"/>
    /// </example>
    public static DictionaryWordEnumerator Words(ReadOnlySpan<char> text, WordDictionaries? dictionaries)
    {
        WordDictionaries set = dictionaries ?? WordDictionaries.BuiltIn;
        return new DictionaryWordEnumerator(text, set.IsEmpty ? null : set);
    }

    /// <summary>Gets whether an index is an extended grapheme cluster boundary.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">An index from 0 to the text's length.</param>
    /// <returns><see langword="true"/> at the start and end of the text and between clusters.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or past the end.</exception>
    public static bool IsGraphemeBoundary(ReadOnlySpan<char> text, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, text.Length);
        if (index == 0 || index == text.Length)
        {
            return true;
        }

        // A cluster never continues past a control, CR or LF except CR LF, so the walk can start just after the last one.
        int start = index - 1;
        while (start > 0)
        {
            int cp = CodePointBefore(text, start, out int size);
            GraphemeClusterBreak g = UnicodeData.GraphemeBreakOf(cp);
            if (g is GraphemeClusterBreak.Control or GraphemeClusterBreak.LF || (g == GraphemeClusterBreak.CR && text[start] != '\n'))
            {
                break;
            }

            start -= size;
        }

        int boundary = Math.Max(start, 0);
        while (boundary < index)
        {
            boundary = NextGraphemeBoundary(text, boundary);
        }

        return boundary == index;
    }

    /// <summary>The next grapheme cluster boundary after <paramref name="start"/>, which must be a boundary.</summary>
    internal static int NextGraphemeBoundary(ReadOnlySpan<char> text, int start)
    {
        if (start >= text.Length)
        {
            return text.Length;
        }

        int cp = CodePointAt(text, start, out int size);
        GraphemeClusterBreak prev = UnicodeData.GraphemeBreakOf(cp);
        int regional = prev == GraphemeClusterBreak.RegionalIndicator ? 1 : 0;
        EmojiState emoji = UnicodeData.HasFlag(cp, UnicodeTables.ExtendedPictographicFlag) ? EmojiState.Pictographic : EmojiState.None;
        ConjunctState conjunct = UnicodeData.IndicConjunctBreakOf(cp) == IndicConjunctBreak.Consonant ? ConjunctState.Consonant : ConjunctState.None;
        int pos = start + size;
        while (pos < text.Length)
        {
            cp = CodePointAt(text, pos, out size);
            GraphemeClusterBreak cur = UnicodeData.GraphemeBreakOf(cp);
            IndicConjunctBreak incb = UnicodeData.IndicConjunctBreakOf(cp);
            bool pictographic = UnicodeData.HasFlag(cp, UnicodeTables.ExtendedPictographicFlag);
            if (IsGraphemeBreak(prev, cur, regional, emoji, conjunct, incb, pictographic))
            {
                return pos;
            }

            regional = cur == GraphemeClusterBreak.RegionalIndicator ? regional + 1 : 0;
            emoji = pictographic ? EmojiState.Pictographic
                : emoji == EmojiState.Pictographic && cur == GraphemeClusterBreak.Extend ? EmojiState.Pictographic
                : emoji == EmojiState.Pictographic && cur == GraphemeClusterBreak.ZWJ ? EmojiState.PictographicZwj
                : EmojiState.None;
            conjunct = incb switch
            {
                IndicConjunctBreak.Consonant => ConjunctState.Consonant,
                IndicConjunctBreak.Linker when conjunct != ConjunctState.None => ConjunctState.ConsonantLinker,
                IndicConjunctBreak.Extend => conjunct,
                _ => ConjunctState.None,
            };
            prev = cur;
            pos += size;
        }

        return text.Length;
    }

    /// <summary>
    /// The next word boundary after <paramref name="start"/>, which must be a boundary; <paramref name="state"/> holds the walk over
    /// every code point before <paramref name="start"/> (default at the start of the text) and is carried to the boundary returned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int NextWordBoundary(ReadOnlySpan<char> text, int start, ref WordState state) =>
        NextWordBoundary<Uax29Words>(text, start, ref state);

    /// <inheritdoc cref="NextWordBoundary(ReadOnlySpan{char}, int, ref WordState)"/>
    /// <typeparam name="TClasses">Whether complex-context letters walk as ALetter; a struct, so each walk compiles on its own.</typeparam>
    internal static int NextWordBoundary<TClasses>(ReadOnlySpan<char> text, int start, ref WordState state)
        where TClasses : struct, IWordClasses
    {
        if (start >= text.Length)
        {
            return text.Length;
        }

        int pos = start + Advance<TClasses>(ref state, text, start);
        while (pos < text.Length)
        {
            int cp = CodePointAt(text, pos, out int size);
            if (IsWordBreak<TClasses>(text, pos + size, state, WordBreakOf<TClasses>(cp), UnicodeData.HasFlag(cp, UnicodeTables.ExtendedPictographicFlag)))
            {
                return pos;
            }

            pos += Advance<TClasses>(ref state, text, pos);
        }

        return text.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CodePointAt(ReadOnlySpan<char> text, int index, out int size)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            size = 2;
            return char.ConvertToUtf32(c, text[index + 1]);
        }

        size = 1;
        return c;
    }

    internal static int CodePointBefore(ReadOnlySpan<char> text, int index, out int size)
    {
        char c = text[index - 1];
        if (char.IsLowSurrogate(c) && index >= 2 && char.IsHighSurrogate(text[index - 2]))
        {
            size = 2;
            return char.ConvertToUtf32(text[index - 2], c);
        }

        size = 1;
        return c;
    }

    private static bool IsGraphemeBreak(GraphemeClusterBreak prev, GraphemeClusterBreak cur, int regional, EmojiState emoji, ConjunctState conjunct,
        IndicConjunctBreak incb, bool pictographic)
    {
        if (prev == GraphemeClusterBreak.CR && cur == GraphemeClusterBreak.LF)
        {
            return false; // GB3
        }

        if (prev is GraphemeClusterBreak.Control or GraphemeClusterBreak.CR or GraphemeClusterBreak.LF
            || cur is GraphemeClusterBreak.Control or GraphemeClusterBreak.CR or GraphemeClusterBreak.LF)
        {
            return true; // GB4, GB5
        }

        if (prev == GraphemeClusterBreak.L && cur is GraphemeClusterBreak.L or GraphemeClusterBreak.V or GraphemeClusterBreak.LV or GraphemeClusterBreak.LVT)
        {
            return false; // GB6
        }

        if (prev is GraphemeClusterBreak.LV or GraphemeClusterBreak.V && cur is GraphemeClusterBreak.V or GraphemeClusterBreak.T)
        {
            return false; // GB7
        }

        if (prev is GraphemeClusterBreak.LVT or GraphemeClusterBreak.T && cur == GraphemeClusterBreak.T)
        {
            return false; // GB8
        }

        if (cur is GraphemeClusterBreak.Extend or GraphemeClusterBreak.ZWJ or GraphemeClusterBreak.SpacingMark || prev == GraphemeClusterBreak.Prepend)
        {
            return false; // GB9, GB9a, GB9b
        }

        if (conjunct == ConjunctState.ConsonantLinker && incb == IndicConjunctBreak.Consonant)
        {
            return false; // GB9c
        }

        if (emoji == EmojiState.PictographicZwj && pictographic)
        {
            return false; // GB11
        }

        return !(prev == GraphemeClusterBreak.RegionalIndicator && cur == GraphemeClusterBreak.RegionalIndicator && (regional & 1) == 1); // GB12, GB13, GB999
    }

    /// <summary>
    /// A code point's word break class; with <see cref="IcuWords"/>, complex-context letters (Other in UAX #29) are ALetter, as
    /// in ICU's word rules, so a run of them stays one word for the dictionaries to split.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WordBreak WordBreakOf<TClasses>(int cp)
        where TClasses : struct, IWordClasses
    {
        WordBreak w = UnicodeData.WordBreakOf(cp);
        return TClasses.ComplexAsLetter && w == WordBreak.Other && cp >= 0x0E01 && UnicodeData.LineBreakClassOf(cp) == LineBreakClass.ComplexContext ? WordBreak.ALetter : w;
    }

    private static bool IsIgnorable(WordBreak w) => w is WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ;

    private static bool IsAHLetter(WordBreak w) => w is WordBreak.ALetter or WordBreak.HebrewLetter;

    private static bool IsMidNumLetQ(WordBreak w) => w is WordBreak.MidNumLet or WordBreak.SingleQuote;

    /// <summary>Takes the code point at <paramref name="index"/> into the walk's state (rule WB4 folds ignorables into what precedes them).</summary>
    /// <returns>The code point's size in UTF-16 units.</returns>
    private static int Advance<TClasses>(ref WordState state, ReadOnlySpan<char> text, int index)
        where TClasses : struct, IWordClasses
    {
        WordBreak cur = WordBreakOf<TClasses>(CodePointAt(text, index, out int size));
        if (IsIgnorable(cur) && state.HasEffective && state.Effective is not (WordBreak.CR or WordBreak.LF or WordBreak.Newline))
        {
            state = state with { Raw = cur };
        }
        else
        {
            int regional = cur == WordBreak.RegionalIndicator ? state.Regional + 1 : 0;
            state = new WordState(cur, cur, state.HasEffective ? state.Effective : WordBreak.Other, true, regional);
        }

        return size;
    }

    private static bool IsWordBreak<TClasses>(ReadOnlySpan<char> text, int afterCurrent, in WordState state, WordBreak cur, bool pictographic)
        where TClasses : struct, IWordClasses
    {
        WordBreak raw = state.Raw;
        if (raw == WordBreak.CR && cur == WordBreak.LF)
        {
            return false; // WB3
        }

        if (raw is WordBreak.Newline or WordBreak.CR or WordBreak.LF || cur is WordBreak.Newline or WordBreak.CR or WordBreak.LF)
        {
            return true; // WB3a, WB3b
        }

        if (raw == WordBreak.ZWJ && pictographic)
        {
            return false; // WB3c
        }

        if (raw == WordBreak.WSegSpace && cur == WordBreak.WSegSpace)
        {
            return false; // WB3d
        }

        if (IsIgnorable(cur))
        {
            return false; // WB4
        }

        WordBreak p1 = state.Effective;
        WordBreak p2 = state.Previous;
        if (IsAHLetter(p1) && IsAHLetter(cur))
        {
            return false; // WB5
        }

        if (IsAHLetter(p1) && (cur == WordBreak.MidLetter || IsMidNumLetQ(cur)) && IsAHLetter(NextEffective<TClasses>(text, afterCurrent)))
        {
            return false; // WB6
        }

        if (IsAHLetter(p2) && (p1 == WordBreak.MidLetter || IsMidNumLetQ(p1)) && IsAHLetter(cur))
        {
            return false; // WB7
        }

        if (p1 == WordBreak.HebrewLetter && cur == WordBreak.SingleQuote)
        {
            return false; // WB7a
        }

        if (p1 == WordBreak.HebrewLetter && cur == WordBreak.DoubleQuote && NextEffective<TClasses>(text, afterCurrent) == WordBreak.HebrewLetter)
        {
            return false; // WB7b
        }

        if (p2 == WordBreak.HebrewLetter && p1 == WordBreak.DoubleQuote && cur == WordBreak.HebrewLetter)
        {
            return false; // WB7c
        }

        if ((p1 == WordBreak.Numeric || IsAHLetter(p1)) && cur == WordBreak.Numeric || p1 == WordBreak.Numeric && IsAHLetter(cur))
        {
            return false; // WB8, WB9, WB10
        }

        if (p2 == WordBreak.Numeric && (p1 == WordBreak.MidNum || IsMidNumLetQ(p1)) && cur == WordBreak.Numeric)
        {
            return false; // WB11
        }

        if (p1 == WordBreak.Numeric && (cur == WordBreak.MidNum || IsMidNumLetQ(cur)) && NextEffective<TClasses>(text, afterCurrent) == WordBreak.Numeric)
        {
            return false; // WB12
        }

        if (p1 == WordBreak.Katakana && cur == WordBreak.Katakana)
        {
            return false; // WB13
        }

        if ((IsAHLetter(p1) || p1 is WordBreak.Numeric or WordBreak.Katakana or WordBreak.ExtendNumLet) && cur == WordBreak.ExtendNumLet)
        {
            return false; // WB13a
        }

        if (p1 == WordBreak.ExtendNumLet && (IsAHLetter(cur) || cur is WordBreak.Numeric or WordBreak.Katakana))
        {
            return false; // WB13b
        }

        return !(p1 == WordBreak.RegionalIndicator && cur == WordBreak.RegionalIndicator && (state.Regional & 1) == 1); // WB15, WB16, WB999
    }

    /// <summary>The class of the next code point that is not Extend, Format or ZWJ, or Other at the end.</summary>
    private static WordBreak NextEffective<TClasses>(ReadOnlySpan<char> text, int index)
        where TClasses : struct, IWordClasses
    {
        while (index < text.Length)
        {
            WordBreak w = WordBreakOf<TClasses>(CodePointAt(text, index, out int size));
            if (!IsIgnorable(w))
            {
                return w;
            }

            index += size;
        }

        return WordBreak.Other;
    }

    private enum EmojiState : byte
    {
        None,
        Pictographic,
        PictographicZwj,
    }

    private enum ConjunctState : byte
    {
        None,
        Consonant,
        ConsonantLinker,
    }

    /// <summary>
    /// The word walk's state: the last code point's class, the last two classes after rule WB4 (Other before the text), and the
    /// number of regional indicators in a row.
    /// </summary>
    // Complex-context letters lie in Thai to Tai Tham (U+0E01 to U+1AAF) and Myanmar Extended-B to Tai Viet (U+A9E0 to
    // U+AADF); a cheaper test than the table for most text.
    private const char FirstComplex = (char)0x0E01;
    private const char LastComplex = (char)0x1AAF;
    private const char FirstComplexB = (char)0xA9E0;
    private const char LastComplexB = (char)0xAADF;

    /// <summary>Gets whether a character lies in a block with complex-context (SA) letters.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool MayBeComplex(char c) =>
        (uint)(c - FirstComplex) <= LastComplex - FirstComplex || (uint)(c - FirstComplexB) <= LastComplexB - FirstComplexB;

    /// <summary>The index of the first character from <paramref name="start"/> on that may be a complex-context letter, or -1.</summary>
    internal static int NextComplexCandidate(ReadOnlySpan<char> text, int start)
    {
        int found = text[start..].IndexOfAny(ComplexBlocks);
        return found < 0 ? -1 : start + found;
    }

    private static readonly SearchValues<char> ComplexBlocks = SearchValues.Create(ComplexBlockChars());

    private static string ComplexBlockChars()
    {
        var chars = new char[LastComplex - FirstComplex + 1 + LastComplexB - FirstComplexB + 1];
        int n = 0;
        for (char c = FirstComplex; c <= LastComplex; c++)
        {
            chars[n++] = c;
        }

        for (char c = FirstComplexB; c <= LastComplexB; c++)
        {
            chars[n++] = c;
        }

        return new string(chars);
    }

    /// <summary>Which word break classes a walk uses.</summary>
    internal interface IWordClasses
    {
        /// <summary>Gets whether complex-context (SA) letters count as ALetter, as in ICU's word rules.</summary>
        static abstract bool ComplexAsLetter { get; }
    }

    /// <summary>Unicode's default classes (UAX #29).</summary>
    internal readonly struct Uax29Words : IWordClasses
    {
        public static bool ComplexAsLetter => false;
    }

    /// <summary>ICU's classes, for the dictionary walk: complex-context letters are ALetter.</summary>
    internal readonly struct IcuWords : IWordClasses
    {
        public static bool ComplexAsLetter => true;
    }

    internal readonly record struct WordState(WordBreak Raw, WordBreak Effective, WordBreak Previous, bool HasEffective, int Regional);
}

/// <summary>Enumerates extended grapheme clusters; get one from <see cref="TextSegmenter.Graphemes(ReadOnlySpan{char})"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="segments"/>
/// </example>
public ref struct GraphemeEnumerator
{
    private readonly ReadOnlySpan<char> _text;
    private int _next;

    internal GraphemeEnumerator(ReadOnlySpan<char> text)
    {
        _text = text;
        Current = default;
        _next = 0;
    }

    /// <summary>Gets the current cluster.</summary>
    public TextSegment Current { get; private set; }

    /// <summary>Returns this enumerator, for <c>foreach</c>.</summary>
    /// <returns>This enumerator.</returns>
    public readonly GraphemeEnumerator GetEnumerator() => this;

    /// <summary>Moves to the next cluster.</summary>
    /// <returns><see langword="false"/> at the end of the text.</returns>
    public bool MoveNext()
    {
        if (_next >= _text.Length)
        {
            return false;
        }

        int end = TextSegmenter.NextGraphemeBoundary(_text, _next);
        Current = new TextSegment(_next, end - _next);
        _next = end;
        return true;
    }
}

/// <summary>Enumerates the segments between word boundaries; get one from <see cref="TextSegmenter.Words(ReadOnlySpan{char})"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="segments"/>
/// </example>
public ref struct WordEnumerator
{
    private readonly ReadOnlySpan<char> _text;
    private int _next;
    private TextSegmenter.WordState _state;

    internal WordEnumerator(ReadOnlySpan<char> text)
    {
        _text = text;
        Current = default;
        _next = 0;
        _state = default;
    }

    /// <summary>Gets the current segment.</summary>
    public TextSegment Current { get; private set; }

    /// <summary>Returns this enumerator, for <c>foreach</c>.</summary>
    /// <returns>This enumerator.</returns>
    public readonly WordEnumerator GetEnumerator() => this;

    /// <summary>Moves to the next segment.</summary>
    /// <returns><see langword="false"/> at the end of the text.</returns>
    public bool MoveNext()
    {
        if (_next >= _text.Length)
        {
            return false;
        }

        int end = TextSegmenter.NextWordBoundary(_text, _next, ref _state);
        Current = new TextSegment(_next, end - _next);
        _next = end;
        return true;
    }
}

/// <summary>
/// Enumerates word segments with word dictionaries; get one from
/// <see cref="TextSegmenter.Words(ReadOnlySpan{char}, WordDictionaries)"/>. Complex-context letters walk as ALetter, as in ICU's
/// word rules, so their runs stay whole, and the dictionaries split them at word ends; a word end before the rule boundary
/// leaves the rule walk ahead, to resume from there.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="dictionary-words"/>
/// </example>
public ref struct DictionaryWordEnumerator
{
    private readonly ReadOnlySpan<char> _text;
    private int _next;

    /// <summary>Where the rule walk has reached; <see cref="_state"/> is the walk's state there. It runs ahead of
    /// <see cref="_next"/> when a word inside a complex-context run ends first.</summary>
    private int _walked;
    private TextSegmenter.WordState _state;
    /// <summary>The next complex-context letter at or after where the last search began (int.MaxValue for none): segments
    /// before it need no dictionary.</summary>
    private int _complex;
    private DictionarySegmenter _words;

    internal DictionaryWordEnumerator(ReadOnlySpan<char> text, WordDictionaries? set)
    {
        _text = text;
        Current = default;
        _next = 0;
        _walked = 0;
        _state = default;
        _words = new DictionarySegmenter(set);
        int found = _words.IsOn ? TextSegmenter.NextComplexCandidate(text, 0) : -1;
        _complex = found < 0 ? int.MaxValue : found;
    }

    /// <summary>Gets the current segment.</summary>
    public TextSegment Current { get; private set; }

    /// <summary>Returns this enumerator, for <c>foreach</c>.</summary>
    /// <returns>This enumerator.</returns>
    public readonly DictionaryWordEnumerator GetEnumerator() => this;

    /// <summary>Moves to the next segment.</summary>
    /// <returns><see langword="false"/> at the end of the text.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_next >= _text.Length)
        {
            return false;
        }

        int end = _walked > _next ? _walked : (_walked = TextSegmenter.NextWordBoundary<TextSegmenter.IcuWords>(_text, _next, ref _state));
        if (_complex < end)
        {
            end = WordEnd();
        }

        Current = new TextSegment(_next, end - _next);
        _next = end;
        return true;
    }

    /// <summary>The first dictionary word end inside a complex-context run before the rule boundary, or the boundary.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int WordEnd()
    {
        for (int i = Math.Max(_next + 1, _complex); i < _walked; i++)
        {
            if (TextSegmenter.MayBeComplex(_text[i]) && DictionarySegmenter.IsInsideRun(_text, i) && _words.IsWordEnd(_text, i))
            {
                return i;
            }
        }

        int found = TextSegmenter.NextComplexCandidate(_text, _walked);
        _complex = found < 0 ? int.MaxValue : found;
        return _walked;
    }
}
