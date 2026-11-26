namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.Codabar(string, CodabarOptions?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-codabar"/>
/// </example>
public sealed record class CodabarOptions
{
    /// <summary>Gets the defaults.</summary>
    public static CodabarOptions Default { get; } = new();

    /// <summary>
    /// Gets the start character, A to D; A by default. Text that begins and ends with A to D keeps its own start and stop
    /// characters.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not A, B, C or D.</exception>
    public char Start
    {
        get;
        init => field = value is >= 'A' and <= 'D' ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The start character must be A, B, C or D.");
    } = 'A';

    /// <summary>
    /// Gets the stop character, A to D; A by default. Text that begins and ends with A to D keeps its own start and stop
    /// characters.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not A, B, C or D.</exception>
    public char Stop
    {
        get;
        init => field = value is >= 'A' and <= 'D' ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The stop character must be A, B, C or D.");
    } = 'A';

    /// <summary>
    /// Gets a value indicating whether a modulo 16 check character goes before the stop character; off by default. Readers
    /// cannot tell whether a Codabar symbol has one, so they return it with the text.
    /// </summary>
    public bool CheckDigit { get; init; }

    /// <summary>Gets the width of a wide bar or space in narrow ones, 2 or 3, or <see langword="null"/>, the default, for 2.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 2 or 3.</exception>
    public int? WideRatio
    {
        get;
        init => field = value is null or 2 or 3 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The wide ratio must be 2 or 3.");
    }

    /// <summary>
    /// Gets the height of the bars in modules, or <see langword="null"/>, the default, for 50. It sets the height when the
    /// barcode is drawn at a module size; drawn into a rectangle, the bars fill it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Height
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The height must be positive.");
    }
}
