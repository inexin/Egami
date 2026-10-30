using Egami.Formats;
using Egami.Pixels;

namespace Egami;

/// <content>Files with several images of their own sizes: TIFF pages, icon entries.</content>
public abstract partial class Raster
{
    /// <summary>
    /// Loads every image of a file, each in the pixel type that best fits it: every full-resolution page of a TIFF, the one image of
    /// any other format. Each image is loaded as <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/> would load it, so the
    /// options apply to each.
    /// </summary>
    /// <param name="stream">The encoded file. It is read from its current position and left open; a stream that cannot seek is read whole first.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list, which disposes them.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image breaks a safety limit, there are more than <see cref="SafetyLimits.MaxFrames"/>, or together they pass <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static RasterList LoadAll(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        EgamiContext ctx = context ?? EgamiContext.Default;
        return FormatPipeline.LoadAll(stream, ctx, options ?? ReaderOptions.Default, (s, o) => Load(s, ctx, o), CancellationToken.None);
    }

    /// <summary>Loads every image of a file. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static RasterList LoadAll(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using FileStream stream = OpenRead(path);
        return LoadAll(stream, context, options);
    }

    /// <summary>Loads every image of a file in memory. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static RasterList LoadAll(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using MemoryStream stream = AsStream(data);
        return LoadAll(stream, context, options);
    }

    /// <summary>Loads every image of a file as <typeparamref name="TPixel"/>. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce; each image in the list is a <see cref="Raster{TPixel}"/>.</typeparam>
    /// <param name="stream">The encoded file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static RasterList LoadAll<TPixel>(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        EgamiContext ctx = context ?? EgamiContext.Default;
        return FormatPipeline.LoadAll(stream, ctx, options ?? ReaderOptions.Default, (s, o) => Load<TPixel>(s, ctx, o), CancellationToken.None);
    }

    /// <summary>Loads every image of a file as <typeparamref name="TPixel"/>. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static RasterList LoadAll<TPixel>(string path, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using FileStream stream = OpenRead(path);
        return LoadAll<TPixel>(stream, context, options);
    }

    /// <summary>Loads every image of a file in memory as <typeparamref name="TPixel"/>. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="data">The encoded file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static RasterList LoadAll<TPixel>(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = AsStream(data);
        return LoadAll<TPixel>(stream, context, options);
    }

    /// <summary>
    /// Reads the file asynchronously, then loads every image. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>; the
    /// bytes held count against <see cref="SafetyLimits.MaxTotalBytes"/>.
    /// </summary>
    /// <param name="stream">The encoded file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and, between images, decoding.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static Task<RasterList> LoadAllAsync(Stream stream, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EgamiContext ctx = context ?? EgamiContext.Default;
        ReaderOptions read = options ?? ReaderOptions.Default;
        return FormatPipeline.RunBufferedAsync(stream, ctx, s => FormatPipeline.LoadAll(s, ctx, read, (m, o) => Load(m, ctx, o), cancellationToken), cancellationToken);
    }

    /// <summary>Reads an image file asynchronously, then loads every image. See <see cref="LoadAllAsync(Stream, EgamiContext?, ReaderOptions?, CancellationToken)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and decoding.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static async Task<RasterList> LoadAllAsync(string path, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        FileStream stream = OpenRead(path, async: true);
        await using (stream.ConfigureAwait(false))
        {
            return await LoadAllAsync(stream, context, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the file asynchronously, then loads every image as <typeparamref name="TPixel"/>. See <see cref="LoadAllAsync(Stream, EgamiContext?, ReaderOptions?, CancellationToken)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="stream">The encoded file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and decoding.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static Task<RasterList> LoadAllAsync<TPixel>(Stream stream, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(stream);
        EgamiContext ctx = context ?? EgamiContext.Default;
        ReaderOptions read = options ?? ReaderOptions.Default;
        return FormatPipeline.RunBufferedAsync(stream, ctx, s => FormatPipeline.LoadAll(s, ctx, read, (m, o) => Load<TPixel>(m, ctx, o), cancellationToken), cancellationToken);
    }

    /// <summary>Reads an image file asynchronously, then loads every image as <typeparamref name="TPixel"/>. See <see cref="LoadAllAsync(Stream, EgamiContext?, ReaderOptions?, CancellationToken)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and decoding.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public static async Task<RasterList> LoadAllAsync<TPixel>(string path, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        FileStream stream = OpenRead(path, async: true);
        await using (stream.ConfigureAwait(false))
        {
            return await LoadAllAsync<TPixel>(stream, context, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes several rasters to one file in <paramref name="format"/>, in order: each a page of a TIFF. Every format takes one
    /// raster, written as <see cref="Save(Stream, FormatInfo, WriterOptions?)"/> writes it. The rasters are not changed.
    /// </summary>
    /// <param name="rasters">The images to write, at least one.</param>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="format">The format.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="ArgumentException">No raster was given, or more than one to a format that holds one image.</exception>
    /// <exception cref="UnsupportedFormatException">The format has no writer.</exception>
    /// <exception cref="ObjectDisposedException">A raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rasters"/>, <paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save-all"/>
    /// </example>
    public static void SaveAll(IEnumerable<Raster> rasters, Stream stream, FormatInfo format, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        FormatPipeline.SaveAll([.. rasters], stream, format, options ?? WriterOptions.Default);
    }

    /// <summary>Writes several rasters to one file in the registered format with this name or file extension. See <see cref="SaveAll(IEnumerable{Raster}, Stream, FormatInfo, WriterOptions?)"/>.</summary>
    /// <param name="rasters">The images to write, at least one.</param>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="formatNameOrExtension">The format's name or one of its extensions, ignoring case.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="ArgumentException">No raster was given, or more than one to a format that holds one image.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">A raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save-all"/>
    /// </example>
    public static void SaveAll(IEnumerable<Raster> rasters, Stream stream, string formatNameOrExtension, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        Raster[] list = [.. rasters];
        EgamiContext context = list.Length > 0 ? list[0].Context : EgamiContext.Default;
        SaveAll(list, stream, FormatPipeline.FormatForName(context, formatNameOrExtension), options);
    }

    /// <summary>
    /// Writes several rasters to one file, choosing the format from the file extension. An existing file is replaced; if writing
    /// fails, the partly written file is deleted. See <see cref="SaveAll(IEnumerable{Raster}, Stream, FormatInfo, WriterOptions?)"/>.
    /// </summary>
    /// <param name="rasters">The images to write, at least one.</param>
    /// <param name="path">The file path.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="ArgumentException">No raster was given, or more than one to a format that holds one image.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format uses the extension, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">A raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rasters"/> or <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save-all"/>
    /// </example>
    public static void SaveAll(IEnumerable<Raster> rasters, string path, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        Raster[] list = [.. rasters];
        EgamiContext context = list.Length > 0 ? list[0].Context : EgamiContext.Default;
        FormatInfo format = FormatPipeline.FormatForPath(context, path);
        options = FormatPipeline.OptionsForPath(format, path, options);
        WriteFile(path, stream => FormatPipeline.SaveAll(list, stream, format, options ?? WriterOptions.Default));
    }

    /// <summary>
    /// Encodes several rasters into a pooled buffer, then writes them to <paramref name="stream"/> asynchronously. See
    /// <see cref="SaveAll(IEnumerable{Raster}, Stream, FormatInfo, WriterOptions?)"/>.
    /// </summary>
    /// <param name="rasters">The images to write, at least one.</param>
    /// <param name="stream">Where to write. It is left open.</param>
    /// <param name="format">The format.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="cancellationToken">Cancels before encoding and while writing.</param>
    /// <returns>A task that completes when the data is written.</returns>
    /// <exception cref="ArgumentException">No raster was given, or more than one to a format that holds one image.</exception>
    /// <exception cref="UnsupportedFormatException">The format has no writer.</exception>
    /// <exception cref="ObjectDisposedException">A raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-save-all"/>
    /// </example>
    public static async Task SaveAllAsync(IEnumerable<Raster> rasters, Stream stream, FormatInfo format, WriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        cancellationToken.ThrowIfCancellationRequested();
        using PooledBufferStream buffer = new();
        FormatPipeline.SaveAll([.. rasters], buffer, format, options ?? WriterOptions.Default);
        await stream.WriteAsync(buffer.Written, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes several rasters to one file as <see cref="SaveAll(IEnumerable{Raster}, Stream, FormatInfo, WriterOptions?)"/> does,
    /// reporting progress.
    /// </summary>
    /// <param name="rasters">The images to write, at least one.</param>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="format">The format.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="progress">Gets <see cref="SaveProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the encode between bands of rows, and inside the codecs that count their own work.</param>
    /// <exception cref="ArgumentException">No raster was given, or more than one to a format that holds one image.</exception>
    /// <exception cref="UnsupportedFormatException">The format has no writer.</exception>
    /// <exception cref="ObjectDisposedException">A raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rasters"/>, <paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="save-progress"/>
    /// </example>
    public static void SaveAll(IEnumerable<Raster> rasters, Stream stream, FormatInfo format, WriterOptions? options, IProgress<SaveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        SaveReporter? reporter = SaveReporter.For(progress, cancellationToken);
        using (reporter?.Enter(stream) ?? default)
        {
            FormatPipeline.SaveAll([.. rasters], stream, format, options ?? WriterOptions.Default);
        }

        reporter?.Finish();
    }

    /// <summary>Writes several rasters to one file in the named format, reporting progress. See <see cref="SaveAll(IEnumerable{Raster}, Stream, FormatInfo, WriterOptions?, IProgress{SaveProgress}?, CancellationToken)"/>.</summary>
    /// <param name="rasters">The images to write, at least one.</param>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="formatNameOrExtension">The format's name or one of its extensions, ignoring case.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="progress">Gets <see cref="SaveProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the encode between bands of rows, and inside the codecs that count their own work.</param>
    /// <exception cref="ArgumentException">No raster was given, or more than one to a format that holds one image.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">A raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="save-progress"/>
    /// </example>
    public static void SaveAll(IEnumerable<Raster> rasters, Stream stream, string formatNameOrExtension, WriterOptions? options, IProgress<SaveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        Raster[] list = [.. rasters];
        EgamiContext context = list.Length > 0 ? list[0].Context : EgamiContext.Default;
        SaveAll(list, stream, FormatPipeline.FormatForName(context, formatNameOrExtension), options, progress, cancellationToken);
    }

    /// <summary>
    /// Writes several rasters to one file, choosing the format from the file extension, reporting progress. If writing fails or is
    /// canceled, the partly written file is deleted.
    /// </summary>
    /// <param name="rasters">The images to write, at least one.</param>
    /// <param name="path">The file path.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <param name="progress">Gets <see cref="SaveProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the encode between bands of rows, and inside the codecs that count their own work.</param>
    /// <exception cref="ArgumentException">No raster was given, or more than one to a format that holds one image.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format uses the extension, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">A raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rasters"/> or <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="save-progress"/>
    /// </example>
    public static void SaveAll(IEnumerable<Raster> rasters, string path, WriterOptions? options, IProgress<SaveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        Raster[] list = [.. rasters];
        EgamiContext context = list.Length > 0 ? list[0].Context : EgamiContext.Default;
        FormatInfo format = FormatPipeline.FormatForPath(context, path);
        options = FormatPipeline.OptionsForPath(format, path, options);
        WriteFile(path, stream => SaveAll(list, stream, format, options, progress, cancellationToken));
    }
}
