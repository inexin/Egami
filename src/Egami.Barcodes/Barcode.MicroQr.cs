using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a Micro QR Code (ISO/IEC 18004, versions M1 to M4, 11 x 11 to 17 x 17 modules) from text. The text is split into
    /// numeric, alphanumeric, byte and Kanji segments that give the fewest bits, as far as the version has the modes: M1 holds
    /// digits only, M2 adds alphanumeric, M3 and M4 bytes and Kanji. Micro QR has no ECI, so the text must be ISO 8859-1 or
    /// what Shift JIS holds. At most 35 digits, 21 alphanumeric characters or 15 bytes fit (M4 at level L).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The level and smallest version, or <see langword="null"/> for <see cref="MicroQrOptions.Default"/>.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentException">The text is empty, holds characters Micro QR cannot, or does not fit.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-micro-qr"/>
    /// </example>
    public static Barcode MicroQr(string text, MicroQrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("The text is empty.", nameof(text));
        }

        QrEncoder.Input input;
        if (!text.AsSpan().ContainsAnyExceptInRange('\0', '\xFF'))
        {
            input = new QrEncoder.Input(Encoding.Latin1.GetBytes(text), Kanji: false, Eci: 0, Gs1: false);
        }
        else if (ShiftJis(text) is byte[] shiftJis)
        {
            input = new QrEncoder.Input(shiftJis, Kanji: true, Eci: 0, Gs1: false);
        }
        else
        {
            throw new ArgumentException("Micro QR has no ECI: the text must be ISO 8859-1 or Shift JIS.", nameof(text));
        }

        options ??= MicroQrOptions.Default;
        int level = (int)options.ErrorCorrection;
        MicroQrEncoder.Result result = (input.Bytes.Length <= 35
            ? MicroQrEncoder.Encode(input, level, options.RaiseErrorCorrection, options.MinVersion ?? 1, -1)
            : null)
            ?? throw new ArgumentException(
                $"The text does not fit in a Micro QR Code{(options.MinVersion is int v ? $" from M{v}" : string.Empty)} at level {options.ErrorCorrection}.",
                nameof(text));
        return new Barcode(BarcodeFormat.MicroQr, result.Modules, 2, null);
    }
}
