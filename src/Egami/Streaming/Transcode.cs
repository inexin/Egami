using Egami.Formats;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami;

public abstract partial class Raster
{
    /// <summary>
    /// Decodes a file, runs a short chain of operations and encodes the result, a band of rows at a time, so a huge image never
    /// has to exist whole: a thumbnail of a 20,000 × 20,000 PNG takes a few MB. When the chain starts with Fit, Cover or Resize
    /// the decoder already shrinks by a whole factor (<see cref="ReaderOptions.MinimumSize"/>), and a leading Crop becomes
    /// <see cref="ReaderOptions.Region"/>, so the result equals loading with those options and running the rest of the chain.
    /// </summary>
    /// <remarks>
    /// The chain may hold Crop, Resize, Fit and Cover with any filter, FlipHorizontal (and AutoOrient of a mirrored image), and the
    /// per-pixel color adjustments (Brightness, Contrast, Saturation, HueRotate, Sepia, Grayscale, Invert, Opacity, ColorMatrix).
    /// Anything that needs rows out of order or the whole image (Blur, Rotate, FlipVertical, DrawImage and the rest) throws
    /// <see cref="NotSupportedException"/> before any output is written. An animation keeps its frames when the output format
    /// animates; otherwise its first frame is written. Metadata that a format stores after the image data (PNG text after IDAT)
    /// is not carried over, since the output's metadata goes before its image data.
    /// </remarks>
    /// <param name="inputPath">The image to read.</param>
    /// <param name="outputPath">The file to write; the format comes from its extension, as in <see cref="Save(string, WriterOptions?)"/>.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode"/>
    /// </example>
    public static void Transcode(string inputPath, string outputPath, Action<RasterOps>? operations = null, ReaderOptions? readerOptions = null, WriterOptions? writerOptions = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        using FileStream input = OpenRead(inputPath);
        Transcoder.Run(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new PathTarget(outputPath, writerOptions), CancellationToken.None);
    }

