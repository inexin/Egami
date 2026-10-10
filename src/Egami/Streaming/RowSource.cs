using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Streaming;

/// <summary>
/// The reader interface the Phase 2.2 format tasks implement: a format that can hand out rows opens a <see cref="RowSource"/>
/// instead of returning a whole raster. Formats without it are read whole by <see cref="BufferedRowSource"/>.
/// </summary>
internal interface IRowFormatReader
{
    /// <summary>
    /// Reads the header and the metadata before the image data, and returns a source positioned before the first frame. The source
    /// owns <paramref name="input"/> from then on: its <see cref="RowSource.Dispose()"/> returns unread bytes and disposes it.
    /// </summary>
    /// <param name="input">The data, positioned at the start of the image.</param>
    /// <param name="options">
    /// How to read. <see cref="ReaderOptions.Region"/> and <see cref="ReaderOptions.MinimumSize"/> are applied by the row stage; a
    /// source may use them only to skip work, and must report any reduction of its own in <see cref="RowSource.NativeScale"/>.
    /// </param>
    RowSource Open(EgamiInput input, ReaderOptions options);
}

/// <summary>Work that needs a source's natural pixel type, reached through <see cref="RowSource.WithNaturalType"/>.</summary>
/// <typeparam name="TResult">What the work returns.</typeparam>
internal interface INaturalTypeVisitor<TResult>
{
    TResult Visit<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>Marks a reader that reduces while decoding when <see cref="ReaderOptions.NativeScale"/> asks, by powers of two.</summary>
internal interface INativeScaling
{
    /// <summary>Gets the largest reduction the reader applies itself.</summary>
    int MaxNativeScale { get; }
}

/// <summary>
/// One open image as a reader produces it: frames of rows at the reader's own scale, in the reader's natural pixel type,
/// converted on the way out. Rows of a frame are asked for in order; a source decodes or skips the rows in between as it must.
/// </summary>
internal abstract class RowSource : IDisposable
{
    /// <summary>Gets the stored width, before any reduction.</summary>
    public abstract int StoredWidth { get; }

    /// <summary>Gets the stored height, before any reduction.</summary>
    public abstract int StoredHeight { get; }

    /// <summary>Gets the reduction the reader applied itself; rows are ceil(stored size / NativeScale) big.</summary>
    public abstract int NativeScale { get; }

    /// <summary>Gets the largest reduction the reader could have applied itself, which the plan needs to agree on the box factor.</summary>
    public abstract int MaxNativeScale { get; }

    /// <summary>Gets the layout of the reader's natural pixel type.</summary>
    public abstract PixelLayoutInfo Layout { get; }

    /// <summary>Calls <paramref name="visitor"/> with the reader's natural pixel type.</summary>
    public abstract TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor);

    /// <summary>Gets the metadata read so far.</summary>
    public abstract RasterMetadata Metadata { get; }

    /// <summary>Gets the number of frames, or <see langword="null"/> while unknown.</summary>
    public abstract int? FrameCount { get; }

    /// <summary>Gets whether rows are decoded as they are asked for or served from a buffer.</summary>
    public abstract RasterAccess Access { get; }

    /// <summary>Gets the most bytes the source holds while decoding.</summary>
    public abstract long WorkingBytes { get; }

    /// <summary>
    /// Gets whether <see cref="Metadata"/> is complete. Formats can put metadata after the image data (PNG text and eXIf, GIF
    /// comments), which is read only once the last frame is finished.
    /// </summary>
    public virtual bool MetadataComplete => true;

    /// <summary>Moves to the next frame: the first call moves to frame 0. Returns its metadata, or <see langword="null"/> after the last.</summary>
    public abstract FrameMetadata? NextFrame();

    /// <summary>Gets whether <see cref="PreviewRow"/> can show the frame in progress before its rows can be read.</summary>
    public virtual bool CanPreview => false;

    /// <summary>
    /// Converts a row of a preview of the frame in progress: what has arrived so far, each known pixel repeated over the block it
    /// stands for. Rows come in order but the decode does not move on; nothing is consumed.
    /// </summary>
    public virtual void PreviewRow<TPixel>(int y, int x, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw new NotSupportedException("The format has no preview.");
    }

    /// <summary>
    /// Says no more rows of the current frame are needed, so the source can skip the rest of its data without decoding it and,
    /// after the last frame, read what follows the image. Called again, it does nothing.
    /// </summary>
    public virtual void EndFrame()
    {
    }

    /// <summary>
    /// Converts row <paramref name="y"/> of the current frame, from column <paramref name="x"/>, into <paramref name="destination"/>.
    /// <paramref name="y"/> never decreases within a frame.
    /// </summary>
    public abstract void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Returns the source's buffers.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected abstract void Dispose(bool disposing);
}

/// <summary>
/// The fallback for formats without <see cref="IRowFormatReader"/>: the reader's <see cref="IFormatReader.Read(EgamiInput, ReaderOptions)"/>
/// decodes the whole image in its natural pixel type, and rows are served from it. Converting a natural row gives the same
/// pixels as reading straight into the target type, which the streaming tests check.
/// </summary>
internal sealed class BufferedRowSource : RowSource
{
    private Raster? _raster;
    private int _frame = -1;

    private BufferedRowSource(Raster raster, int storedWidth, int storedHeight, int nativeScale, int maxNativeScale)
    {
        _raster = raster;
        StoredWidth = storedWidth;
        StoredHeight = storedHeight;
        NativeScale = nativeScale;
        MaxNativeScale = maxNativeScale;
    }

