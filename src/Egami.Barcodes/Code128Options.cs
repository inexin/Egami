namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.Code128(string, Code128Options?)"/> and <see cref="Barcode.Gs1_128(string, Code128Options?)"/> make a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-code128"/>
/// </example>
public sealed record class Code128Options
{
    /// <summary>Gets the defaults.</summary>
    public static Code128Options Default { get; } = new();

    /// <summary>
    /// Gets the height of the bars in modules, or <see langword="null"/>, the default, for 50. It sets the height when the
    /// barcode is drawn at a module size; drawn into a rectangle, the bars fill it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Height
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The height must be positive.");
    }
}
