using Egami.Metadata;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// The JPEG 2000 format (ITU-T T.800, Part 1), read only, registered in <see cref="EgamiContext.Default"/>: JP2 and JPX files
/// and raw codestreams (J2K, J2C), including the JPX streams a PDF holds, which load with plain <see cref="Raster.Load(ReadOnlyMemory{byte}, EgamiContext?, ReaderOptions?)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-load"/>
/// </example>
public static class Jpeg2000Format
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature and reader. It has no writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "JPEG 2000",
        MimeTypes = ["image/jp2", "image/jpx", "image/j2c"],
        Extensions = ["jp2", "jpx", "jpf", "j2k", "j2c", "jpc"],
        SignatureLength = 12,
        Matches = header => header.StartsWith(Jp2File.Signature) || J2kCodestream.Matches(header),
        Reader = Jpeg2000Reader.Instance,
    };
}

/// <summary>Options for reading JPEG 2000.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-options"/>
/// </example>
public sealed record Jpeg2000ReaderOptions : ReaderOptions
{
    /// <summary>
    /// Gets what the pixels are: <see cref="Jpeg2000Components.Color"/> (the default) applies palettes, channel definitions and
    /// color spaces; <see cref="Jpeg2000Components.Raw"/> returns the codestream's components as stored, for a PDF that names its
    /// own color space.
    /// </summary>
    public Jpeg2000Components Components { get; init; }

    /// <summary>
    /// Gets how many quality layers to decode, for a quick preview of a layered file, or <see langword="null"/> (the default) for
    /// all of them.
    /// </summary>
    public int? MaxQualityLayers { get; init; }

    /// <summary>
    /// Gets whether subsampled chroma is repeated over the pixels it covers, as OpenJPEG does, instead of interpolated at its
    /// siting (the default).
    /// </summary>
    public bool FastUpsampling { get; init; }
}

/// <summary>What a JPEG 2000 image's pixels are made from.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-options"/>
/// </example>
public enum Jpeg2000Components
{
    /// <summary>
    /// Color: palettes applied, channels ordered and alpha found by the channel definitions, sYCC and CIELab converted to RGB;
    /// gray, RGB or CMYK, with alpha when there is one.
    /// </summary>
    Color,

    /// <summary>
    /// The codestream's components as stored, after its own component transform: 1 as gray, 2 as gray with alpha, 3 as RGB and 4
    /// as CMYK. Palettes and color spaces are not applied.
    /// </summary>
    Raw,
}

/// <summary>The container a JPEG 2000 image came in.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-details"/>
/// </example>
public enum Jpeg2000Container
{
    /// <summary>A raw codestream (J2K, J2C), with no color information.</summary>
    Codestream,

    /// <summary>A JP2 file (T.800 Annex I).</summary>
    Jp2,

    /// <summary>A JPX file (T.801), of which the JP2 parts and the first codestream are read.</summary>
    Jpx,
}

/// <summary>The color space a JPEG 2000 file declares.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-details"/>
/// </example>
public enum Jpeg2000ColorSpace
{
    /// <summary>None: a raw codestream, or a JP2 file whose colour specification Egami does not read.</summary>
    Unspecified,

    /// <summary>sRGB (enumerated 16).</summary>
    Srgb,

    /// <summary>Grayscale (enumerated 17).</summary>
    Gray,

    /// <summary>sYCC (enumerated 18), converted to RGB.</summary>
    Sycc,

    /// <summary>CMYK (JPX enumerated 12).</summary>
    Cmyk,

    /// <summary>CIELab (JPX enumerated 14), converted to sRGB.</summary>
    CieLab,

    /// <summary>An ICC profile, kept in <see cref="RasterMetadata.IccProfile"/>.</summary>
    Icc,

    /// <summary>Another JPX enumerated space, read as its components say; <see cref="Jpeg2000Details.EnumeratedColorSpace"/> names it.</summary>
    Other,
}

/// <summary>The order a JPEG 2000 codestream's packets come in (T.800 Table A.16).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-details"/>
/// </example>
public enum Jpeg2000ProgressionOrder
{
    /// <summary>Layer, resolution, component, position.</summary>
    Lrcp,

    /// <summary>Resolution, layer, component, position.</summary>
    Rlcp,

    /// <summary>Resolution, position, component, layer.</summary>
    Rpcl,

    /// <summary>Position, component, resolution, layer.</summary>
    Pcrl,

    /// <summary>Component, position, resolution, layer.</summary>
    Cprl,
}

