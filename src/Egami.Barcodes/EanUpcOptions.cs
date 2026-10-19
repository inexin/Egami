namespace Egami.Barcodes;

/// <summary>
/// How <see cref="Barcode.Ean13(string, EanUpcOptions?)"/>, <see cref="Barcode.Ean8(string, EanUpcOptions?)"/>,
/// <see cref="Barcode.UpcA(string, EanUpcOptions?)"/> and <see cref="Barcode.UpcE(string, EanUpcOptions?)"/> make a symbol.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-ean"/>
/// </example>
public sealed record class EanUpcOptions
{
    /// <summary>Gets the defaults.</summary>
    public static EanUpcOptions Default { get; } = new();

    /// <summary>
    /// Gets an add-on of 2 digits (EAN-2, such as a magazine's issue) or 5 digits (EAN-5, such as a book's price), drawn to the
    /// right of the main symbol with its digits above it; or <see langword="null"/>, the default, for none.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not 2 or 5 digits.</exception>
    public string? AddOn
    {
        get;
        init => field = value is null || (value.Length is 2 or 5 && value.All(char.IsAsciiDigit))
            ? value
            : throw new ArgumentException("An add-on is 2 or 5 digits.", nameof(value));
    }

    /// <summary>
    /// Gets the height of the bars in modules, or <see langword="null"/>, the default, for the standard's nominal height: 69 for
    /// EAN-13, UPC-A and UPC-E, 55 for EAN-8. It sets the height when the barcode is drawn at a module size; drawn into a
    /// rectangle, the bars fill it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Height
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The height must be positive.");
    }
}
