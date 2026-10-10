using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats;

/// <summary>
/// An optional interface for a format's reader: an open image that hands out rows as they are asked for, so
/// <see cref="RasterDecoder"/> streams the format, with <see cref="ReaderOptions.Region"/> and <see cref="ReaderOptions.MinimumSize"/>
/// applied on the way out. A reader without it still works everywhere: the decoder reads the whole image with
/// <see cref="IFormatReader.Read(EgamiInput, ReaderOptions)"/> and serves rows from it. Load, Probe and an incremental decode
/// keep using <see cref="IFormatReader"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
/// </example>
public interface IRowFormatReader
{
    /// <summary>Reads the header and returns the open image, which reads its rows from <paramref name="input"/>; the decoder keeps the input open until it disposes the image.</summary>
    /// <param name="input">The data, positioned at the start of the image.</param>
    /// <param name="options">How to read. Region and MinimumSize are applied by the decoder; a reader may ignore them.</param>
    /// <returns>The open image.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
    /// </example>
    RowReader Open(EgamiInput input, ReaderOptions options);
}

/// <summary>
/// One open image, as a format reader streams it: frames of rows at the stored size. Derive from <see cref="RowReader{TNatural}"/>,
/// which names the natural pixel type.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
/// </example>
public abstract class RowReader : IDisposable
{
    /// <summary>Creates the reader. Only <see cref="RowReader{TNatural}"/> can derive from it, since it names the natural pixel type.</summary>
    protected RowReader()
    {
    }

    /// <summary>Gets the stored width.</summary>
    public abstract int Width { get; }

    /// <summary>Gets the stored height.</summary>
    public abstract int Height { get; }

    /// <summary>Gets the layout of the natural pixel type.</summary>
    public abstract PixelLayoutInfo Layout { get; }

    /// <summary>Gets the metadata read so far.</summary>
    public abstract RasterMetadata Metadata { get; }

    /// <summary>Gets the number of frames, or <see langword="null"/> while unknown. Defaults to 1.</summary>
    public virtual int? FrameCount => 1;

    /// <summary>Gets whether rows are decoded as they are asked for or served from a buffer. Defaults to streamed.</summary>
    public virtual RasterAccess Access => RasterAccess.Streamed;

    /// <summary>Gets the most bytes the reader holds while decoding, for <see cref="RasterInfo.WorkingBytes"/>. Defaults to 0.</summary>
    public virtual long WorkingBytes => 0;

    /// <summary>Gets whether <see cref="Metadata"/> is complete; a format can store metadata after its image data. Defaults to true.</summary>
    public virtual bool IsMetadataComplete => true;

    /// <summary>
    /// Moves to the next frame: the first call moves to frame 0. Returns its metadata, or <see langword="null"/> after the last.
    /// </summary>
    /// <returns>The frame's metadata, or <see langword="null"/> when there are no more frames.</returns>
    public abstract FrameMetadata? NextFrame();

    /// <summary>
    /// Converts row <paramref name="y"/> of the current frame, from column <paramref name="x"/>, into <paramref name="destination"/>.
    /// <paramref name="y"/> never decreases within a frame, so rows in between can be skipped.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="y">The row.</param>
    /// <param name="x">The first column.</param>
    /// <param name="destination">Where the row goes; its length is how many columns are wanted.</param>
    public abstract void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Says no more rows of the current frame are needed, so the rest can be skipped. Does nothing by default.</summary>
    public virtual void EndFrame()
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Returns the reader's buffers.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    internal abstract TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor);
}

/// <summary>An open image whose natural pixel type is <typeparamref name="TNatural"/>, as <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/> would give it.</summary>
/// <typeparam name="TNatural">The pixel type that best fits the file.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
/// </example>
public abstract class RowReader<TNatural> : RowReader
    where TNatural : unmanaged, IPixelType<TNatural>
{
    /// <inheritdoc/>
    public override PixelLayoutInfo Layout => TNatural.Layout;

    internal sealed override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<TNatural>();
}
