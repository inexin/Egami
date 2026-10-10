using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami;

/// <summary>Creates a <see cref="RasterEncoder{TPixel}"/>, which writes an image a band of rows or a frame at a time.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="encode-rows"/>
/// </example>
public static class RasterEncoder
{
    /// <summary>
    /// Starts writing a file, choosing the format from the extension as <see cref="Raster.Save(string, WriterOptions?)"/> does. An
    /// existing file is replaced; if the encoder is disposed before <see cref="RasterEncoder{TPixel}.Complete"/>, the partly written
    /// file is deleted.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the rows and frames to be written.</typeparam>
    /// <param name="path">The file path.</param>
    /// <param name="width">The image width, at least 1.</param>
    /// <param name="height">The image height, at least 1.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="metadata">
    /// The metadata to write, such as EXIF, ICC and text, or <see langword="null"/> for none. It is given now because most formats
    /// write it before the image data.
    /// </param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The encoder, which owns the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format uses the extension, or it has no writer.</exception>
    /// <exception cref="NotSupportedException">An option needs every pixel before the first byte, such as an automatic palette.</exception>
    /// <exception cref="IOException">The file cannot be created.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="encode-rows"/>
    /// </example>
    public static RasterEncoder<TPixel> Create<TPixel>(string path, int width, int height, WriterOptions? options = null, RasterMetadata? metadata = null, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(path);
        context ??= EgamiContext.Default;
        FormatInfo format = FormatPipeline.FormatForPath(context, path);
        options = FormatPipeline.OptionsForPath(format, path, options);
        CheckSize(width, height);
        FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            return new RasterEncoder<TPixel>(stream, format, width, height, options ?? WriterOptions.Default, metadata, context, path);
        }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// Starts writing an image to a stream. A seekable stream lets BMP, TGA and PFM rows land bottom-up and an APNG's frame count be
    /// filled in at the end, as Save writes them; a stream that cannot seek gets top-down BMP and TGA rows, and an APNG needs
    /// <see cref="Formats.Png.PngWriterOptions.FrameCount"/>.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the rows and frames to be written.</typeparam>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="format">The format.</param>
    /// <param name="width">The image width, at least 1.</param>
    /// <param name="height">The image height, at least 1.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="metadata">The metadata to write, or <see langword="null"/> for none.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <exception cref="UnsupportedFormatException">The format has no writer.</exception>
    /// <exception cref="NotSupportedException">An option needs every pixel before the first byte, such as an automatic palette.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="encode-frames"/>
    /// </example>
    public static RasterEncoder<TPixel> Create<TPixel>(Stream stream, FormatInfo format, int width, int height, WriterOptions? options = null, RasterMetadata? metadata = null, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        CheckSize(width, height);
        return new RasterEncoder<TPixel>(stream, format, width, height, options ?? WriterOptions.Default, metadata, context ?? EgamiContext.Default, null);
    }

    private static void CheckSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
    }
}

