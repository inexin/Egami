using Egami.Metadata;

namespace Egami.Formats.Tiff;

/// <summary>The TIFF format, registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-load"/>
/// </example>
public static class TiffFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "TIFF",
        MimeTypes = ["image/tiff"],
        Extensions = ["tif", "tiff"],
        SignatureLength = TiffReader.SignatureLength,
        Matches = TiffReader.Matches,
        Reader = TiffReader.Instance,
        Writer = TiffWriter.Instance,
    };
}

/// <summary>Options for reading TIFF.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-page"/>
/// </example>
public sealed record TiffReaderOptions : ReaderOptions
{
    /// <summary>
    /// Gets the page Load returns, by its index among the full-resolution pages, or <see langword="null"/> (the default) for the
    /// first. Reduced-resolution pages and SubIFDs are never counted.
    /// </summary>
    public int? Page { get; init; }

    /// <summary>
    /// Gets whether LoadAll also returns the reduced-resolution pages and SubIFD previews, each after the page it belongs to.
    /// Defaults to <see langword="false"/>: only full-resolution pages.
    /// </summary>
    public bool IncludeReducedPages { get; init; }

    /// <summary>Gets the directory LoadAll asks for: a main-chain index and a SubIFD index (-1 for none). Overrides <see cref="Page"/>.</summary>
    internal (int Chain, int Sub)? Directory { get; init; }
}

/// <summary>Options for writing TIFF.</summary>
/// <remarks>
/// Re-saving a raster loaded from TIFF keeps the source's compression, predictor, tiling and byte order for every option left at
/// <see langword="null"/>, so a round trip writes the same kind of file. A JPEG or WebP source compression is kept only while the
/// raster is unchanged since loading; otherwise it falls back to Deflate, to avoid a second lossy pass.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save"/>
/// </example>
public sealed class TiffWriterOptions : WriterOptions
{
    private readonly int _compressionLevel = 5;
    private readonly int _quality = 75;
    private readonly int? _tileSize;

    /// <summary>Gets the default options.</summary>
    public static new TiffWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the compression, or <see langword="null"/> (the default) for the TIFF source's, else <see cref="TiffCompression.Deflate"/>.
    /// The writer takes <see cref="TiffCompression.None"/>, <see cref="TiffCompression.Lzw"/>, <see cref="TiffCompression.Deflate"/>,
    /// <see cref="TiffCompression.PackBits"/>, <see cref="TiffCompression.CcittGroup3"/> and <see cref="TiffCompression.CcittGroup4"/>
    /// (1-bit images only), <see cref="TiffCompression.Jpeg"/> and <see cref="TiffCompression.Webp"/>.
    /// </summary>
    public TiffCompression? Compression { get; init; }

    /// <summary>Gets the Deflate level, 0 (fastest) to 9 (smallest). Defaults to 5, as PNG's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 9.</exception>
    public int CompressionLevel
    {
        get => _compressionLevel;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 9);
            _compressionLevel = value;
        }
    }

    /// <summary>
    /// Gets the predictor for LZW and Deflate, or <see langword="null"/> (the default) for the source's, else
    /// <see cref="TiffPredictor.Horizontal"/> for 8, 16 and 32-bit integer samples and <see cref="TiffPredictor.FloatingPoint"/>
    /// for float samples. <see cref="TiffPredictor.None"/> turns it off.
    /// </summary>
    public TiffPredictor? Predictor { get; init; }

    /// <summary>Gets the quality of JPEG and lossy WebP compression, 1 to 100, with the JPEG and WebP writers' meanings. Defaults to 75.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _quality = value;
        }
    }

    /// <summary>
    /// Gets the side of square tiles, a multiple of 16, or 0 for strips of about 64 KB of uncompressed rows, or
    /// <see langword="null"/> (the default) for the source's layout, else strips.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not a multiple of 16.</exception>
    public int? TileSize
    {
        get => _tileSize;
        init => _tileSize = value is null or 0 || (value > 0 && value % 16 == 0)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Tiles are a multiple of 16 pixels square; 0 means strips.");
    }

    /// <summary>
    /// Gets whether classic TIFF or BigTIFF is written. Defaults to <see cref="TiffLayout.Classic"/>, which throws before writing
    /// anything when the uncompressed pixels pass 4 GB.
    /// </summary>
    public TiffLayout Layout { get; init; }

    /// <summary>
    /// Gets whether to write the smallest exact form the pixels allow: 1 bit when every pixel is black or white, gray when red,
    /// green and blue are equal, a palette of up to 256 colors for opaque images, 8 bits when every 16-bit sample is a multiple of
    /// 257. Defaults to <see langword="false"/>: each pixel type is written at its own depth.
    /// </summary>
    public bool Reduce { get; init; }

    /// <summary>
    /// Gets whether straight alpha is written premultiplied, with ExtraSamples 1 (associated). Premultiplied pixel types are always
    /// written that way. Defaults to <see langword="false"/>.
    /// </summary>
    public bool AssociatedAlpha { get; init; }

    /// <summary>
    /// Gets whether strips and tiles are compressed on up to <see cref="EgamiContext.MaxParallelism"/> threads. The file is the same
    /// either way. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ParallelCompression { get; init; }
}

