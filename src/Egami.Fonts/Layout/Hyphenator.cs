using System.Buffers;
using System.Globalization;
using System.Text;

using Egami.Fonts.Layout.Hyphenation;

namespace Egami.Fonts.Layout;

/// <summary>
/// One language's hyphenation patterns and exceptions: finds where a word may break with a hyphen, by Liang's algorithm
/// (F. M. Liang, "Word Hy-phen-a-tion by Com-put-er", Stanford, 1983) as TeX, LibreOffice and browsers use it.
/// </summary>
/// <remarks>
/// <para>Load the patterns from a hyph-utf8 <c>.tex</c> file, a <c>.pat.txt</c> list, a LibreOffice <c>.dic</c> file or the
/// compiled form <see cref="Save"/> writes, or give them as text to <see cref="FromPatterns"/>. A word in the exception list
/// breaks exactly where the list says; any other word breaks where the patterns' highest value between two letters is odd.
/// Either way no break leaves fewer than <see cref="MinBefore"/> characters before it or <see cref="MinAfter"/> after it.</para>
/// <para>A hyphenator is immutable and safe to share between threads; <see cref="GetPoints"/> allocates nothing for words up
/// to about 60 characters.</para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="hyphenation"/>
/// </example>
public sealed class Hyphenator
{
    private const int StackWord = 126;

    private readonly PatternTrie _trie;
    private readonly Dictionary<string, int[]> _exceptions;
    private readonly Dictionary<string, int[]>.AlternateLookup<ReadOnlySpan<char>> _exceptionLookup;
    private readonly string[] _noHyphen;

    internal Hyphenator(PatternTrie trie, Dictionary<string, int[]> exceptions, string[] noHyphen, int minBefore, int minAfter,
        IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        CheckMinimum(minBefore, nameof(minBefore));
        CheckMinimum(minAfter, nameof(minAfter));
        _trie = trie;
        _exceptions = exceptions;
        _exceptionLookup = exceptions.GetAlternateLookup<ReadOnlySpan<char>>();
        _noHyphen = noHyphen;
        MinBefore = minBefore;
        MinAfter = minAfter;
        Metadata = metadata;
    }

    /// <summary>The fewest characters (Unicode code points) a hyphenated word keeps before a break, TeX's
    /// <c>\lefthyphenmin</c>.</summary>
    public int MinBefore { get; }

    /// <summary>The fewest characters (Unicode code points) a hyphenated word keeps after a break, TeX's
    /// <c>\righthyphenmin</c>.</summary>
    public int MinAfter { get; }

    internal PatternTrie Trie => _trie;

    internal IReadOnlyDictionary<string, int[]> Exceptions => _exceptions;

    internal IReadOnlyList<string> NoHyphen => _noHyphen;

    /// <summary>Where the patterns came from (name, version, licence), kept in the compiled form.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> Metadata { get; }

