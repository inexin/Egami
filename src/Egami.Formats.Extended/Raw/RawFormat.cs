using Egami.Metadata;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Camera RAW files: the sensor data a camera recorded, developed into an image on load. Reads DNG (Adobe's open RAW format,
/// which phones, Leica, Pentax, Ricoh, Hasselblad and Adobe DNG Converter write). Load develops the image with the file's own
/// white balance and colour data; <see cref="RawReaderOptions"/> picks an embedded preview, a half-size develop or the
/// untouched sensor mosaic instead, and sets the develop. Registered with <see cref="ExtendedContextExtensions.WithRaw"/>,
/// which places it ahead of TIFF, since DNG files are TIFF files.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-load"/>
/// </example>
public static class RawFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "RAW",
        MimeTypes = ["image/x-adobe-dng", "image/dng", "image/x-canon-cr2", "image/x-nikon-nef", "image/x-nikon-nrw", "image/x-pentax-pef", "image/x-sony-arw", "image/x-sony-sr2", "image/x-olympus-orf", "image/x-panasonic-rw2", "image/x-fuji-raf", "image/x-canon-cr3"],
        Extensions = ["dng", "cr2", "nef", "nrw", "pef", "arw", "sr2", "orf", "rw2", "raf", "cr3"],
        SignatureLength = RawDetect.SignatureLength,
        Matches = RawDetect.Matches,
        Reader = new RawReader(),
        ConvertsColor = true,
    };
}

/// <summary>What a RAW load returns.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-outputs"/>
/// </example>
public enum RawOutput
{
    /// <summary>
    /// The developed image, in <see cref="Pixels.Rgb16"/> sRGB, or rendered straight into the RGB space
    /// <see cref="ReaderOptions.ConvertColorTo"/> names (Display P3, Adobe RGB, ProPhoto, Rec. 2020, linear ones), whose ICC
    /// profile the raster then carries.
    /// </summary>
    Developed,

    /// <summary>The largest embedded preview, as the camera or converter rendered it; fast, but its look is the maker's.</summary>
    Preview,

    /// <summary>
    /// The sensor data as stored: <see cref="Pixels.Gray16"/> for a colour filter array (the pattern is in
    /// <see cref="RawDetails.CfaPattern"/>), <see cref="Pixels.Rgb16"/> for LinearRaw, float types for floating-point data.
    /// Nothing is scaled, cropped or balanced, and <see cref="ReaderOptions.ConvertColorTo"/> does not apply.
    /// </summary>
    Mosaic,

    /// <summary>A develop at half the width and height, each 2 × 2 block of the mosaic one pixel: about four times faster, and no demosaicing.</summary>
    HalfSize,
}

/// <summary>How a develop fills in the colours a colour filter array did not sample.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-develop"/>
/// </example>
public enum RawDemosaicMethod
{
    /// <summary>The average of the nearest samples of each colour. Fastest, softest.</summary>
    Bilinear,

    /// <summary>Malvar, He and Cutler's gradient-corrected interpolation: sharper than bilinear at almost the same speed.</summary>
    Malvar,

    /// <summary>Hirakawa and Parks' adaptive homogeneity-directed interpolation: the fewest colour artifacts on fine detail. The default.</summary>
    Ahd,
}

/// <summary>Where a develop's white balance comes from.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-develop"/>
/// </example>
public enum RawWhiteBalance
{
    /// <summary>The white balance the camera recorded. The default.</summary>
    AsShot,

    /// <summary>Measured from the image, taking its average colour as gray.</summary>
    Auto,

    /// <summary><see cref="RawReaderOptions.Temperature"/> and <see cref="RawReaderOptions.Tint"/>.</summary>
    Custom,
}

/// <summary>What a develop does where the sensor clipped some colours but not others.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-develop"/>
/// </example>
public enum RawHighlightMode
{
    /// <summary>Cuts every colour at the clipping level, so clipped areas turn white. The default.</summary>
    Clip,

    /// <summary>Clips, then lifts what clipping removed evenly across the colours, for smoother, neutral highlights.</summary>
    Blend,

    /// <summary>
    /// Rebuilds clipped colours from the ones that were not, with the colour of the surroundings. Detail returns in highlights
    /// when <see cref="RawReaderOptions.Exposure"/> is lowered.
    /// </summary>
    Rebuild,
}

/// <summary>The kind of an embedded preview.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-details"/>
/// </example>
public enum RawPreviewKind
{
    /// <summary>A baseline JPEG.</summary>
    Jpeg,

    /// <summary>Uncompressed 8-bit RGB, usually a small thumbnail.</summary>
    Uncompressed,
}

/// <summary>An embedded preview a RAW file carries.</summary>
/// <param name="Width">The preview's width in pixels.</param>
/// <param name="Height">The preview's height in pixels.</param>
/// <param name="Kind">How the preview is stored.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-details"/>
/// </example>
public sealed record RawPreview(int Width, int Height, RawPreviewKind Kind);

