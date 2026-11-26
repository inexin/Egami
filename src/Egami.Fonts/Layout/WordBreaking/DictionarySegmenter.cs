using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout.WordBreaking;

/// <summary>
/// Finds the word ends inside runs of Thai, Lao, Khmer, Myanmar or other complex-context (SA) letters of one script, from a
/// word dictionary, for <see cref="LineBreakEnumerator"/>.
/// </summary>
/// <remarks>
/// <para>The algorithm is ICU's dictionary break engines' (dictbe.cpp): at each position it takes the dictionary words the
/// text starts with and prefers one that two more words follow, then one that one more word follows, then the longest. Text
/// that no word fits runs on to the next point where a word can end and another begin, unless the word before it is long or
/// the text shares a long prefix with a word. Combining marks stay with the word before them, and in Thai so do PAIYANNOI and
/// MAIYAMOK. A run of fewer than four letters (in Thai, five) is not split.</para>
/// <para>The segmenter walks forward through a run as the enumerator asks about later positions, looking at most three words
/// ahead, and allocates nothing.</para>
/// </remarks>
internal struct DictionarySegmenter
{
    /// <summary>The most candidate words kept at one position, as ICU keeps.</summary>
    private const int MaxCandidates = 20;

    /// <summary>A word no longer than this may take the text no word fits after it.</summary>
    private const int RootCombineThreshold = 3;

    /// <summary>Text sharing this long a prefix with a word may follow a word as one.</summary>
    private const int PrefixCombineThreshold = 3;

    private const int Paiyannoi = 0x0E2F;
    private const int Maiyamok = 0x0E46;

    private readonly WordDictionaries? _dictionaries;
    private int _script;
    private WordGraph? _graph;
    private Engine _engine;
    private int _runStart;
    private int _runEnd;
    private int _index;
    private uint _wordsFound;
    private int _next;
    private Slots _slots;

    /// <summary>The rules a script's runs follow.</summary>
    private enum Engine : byte
    {
        Generic,
        Thai,
        Lao,
        Myanmar,
        Khmer,
    }

    /// <summary>Starts a segmenter for the dictionaries; none turns it off.</summary>
    public DictionarySegmenter(WordDictionaries? dictionaries)
    {
        _dictionaries = dictionaries;
        _script = -1;
    }

    /// <summary>Gets whether the segmenter can find any word end.</summary>
    public readonly bool IsOn => _dictionaries is not null;

    /// <summary>
    /// Gets whether a word ends at <paramref name="index"/>, between two complex-context letters; the enumerator asks about
    /// such positions in increasing order.
    /// </summary>
    public bool IsWordEnd(ReadOnlySpan<char> text, int index)
    {
        if (index <= _runStart || index >= _runEnd)
        {
            if (!StartRun(text, index))
            {
                return false;
            }
        }

        while (_next < index)
        {
            _next = Step(text);
        }

        return _next == index && MayBreakBefore(text, index);
    }

    /// <summary>Gets whether <paramref name="index"/> falls between two complex-context letters of one script, inside a run
    /// the segmenter splits.</summary>
    public static bool IsInsideRun(ReadOnlySpan<char> text, int index)
    {
        if (index <= 0 || index >= text.Length)
        {
            return false;
        }

        char c = text[index];
        return !char.IsSurrogate(c) && UnicodeData.LineBreakClassOf(c) == LineBreakClass.ComplexContext
            && InRun(text[index - 1], UnicodeData.ScriptIndex(c));
    }

    private static bool InRun(char c, int script) =>
        !char.IsSurrogate(c) && UnicodeData.LineBreakClassOf(c) == LineBreakClass.ComplexContext && UnicodeData.ScriptIndex(c) == script;

