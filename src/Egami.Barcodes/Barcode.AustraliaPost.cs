using System.Globalization;

using Egami.Barcodes.Postal;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes an Australia Post 4-State Customer Barcode: the eight-digit Delivery Point Identifier (DPID), followed for Customer
    /// Barcodes 2 and 3 by the customer information, such as <c>39549554</c> or <c>54516251ABC123</c>. The Format Control Code,
    /// filler bars and four Reed-Solomon check symbols are added. The modules are a grid of 8 rows (3 for the ascender, 2 for
    /// the tracker, 3 for the descender); draw it at a module size of 0.55 mm for bars 4.4 mm tall at 23 bars per 25.4 mm,
    /// within Australia Post's sizes.
    /// </summary>
    /// <param name="text">The DPID and the customer information.</param>
    /// <param name="options">The format and the customer information's encoding table, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The barcode, with the text representation Australia Post gives as its human-readable text: the Format Control Code, the
    /// DPID, the customer information and the four check symbols as two-digit numbers, such as <c>11 39549554 54 17 53 58</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The text does not start with eight digits, has customer information for a format without it, has a character the
    /// encoding table lacks, or holds more customer information than the format does.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-australia-post"/>
    /// </example>
    public static Barcode AustraliaPost(string text, AustraliaPostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= AustraliaPostOptions.Default;
        int bad = text.AsSpan(0, Math.Min(8, text.Length)).IndexOfAnyExceptInRange('0', '9');
        if (bad >= 0)
        {
            throw new ArgumentException($"An Australia Post barcode starts with the eight digits of the DPID; '{text[bad]}' at {bad} is not one.", nameof(text));
        }

        if (text.Length < 8)
        {
            throw new ArgumentException($"An Australia Post barcode starts with the eight digits of the DPID, not {text.Length}.", nameof(text));
        }

        string information = text[8..];
        AustraliaPostEncoding encoding = options.Encoding ?? (information.AsSpan().ContainsAnyExceptInRange('0', '9') ? AustraliaPostEncoding.C : AustraliaPostEncoding.N);
        if (encoding is not (AustraliaPostEncoding.N or AustraliaPostEncoding.C))
        {
            throw new ArgumentOutOfRangeException(nameof(options), encoding, "Unknown Australia Post encoding.");
        }

        for (int i = 0; i < information.Length; i++)
        {
            if (encoding == AustraliaPostEncoding.N ? !char.IsAsciiDigit(information[i]) : !AustraliaPostEncoder.CCharacters.Contains(information[i], StringComparison.Ordinal))
            {
                string table = encoding == AustraliaPostEncoding.N ? "the N table holds digits" : "the C table holds letters, digits, space and #";
                throw new ArgumentException($"In an Australia Post barcode's customer information {table}; '{information[i]}' at {i + 8} is not one.", nameof(text));
            }
        }

        int bars = information.Length * (encoding == AustraliaPostEncoding.N ? 2 : 3);
        AustraliaPostFormat format = options.Format ?? (bars == 0 ? AustraliaPostFormat.Standard : bars <= 16 ? AustraliaPostFormat.Customer2 : AustraliaPostFormat.Customer3);
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(options), format, "Unknown Australia Post format.");
        }

        int room = AustraliaPostEncoder.DataBars(format) - 23;
        if (bars > (room == 0 ? 0 : room + 1))
        {
            throw new ArgumentException(room == 0
                ? $"The Australia Post {format} format holds the DPID alone, without customer information."
                : $"The customer information takes {bars} bars; Australia Post's {format} format holds {room + 1}.", nameof(text));
        }

        byte[] values = AustraliaPostEncoder.Values(format, text.AsSpan(0, 8), information, encoding, out int[] check);
        string readable = string.Join(' ', [((int)format).ToString(CultureInfo.InvariantCulture), text[..8], .. information.Length > 0 ? [information] : (string[])[], .. check.Select(c => c.ToString("D2", CultureInfo.InvariantCulture))]);
        return new Barcode(BarcodeFormat.AustraliaPost, FourStateGrid.Create(AustraliaPostEncoder.Bars(values), 3, 2), 11, readable);
    }
}
