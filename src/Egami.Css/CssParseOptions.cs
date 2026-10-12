namespace Egami.Css;

/// <summary>A problem found while parsing CSS. Parsing recovers as CSS Syntax 3 says and goes on.</summary>
/// <param name="Line">The 1-based line, or 0 when unknown.</param>
/// <param name="Column">The 1-based column in UTF-16 code units, or 0 when unknown.</param>
/// <param name="Message">What was wrong and what the parser did about it.</param>
public sealed record CssParseError(int Line, int Column, string Message);

/// <summary>Options for parsing CSS.</summary>
public sealed record class CssParseOptions
{
    /// <summary>Gets the default options: errors are not reported.</summary>
    public static CssParseOptions Default { get; } = new();

    /// <summary>Gets a callback that receives every parse error, or <see langword="null"/> to ignore them.</summary>
    public Action<CssParseError>? Errors { get; init; }

    /// <summary>
    /// Gets the deepest nesting of blocks and functions kept. A block nested deeper is dropped with an error, which keeps
    /// hostile input from exhausting the stack. Defaults to 128.
    /// </summary>
    public int MaxNestingDepth { get; init; } = 128;

    internal void Report(int line, int column, string message) => Errors?.Invoke(new CssParseError(line, column, message));
}
