using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    private static readonly Lazy<Encoding?> s_shiftJis = new(() =>
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    });

    /// <summary>
    /// Makes a QR Code (ISO/IEC 18004) from text. The text is split into numeric, alphanumeric, byte and Kanji segments that
    /// give the fewest bits. Text in ISO 8859-1 is written as it is; text that Shift JIS holds is written in Shift JIS, its kanji
    /// in Kanji mode; other text is written as UTF-8 with ECI 26 before it.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The level, version and mask, or <see langword="null"/> for <see cref="QrOptions.Default"/>: level M, raised while it fits.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentException">The text is empty or does not fit in the symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-qr"/>
    /// </example>
    public static Barcode Qr(string text, QrOptions? options = null)
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
            input = new QrEncoder.Input(Encoding.UTF8.GetBytes(text), Kanji: false, Eci: 26, Gs1: false);
        }

        return FromQr(input, options, nameof(text));
    }

    /// <summary>Makes a QR Code from bytes, written in byte mode (and numeric and alphanumeric where shorter) without an ECI.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="options">The level, version and mask, or <see langword="null"/> for <see cref="QrOptions.Default"/>.</param>
    /// <returns>The barcode, with no human-readable text.</returns>
    /// <exception cref="ArgumentException">There are no bytes, or they do not fit in the symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-qr"/>
    /// </example>
    public static Barcode Qr(ReadOnlySpan<byte> data, QrOptions? options = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("There is no data.", nameof(data));
        }

        return FromQr(new QrEncoder.Input(data.ToArray(), Kanji: false, Eci: 0, Gs1: false), options, nameof(data));
    }

    /// <summary>
    /// Makes a GS1 QR Code: FNC1 in first position and the element strings after it, separated where their AIs need it.
    /// </summary>
    /// <param name="elements">The element strings, from <see cref="Gs1.Parse(string)"/>.</param>
    /// <param name="options">The level, version and mask, or <see langword="null"/> for <see cref="QrOptions.Default"/>.</param>
    /// <returns>The barcode, with the element strings in their human-readable form as its text.</returns>
    /// <exception cref="ArgumentException">There are no element strings, or they do not fit in the symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-qr"/>
    /// </example>
    public static Barcode Qr(IReadOnlyList<Gs1ElementString> elements, QrOptions? options = null)
    {
        byte[] data = Gs1Bytes(elements, nameof(elements));
        Barcode barcode = FromQr(new QrEncoder.Input(data, Kanji: false, Eci: 0, Gs1: true), options, nameof(elements));
        return new Barcode(barcode.Format, barcode.Modules, barcode.QuietZone, string.Concat(elements));
    }

    /// <summary>Returns element strings as ASCII bytes with GS (29) after each field whose AI has no predefined length, except the last.</summary>
    internal static byte[] Gs1Bytes(IReadOnlyList<Gs1ElementString> elements, string parameter)
    {
        ArgumentNullException.ThrowIfNull(elements, parameter);
        if (elements.Count == 0)
        {
            throw new ArgumentException("There are no element strings.", parameter);
        }

        var bytes = new List<byte>();
        for (int i = 0; i < elements.Count; i++)
        {
            Gs1ElementString element = elements[i] ?? throw new ArgumentException("An element string is null.", parameter);
            bytes.AddRange(Encoding.ASCII.GetBytes(element.Ai + element.Value));
            if (!element.PredefinedLength && i < elements.Count - 1)
            {
                bytes.Add(0x1D);
            }
        }

        return [.. bytes];
    }

    private static Barcode FromQr(QrEncoder.Input input, QrOptions? options, string parameter)
    {
        options ??= QrOptions.Default;
        if (input.Bytes.Length > 7089)
        {
            throw new ArgumentException("The data does not fit in a QR Code, which holds at most 7089 digits or 2953 bytes.", parameter);
        }

        int level = options.ErrorCorrection is QrErrorCorrection e ? (int)e : 1;
        QrEncoder.Result result = QrEncoder.Encode(input, level, raiseLevel: options.ErrorCorrection is null, options.Version ?? 0, options.Mask ?? -1)
            ?? throw new ArgumentException(options.Version is int v
                ? $"The data does not fit in a version {v} QR Code at level {(QrErrorCorrection)level}."
                : $"The data does not fit in a QR Code at level {(QrErrorCorrection)level}.", parameter);
        return new Barcode(BarcodeFormat.Qr, result.Modules, 4, null);
    }

    private static byte[]? ShiftJis(string text)
    {
        if (s_shiftJis.Value is not Encoding encoding)
        {
            return null;
        }

        try
        {
            return encoding.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }
}
