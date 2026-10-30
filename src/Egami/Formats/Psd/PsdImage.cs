using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Psd;

/// <summary>Reads what PSD and PSB hold besides the image Load gives: the layers, each with its pixels and mask.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
/// </example>
public static class PsdImage
{
    /// <summary>
    /// Reads every layer with its pixels, for editors and for flattening: each in the natural pixel type Load would use, always
    /// with alpha (<see cref="Rgba8"/>, <see cref="Rgba16"/> or <see cref="RgbaF32"/> for RGB and Lab, <see cref="GrayAlpha8"/>
    /// or <see cref="GrayAlpha16"/> for gray, <see cref="RgbaF32"/> for 32-bit gray), CMYK layers as an <see cref="InkRaster"/>.
    /// Load gives only the composite Photoshop stored.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The document. The caller must dispose it.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">A layer breaks <see cref="SafetyLimits.MaxWidth"/>, <see cref="SafetyLimits.MaxHeight"/> or <see cref="SafetyLimits.MaxPixels"/>, or the layers together break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
    /// </example>
    public static PsdDocument ReadLayers(string path, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return ReadLayers(stream, options, context);
    }

    /// <summary>Reads every layer with its pixels from a stream; see <see cref="ReadLayers(string, ReaderOptions?, EgamiContext?)"/>.</summary>
    /// <param name="stream">The file's data, from its start. It need not seek.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The document. The caller must dispose it.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">A layer or the layers together break a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
    /// </example>
    public static PsdDocument ReadLayers(Stream stream, ReaderOptions? options = null, EgamiContext? context = null)
    {
        ReaderOptions read = (options ?? ReaderOptions.Default) with { Format = PsdFormat.Info };
        EgamiContext used = context ?? EgamiContext.Default;
        return FormatPipeline.Run(stream, used, read, (_, input) => PsdLayerReader.Read(input, read, used, null), CancellationToken.None);
    }

    /// <summary>
    /// Reads every layer with its pixels converted to <typeparamref name="TPixel"/>; see
    /// <see cref="ReadLayers(string, ReaderOptions?, EgamiContext?)"/>. Every <see cref="PsdLayerImage.Pixels"/> is a
    /// <see cref="Raster{TPixel}"/>; a CMYK layer keeps its <see cref="PsdLayerImage.Ink"/> too.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the layers.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The document. The caller must dispose it.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">A layer or the layers together break a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
    /// </example>
    public static PsdDocument ReadLayers<TPixel>(string path, ReaderOptions? options = null, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return ReadLayers<TPixel>(stream, options, context);
    }

    /// <summary>Reads every layer with its pixels converted to <typeparamref name="TPixel"/> from a stream; see <see cref="ReadLayers{TPixel}(string, ReaderOptions?, EgamiContext?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type of the layers.</typeparam>
    /// <param name="stream">The file's data, from its start. It need not seek.</param>
    /// <param name="options">The general options; the frame range is ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The document. The caller must dispose it.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">A layer or the layers together break a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
    /// </example>
    public static PsdDocument ReadLayers<TPixel>(Stream stream, ReaderOptions? options = null, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ReaderOptions read = (options ?? ReaderOptions.Default) with { Format = PsdFormat.Info };
        EgamiContext used = context ?? EgamiContext.Default;
        return FormatPipeline.Run(stream, used, read, (_, input) => PsdLayerReader.Read(input, read, used, static raster => raster.As<TPixel>()), CancellationToken.None);
    }
}

/// <summary>A PSD or PSB file's layers with their pixels, from <see cref="PsdImage.ReadLayers(string, ReaderOptions?, EgamiContext?)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
/// </example>
public sealed partial class PsdDocument : IDisposable
{
    private readonly PsdLayerImage[] _layers;
    private readonly PsdFile _file;
    private readonly int[] _imageOf;
    private bool _disposed;

    internal PsdDocument(PsdFile file, PsdLayerImage[] layers, int[] imageOf, RasterMetadata metadata, EgamiContext context)
    {
        (Width, Height, ColorMode, BitsPerChannel, _layers, Metadata, Context) = (file.Width, file.Height, file.Mode, file.Depth, layers, metadata, context);
        (_file, _imageOf) = (file, imageOf);
    }