/// <summary>Details only RAW files have, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-details"/>
/// </example>
public sealed class RawDetails : FormatDetails
{
    /// <summary>Gets or sets what this raster is: the developed image, a preview, a half-size develop or the mosaic.</summary>
    public RawOutput Output { get; set; }

    /// <summary>Gets or sets the index in <see cref="Previews"/> of the preview this raster is, or <see langword="null"/>.</summary>
    public int? PreviewIndex { get; set; }

    /// <summary>Gets or sets the camera maker, as the file says.</summary>
    public string Make { get; set; } = "";

    /// <summary>Gets or sets the camera model, as the file says.</summary>
    public string Model { get; set; } = "";

    /// <summary>Gets or sets DNG's UniqueCameraModel, the name colour profiles are matched by, or <see langword="null"/>.</summary>
    public string? UniqueCameraModel { get; set; }

    /// <summary>Gets or sets the DNG version the file was written for, or <see langword="null"/> for other formats.</summary>
    public Version? DngVersion { get; set; }

    /// <summary>Gets or sets the width of the sensor data as stored.</summary>
    public int SensorWidth { get; set; }

    /// <summary>Gets or sets the height of the sensor data as stored.</summary>
    public int SensorHeight { get; set; }

    /// <summary>Gets or sets the part of the sensor data that holds image (DNG's ActiveArea), in sensor coordinates.</summary>
    public Rect ActiveArea { get; set; }

    /// <summary>Gets or sets the part a develop outputs (DNG's default crop), in sensor coordinates.</summary>
    public Rect CropArea { get; set; }

    /// <summary>
    /// Gets or sets the colour filter array pattern at the sensor data's top-left, row by row, such as <c>RGGB</c>, or
    /// <see langword="null"/> for data that is already RGB.
    /// </summary>
    public string? CfaPattern { get; set; }

    /// <summary>Gets or sets the width of the colour filter array's repeating pattern (2 for Bayer).</summary>
    public int CfaWidth { get; set; }

    /// <summary>Gets or sets the height of the colour filter array's repeating pattern.</summary>
    public int CfaHeight { get; set; }

    /// <summary>Gets or sets the bits each sample was stored with.</summary>
    public int BitsPerSample { get; set; }

    /// <summary>Gets or sets the TIFF compression code of the sensor data (1 none, 7 lossless JPEG, 8 Deflate, 34892 lossy JPEG, 52546 JPEG XL).</summary>
    public int Compression { get; set; }

    /// <summary>Gets or sets the black levels, in stored values: one per position of the black level pattern and sample.</summary>
    public IReadOnlyList<double> BlackLevels { get; set; } = [];

    /// <summary>Gets or sets the white level of each sample, in stored values: where the sensor clips.</summary>
    public IReadOnlyList<double> WhiteLevels { get; set; } = [];

    /// <summary>Gets or sets the camera's response to the white the camera balanced for, or empty when the file has none.</summary>
    public IReadOnlyList<double> AsShotNeutral { get; set; } = [];

    /// <summary>Gets or sets the white-balance multipliers of red, green and blue the develop used, the smallest 1.</summary>
    public IReadOnlyList<double> WhiteBalance { get; set; } = [];

    /// <summary>Gets or sets the colour temperature in kelvin of the white the develop balanced for.</summary>
    public double Temperature { get; set; }

    /// <summary>Gets or sets the tint of the white the develop balanced for: positive toward magenta, negative toward green.</summary>
    public double Tint { get; set; }

    /// <summary>Gets or sets the 3 × 3 matrix, row by row, from white-balanced camera RGB to XYZ D50 the develop used.</summary>
    public IReadOnlyList<double> CameraToXyz { get; set; } = [];

    /// <summary>Gets or sets the camera profile's name, or <see langword="null"/>.</summary>
    public string? ProfileName { get; set; }

    /// <summary>Gets or sets the exposure in stops the file asks a develop to add (DNG's BaselineExposure).</summary>
    public double BaselineExposure { get; set; }

    /// <summary>Gets or sets the DNG opcodes the file carries, by list (1 to 3) and opcode id.</summary>
    public IReadOnlyList<(int List, int Opcode)> Opcodes { get; set; } = [];

    /// <summary>Gets or sets the embedded previews, largest first.</summary>
    public IReadOnlyList<RawPreview> Previews { get; set; } = [];

    /// <summary>Gets or sets the ISO speed, or <see langword="null"/>.</summary>
    public double? Iso { get; set; }

    /// <summary>Gets or sets the exposure time in seconds, or <see langword="null"/>.</summary>
    public double? ExposureTime { get; set; }

    /// <summary>Gets or sets the f-number, or <see langword="null"/>.</summary>
    public double? FNumber { get; set; }

    /// <summary>Gets or sets the focal length in millimetres, or <see langword="null"/>.</summary>
    public double? FocalLength { get; set; }

    /// <summary>Gets or sets the lens model, or <see langword="null"/>.</summary>
    public string? LensModel { get; set; }

    /// <summary>Gets or sets which lens profile corrected the image and what it corrected, or <see langword="null"/> for none.</summary>
    public string? LensProfile { get; set; }

