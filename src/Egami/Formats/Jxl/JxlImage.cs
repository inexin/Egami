using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>Reads what JPEG XL holds besides the image Load gives: the layers before blending, and the preview.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
/// </example>
public static class JxlImage
{
    /// <summary>
    /// Reads every regular frame as it is before blending, for editors: each in the file's color space and natural pixel type,
    /// at its own size, with where it goes on the canvas and how it blends. Load gives the frames already blended.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The layers, in the file's order. The caller must dispose each.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
    /// </example>
    public static IReadOnlyList<JxlLayer> ReadLayers(string path, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return ReadLayers(stream, options, context);
    }

    /// <summary>Reads every regular frame before blending from a stream; see <see cref="ReadLayers(string, ReaderOptions?, EgamiContext?)"/>.</summary>
    /// <param name="stream">The file's data, from its start.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The layers, in the file's order. The caller must dispose each.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
    /// </example>
    public static IReadOnlyList<JxlLayer> ReadLayers(Stream stream, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ReaderOptions read = (options ?? ReaderOptions.Default) with { Format = JxlFormat.Info };
        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, read, (_, input) => JxlImageDecoder.DecodeLayers(input, read), CancellationToken.None);
    }

    /// <summary>Reads the preview: a small image some files carry for quick display, in the natural pixel type.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The preview, or <see langword="null"/> when the file has none. The caller must dispose it.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
    /// </example>
    public static Raster? ReadPreview(string path, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return ReadPreview(stream, options, context);
    }

    /// <summary>Reads the preview from a stream; see <see cref="ReadPreview(string, ReaderOptions?, EgamiContext?)"/>.</summary>
    /// <param name="stream">The file's data, from its start.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The preview, or <see langword="null"/> when the file has none. The caller must dispose it.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
    /// </example>
    public static Raster? ReadPreview(Stream stream, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ReaderOptions read = (options ?? ReaderOptions.Default) with { Format = JxlFormat.Info };
        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, read, (_, input) => JxlImageDecoder.DecodePreview(input, read), CancellationToken.None);
    }

    /// <summary>
    /// Writes the JPEG a JPEG XL file was losslessly recompressed from, byte for byte (design question 8). Load decodes such a
    /// file to pixels like any other; this gives the original JPEG back instead, when <see cref="JxlDetails.HasJpegReconstruction"/>.
    /// </summary>
    /// <param name="path">The JPEG XL file.</param>
    /// <param name="outputPath">The JPEG file to write.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <exception cref="UnsupportedFormatException">The file has no JPEG reconstruction data, or its frame is not one Egami can rebuild a JPEG from.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="reconstruct"/>
    /// </example>
    public static void ReconstructJpeg(string path, string outputPath, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(outputPath);
        using FileStream input = File.OpenRead(path);
        using MemoryStream jpeg = new();
        ReconstructJpeg(input, jpeg, context);
        File.WriteAllBytes(outputPath, jpeg.ToArray());
    }

    /// <summary>Writes the original JPEG of a recompressed JPEG XL file to a stream; see <see cref="ReconstructJpeg(string, string, EgamiContext?)"/>.</summary>
    /// <param name="stream">The JPEG XL file's data, from its start.</param>
    /// <param name="output">Where to write the JPEG.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <exception cref="UnsupportedFormatException">The file has no JPEG reconstruction data, or its frame is not one Egami can rebuild a JPEG from.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="reconstruct"/>
    /// </example>
    public static void ReconstructJpeg(Stream stream, Stream output, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ReaderOptions read = ReaderOptions.Default with { Format = JxlFormat.Info };
        FormatPipeline.Run<object?>(stream, context ?? EgamiContext.Default, read, (_, input) =>
        {
            // Written to memory first, so a failure halfway leaves the output untouched.
            using MemoryStream jpeg = new();
            JxlImageDecoder.ReconstructJpeg(input, jpeg);
            jpeg.Position = 0;
            jpeg.CopyTo(output);
            return null;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Recompresses a JPEG into JPEG XL without loss, about a fifth smaller (design question 8): the JPEG's quantized
    /// coefficients become the JPEG XL image, so Load shows the same pixels a JPEG decoder would, and
    /// <see cref="ReconstructJpeg(string, string, EgamiContext?)"/> gives the original file back byte for byte. The file
    /// is what cjxl makes at its default effort, with the ICC profile in the codestream and Exif and XMP in boxes.
    /// </summary>
    /// <param name="path">The JPEG file.</param>
    /// <param name="outputPath">The JPEG XL file to write.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <exception cref="UnsupportedFormatException">
    /// The JPEG uses something JPEG XL cannot keep: arithmetic coding, 12-bit samples, CMYK or four components, or sampling
    /// other than 4:4:4, 4:2:0, 4:2:2 and 4:4:0.
    /// </exception>
    /// <exception cref="CorruptDataException">The JPEG breaks the format's rules.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="recompress"/>
    /// </example>
    public static void RecompressJpeg(string path, string outputPath, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(outputPath);
        byte[] jxl = Recompress(File.ReadAllBytes(path), context ?? EgamiContext.Default);
        File.WriteAllBytes(outputPath, jxl);
    }

    /// <summary>Recompresses a JPEG from a stream; see <see cref="RecompressJpeg(string, string, EgamiContext?)"/>.</summary>
    /// <param name="stream">The JPEG's data, from its start.</param>
    /// <param name="output">Where to write the JPEG XL file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <exception cref="UnsupportedFormatException">The JPEG uses something JPEG XL cannot keep.</exception>
    /// <exception cref="CorruptDataException">The JPEG breaks the format's rules.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="recompress"/>
    /// </example>
    public static void RecompressJpeg(Stream stream, Stream output, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        using MemoryStream input = new();
        stream.CopyTo(input);
        output.Write(Recompress(input.ToArray(), context ?? EgamiContext.Default));
    }

    private static byte[] Recompress(byte[] jpeg, EgamiContext context) => JxlJpegEncoder.Recompress(jpeg, context);

    /// <summary>
    /// Reads one extra channel, such as depth, a selection mask, thermal data or a spot color, into a gray raster at the image
    /// size: as stored, scaled to 0 to 1 for integer samples, with a frame for each frame Load shows.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="index">The channel's index in <see cref="JxlDetails.ExtraChannels"/>.</param>
    /// <param name="options">The general options, with the frame range.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The channel's samples. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The image has no extra channel <paramref name="index"/>.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="extra-channels"/>
    /// </example>
    public static Raster<GrayF32> ReadExtraChannel(string path, int index, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return ReadExtraChannel(stream, index, options, context);
    }

    /// <summary>Reads one extra channel from a stream; see <see cref="ReadExtraChannel(string, int, ReaderOptions?, EgamiContext?)"/>.</summary>
    /// <param name="stream">The file's data, from its start.</param>
    /// <param name="index">The channel's index in <see cref="JxlDetails.ExtraChannels"/>.</param>
    /// <param name="options">The general options, with the frame range.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The channel's samples. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The image has no extra channel <paramref name="index"/>.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="extra-channels"/>
    /// </example>
    public static Raster<GrayF32> ReadExtraChannel(Stream stream, int index, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ReaderOptions read = (options ?? ReaderOptions.Default) with { Format = JxlFormat.Info };
        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, read, (_, input) => JxlImageDecoder.DecodeExtraChannel(input, read, index), CancellationToken.None);
    }
}

/// <summary>How a JPEG XL layer combines with the canvas below it (F.2).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
/// </example>
public enum JxlLayerBlend
{
    /// <summary>The layer replaces the canvas.</summary>
    Replace = 0,

    /// <summary>The layer's samples are added to the canvas's.</summary>
    Add = 1,

    /// <summary>The layer is alpha-blended over the canvas.</summary>
    Blend = 2,

    /// <summary>The layer, weighted by its alpha, is added to the canvas.</summary>
    MulAdd = 3,

    /// <summary>The canvas is multiplied by the layer.</summary>
    Multiply = 4,
}

/// <summary>A JPEG XL frame before blending, from <see cref="JxlImage.ReadLayers(string, ReaderOptions?, EgamiContext?)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
/// </example>
public sealed class JxlLayer : IDisposable
{
    internal JxlLayer(Raster raster, int x, int y, JxlLayerBlend blend, TimeSpan duration, string name)
    {
        Raster = raster;
        X = x;
        Y = y;
        Blend = blend;
        Duration = duration;
        Name = name;
    }

    /// <summary>Gets the layer's pixels, at its own size.</summary>
    public Raster Raster { get; }

    /// <summary>Gets the left edge of the layer on the canvas; it can be negative or reach past the canvas.</summary>
    public int X { get; }

    /// <summary>Gets the top edge of the layer on the canvas.</summary>
    public int Y { get; }

    /// <summary>Gets how the layer's color combines with the canvas.</summary>
    public JxlLayerBlend Blend { get; }

    /// <summary>Gets how long the canvas shows after this layer in an animation; zero for a layer that is not shown alone.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets the layer's name, or an empty string.</summary>
    public string Name { get; }

    /// <summary>Disposes the raster.</summary>
    public void Dispose() => Raster.Dispose();
}

/// <summary>Options for reading JPEG XL, on top of the general <see cref="ReaderOptions"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="layers"/>
/// </example>
public sealed record JxlReaderOptions : ReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static new JxlReaderOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether spot color channels are mixed into the color, as djxl does. On by default; off leaves the
    /// color as coded, and the spot channels can be read with <see cref="JxlImage.ReadExtraChannel(string, int, ReaderOptions?, EgamiContext?)"/>.
    /// </summary>
    public bool RenderSpotColors { get; init; } = true;

    /// <summary>
    /// Gets how much smaller than the image the decode may be. 8 or more decodes only the LF image of a lossy still, an eighth of
    /// the size each way and far faster; Modular, animated and layered files, and files with extra channels, decode in full.
    /// 1, the default, always decodes in full.
    /// </summary>
    public int MaxDownscale { get; init; } = 1;

    /// <summary>
    /// Gets a value indicating whether to give linear light in single float, in the file's own primaries (sRGB's when its ICC
    /// profile has none), instead of the file's encoded color space. Off by default. CMYK files are not affected.
    /// </summary>
    public bool LinearOutput { get; init; }
}
