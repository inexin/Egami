using Egami.Colors;
using Egami.Metadata;

namespace Egami.Formats.Exr;

/// <summary>The OpenEXR format, registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public static class ExrFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "OpenEXR",
        MimeTypes = ["image/x-exr"],
        Extensions = ["exr"],
        SignatureLength = 4,
        Matches = ExrReader.Matches,
        Reader = ExrReader.Instance,
        Writer = ExrWriter.Instance,
    };
}

/// <summary>How an OpenEXR part's pixel data is compressed.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr-write"/>
/// </example>
public enum ExrCompression
{
    /// <summary>Not compressed.</summary>
    None = 0,

    /// <summary>Run-length encoding of byte differences, one scanline a block. Lossless.</summary>
    Rle = 1,

    /// <summary>Deflate of byte differences, one scanline a block. Lossless.</summary>
    Zips = 2,

    /// <summary>Deflate of byte differences, 16 scanlines a block. Lossless.</summary>
    Zip = 3,

    /// <summary>A Haar wavelet and Huffman coding, 32 scanlines a block. Lossless; good on grainy photographs.</summary>
    Piz = 4,

    /// <summary>Float samples rounded to 24 bits, then deflated, 16 scanlines a block. Lossy for float, lossless for half and uint.</summary>
    Pxr24 = 5,

    /// <summary>Half samples in fixed 4 × 4 blocks of 14 bytes, 32 scanlines a block. Lossy.</summary>
    B44 = 6,

    /// <summary>B44, with flat blocks stored in 3 bytes. Lossy.</summary>
    B44A = 7,

    /// <summary>DreamWorks' DCT compression, 32 scanlines a block. Lossy.</summary>
    Dwaa = 8,

    /// <summary>DreamWorks' DCT compression, 256 scanlines a block. Lossy.</summary>
    Dwab = 9,

    /// <summary>Zstandard, new in OpenEXR 3.4. Egami reads the header but not the pixels.</summary>
    Zstd = 10,

    /// <summary>High-throughput JPEG 2000 at 256 scanlines a block, new in OpenEXR 3.4. Egami reads the header but not the pixels.</summary>
    Htj2k256 = 11,

    /// <summary>High-throughput JPEG 2000 at 32 scanlines a block, new in OpenEXR 3.4. Egami reads the header but not the pixels.</summary>
    Htj2k32 = 12,
}

/// <summary>The type of an OpenEXR channel's samples.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These are OpenEXR's names for its pixel types.")]
public enum ExrPixelType
{
    /// <summary>32-bit unsigned integers, such as object IDs.</summary>
    UInt = 0,

    /// <summary>16-bit floating point.</summary>
    Half = 1,

    /// <summary>32-bit floating point.</summary>
    Float = 2,
}

/// <summary>How an OpenEXR part stores its pixels.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public enum ExrPartKind
{
    /// <summary>Blocks of scanlines.</summary>
    Scanline,

    /// <summary>Tiles, possibly at several resolution levels.</summary>
    Tiled,

    /// <summary>Deep scanlines: a varying number of samples a pixel. Egami reads the header but not the pixels.</summary>
    DeepScanline,

    /// <summary>Deep tiles. Egami reads the header but not the pixels.</summary>
    DeepTiled,
}

/// <summary>The resolution levels of a tiled OpenEXR part.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public enum ExrLevelMode
{
    /// <summary>One level, the full resolution.</summary>
    OneLevel = 0,

    /// <summary>Levels halving both dimensions each time. Egami reads the top level.</summary>
    MipMap = 1,

    /// <summary>Levels halving each dimension on its own. Egami reads the top level.</summary>
    RipMap = 2,
}

/// <summary>Which window of an OpenEXR part a raster covers.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public enum ExrWindow
{
    /// <summary>The data window: the pixels the file stores. Its offset is in <see cref="ExrDetails.DataWindow"/>.</summary>
    Data,

    /// <summary>The display window: the data window cropped to it, and padded with transparent black where it has no data.</summary>
    Display,
}

/// <summary>One channel of an OpenEXR part.</summary>
/// <param name="Name">The full name, such as "R", "diffuse.R" or "Z".</param>
/// <param name="PixelType">The sample type.</param>
/// <param name="XSampling">Samples are stored for every x that is a multiple of this; 1 for full resolution.</param>
/// <param name="YSampling">Samples are stored for every y that is a multiple of this; 1 for full resolution.</param>
/// <param name="PerceptuallyLinear">Whether the channel holds perceptually linear values, which lossy compressions treat differently.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public sealed record ExrChannel(string Name, ExrPixelType PixelType, int XSampling = 1, int YSampling = 1, bool PerceptuallyLinear = false);

/// <summary>An attribute of an OpenEXR header, kept as stored.</summary>
/// <param name="Name">The attribute's name, such as "owner".</param>
/// <param name="Type">The attribute's type name, such as "string" or "v2f".</param>
/// <param name="Value">The value's bytes, little-endian as in the file.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public sealed record ExrHeaderEntry(string Name, string Type, ReadOnlyMemory<byte> Value);

