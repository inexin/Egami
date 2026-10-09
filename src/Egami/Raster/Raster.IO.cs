using Egami.Formats;
using Egami.Pixels;

namespace Egami;

/// <content>Loading, probing and saving.</content>
public abstract partial class Raster
{
    /// <summary>
    /// Detects the format from the first bytes and decodes the image in the pixel type that best fits it,
    /// such as <see cref="Rgb8"/> for a color image without alpha or <see cref="Gray16"/> for a 16-bit gray one.
    /// </summary>
    /// <param name="stream">The encoded image. It is read from its current position and left open; a seekable stream ends just past the image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="load-edit-save"/>
    /// </example>
    public static Raster Load(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return FormatPipeline.Load(stream, context ?? EgamiContext.Default, options ?? ReaderOptions.Default, CancellationToken.None);
    }

    /// <summary>Loads an image file. See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="load-edit-save"/>
    /// </example>
    public static Raster Load(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using FileStream stream = OpenRead(path);
        return Load(stream, context, options);
    }

    /// <summary>Loads an image from bytes in memory. See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="load-edit-save"/>
    /// </example>
    public static Raster Load(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using MemoryStream stream = AsStream(data);
        return Load(stream, context, options);
    }

    /// <summary>
    /// Detects the format and decodes the image straight into <typeparamref name="TPixel"/>, converting row by row while decoding.
    /// See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="stream">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="pixel-types"/>
    /// </example>
    public static Raster<TPixel> Load<TPixel>(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return FormatPipeline.Load<TPixel>(stream, context ?? EgamiContext.Default, options ?? ReaderOptions.Default, CancellationToken.None);
    }

    /// <summary>Loads an image file as <typeparamref name="TPixel"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="pixel-types"/>
    /// </example>
    public static Raster<TPixel> Load<TPixel>(string path, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using FileStream stream = OpenRead(path);
        return Load<TPixel>(stream, context, options);
    }

    /// <summary>Loads an image from bytes in memory as <typeparamref name="TPixel"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="data">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="pixel-types"/>
    /// </example>
    public static Raster<TPixel> Load<TPixel>(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = AsStream(data);
        return Load<TPixel>(stream, context, options);
    }

    /// <summary>
    /// Reads the encoded image asynchronously, then decodes it. The whole stream is read into a pooled buffer first, which counts against
    /// <see cref="SafetyLimits.MaxTotalBytes"/>; decoding itself is synchronous, since it is CPU-bound.
    /// </summary>
    /// <param name="stream">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and, between rows, decoding.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="async"/>
    /// </example>
    public static Task<Raster> LoadAsync(Stream stream, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        context ??= EgamiContext.Default;
        return FormatPipeline.RunBufferedAsync(
            stream, context, s => FormatPipeline.Load(s, context, options ?? ReaderOptions.Default, cancellationToken), cancellationToken);
    }

