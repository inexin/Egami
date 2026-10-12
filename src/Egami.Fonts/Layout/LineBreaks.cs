using System.Globalization;
using System.Runtime.CompilerServices;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// Finds where lines may break: Unicode's line breaking algorithm (UAX #14, Unicode 17.0) from Egami's own tables, with CSS
/// Text 3's <c>word-break</c> and <c>line-break</c> switches. No shaping is involved, and there are no dictionaries: Thai, Lao,
/// Khmer and Myanmar break only at spaces and zero width spaces.
/// </summary>
/// <remarks>
/// With <see cref="LineBreakStrictness.Strict"/>, which is UAX #14's own rules, every break Unicode's LineBreakTest.txt lists
/// is found; the default (CSS's <c>auto</c>) also breaks before small kana. An object replacement character (U+FFFC, as
/// placeholders use) breaks before and after. The enumerator walks the text once and allocates nothing.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
/// </example>
public static class LineBreaks
{
    /// <summary>Enumerates the break opportunities of a text, in order; the last is always at the end of the text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The CSS switches; the default is CSS's.</param>
    /// <returns>An enumerator of the breaks. An empty text has none.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
    /// </example>
    public static LineBreakEnumerator Enumerate(ReadOnlySpan<char> text, LineBreakOptions options = default) => new(text, options);

    /// <summary>Whether a BCP 47 language tag is Chinese or Japanese, which CSS's strictness rules single out.</summary>
    internal static bool IsChineseOrJapanese(string? language)
    {
        if (string.IsNullOrEmpty(language) || language.Length < 2)
        {
            return false;
        }

        bool prefix = string.Compare(language, 0, "ja", 0, 2, StringComparison.OrdinalIgnoreCase) == 0
            || string.Compare(language, 0, "zh", 0, 2, StringComparison.OrdinalIgnoreCase) == 0;
        return prefix && (language.Length == 2 || language[2] is '-' or '_');
    }
}

/// <summary>Enumerates line break opportunities; get one from <see cref="LineBreaks.Enumerate"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="line-breaks"/>
/// </example>
public ref struct LineBreakEnumerator
{
    private readonly ReadOnlySpan<char> _text;
    private readonly LineBreaker.Tailoring _tailoring;
    private LineBreaker.Item _a;
    private LineBreaker.State _state;
    private bool _started;
    private bool _done;

    internal LineBreakEnumerator(ReadOnlySpan<char> text, LineBreakOptions options)
    {
        _text = text;
        _tailoring = new LineBreaker.Tailoring(options);
        _a = default;
        _state = default;
        _started = false;
        _done = text.IsEmpty;
        Current = default;
    }

    /// <summary>Gets the current break.</summary>
    public LineBreak Current { get; private set; }

    /// <summary>Returns this enumerator, for <c>foreach</c>.</summary>
    /// <returns>This enumerator.</returns>
    public readonly LineBreakEnumerator GetEnumerator() => this;

    /// <summary>Moves to the next break opportunity.</summary>
    /// <returns><see langword="false"/> after the break at the end of the text.</returns>
    public bool MoveNext()
    {
        if (_done)
        {
            return false;
        }

        if (_tailoring.Anywhere)
        {
            return MoveNextAnywhere();
        }

        if (!_started)
        {
            _started = true;
            _a = LineBreaker.ReadItem(_text, 0, _tailoring);
            _state = LineBreaker.State.Start(_a);
        }

        while (_a.End < _text.Length)
        {
            LineBreaker.Item b = LineBreaker.ReadItem(_text, _a.End, _tailoring);
            int decision = LineBreaker.Decide(_text, _a, b, _state, _tailoring);
            LineBreaker.Item a = _a;
            _state = _state.Advance(_a, b);
            _a = b;
            if (decision != LineBreaker.NoBreak)
            {
                Current = new LineBreak(b.Start, decision == LineBreaker.Mandatory, a.IsSoftHyphen);
                return true;
            }
        }

        // LB3: always break at the end of text; the break is mandatory after a hard line break.
        _done = true;
        Current = new LineBreak(_text.Length, LineBreaker.IsHardBreak(_a.Class), _a.IsSoftHyphen);
        return true;
    }

    /// <summary>CSS <c>line-break: anywhere</c>: every grapheme boundary, except before a hard line break.</summary>
    private bool MoveNextAnywhere()
    {
        int start = _started ? Current.Index : 0;
        _started = true;
        while (true)
        {
            int end = TextSegmenter.NextGraphemeBoundary(_text, start);
            int cp = TextSegmenter.CodePointBefore(_text, end, out _);
            LineBreakClass before = UnicodeData.LineBreakClassOf(cp);
            bool hard = LineBreaker.IsHardBreak(before) && !(before == LineBreakClass.CarriageReturn && end < _text.Length && _text[end] == '\n');
            if (end >= _text.Length)
            {
                _done = true;
                Current = new LineBreak(_text.Length, hard, cp == 0xAD);
                return true;
            }

            LineBreakClass next = UnicodeData.LineBreakClassOf(TextSegmenter.CodePointAt(_text, end, out _));
            if (!LineBreaker.IsHardBreak(next) || hard)
            {
                Current = new LineBreak(end, hard, cp == 0xAD);
                return true;
            }

            start = end;
        }
    }
}

