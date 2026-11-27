using Egami.Barcodes.Postal;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes a Royal Mail 4-State Customer Code (RM4SCC, the CBC of Royal Mail's mailing services): digits and letters A to Z,
    /// usually a postcode without its space and the two-character Delivery Point Suffix, such as <c>LU17 8XE2B</c> written
    /// <c>LU178XE2B</c>. Lowercase letters are made uppercase. A start bar, the characters, a check character and a stop bar
    /// make the symbol, four bars to a character. The modules are a grid of 8 rows (3 for the ascender, 2 for the tracker, 3
    /// for the descender); draw it at a module size of 0.6 mm for bars 4.8 mm tall at 21.2 bars per inch, Royal Mail's sizes.
    /// </summary>
    /// <param name="text">The characters.</param>
    /// <returns>The barcode, with the characters, uppercase and without the check character, as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is empty, longer than 1000 characters, or has a character other than a digit or a letter A to Z.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-royal-mail"/>
    /// </example>
    public static Barcode RoyalMail(string text)
    {
        int[] indices = RoyalMailIndices(text, "RM4SCC");
        int check = RoyalMailEncoder.Check(indices);
        FourStateBar[] bars = RoyalMailEncoder.Bars([.. indices, check], 1, 1);
        bars[0] = FourStateBar.Ascender;
        bars[^1] = FourStateBar.Full;
        return new Barcode(BarcodeFormat.RoyalMail, FourStateGrid.Create(bars, 3, 2), 4, text.ToUpperInvariant());
    }

    /// <summary>
    /// Makes a KIX code (Klantindex), PostNL's customer code for Dutch mail: RM4SCC's characters without its start and stop
    /// bars or check character, usually the postcode, the house number and, after an X, its addition, such as
    /// <c>2500GG30X250</c>. Lowercase letters are made uppercase. The modules are a grid of 8 rows, as for
    /// <see cref="RoyalMail(string)"/>; draw it at a module size of 0.6 mm.
    /// </summary>
    /// <param name="text">The characters.</param>
    /// <returns>The barcode, with the characters, uppercase, as its human-readable text.</returns>
    /// <exception cref="ArgumentException">The text is empty, longer than 1000 characters, or has a character other than a digit or a letter A to Z.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-kix"/>
    /// </example>
    public static Barcode Kix(string text)
    {
        int[] indices = RoyalMailIndices(text, "A KIX code");
        return new Barcode(BarcodeFormat.Kix, FourStateGrid.Create(RoyalMailEncoder.Bars(indices, 0, 0), 3, 2), 4, text.ToUpperInvariant());
    }

    private static int[] RoyalMailIndices(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckLength(text.Length, nameof(text));
        int[] indices = RoyalMailEncoder.Indices(text, out int bad);
        return bad < 0 ? indices : throw new ArgumentException($"{name} holds digits and the letters A to Z; '{text[bad]}' at {bad} is not one.", nameof(text));
    }
}
