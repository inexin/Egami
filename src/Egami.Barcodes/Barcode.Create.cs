namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a barcode of a format chosen at run time, with that format's default options, for callers that store the format
    /// with the data. It calls the format's own factory, such as <see cref="Qr(string, QrOptions?)"/> or
    /// <see cref="Ean13(string, EanUpcOptions?)"/>.
    /// </summary>
    /// <param name="format">The symbology.</param>
    /// <param name="text">The text or digits.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a <see cref="BarcodeFormat"/>.</exception>
    /// <exception cref="ArgumentException">The text does not suit the format or does not fit in a symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-create"/>
    /// </example>
    public static Barcode Create(BarcodeFormat format, string text) => format switch
    {
        BarcodeFormat.Qr => Qr(text),
        BarcodeFormat.DataMatrix => DataMatrix(text),
        BarcodeFormat.Pdf417 => Pdf417(text),
        BarcodeFormat.Aztec => Aztec(text),
        BarcodeFormat.Code128 => Code128(text),
        BarcodeFormat.Code39 => Code39(text),
        BarcodeFormat.Itf => Itf(text),
        BarcodeFormat.Ean13 => Ean13(text),
        BarcodeFormat.Ean8 => Ean8(text),
        BarcodeFormat.UpcA => UpcA(text),
        BarcodeFormat.UpcE => UpcE(text),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown barcode format."),
    };
}
