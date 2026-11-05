using Egami.Metadata;

namespace Egami.Formats;

/// <summary>
/// An image as its codestream stores it: every component's samples at their own precision and sign, with no color conversion,
/// widening or offset. For a container that keeps its own pixel description, such as DICOM, which needs the stored values to
/// apply its rescale, window and color rules itself. JPEG (baseline, extended 12-bit and lossless at 2 to 16 bits), JPEG 2000
/// and JPEG XL load this way.
/// </summary>
/// <remarks>
/// Samples are interleaved in component order, one <see cref="int"/> each, so signed values keep their sign. Components sampled
/// at less than full resolution are brought to the image's size: JPEG's chroma upsampled as <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/>
/// does it (or replicated with <see cref="Jpeg.JpegReaderOptions.FastUpsampling"/>), JPEG 2000's replicated.
/// <para>
/// A preview, as DICOM is: marked <c>[Experimental]</c> with the diagnostic <c>EGAMIX001</c>, so a call does not compile until the
/// app suppresses that id, and it may change in any minor release.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/StoredSamples.cs" region="sample-image"/>
/// </example>
[System.Diagnostics.CodeAnalysis.Experimental(SampleImage.DiagnosticId, UrlFormat = SampleImage.DiagnosticUrl)]
public sealed class SampleImage
{
    /// <summary>The diagnostic id of the preview, shared with DICOM in Egami.Formats.Extended.</summary>
    internal const string DiagnosticId = "EGAMIX001";

    /// <summary>Where the diagnostic points.</summary>
    internal const string DiagnosticUrl = "https://github.com/inexin/Egami/blob/main/docs/articles/formats/index.md#stored-samples";

    private readonly int[] _samples;
    private readonly SampleComponent[] _components;

    internal SampleImage(int width, int height, SampleComponent[] components, SampleColor color, int[] samples, RasterMetadata metadata)
    {
        Width = width;
        Height = height;
        _components = components;
        Color = color;
        _samples = samples;
        Metadata = metadata;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets each component's precision and sign, in the order the samples are interleaved.</summary>
    public IReadOnlyList<SampleComponent> Components => _components;

    /// <summary>Gets what the components are, as far as the codestream says: <see cref="SampleColor.YCbCr"/> means no conversion to RGB was made.</summary>
    public SampleColor Color { get; }

    /// <summary>
    /// Gets every sample, row after row, each pixel's components together: sample <c>c</c> of pixel (x, y) is at
    /// <c>((y * Width) + x) * Components.Count + c</c>.
    /// </summary>
    public Memory<int> Samples => _samples;

    /// <summary>Gets the metadata the file holds, with any warnings from a lenient read of a damaged codestream.</summary>
    public RasterMetadata Metadata { get; }

    /// <summary>Returns row <paramref name="y"/>: <see cref="Width"/> pixels of <see cref="Components"/> samples each.</summary>
    /// <param name="y">The row, from 0 at the top.</param>
    /// <returns>The row's samples.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the image.</exception>
    public Span<int> Row(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        int stride = Width * _components.Length;
        return _samples.AsSpan(y * stride, stride);
    }

    /// <summary>
    /// Decodes an image to its stored samples. The format comes from the data, as for <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/>.
    /// </summary>
    /// <param name="stream">The encoded image. It is read from its current position and left open.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>. Format options apply where they make sense, such as <see cref="Jpeg.JpegReaderOptions.FastUpsampling"/> and <see cref="Jpeg2000.Jpeg2000ReaderOptions.MaxQualityLayers"/>.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="NotSupportedException">
    /// The format has no stored samples to give, or this image's do not fit: floating-point or more than 31 bits, or a JPEG with
    /// 2 or 4 components. Also thrown for <see cref="ReaderOptions.Region"/>, <see cref="ReaderOptions.MinimumSize"/> and
    /// <see cref="ReaderOptions.ConvertColorTo"/>.
    /// </exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/StoredSamples.cs" region="sample-image"/>
    /// </example>
    public static SampleImage Load(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= ReaderOptions.Default;
        if (options.Region is not null || options.MinimumSize is not null || options.ConvertColorTo is not null)
        {
            throw new NotSupportedException("Stored samples are the whole image as coded; load a Raster to crop, scale or convert color.");
        }

        return FormatPipeline.Run(stream, context ?? EgamiContext.Default, options, (format, input) => Reader(format).ReadSamples(input, options), CancellationToken.None);
    }

    /// <summary>Decodes an image file to its stored samples. See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="NotSupportedException">The format or image has no stored samples to give.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/StoredSamples.cs" region="sample-image"/>
    /// </example>
    public static SampleImage Load(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using FileStream stream = Raster.OpenRead(path);
        return Load(stream, context, options);
    }

    /// <summary>Decodes an image in memory to its stored samples. See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="NotSupportedException">The format or image has no stored samples to give.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/StoredSamples.cs" region="sample-image"/>
    /// </example>
    public static SampleImage Load(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using MemoryStream stream = Raster.AsStream(data);
        return Load(stream, context, options);
    }

    /// <summary>
    /// Decodes an image of a known format to its stored samples, without detecting the format: a codestream taken from a
    /// container, such as a DICOM pixel data fragment. See <see cref="Load(Stream, EgamiContext?, ReaderOptions?)"/>.
    /// </summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="format">The format to read with, such as <see cref="Jpeg2000.Jpeg2000Format.Info"/>. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="NotSupportedException">The format or image has no stored samples to give.</exception>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/StoredSamples.cs" region="sample-image-format"/>
    /// </example>
    public static SampleImage Load(ReadOnlyMemory<byte> data, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return Load(data, context, Raster.WithFormat(format, options));
    }

    private static ISampleFormatReader Reader(FormatInfo format)
    {
        return format.Reader as ISampleFormatReader
            ?? throw new NotSupportedException($"{format.Name} files have no stored samples to give; load them with Raster.Load.");
    }

    /// <summary>Checks the size and allocates the samples of an image, within the load's limits.</summary>
    internal static int[] Allocate(EgamiInput input, int width, int height, int components)
    {
        input.CheckFrameSize(width, height);
        long count = (long)width * height * components;
        input.CheckMemory(count * sizeof(int));
        if (count > Array.MaxLength)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), Array.MaxLength, count);
        }

        return new int[count];
    }
}