/// <summary>The rules of UAX #14 over items: a base character with its combining marks (LB9), resolved by LB1 and tailored.</summary>
internal static class LineBreaker
{
    internal const int NoBreak = 0;
    internal const int Allowed = 1;
    internal const int Mandatory = 2;

    /// <summary>The start of text, where a rule looks back for a class.</summary>
    private const byte Sot = 255;

    private const byte EastAsianFlag = 1;
    private const byte InitialQuoteFlag = 2;
    private const byte FinalQuoteFlag = 4;
    private const byte UnassignedPictographicFlag = 8;
    private const byte DottedCircleFlag = 16;
    private const byte EndsWithZwjFlag = 32;
    private const byte SoftHyphenFlag = 64;

    /// <summary>The tailorings the options ask for, resolved once.</summary>
    internal readonly struct Tailoring
    {
        public readonly bool Anywhere;
        public readonly bool BreakAll;
        public readonly bool KeepAll;
        public readonly bool SmallKanaAsIdeographs;
        public readonly bool HyphensAsIdeographs;
        public readonly bool Loose;

        public Tailoring(LineBreakOptions options)
        {
            Anywhere = options.Strictness == LineBreakStrictness.Anywhere;
            BreakAll = options.WordBreak == WordBreak.BreakAll;
            KeepAll = options.WordBreak == WordBreak.KeepAll;
            bool lenient = options.Strictness is LineBreakStrictness.Auto or LineBreakStrictness.Normal or LineBreakStrictness.Loose;
            bool cj = LineBreaks.IsChineseOrJapanese(options.Language);
            SmallKanaAsIdeographs = lenient;
            HyphensAsIdeographs = lenient && cj;
            Loose = options.Strictness == LineBreakStrictness.Loose && cj;
        }
    }

    /// <summary>An item: a base character and the combining marks and joiners that follow it (LB9).</summary>
    internal readonly struct Item(int start, int end, LineBreakClass cls, byte flags)
    {
        public readonly int Start = start;
        public readonly int End = end;
        public readonly LineBreakClass Class = cls;
        public readonly byte Flags = flags;

        public bool IsEastAsian => (Flags & EastAsianFlag) != 0;

        public bool IsInitialQuote => Class == LineBreakClass.Quotation && (Flags & InitialQuoteFlag) != 0;

        public bool IsFinalQuote => Class == LineBreakClass.Quotation && (Flags & FinalQuoteFlag) != 0;

        public bool IsSoftHyphen => (Flags & SoftHyphenFlag) != 0;

        public bool EndsWithZwj => (Flags & EndsWithZwjFlag) != 0;

        /// <summary>AK, AS or the dotted circle, the bases of LB28a.</summary>
        public bool IsAksaraBase => Class is LineBreakClass.Aksara or LineBreakClass.AksaraStart || (Flags & DottedCircleFlag) != 0;
    }

