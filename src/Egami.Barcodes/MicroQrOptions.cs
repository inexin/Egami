namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.MicroQr(string, MicroQrOptions?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-micro-qr"/>
/// </example>
public sealed record class MicroQrOptions
{
    /// <summary>Gets the defaults: level L, raised while the symbol stays the same version, and the smallest version that fits.</summary>
    public static MicroQrOptions Default { get; } = new();

    /// <summary>
    /// Gets the error correction level: <see cref="QrErrorCorrection.L"/> (the default), <see cref="QrErrorCorrection.M"/>
    /// (M2 and up) or <see cref="QrErrorCorrection.Q"/> (M4 only). M1 has error detection only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is <see cref="QrErrorCorrection.H"/>, which Micro QR does not have.</exception>
    public QrErrorCorrection ErrorCorrection
    {
        get;
        init => field = value is QrErrorCorrection.L or QrErrorCorrection.M or QrErrorCorrection.Q
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Micro QR has levels L, M and Q.");
    }

    /// <summary>
    /// Gets the smallest version, 1 (M1, 11 x 11 modules) to 4 (M4, 17 x 17), or <see langword="null"/>, the default, for any;
    /// the symbol is the smallest version from it that holds the data.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1 to 4.</exception>
    public int? MinVersion
    {
        get;
        init => field = value is null or (>= 1 and <= 4) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The version must be 1 to 4.");
    }

    /// <summary>
    /// Gets whether to raise the level, to M and then Q, while the data still fits the same version. The default is
    /// <see langword="true"/>.
    /// </summary>
    public bool RaiseErrorCorrection { get; init; } = true;
}
