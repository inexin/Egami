using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a PDF417 symbol (ISO/IEC 15438) from text, in text, numeric and byte compaction chosen run by run. Text in ISO
    /// 8859-1 is written as it is; other text is written as UTF-8 with ECI 26 before it. Each row is 3 modules tall.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The columns, error level and compact form, or <see langword="null"/> for <see cref="Pdf417Options.Default"/>.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentException">The text is empty or does not fit in a symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-pdf417"/>
    /// </example>
    public static Barcode Pdf417(string text, Pdf417Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("The text is empty.", nameof(text));
        }

        bool latin1 = !text.AsSpan().ContainsAnyExceptInRange('\0', '\xFF');
        return FromPdf417(latin1 ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text), latin1 ? 0 : 26, options, nameof(text));
    }

    /// <summary>Makes a PDF417 symbol from bytes, without an ECI.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="options">The columns, error level and compact form, or <see langword="null"/> for <see cref="Pdf417Options.Default"/>.</param>
    /// <returns>The barcode, with no human-readable text.</returns>
    /// <exception cref="ArgumentException">There are no bytes, or they do not fit in a symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-pdf417"/>
    /// </example>
    public static Barcode Pdf417(ReadOnlySpan<byte> data, Pdf417Options? options = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("There is no data.", nameof(data));
        }

        return FromPdf417(data.ToArray(), 0, options, nameof(data));
    }

    private static Barcode FromPdf417(byte[] data, int eci, Pdf417Options? options, string parameter)
    {
        options ??= Pdf417Options.Default;
        if (data.Length > 2710)
        {
            throw new ArgumentException("The data does not fit in a PDF417 symbol, which holds at most 2710 digits or 1850 text characters.", parameter);
        }

        Pdf417Encoder.Result result = Pdf417Encoder.Encode(data, eci, options.Columns, options.ErrorLevel, options.Compact)
            ?? throw new ArgumentException("The data does not fit in a PDF417 symbol with these options.", parameter);
        return new Barcode(BarcodeFormat.Pdf417, result.Modules, 2, null);
    }
}
