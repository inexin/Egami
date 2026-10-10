using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats;

/// <summary>
/// An optional interface for a format's writer: an encoder that takes rows and frames one at a time. A writer that implements it
/// streams through <see cref="RasterEncoder{TPixel}"/> and <see cref="Raster.Transcode(Stream, Stream, FormatInfo, Action{Operations.RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?)"/>,
/// and <see cref="Raster.Save(Stream, FormatInfo, WriterOptions?)"/> and SaveAsync use it in place of
/// <see cref="IFormatWriter.Write"/>, so all of them write the same bytes. A writer without it still works everywhere: the
/// encoder gathers the image and calls <see cref="IFormatWriter.Write"/> at the end.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
/// </example>
public interface IRowFormatWriter
{
    /// <summary>
    /// Starts an encode into <paramref name="output"/>, writing what goes before the image data. An option that needs every pixel
    /// before the first byte throws <see cref="NotSupportedException"/> here when <see cref="EncodeStart{TPixel}.Streaming"/>, as
    /// <see cref="EncodeStart{TPixel}.Refuse"/> does, before anything is written.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the rows and frames.</typeparam>
    /// <param name="output">Where to write. The encoder leaves it open.</param>
    /// <param name="start">The size, metadata, options and, for Save, the whole image.</param>
    /// <returns>The encode in progress, which the caller disposes.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
    /// </example>
    RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>What an encode starts from: the size, metadata and options, and the whole image when Save has one.</summary>
/// <typeparam name="TPixel">The pixel type of the rows and frames.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
/// </example>
public sealed class EncodeStart<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    internal EncodeStart(int width, int height, RasterMetadata metadata, WriterOptions options, EgamiContext context, Raster<TPixel>? whole, bool seekable)
    {
        Width = width;
        Height = height;
        Metadata = metadata;
        Options = options;
        Context = context;
        Whole = whole;
        Seekable = seekable;
    }

    /// <summary>Gets the image width.</summary>
    public int Width { get; }

    /// <summary>Gets the image height.</summary>
    public int Height { get; }

    /// <summary>Gets the metadata to write.</summary>
    public RasterMetadata Metadata { get; }

    /// <summary>Gets the options, which may be the base <see cref="WriterOptions"/> or the format's own.</summary>
    public WriterOptions Options { get; }

    /// <summary>Gets the context, for its memory and parallelism.</summary>
    public EgamiContext Context { get; }

    /// <summary>
    /// Gets the whole image when Save writes one: choices may then look at every pixel, and <see cref="RowEncoder{TPixel}.WriteWhole"/>
    /// takes the rows in the file's order. <see langword="null"/> when streaming, where rows come top-down as the caller has them.
    /// </summary>
    public Raster<TPixel>? Whole { get; }

    /// <summary>Gets whether the output can seek back: when streaming, bottom-up rows can then land in place and counts be patched at the end.</summary>
    public bool Seekable { get; }

    /// <summary>Gets whether this is a streamed encode, without the whole image.</summary>
    public bool Streaming => Whole is null;

    /// <summary>
    /// Throws <see cref="NotSupportedException"/>, for a streamed encode, that <paramref name="what"/> needs every pixel before the
    /// first byte, and says what to set instead. Does nothing for Save.
    /// </summary>
    /// <param name="format">The format's name, which starts the message.</param>
    /// <param name="what">What needs every pixel, such as "Building a palette".</param>
    /// <param name="instead">What to set instead.</param>
    /// <exception cref="NotSupportedException">The encode is streamed.</exception>
    public void Refuse(string format, string what, string instead)
    {
        if (Streaming)
        {
            throw new NotSupportedException($"{format}: {what} needs every pixel before the first byte, so it cannot be streamed. {instead}");
        }
    }
}

/// <summary>
/// One encode in progress: a still image's rows top-down with <see cref="WriteRow"/>, or whole frames with <see cref="WriteFrame"/>,
/// then <see cref="Complete"/>.
/// </summary>
/// <typeparam name="TPixel">The pixel type of the rows and frames.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="row-format"/>
/// </example>
public abstract class RowEncoder<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>
    /// Gets whether the format holds more than one frame, so <see cref="WriteFrame"/> takes them all. Otherwise one frame is
    /// written as the still image, through <see cref="WriteRow"/>.
    /// </summary>
    public virtual bool Animates => false;

    /// <summary>Writes the next row of the still image, top-down.</summary>
    /// <param name="row">The row, <see cref="EncodeStart{TPixel}.Width"/> wide.</param>
    public abstract void WriteRow(ReadOnlySpan<TPixel> row);

    /// <summary>Writes the next frame of an animation. Only called when <see cref="Animates"/>.</summary>
    /// <param name="frame">The frame and its metadata; the encoder may not keep it after returning.</param>
    /// <exception cref="NotSupportedException">The format does not animate.</exception>
    public virtual void WriteFrame(Frame<TPixel> frame) => throw new NotSupportedException("The format holds one image.");

    /// <summary>Writes what follows the image data. Every row or frame has been written.</summary>
    public abstract void Complete();

    /// <summary>
    /// Writes the whole image Save was given, in steps: SaveAsync writes out the bytes so far at each one. By default the rows of
    /// the first frame, top-down, 64 at a time; a format with bottom-up rows or frames overrides it.
    /// </summary>
    /// <param name="raster">The image, which is also <see cref="EncodeStart{TPixel}.Whole"/>.</param>
    /// <returns>A step after each band or frame; the values only count steps.</returns>
    public virtual IEnumerable<int> WriteWhole(Raster<TPixel> raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        return Rows(raster);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Returns the encoder's buffers.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    private IEnumerable<int> Rows(Raster<TPixel> raster)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            WriteRow(raster.Row(y));
            if ((y & 63) == 63)
            {
                yield return y;
            }
        }
    }
}