/// <summary>A TIFF compression scheme, by its code in the Compression tag.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save"/>
/// </example>
public enum TiffCompression
{
    /// <summary>No compression (1).</summary>
    None = 1,

    /// <summary>CCITT modified Huffman run lengths (2); read only.</summary>
    CcittRle = 2,

    /// <summary>CCITT Group 3 fax, T.4 (3); 1-bit images only.</summary>
    CcittGroup3 = 3,

    /// <summary>CCITT Group 4 fax, T.6 (4); 1-bit images only.</summary>
    CcittGroup4 = 4,

    /// <summary>LZW (5).</summary>
    Lzw = 5,

    /// <summary>Old-style JPEG from TIFF 6.0 (6); read only, best effort.</summary>
    OldJpeg = 6,

    /// <summary>JPEG as Adobe's TIFF Technical Note 2 describes it (7).</summary>
    Jpeg = 7,

    /// <summary>Deflate (8), the zlib format.</summary>
    Deflate = 8,

    /// <summary>CCITT run lengths on 16-bit words (32771); read only.</summary>
    CcittRleWord = 32771,

    /// <summary>PackBits (32773).</summary>
    PackBits = 32773,

    /// <summary>ThunderScan 4-bit gray (32809); read only.</summary>
    ThunderScan = 32809,

    /// <summary>Deflate under its older private code (32946); read only, written as <see cref="Deflate"/>.</summary>
    AdobeDeflate = 32946,

    /// <summary>SGILog, LogL and LogLuv high dynamic range (34676); read only.</summary>
    SgiLog = 34676,

    /// <summary>SGILog24, LogLuv in 24 bits (34677); read only.</summary>
    SgiLog24 = 34677,

    /// <summary>WebP (50001).</summary>
    Webp = 50001,
}

/// <summary>The horizontal differencing applied before LZW or Deflate, from the Predictor tag.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save"/>
/// </example>
public enum TiffPredictor
{
    /// <summary>No prediction (1).</summary>
    None = 1,

    /// <summary>Each sample stored as the difference from the one a pixel to its left (2), for integer samples.</summary>
    Horizontal = 2,

    /// <summary>Adobe's floating-point predictor (3): byte planes, then horizontal differences, for float samples.</summary>
    FloatingPoint = 3,
}

/// <summary>Classic TIFF, with 32-bit offsets and a 4 GB limit, or BigTIFF, with 64-bit offsets.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save"/>
/// </example>
public enum TiffLayout
{
    /// <summary>Classic TIFF (version 42). Saving throws before writing anything when the file could pass 4 GB.</summary>
    Classic,

    /// <summary>BigTIFF (version 43), which some older readers cannot open.</summary>
    Big,

    /// <summary>Classic TIFF unless the uncompressed pixel bytes pass 4 GB, then BigTIFF.</summary>
    Auto,
}

/// <summary>The byte order of a TIFF file.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-details"/>
/// </example>
public enum TiffByteOrder
{
    /// <summary>Intel order, "II".</summary>
    LittleEndian,

    /// <summary>Motorola order, "MM".</summary>
    BigEndian,
}

/// <summary>What a page's samples mean, from the PhotometricInterpretation tag.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-details"/>
/// </example>
public enum TiffPhotometric
{
    /// <summary>Gray, 0 white (0).</summary>
    WhiteIsZero = 0,

    /// <summary>Gray, 0 black (1).</summary>
    BlackIsZero = 1,

    /// <summary>Red, green, blue (2).</summary>
    Rgb = 2,

    /// <summary>Indexes into a ColorMap (3).</summary>
    Palette = 3,

    /// <summary>A 1-bit mask for another page (4).</summary>
    TransparencyMask = 4,

    /// <summary>Inks, usually CMYK (5).</summary>
    Separated = 5,

    /// <summary>Luma and chroma (6).</summary>
    YCbCr = 6,

    /// <summary>CIE L*a*b* with signed a* and b* (8).</summary>
    CieLab = 8,

    /// <summary>CIE L*a*b* with a* and b* offset to unsigned, as ICC stores them (9).</summary>
    IccLab = 9,

    /// <summary>CIE L*a*b* scaled as ITU-T T.42 does (10).</summary>
    ItuLab = 10,

    /// <summary>SGILog luminance (32844).</summary>
    LogL = 32844,

    /// <summary>SGILog luminance and chromaticity (32845).</summary>
    LogLuv = 32845,
}

/// <summary>What an extra sample is, from the ExtraSamples tag.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-details"/>
/// </example>
public enum TiffExtraSample
{
    /// <summary>Unspecified data (0), which Egami drops with a warning.</summary>
    Unspecified = 0,

    /// <summary>Associated (premultiplied) alpha (1).</summary>
    Associated = 1,

    /// <summary>Unassociated (straight) alpha (2).</summary>
    Unassociated = 2,
}

