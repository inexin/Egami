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
    /// <summary>Gray for gray pixel types, YCbCr for everything else.</summary>
    Auto,

    /// <summary>One gray component, whatever the pixel type; color is converted to luma.</summary>
    Gray,
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

    /// <summary>Gets which components to write. Defaults to <see cref="JpegColorType.Auto"/>.</summary>
    public JpegColorType ColorType { get; init; }

    /// <summary>
    /// Gets the color that transparent pixels are composited onto before writing, since JPEG has no alpha, or
    /// <see langword="null"/> to ignore alpha (every pixel written as if opaque). Its own alpha is ignored. Defaults to
    /// <see langword="null"/>.
    /// </summary>
    public Rgba8? Background { get; init; }
}
