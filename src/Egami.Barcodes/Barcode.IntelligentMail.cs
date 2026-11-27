using Egami.Barcodes.Postal;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a USPS Intelligent Mail barcode (USPS-B-3200): a 20-digit tracking code (barcode identifier, service type,
    /// mailer identifier and serial number) and a routing code of 0, 5, 9 or 11 digits (the ZIP Code, ZIP+4 or delivery point),
    /// encoded in 65 bars with an 11-bit frame check. The modules are a grid of 6 rows (2 for the ascender, 2 for the tracker,
    /// 2 for the descender); draw it at a module size of 1/44 inch for bars 0.136 inch tall at 22 bars per inch, mid USPS's
    /// ranges.
    /// </summary>
    /// <param name="tracking">The 20 digits of the tracking code; the second digit is 0 to 4.</param>
    /// <param name="routing">The 0, 5, 9 or 11 digits of the routing code, or <see langword="null"/> for none.</param>
    /// <returns>The barcode, with the tracking code and, after a space, the routing code as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The tracking code is not 20 digits or its second digit is above 4, or the routing code is not 0, 5, 9 or 11 digits.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-intelligent-mail"/>
    /// </example>
    public static Barcode IntelligentMail(string tracking, string? routing = null)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        routing ??= "";
        CheckDigits(tracking, nameof(tracking), "The tracking code");
        CheckDigits(routing, nameof(routing), "The routing code");
        if (tracking.Length != 20)
        {
            throw new ArgumentException($"The tracking code has 20 digits, not {tracking.Length}.", nameof(tracking));
        }

        if (tracking[1] > '4')
        {
            throw new ArgumentException($"The tracking code's second digit, the barcode identifier's, is 0 to 4; '{tracking[1]}' at 1 is not.", nameof(tracking));
        }

        if (routing.Length is not (0 or 5 or 9 or 11))
        {
            throw new ArgumentException($"The routing code has 0, 5, 9 or 11 digits, not {routing.Length}.", nameof(routing));
        }

        FourStateBar[] bars = IntelligentMailEncoder.Bars(tracking, routing);
        return new Barcode(BarcodeFormat.IntelligentMail, FourStateGrid.Create(bars, 2, 2), 6, routing.Length == 0 ? tracking : $"{tracking} {routing}");

        static void CheckDigits(string digits, string parameter, string name)
        {
            int bad = digits.AsSpan().IndexOfAnyExceptInRange('0', '9');
            if (bad >= 0)
            {
                throw new ArgumentException($"{name} holds digits; '{digits[bad]}' at {bad} is not one.", parameter);
            }
        }
    }
}
