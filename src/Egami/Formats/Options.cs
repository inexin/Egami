using Egami.Pixels;

namespace Egami.Formats;

/// <summary>How a reader handles damaged data.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/MemoryAndSecurity.cs" region="untrusted"/>
/// </example>
public enum RecoveryMode
{
    /// <summary>
    /// Return what was decoded when the data ends early or a checksum fails after valid pixels, and note the problem in
    /// <see cref="Metadata.RasterMetadata.Warnings"/>. Data that would need guessing, such as an invalid header, still throws.
    /// </summary>
    Lenient,

    /// <summary>Throw <see cref="CorruptDataException"/> on any problem.</summary>
    Strict,
}

/// <summary>Options for reading. The defaults read everything leniently. Formats with options of their own derive from it, such as JpegReaderOptions.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="scaled-decode"/>
/// </example>
public record ReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static ReaderOptions Default { get; } = new();

    /// <summary>Gets how damaged data is handled. Defaults to <see cref="RecoveryMode.Lenient"/>.</summary>
    public RecoveryMode Recovery { get; init; }

    /// <summary>Gets whether metadata such as EXIF, ICC, XMP and text is read. Defaults to <see langword="true"/>.</summary>
    public bool ReadMetadata { get; init; } = true;

    /// <summary>Gets the frames to read. Defaults to all of them. Use <c>..1</c> for only the first frame.</summary>
    public Range Frames { get; init; } = Range.All;

    /// <summary>
    /// Gets the smallest size the caller needs, or <see langword="null"/> for full size. The image is decoded reduced by the largest
    /// whole factor N that keeps it at least this big: ceil(width / N) by ceil(height / N), each pixel the alpha-weighted average
    /// of an N × N block of encoded values (partial blocks at the right and bottom edges average the pixels they have). JPEG keeps
    /// its IDCT scaling by 1/2, 1/4 or 1/8 and averages blocks only beyond 1/8. With <see cref="Region"/> the factor is chosen
    /// for the region.
    /// </summary>
    /// <remarks>Before 1.x 2.2, formats other than JPEG ignored this.</remarks>
    public Size? MinimumSize { get; init; }

    /// <summary>
    /// Gets the rectangle to decode, in stored pixels (before EXIF orientation), or <see langword="null"/> for the whole image.
    /// It is clipped to the image and applied before <see cref="MinimumSize"/>; the result is exactly the clipped rectangle, never
    /// widened to a block boundary. For JPEG with a reduced scale the result is the scaled image cropped to the scaled rectangle.
    /// </summary>
    /// <remarks>A rectangle that misses the image entirely, or has no area, throws <see cref="ArgumentOutOfRangeException"/>.</remarks>
    public Rect? Region { get; init; }

    /// <summary>
    /// Gets the reduction a reader that scales while decoding (JPEG) should use, chosen from the stored size, or
    /// <see langword="null"/> to choose from <see cref="MinimumSize"/> alone as in 1.3. Set by the streaming path, never by callers.
    /// </summary>
    internal Func<int, int, int>? NativeScale { get; init; }
}

/// <summary>How a decode produces rows, as <see cref="RasterInfo.Access"/> reports it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="region"/>
/// </example>
public enum RasterAccess
{
    /// <summary>The image is decoded into an internal buffer first, and rows are served from it.</summary>
    Buffered,

    /// <summary>Rows are decoded as they are asked for, holding only the format's own working set.</summary>
    Streamed,
}

/// <summary>
/// Options for writing. Each format has a subclass with its own settings; writers given this base class use their defaults.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
/// </example>
public class WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static WriterOptions Default { get; } = new();

    /// <summary>Gets whether to leave out all metadata. Defaults to <see langword="false"/>: writers keep whatever their format supports.</summary>
    public bool StripMetadata { get; init; }
}

/// <summary>What a file's header says, read without decoding pixels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
/// </example>
public sealed record RasterInfo
{
    /// <summary>Gets the format.</summary>
    public required FormatInfo Format { get; init; }

    /// <summary>Gets the width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Gets the height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Gets the layout of the pixel type <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/> would return.</summary>
    public required PixelLayoutInfo Layout { get; init; }

    /// <summary>Gets the number of frames, or <see langword="null"/> if finding out would need more than the header.</summary>
    public int? FrameCount { get; init; }

    /// <summary>Gets whether the header shows any metadata, such as EXIF, ICC, XMP or text.</summary>
    public bool HasMetadata { get; init; }

    /// <summary>Gets an estimate of the bytes a full load in the natural pixel type would allocate, so large images can be rejected before decoding.</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>
    /// Gets the EXIF orientation: how the stored pixels must be turned to show the image upright. Load returns the pixels as
    /// stored; apply this with <c>AutoOrient</c>. <see cref="Metadata.ImageOrientation.Normal"/> when the file says nothing.
    /// </summary>
    public Metadata.ImageOrientation Orientation { get; init; } = Metadata.ImageOrientation.Normal;

    /// <summary>
    /// Gets how many times an animation plays (0 for forever), as <see cref="Metadata.RasterMetadata.LoopCount"/> would say, or
    /// <see langword="null"/> for formats without animation.
    /// </summary>
    public int? LoopCount { get; init; }

    /// <summary>
    /// Gets whether a <see cref="RasterDecoder"/> streams this image's rows or decodes it into a buffer first. Set by
    /// <see cref="Raster.Probe(Stream, EgamiContext?, ReaderOptions?)"/> and the decoder; formats need not set it.
    /// </summary>
    public RasterAccess Access { get; init; }

    /// <summary>
    /// Gets the most bytes a <see cref="RasterDecoder"/> holds while decoding with the same options, besides the caller's band or
    /// target. Set by <see cref="Raster.Probe(Stream, EgamiContext?, ReaderOptions?)"/> and the decoder; formats need not set it.
    /// </summary>
    public long WorkingBytes { get; init; }
}