/// <summary>How a page's samples are numbers, from the SampleFormat tag.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-details"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These are the TIFF specification's names.")]
public enum TiffSampleFormat
{
    /// <summary>Unsigned integers (1).</summary>
    UnsignedInteger = 1,

    /// <summary>Two's complement integers (2), read offset to unsigned so 0 becomes the middle value.</summary>
    SignedInteger = 2,

    /// <summary>IEEE floating point (3).</summary>
    Float = 3,
}

/// <summary>
/// A TIFF tag Egami does not map, kept as it was read so the TIFF writer can write it back: GeoTIFF, DNG and private tags.
/// </summary>
/// <param name="Id">The tag number.</param>
/// <param name="Type">The field type.</param>
/// <param name="Count">The number of values.</param>
/// <param name="Value">The values, little-endian whatever the file's byte order.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-details"/>
/// </example>
public sealed record TiffTag(int Id, ExifType Type, int Count, ReadOnlyMemory<byte> Value);

/// <summary>Details only TIFF has, kept per page in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-details"/>
/// </example>
public sealed class TiffDetails : FormatDetails
{
    /// <summary>Gets or sets the page's index in the file's chain of directories, counting every directory.</summary>
    public int PageIndex { get; set; }

    /// <summary>Gets or sets whether the page is a reduced-resolution copy (NewSubfileType bit 0, or a SubIFD).</summary>
    public bool IsReducedResolution { get; set; }

    /// <summary>Gets or sets the file's byte order. Defaults to little-endian.</summary>
    public TiffByteOrder ByteOrder { get; set; }

    /// <summary>Gets or sets whether the file is classic TIFF or BigTIFF. Defaults to classic.</summary>
    public TiffLayout Layout { get; set; }

    /// <summary>Gets or sets the compression. Defaults to <see cref="TiffCompression.None"/>.</summary>
    public TiffCompression Compression { get; set; } = TiffCompression.None;

    /// <summary>Gets or sets the predictor. Defaults to <see cref="TiffPredictor.None"/>.</summary>
    public TiffPredictor Predictor { get; set; } = TiffPredictor.None;

    /// <summary>Gets or sets what the samples mean. Defaults to <see cref="TiffPhotometric.Rgb"/>.</summary>
    public TiffPhotometric Photometric { get; set; } = TiffPhotometric.Rgb;

    /// <summary>Gets or sets the bits of each sample as stored. Defaults to 8.</summary>
    public int BitsPerSample { get; set; } = 8;

    /// <summary>Gets or sets how the samples are numbers. Defaults to unsigned integers.</summary>
    public TiffSampleFormat SampleFormat { get; set; } = TiffSampleFormat.UnsignedInteger;

    /// <summary>Gets or sets whether each sample has its own plane (PlanarConfiguration 2).</summary>
    public bool Planar { get; set; }

    /// <summary>Gets or sets the tile width, or <see langword="null"/> for a page in strips.</summary>
    public int? TileWidth { get; set; }

    /// <summary>Gets or sets the tile height, or <see langword="null"/> for a page in strips.</summary>
    public int? TileHeight { get; set; }

    /// <summary>Gets or sets the rows in each strip, or <see langword="null"/> for a tiled page.</summary>
    public int? RowsPerStrip { get; set; }

    /// <summary>Gets or sets what each extra sample is, in order.</summary>
    public IList<TiffExtraSample> ExtraSamples { get; set; } = [];

    /// <summary>Gets or sets the ink names of a separated page.</summary>
    public IList<string> InkNames { get; set; } = [];

    /// <summary>Gets the tags Egami does not map, kept raw so the TIFF writer writes them back.</summary>
    public IList<TiffTag> OtherTags { get; } = [];

    /// <summary>
    /// Gets or sets whether the pixels are as loaded: no operation has run on the raster since, which decides whether a JPEG, WebP
    /// or CCITT source keeps its compression on save.
    /// </summary>
    internal bool Unchanged { get; set; }

    /// <summary>Gets or sets whether a WebP page's first unit was lossless, which a re-save keeps.</summary>
    internal bool WebpLossless { get; set; }

    internal override void PixelsChanged() => Unchanged = false;

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        TiffDetails copy = new()
        {
            PageIndex = PageIndex,
            IsReducedResolution = IsReducedResolution,
            ByteOrder = ByteOrder,
            Layout = Layout,
            Compression = Compression,
            Predictor = Predictor,
            Photometric = Photometric,
            BitsPerSample = BitsPerSample,
            SampleFormat = SampleFormat,
            Planar = Planar,
            TileWidth = TileWidth,
            TileHeight = TileHeight,
            RowsPerStrip = RowsPerStrip,
            ExtraSamples = [.. ExtraSamples],
            InkNames = [.. InkNames],
            Unchanged = Unchanged,
            WebpLossless = WebpLossless,
        };
        foreach (TiffTag tag in OtherTags)
        {
            copy.OtherTags.Add(tag);
        }

        return copy;
    }
}
