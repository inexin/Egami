using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>How the chroma (Cb and Cr) components are sampled relative to luma.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-write"/>
/// </example>
public enum ChromaSubsampling
{
    /// <summary>4:2:0: half the width and half the height. The usual choice for photos.</summary>
    Subsample420,

    /// <summary>4:2:2: half the width, full height.</summary>
    Subsample422,

    /// <summary>4:4:4: every pixel keeps its own chroma. Larger files; sharper colored edges and text.</summary>
    Subsample444,
}

/// <summary>Which components a JPEG is written with.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="jpeg-details"/>
/// </example>
public enum JpegColorType
{
    /// <summary>Gray for gray pixel types, CMYK for CMYK pixel types, YCbCr for everything else.</summary>
    Auto,

    /// <summary>One gray component, whatever the pixel type; color is converted to luma.</summary>
    Gray,

    /// <summary>
    /// Four CMYK components with an Adobe marker (transform 0), the values inverted as Photoshop writes them and every reader
    /// expects. Other pixel types are converted to ink with the plain formula. <see cref="Auto"/> picks this for CMYK pixel types.
    /// </summary>
    Cmyk,

    /// <summary>
    /// Four YCCK components with an Adobe marker (transform 2): cyan, magenta and yellow converted to YCbCr as libjpeg converts
    /// Adobe CMYK, chroma subsampled as <see cref="JpegWriterOptions.Subsampling"/> says, and the inverted black at full size.
    /// Usually smaller than <see cref="Cmyk"/>.
    /// </summary>
    Ycck,
}

/// <summary>
/// Options for writing JPEG. The defaults are libjpeg-turbo's <c>cjpeg</c> defaults: quality 75, 4:2:0 and the standard Huffman
/// tables of T.81 Annex K, so files compare one to one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="jpeg-details"/>
/// </example>
public sealed class JpegWriterOptions : WriterOptions
{
    private readonly int _quality = 75;
    private readonly int _restartInterval;
    private readonly int _precision = 8;

    /// <summary>Gets the default options.</summary>
    public static new JpegWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the quality, 1 (smallest) to 100 (best). It scales the example quantization tables of T.81 Annex K with the IJG
    /// formula, so the same number gives about the same file as other libjpeg-based encoders. Defaults to 75.
    /// </summary>
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

    /// <summary>Gets the chroma subsampling. Defaults to <see cref="ChromaSubsampling.Subsample420"/> (4:2:0). Ignored for gray output.</summary>
    public ChromaSubsampling Subsampling { get; init; }

    /// <summary>
    /// Gets a value indicating whether to build Huffman tables for this image in a second pass instead of using the standard ones:
    /// usually 5 to 10% smaller files, and slower. Defaults to <see langword="false"/>.
    /// </summary>
    public bool OptimizeHuffman { get; init; }

    /// <summary>
    /// Gets the number of MCUs between restart markers, or 0 for none. Restart markers let a reader resynchronize after damaged
    /// data, at 2 bytes each. Defaults to 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 65,535.</exception>
    public int RestartInterval
    {
        get => _restartInterval;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ushort.MaxValue);
            _restartInterval = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether to write a progressive file: libjpeg's standard scan script (ten scans for color, six for
    /// gray) with Huffman tables optimized for each scan, as <c>cjpeg -progressive</c> writes. Readers show a coarse image first
    /// and refine it; files are usually a few percent smaller than baseline, and decode to the same pixels. The encoder holds every
    /// quantized coefficient until the last scan, about 2 bytes per sample, counted against <see cref="SafetyLimits.MaxTotalBytes"/>;
    /// a streamed encode buffers. Re-saving a progressive file writes baseline unless this is set. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool Progressive { get; init; }

    /// <summary>
    /// Gets the bits per sample, 8 or 12. 12 keeps the detail of 16-bit and float images (written as SOF1, or SOF2 when
    /// progressive, with Huffman tables always optimized), but no browser shows 12-bit JPEG; 8-bit pixel types gain nothing from
    /// it. Defaults to 8.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 8 or 12.</exception>
    public int Precision
    {
        get => _precision;
        init
        {
            if (value is not (8 or 12))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "JPEG precision is 8 or 12 bits.");
            }

            _precision = value;
        }
    }

    /// <summary>Gets which components to write. Defaults to <see cref="JpegColorType.Auto"/>.</summary>
    public JpegColorType ColorType { get; init; }

    /// <summary>
    /// Gets the color that transparent pixels are composited onto before writing, since JPEG has no alpha, or
    /// <see langword="null"/> to ignore alpha (every pixel written as if opaque). Its own alpha is ignored. Defaults to
    /// <see langword="null"/>.
    /// </summary>
    public Rgba8? Background { get; init; }
}