/// <summary>
/// An image being written a band of rows or a frame at a time, so a huge image never has to exist whole. The bytes are the ones
/// <see cref="Raster.Save(Stream, FormatInfo, WriterOptions?)"/> writes for the same pixels and options, except where a format must
/// write differently without seeking: top-down BMP and TGA rows, and run-length TGA, which is always top-down. Options that need
/// every pixel before the first byte (automatic palettes, PNG's Reduce, automatic alpha) throw at Create instead of quietly
/// writing something else.
/// </summary>
/// <remarks>
/// Write a still image with <see cref="WriteRows(Raster{TPixel}, int)"/>, top to bottom, or an animation with
/// <see cref="WriteFrame"/>; then call <see cref="Complete"/>, which writes what follows the image data. An encoder is one caller's
/// object and is not thread-safe. Formats from plugins that do not stream gather the image and write it at Complete.
/// </remarks>
/// <typeparam name="TPixel">The pixel type of the rows and frames.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="encode-rows"/>
/// </example>
public sealed class RasterEncoder<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly Stream _target;
    private readonly EncodeOutput _output;
    private readonly string? _path;
    private readonly RowEncoder<TPixel>? _encoder;
    private readonly IFormatWriter? _fallback;
    private readonly WriterOptions _options;
    private readonly RasterMetadata _metadata;
    private readonly EgamiContext _context;
    private readonly List<Frame<TPixel>> _gathered = [];
    private bool _frames;
    private bool _completed;
    private bool _disposed;

    internal RasterEncoder(Stream target, FormatInfo format, int width, int height, WriterOptions options, RasterMetadata? metadata, EgamiContext context, string? path)
    {
        IFormatWriter writer = format.Writer ?? throw new UnsupportedFormatException($"The {format.Name} format has no writer.");
        _target = target;
        _path = path;
        _options = options;
        _metadata = metadata ?? new RasterMetadata();
        _context = context;
        Format = format;
        Width = width;
        Height = height;
        _output = new EncodeOutput(target);
        try
        {
            if (writer is IRowFormatWriter rows)
            {
                _encoder = rows.Start(_output, new EncodeStart<TPixel>
                {
                    Width = width,
                    Height = height,
                    Metadata = _metadata,
                    Options = options,
                    Context = context,
                    Seekable = target.CanSeek,
                });
            }
            else
            {
                _fallback = writer;
            }
        }
        catch
        {
            _output.Dispose();
            throw;
        }
    }

    /// <summary>Gets whether frames make an animation in this format; otherwise one frame is the still image.</summary>
    internal bool Animates => _encoder?.Animates ?? true;

    /// <summary>Sets whether bytes are held until <see cref="WriteOutAsync"/>, for a caller that writes rows from async code.</summary>
    internal bool Hold
    {
        set => _output.Hold = value;
    }

    /// <summary>Writes out the bytes held, asynchronously.</summary>
    internal ValueTask WriteOutAsync(CancellationToken cancellationToken) => _output.WriteOutAsync(cancellationToken);

    /// <summary>Gets the format being written.</summary>
    public FormatInfo Format { get; }

    /// <summary>Gets the image width.</summary>
    public int Width { get; }

    /// <summary>Gets the image height.</summary>
    public int Height { get; }

    /// <summary>Gets how many rows of the still image have been written.</summary>
    public int RowsWritten { get; private set; }

    /// <summary>Gets how many frames have been written.</summary>
    public int FramesWritten { get; private set; }

    /// <summary>
    /// Writes the next <paramref name="rows"/> rows of the still image, from the top of <paramref name="band"/>'s first frame. Rows
    /// go top to bottom; together the calls write exactly <see cref="Height"/> rows.
    /// </summary>
    /// <param name="band">The rows, <see cref="Width"/> wide.</param>
    /// <param name="rows">How many of the band's rows to write, from its top.</param>
    /// <exception cref="ArgumentNullException"><paramref name="band"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="band"/> is not <see cref="Width"/> wide.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is negative, more than the band holds, or more than are left.</exception>
    /// <exception cref="InvalidOperationException">Frames were written, or the encoder is complete.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="encode-rows"/>
    /// </example>
    public void WriteRows(Raster<TPixel> band, int rows)
    {
        CheckRows(band, rows);
        WriteRowsCore(band, rows);
    }

    /// <summary>Writes every row of <paramref name="band"/>'s first frame as the next rows of the still image.</summary>
    /// <param name="band">The rows, <see cref="Width"/> wide.</param>
    /// <exception cref="ArgumentNullException"><paramref name="band"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="band"/> is not <see cref="Width"/> wide.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The band holds more rows than are left.</exception>
    /// <exception cref="InvalidOperationException">Frames were written, or the encoder is complete.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public void WriteRows(Raster<TPixel> band)
    {
        ArgumentNullException.ThrowIfNull(band);
        WriteRows(band, band.Height);
    }

    /// <summary>
    /// Writes the next frame of an animation (GIF, APNG), with its delay from <paramref name="metadata"/>. A format that holds one
    /// image takes one frame, written as the still image.
    /// </summary>
    /// <param name="frame">The frame, <see cref="Width"/> × <see cref="Height"/>; its first frame is written.</param>
    /// <param name="metadata">The frame's delay, or <see langword="null"/> for the frame's own metadata.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="frame"/> is not <see cref="Width"/> × <see cref="Height"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Rows were written, the format holds one image and it was written, an APNG to a stream that cannot seek has no
    /// <see cref="Formats.Png.PngWriterOptions.FrameCount"/>, or the encoder is complete.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="encode-frames"/>
    /// </example>
    public void WriteFrame(Raster<TPixel> frame, FrameMetadata? metadata = null)
    {
        Frame<TPixel> view = CheckFrame(frame, metadata);
        WriteFrameCore(view, frame);
    }

    /// <summary>
    /// Writes what follows the image data, such as PNG's IEND or a TGA footer, and the bytes still held. Without it the output is
    /// truncated.
    /// </summary>
    /// <exception cref="InvalidOperationException">Fewer rows than <see cref="Height"/>, or no frame, were written.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public void Complete()
    {
        CompleteCore();
        _output.Flush();
    }

    /// <summary>
    /// Writes the next rows as <see cref="WriteRows(Raster{TPixel}, int)"/> does, then writes the encoded bytes to the stream
    /// asynchronously. Encoding itself is synchronous, since it is CPU-bound.
    /// </summary>
    /// <param name="band">The rows, <see cref="Width"/> wide.</param>
    /// <param name="rows">How many of the band's rows to write, from its top.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="band"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="band"/> is not <see cref="Width"/> wide.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is negative, more than the band holds, or more than are left.</exception>
    /// <exception cref="InvalidOperationException">Frames were written, or the encoder is complete.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="encode-async"/>
    /// </example>
    public async ValueTask WriteRowsAsync(Raster<TPixel> band, int rows, CancellationToken cancellationToken = default)
    {
        CheckRows(band, rows);
        cancellationToken.ThrowIfCancellationRequested();
        _output.Hold = true;
        try
        {
            WriteRowsCore(band, rows);
        }
        finally
        {
            _output.Hold = false;
        }

        await _output.WriteOutAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the next frame as <see cref="WriteFrame"/> does, then writes the encoded bytes asynchronously.</summary>
    /// <param name="frame">The frame, <see cref="Width"/> × <see cref="Height"/>.</param>
    /// <param name="metadata">The frame's delay, or <see langword="null"/> for the frame's own metadata.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="frame"/> is not <see cref="Width"/> × <see cref="Height"/>.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="WriteFrame"/>.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask WriteFrameAsync(Raster<TPixel> frame, FrameMetadata? metadata = null, CancellationToken cancellationToken = default)
    {
        Frame<TPixel> view = CheckFrame(frame, metadata);
        cancellationToken.ThrowIfCancellationRequested();
        _output.Hold = true;
        try
        {
            WriteFrameCore(view, frame);
        }
        finally
        {
            _output.Hold = false;
        }

        await _output.WriteOutAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes the image as <see cref="Complete"/> does, writing the last bytes asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when every byte is written.</returns>
    /// <exception cref="InvalidOperationException">Fewer rows than <see cref="Height"/>, or no frame, were written.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.Hold = true;
        try
        {
            CompleteCore();
        }
        finally
        {
            _output.Hold = false;
        }

        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the encoder. Disposing before <see cref="Complete"/> leaves a truncated stream; a file the encoder created is
    /// deleted.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _encoder?.Dispose();
        foreach (Frame<TPixel> frame in _gathered)
        {
            frame.Plane.Dispose();
        }

        _gathered.Clear();
        if (_path is not null)
        {
            _output.Dispose();
            _target.Dispose();
            if (!_completed)
            {
                File.Delete(_path);
            }
        }
        else
        {
            _output.Dispose();
        }
    }

    private void CheckRows(Raster<TPixel> band, int rows)
    {
        ArgumentNullException.ThrowIfNull(band);
        CheckOpen();
        if (band.Width != Width)
        {
            throw new ArgumentException($"The band is {band.Width} wide; the image is {Width}.", nameof(band));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, band.Height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, Height - RowsWritten);
        if (_frames)
        {
            throw new InvalidOperationException("Frames were written; an image takes rows or frames, not both.");
        }
    }

    private Frame<TPixel> CheckFrame(Raster<TPixel> frame, FrameMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(frame);
        CheckOpen();
        if (frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException($"The frame is {frame.Width} × {frame.Height}; the image is {Width} × {Height}.", nameof(frame));
        }

        if (RowsWritten > 0)
        {
            throw new InvalidOperationException("Rows were written; an image takes rows or frames, not both.");
        }

        if (FramesWritten > 0 && _encoder is { Animates: false })
        {
            throw new InvalidOperationException($"{Format.Name} holds one image, and a frame was already written.");
        }

        Frame<TPixel> first = frame.Frames[0];
        return new Frame<TPixel>(first.Plane, metadata ?? first.Metadata);
    }

    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The encoder is complete.");
        }
    }

    private void WriteRowsCore(Raster<TPixel> band, int rows)
    {
        if (_encoder is not null)
        {
            for (int y = 0; y < rows; y++)
            {
                _encoder.WriteRow(band.Row(y));
            }
        }
        else
        {
            if (_gathered.Count == 0)
            {
                _gathered.Add(new Frame<TPixel>(PixelPlane.Allocate<TPixel>(Width, Height, _context.Memory), new FrameMetadata()));
            }

            for (int y = 0; y < rows; y++)
            {
                band.Row(y).CopyTo(_gathered[0].Row(RowsWritten + y));
            }
        }

        RowsWritten += rows;
    }

    private void WriteFrameCore(Frame<TPixel> view, Raster<TPixel> frame)
    {
        _frames = true;
        if (_encoder is { Animates: true })
        {
            _encoder.WriteFrame(view);
        }
        else if (_encoder is not null)
        {
            for (int y = 0; y < Height; y++)
            {
                _encoder.WriteRow(frame.Row(y));
            }
        }
        else
        {
            PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(Width, Height, _context.Memory);
            for (int y = 0; y < Height; y++)
            {
                view.Row(y).CopyTo(plane.Row(y));
            }

            _gathered.Add(new Frame<TPixel>(plane, view.Metadata.Copy()));
        }

        FramesWritten++;
    }

    private void CompleteCore()
    {
        CheckOpen();
        if (FramesWritten == 0 && RowsWritten < Height)
        {
            throw new InvalidOperationException($"{RowsWritten} of {Height} rows were written.");
        }

        if (_encoder is not null)
        {
            _encoder.Complete();
        }
        else
        {
            using Raster<TPixel> whole = new([.. _gathered], _metadata, _context);
            _gathered.Clear();
            _fallback!.Write(whole, _output, _options);
        }

        _completed = true;
    }
}