/// <summary>One part of an OpenEXR file, from its header.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public sealed record ExrPart
{
    /// <summary>Gets the part's name, or an empty string in a single-part file without one.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets how the part stores its pixels.</summary>
    public ExrPartKind Kind { get; init; }

    /// <summary>Gets the compression.</summary>
    public ExrCompression Compression { get; init; }

    /// <summary>Gets the channels, in the file's (alphabetical) order.</summary>
    public IReadOnlyList<ExrChannel> Channels { get; init; } = [];

    /// <summary>Gets the layer names: the prefixes before the last dot of the channel names, such as "diffuse" for "diffuse.R".</summary>
    public IReadOnlyList<string> Layers { get; init; } = [];

    /// <summary>Gets the data window: the area the file stores pixels for, which may lie partly outside the display window.</summary>
    public Rect DataWindow { get; init; }

    /// <summary>Gets the display window: the area the image is meant to be seen in.</summary>
    public Rect DisplayWindow { get; init; }

    /// <summary>Gets whether scanline blocks are stored bottom first.</summary>
    public bool DecreasingY { get; init; }

    /// <summary>Gets the width of a pixel over its height.</summary>
    public double PixelAspectRatio { get; init; } = 1;

    /// <summary>Gets the tile width and height of a tiled part, or <see langword="null"/>.</summary>
    public Size? TileSize { get; init; }

    /// <summary>Gets the resolution levels of a tiled part.</summary>
    public ExrLevelMode LevelMode { get; init; }

    /// <summary>Gets the chromaticities attribute, or <see langword="null"/> when absent (Rec. 709 is meant then).</summary>
    public ColorPrimaries? Chromaticities { get; init; }

    /// <summary>Gets the whiteLuminance attribute: the luminance, in cd/m², of RGB (1, 1, 1), or <see langword="null"/>.</summary>
    public double? WhiteLuminance { get; init; }

    /// <summary>Gets the colorInteropID attribute, such as "lin_rec709_scene", or <see langword="null"/>.</summary>
    public string? ColorInteropId { get; init; }

    /// <summary>Gets the view attribute of a multi-view file, such as "left", or <see langword="null"/>.</summary>
    public string? View { get; init; }

    /// <summary>Gets the framesPerSecond attribute, or <see langword="null"/>.</summary>
    public double? FramesPerSecond { get; init; }

    /// <summary>Gets the timeCode attribute's time and control packet (SMPTE 12M), or <see langword="null"/>.</summary>
    public ulong? TimeCode { get; init; }

    /// <summary>Gets every attribute of the header, in file order, as stored.</summary>
    public IReadOnlyList<ExrHeaderEntry> Attributes { get; init; } = [];
}

/// <summary>Details only OpenEXR has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public sealed class ExrDetails : FormatDetails
{
    /// <summary>Gets or sets every part of the file.</summary>
    public IReadOnlyList<ExrPart> Parts { get; set; } = [];

    /// <summary>Gets or sets the index of the part the raster was read from.</summary>
    public int Part { get; set; }

    /// <summary>Gets or sets the layer the raster was read from: empty for the default layer.</summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>Gets or sets the area the raster covers, in the file's pixel coordinates: the data window, or the display window.</summary>
    public Rect Window { get; set; }

    /// <summary>Gets the data window of the part read.</summary>
    public Rect DataWindow => Part < Parts.Count ? Parts[Part].DataWindow : default;

    /// <summary>Gets the display window of the part read.</summary>
    public Rect DisplayWindow => Part < Parts.Count ? Parts[Part].DisplayWindow : default;

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new ExrDetails { Parts = Parts, Part = Part, Layer = Layer, Window = Window };
    }
}

/// <summary>Options for reading OpenEXR, on top of the general <see cref="ReaderOptions"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public sealed record ExrReaderOptions : ReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static new ExrReaderOptions Default { get; } = new();

    /// <summary>Gets the index of the part to read. Defaults to 0; <see cref="Raster.LoadAll(Stream, EgamiContext?, ReaderOptions?)"/> reads every part.</summary>
    public int Part { get; init; }

    /// <summary>
    /// Gets the layer to read, such as "diffuse" for diffuse.R, diffuse.G, diffuse.B and diffuse.A. Defaults to
    /// <see langword="null"/>: the channels without a layer, or the first layer with color channels when there are none.
    /// </summary>
    public string? Layer { get; init; }

    /// <summary>Gets which window the raster covers. Defaults to <see cref="ExrWindow.Data"/>.</summary>
    public ExrWindow Window { get; init; }

    /// <summary>Gets the one channel to read, for <see cref="ExrImage.ReadChannel(Stream, string, ExrReaderOptions?, EgamiContext?)"/>.</summary>
    internal string? Channel { get; init; }
}

/// <summary>Options for writing OpenEXR: one scanline part, half samples for half pixel types and float otherwise.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr-write"/>
/// </example>
public sealed class ExrWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new ExrWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the compression: <see cref="ExrCompression.None"/>, <see cref="ExrCompression.Rle"/>, <see cref="ExrCompression.Zips"/>,
    /// <see cref="ExrCompression.Zip"/> (the default) or <see cref="ExrCompression.Piz"/>.
    /// </summary>
    public ExrCompression Compression { get; init; } = ExrCompression.Zip;

    /// <summary>
    /// Gets the sample type to write, or <see langword="null"/> (the default) to choose by pixel type: half for
    /// <see cref="Pixels.RgbaF16"/> and 8 or 16-bit types, float for float types.
    /// </summary>
    public ExrPixelType? PixelType { get; init; }
}
