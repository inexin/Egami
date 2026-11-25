namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.Code93(string, Code93Options?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-code93"/>
/// </example>
public sealed record class Code93Options
{
    /// <summary>Gets the defaults.</summary>
    public static Code93Options Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether to write Full ASCII, which spells the ASCII characters beyond the 43 data characters of
    /// Code 93 as a shift character and a letter, or <see langword="null"/>, the default, for Full ASCII only when the text
    /// needs it. <see langword="false"/> rejects text that needs it. Code 93's shift characters are characters of their own, so
    /// readers decode Full ASCII without being told.
    /// </summary>
    public bool? FullAscii { get; init; }

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
