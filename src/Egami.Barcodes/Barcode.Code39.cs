using Egami.Barcodes.Gs1Data;
using Egami.Barcodes.Linear;
using Egami.Barcodes.Rendering;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a Code 39 barcode (ISO/IEC 16388): digits, capital letters, space and <c>- . $ / + %</c>. Other ASCII characters are
    /// written as Full ASCII pairs when <see cref="Code39Options.FullAscii"/> allows it.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The check character, Full ASCII, wide ratio and height, or <see langword="null"/> for <see cref="Code39Options.Default"/>.</param>
    /// <returns>The barcode, with the text (and a check character, when there is one and the text is not Full ASCII) as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is empty, longer than 1000 characters, or has a character Code 39 cannot hold.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-code39"/>
    /// </example>
    public static Barcode Code39(string text, Code39Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckLength(text.Length, nameof(text));
        options ??= Code39Options.Default;
        bool fullAscii = options.FullAscii ?? !Code39Encoder.IsStandard(text);
        string encoded;
        if (fullAscii)
        {
            int bad = text.AsSpan().IndexOfAnyExceptInRange('\0', '\x7F');
            if (bad >= 0)
            {
                throw new ArgumentException($"Code 39 Full ASCII holds characters 0 to 127; '{text[bad]}' at {bad} is not one.", nameof(text));
            }

            encoded = Code39Encoder.ToFullAscii(text);
        }
        else
        {
            int bad = text.AsSpan().IndexOfAnyExcept(Code39Encoder.CharacterSet);
            if (bad >= 0)
            {
                throw new ArgumentException($"Code 39 holds digits, capital letters, space and - . $ / + %; '{text[bad]}' at {bad} needs Full ASCII.", nameof(text));
            }

            encoded = text;
        }

        CheckLength(encoded.Length, nameof(text));
        string readable = text;
        if (options.CheckDigit)
        {
            char check = Code39Encoder.CheckCharacter(encoded);
            encoded += check;
            readable = fullAscii ? text : text + check;
        }

        int[] runs = Code39Encoder.Runs(encoded, options.WideRatio ?? 2);
        return new Barcode(BarcodeFormat.Code39, BarcodeModules.CreateLinear(runs, options.Height ?? 50), 10, ControlsAsSpaces(readable));
    }

    /// <summary>
    /// Makes an Interleaved 2 of 5 barcode (ISO/IEC 16390) from digits; an odd number of digits gets a leading zero, as the
    /// symbology holds pairs.
    /// </summary>
    /// <param name="digits">The digits.</param>
    /// <param name="options">The wide ratio, bearer bars and height, or <see langword="null"/> for <see cref="ItfOptions.Default"/>.</param>
    /// <returns>The barcode, with the digits it holds as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is empty, longer than 1000 characters, or has a character that is not a digit.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-itf"/>
    /// </example>
    public static Barcode Itf(string digits, ItfOptions? options = null)
    {
        CheckDigits(digits, nameof(digits));
        CheckLength(digits.Length, nameof(digits));
        return FromItf(digits.Length % 2 == 0 ? digits : "0" + digits, options ?? ItfOptions.Default, bearers: false);
    }

    /// <summary>
    /// Makes an ITF-14 barcode (GS1): a GTIN-14 as Interleaved 2 of 5, framed by bearer bars. Thirteen digits get their check digit
    /// added; fourteen are checked.
    /// </summary>
    /// <param name="digits">13 or 14 digits.</param>
    /// <param name="options">The wide ratio, bearer bars and height, or <see langword="null"/> for <see cref="ItfOptions.Default"/>.</param>
    /// <returns>The barcode, with the 14 digits as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is not 13 or 14 digits, or the check digit is wrong.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-itf"/>
    /// </example>
    public static Barcode Itf14(string digits, ItfOptions? options = null) =>
        FromItf(WithCheckDigit(digits, 14, "ITF-14", nameof(digits)), options ?? ItfOptions.Default, bearers: true);

    private static Barcode FromItf(string digits, ItfOptions options, bool bearers)
    {
        int[] runs = ItfEncoder.Runs(digits, options.WideRatio ?? 3);
        LinearDecoration? decoration = options.BearerBars ?? bearers ? new LinearDecoration(null, -1, 5, null) : null;
        return new Barcode(BarcodeFormat.Itf, BarcodeModules.CreateLinear(runs, options.Height ?? 50), 10, digits, decoration);
    }

    /// <summary>Returns the digits with their GS1 check digit: added when one short of the length, checked when at the length.</summary>
    private static string WithCheckDigit(string digits, int length, string name, string parameter)
    {
        CheckDigits(digits, parameter);
        if (digits.Length == length - 1)
        {
            return digits + (char)('0' + Gs1Checks.CheckDigit(digits));
        }

        if (digits.Length != length)
        {
            throw new ArgumentException($"{name} takes {length - 1} digits, or {length} with the check digit, not {digits.Length}.", parameter);
        }

        int check = Gs1Checks.CheckDigit(digits.AsSpan(0, length - 1));
        return digits[^1] - '0' == check ? digits : throw new ArgumentException($"The {name} check digit should be {check}, not {digits[^1]}.", parameter);
    }

    private static void CheckDigits(string digits, string parameter)
    {
        ArgumentNullException.ThrowIfNull(digits, parameter);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The text must be digits 0 to 9.", parameter);
        }
    }

    private static string ControlsAsSpaces(string text)
    {
        // char.IsControl: U+0000 to U+001F and U+007F to U+009F. Most text has none and is returned as it is.
        if (!text.AsSpan().ContainsAnyInRange('\0', '\x1F') && !text.AsSpan().ContainsAnyInRange('\x7F', '\x9F'))
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, text) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(text[i]) ? ' ' : text[i];
            }
        });
    }
}
