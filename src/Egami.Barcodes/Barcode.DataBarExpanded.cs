using System.Text;

using Egami.Barcodes.Linear;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a GS1 DataBar Expanded barcode (ISO/IEC 24724), or Expanded Stacked when <see cref="DataBarExpandedOptions.SegmentsPerRow"/>
    /// is set: GS1 element strings, such as a GTIN with a weight, price, batch or date, in up to 74 digits or 41 letters, for
    /// variable-measure and fresh goods and coupons. Expanded Stacked is a 2D grid of its rows.
    /// </summary>
    /// <param name="elements">The element strings, from <see cref="Gs1.Parse(string)"/> or <see cref="Gs1ElementString.Create(string, string)"/>.</param>
    /// <param name="options">The rows, or <see langword="null"/> for <see cref="DataBarExpandedOptions.Default"/>.</param>
    /// <returns>The barcode, with the element strings, each AI in parentheses, as its human-readable text.</returns>
    /// <exception cref="ArgumentException">There are no element strings, one has a character DataBar Expanded cannot hold, or they need more than 21 data characters.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-databar-expanded"/>
    /// </example>
    public static Barcode DataBarExpanded(IReadOnlyList<Gs1ElementString> elements, DataBarExpandedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
        {
            throw new ArgumentException("There are no element strings.", nameof(elements));
        }

        options ??= DataBarExpandedOptions.Default;
        var data = new StringBuilder();
        for (int i = 0; i < elements.Count; i++)
        {
            Gs1ElementString element = elements[i] ?? throw new ArgumentException("An element string is null.", nameof(elements));
            data.Append(element.Ai).Append(element.Value);
            if (!element.PredefinedLength && i < elements.Count - 1)
            {
                data.Append('\x1D');
            }
        }

        int columns = (options.SegmentsPerRow ?? 0) / 2;
        int[] widths = DataBarExpandedEncoder.Elements(data.ToString(), ref columns, out int symbolCharacters, out string? error)
            ?? throw new ArgumentException(error, nameof(elements));
        string text = string.Concat(elements);
        BarcodeModules modules = columns > 0 && symbolCharacters > 2 * columns
            ? DataBarExpandedEncoder.Stacked(widths, symbolCharacters, columns, 34)
            : BarcodeModules.WrapLinear(DataBarEncoder.Runs(DataBarExpandedEncoder.WithGuards(widths)), 34);
        return new Barcode(BarcodeFormat.DataBarExpanded, modules, 1, text);
    }

    /// <summary>
    /// Makes a GS1 DataBar Expanded barcode, or Expanded Stacked, from element strings written with each AI in parentheses.
    /// </summary>
    /// <param name="gs1">The element strings, such as <c>(01)09501101530003(3103)000123</c>; see <see cref="Gs1.Parse(string)"/>.</param>
    /// <param name="options">The rows, or <see langword="null"/> for <see cref="DataBarExpandedOptions.Default"/>.</param>
    /// <returns>The barcode, with the element strings as its human-readable text.</returns>
    /// <exception cref="FormatException">The text is not valid element strings.</exception>
    /// <exception cref="ArgumentException">An element string has a character DataBar Expanded cannot hold, or they need more than 21 data characters.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-databar-expanded"/>
    /// </example>
    public static Barcode DataBarExpanded(string gs1, DataBarExpandedOptions? options = null) => DataBarExpanded(Gs1.Parse(gs1), options);
}
