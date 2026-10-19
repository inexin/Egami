namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.Aztec(string, AztecOptions?)"/> makes a symbol; every unset property is chosen automatically.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-aztec"/>
/// </example>
public sealed record class AztecOptions
{
    /// <summary>Gets the defaults.</summary>
    public static AztecOptions Default { get; } = new();

    /// <summary>
    /// Gets the share of the codewords, in percent, that must at least go to error correction, plus 3 codewords; or
    /// <see langword="null"/>, the default, for the 23 % the standard recommends. Spare room in the symbol goes to error
    /// correction too.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 5 to 95.</exception>
    public int? ErrorCorrectionPercent
    {
        get;
        init => field = value is null or (>= 5 and <= 95) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The error correction share must be 5 to 95 percent.");
    }

    /// <summary>
    /// Gets a value indicating whether the symbol is compact (1 to 4 layers, 15 x 15 to 27 x 27 modules) or full-range (up to
    /// 32 layers, 151 x 151), or <see langword="null"/>, the default, for whichever is smallest.
    /// </summary>
    public bool? Compact { get; init; }

    /// <summary>Gets the number of data layers, 1 to 32 (1 to 4 when compact), or <see langword="null"/>, the default, for the fewest that fit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1 to 32.</exception>
    public int? Layers
    {
        get;
        init => field = value is null or (>= 1 and <= 32) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The layers must be 1 to 32.");
    }
}
