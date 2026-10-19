using Egami.Barcodes.Linear;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes an EAN-13 barcode (ISO/IEC 15420), such as a GTIN-13 or an ISBN. Twelve digits get their check digit added; thirteen
    /// are checked. With a <see cref="BarcodeDrawOptions.Font"/>, the first digit is drawn left of the symbol and the others
    /// between the guard bars, which reach down into the text line.
    /// </summary>
    /// <param name="digits">12 or 13 digits.</param>
    /// <param name="options">The add-on and height, or <see langword="null"/> for <see cref="EanUpcOptions.Default"/>.</param>
    /// <returns>The barcode, with the 13 digits (and the add-on after a space) as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is not 12 or 13 digits, or the check digit is wrong.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-ean"/>
    /// </example>
    public static Barcode Ean13(string digits, EanUpcOptions? options = null)
    {
        options ??= EanUpcOptions.Default;
        string full = WithCheckDigit(digits, 13, "EAN-13", nameof(digits));
        return FromEanUpc(BarcodeFormat.Ean13, EanUpcEncoder.Ean13(full, upcA: false, options.AddOn), options.Height ?? 69, 11);
    }

    /// <summary>
    /// Makes an EAN-8 barcode (ISO/IEC 15420) for small packages. Seven digits get their check digit added; eight are checked.
    /// </summary>
    /// <param name="digits">7 or 8 digits.</param>
    /// <param name="options">The add-on and height, or <see langword="null"/> for <see cref="EanUpcOptions.Default"/>.</param>
    /// <returns>The barcode, with the 8 digits (and the add-on after a space) as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is not 7 or 8 digits, or the check digit is wrong.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-ean"/>
    /// </example>
    public static Barcode Ean8(string digits, EanUpcOptions? options = null)
    {
        options ??= EanUpcOptions.Default;
        string full = WithCheckDigit(digits, 8, "EAN-8", nameof(digits));
        return FromEanUpc(BarcodeFormat.Ean8, EanUpcEncoder.Ean8(full, options.AddOn), options.Height ?? 55, 7);
    }

    /// <summary>
    /// Makes a UPC-A barcode (ISO/IEC 15420). Eleven digits get their check digit added; twelve are checked. With a
    /// <see cref="BarcodeDrawOptions.Font"/>, the first and last digits are drawn outside the symbol, and their bars reach down
    /// with the guard bars.
    /// </summary>
    /// <param name="digits">11 or 12 digits.</param>
    /// <param name="options">The add-on and height, or <see langword="null"/> for <see cref="EanUpcOptions.Default"/>.</param>
    /// <returns>The barcode, with the 12 digits (and the add-on after a space) as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is not 11 or 12 digits, or the check digit is wrong.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-ean"/>
    /// </example>
    public static Barcode UpcA(string digits, EanUpcOptions? options = null)
    {
        options ??= EanUpcOptions.Default;
        string full = WithCheckDigit(digits, 12, "UPC-A", nameof(digits));
        return FromEanUpc(BarcodeFormat.UpcA, EanUpcEncoder.Ean13("0" + full, upcA: true, options.AddOn), options.Height ?? 69, 9);
    }

    /// <summary>
    /// Makes a UPC-E barcode (ISO/IEC 15420), the zero-suppressed form of UPC-A. It takes the six UPC-E digits (number system 0),
    /// the number system (0 or 1) and the six digits, those and the check digit, or a UPC-A number of 11 or 12 digits, which is
    /// compressed to UPC-E.
    /// </summary>
    /// <param name="digits">6, 7 or 8 UPC-E digits, or 11 or 12 UPC-A digits.</param>
    /// <param name="options">The add-on and height, or <see langword="null"/> for <see cref="EanUpcOptions.Default"/>.</param>
    /// <returns>The barcode, with the number system, six digits and check digit (and the add-on after a space) as its human-readable text.</returns>
    /// <exception cref="ArgumentException">
    /// The text is not 6, 7, 8, 11 or 12 digits, the number system is not 0 or 1, the check digit is wrong, or a UPC-A number
    /// has too few zeros to compress.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-ean"/>
    /// </example>
    public static Barcode UpcE(string digits, EanUpcOptions? options = null)
    {
        options ??= EanUpcOptions.Default;
        CheckDigits(digits, nameof(digits));
        string system, six;
        string? given = null;
        switch (digits.Length)
        {
            case 6:
                (system, six) = ("0", digits);
                break;
            case 7 or 8:
                (system, six) = (digits[..1], digits[1..7]);
                given = digits.Length == 8 ? digits[7..] : null;
                break;
            case 11 or 12:
                string upcA = WithCheckDigit(digits, 12, "UPC-A", nameof(digits));
                (system, six) = (upcA[..1], EanUpcEncoder.CompressUpcA(upcA[..11])
                    ?? throw new ArgumentException($"UPC-A {upcA} has too few zeros to write as UPC-E.", nameof(digits)));
                break;
            default:
                throw new ArgumentException($"UPC-E takes 6, 7 or 8 digits, or 11 or 12 UPC-A digits, not {digits.Length}.", nameof(digits));
        }

        if (system is not ("0" or "1"))
        {
            throw new ArgumentException($"The UPC-E number system is 0 or 1, not {system}.", nameof(digits));
        }

        char check = EanUpcEncoder.CheckDigit(EanUpcEncoder.ExpandUpcE(system, six));
        if (given is not null && given[0] != check)
        {
            throw new ArgumentException($"The UPC-E check digit should be {check}, not {given}.", nameof(digits));
        }

        return FromEanUpc(BarcodeFormat.UpcE, EanUpcEncoder.UpcE(system + six + check, options.AddOn), options.Height ?? 69, 9);
    }

    private static Barcode FromEanUpc(BarcodeFormat format, EanUpcEncoder.Symbol symbol, int height, int quietZone) =>
        new(format, BarcodeModules.CreateLinear(symbol.Runs, height), quietZone, symbol.Text, symbol.Decoration);
}