    /// <summary>Gets or sets the maker note as stored: maker-specific data Egami keeps but does not parse, or empty.</summary>
    public ReadOnlyMemory<byte> MakerNote { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return (RawDetails)MemberwiseClone();
    }
}

/// <summary>Options for reading camera RAW files: what Load returns, and how it develops.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-develop"/>
/// </example>
public sealed record RawReaderOptions : ReaderOptions
{
    private readonly double _temperature = 5500;
    private readonly double _tint;
    private readonly double _exposure;
    private readonly double _luminance;
    private readonly double _color = 25;
    private readonly int? _previewIndex;

    /// <summary>
    /// Gets what Load returns. Defaults to <see cref="RawOutput.Developed"/>. When it is that and
    /// <see cref="ReaderOptions.MinimumSize"/> is set, Load returns the smallest embedded preview, or else a half-size develop,
    /// that covers it.
    /// </summary>
    public RawOutput Output { get; init; }

    /// <summary>
    /// Gets which preview <see cref="RawOutput.Preview"/> returns, as an index into <see cref="RawDetails.Previews"/>, or
    /// <see langword="null"/> (the default) for the largest.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? PreviewIndex
    {
        get => _previewIndex;
        init
        {
            if (value is int v)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(v);
            }

            _previewIndex = value;
        }
    }

    /// <summary>Gets the demosaicing method. Defaults to <see cref="RawDemosaicMethod.Ahd"/>.</summary>
    public RawDemosaicMethod Demosaic { get; init; } = RawDemosaicMethod.Ahd;

    /// <summary>Gets where the white balance comes from. Defaults to <see cref="RawWhiteBalance.AsShot"/>.</summary>
    public RawWhiteBalance WhiteBalance { get; init; }

    /// <summary>Gets the colour temperature in kelvin, 2,000 to 50,000, that <see cref="RawWhiteBalance.Custom"/> balances for. Defaults to 5,500.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 2,000 to 50,000.</exception>
    public double Temperature
    {
        get => _temperature;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2000);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 50000);
            _temperature = value;
        }
    }

    /// <summary>
    /// Gets the tint, −150 to 150, that <see cref="RawWhiteBalance.Custom"/> balances for: positive for light that is magenta,
    /// negative for light that is green. Defaults to 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside −150 to 150.</exception>
    public double Tint
    {
        get => _tint;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, -150);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 150);
            _tint = value;
        }
    }

    /// <summary>Gets the exposure to add, in stops, −10 to 10. Defaults to 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside −10 to 10.</exception>
    public double Exposure
    {
        get => _exposure;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, -10);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10);
            _exposure = value;
        }
    }

    /// <summary>Gets whether the file's own exposure correction (DNG's BaselineExposure) is applied. Defaults to <see langword="true"/>.</summary>
    public bool ApplyBaselineExposure { get; init; } = true;

    /// <summary>Gets what happens where the sensor clipped. Defaults to <see cref="RawHighlightMode.Clip"/>.</summary>
    public RawHighlightMode Highlights { get; init; }

    /// <summary>
    /// Gets the strength, 0 to 100, of luminance noise reduction, done on the sensor data before demosaicing with the file's
    /// noise model, or one measured from the image. Defaults to 0 (off).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public double LuminanceNoiseReduction
    {
        get => _luminance;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _luminance = value;
        }
    }

    /// <summary>Gets the strength, 0 to 100, of colour noise reduction after demosaicing. Defaults to 25.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public double ColorNoiseReduction
    {
        get => _color;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _color = value;
        }
    }

    /// <summary>
    /// Gets whether the lens corrections the file carries (distortion, vignetting and lateral chromatic aberration, from DNG
    /// opcodes) are applied. Defaults to <see langword="true"/>.
    /// </summary>
    public bool LensCorrections { get; init; } = true;

    /// <summary>
    /// Gets whether the camera profile's look is applied: its HueSatMap, LookTable and tone curve. Defaults to
    /// <see langword="true"/>; without it, colours are the colour matrices' alone.
    /// </summary>
    public bool ApplyProfile { get; init; } = true;

    /// <summary>
    /// Gets the folders of lens profiles to correct with: Lensfun database files (<c>.xml</c>) and Adobe lens profiles
    /// (<c>.lcp</c>), searched with their subfolders, and a system Lensfun install when there is one. A file whose own data
    /// corrects the lens (DNG's lens opcodes) uses that instead. Egami ships no profiles: Lensfun's database is under CC BY-SA
    /// and Adobe's profiles are Adobe's, so you supply them. Defaults to none. Applies only with <see cref="LensCorrections"/>.
    /// </summary>
    public IReadOnlyList<string> LensProfiles { get; init; } = [];

    /// <summary>
    /// Gets whether a file that no lens correction covers (no lens opcodes, no matching profile) has its lateral chromatic
    /// aberration measured from the image and corrected: red and blue are scaled about the centre to line their edges up with
    /// green's. Defaults to <see langword="false"/>, since the measurement costs about as much as the demosaic. Applies only
    /// with <see cref="LensCorrections"/>.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-develop"/>
    /// </example>
    public bool AutoChromaticAberration { get; init; }
}
