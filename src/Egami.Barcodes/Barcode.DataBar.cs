using Egami.Barcodes.Linear;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a GS1 DataBar Omnidirectional barcode (ISO/IEC 24724), or its Truncated, Stacked or Stacked Omnidirectional form:
    /// a GTIN-14 in a small symbol for retail items and fresh foods. Stacked forms are 2D grids of their rows.
    /// </summary>
    /// <param name="gtin">The GTIN: 13 digits, and the check digit is added, or 14 with the check digit, which is checked. Shorter GTINs (GTIN-8, -12 and -13) take leading zeros.</param>
    /// <param name="options">The form, or <see langword="null"/> for <see cref="DataBarOptions.Default"/>.</param>
    /// <returns>The barcode, with the AI and GTIN-14, such as <c>(01)09501101530003</c>, as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The GTIN is not 13 or 14 digits, or its check digit is wrong.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-databar"/>
    /// </example>
    public static Barcode DataBar(string gtin, DataBarOptions? options = null)
    {
        string digits = WithCheckDigit(gtin, 14, "GS1 DataBar", nameof(gtin));
        options ??= DataBarOptions.Default;
        int[] widths = DataBarEncoder.OmniWidths(long.Parse(digits.AsSpan(0, 13), System.Globalization.CultureInfo.InvariantCulture));
        BarcodeModules modules = options.Kind switch
        {
            DataBarKind.Truncated => BarcodeModules.WrapLinear(DataBarEncoder.Runs(widths), 13),
            DataBarKind.Stacked => DataBarEncoder.Stacked(widths),
            DataBarKind.StackedOmnidirectional => DataBarEncoder.StackedOmni(widths),
            _ => BarcodeModules.WrapLinear(DataBarEncoder.Runs(widths), 33),
        };
        return new Barcode(BarcodeFormat.DataBar, modules, 1, "(01)" + digits);
    }
}
