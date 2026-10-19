using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a Data Matrix ECC 200 symbol (ISO/IEC 16022) from text. The six encodation modes are switched for the fewest
    /// codewords. Text in ISO 8859-1 is written as it is; other text is written as UTF-8 with ECI 26 before it.
    /// </summary>
    /// <param name="text">The text, at most 3116 characters.</param>
    /// <param name="options">The shape, or <see langword="null"/> for <see cref="DataMatrixOptions.Default"/>: square.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentException">The text is empty or does not fit in the largest allowed symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-datamatrix"/>
    /// </example>
    public static Barcode DataMatrix(string text, DataMatrixOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("The text is empty.", nameof(text));
        }

        bool latin1 = !text.AsSpan().ContainsAnyExceptInRange('\0', '\xFF');
        byte[] bytes = latin1 ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);
        return FromDataMatrix(new DataMatrixEncoder.Input(bytes, latin1 ? 0 : 26, Gs1: false), options, nameof(text), null);
    }

    /// <summary>Makes a Data Matrix symbol from bytes, without an ECI.</summary>
    /// <param name="data">The bytes, at most 3116.</param>
    /// <param name="options">The shape, or <see langword="null"/> for <see cref="DataMatrixOptions.Default"/>.</param>
    /// <returns>The barcode, with no human-readable text.</returns>
    /// <exception cref="ArgumentException">There are no bytes, or they do not fit in the largest allowed symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-datamatrix"/>
    /// </example>
    public static Barcode DataMatrix(ReadOnlySpan<byte> data, DataMatrixOptions? options = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("There is no data.", nameof(data));
        }

        return FromDataMatrix(new DataMatrixEncoder.Input(data.ToArray(), 0, Gs1: false), options, nameof(data), null);
    }

    /// <summary>Makes a GS1 Data Matrix symbol: FNC1 first and the element strings, separated by FNC1 where their AIs need it.</summary>
    /// <param name="elements">The element strings, from <see cref="Gs1.Parse(string)"/>.</param>
    /// <param name="options">The shape, or <see langword="null"/> for <see cref="DataMatrixOptions.Default"/>.</param>
    /// <returns>The barcode, with the element strings in their human-readable form as its text.</returns>
    /// <exception cref="ArgumentException">There are no element strings, or they do not fit in the largest allowed symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-datamatrix"/>
    /// </example>
    public static Barcode DataMatrix(IReadOnlyList<Gs1ElementString> elements, DataMatrixOptions? options = null)
    {
        byte[] data = Gs1Bytes(elements, nameof(elements));
        return FromDataMatrix(new DataMatrixEncoder.Input(data, 0, Gs1: true), options, nameof(elements), string.Concat(elements));
    }

    private static Barcode FromDataMatrix(DataMatrixEncoder.Input input, DataMatrixOptions? options, string parameter, string? text)
    {
        options ??= DataMatrixOptions.Default;
        if (input.Bytes.Length > 3116)
        {
            throw new ArgumentException("The data does not fit in a Data Matrix symbol, which holds at most 3116 digits or 1555 bytes.", parameter);
        }

        var shapes = new DataMatrixEncoder.Shapes(
            options.Shape is DataMatrixShape.Square or DataMatrixShape.Any,
            options.Shape is DataMatrixShape.Rectangle or DataMatrixShape.Any,
            options.AllowDmre);
        (DataMatrixTables.Size _, BarcodeModules modules) = DataMatrixEncoder.Encode(input, shapes)
            ?? throw new ArgumentException($"The data does not fit in a Data Matrix symbol of shape {options.Shape}.", parameter);
        return new Barcode(BarcodeFormat.DataMatrix, modules, 1, text);
    }
}
