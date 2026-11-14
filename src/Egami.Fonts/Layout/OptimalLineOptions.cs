namespace Egami.Fonts.Layout;

/// <summary>
/// The penalties and tolerances <see cref="LineFill.Optimal"/> weighs, after Knuth and Plass ("Breaking Paragraphs into Lines",
/// 1981) and TeX. Each line costs (<see cref="LinePenalty"/> + badness)^2, plus its break's penalty squared and the extra
/// demerits below; the breaks with the least total win. A justified line's badness is 100 r^3, where r is how far its word
/// spaces stretch (by up to half a space each at r = 1) or shrink; a ragged line's is 100 r^2, where r is the space left at its
/// end in units of 2 em.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="optimal-lines"/>
/// </example>
public sealed record class OptimalLineOptions
{
    private readonly int _linePenalty = 10;
    private readonly int _hyphenPenalty = 50;
    private readonly int _explicitHyphenPenalty = 50;
    private readonly int _doubleHyphenDemerits = 3000;
    private readonly int _finalHyphenDemerits = 5000;
    private readonly int _adjacentDemerits = 100;
    private readonly int _tolerance = 200;
    private readonly int _secondTolerance = 2000;

    internal static OptimalLineOptions Default { get; } = new();

    /// <summary>Gets the cost of every line, which favours fewer lines; 10 by default (TeX's <c>\linepenalty</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or more than 10000.</exception>
    public int LinePenalty
    {
        get => _linePenalty;
        init => _linePenalty = InRange(value);
    }

    /// <summary>Gets the penalty for a break at a soft hyphen or an automatic hyphenation point; 50 by default (TeX's
    /// <c>\hyphenpenalty</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or more than 10000.</exception>
    public int HyphenPenalty
    {
        get => _hyphenPenalty;
        init => _hyphenPenalty = InRange(value);
    }

    /// <summary>Gets the penalty for a break after a hyphen in the text (U+002D or U+2010); 50 by default (TeX's
    /// <c>\exhyphenpenalty</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or more than 10000.</exception>
    public int ExplicitHyphenPenalty
    {
        get => _explicitHyphenPenalty;
        init => _explicitHyphenPenalty = InRange(value);
    }

    /// <summary>Gets the extra demerits for two lines in a row that end with a hyphen; 3000 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int DoubleHyphenDemerits
    {
        get => _doubleHyphenDemerits;
        init => _doubleHyphenDemerits = NotNegative(value);
    }

    /// <summary>Gets the extra demerits for a hyphen at the end of the last line but one (before a mandatory break or the
    /// paragraph's end); 5000 by default (TeX's <c>\finalhyphendemerits</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int FinalHyphenDemerits
    {
        get => _finalHyphenDemerits;
        init => _finalHyphenDemerits = NotNegative(value);
    }

    /// <summary>Gets the extra demerits for a line next to one whose spacing is far different (a tight line after a loose one,
    /// say: fitness classes more than one apart); 100 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int AdjacentDemerits
    {
        get => _adjacentDemerits;
        init => _adjacentDemerits = NotNegative(value);
    }

    /// <summary>Gets the worst badness a line may have on the first pass; 200 by default (TeX's <c>\tolerance</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or more than 10000.</exception>
    public int Tolerance
    {
        get => _tolerance;
        init => _tolerance = InRange(value);
    }

    /// <summary>Gets the worst badness on the second pass, tried when no breaks meet <see cref="Tolerance"/>; 2000 by default.
    /// When that fails too, a last pass takes any line that is not overfull, and lets a piece too wide for any line overflow
    /// its own line, as greedy filling does.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or more than 10000.</exception>
    public int SecondTolerance
    {
        get => _secondTolerance;
        init => _secondTolerance = InRange(value);
    }

    /// <summary>Gets whether word spaces in justified text may shrink, by up to a third of a space, to fit more on a line, as in
    /// TeX; false by default, as Word and browsers only stretch them. A last line (before a mandatory break or the paragraph's
    /// end) shrinks only when <see cref="ParagraphStyle.AlignLast"/> justifies it.</summary>
    public bool ShrinkSpaces { get; init; }

    private static int InRange(int value) =>
        value is >= 0 and <= 10000 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "From 0 to 10000.");

    private static int NotNegative(int value) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "0 or more.");
}
