namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.JapanPost(string, JapanPostOptions?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-japan-post"/>
/// </example>
public sealed record class JapanPostOptions
{
    /// <summary>Gets the defaults.</summary>
    public static JapanPostOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether an address number too long for the symbol loses its end, as Japan Post's manual says;
    /// on by default. A letter whose control code is the last character the symbol holds keeps its control code alone. Off,
    /// such an address number throws <see cref="ArgumentException"/> instead.
    /// </summary>
    public bool TruncateAddress { get; init; } = true;
}