    /// <summary>Gets the canvas width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the canvas height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the file's color mode.</summary>
    public PsdColorMode ColorMode { get; }

    /// <summary>Gets the bits of each channel: 8, 16 or 32.</summary>
    public int BitsPerChannel { get; }

    /// <summary>
    /// Gets the layers and groups, bottom to top as the file lists them; a group comes after its contents, which name it in
    /// <see cref="PsdLayerImage.ParentIndex"/>. The hidden records that mark where a group starts are left out.
    /// </summary>
    public IReadOnlyList<PsdLayerImage> Layers => _layers;

    /// <summary>Gets the file's metadata as Load gives it, with the <see cref="PsdDetails"/> and the warnings reading the layers raised.</summary>
    public RasterMetadata Metadata { get; }

    /// <summary>Gets the context the layers were read with.</summary>
    public EgamiContext Context { get; }

    /// <summary>
    /// Composites the layers as Photoshop does, with each layer's <see cref="PsdLayerImage.Info"/> as it is now, so a changed copy
    /// (a layer hidden, another opacity or blend mode) flattens differently: visibility, opacity, fill opacity, blend mode (all of
    /// Photoshop's), pixel and vector masks, clipping, pass-through and isolated groups, blending ranges, solid color fills and
    /// the levels, curves, hue/saturation, brightness/contrast and invert adjustments. Layer effects, other adjustments,
    /// gradient and pattern fills and knockout are left out with one warning a kind naming the layers. 8 and 16-bit documents
    /// blend their stored values, 32-bit ones linear light.
    /// </summary>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The flattened image at the canvas's size, in the type Load gives a file of this mode and depth: with alpha only when some
    /// pixel is not opaque. Its metadata is the document's, with the warnings of the flattening. The caller must dispose it.
    /// </returns>
    /// <exception cref="UnsupportedFormatException">The document is Lab, bitmap, indexed or multichannel, which Egami does not flatten.</exception>
    /// <exception cref="LimitExceededException">The canvases the flattening needs break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The document has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-flatten"/>
    /// </example>
    public Raster Flatten(PsdFlattenOptions? options = null)
    {
        WarningList warnings = new();
        Raster flat = Flattener(options, warnings).Finish();
        flat.Metadata.ReplaceWith(Metadata);
        flat.Metadata.WarningList.AddRange(warnings);
        return flat;
    }

    /// <summary>
    /// Composites a CMYK document's layers onto ink plates, as <see cref="Flatten"/> does, with the ink blend math of Egami.Drawing's
    /// ink canvas: every blend mode on the inks' complements, the non-separable ones on cyan, magenta and yellow with black kept.
    /// </summary>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The flattened inks: the process inks of the file's CMYK profile (FOGRA39's when it has none), at the document's depth,
    /// premultiplied by the alpha plane. The caller must dispose it.
    /// </returns>
    /// <exception cref="UnsupportedFormatException">The document is not CMYK.</exception>
    /// <exception cref="LimitExceededException">The canvases the flattening needs break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The document has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-flatten"/>
    /// </example>
    public InkRaster FlattenInk(PsdFlattenOptions? options = null)
    {
        if (ColorMode != PsdColorMode.Cmyk)
        {
            throw new UnsupportedFormatException($"Only CMYK documents flatten to inks; this one is {ColorMode}.");
        }

        WarningList warnings = new();
        PsdFlattener flattener = Flattener(options, warnings);
        return flattener.FinishInk(PsdLayerReader.InksOf(_file));
    }

    private PsdFlattener Flattener(PsdFlattenOptions? options, WarningList warnings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ColorMode is not (PsdColorMode.Rgb or PsdColorMode.Grayscale or PsdColorMode.Duotone or PsdColorMode.Cmyk))
        {
            throw new UnsupportedFormatException($"{ColorMode} documents are not flattened; Load gives the composite Photoshop stored.");
        }

        PsdFlattener flattener = new(_file, options ?? PsdFlattenOptions.Default, Context, warnings, i => _imageOf[i] >= 0 ? _layers[_imageOf[i]].Info : _file.Records[i].Info);
        for (int i = 0; i < _imageOf.Length; i++)
        {
            PsdLayerImage? layer = _imageOf[i] >= 0 ? _layers[_imageOf[i]] : null;
            flattener.Add(i, layer?.Pixels, layer?.Mask, layer?.Ink);
        }