    public override int StoredWidth { get; }

    public override int StoredHeight { get; }

    public override int NativeScale { get; }

    public override int MaxNativeScale { get; }

    public override PixelLayoutInfo Layout => Raster.Layout;

    public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
    {
        return Raster.Accept(new Adapter<TResult>(visitor));
    }

    public override RasterMetadata Metadata => Raster.Metadata;

    public override int? FrameCount => Raster.FrameCount;

    public override RasterAccess Access => RasterAccess.Buffered;

    public override long WorkingBytes => Bytes(Raster);

    /// <summary>Gets the whole decoded image.</summary>
    public Raster Raster => _raster ?? throw new ObjectDisposedException(nameof(BufferedRowSource));

    /// <summary>
    /// Decodes the whole image with <paramref name="format"/>'s reader. A reader that scales natively is asked for the scale
    /// <see cref="ReadPlan"/> picks for its stored size; any other reader decodes at full size and the row stage reduces.
    /// </summary>
    public static BufferedRowSource Read(FormatInfo format, EgamiInput input, ReaderOptions options)
    {
        IFormatReader reader = format.Reader!;
        int maxScale = reader is INativeScaling native ? native.MaxNativeScale : 1;
        int storedWidth = 0;
        int storedHeight = 0;
        int scale = 1;
        ReaderOptions read = options with { Region = null, MinimumSize = null, NativeScale = null };
        if (maxScale > 1 && (options.Region is not null || options.MinimumSize is not null))
        {
            Rect? region = options.Region;
            Size? minimum = options.MinimumSize;
            read = read with
            {
                NativeScale = (width, height) =>
                {
                    (storedWidth, storedHeight) = (width, height);
                    scale = ReadPlan.Create(width, height, region, minimum, maxScale).NativeScale;
                    return scale;
                },
            };
        }

        Raster raster = reader.Read(input, read);
        if (storedWidth == 0)
        {
            (storedWidth, storedHeight) = (raster.Width, raster.Height);
        }

        return new BufferedRowSource(raster, storedWidth, storedHeight, scale, maxScale);
    }

    /// <summary>Hands the whole image to the caller, who then owns it; the source can no longer serve rows.</summary>
    public Raster Take()
    {
        Raster raster = Raster;
        _raster = null;
        return raster;
    }

    public override FrameMetadata? NextFrame()
    {
        Raster raster = Raster;
        _frame = Math.Min(_frame + 1, raster.FrameCount);
        return _frame < raster.FrameCount ? raster.FrameMetadataAt(_frame) : null;
    }

    public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
    {
        Raster.ReadFrameRow(_frame, y, x, destination);
    }

    protected override void Dispose(bool disposing)
    {
        _raster?.Dispose();
        _raster = null;
    }

    private sealed class Adapter<TResult>(INaturalTypeVisitor<TResult> visitor) : IRasterVisitor<TResult>
    {
        public TResult Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            return visitor.Visit<TPixel>();
        }
    }

    /// <summary>Returns the pixel bytes of every frame of <paramref name="raster"/>.</summary>
    internal static long Bytes(Raster raster)
    {
        return (long)raster.Width * raster.Height * raster.Layout.BytesPerPixel * raster.FrameCount;
    }
}

/// <summary>Where the header of an incremental decode stands, as <see cref="IIncrementalFormatReader.HeaderStatus"/> tells it.</summary>
internal enum HeaderState
{
    /// <summary>Not every byte <see cref="IRowFormatReader.Open"/> reads has arrived yet.</summary>
    NotYet,

    /// <summary>Every byte <see cref="IRowFormatReader.Open"/> reads has arrived: the source can be opened.</summary>
    Arrived,

    /// <summary>
    /// As <see cref="Arrived"/>, but no row can come out before the last byte (a progressive JPEG): a caller that only wants the
    /// whole image, not a preview, loses nothing by waiting for the end.
    /// </summary>
    ArrivedRowsAtEnd,

    /// <summary>The file decodes into a buffer first with these options, so the decode waits for the last byte.</summary>
    WaitsForEnd,
}

/// <summary>
/// Marks a reader whose row source can decode while bytes are still arriving: it says when the header has arrived, and its
/// source waits (with <see cref="NeedsInputException"/>) instead of treating missing bytes as the end of the data.
/// </summary>
internal interface IIncrementalFormatReader
{
    /// <summary>Looks ahead, without consuming, at whether the header has arrived or the file will wait for the end.</summary>
    HeaderState HeaderStatus(EgamiInput input, ReaderOptions options);
}

/// <summary>A source whose rows are another source's preview rows, so the row stage can crop and reduce a preview too.</summary>
internal sealed class PreviewSource(RowSource inner) : RowSource
{
    public override int StoredWidth => inner.StoredWidth;

    public override int StoredHeight => inner.StoredHeight;

    public override int NativeScale => inner.NativeScale;

    public override int MaxNativeScale => inner.MaxNativeScale;

    public override PixelLayoutInfo Layout => inner.Layout;

    public override RasterMetadata Metadata => inner.Metadata;

    public override int? FrameCount => inner.FrameCount;

    public override RasterAccess Access => inner.Access;

    public override long WorkingBytes => inner.WorkingBytes;

    public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => inner.WithNaturalType(visitor);

    public override FrameMetadata? NextFrame() => throw new NotSupportedException();

    public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination) => inner.PreviewRow(y, x, destination);

    protected override void Dispose(bool disposing)
    {
    }
}
