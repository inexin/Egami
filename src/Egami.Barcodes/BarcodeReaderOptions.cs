namespace Egami.Barcodes;

/// <summary>A set of barcode symbologies, for <see cref="BarcodeReaderOptions.Formats"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read-options"/>
/// </example>
[Flags]
public enum BarcodeFormats
{
    /// <summary>No symbology.</summary>
    None = 0,

    /// <summary>QR Code.</summary>
    Qr = 1 << 0,

    /// <summary>Data Matrix.</summary>
    DataMatrix = 1 << 1,

    /// <summary>PDF417, compact PDF417 too.</summary>
    Pdf417 = 1 << 2,

    /// <summary>Aztec Code and Aztec Runes.</summary>
    Aztec = 1 << 3,

    /// <summary>Code 128 and GS1-128.</summary>
    Code128 = 1 << 4,

    /// <summary>Code 39, Full ASCII too.</summary>
    Code39 = 1 << 5,

    /// <summary>Interleaved 2 of 5 and ITF-14.</summary>
    Itf = 1 << 6,

    /// <summary>EAN-13.</summary>
    Ean13 = 1 << 7,

    /// <summary>EAN-8.</summary>
    Ean8 = 1 << 8,

    /// <summary>UPC-A.</summary>
    UpcA = 1 << 9,

    /// <summary>UPC-E.</summary>
    UpcE = 1 << 10,

    /// <summary>The linear symbologies.</summary>
    Linear = Code128 | Code39 | Itf | Ean13 | Ean8 | UpcA | UpcE,

    /// <summary>The 2D symbologies.</summary>
    Matrix = Qr | DataMatrix | Pdf417 | Aztec,

    /// <summary>Every symbology.</summary>
    All = Linear | Matrix,
}

/// <summary>How <see cref="BarcodeReader.ReadBarcodes{TPixel}(Raster{TPixel}, BarcodeReaderOptions?)"/> searches an image.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read-options"/>
/// </example>
public sealed record class BarcodeReaderOptions
{
    /// <summary>Gets the defaults: every symbology, effort 5.</summary>
    public static BarcodeReaderOptions Default { get; } = new();

    /// <summary>Gets the symbologies to look for; <see cref="BarcodeFormats.All"/> by default. Fewer formats read faster.</summary>
    public BarcodeFormats Formats { get; init; } = BarcodeFormats.All;

    /// <summary>
    /// Gets how hard to look, 0 to 10; 5 by default. Higher efforts try more binarizations, scan rows and rotations, and from 5
    /// also inverted (light on dark) and mirrored symbols. Effort never changes what counts as a valid read.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 0 to 10.</exception>
    public int Effort
    {
        get;
        init => field = value is >= 0 and <= 10 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The effort must be 0 to 10.");
    } = 5;

    /// <summary>
    /// Gets a value indicating whether to look for light symbols on a dark background too, or <see langword="null"/>, the
    /// default, for from effort 5.
    /// </summary>
    public bool? TryInverted { get; init; }

    /// <summary>
    /// Gets a value indicating whether the image holds one symbol, upright and unrotated, with only its quiet zone around it,
    /// as a rendered barcode does; such images are read without searching. Off by default.
    /// </summary>
    public bool PureBarcode { get; init; }

    /// <summary>Gets the part of the image to search, in pixels, or <see langword="null"/>, the default, for all of it.</summary>
    public Rect? Region { get; init; }

    /// <summary>
    /// Gets the context whose parallelism large images are searched with in stripes, or <see langword="null"/> for
    /// <see cref="EgamiContext.Default"/>. Results never depend on the number of threads.
    /// </summary>
    public EgamiContext? Context { get; init; }
}
