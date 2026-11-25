using Egami.Barcodes.Linear;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a GS1 DataBar Limited barcode (ISO/IEC 24724): a GTIN-14 that starts with 0 or 1 in a short symbol, 79 modules
    /// wide, for small items that are not scanned at the point of sale at any angle.
    /// </summary>
    /// <param name="gtin">The GTIN: 13 digits, and the check digit is added, or 14 with the check digit, which is checked; it must start with 0 or 1. Shorter GTINs take leading zeros.</param>
    /// <returns>The barcode, with the AI and GTIN-14, such as <c>(01)09501101530003</c>, as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The GTIN is not 13 or 14 digits, starts with a digit other than 0 or 1, or its check digit is wrong.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-databar"/>
    /// </example>
    public static Barcode DataBarLimited(string gtin)
    {
        string digits = WithCheckDigit(gtin, 14, "GS1 DataBar Limited", nameof(gtin));
        if (digits[0] > '1')
        {
            throw new ArgumentException($"GS1 DataBar Limited holds GTINs that start with 0 or 1, not {digits[0]}.", nameof(gtin));
        }

        int[] widths = DataBarEncoder.LimitedWidths(long.Parse(digits.AsSpan(0, 13), System.Globalization.CultureInfo.InvariantCulture));
        return new Barcode(BarcodeFormat.DataBarLimited, BarcodeModules.WrapLinear(DataBarEncoder.Runs(widths), 10), 5, "(01)" + digits);
    }
}