    /// <summary>What the rules need to know about the items before the current boundary.</summary>
    internal readonly struct State
    {
        /// <summary>The last item that is not a space, valid when <see cref="HasNonSpace"/>.</summary>
        public readonly Item LastNonSpace;

        /// <summary>The item before <see cref="LastNonSpace"/>, valid when <see cref="HasBeforeNonSpace"/> (else start of text).</summary>
        public readonly Item BeforeNonSpace;

        public readonly bool HasNonSpace;
        public readonly bool HasBeforeNonSpace;

        /// <summary>ZW SP* ends at the current item.</summary>
        public readonly bool ZeroWidthRun;

        /// <summary>NU (SY | IS)* ends at the current item.</summary>
        public readonly bool NumberRun;

        /// <summary>NU (SY | IS)* (CL | CP) ends at the current item.</summary>
        public readonly bool NumberClose;

        /// <summary>The number of regional indicators ending at the current item.</summary>
        public readonly int RegionalCount;

        private State(Item lastNonSpace, Item beforeNonSpace, bool hasNonSpace, bool hasBefore, bool zw, bool number, bool close, int regional)
        {
            LastNonSpace = lastNonSpace;
            BeforeNonSpace = beforeNonSpace;
            HasNonSpace = hasNonSpace;
            HasBeforeNonSpace = hasBefore;
            ZeroWidthRun = zw;
            NumberRun = number;
            NumberClose = close;
            RegionalCount = regional;
        }

        public static State Start(Item first)
        {
            bool space = first.Class == LineBreakClass.Space;
            return new State(space ? default : first, default, !space, false, first.Class == LineBreakClass.ZWSpace,
                first.Class == LineBreakClass.Numeric, false, first.Class == LineBreakClass.RegionalIndicator ? 1 : 0);
        }

        public State Advance(Item a, Item b)
        {
            LineBreakClass c = b.Class;
            bool space = c == LineBreakClass.Space;
            bool zw = c == LineBreakClass.ZWSpace || (space && ZeroWidthRun);
            bool close = c is LineBreakClass.ClosePunctuation or LineBreakClass.CloseParenthesis && NumberRun;
            bool number = c == LineBreakClass.Numeric || (c is LineBreakClass.BreakSymbols or LineBreakClass.InfixNumeric && NumberRun);
            int regional = c == LineBreakClass.RegionalIndicator ? RegionalCount + 1 : 0;
            return space
                ? new State(LastNonSpace, BeforeNonSpace, HasNonSpace, HasBeforeNonSpace, zw, number, close, regional)
                : new State(b, a, true, true, zw, number, close, regional);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsHardBreak(LineBreakClass c) =>
        c is LineBreakClass.MandatoryBreak or LineBreakClass.CarriageReturn or LineBreakClass.LineFeed or LineBreakClass.NextLine;

    /// <summary>LB1 and the CSS tailorings: the class a code point takes part in the rules with.</summary>
    internal static LineBreakClass Resolve(int cp, LineBreakClass raw, in Tailoring t)
    {
        LineBreakClass c = raw;
        switch (raw)
        {
            case LineBreakClass.Ambiguous:
            case LineBreakClass.Surrogate:
            case LineBreakClass.Unknown:
                c = LineBreakClass.Alphabetic;
                break;
            case LineBreakClass.ComplexContext:
                UnicodeCategory gc = UnicodeData.CategoryOf(cp);
                c = gc is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark ? LineBreakClass.CombiningMark : LineBreakClass.Alphabetic;
                break;
            case LineBreakClass.ConditionalJapaneseStarter:
                c = t.SmallKanaAsIdeographs ? LineBreakClass.Ideographic : LineBreakClass.Nonstarter;
                break;
        }

        // CSS Text 3 line-break: hyphens may start a line in Chinese and Japanese under normal and loose rules; loose rules
        // also let iteration marks, inseparable characters, centred punctuation and wide prefixes and suffixes break.
        if (t.HyphensAsIdeographs && cp is 0x2010 or 0x2013 or 0x301C or 0x30A0)
        {
            c = LineBreakClass.Ideographic;
        }

        if (t.Loose)
        {
            if (cp is 0x3005 or 0x303B or 0x309D or 0x309E or 0x30FD or 0x30FE
                or 0x30FB or 0xFF1A or 0xFF1B or 0xFF65 or 0x203C or 0x2047 or 0x2048 or 0x2049 or 0xFF01 or 0xFF1F
                || c == LineBreakClass.Inseparable)
            {
                c = LineBreakClass.Ideographic;
            }
            else if (c is LineBreakClass.PostfixNumeric or LineBreakClass.PrefixNumeric
                && UnicodeData.GetEastAsianWidth(cp) is EastAsianWidth.Fullwidth or EastAsianWidth.Wide or EastAsianWidth.Ambiguous)
            {
                c = LineBreakClass.Ideographic;
            }
        }

        // CSS word-break: break-all treats letters and digits of every script like ideographs.
        if (t.BreakAll && c is LineBreakClass.Alphabetic or LineBreakClass.HebrewLetter or LineBreakClass.Numeric)
        {
            c = LineBreakClass.Ideographic;
        }

        return c;
    }

    /// <summary>Reads the item starting at an index: its base character and, unless the base is a break or a space, the
    /// combining marks and joiners after it (LB9); a lone combining mark or joiner is alphabetic (LB10).</summary>
    internal static Item ReadItem(ReadOnlySpan<char> text, int start, in Tailoring t)
    {
        int cp = TextSegmenter.CodePointAt(text, start, out int size);
        LineBreakClass raw = UnicodeData.LineBreakClassOf(cp);
        LineBreakClass c = Resolve(cp, raw, t);
        int end = start + size;
        byte flags = 0;
        bool zwj = c == LineBreakClass.ZWJ;
        if (c is LineBreakClass.CombiningMark or LineBreakClass.ZWJ)
        {
            // LB10: as U+0041, with none of the base's flags.
            c = LineBreakClass.Alphabetic;
        }
        else
        {
            if (UnicodeData.GetEastAsianWidth(cp) is EastAsianWidth.Fullwidth or EastAsianWidth.Wide or EastAsianWidth.Halfwidth)
            {
                flags |= EastAsianFlag;
            }

            if (raw == LineBreakClass.Quotation)
            {
                UnicodeCategory gc = UnicodeData.CategoryOf(cp);
                flags |= gc == UnicodeCategory.InitialQuotePunctuation ? InitialQuoteFlag : gc == UnicodeCategory.FinalQuotePunctuation ? FinalQuoteFlag : (byte)0;
            }

            if (UnicodeData.HasFlag(cp, UnicodeTables.ExtendedPictographicFlag) && UnicodeData.CategoryOf(cp) == UnicodeCategory.OtherNotAssigned)
            {
                flags |= UnassignedPictographicFlag;
            }

            if (cp == 0x25CC)
            {
                flags |= DottedCircleFlag;
            }

            if (cp == 0xAD)
            {
                flags |= SoftHyphenFlag;
            }
        }

        if (c is not (LineBreakClass.MandatoryBreak or LineBreakClass.CarriageReturn or LineBreakClass.LineFeed or LineBreakClass.NextLine
            or LineBreakClass.Space or LineBreakClass.ZWSpace))
        {
            while (end < text.Length)
            {
                int next = TextSegmenter.CodePointAt(text, end, out int nextSize);
                LineBreakClass nc = Resolve(next, UnicodeData.LineBreakClassOf(next), t);
                if (nc is not (LineBreakClass.CombiningMark or LineBreakClass.ZWJ))
                {
                    break;
                }

                zwj = nc == LineBreakClass.ZWJ;
                end += nextSize;
            }
        }

        if (zwj)
        {
            flags |= EndsWithZwjFlag;
        }

        return new Item(start, end, c, flags);
    }

    /// <summary>The item after another, or a start-of-nothing item with class <see cref="LineBreakClass.Unknown"/> at the end.</summary>
    private static Item Peek(ReadOnlySpan<char> text, Item after, in Tailoring t) =>
        after.End < text.Length ? ReadItem(text, after.End, t) : new Item(text.Length, text.Length, LineBreakClass.Unknown, 0);

    private static bool IsEot(Item item) => item.Start == item.End;

    private static bool IsAlphabetic(LineBreakClass c) => c is LineBreakClass.Alphabetic or LineBreakClass.HebrewLetter;

    private static bool IsLetterLike(LineBreakClass c) => c is LineBreakClass.Alphabetic or LineBreakClass.HebrewLetter or LineBreakClass.Numeric
        or LineBreakClass.Ideographic or LineBreakClass.H2 or LineBreakClass.H3 or LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT
        or LineBreakClass.EBase or LineBreakClass.EModifier or LineBreakClass.Aksara or LineBreakClass.AksaraStart or LineBreakClass.AksaraPrebase;

    /// <summary>Decides the boundary between items <paramref name="a"/> and <paramref name="b"/> (LB4 to LB31).</summary>
    internal static int Decide(ReadOnlySpan<char> text, Item a, Item b, State s, in Tailoring t)
    {
        LineBreakClass A = a.Class;
        LineBreakClass B = b.Class;

        // LB4, LB5: hard line breaks.
        if (A == LineBreakClass.MandatoryBreak)
        {
            return Mandatory;
        }

        if (A == LineBreakClass.CarriageReturn)
        {
            return B == LineBreakClass.LineFeed ? NoBreak : Mandatory;
        }

        if (A is LineBreakClass.LineFeed or LineBreakClass.NextLine)
        {
            return Mandatory;
        }

        // LB6, LB7.
        if (IsHardBreak(B) || B is LineBreakClass.Space or LineBreakClass.ZWSpace)
        {
            return NoBreak;
        }

        // LB8: ZW SP* ÷.
        if (s.ZeroWidthRun)
        {
            return Allowed;
        }

        // LB8a: ZWJ ×.
        if (a.EndsWithZwj)
        {
            return NoBreak;
        }

        // LB11, LB12, LB12a.
        if (A == LineBreakClass.WordJoiner || B == LineBreakClass.WordJoiner || A == LineBreakClass.Glue)
        {
            return NoBreak;
        }

        if (B == LineBreakClass.Glue && A is not (LineBreakClass.Space or LineBreakClass.BreakAfter or LineBreakClass.Hyphen or LineBreakClass.UnambiguousHyphen))
        {
            return NoBreak;
        }

        // LB13.
        if (B is LineBreakClass.ClosePunctuation or LineBreakClass.CloseParenthesis or LineBreakClass.Exclamation or LineBreakClass.BreakSymbols)
        {
            return NoBreak;
        }

        LineBreakClass beforeSpaces = s.HasNonSpace ? s.LastNonSpace.Class : (LineBreakClass)Sot;

        // LB14: OP SP* ×.
        if (beforeSpaces == LineBreakClass.OpenPunctuation)
        {
            return NoBreak;
        }

        // LB15a: (sot | BK | CR | LF | NL | OP | QU | GL | SP | ZW) [\p{Pi}&QU] SP* ×.
        if (s.HasNonSpace && s.LastNonSpace.IsInitialQuote)
        {
            LineBreakClass p = s.HasBeforeNonSpace ? s.BeforeNonSpace.Class : (LineBreakClass)Sot;
            if (p == (LineBreakClass)Sot || IsHardBreak(p) || p is LineBreakClass.OpenPunctuation or LineBreakClass.Quotation or LineBreakClass.Glue
                or LineBreakClass.Space or LineBreakClass.ZWSpace)
            {
                return NoBreak;
            }
        }

        // LB15b: × [\p{Pf}&QU] (SP | GL | WJ | CL | QU | CP | EX | IS | SY | BK | CR | LF | NL | ZW | eot).
        if (b.IsFinalQuote)
        {
            Item n = Peek(text, b, t);
            if (IsEot(n) || IsHardBreak(n.Class) || n.Class is LineBreakClass.Space or LineBreakClass.Glue or LineBreakClass.WordJoiner
                or LineBreakClass.ClosePunctuation or LineBreakClass.Quotation or LineBreakClass.CloseParenthesis or LineBreakClass.Exclamation
                or LineBreakClass.InfixNumeric or LineBreakClass.BreakSymbols or LineBreakClass.ZWSpace)
            {
                return NoBreak;
            }
        }

        // LB15c: SP ÷ IS NU; LB15d: × IS.
        if (B == LineBreakClass.InfixNumeric)
        {
            return A == LineBreakClass.Space && Peek(text, b, t).Class == LineBreakClass.Numeric ? Allowed : NoBreak;
        }

        // LB16: (CL | CP) SP* × NS; LB17: B2 SP* × B2.
        if (B == LineBreakClass.Nonstarter && beforeSpaces is LineBreakClass.ClosePunctuation or LineBreakClass.CloseParenthesis)
        {
            return NoBreak;
        }

        if (B == LineBreakClass.BreakBoth && beforeSpaces == LineBreakClass.BreakBoth)
        {
            return NoBreak;
        }

        // LB18: SP ÷.
        if (A == LineBreakClass.Space)
        {
            return Allowed;
        }

        // LB19: × [QU - \p{Pi}], [QU - \p{Pf}] ×.
        if (B == LineBreakClass.Quotation && !b.IsInitialQuote)
        {
            return NoBreak;
        }

        if (A == LineBreakClass.Quotation && !a.IsFinalQuote)
        {
            return NoBreak;
        }

        // LB19a: unless surrounded by East Asian characters, do not break either side of a quotation mark.
        if (B == LineBreakClass.Quotation)
        {
            if (!a.IsEastAsian)
            {
                return NoBreak;
            }

            Item n = Peek(text, b, t);
            if (IsEot(n) || !n.IsEastAsian)
            {
                return NoBreak;
            }
        }

        if (A == LineBreakClass.Quotation)
        {
            if (!b.IsEastAsian)
            {
                return NoBreak;
            }

            if (!s.HasBeforeNonSpace || !s.BeforeNonSpace.IsEastAsian)
            {
                return NoBreak;
            }
        }

        // LB20: ÷ CB, CB ÷.
        if (A == LineBreakClass.ContingentBreak || B == LineBreakClass.ContingentBreak)
        {
            return Allowed;
        }

        // LB20a: (sot | BK | CR | LF | NL | SP | ZW | CB | GL) (HY | HH) × (AL | HL).
        if (A is LineBreakClass.Hyphen or LineBreakClass.UnambiguousHyphen && IsAlphabetic(B))
        {
            LineBreakClass p = s.HasBeforeNonSpace ? s.BeforeNonSpace.Class : (LineBreakClass)Sot;
            if (p == (LineBreakClass)Sot || IsHardBreak(p) || p is LineBreakClass.Space or LineBreakClass.ZWSpace or LineBreakClass.ContingentBreak
                or LineBreakClass.Glue)
            {
                return NoBreak;
            }
        }

        // LB21.
        if (B is LineBreakClass.BreakAfter or LineBreakClass.UnambiguousHyphen or LineBreakClass.Hyphen or LineBreakClass.Nonstarter
            || A == LineBreakClass.BreakBefore)
        {
            return NoBreak;
        }

        // LB21a: HL (HY | HH) × [^HL]; LB21b: SY × HL.
        if (A is LineBreakClass.Hyphen or LineBreakClass.UnambiguousHyphen && B != LineBreakClass.HebrewLetter
            && s.HasBeforeNonSpace && s.BeforeNonSpace.Class == LineBreakClass.HebrewLetter)
        {
            return NoBreak;
        }

        if (A == LineBreakClass.BreakSymbols && B == LineBreakClass.HebrewLetter)
        {
            return NoBreak;
        }

        // LB22: × IN.
        if (B == LineBreakClass.Inseparable)
        {
            return NoBreak;
        }

        // LB23, LB23a, LB24.
        if ((IsAlphabetic(A) && B == LineBreakClass.Numeric) || (A == LineBreakClass.Numeric && IsAlphabetic(B)))
        {
            return NoBreak;
        }

        if ((A == LineBreakClass.PrefixNumeric && B is LineBreakClass.Ideographic or LineBreakClass.EBase or LineBreakClass.EModifier)
            || (A is LineBreakClass.Ideographic or LineBreakClass.EBase or LineBreakClass.EModifier && B == LineBreakClass.PostfixNumeric))
        {
            return NoBreak;
        }

        if ((A is LineBreakClass.PrefixNumeric or LineBreakClass.PostfixNumeric && IsAlphabetic(B))
            || (IsAlphabetic(A) && B is LineBreakClass.PrefixNumeric or LineBreakClass.PostfixNumeric))
        {
            return NoBreak;
        }

        // LB25: numbers.
        if (B is LineBreakClass.PostfixNumeric or LineBreakClass.PrefixNumeric && (s.NumberClose || s.NumberRun))
        {
            return NoBreak;
        }

        if (A is LineBreakClass.PostfixNumeric or LineBreakClass.PrefixNumeric)
        {
            if (B == LineBreakClass.Numeric)
            {
                return NoBreak;
            }

            if (B == LineBreakClass.OpenPunctuation)
            {
                Item n = Peek(text, b, t);
                if (n.Class == LineBreakClass.Numeric
                    || (n.Class == LineBreakClass.InfixNumeric && !IsEot(n) && Peek(text, n, t).Class == LineBreakClass.Numeric))
                {
                    return NoBreak;
                }
            }
        }

        if (B == LineBreakClass.Numeric && (A is LineBreakClass.Hyphen or LineBreakClass.InfixNumeric || s.NumberRun))
        {
            return NoBreak;
        }

        // LB26, LB27: Korean syllable blocks.
        if ((A == LineBreakClass.JL && B is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3)
            || (A is LineBreakClass.JV or LineBreakClass.H2 && B is LineBreakClass.JV or LineBreakClass.JT)
            || (A is LineBreakClass.JT or LineBreakClass.H3 && B == LineBreakClass.JT))
        {
            return NoBreak;
        }

        if ((A is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3 && B == LineBreakClass.PostfixNumeric)
            || (A == LineBreakClass.PrefixNumeric && B is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3))
        {
            return NoBreak;
        }

        // LB28: (AL | HL) × (AL | HL).
        if (IsAlphabetic(A) && IsAlphabetic(B))
        {
            return NoBreak;
        }

        // LB28a: Brahmic orthographic syllables.
        if (A == LineBreakClass.AksaraPrebase && b.IsAksaraBase)
        {
            return NoBreak;
        }

        if (a.IsAksaraBase && B is LineBreakClass.ViramaFinal or LineBreakClass.Virama)
        {
            return NoBreak;
        }

        if (A == LineBreakClass.Virama && (b.Class == LineBreakClass.Aksara || (b.Flags & DottedCircleFlag) != 0)
            && s.HasBeforeNonSpace && s.BeforeNonSpace.IsAksaraBase)
        {
            return NoBreak;
        }

        if (a.IsAksaraBase && b.IsAksaraBase && Peek(text, b, t).Class == LineBreakClass.ViramaFinal)
        {
            return NoBreak;
        }

        // LB29: IS × (AL | HL).
        if (A == LineBreakClass.InfixNumeric && IsAlphabetic(B))
        {
            return NoBreak;
        }

        // LB30: letters, numbers and parentheses that are not East Asian.
        if ((A is LineBreakClass.Alphabetic or LineBreakClass.HebrewLetter or LineBreakClass.Numeric && B == LineBreakClass.OpenPunctuation && !b.IsEastAsian)
            || (A == LineBreakClass.CloseParenthesis && !a.IsEastAsian && B is LineBreakClass.Alphabetic or LineBreakClass.HebrewLetter or LineBreakClass.Numeric))
        {
            return NoBreak;
        }

        // LB30a: regional indicator pairs.
        if (A == LineBreakClass.RegionalIndicator && B == LineBreakClass.RegionalIndicator && (s.RegionalCount & 1) == 1)
        {
            return NoBreak;
        }

        // LB30b: EB × EM; [\p{Extended_Pictographic}&\p{Cn}] × EM.
        if (B == LineBreakClass.EModifier && (A == LineBreakClass.EBase || (a.Flags & UnassignedPictographicFlag) != 0))
        {
            return NoBreak;
        }

        // CSS word-break: keep-all keeps letters, digits, ideographs and Hangul together.
        if (t.KeepAll && IsLetterLike(A) && IsLetterLike(B))
        {
            return NoBreak;
        }

        // LB31.
        return Allowed;
    }
}
