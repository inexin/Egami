namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.Code39(string, Code39Options?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-code39"/>
/// </example>
public sealed record class Code39Options
{
    /// <summary>Gets the defaults.</summary>
    public static Code39Options Default { get; } = new();

    /// <summary>Gets a value indicating whether a mod 43 check character goes before the stop character; off by default.</summary>
    public bool CheckDigit { get; init; }

    /// <summary>
    /// Gets a value indicating whether to write Full ASCII, which spells the ASCII characters beyond the 43 of Code 39 as pairs,
    /// or <see langword="null"/>, the default, for Full ASCII only when the text needs it. <see langword="false"/> rejects text
    /// that needs it. A reader must be set to Full ASCII to read such text back.
    /// </summary>
    public bool? FullAscii { get; init; }

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
