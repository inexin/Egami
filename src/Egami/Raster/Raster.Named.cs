using Egami.Formats;
using Egami.Pixels;

namespace Egami;

/// <content>Loading with a named format, skipping detection.</content>
public abstract partial class Raster
{
    /// <summary>
    /// Decodes an image of a known format without detecting it, for data that has no signature of its own, such as a JBIG2 stream
    /// embedded in a PDF. Otherwise as <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.
    /// </summary>
    /// <param name="stream">The encoded image. It is read from its current position and left open; a seekable stream ends just past the image.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static Raster Load(Stream stream, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return Load(stream, context, WithFormat(format, options));
    }

    /// <summary>Decodes an image of a known format from bytes in memory. See <see cref="Load(Stream, FormatInfo, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static Raster Load(ReadOnlyMemory<byte> data, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return Load(data, context, WithFormat(format, options));
    }

    /// <summary>Decodes an image of a known format as <typeparamref name="TPixel"/>. See <see cref="Load(Stream, FormatInfo, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="stream">The encoded image.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static Raster<TPixel> Load<TPixel>(Stream stream, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Load<TPixel>(stream, context, WithFormat(format, options));
    }

    /// <summary>Decodes an image of a known format from bytes in memory as <typeparamref name="TPixel"/>. See <see cref="Load(Stream, FormatInfo, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="data">The encoded image.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The raster. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static Raster<TPixel> Load<TPixel>(ReadOnlyMemory<byte> data, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Load<TPixel>(data, context, WithFormat(format, options));
    }

    /// <summary>Loads every image of a file of a known format. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="stream">The encoded file.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterList LoadAll(Stream stream, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return LoadAll(stream, context, WithFormat(format, options));
    }

    /// <summary>Loads every image of a file of a known format in memory. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded file.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterList LoadAll(ReadOnlyMemory<byte> data, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return LoadAll(data, context, WithFormat(format, options));
    }

    /// <summary>Loads every image of a file of a known format as <typeparamref name="TPixel"/>. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce; each image in the list is a <see cref="Raster{TPixel}"/>.</typeparam>
    /// <param name="stream">The encoded file.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterList LoadAll<TPixel>(Stream stream, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return LoadAll<TPixel>(stream, context, WithFormat(format, options));
    }

    /// <summary>Loads every image of a file of a known format in memory as <typeparamref name="TPixel"/>. See <see cref="LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce; each image in the list is a <see cref="Raster{TPixel}"/>.</typeparam>
    /// <param name="data">The encoded file.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read each image, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The images, in the file's order. The caller must dispose the list.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">An image or the images together break a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterList LoadAll<TPixel>(ReadOnlyMemory<byte> data, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return LoadAll<TPixel>(data, context, WithFormat(format, options));
    }

    /// <summary>Reads only the header of an image of a known format. See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="stream">The encoded image. A seekable stream ends just past the bytes read.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">Reader options, or <see langword="null"/> for the defaults.</param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterInfo Probe(Stream stream, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return Probe(stream, context, WithFormat(format, options));
    }

    /// <summary>Reads only the header of an image of a known format in memory. See <see cref="Probe(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">Reader options, or <see langword="null"/> for the defaults.</param>
    /// <returns>What the header says.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterInfo Probe(ReadOnlyMemory<byte> data, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return Probe(data, context, WithFormat(format, options));
    }

    /// <summary>Returns the options with the format named, so detection is skipped.</summary>
    internal static ReaderOptions WithFormat(FormatInfo format, ReaderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Reader is null)
        {
            throw new UnsupportedFormatException($"The {format.Name} format has no reader.");
        }

        return (options ?? ReaderOptions.Default) with { Format = format };
    }
}