/// <summary>What a JPEG 2000 channel definition (cdef) says a channel is.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-details"/>
/// </example>
public enum Jpeg2000ChannelType
{
    /// <summary>A color channel.</summary>
    Color,

    /// <summary>Opacity (alpha).</summary>
    Opacity,

    /// <summary>Premultiplied opacity; Egami loads the image with straight alpha.</summary>
    PremultipliedOpacity,

    /// <summary>Unspecified.</summary>
    Unspecified,
}

/// <summary>One component of a JPEG 2000 codestream, as its SIZ marker gives it.</summary>
/// <param name="Precision">The bits per sample, 1 to 38.</param>
/// <param name="IsSigned">Whether samples are signed.</param>
/// <param name="SubsamplingX">The horizontal sampling step on the reference grid (XRsiz).</param>
/// <param name="SubsamplingY">The vertical sampling step on the reference grid (YRsiz).</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-details"/>
/// </example>
public readonly record struct Jpeg2000Component(int Precision, bool IsSigned, int SubsamplingX, int SubsamplingY);

/// <summary>One channel definition of a JP2 file (cdef).</summary>
/// <param name="Channel">The channel's index.</param>
/// <param name="Type">What the channel is.</param>
/// <param name="Association">The color the channel belongs to, counting from 1; 0 for the whole image; -1 for none.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-details"/>
/// </example>
public readonly record struct Jpeg2000Channel(int Channel, Jpeg2000ChannelType Type, int Association);

/// <summary>Details only JPEG 2000 has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jpeg2000.cs" region="jpeg2000-details"/>
/// </example>
public sealed class Jpeg2000Details : FormatDetails
{
    /// <summary>Gets or sets the container the codestream came in.</summary>
    public Jpeg2000Container Container { get; set; }

    /// <summary>Gets or sets the codestream's capabilities (SIZ Rsiz): 0 for no restrictions, 1 and 2 for profiles 0 and 1.</summary>
    public int Profile { get; set; }

    /// <summary>Gets the codestream's components as stored.</summary>
    public IList<Jpeg2000Component> Components { get; } = [];

    /// <summary>Gets or sets whether the codestream uses the reversible 5/3 wavelet (lossless); otherwise the 9/7 one.</summary>
    public bool Reversible { get; set; }

    /// <summary>Gets or sets the number of wavelet decomposition levels.</summary>
    public int DecompositionLevels { get; set; }

    /// <summary>Gets or sets the number of quality layers.</summary>
    public int QualityLayers { get; set; }

    /// <summary>Gets or sets the order the codestream's packets come in.</summary>
    public Jpeg2000ProgressionOrder ProgressionOrder { get; set; }

    /// <summary>Gets or sets the tile width on the reference grid.</summary>
    public int TileWidth { get; set; }

    /// <summary>Gets or sets the tile height on the reference grid.</summary>
    public int TileHeight { get; set; }

    /// <summary>Gets or sets the number of tiles.</summary>
    public int TileCount { get; set; }

    /// <summary>Gets or sets the color space the file declares.</summary>
    public Jpeg2000ColorSpace ColorSpace { get; set; }

    /// <summary>Gets or sets the enumerated color space number (T.800 Table I.10, T.801 Table M.25), or -1 when there is none.</summary>
    public int EnumeratedColorSpace { get; set; } = -1;

    /// <summary>Gets or sets whether the file has a palette (pclr).</summary>
    public bool HasPalette { get; set; }

    /// <summary>Gets the channel definitions (cdef), empty when the file has none.</summary>
    public IList<Jpeg2000Channel> Channels { get; } = [];

    /// <summary>Gets the codestream's text comments (COM markers).</summary>
    public IList<string> Comments { get; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        Jpeg2000Details copy = new()
        {
            Container = Container,
            Profile = Profile,
            Reversible = Reversible,
            DecompositionLevels = DecompositionLevels,
            QualityLayers = QualityLayers,
            ProgressionOrder = ProgressionOrder,
            TileWidth = TileWidth,
            TileHeight = TileHeight,
            TileCount = TileCount,
            ColorSpace = ColorSpace,
            EnumeratedColorSpace = EnumeratedColorSpace,
            HasPalette = HasPalette,
        };
        foreach (Jpeg2000Component component in Components)
        {
            copy.Components.Add(component);
        }

        foreach (Jpeg2000Channel channel in Channels)
        {
            copy.Channels.Add(channel);
        }

        foreach (string comment in Comments)
        {
            copy.Comments.Add(comment);
        }

        return copy;
    }
}
