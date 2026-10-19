using System.Text;

using Egami.Barcodes.Matrix;

namespace Egami.Barcodes;

public sealed partial class Barcode
{
    /// <summary>
    /// Makes an Aztec Code (ISO/IEC 24778) from text, as on rail and airline tickets. The text is written in the modes that give
    /// the fewest bits; text in ISO 8859-1 is written as it is, other text as UTF-8 with ECI 26. Aztec needs no quiet zone.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The error correction share, compact form and layers, or <see langword="null"/> for <see cref="AztecOptions.Default"/>.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentException">The text is empty or does not fit in a symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-aztec"/>
    /// </example>
    public static Barcode Aztec(string text, AztecOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("The text is empty.", nameof(text));
        }

        bool latin1 = !text.AsSpan().ContainsAnyExceptInRange('\0', '\xFF');
        return FromAztec(latin1 ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text), latin1 ? 0 : 26, options, nameof(text));
    }

    /// <summary>Makes an Aztec Code from bytes, without an ECI.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="options">The error correction share, compact form and layers, or <see langword="null"/> for <see cref="AztecOptions.Default"/>.</param>
    /// <returns>The barcode, with no human-readable text.</returns>
    /// <exception cref="ArgumentException">There are no bytes, or they do not fit in a symbol.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-aztec"/>
    /// </example>
    public static Barcode Aztec(ReadOnlySpan<byte> data, AztecOptions? options = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("There is no data.", nameof(data));
        }

        return FromAztec(data.ToArray(), 0, options, nameof(data));
    }

    /// <summary>Makes an Aztec Rune: an 11 x 11 core that holds one value, 0 to 255, for machine-readable marks.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 0 to 255.</exception>
    public static Barcode AztecRune(int value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)value, 255u, nameof(value));
        return new Barcode(BarcodeFormat.Aztec, AztecEncoder.Rune(value), 0, null);
    }

    private static Barcode FromAztec(byte[] data, int eci, AztecOptions? options, string parameter)
    {
        options ??= AztecOptions.Default;
        if (data.Length > 3832)
        {
            throw new ArgumentException("The data does not fit in an Aztec Code, which holds at most 3832 digits or 1914 bytes.", parameter);
        }

        if (options.Compact == true && options.Layers > 4)
        {
            throw new ArgumentException("A compact Aztec Code has 1 to 4 layers.", nameof(options));
        }

        AztecEncoder.Result result = AztecEncoder.Encode(data, eci, options.ErrorCorrectionPercent ?? 23, options.Compact, options.Layers)
            ?? throw new ArgumentException("The data does not fit in an Aztec Code with these options.", parameter);
        return new Barcode(BarcodeFormat.Aztec, result.Modules, 0, null);
    }
}