    /// <summary>Loads an image file asynchronously. See <see cref="LoadAsync(Stream, EgamiContext?, ReaderOptions?, CancellationToken)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and decoding.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="async"/>
    /// </example>
    public static async Task<Raster> LoadAsync(string path, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        FileStream stream = OpenRead(path, async: true);
        await using (stream.ConfigureAwait(false))
        {
            return await LoadAsync(stream, context, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the encoded image asynchronously, then decodes it as <typeparamref name="TPixel"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="stream">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and decoding.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="async"/>
    /// </example>
    public static Task<Raster<TPixel>> LoadAsync<TPixel>(Stream stream, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(stream);
        context ??= EgamiContext.Default;
        return FormatPipeline.RunBufferedAsync(
            stream, context, s => FormatPipeline.Load<TPixel>(s, context, options ?? ReaderOptions.Default, cancellationToken), cancellationToken);
    }

    /// <summary>Loads an image file asynchronously as <typeparamref name="TPixel"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and decoding.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="async"/>
    /// </example>
    public static async Task<Raster<TPixel>> LoadAsync<TPixel>(string path, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        FileStream stream = OpenRead(path, async: true);
        await using (stream.ConfigureAwait(false))
        {
            return await LoadAsync<TPixel>(stream, context, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads only the header: format, size, natural pixel layout, frame count when cheap, metadata presence and the estimated memory
    /// a full load would allocate. No pixel memory is allocated.
    /// </summary>
    /// <param name="stream">The encoded image. A seekable stream ends just past the bytes read.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">
    /// Reader options, or <see langword="null"/> for the defaults. <see cref="ReaderOptions.MinimumSize"/> lowers the estimate for
    /// formats that can decode at a smaller scale, and <see cref="ReaderOptions.Recovery"/> applies to the header.
    /// </param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit, such as the metadata size.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Cookbook.cs" region="read-size"/>
    /// </example>
    public static RasterInfo Probe(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return FormatPipeline.Probe(stream, context ?? EgamiContext.Default, options ?? ReaderOptions.Default, CancellationToken.None);
    }

    /// <summary>Reads only the header of an image file. See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">
    /// Reader options, or <see langword="null"/> for the defaults. <see cref="ReaderOptions.MinimumSize"/> lowers the estimate for
    /// formats that can decode at a smaller scale, and <see cref="ReaderOptions.Recovery"/> applies to the header.
    /// </param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit, such as the metadata size.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Cookbook.cs" region="read-size"/>
    /// </example>
    public static RasterInfo Probe(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using FileStream stream = OpenRead(path);
        return Probe(stream, context, options);
    }

    /// <summary>Reads only the header of an image in memory. See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">
    /// Reader options, or <see langword="null"/> for the defaults. <see cref="ReaderOptions.MinimumSize"/> lowers the estimate for
    /// formats that can decode at a smaller scale, and <see cref="ReaderOptions.Recovery"/> applies to the header.
    /// </param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit, such as the metadata size.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Cookbook.cs" region="read-size"/>
    /// </example>
    public static RasterInfo Probe(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using MemoryStream stream = AsStream(data);
        return Probe(stream, context, options);
    }

    /// <summary>
    /// Reads the header asynchronously, reading only as much of the stream as the header needs (at least 64 KB, or all of it if
    /// shorter). See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.
    /// </summary>
    /// <param name="stream">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">
    /// Reader options, or <see langword="null"/> for the defaults. <see cref="ReaderOptions.MinimumSize"/> lowers the estimate for
    /// formats that can decode at a smaller scale, and <see cref="ReaderOptions.Recovery"/> applies to the header.
    /// </param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit, such as the metadata size.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="probe-async"/>
    /// </example>
    public static Task<RasterInfo> ProbeAsync(Stream stream, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        context ??= EgamiContext.Default;
        return FormatPipeline.ProbeAsync(stream, context, options ?? ReaderOptions.Default, cancellationToken);
    }

    /// <summary>Reads the header of an image file asynchronously. See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">
    /// Reader options, or <see langword="null"/> for the defaults. <see cref="ReaderOptions.MinimumSize"/> lowers the estimate for
    /// formats that can decode at a smaller scale, and <see cref="ReaderOptions.Recovery"/> applies to the header.
    /// </param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit, such as the metadata size.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="probe-async"/>
    /// </example>
    public static async Task<RasterInfo> ProbeAsync(string path, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        FileStream stream = OpenRead(path, async: true);
        await using (stream.ConfigureAwait(false))
        {
            return await ProbeAsync(stream, context, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Encodes the raster in <paramref name="format"/>. The raster is not changed.</summary>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="format">The format.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="UnsupportedFormatException">The format has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="png"/>
    /// </example>
    public void Save(Stream stream, FormatInfo format, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        FormatPipeline.Save(stream, format, options ?? WriterOptions.Default, WriteWith);
    }

    /// <summary>Encodes the raster in the registered format with this name or file extension, such as "PNG" or ".png".</summary>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="formatNameOrExtension">The format's name or one of its extensions, ignoring case.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="UnsupportedFormatException">No registered format matches, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="png"/>
    /// </example>
    public void Save(Stream stream, string formatNameOrExtension, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        Save(stream, FormatPipeline.FormatForName(Context, formatNameOrExtension), options);
    }

    /// <summary>
    /// Encodes the raster to a file, choosing the format from the file extension. An existing file is replaced.
    /// If encoding fails, the partly written file is deleted.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="UnsupportedFormatException">No registered format uses the extension, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="png"/>
    /// </example>
    public void Save(string path, WriterOptions? options = null)
    {
        FormatInfo format = FormatPipeline.FormatForPath(Context, path);
        WriteFile(path, stream => Save(stream, format, options));
    }

    /// <summary>
    /// Encodes the raster into a pooled buffer, then writes it to <paramref name="stream"/> asynchronously.
    /// See <see cref="Save(Stream, FormatInfo, WriterOptions?)"/>.
    /// </summary>
    /// <param name="stream">Where to write. It is left open.</param>
    /// <param name="format">The format.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="cancellationToken">Cancels before encoding and while writing.</param>
    /// <returns>A task that completes when the data is written.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="async"/>
    /// </example>
    public async Task SaveAsync(Stream stream, FormatInfo format, WriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        using PooledBufferStream buffer = new();
        Save(buffer, format, options);
        await stream.WriteAsync(buffer.Written, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes the raster in the named format and writes it asynchronously.</summary>
    /// <param name="stream">Where to write. It is left open.</param>
    /// <param name="formatNameOrExtension">The format's name or one of its extensions, ignoring case.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="cancellationToken">Cancels before encoding and while writing.</param>
    /// <returns>A task that completes when the data is written.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="async"/>
    /// </example>
    public Task SaveAsync(Stream stream, string formatNameOrExtension, WriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        return SaveAsync(stream, FormatPipeline.FormatForName(Context, formatNameOrExtension), options, cancellationToken);
    }

    /// <summary>Encodes the raster to a file asynchronously, choosing the format from the file extension.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="cancellationToken">Cancels before encoding and while writing.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="async"/>
    /// </example>
    public async Task SaveAsync(string path, WriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        FormatInfo format = FormatPipeline.FormatForPath(Context, path);
        FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await SaveAsync(stream, format, options, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static FileStream OpenRead(string path, bool async = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, async ? FileOptions.Asynchronous | FileOptions.SequentialScan : FileOptions.SequentialScan);
    }

    private static MemoryStream AsStream(ReadOnlyMemory<byte> data)
    {
        return System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);
    }

    private static void WriteFile(string path, Action<Stream> write)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
            write(stream);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