    /// <summary>
    /// Reads patterns, exceptions and minimums from <paramref name="stream"/> in one of the
    /// <see cref="HyphenationFormat">formats</see>. Without minimums in the file, TeX formats get TeX's defaults (2 and 3) and
    /// LibreOffice files libhyphen's (2 and 2).
    /// </summary>
    /// <param name="stream">The file, read to its end; the stream is left open.</param>
    /// <param name="format">The file's format, or <see cref="HyphenationFormat.Auto"/> to tell it from the data.</param>
    /// <returns>The hyphenator.</returns>
    /// <exception cref="InvalidDataException">The data is not a valid file of the format.</exception>
    /// <exception cref="NotSupportedException">A LibreOffice file has replacement rules (patterns with <c>/</c>), or a TeX
    /// file loads its patterns from another file with <c>\input</c>.</exception>
    public static Hyphenator Load(Stream stream, HyphenationFormat format = HyphenationFormat.Auto)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] data = copy.ToArray();
        if (format == HyphenationFormat.Auto)
        {
            format = HyphenationReader.Detect(data);
        }

        return format switch
        {
            HyphenationFormat.Compiled => CompiledHyphenation.Read(data),
            HyphenationFormat.TeX => HyphenationReader.ReadTeX(HyphenationReader.Utf8(data)),
            HyphenationFormat.PatternList => HyphenationReader.ReadPatternList(HyphenationReader.Utf8(data), null, 2, 3),
            HyphenationFormat.LibHyphen => HyphenationReader.ReadLibHyphen(data),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    /// <summary>
    /// Makes a hyphenator from patterns in Liang's notation (such as <c>.hy3ph</c> and <c>hen5at</c>, separated by white space,
    /// as in hyph-utf8's <c>.pat.txt</c> files) and exceptions in <c>hy-phen-ate</c> form (as in its <c>.hyp.txt</c> files).
    /// </summary>
    /// <param name="patterns">The patterns.</param>
    /// <param name="exceptions">The exception words, separated by white space, with a hyphen at every break; or null.</param>
    /// <param name="minBefore">The fewest characters kept before a break; at least 1.</param>
    /// <param name="minAfter">The fewest characters kept after a break; at least 1.</param>
    /// <returns>The hyphenator.</returns>
    public static Hyphenator FromPatterns(string patterns, string? exceptions = null, int minBefore = 2, int minAfter = 3)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        CheckMinimum(minBefore, nameof(minBefore));
        CheckMinimum(minAfter, nameof(minAfter));
        return HyphenationReader.ReadPatternList(patterns, exceptions, minBefore, minAfter);
    }

    /// <summary>
    /// Finds where <paramref name="word"/> may break with a hyphen. Letter case does not matter. The caller passes a single
    /// word: digits, punctuation and spaces are looked up like letters, and patterns rarely match them.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <param name="points">Receives the break offsets: UTF-16 indexes into <paramref name="word"/> of the first character after
    /// each break, ascending. <c>word.Length - 1</c> entries always suffice.</param>
    /// <returns>The number of points written.</returns>
    /// <exception cref="ArgumentException"><paramref name="points"/> is too short for every point.</exception>
    public int GetPoints(ReadOnlySpan<char> word, Span<int> points)
    {
        int n = word.Length;
        if (n < 2)
        {
            return 0;
        }

        int letters = n;
        for (int i = 1; i < n; i++)
        {
            if (IsTrailSurrogate(word, i))
            {
                letters--;
            }
        }

        if (letters < MinBefore + MinAfter)
        {
            return 0;
        }

        char[]? rentedChars = null;
        byte[]? rentedLevels = null;
        try
        {
            Span<char> dotted = n <= StackWord
                ? stackalloc char[StackWord + 2]
                : (rentedChars = ArrayPool<char>.Shared.Rent(n + 2));
            dotted = dotted[..(n + 2)];
            dotted[0] = '.';
            dotted[^1] = '.';
            Span<char> lower = dotted[1..^1];
            Lower(word, lower);

            // levels[k] is the gap before dotted[k]; the break before word[p] is the gap before dotted[p + 1].
            Span<byte> levels = n <= StackWord
                ? stackalloc byte[StackWord + 3]
                : (rentedLevels = ArrayPool<byte>.Shared.Rent(n + 3));
            levels = levels[..(n + 3)];
            levels.Clear();
            if (_exceptionLookup.TryGetValue(lower, out int[]? exception))
            {
                foreach (int p in exception)
                {
                    levels[p + 1] = 1;
                }
            }
            else
            {
                _trie.Apply(dotted, levels);
            }

            int count = 0;
            int before = 1;
            for (int p = 1; p < n; p++)
            {
                bool trail = IsTrailSurrogate(word, p);
                if ((levels[p + 1] & 1) != 0 && !trail && before >= MinBefore && letters - before >= MinAfter
                    && !IsMark(word, p) && !NextToNoHyphen(lower, p))
                {
                    if (count == points.Length)
                    {
                        throw new ArgumentException("The span is too short for every hyphenation point.", nameof(points));
                    }

                    points[count++] = p;
                }

                if (!trail)
                {
                    before++;
                }
            }

            return count;
        }
        finally
        {
            if (rentedChars is not null)
            {
                ArrayPool<char>.Shared.Return(rentedChars);
            }

            if (rentedLevels is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedLevels);
            }
        }
    }

    /// <summary>
    /// Returns a hyphenator with the same patterns and more exceptions, each in <c>hy-phen-ate</c> form: the word with a hyphen
    /// at every break, or with none to keep it whole. A word already in the list takes the new breaks.
    /// </summary>
    /// <param name="words">The exception words.</param>
    /// <returns>The new hyphenator; this one is unchanged.</returns>
    public Hyphenator WithExceptions(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        Dictionary<string, int[]> exceptions = new(_exceptions, StringComparer.Ordinal);
        foreach (string word in words)
        {
            ArgumentNullException.ThrowIfNull(word, nameof(words));
            AddException(exceptions, word);
        }

        return new Hyphenator(_trie, exceptions, _noHyphen, MinBefore, MinAfter, Metadata);
    }

    /// <summary>
    /// Writes the compiled patterns, exceptions and minimums in Egami's own form (<c>.ehyp</c>), which
    /// <see cref="Load(Stream, HyphenationFormat)"/> reads back far faster than it parses pattern text.
    /// </summary>
    /// <param name="output">The stream to write to; it is left open.</param>
    public void Save(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        CompiledHyphenation.Write(this, output);
    }

    /// <summary>A copy with other source details, for the compiled form.</summary>
    internal Hyphenator WithMetadata(IReadOnlyList<KeyValuePair<string, string>> metadata) =>
        new(_trie, _exceptions, _noHyphen, MinBefore, MinAfter, metadata);

    /// <summary>Adds one <c>hy-phen-ate</c> word to <paramref name="exceptions"/>.</summary>
    internal static void AddException(Dictionary<string, int[]> exceptions, ReadOnlySpan<char> word)
    {
        StringBuilder letters = new(word.Length);
        List<int> points = [];
        foreach (char c in word)
        {
            if (c == '-')
            {
                if (letters.Length > 0 && (points.Count == 0 || points[^1] != letters.Length))
                {
                    points.Add(letters.Length);
                }
            }
            else
            {
                letters.Append(c);
            }
        }

        if (letters.Length == 0)
        {
            return;
        }

        if (points.Count > 0 && points[^1] == letters.Length)
        {
            points.RemoveAt(points.Count - 1);
        }

        char[] lower = new char[letters.Length];
        Lower(letters.ToString(), lower);
        exceptions[new string(lower)] = [.. points];
    }

    /// <summary>Lower-cases <paramref name="word"/> into <paramref name="lower"/> code point by code point, keeping each one's
    /// UTF-16 length so offsets stay the same.</summary>
    internal static void Lower(ReadOnlySpan<char> word, Span<char> lower)
    {
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (char.IsHighSurrogate(c) && i + 1 < word.Length && char.IsLowSurrogate(word[i + 1]))
            {
                Rune r = Rune.ToLowerInvariant(new Rune(c, word[i + 1]));
                if (r.Utf16SequenceLength == 2)
                {
                    r.EncodeToUtf16(lower[i..]);
                }
                else
                {
                    lower[i] = c;
                    lower[i + 1] = word[i + 1];
                }

                i++;
            }
            else
            {
                lower[i] = char.ToLowerInvariant(c);
            }
        }
    }

    private static bool IsTrailSurrogate(ReadOnlySpan<char> word, int i) =>
        char.IsLowSurrogate(word[i]) && char.IsHighSurrogate(word[i - 1]);

    // A break never falls before a combining mark, which belongs to the character before it.
    private static bool IsMark(ReadOnlySpan<char> word, int p)
    {
        Rune.DecodeFromUtf16(word[p..], out Rune rune, out _);
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    private bool NextToNoHyphen(ReadOnlySpan<char> lower, int p)
    {
        foreach (string s in _noHyphen)
        {
            if (lower[..p].EndsWith(s, StringComparison.Ordinal) || lower[p..].StartsWith(s, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckMinimum(int value, string name)
    {
        if (value is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(name, value, "A hyphenation minimum is from 1 to 255 characters.");
        }
    }
}
