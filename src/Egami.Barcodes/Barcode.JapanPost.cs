using Egami.Barcodes.Postal;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a Japan Post customer barcode (カスタマバーコード) from prepared data: the 7-digit postal code, with or without its
    /// hyphen (<c>NNN-NNNN</c>), then the address number, the digits, hyphens and letters A to Z the manual's extraction rules
    /// leave of the address, such as <c>100-0013 1-3-2-503</c>. Spaces, or one hyphen, between the postal code and the address
    /// number are dropped, and lowercase letters are made uppercase. The symbol holds 20 characters: the postal code takes 7, a
    /// letter 2 (a control code and a digit), and CC4 pads the rest; an address number that does not fit loses its end, as the
    /// manual says, unless <see cref="JapanPostOptions.TruncateAddress"/> is off. The modules are a grid of 6 rows, one per 0.6 mm
    /// at the manual's size; draw it at a module size of 0.6 mm for a 79.8 by 3.6 mm symbol.
    /// </summary>
    /// <param name="data">The postal code and the address number.</param>
    /// <param name="options">Whether a long address number is cut, or <see langword="null"/> for <see cref="JapanPostOptions.Default"/>.</param>
    /// <returns>
    /// The barcode, with the postal code (with its hyphen) and the address number, after a space, as its human-readable text;
    /// the whole address number, also when the symbol holds only its start.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The data does not start with a 7-digit postal code, the address number has a character other than a digit, a hyphen or a
    /// letter, or it does not fit and <see cref="JapanPostOptions.TruncateAddress"/> is off.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-japan-post"/>
    /// </example>
    public static Barcode JapanPost(string data, JapanPostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        CheckLength(data.Length, nameof(data));
        options ??= JapanPostOptions.Default;
        Span<char> postalCode = stackalloc char[7];
        int at = 0;
        for (int digits = 0; digits < 7; digits++, at++)
        {
            if (digits == 3 && at < data.Length && data[at] == '-')
            {
                at++;
            }

            if (at >= data.Length || !char.IsAsciiDigit(data[at]))
            {
                string found = at < data.Length ? $"'{data[at]}' at {at} is not a digit" : "the data ends early";
                throw new ArgumentException($"Japan Post data starts with the 7-digit postal code, NNNNNNN or NNN-NNNN; {found}.", nameof(data));
            }

            postalCode[digits] = data[at];
        }

        while (at < data.Length && data[at] == ' ')
        {
            at++;
        }

        if (at < data.Length && data[at] == '-')
        {
            at++;
            while (at < data.Length && data[at] == ' ')
            {
                at++;
            }
        }

        int start = at;
        char[] address = new char[data.Length - start];
        for (; at < data.Length; at++)
        {
            char c = char.ToUpperInvariant(data[at]);
            if (!(char.IsAsciiDigit(c) || c == '-' || char.IsAsciiLetterUpper(c)))
            {
                throw new ArgumentException($"A Japan Post address number holds digits, hyphens and the letters A to Z; '{data[at]}' at {at} is not one.", nameof(data));
            }

            address[at - start] = c;
        }

        byte[] values = JapanPostEncoder.Values(postalCode, address, options.TruncateAddress)
            ?? throw new ArgumentException($"The address number '{new string(address)}' does not fit: a Japan Post barcode holds 13 characters after the postal code, a letter taking 2.", nameof(data));
        string text = $"{postalCode[..3]}-{postalCode[3..]}" + (address.Length > 0 ? " " + new string(address) : "");
        return new Barcode(BarcodeFormat.JapanPost, JapanPostEncoder.Grid(values), 4, text);
    }
}
