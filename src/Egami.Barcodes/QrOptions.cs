namespace Egami.Barcodes;

/// <summary>The error correction level of a QR Code: how much of the symbol can be damaged and still read.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-qr"/>
/// </example>
public enum QrErrorCorrection
{
    /// <summary>About 7 % of the codewords can be restored.</summary>
    L,

    /// <summary>About 15 % of the codewords can be restored.</summary>
    M,

    /// <summary>About 25 % of the codewords can be restored.</summary>
    Q,

    /// <summary>About 30 % of the codewords can be restored.</summary>
    H,
}

/// <summary>How <see cref="Barcode.Qr(string, QrOptions?)"/> makes a symbol; every unset property is chosen automatically.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-qr"/>
/// </example>
public sealed record class QrOptions
{
    /// <summary>Gets the defaults.</summary>
    public static QrOptions Default { get; } = new();

    /// <summary>
    /// Gets the error correction level, or <see langword="null"/>, the default, for level M raised to Q or H while the symbol
    /// stays the same size.
    /// </summary>
    public QrErrorCorrection? ErrorCorrection { get; init; }

    /// <summary>Gets the version, 1 (21 x 21 modules) to 40 (177 x 177), or <see langword="null"/>, the default, for the smallest that fits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1 to 40.</exception>
    public int? Version
    {
        get;
        init => field = value is null or (>= 1 and <= 40) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The version must be 1 to 40.");
    }

    /// <summary>Gets the mask pattern, 0 to 7, or <see langword="null"/>, the default, for the one with the lowest penalty score.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 0 to 7.</exception>
    public int? Mask
    {
        get;
        init => field = value is null or (>= 0 and <= 7) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The mask must be 0 to 7.");
    }
}
