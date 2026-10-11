using Egami.Formats;
using Egami.Pixels;

namespace Egami;

/// <content>Loading and probing.</content>
public abstract partial class YuvRaster
{
    /// <summary>
    /// Decodes a format's own planes, with no color conversion or chroma upsampling: for JPEG, a YCbCr file's planes at its own
    /// sampling (4:2:0, 4:2:2 or 4:4:4; other samplings are replicated up to 4:4:4) and a gray file's as 4:0:0, all
    /// <see cref="YuvColor.Jpeg"/>. <see cref="ReaderOptions.MinimumSize"/> decodes at a reduced scale (1/2, 1/4 or 1/8).
    /// </summary>
    /// <param name="stream">The encoded image. It is read from its current position and left open.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="NotSupportedException">
    /// The format, or this file (a CMYK, YCCK or RGB JPEG), has no YUV planes; <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/>
    /// converts it. Also thrown for <see cref="ReaderOptions.Region"/> and <see cref="ReaderOptions.ConvertColorTo"/>.
    /// </exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
    public static YuvRaster Load(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        options ??= ReaderOptions.Default;
        if (options.Region is not null || options.ConvertColorTo is not null)
        {
            throw new NotSupportedException("Loading planes does not crop or convert color; load a Raster for that.");
        }

        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, (format, input) => Reader(format).ReadYuv(input, options), CancellationToken.None);
    }

    /// <summary>Loads an image file's planes. See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="NotSupportedException">The format or file has no YUV planes.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static YuvRaster Load(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using FileStream stream = Raster.OpenRead(path);
        return Load(stream, context, options);
    }

    /// <summary>Loads an image's planes from bytes in memory. See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="NotSupportedException">The format or file has no YUV planes.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    public static YuvRaster Load(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using MemoryStream stream = Raster.AsStream(data);
        return Load(stream, context, options);
    }

    /// <summary>
    /// Reads only the header, as <see cref="Raster.Probe(Stream, EgamiContext?, ReaderOptions?)"/> does, and says whether
    /// <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/> would give planes and in what chroma format.
    /// </summary>
    /// <param name="stream">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
    public static YuvRasterInfo Probe(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        options ??= ReaderOptions.Default;
        context ??= EgamiContext.Default;
        return FormatPipeline.Run(stream, context, (format, input) =>
        {
            if (format.Reader is IYuvFormatReader yuv)
            {
                (RasterInfo info, ChromaFormat? chroma, int depth, YuvColor color) = yuv.ProbeYuv(input, options);
                return new YuvRasterInfo(info, chroma, chroma is null ? 0 : depth, chroma is null ? null : color);
            }

            return new YuvRasterInfo(format.Reader!.ReadInfo(input, options), null, 0, null);
        }, CancellationToken.None);
    }

    /// <summary>Reads only the header of an image file. See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static YuvRasterInfo Probe(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using FileStream stream = Raster.OpenRead(path);
        return Probe(stream, context, options);
    }

    /// <summary>Reads only the header of an image in memory. See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    public static YuvRasterInfo Probe(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using MemoryStream stream = Raster.AsStream(data);
        return Probe(stream, context, options);
    }

    private static IYuvFormatReader Reader(FormatInfo format)
    {
        return format.Reader as IYuvFormatReader
            ?? throw new NotSupportedException($"{format.Name} files hold no YUV planes; load them with Raster.Load and convert with YuvRaster.FromRaster.");
    }
}

/// <summary>
/// What <see cref="YuvRaster.Probe(Stream, EgamiContext?, ReaderOptions?)"/> reads from a header: the image as
/// <see cref="Raster.Probe(Stream, EgamiContext?, ReaderOptions?)"/> describes it, and the planes
/// <see cref="YuvRaster.Load(Stream, EgamiContext?, ReaderOptions?)"/> would give, if any.
/// </summary>
/// <param name="Info">The image, as <see cref="Raster.Probe(Stream, EgamiContext?, ReaderOptions?)"/> describes it.</param>
/// <param name="ChromaFormat">The planes' chroma format, or <see langword="null"/> when loading planes is not supported.</param>
/// <param name="BitDepth">The planes' bits per sample, or 0.</param>
/// <param name="Color">The planes' color description, or <see langword="null"/>.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
/// </example>
public sealed record YuvRasterInfo(RasterInfo Info, ChromaFormat? ChromaFormat, int BitDepth, YuvColor? Color)
{
    /// <summary>Gets a value indicating whether <see cref="YuvRaster.Load(Stream, EgamiContext?, ReaderOptions?)"/> gives planes.</summary>
    public bool CanLoad => ChromaFormat is not null;
}