    /// <summary>
    /// Transcodes from one stream to another, a band of rows at a time. See <see cref="Transcode(string, string, Action{RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?)"/>.
    /// </summary>
    /// <param name="input">The image to read, from its current position. It is left open.</param>
    /// <param name="output">Where to write, from its current position. It is left open.</param>
    /// <param name="outputFormat">The output format.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/>, <paramref name="output"/> or <paramref name="outputFormat"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode"/>
    /// </example>
    public static void Transcode(Stream input, Stream output, FormatInfo outputFormat, Action<RasterOps>? operations = null, ReaderOptions? readerOptions = null, WriterOptions? writerOptions = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputFormat);
        Transcoder.Run(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new StreamTarget(output, outputFormat, writerOptions), CancellationToken.None);
    }

    /// <summary>
    /// Transcodes a file as <see cref="Transcode(string, string, Action{RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?)"/>
    /// does, reading and writing asynchronously. Decoding, the chain and encoding are synchronous, since they are CPU-bound.
    /// </summary>
    /// <param name="inputPath">The image to read.</param>
    /// <param name="outputPath">The file to write; the format comes from its extension.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading, writing and, between bands, the work.</param>
    /// <returns>A task that completes when the output is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode-async"/>
    /// </example>
    public static async Task TranscodeAsync(string inputPath, string outputPath, Action<RasterOps>? operations = null, ReaderOptions? readerOptions = null, WriterOptions? writerOptions = null, EgamiContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        FileStream input = OpenRead(inputPath, async: true);
        await using (input.ConfigureAwait(false))
        {
            await Transcoder.RunAsync(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new PathTarget(outputPath, writerOptions), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Transcodes from one stream to another asynchronously. See <see cref="TranscodeAsync(string, string, Action{RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?, CancellationToken)"/>.</summary>
    /// <param name="input">The image to read, from its current position. It is left open.</param>
    /// <param name="output">Where to write, from its current position. It is left open.</param>
    /// <param name="outputFormat">The output format.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading, writing and, between bands, the work.</param>
    /// <returns>A task that completes when the output is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/>, <paramref name="output"/> or <paramref name="outputFormat"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode-async"/>
    /// </example>
    public static Task TranscodeAsync(Stream input, Stream output, FormatInfo outputFormat, Action<RasterOps>? operations = null, ReaderOptions? readerOptions = null, WriterOptions? writerOptions = null, EgamiContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputFormat);
        return Transcoder.RunAsync(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new StreamTarget(output, outputFormat, writerOptions), cancellationToken);
    }

    /// <summary>
    /// Transcodes a file as <see cref="Transcode(string, string, Action{RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?)"/>
    /// does, reporting how far the output has come. If the transcode fails or is canceled, the partly written file is deleted.
    /// </summary>
    /// <param name="inputPath">The image to read.</param>
    /// <param name="outputPath">The file to write; the format comes from its extension.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="progress">Gets <see cref="SaveProgress"/> reports as the output is written, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the work between bands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode-progress"/>
    /// </example>
    public static void Transcode(string inputPath, string outputPath, Action<RasterOps>? operations, ReaderOptions? readerOptions, WriterOptions? writerOptions, EgamiContext? context, IProgress<SaveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        SaveReporter? reporter = SaveReporter.For(progress, cancellationToken);
        using FileStream input = OpenRead(inputPath);
        using (reporter?.Enter(null) ?? default)
        {
            Transcoder.Run(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new PathTarget(outputPath, writerOptions), cancellationToken);
        }

        reporter?.Finish(outputPath);
    }

    /// <summary>
    /// Transcodes from one stream to another, a band of rows at a time, reporting how far the output has come. See
    /// <see cref="Transcode(string, string, Action{RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?, IProgress{SaveProgress}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="input">The image to read, from its current position. It is left open.</param>
    /// <param name="output">Where to write, from its current position. It is left open.</param>
    /// <param name="outputFormat">The output format.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="progress">Gets <see cref="SaveProgress"/> reports as the output is written, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the work between bands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/>, <paramref name="output"/> or <paramref name="outputFormat"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode-progress"/>
    /// </example>
    public static void Transcode(Stream input, Stream output, FormatInfo outputFormat, Action<RasterOps>? operations, ReaderOptions? readerOptions, WriterOptions? writerOptions, EgamiContext? context, IProgress<SaveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputFormat);
        SaveReporter? reporter = SaveReporter.For(progress, cancellationToken);
        using (reporter?.Enter(output) ?? default)
        {
            Transcoder.Run(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new StreamTarget(output, outputFormat, writerOptions), cancellationToken);
        }

        reporter?.Finish();
    }

    /// <summary>
    /// Transcodes a file asynchronously, reporting how far the output has come. See
    /// <see cref="TranscodeAsync(string, string, Action{RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?, CancellationToken)"/>.
    /// </summary>
    /// <param name="inputPath">The image to read.</param>
    /// <param name="outputPath">The file to write; the format comes from its extension.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="progress">Gets <see cref="SaveProgress"/> reports as the output is written, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels reading, writing and, between bands, the work.</param>
    /// <returns>A task that completes when the output is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode-progress"/>
    /// </example>
    public static async Task TranscodeAsync(string inputPath, string outputPath, Action<RasterOps>? operations, ReaderOptions? readerOptions, WriterOptions? writerOptions, EgamiContext? context, IProgress<SaveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        SaveReporter? reporter = SaveReporter.For(progress, cancellationToken);
        FileStream input = OpenRead(inputPath, async: true);
        await using (input.ConfigureAwait(false))
        {
            using (reporter?.Enter(null) ?? default)
            {
                await Transcoder.RunAsync(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new PathTarget(outputPath, writerOptions), cancellationToken).ConfigureAwait(false);
            }
        }

        reporter?.Finish(outputPath);
    }

    /// <summary>
    /// Transcodes from one stream to another asynchronously, reporting how far the output has come. See
    /// <see cref="TranscodeAsync(string, string, Action{RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?, CancellationToken)"/>.
    /// </summary>
    /// <param name="input">The image to read, from its current position. It is left open.</param>
    /// <param name="output">Where to write, from its current position. It is left open.</param>
    /// <param name="outputFormat">The output format.</param>
    /// <param name="operations">The chain, or <see langword="null"/> to only convert the format.</param>
    /// <param name="readerOptions">How to read, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">How to write, or <see langword="null"/> for the format's defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="progress">Gets <see cref="SaveProgress"/> reports as the output is written, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels reading, writing and, between bands, the work.</param>
    /// <returns>A task that completes when the output is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/>, <paramref name="output"/> or <paramref name="outputFormat"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The chain holds an operation that cannot stream, or a writer option that needs every pixel first.</exception>
    /// <exception cref="UnsupportedFormatException">The input's format is not recognized, or the output's has no writer.</exception>
    /// <exception cref="CorruptDataException">The input breaks its format's rules.</exception>
    /// <exception cref="LimitExceededException">The input exceeds the context's safety limits.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="transcode-progress"/>
    /// </example>
    public static async Task TranscodeAsync(Stream input, Stream output, FormatInfo outputFormat, Action<RasterOps>? operations, ReaderOptions? readerOptions, WriterOptions? writerOptions, EgamiContext? context, IProgress<SaveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputFormat);
        SaveReporter? reporter = SaveReporter.For(progress, cancellationToken);
        using (reporter?.Enter(output) ?? default)
        {
            await Transcoder.RunAsync(input, context ?? EgamiContext.Default, readerOptions ?? ReaderOptions.Default, operations, new StreamTarget(output, outputFormat, writerOptions), cancellationToken).ConfigureAwait(false);
        }

        reporter?.Finish();
    }
}
