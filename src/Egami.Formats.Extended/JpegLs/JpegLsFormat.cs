using Egami.Metadata;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>
/// JPEG-LS (ITU-T T.87, ISO/IEC 14495-1; .jls): lossless and near-lossless coding of 2 to 16-bit samples by context modelling
/// and Golomb codes, as DICOM uses it. Reads every interleave mode, restart markers, preset parameters, mapping tables
/// (palettes), subsampled components and HP's colour transforms; writes lossless and near-lossless files of 1 to 4 components.
/// Registered with <see cref="ExtendedContextExtensions.WithJpegLs"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls"/>
/// </example>
public static class JpegLsFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "JPEG-LS",
        MimeTypes = ["image/jls", "image/x-jls"],
        Extensions = ["jls"],
        SignatureLength = 512,
        Matches = JpegLsSegments.Matches,
        Reader = new JpegLsFormatReader(),
        Writer = new JpegLsFormatWriter(),
    };
}

/// <summary>How the components of a JPEG-LS scan are interleaved (T.87 annex A.8).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls-options"/>
/// </example>
public enum JpegLsInterleave
{
    /// <summary>One scan per component, each holding a whole plane.</summary>
    None,

    /// <summary>One scan; each line of the image holds a line of every component in turn.</summary>
    Line,

    /// <summary>One scan; each pixel holds every component in turn.</summary>
    Sample,
}

/// <summary>
/// A reversible colour transform applied to RGB before coding, as HP's JPEG-LS software and CharLS write it in an APP8 "mrfx"
/// segment. It usually makes colour photographs smaller.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls-options"/>
/// </example>
public enum JpegLsColorTransform
{
    /// <summary>No transform: the components are coded as they are.</summary>
    None,

    /// <summary>HP1: red and blue are coded as their difference from green.</summary>
    Hp1,

    /// <summary>HP2: red as its difference from green, blue as its difference from the mean of red and green.</summary>
    Hp2,

    /// <summary>HP3: blue and red as differences from green, green with a quarter of their sum added.</summary>
    Hp3,
}

/// <summary>Details only JPEG-LS has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls"/>
/// </example>
public sealed class JpegLsDetails : FormatDetails
{
    /// <summary>
    /// Gets or sets the largest error of a sample: 0 when the file is lossless. The writer writes it back unless
    /// <see cref="JpegLsWriterOptions.Near"/> says otherwise.
    /// </summary>
    public int Near { get; set; }

    /// <summary>Gets or sets how the components were interleaved.</summary>
    public JpegLsInterleave Interleave { get; set; }

    /// <summary>Gets or sets the colour transform the file was coded with.</summary>
    public JpegLsColorTransform ColorTransform { get; set; }

    /// <summary>Gets or sets the bits of a stored sample, 2 to 16. Fewer than 8 or 16 are scaled to the pixel's full range on load.</summary>
    public int BitsPerSample { get; set; } = 8;

    /// <summary>Gets or sets the number of components the file stores.</summary>
    public int Components { get; set; } = 1;

    /// <summary>Gets or sets whether the samples were indices into a mapping table (a palette) that gave the pixels.</summary>
    public bool Palettized { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new JpegLsDetails
        {
            Near = Near,
            Interleave = Interleave,
            ColorTransform = ColorTransform,
            BitsPerSample = BitsPerSample,
            Components = Components,
            Palettized = Palettized,
        };
    }
}

/// <summary>Options for writing JPEG-LS.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls-options"/>
/// </example>
public sealed class JpegLsWriterOptions : WriterOptions
{
    /// <summary>Gets the default options: lossless, line-interleaved, no colour transform.</summary>
    public static new JpegLsWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest error allowed per sample, 0 to 255 (and at most half the largest sample value): 0, the default, is
    /// lossless; 1 to 3 make files much smaller with errors no eye sees. <see langword="null"/> keeps the value of
    /// <see cref="JpegLsDetails"/> when the raster came from a JPEG-LS file, else 0.
    /// </summary>
    public int? Near { get; init; }

    /// <summary>
    /// Gets how the components are interleaved. <see cref="JpegLsInterleave.Line"/>, the default, codes as small as the others
    /// and writes as the rows come; <see cref="JpegLsInterleave.None"/> keeps the whole image in memory until the end.
    /// </summary>
    public JpegLsInterleave Interleave { get; init; } = JpegLsInterleave.Line;

    /// <summary>
    /// Gets the colour transform for RGB rasters. Defaults to <see cref="JpegLsColorTransform.None"/>, which every JPEG-LS
    /// decoder reads; the HP transforms are read by HP's software, CharLS and Egami. Ignored for gray rasters, for
    /// <see cref="JpegLsInterleave.None"/> and for near-lossless coding.
    /// </summary>
    public JpegLsColorTransform ColorTransform { get; init; }

    /// <summary>
    /// Gets the bits of a written sample, 2 to 16, or <see langword="null"/> for the raster's own: 8, or 16 for 16-bit pixels,
    /// or the <see cref="JpegLsDetails.BitsPerSample"/> of a raster read from JPEG-LS at that depth. Fewer bits round the samples.
    /// </summary>
    public int? BitsPerSample { get; init; }

    /// <summary>
    /// Gets the restart interval in lines, or 0 (the default) for none. Restart markers let a damaged file be read again from
    /// the next marker, at a cost of a few bytes each.
    /// </summary>
    public int RestartInterval { get; init; }
}
