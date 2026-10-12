namespace Egami.Svg;

/// <summary>How <see cref="SvgDocument.Save(Stream, SvgSaveOptions?)"/> writes a document.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="save"/>
/// </example>
public sealed record class SvgSaveOptions
{
    private readonly int _precision = 3;

    /// <summary>Gets the defaults: indented, three decimals, not gzipped.</summary>
    public static SvgSaveOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether elements made in code are written on their own indented lines. Content read from a file
    /// keeps its own whitespace either way. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Indent { get; init; } = true;

    /// <summary>
    /// Gets the number of decimals written for numbers and paths set in code (through
    /// <see cref="SvgAttributeCollection.Set(string, double, string?, string?)"/> and <see cref="SvgAttributeCollection.SetPath"/>), from 0 to 9; trailing
    /// zeros are left out. Text attributes are written as they are. Defaults to 3.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 0 or above 9.</exception>
    public int Precision
    {
        get => _precision;
        init => _precision = value is >= 0 and <= 9 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The precision must be from 0 to 9 decimals.");
    }

    /// <summary>Gets a value indicating whether to write gzip data, an .svgz file. Defaults to <see langword="false"/>.</summary>
    public bool Gzip { get; init; }
}