        return flattener;
    }

    /// <summary>Disposes every layer's pixels and mask.</summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (PsdLayerImage layer in _layers)
        {
            layer.Dispose();
        }
    }
}

/// <summary>Options for <see cref="PsdDocument.Flatten"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-flatten"/>
/// </example>
public sealed record PsdFlattenOptions
{
    /// <summary>Gets the default options.</summary>
    public static PsdFlattenOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the adjustment layers Egami knows (levels, curves, hue/saturation, brightness/contrast and
    /// invert) change the layers under them. On by default; off leaves every adjustment layer out, without a warning.
    /// </summary>
    public bool ApplyAdjustments { get; init; } = true;
}

/// <summary>One layer or group of a <see cref="PsdDocument"/>, with its pixels and mask.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
/// </example>
public sealed class PsdLayerImage : IDisposable
{
    private PsdLayer _info;

    internal PsdLayerImage(PsdLayerRecord record, int index, Raster? pixels, InkRaster? ink, PsdMask? mask)
    {
        Record = record;
        _info = record.Info;
        Index = index;
        Pixels = pixels;
        Ink = ink;
        Mask = mask;
        X = record.Left;
        Y = record.Top;
    }

    /// <summary>
    /// Gets or sets the layer's record: name, opacity, blend mode, visibility and the rest. Set a changed copy (with
    /// <c>with</c>) to flatten the document differently, for example with a layer hidden.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public PsdLayer Info
    {
        get => _info;
        set => _info = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the layer's position in <see cref="PsdDocument.Layers"/>.</summary>
    public int Index { get; }

    /// <summary>Gets the position in <see cref="PsdDocument.Layers"/> of the group the layer is in, or −1 at the top level.</summary>
    public int ParentIndex { get; internal set; } = -1;

    /// <summary>
    /// Gets the layer's pixels at its own size, with straight alpha, or <see langword="null"/> for a group, a layer without
    /// pixels (an adjustment, an empty layer) and a CMYK layer, whose pixels are in <see cref="Ink"/>.
    /// </summary>
    public Raster? Pixels { get; }

    /// <summary>Gets a CMYK layer's pixels as ink, the planes premultiplied by its alpha; <see langword="null"/> for other modes.</summary>
    public InkRaster? Ink { get; }

    /// <summary>Gets the left edge of the layer on the canvas; it can be negative or reach past the canvas.</summary>
    public int X { get; }

    /// <summary>Gets the top edge of the layer on the canvas.</summary>
    public int Y { get; }

    /// <summary>Gets the layer's pixel mask, or <see langword="null"/>.</summary>
    public PsdMask? Mask { get; }

    internal PsdLayerRecord Record { get; }

    /// <summary>Disposes the pixels and the mask.</summary>
    public void Dispose()
    {
        Pixels?.Dispose();
        Ink?.Dispose();
        Mask?.Pixels.Dispose();
    }
}

/// <summary>A layer's pixel mask: 0 hides the layer, the maximum shows it.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-read-layers"/>
/// </example>
public sealed class PsdMask
{
    internal PsdMask(Raster pixels, Rect bounds, int defaultColor, bool isDisabled, float density, double feather)
    {
        (Pixels, Bounds, DefaultColor, IsDisabled, Density, Feather) = (pixels, bounds, defaultColor, isDisabled, density, feather);
    }

    /// <summary>Gets the mask's values: <see cref="Gray8"/>, <see cref="Gray16"/> for a 16-bit file or <see cref="GrayF32"/> for a 32-bit one.</summary>
    public Raster Pixels { get; }

    /// <summary>Gets where the mask lies on the canvas.</summary>
    public Rect Bounds { get; }

    /// <summary>Gets the mask's value outside <see cref="Bounds"/>: 0 (hidden) or 255 (shown).</summary>
    public int DefaultColor { get; }

    /// <summary>Gets whether the mask is turned off, so it does not hide anything.</summary>
    public bool IsDisabled { get; }

    /// <summary>Gets the mask's density, 0 to 1: how much of the hiding applies.</summary>
    public float Density { get; }

    /// <summary>Gets the feather radius in pixels Photoshop blurs the mask by when it draws; the values are not blurred.</summary>
    public double Feather { get; }
}
