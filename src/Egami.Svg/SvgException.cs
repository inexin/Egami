namespace Egami.Svg;

/// <summary>
/// Thrown when an SVG file is not well-formed XML, breaks a safety limit, or, with <see cref="SvgLoadOptions.Strict"/>, has
/// anything a lenient load would skip.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="strict"/>
/// </example>
public sealed class SvgException : EgamiException
{
    /// <summary>Initializes a new instance of the <see cref="SvgException"/> class.</summary>
    /// <param name="line">The 1-based line of the problem in the file, or 0 if unknown.</param>
    /// <param name="column">The 1-based column of the problem, or 0 if unknown.</param>
    /// <param name="problem">What is wrong, as a sentence.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public SvgException(int line, int column, string problem, Exception? innerException = null)
        : base(line > 0 ? $"Invalid SVG at line {line}, column {column}: {problem}" : $"Invalid SVG: {problem}", innerException)
    {
        Line = line;
        Column = column;
        Problem = problem;
    }

    /// <summary>Gets the 1-based line of the problem in the file, or 0 if unknown.</summary>
    public int Line { get; }

    /// <summary>Gets the 1-based column of the problem, or 0 if unknown.</summary>
    public int Column { get; }

    /// <summary>Gets what is wrong, as a sentence.</summary>
    public string Problem { get; }
}
