namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.DataBarExpanded(string, DataBarExpandedOptions?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-databar-expanded"/>
/// </example>
public sealed record class DataBarExpandedOptions
{
    /// <summary>Gets the defaults.</summary>
    public static DataBarExpandedOptions Default { get; } = new();

    /// <summary>
    /// Gets the symbol characters per row of DataBar Expanded Stacked, an even number 2 to 20, or <see langword="null"/>, the
    /// default, for DataBar Expanded in one row. A row never ends with a lone character, so a symbol can come out a character
    /// longer than its data needs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not an even number 2 to 20.</exception>
    public int? SegmentsPerRow
    {
        get;
        init => field = value is null or (>= 2 and <= 20) && value % 2 != 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The segments per row must be an even number 2 to 20.");
    }
}
