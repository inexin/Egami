namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.Itf(string, ItfOptions?)"/> and <see cref="Barcode.Itf14(string, ItfOptions?)"/> make a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-itf"/>
/// </example>
public sealed record class ItfOptions
{
    /// <summary>Gets the defaults.</summary>
    public static ItfOptions Default { get; } = new();

    /// <summary>Gets the width of a wide bar or space in narrow ones, 2 or 3, or <see langword="null"/>, the default, for 3.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 2 or 3.</exception>
    public int? WideRatio
    {
        get;
        init => field = value is null or 2 or 3 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The wide ratio must be 2 or 3.");
    }

    /// <summary>
    /// Gets a value indicating whether bearer bars frame the symbol and its quiet zone, 5 modules thick, so a reader cannot
    /// mistake part of the symbol for a shorter one; or <see langword="null"/>, the default, for bearer bars on ITF-14 only.
    /// </summary>
    public bool? BearerBars { get; init; }

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
