
using Egami.Barcodes.Linear;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>The most characters a linear symbol's text may have, so a symbol stays printable.</summary>
    internal const int MaxLinearLength = 1000;

    /// <summary>
    /// Makes a Code 128 barcode (ISO/IEC 15417). The code sets A, B and C, and the changes and shifts between them, are picked to
    /// give the shortest symbol; runs of digits go in pairs in code set C. Characters 128 to 255 (ISO 8859-1) take an FNC4 each.
    /// </summary>
    /// <param name="text">The text, of characters 0 to 255.</param>
    /// <param name="options">The bar height, or <see langword="null"/> for <see cref="Code128Options.Default"/>.</param>
    /// <returns>The barcode, with the text as its human-readable text (control characters shown as spaces).</returns>
    /// <exception cref="ArgumentException">The text is empty, longer than 1000 characters, or has a character above 255.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-code128"/>
    /// </example>
    public static Barcode Code128(string text, Code128Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckLength(text.Length, nameof(text));
        int[] input = new int[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            input[i] = text[i] <= 255 ? text[i] : throw new ArgumentException($"Code 128 holds characters 0 to 255; '{text[i]}' at {i} is not one.", nameof(text));
        }

        return FromCode128(input, ControlsAsSpaces(text), options);
    }

    /// <summary>
    /// Makes a GS1-128 barcode: Code 128 with FNC1 first and the element strings after it, an FNC1 after each one whose AI has no
    /// predefined length unless it is the last.
    /// </summary>
    /// <param name="elements">The element strings, from <see cref="Gs1.Parse(string)"/> or <see cref="Gs1ElementString.Create(string, string)"/>.</param>
    /// <param name="options">The bar height, or <see langword="null"/> for <see cref="Code128Options.Default"/>.</param>
    /// <returns>The barcode, with the element strings in their human-readable form as its text.</returns>
    /// <exception cref="ArgumentException">There are no element strings, or more than 1000 characters.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-gs1"/>
    /// </example>
    public static Barcode Gs1Code128(IReadOnlyList<Gs1ElementString> elements, Code128Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
        {
            throw new ArgumentException("GS1-128 needs at least one element string.", nameof(elements));
        }

        var input = new List<int> { Code128Encoder.Fnc1 };
        for (int i = 0; i < elements.Count; i++)
        {
            Gs1ElementString element = elements[i] ?? throw new ArgumentException("An element string is null.", nameof(elements));
            input.AddRange(element.Ai.Select(c => (int)c));
            input.AddRange(element.Value.Select(c => (int)c));
            if (!element.PredefinedLength && i < elements.Count - 1)
            {
                input.Add(Code128Encoder.Fnc1);
            }
        }

        CheckLength(input.Count, nameof(elements));
        return FromCode128([.. input], string.Concat(elements), options);
    }

    /// <summary>Makes a GS1-128 barcode from element strings in their human-readable form, such as <c>(01)09501101530003(10)AB12</c>.</summary>
    /// <param name="elements">The element strings, each AI in parentheses; see <see cref="Gs1.Parse(string)"/>.</param>
    /// <param name="options">The bar height, or <see langword="null"/> for <see cref="Code128Options.Default"/>.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="FormatException">The text is not valid element strings; the message says which and how.</exception>
    /// <exception cref="ArgumentException">There are more than 1000 characters.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-gs1"/>
    /// </example>
    public static Barcode Gs1Code128(string elements, Code128Options? options = null) => Gs1Code128(Gs1.Parse(elements), options);

    private static Barcode FromCode128(int[] input, string readable, Code128Options? options)
    {
        int[] runs = Code128Encoder.Runs(Code128Encoder.Encode(input));
        int height = (options ?? Code128Options.Default).Height ?? 50;
        return new Barcode(BarcodeFormat.Code128, BarcodeModules.CreateLinear(runs, height), 10, readable);
    }

    private static void CheckLength(int length, string name)
    {
        if (length == 0)
        {
            throw new ArgumentException("The text is empty.", name);
        }

        if (length > MaxLinearLength)
        {
            throw new ArgumentException($"A linear barcode holds at most {MaxLinearLength} characters, not {length}.", name);
        }
    }
}
