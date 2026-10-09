namespace Egami.Formats.Png;

/// <summary>How the PNG writer filters scanlines before compression (PNG specification, section 12.7).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="png-writer"/>
/// </example>
public enum PngFilterMode
{
    /// <summary>
    /// For each row, try all five filters and keep the one with the smallest sum of absolute values, as the specification recommends.
    /// Palette images and bit depths below 8 use no filter.
    /// </summary>
    Adaptive,

    /// <summary>No filter.</summary>
    None,

    /// <summary>Difference from the pixel to the left.</summary>
    Sub,

    /// <summary>Difference from the pixel above.</summary>
    Up,

    /// <summary>Difference from the average of left and above.</summary>
    Average,

    /// <summary>Difference from the Paeth predictor of left, above and upper-left.</summary>
    Paeth,
}

/// <summary>The Deflate strategy for the image data.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="png-writer"/>
/// </example>
public enum PngCompressionStrategy
{
    /// <summary>
    /// The strategy Egami picks. Currently <see cref="Standard"/>: in Egami's benchmarks with the runtime's zlib, <see cref="Filtered"/>
    /// was never smaller and was larger at level 9.
    /// </summary>
    Auto,

    /// <summary>zlib's default strategy.</summary>
    Standard,

    /// <summary>Tuned for filtered data: favors Huffman coding over string matching.</summary>
    Filtered,

    /// <summary>Huffman coding only, no string matching. Fast.</summary>
    HuffmanOnly,

    /// <summary>Only matches of the previous byte. Good for images with long runs.</summary>
    RunLengthEncoding,
}

/// <summary>Options for writing PNG. The defaults keep every pixel exactly, in the color type that matches the raster's pixel type.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="png-writer"/>
/// </example>
public sealed class PngWriterOptions : WriterOptions
{
    private readonly int? _bitDepth;

    private readonly int _compressionLevel = 5;
    private readonly int _maxIdatSize = 64 * 1024;

    /// <summary>Gets the default options.</summary>
    public static new PngWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the color type to write, or <see langword="null"/> to follow the pixel type (or <see cref="Reduce"/>). A palette for an
    /// image with more colors than the bit depth holds is built as <see cref="Palette"/> says.
    /// </summary>
    public PngColorType? ColorType { get; init; }

    /// <summary>
    /// Gets how a palette is built when <see cref="ColorType"/> is <see cref="PngColorType.Palette"/> and the image has more colors
    /// than fit; partial alpha is kept in the palette. Defaults to <see cref="Quantization.PaletteOptions.Default"/>. Images whose
    /// colors fit keep them exactly, and <see cref="Reduce"/> never quantizes.
    /// </summary>
    public Quantization.PaletteOptions Palette { get; init; } = Quantization.PaletteOptions.Default;

    /// <summary>
    /// Gets whether only the first frame is written. Defaults to <see langword="false"/>: a raster with more than one frame is
    /// written as an APNG, which viewers without APNG support show as its first frame.
    /// </summary>
    public bool FirstFrameOnly { get; init; }

    /// <summary>
    /// Gets the bit depth to write, or <see langword="null"/> for the color type's natural depth.
    /// Lower depths than the image holds round each value to the nearest one the depth can store.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1, 2, 4, 8 or 16.</exception>
    public int? BitDepth
    {
        get => _bitDepth;
        init => _bitDepth = value is null or 1 or 2 or 4 or 8 or 16 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "PNG bit depths are 1, 2, 4, 8 and 16.");
    }

    /// <summary>
    /// Gets a value indicating whether to scan the image first and write the smallest exact form: no alpha when every pixel is opaque,
    /// gray when every pixel is gray, a palette when there are at most 256 colors, and the lowest bit depth that keeps every value.
    /// The file decodes to exactly the same pixels. Ignored when <see cref="ColorType"/> is set. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Reduce { get; init; }

    /// <summary>Gets how rows are filtered. Defaults to <see cref="PngFilterMode.Adaptive"/>.</summary>
    public PngFilterMode Filter { get; init; }

    /// <summary>
    /// Gets the zlib compression level, 0 (none, fastest) to 9 (smallest). Defaults to 5, which on photos compressed over twice as
    /// fast as level 6 for files about 5% larger.
    /// </summary>
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

    /// <summary>Gets the Deflate strategy. Defaults to <see cref="PngCompressionStrategy.Auto"/>.</summary>
    public PngCompressionStrategy CompressionStrategy { get; init; }

    /// <summary>Gets the largest IDAT chunk, in bytes. Defaults to 65,536.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    internal int MaxIdatSize
    {
        get => _maxIdatSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxIdatSize = value;
        }
    }
}
