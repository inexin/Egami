using Egami.Pixels;

namespace Egami.Formats;

/// <summary>
/// Reads one file format. Readers are stateless and safe to share across threads.
/// They read only through <see cref="EgamiInput"/> and allocate pixel memory only through <see cref="EgamiInput.CreateRaster"/>,
/// which checks the safety limits first, so no reader can allocate before the size has been checked.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
/// </example>
public interface IFormatReader
{
    /// <summary>Reads only the header: size, natural pixel layout and whatever else is cheap. <see cref="Raster.Probe(Stream, EgamiContext?, ReaderOptions?)"/> stops here.</summary>
    /// <param name="input">The data, positioned at the start of the image.</param>
    /// <param name="options">How to read.</param>
    /// <returns>What the header says.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
    /// </example>
    RasterInfo ReadInfo(EgamiInput input, ReaderOptions options);

    /// <summary>Reads the image, converting to <typeparamref name="TPixel"/> row by row while decoding.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="input">The data, positioned at the start of the image.</param>
    /// <param name="options">How to read.</param>
    /// <returns>The raster.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
    /// </example>
    Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Reads the image in the pixel type that best fits the file, such as <see cref="Gray16"/> for a 16-bit gray image.</summary>
    /// <param name="input">The data, positioned at the start of the image.</param>
    /// <param name="options">How to read.</param>
    /// <returns>The raster.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
    /// </example>
    Raster Read(EgamiInput input, ReaderOptions options);
}

/// <summary>Writes one file format. Writers are stateless, safe to share across threads, and never change the raster they are given.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
/// </example>
public interface IFormatWriter
{
    /// <summary>Encodes <paramref name="raster"/> to <paramref name="output"/>, converting rows to the format's layout on the fly.</summary>
    /// <typeparam name="TPixel">The raster's pixel type.</typeparam>
    /// <param name="raster">The image to write.</param>
    /// <param name="output">Where to write. The writer leaves it open.</param>
    /// <param name="options">How to write. Writers accept the base <see cref="WriterOptions"/> and use their defaults for anything it does not set.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
    /// </example>
    void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>
/// A writer that can convert a file of another format to its own without decoding it to pixels first, such as camera RAW data
/// copied into a DNG unchanged. <see cref="Raster.Transcode(string, string, Action{Operations.RasterOps}?, ReaderOptions?, WriterOptions?, EgamiContext?)"/>
/// asks the output format's writer when there are no operations and the reader options ask for nothing beyond the format's
/// defaults; a writer that declines is used as usual.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="format-converter"/>
/// </example>
public interface IFormatConverter
{
    /// <summary>
    /// Converts <paramref name="input"/>, a file of <paramref name="inputFormat"/>, to this writer's format, or declines, by the
    /// format alone, before reading or writing anything.
    /// </summary>
    /// <param name="input">The file to convert, from its current position. It is left open.</param>
    /// <param name="inputFormat">The format <paramref name="input"/> was detected as.</param>
    /// <param name="output">Where to write. The writer leaves it open.</param>
    /// <param name="options">How to write.</param>
    /// <param name="context">The context of the call: its formats, limits and diagnostics.</param>
    /// <returns><see langword="true"/> when the file was converted; <see langword="false"/>, with nothing read or written, to decode it as usual.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="format-converter"/>
    /// </example>
    bool TryConvert(Stream input, FormatInfo inputFormat, Stream output, WriterOptions options, EgamiContext context);
}
