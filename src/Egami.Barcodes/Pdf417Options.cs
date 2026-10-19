namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.Pdf417(string, Pdf417Options?)"/> makes a symbol; every unset property is chosen automatically.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-pdf417"/>
/// </example>
public sealed record class Pdf417Options
{
    /// <summary>Gets the defaults.</summary>
    public static Pdf417Options Default { get; } = new();

    /// <summary>
    /// Gets the number of data columns, 1 to 30, or <see langword="null"/>, the default, for a symbol about three times as wide
    /// as it is tall.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1 to 30.</exception>
    public int? Columns
    {
        get;
        init => field = value is null or (>= 1 and <= 30) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The columns must be 1 to 30.");
    }

    /// <summary>
    /// Gets the error correction level, 0 (2 codewords) to 8 (512 codewords), or <see langword="null"/>, the default, for the
    /// standard's recommended minimum for the amount of data: 2 up to 40 data codewords, 3 up to 160, 4 up to 320, else 5.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 0 to 8.</exception>
    public int? ErrorLevel
    {
        get;
        init => field = value is null or (>= 0 and <= 8) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The error level must be 0 to 8.");
    }

    /// <summary>
    /// Gets a value indicating whether to write compact PDF417, which leaves out the right row indicators and shortens the stop
    /// pattern to one bar, for a narrower symbol that is harder to read when damaged; off by default.
    /// </summary>
    public bool Compact { get; init; }
}
