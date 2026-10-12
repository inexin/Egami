using System.Runtime.CompilerServices;

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
/// rules and no dictionaries.
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
    internal static int NextWordBoundary(ReadOnlySpan<char> text, int start, ref WordState state)
    {
        if (start >= text.Length)
        {
            return text.Length;
        }

        int pos = start + Advance(ref state, text, start);
        while (pos < text.Length)
        {
            int cp = CodePointAt(text, pos, out int size);
            if (IsWordBreak(text, pos + size, state, UnicodeData.WordBreakOf(cp), UnicodeData.HasFlag(cp, UnicodeTables.ExtendedPictographicFlag)))
            {
                return pos;
            }

            pos += Advance(ref state, text, pos);
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

    private static bool IsIgnorable(WordBreak w) => w is WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ;

    private static bool IsAHLetter(WordBreak w) => w is WordBreak.ALetter or WordBreak.HebrewLetter;

    private static bool IsMidNumLetQ(WordBreak w) => w is WordBreak.MidNumLet or WordBreak.SingleQuote;

    /// <summary>Takes the code point at <paramref name="index"/> into the walk's state (rule WB4 folds ignorables into what precedes them).</summary>
    /// <returns>The code point's size in UTF-16 units.</returns>
    private static int Advance(ref WordState state, ReadOnlySpan<char> text, int index)
    {
        WordBreak cur = UnicodeData.WordBreakOf(CodePointAt(text, index, out int size));
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

    private static bool IsWordBreak(ReadOnlySpan<char> text, int afterCurrent, in WordState state, WordBreak cur, bool pictographic)
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

        if (IsAHLetter(p1) && (cur == WordBreak.MidLetter || IsMidNumLetQ(cur)) && IsAHLetter(NextEffective(text, afterCurrent)))
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

        if (p1 == WordBreak.HebrewLetter && cur == WordBreak.DoubleQuote && NextEffective(text, afterCurrent) == WordBreak.HebrewLetter)
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

        if (p1 == WordBreak.Numeric && (cur == WordBreak.MidNum || IsMidNumLetQ(cur)) && NextEffective(text, afterCurrent) == WordBreak.Numeric)
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
    private static WordBreak NextEffective(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length)
        {
            WordBreak w = UnicodeData.WordBreakOf(CodePointAt(text, index, out int size));
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
