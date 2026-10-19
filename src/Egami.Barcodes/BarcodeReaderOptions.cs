namespace Egami.Barcodes;

/// <summary>How <see cref="BarcodeReader.ReadBarcodes{TPixel}(Raster{TPixel}, BarcodeReaderOptions?)"/> searches an image.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read-options"/>
/// </example>
public sealed record class BarcodeReaderOptions
{
    /// <summary>Gets the defaults: every symbology, effort 5.</summary>
    public static BarcodeReaderOptions Default { get; } = new();

    /// <summary>Gets the symbologies to look for; <see cref="BarcodeFormat.All"/> by default. Fewer formats read faster.</summary>
    public BarcodeFormat Formats { get; init; } = BarcodeFormat.All;

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
    /// Gets the context whose parallelism large images are searched with (binarized in stripes, the detectors side by side),
    /// or <see langword="null"/> for <see cref="EgamiContext.Default"/>. Results never depend on the number of threads.
    /// </summary>
    public EgamiContext? Context { get; init; }
}
