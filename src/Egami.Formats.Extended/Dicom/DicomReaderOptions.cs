using System.Diagnostics.CodeAnalysis;

using Egami.Metadata;

namespace Egami.Formats.Extended.Dicom;

/// <summary>What a DICOM load gives, for <see cref="DicomReaderOptions.Values"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-values-option"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public enum DicomValues
{
    /// <summary>
    /// What a viewer shows (the default): monochrome images through the modality, VOI and presentation transforms to
    /// <see cref="Pixels.Gray8"/> or <see cref="Pixels.Gray16"/>, colour and palette images as <see cref="Pixels.Rgb8"/> or
    /// <see cref="Pixels.Rgb16"/>.
    /// </summary>
    Display,

    /// <summary>
    /// The modality values (PS3.3 C.11.1), such as Hounsfield units: the stored values through the Modality LUT or Rescale Slope
    /// and Intercept, as <see cref="Pixels.GrayF32"/>. Colour images load as for <see cref="Display"/>.
    /// </summary>
    Modality,

    /// <summary>
    /// The stored values, signed ones with their sign, as <see cref="Pixels.GrayF32"/> (exact to 2^24); palette images give their
    /// indices. Colour images load as <see cref="Pixels.Rgb8"/> or <see cref="Pixels.Rgb16"/> with their samples as stored, YBR
    /// not converted to RGB.
    /// </summary>
    Stored,
}

/// <summary>
/// Options for reading DICOM: which frame, what values, and which VOI transform shows a monochrome image. Pass them to
/// <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/> or <see cref="DicomFile.LoadFrame"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-window"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed record DicomReaderOptions : ReaderOptions
{
    private readonly int _frame;
    private readonly int _voiIndex;
    private readonly double? _windowWidth;

    /// <summary>Gets the frame to load, from 0 (the default). <see cref="Raster.LoadAll(Stream, EgamiContext?, ReaderOptions?)"/> loads every frame.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Frame
    {
        get => _frame;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _frame = value;
        }
    }

    /// <summary>Gets what the load gives. Defaults to <see cref="DicomValues.Display"/>.</summary>
    public DicomValues Values { get; init; }

    /// <summary>
    /// Gets the window center to show a monochrome image with, in modality values, instead of the file's; used with
    /// <see cref="WindowWidth"/>, and <see langword="null"/> (the default) for the file's own VOI.
    /// </summary>
    public double? WindowCenter { get; init; }

    /// <summary>Gets the window width to go with <see cref="WindowCenter"/>, greater than 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not greater than 0.</exception>
    public double? WindowWidth
    {
        get => _windowWidth;
        init
        {
            if (value is double v && !(v > 0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), v, "The window width must be greater than 0.");
            }

            _windowWidth = value;
        }
    }

    /// <summary>
    /// Gets the window function to use with <see cref="WindowCenter"/> and <see cref="WindowWidth"/>, or <see langword="null"/>
    /// (the default) for the file's VOI LUT Function, LINEAR when it has none.
    /// </summary>
    public DicomWindowFunction? WindowFunction { get; init; }

    /// <summary>
    /// Gets which of the file's windows or VOI LUTs to use when it has several, from 0 (the default, the first). An index past
    /// the last uses the last.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int WindowIndex
    {
        get => _voiIndex;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _voiIndex = value;
        }
    }

    /// <summary>
    /// Gets whether the file's window (Window Center and Width) is used when it also has a VOI LUT Sequence. Defaults to
    /// <see langword="false"/>: the VOI LUT is used, as PS3.3 C.11.2 lets a viewer choose.
    /// </summary>
    public bool PreferWindow { get; init; }

    /// <summary>Gets the file already read, so loading each frame of it does not read it again.</summary>
    internal DicomFile? File { get; init; }
}

/// <summary>A VOI LUT Function (0028,1056): how a window maps modality values to shown values (PS3.3 C.11.2.1.2).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-window"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public enum DicomWindowFunction
{
    /// <summary>LINEAR: a straight ramp from center − 0.5 − (width − 1) / 2 to center − 0.5 + (width − 1) / 2.</summary>
    Linear,

    /// <summary>LINEAR_EXACT: a straight ramp from center − width / 2 to center + width / 2.</summary>
    LinearExact,

    /// <summary>SIGMOID: a logistic curve centered on center, with slope set by width.</summary>
    Sigmoid,
}

/// <summary>
/// What a DICOM load read and did, in <see cref="Metadata.RasterMetadata.FormatDetails"/>: the frame, how its pixels were stored,
/// and the transforms that made the loaded values.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-pixels"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed class DicomDetails : FormatDetails
{
    /// <summary>Gets or sets the frame loaded, from 0.</summary>
    public int Frame { get; set; }

    /// <summary>Gets or sets the number of frames in the file.</summary>
    public int FrameCount { get; set; } = 1;

    /// <summary>Gets or sets the transfer syntax the pixel data was stored in.</summary>
    public DicomTransferSyntax TransferSyntax { get; set; } = DicomTransferSyntax.ExplicitVRLittleEndian;

    /// <summary>Gets or sets the Photometric Interpretation (0028,0004), such as <c>MONOCHROME2</c> or <c>YBR_FULL_422</c>.</summary>
    public string PhotometricInterpretation { get; set; } = "";

    /// <summary>Gets or sets the bits each stored sample has (Bits Stored).</summary>
    public int BitsStored { get; set; }

    /// <summary>Gets or sets whether stored samples are signed (Pixel Representation 1).</summary>
    public bool IsSigned { get; set; }

    /// <summary>Gets or sets what the load gave.</summary>
    public DicomValues Values { get; set; }

    /// <summary>Gets or sets the Rescale Slope applied, 1 when there is none or a Modality LUT was used.</summary>
    public double RescaleSlope { get; set; } = 1;

    /// <summary>Gets or sets the Rescale Intercept applied, 0 when there is none or a Modality LUT was used.</summary>
    public double RescaleIntercept { get; set; }

    /// <summary>Gets or sets whether a Modality LUT Sequence gave the modality values.</summary>
    public bool AppliedModalityLut { get; set; }

    /// <summary>Gets or sets the window center applied, or <see langword="null"/> when no window was.</summary>
    public double? WindowCenter { get; set; }

    /// <summary>Gets or sets the window width applied, or <see langword="null"/> when no window was.</summary>
    public double? WindowWidth { get; set; }

    /// <summary>Gets or sets the window function applied.</summary>
    public DicomWindowFunction WindowFunction { get; set; }

    /// <summary>Gets or sets whether a VOI LUT Sequence gave the shown values.</summary>
    public bool AppliedVoiLut { get; set; }

    /// <summary>
    /// Gets or sets whether the shown values were stretched from the frame's smallest to its largest modality value, because
    /// the file has no window and no VOI LUT.
    /// </summary>
    public bool AppliedMinMax { get; set; }

    /// <summary>Gets or sets whether the shown values were inverted: MONOCHROME1, or Presentation LUT Shape INVERSE.</summary>
    public bool Inverted { get; set; }

    /// <summary>
    /// Gets or sets the time from this frame to the next of a cine series: Frame Time (0018,1063), or this frame's entry of the
    /// Frame Time Vector (0018,1065); <see langword="null"/> when the file gives none.
    /// </summary>
    public TimeSpan? FrameTime { get; set; }

    /// <summary>Gets or sets the Recommended Display Frame Rate (0008,2144) or Cine Rate (0018,0040), in frames a second, or <see langword="null"/>.</summary>
    public double? FrameRate { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return (DicomDetails)MemberwiseClone();
    }
}
