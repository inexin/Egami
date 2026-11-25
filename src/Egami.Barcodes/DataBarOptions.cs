namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.DataBar(string, DataBarOptions?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-databar"/>
/// </example>
public sealed record class DataBarOptions
{
    /// <summary>Gets the defaults.</summary>
    public static DataBarOptions Default { get; } = new();

    /// <summary>Gets the form of the symbol; the default is <see cref="DataBarKind.Omnidirectional"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="DataBarKind"/>.</exception>
    public DataBarKind Kind
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown DataBar kind.");
    }
}
