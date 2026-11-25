using Egami.Barcodes.Linear;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a Codabar barcode (EN 798): digits and <c>- $ : / . +</c> between a start and a stop character, A to D. Text that
    /// begins and ends with A to D keeps those as its start and stop characters; other text gets
    /// <see cref="CodabarOptions.Start"/> and <see cref="CodabarOptions.Stop"/>.
    /// </summary>
    /// <param name="text">The text, with or without its start and stop characters.</param>
    /// <param name="options">The start and stop characters, check character, wide ratio and height, or <see langword="null"/> for <see cref="CodabarOptions.Default"/>.</param>
    /// <returns>
    /// The barcode, with every character of the symbol as its human-readable text: start, text, check character when there is
    /// one, and stop, as readers return it.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The text is empty, longer than 1000 characters, gives a start character A to D without a stop character (or the
    /// reverse), has another character Codabar cannot hold, or makes a symbol of fewer than four characters (start, two
    /// characters or one and the check character, stop).
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-codabar"/>
    /// </example>
    public static Barcode Codabar(string text, CodabarOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckLength(text.Length, nameof(text));
        options ??= CodabarOptions.Default;
        char start = options.Start, stop = options.Stop;
        ReadOnlySpan<char> data = text;
        bool startGiven = CodabarEncoder.IsStartStop(text[0]), stopGiven = text.Length > 1 && CodabarEncoder.IsStartStop(text[^1]);
        if (startGiven != stopGiven)
        {
            throw new ArgumentException("Codabar text that gives its start or stop character, A to D, must give both.", nameof(text));
        }

        if (startGiven)
        {
            (start, stop) = (text[0], text[^1]);
            data = data[1..^1];
        }

        int bad = data.IndexOfAnyExcept(CodabarEncoder.DataSet);
        if (data.IsEmpty)
        {
            throw new ArgumentException("Codabar needs at least one character between its start and stop characters.", nameof(text));
        }

        if (bad >= 0)
        {
            throw new ArgumentException($"Codabar holds digits and - $ : / . + between the start and stop characters A to D; '{data[bad]}' at {bad + (startGiven ? 1 : 0)} is not one.", nameof(text));
        }

        string symbol = start + data.ToString() + stop;
        if (options.CheckDigit)
        {
            symbol = symbol[..^1] + CodabarEncoder.CheckCharacter(symbol) + stop;
        }

        if (symbol.Length < CodabarEncoder.MinimumLength)
        {
            throw new ArgumentException("Codabar needs two characters between its start and stop characters, or one and a check character: readers ignore shorter symbols, which noise can look like.", nameof(text));
        }

        CheckLength(symbol.Length, nameof(text));
        int[] runs = CodabarEncoder.Runs(symbol, options.WideRatio ?? 2);
        return new Barcode(BarcodeFormat.Codabar, BarcodeModules.CreateLinear(runs, options.Height ?? 50), 10, symbol);
    }
}