    private static bool IsMark(char c) =>
        UnicodeData.CategoryOf(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>Whether a break before <paramref name="index"/> keeps grapheme clusters whole and repetition and abbreviation
    /// marks with the word before them.</summary>
    private static bool MayBreakBefore(ReadOnlySpan<char> text, int index)
    {
        char c = text[index];
        if (c is (char)0x0E2F or (char)0x0E46 or (char)0x0EAF or (char)0x0EC6)
        {
            return false;
        }

        // GB9, GB9a and GB9b.
        GraphemeClusterBreak after = UnicodeData.GraphemeBreakOf(c);
        if (after is GraphemeClusterBreak.Extend or GraphemeClusterBreak.SpacingMark or GraphemeClusterBreak.ZWJ
            || UnicodeData.GraphemeBreakOf(text[index - 1]) == GraphemeClusterBreak.Prepend)
        {
            return false;
        }

        // GB9c: a consonant, a linker (such as Myanmar's U+1039) and the consonant it stacks.
        if (UnicodeData.IndicConjunctBreakOf(c) != IndicConjunctBreak.Consonant)
        {
            return true;
        }

        bool linker = false;
        int i = index - 1;
        for (; i >= 0; i--)
        {
            IndicConjunctBreak incb = UnicodeData.IndicConjunctBreakOf(text[i]);
            if (incb == IndicConjunctBreak.Linker)
            {
                linker = true;
            }
            else if (incb != IndicConjunctBreak.Extend)
            {
                break;
            }
        }

        return !(linker && i >= 0 && UnicodeData.IndicConjunctBreakOf(text[i]) == IndicConjunctBreak.Consonant);
    }

    /// <summary>Finds the run around <paramref name="index"/> and starts walking it; false when there is none to split.</summary>
    private bool StartRun(ReadOnlySpan<char> text, int index)
    {
        char c = text[index];
        if (char.IsSurrogate(c) || UnicodeData.LineBreakClassOf(c) != LineBreakClass.ComplexContext)
        {
            return false;
        }

        int script = UnicodeData.ScriptIndex(c);
        if (!InRun(text[index - 1], script))
        {
            return false;
        }

        int start = index - 1;
        while (start > 0 && InRun(text[start - 1], script))
        {
            start--;
        }

        int end = index + 1;
        while (end < text.Length && InRun(text[end], script))
        {
            end++;
        }

        _runStart = start;
        _runEnd = end;
        if (script != _script)
        {
            _script = script;
            _graph = _dictionaries!.TryGet(new Script(script), out WordDictionary? dictionary) ? dictionary.Graph : null;
            _engine = script == Script.Thai.Index ? Engine.Thai
                : script == Script.Lao.Index ? Engine.Lao
                : script == Script.Myanmar.Index ? Engine.Myanmar
                : script == Script.Khmer.Index ? Engine.Khmer
                : Engine.Generic;
        }

        // ICU needs four letters for two words, and in Thai more than four.
        int length = end - start;
        if (_graph is null || length < 4 || (_engine == Engine.Thai && length == 4))
        {
            _next = int.MaxValue;
            return true;
        }

        _index = start;
        _wordsFound = 0;
        _next = start;
        for (int i = 0; i < 3; i++)
        {
            _slots[i].Offset = -1;
        }

        return true;
    }

    /// <summary>Where a word may end: ICU's end-of-word sets.</summary>
    private readonly bool EndsWord(char c) => _engine switch
    {
        Engine.Thai => c is not ((char)0x0E31 or >= (char)0x0E40 and <= (char)0x0E44),
        Engine.Lao => c is not (>= (char)0x0EC0 and <= (char)0x0EC4),
        Engine.Khmer => c != (char)0x17D2,
        _ => true,
    };

    /// <summary>Where a word may begin: ICU's beginning-of-word sets; for other scripts any letter that is not a mark.</summary>
    private readonly bool BeginsWord(char c) => _engine switch
    {
        Engine.Thai => c is >= (char)0x0E01 and <= (char)0x0E2E or >= (char)0x0E40 and <= (char)0x0E44,
        Engine.Lao => c is >= (char)0x0E81 and <= (char)0x0EAE or (char)0x0EDC or (char)0x0EDD or >= (char)0x0EC0 and <= (char)0x0EC4,
        Engine.Myanmar => c is >= (char)0x1000 and <= (char)0x102A,
        Engine.Khmer => c is >= (char)0x1780 and <= (char)0x17B3,
        _ => !IsMark(c),
    };

    [UnscopedRef]
    private ref PossibleWord Slot(uint n) => ref _slots[(int)(n % 3)];

    /// <summary>Fills a slot with the words at <paramref name="at"/> (kept if it already holds them), selects the longest and
    /// returns how many there are; <see cref="_index"/> moves past the longest, or to <paramref name="at"/> with none.</summary>
    private int Candidates(ref PossibleWord word, ReadOnlySpan<char> text, int at)
    {
        if (word.Offset != at)
        {
            word.Offset = at;
            word.Count = _graph!.Match(text[at.._runEnd], word.Lengths, out word.Prefix);
        }

        _index = word.Count > 0 ? at + word.Lengths[word.Count - 1] : at;
        word.Current = word.Count - 1;
        word.Mark = word.Current;
        return word.Count;
    }

    /// <summary>Moves a slot to its next shorter word and <see cref="_index"/> past it; false when there is none.</summary>
    private bool BackUp(ref PossibleWord word)
    {
        if (word.Current > 0)
        {
            _index = word.Offset + word.Lengths[--word.Current];
            return true;
        }

        return false;
    }

    /// <summary>Takes a slot's marked word: moves <see cref="_index"/> past it and returns its length.</summary>
    private int AcceptMarked(ref PossibleWord word)
    {
        int length = word.Lengths[word.Mark];
        _index = word.Offset + length;
        return length;
    }

    /// <summary>Finds the next word end of the run, or <see cref="int.MaxValue"/> after the last one before its end.</summary>
    private int Step(ReadOnlySpan<char> text)
    {
        int current = _index;
        int end = _runEnd;
        if (current >= end)
        {
            return int.MaxValue;
        }

        int length = 0;
        int candidates = Candidates(ref Slot(_wordsFound), text, current);
        if (candidates == 1)
        {
            length = AcceptMarked(ref Slot(_wordsFound));
            _wordsFound++;
        }
        else if (candidates > 1)
        {
            // Prefer a word two more words follow, then one that one more word follows, then the longest.
            if (_index < end)
            {
                do
                {
                    if (Candidates(ref Slot(_wordsFound + 1), text, _index) > 0)
                    {
                        Slot(_wordsFound).Mark = Slot(_wordsFound).Current;
                        if (_index >= end)
                        {
                            break;
                        }

                        bool third = false;
                        do
                        {
                            if (Candidates(ref Slot(_wordsFound + 2), text, _index) > 0)
                            {
                                Slot(_wordsFound).Mark = Slot(_wordsFound).Current;
                                third = true;
                                break;
                            }
                        }
                        while (BackUp(ref Slot(_wordsFound + 1)));

                        if (third)
                        {
                            break;
                        }
                    }
                }
                while (BackUp(ref Slot(_wordsFound)));
            }

            length = AcceptMarked(ref Slot(_wordsFound));
            _wordsFound++;
        }

        // Text that starts no word joins the word before it, unless that word is long or the text shares a long prefix with
        // a word; otherwise it runs to the next place where a word may end and a dictionary word begins.
        if (_index < end && length < RootCombineThreshold)
        {
            ref PossibleWord next = ref Slot(_wordsFound);
            if (Candidates(ref next, text, _index) <= 0 && (length == 0 || next.Prefix < PrefixCombineThreshold))
            {
                int at = _index;
                while (true)
                {
                    char before = text[at++];
                    if (at >= end)
                    {
                        break;
                    }

                    if (EndsWord(before) && BeginsWord(text[at]))
                    {
                        int found = Candidates(ref Slot(_wordsFound + 1), text, at);
                        if (found > 0)
                        {
                            break;
                        }
                    }
                }

                if (length <= 0)
                {
                    _wordsFound++;
                }

                length = at - current;
            }
        }

        // Never stop before a combining mark.
        int position = current + length;
        while (position < end && IsMark(text[position]))
        {
            position++;
        }

        // Thai: PAIYANNOI and MAIYAMOK join the word before them when no dictionary word follows.
        if (_engine == Engine.Thai && position < end && position > current
            && Candidates(ref Slot(_wordsFound), text, position) <= 0 && text[position] is (char)Paiyannoi or (char)Maiyamok)
        {
            int c = text[position];
            if (c == Paiyannoi && text[position - 1] is not ((char)Paiyannoi or (char)Maiyamok))
            {
                position++;
                c = position < text.Length ? text[position] : -1;
            }

            if (c == Maiyamok && text[position - 1] != (char)Maiyamok)
            {
                position++;
            }
        }

        _index = position;
        return position >= end ? int.MaxValue : position;
    }

    /// <summary>The words found at one position, as ICU's PossibleWord keeps them.</summary>
    private struct PossibleWord
    {
        /// <summary>Where the words start; -1 for none yet.</summary>
        public int Offset;

        public int Count;

        /// <summary>How far the dictionary walk got, counting the code unit that stopped it.</summary>
        public int Prefix;

        /// <summary>The preferred word.</summary>
        public int Mark;

        /// <summary>The word being looked at.</summary>
        public int Current;

        /// <summary>The word lengths, shortest first.</summary>
        public LengthBuffer Lengths;
    }

    [InlineArray(MaxCandidates)]
    private struct LengthBuffer
    {
        private int _element;
    }

    [InlineArray(3)]
    private struct Slots
    {
        private PossibleWord _element;
    }
}
