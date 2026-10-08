using Egami.Pixels;

namespace Egami.Formats;

/// <summary>How a reader handles damaged data.</summary>
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

/// <summary>Options for reading. The defaults read everything leniently.</summary>
public sealed record ReaderOptions
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
    /// Gets the smallest size the caller needs, or <see langword="null"/> for full size. Formats that can decode at a reduced scale,
    /// such as JPEG, may return a smaller image that is still at least this big. Others ignore it.
    /// </summary>
    public Size? TargetSize { get; init; }
}

/// <summary>
/// Options for writing. Each format has a subclass with its own settings; writers given this base class use their defaults.
/// </summary>
public class WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static WriterOptions Default { get; } = new();

    /// <summary>Gets whether to leave out all metadata. Defaults to <see langword="false"/>: writers keep whatever their format supports.</summary>
    public bool StripMetadata { get; init; }
}

/// <summary>What a file's header says, read without decoding pixels.</summary>
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
}
