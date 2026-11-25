using Egami.Barcodes.Linear;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a Code 93 barcode (ANSI/AIM BC5, AIM USS-93): digits, capital letters, space and <c>- . $ / + %</c>, with the two
    /// check characters C and K the symbology requires. Other ASCII characters are written as Full ASCII pairs when
    /// <see cref="Code93Options.FullAscii"/> allows it.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Full ASCII and height, or <see langword="null"/> for <see cref="Code93Options.Default"/>.</param>
    /// <returns>The barcode, with the text as its human-readable text (control characters as spaces); the check characters are not shown.</returns>
    /// <exception cref="ArgumentException">The text is empty, longer than 1000 characters, or has a character Code 93 cannot hold.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-code93"/>
    /// </example>
    public static Barcode Code93(string text, Code93Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckLength(text.Length, nameof(text));
        options ??= Code93Options.Default;
        List<int> values;
        if (Code93Encoder.IsStandard(text))
        {
            values = Code93Encoder.Values(text);
        }
        else if (options.FullAscii == false)
        {
            int bad = text.AsSpan().IndexOfAnyExcept(Code93Encoder.CharacterSet);
            throw new ArgumentException($"Code 93 holds digits, capital letters, space and - . $ / + %; '{text[bad]}' at {bad} needs Full ASCII.", nameof(text));
        }
        else
        {
            int bad = text.AsSpan().IndexOfAnyExceptInRange('\0', '\x7F');
            if (bad >= 0)
            {
                throw new ArgumentException($"Code 93 Full ASCII holds characters 0 to 127; '{text[bad]}' at {bad} is not one.", nameof(text));
            }

            values = Code93Encoder.FullAsciiValues(text);
        }

        CheckLength(values.Count, nameof(text));
        Code93Encoder.AddCheckCharacters(values);
        int[] runs = Code93Encoder.Runs(values);
        return new Barcode(BarcodeFormat.Code93, BarcodeModules.CreateLinear(runs, options.Height ?? 50), 10, ControlsAsSpaces(text));
    }
}
