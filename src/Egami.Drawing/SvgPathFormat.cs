namespace Egami.Drawing;

/// <summary>How <see cref="Path.ToSvg"/> writes SVG path data.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="svg"/>
/// </example>
public sealed record class SvgPathFormat
{
    private readonly int _precision = 3;

    /// <summary>Gets the default: absolute commands, three decimals, a space between every number.</summary>
    public static SvgPathFormat Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether to write relative commands (lowercase), each point as an offset from the one before. Offsets
    /// are taken from the rounded points, so rounding errors never add up along the path. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Relative { get; init; }

    /// <summary>Gets the number of decimals written, from 0 to 9; trailing zeros are left out. Defaults to 3.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 0 or above 9.</exception>
    public int Precision
    {
        get => _precision;
        init => _precision = value is >= 0 and <= 9 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The precision must be from 0 to 9 decimals.");
    }

    /// <summary>
    /// Gets a value indicating whether to write the shortest data the grammar allows: no leading zero (<c>.5</c>), no space before
    /// a sign or before a second decimal point, H and V for lines along an axis, and no repeated command letters. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool Compact { get; init; }
}

/// <summary>Thrown by <see cref="Path.ParseSvg"/> when SVG path data breaks the grammar.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="svg"/>
/// </example>
public sealed class SvgPathException : EgamiException
{
    /// <summary>Initializes a new instance of the <see cref="SvgPathException"/> class.</summary>
    /// <param name="position">The index of the character where the data first breaks the grammar.</param>
    /// <param name="problem">What is wrong, as a sentence.</param>
    public SvgPathException(int position, string problem)
        : base($"Bad SVG path data at character {position}: {problem}")
    {
        Position = position;
        Problem = problem;
    }

    /// <summary>Gets the index of the character where the data first breaks the grammar.</summary>
    public int Position { get; }

    /// <summary>Gets what is wrong, as a sentence.</summary>
    public string Problem { get; }
}
