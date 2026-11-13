namespace Egami.Fonts.Layout;

/// <summary>The file formats <see cref="Hyphenator.Load(Stream, HyphenationFormat)"/> reads hyphenation patterns from.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="hyphenation"/>
/// </example>
public enum HyphenationFormat
{
    /// <summary>Tell the format from the data: <see cref="Compiled"/> by its signature, <see cref="TeX"/> when it has a
    /// <c>\patterns</c> group, <see cref="LibHyphen"/> when the first line names a character set, otherwise
    /// <see cref="PatternList"/>.</summary>
    Auto = 0,

    /// <summary>A hyph-utf8 <c>hyph-xx.tex</c> file: <c>\patterns{}</c> and <c>\hyphenation{}</c> groups, <c>%</c> comments,
    /// <c>^^</c> escapes, and the minimums from the <c>hyphenmins</c> header.</summary>
    TeX = 1,

    /// <summary>Plain patterns, one or more per line in UTF-8, as in hyph-utf8's <c>hyph-xx.pat.txt</c>.</summary>
    PatternList = 2,

    /// <summary>A LibreOffice (libhyphen) <c>hyph_xx_YY.dic</c> file: a character set line, <c>LEFTHYPHENMIN</c>,
    /// <c>RIGHTHYPHENMIN</c>, <c>NOHYPHEN</c> and standard patterns. Replacement rules (patterns with <c>/</c>) throw
    /// <see cref="NotSupportedException"/>.</summary>
    LibHyphen = 3,

    /// <summary>Egami's compiled form (<c>.ehyp</c>), written by <see cref="Hyphenator.Save"/>.</summary>
    Compiled = 4,
}