/// <summary>One component of a <see cref="SampleImage"/>.</summary>
/// <param name="Precision">The bits per sample the codestream stores, 1 to 31.</param>
/// <param name="IsSigned">Whether samples are signed: then they run from −2^(Precision − 1) to 2^(Precision − 1) − 1, else from 0 to 2^Precision − 1.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/StoredSamples.cs" region="sample-image"/>
/// </example>
[System.Diagnostics.CodeAnalysis.Experimental(SampleImage.DiagnosticId, UrlFormat = SampleImage.DiagnosticUrl)]
public readonly record struct SampleComponent(int Precision, bool IsSigned);

/// <summary>What the components of a <see cref="SampleImage"/> are.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/StoredSamples.cs" region="sample-image"/>
/// </example>
[System.Diagnostics.CodeAnalysis.Experimental(SampleImage.DiagnosticId, UrlFormat = SampleImage.DiagnosticUrl)]
public enum SampleColor
{
    /// <summary>The codestream does not say, such as palette indices or a component count no color space has.</summary>
    Unknown,

    /// <summary>One gray component, or gray then alpha.</summary>
    Gray,

    /// <summary>Red, green and blue, then alpha when there is a fourth. A JPEG 2000 component transform (RCT or ICT) has been undone.</summary>
    Rgb,

    /// <summary>Luma and two chroma components, not converted: a JPEG in YCbCr, or a JPEG 2000 file in sYCC.</summary>
    YCbCr,
}

/// <summary>A reader that can decode a format's stored samples, for <see cref="SampleImage.Load(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
internal interface ISampleFormatReader
{
    /// <summary>Decodes the samples, or throws <see cref="NotSupportedException"/> when this image's do not fit.</summary>
    SampleImage ReadSamples(EgamiInput input, ReaderOptions options);
}
