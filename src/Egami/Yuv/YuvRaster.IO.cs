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

        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, options, (format, input) => Reader(format).ReadYuv(input, options), CancellationToken.None);
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
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
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
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
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
        return FormatPipeline.Run(stream, context, options, (format, input) =>
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
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
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
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
    public static YuvRasterInfo Probe(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using MemoryStream stream = Raster.AsStream(data);
        return Probe(stream, context, options);
    }

    /// <summary>
    /// Encodes the picture in <paramref name="format"/>. JPEG encodes the planes straight, with no color conversion or chroma
    /// downsampling, when they are 8-bit BT.601 at full range (4:2:0, 4:2:2 and 4:4:4 keep their sampling, 4:0:0 is gray); other
    /// planes are converted to that first. AVIF encodes any planes of 8, 10 or 12 bits straight, their matrix, range and chroma
    /// siting written into the file, and alpha as an alpha item. Other formats are saved from
    /// <see cref="ToRaster(YuvConvertOptions?)"/>.
    /// </summary>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="format">The format.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults. JPEG's subsampling comes from the planes.</param>
    /// <exception cref="UnsupportedFormatException">The format has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
    public void Save(Stream stream, FormatInfo format, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        ThrowIfDisposed();
        if (format.Writer is Formats.Jpeg.JpegWriter)
        {
            Formats.Jpeg.JpegWriterOptions jpeg = options as Formats.Jpeg.JpegWriterOptions ?? new Formats.Jpeg.JpegWriterOptions { StripMetadata = options?.StripMetadata ?? false };
            if (this is YuvRaster<byte> planes && Color.Matrix == YuvMatrix.Bt601 && Color.Range == YuvRange.Full)
            {
                Formats.Jpeg.JpegWriter.WritePlanes(planes, stream, jpeg);
                return;
            }

            using Raster rgb = BitDepth == 8 ? ToRaster<Rgb8>() : ToRaster<Rgba16>();
            using YuvRaster<byte> converted = FromRaster(rgb, ChromaFormat, YuvColor.Jpeg);
            Formats.Jpeg.JpegWriter.WritePlanes(converted, stream, jpeg);
            return;
        }

        if (format.Writer is Formats.Avif.AvifWriter && BitDepth is 8 or 10 or 12)
        {
            Formats.Avif.AvifWriterOptions avif = options as Formats.Avif.AvifWriterOptions ?? Formats.Avif.AvifWriterOptions.Default;
            if (this is YuvRaster<byte> bytes)
            {
                Formats.Avif.AvifWriter.WritePlanes(bytes, stream, avif, Context);
                return;
            }

            if (this is YuvRaster<ushort> words)
            {
                Formats.Avif.AvifWriter.WritePlanes(words, stream, avif, Context);
                return;
            }
        }

        using Raster raster = ToRaster();
        raster.Save(stream, format, options);
    }

    /// <summary>Encodes the picture in the registered format with this name or file extension. See <see cref="Save(Stream, FormatInfo, WriterOptions?)"/>.</summary>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="formatNameOrExtension">The format's name or one of its extensions, ignoring case.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="UnsupportedFormatException">No registered format matches, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
    public void Save(Stream stream, string formatNameOrExtension, WriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        Save(stream, FormatPipeline.FormatForName(Context, formatNameOrExtension), options);
    }

    /// <summary>
    /// Encodes the picture to a file, choosing the format from the extension. An existing file is replaced; if encoding fails,
    /// the partly written file is deleted. See <see cref="Save(Stream, FormatInfo, WriterOptions?)"/>.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">The format's options, or <see langword="null"/> for its defaults.</param>
    /// <exception cref="UnsupportedFormatException">No registered format uses the extension, or it has no writer.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="jpeg"/>
    /// </example>
    public void Save(string path, WriterOptions? options = null)
    {
        FormatInfo format = FormatPipeline.FormatForPath(Context, path);
        options = FormatPipeline.OptionsForPath(format, path, options);
        Raster.WriteFile(path, stream => Save(stream, format, options));
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
