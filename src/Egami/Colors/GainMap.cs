using Egami.Pixels;

namespace Egami.Colors;

/// <summary>Where a gain map's metadata came from.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Hdr.cs" region="gain-map"/>
/// </example>
public enum GainMapFormat
{
    /// <summary>ISO 21496-1 metadata, the standard Android, Chrome, Apple and libultrahdr write.</summary>
    Iso21496,

    /// <summary>Adobe's hdrgm XMP (Ultra HDR 1.0).</summary>
    AdobeXmp,

    /// <summary>Apple's HDRGainMap auxiliary image, with the headroom from the maker note or XMP.</summary>
    Apple,
}

/// <summary>A value for each of red, green and blue; a one-channel gain map has the same three.</summary>
/// <param name="Red">The red value.</param>
/// <param name="Green">The green value.</param>
/// <param name="Blue">The blue value.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Hdr.cs" region="gain-map"/>
/// </example>
public readonly record struct GainMapValues(double Red, double Green, double Blue)
{
    /// <summary>Initializes the same value for all three.</summary>
    /// <param name="all">The value.</param>
    public GainMapValues(double all)
        : this(all, all, all)
    {
    }

    /// <summary>Gets whether the three are equal.</summary>
    public bool IsUniform => Red == Green && Green == Blue;

    /// <summary>Gets the value of channel 0, 1 or 2.</summary>
    /// <param name="channel">0 for red, 1 for green, 2 for blue.</param>
    public double this[int channel] => channel switch { 0 => Red, 1 => Green, _ => Blue };
}

/// <summary>
/// How a gain map turns its base image into another rendition, in ISO 21496-1's terms: for a gain map sample g (0 to 1) after
/// gamma, the log2 boost is lerp(<see cref="GainMapMin"/>, <see cref="GainMapMax"/>, g), applied as
/// (base + <see cref="OffsetSdr"/>) × 2^(boost × weight) − <see cref="OffsetHdr"/>, where the weight rises from 0 at a display
/// headroom of <see cref="HdrCapacityMin"/> to 1 at <see cref="HdrCapacityMax"/>. Boosts and capacities are log2 values.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Hdr.cs" region="gain-map"/>
/// </example>
public sealed record GainMapInfo
{
    /// <summary>Gets the log2 boost a gain map sample of 0 gives. Defaults to 0 (no boost).</summary>
    public GainMapValues GainMapMin { get; init; } = new(0);

    /// <summary>Gets the log2 boost a gain map sample of 1 gives.</summary>
    public GainMapValues GainMapMax { get; init; } = new(1);

    /// <summary>Gets the gamma the gain map samples were encoded with. Defaults to 1.</summary>
    public GainMapValues Gamma { get; init; } = new(1);

    /// <summary>Gets the offset added to the base image before the boost. Defaults to 1/64.</summary>
    public GainMapValues OffsetSdr { get; init; } = new(1.0 / 64);

    /// <summary>Gets the offset taken from the result after the boost. Defaults to 1/64.</summary>
    public GainMapValues OffsetHdr { get; init; } = new(1.0 / 64);

    /// <summary>Gets the log2 display headroom at and below which the base image shows unchanged. Defaults to 0.</summary>
    public double HdrCapacityMin { get; init; }

    /// <summary>Gets the log2 display headroom at and above which the gain map applies in full.</summary>
    public double HdrCapacityMax { get; init; } = 1;

    /// <summary>Gets whether the base image is the HDR rendition and the gain map turns it into SDR. Usually false.</summary>
    public bool BaseRenditionIsHdr { get; init; }

    /// <summary>
    /// Gets whether the gain map applies in the base image's color space (the usual case) rather than the alternate
    /// rendition's, which the gain map image's ICC profile then gives.
    /// </summary>
    public bool UseBaseColorSpace { get; init; } = true;

    /// <summary>Gets which metadata the file gave.</summary>
    public GainMapFormat Format { get; init; }

    /// <summary>Gets the alternate rendition's color profile when it differs from the base's, or <see langword="null"/>.</summary>
    public ColorProfile? AlternateProfile { get; init; }

    /// <summary>Gets the weight the gain map applies with on a display of <paramref name="headroom"/> (its peak over SDR white, linear).</summary>
    /// <param name="headroom">The display headroom, 1 or more.</param>
    /// <returns>0 to 1, or −1 to 0 when the base is HDR.</returns>
    public double Weight(double headroom)
    {
        double log = Math.Log2(Math.Max(headroom, 1));
        double weight = HdrCapacityMax <= HdrCapacityMin ? (log >= HdrCapacityMax ? 1 : 0) : Math.Clamp((log - HdrCapacityMin) / (HdrCapacityMax - HdrCapacityMin), 0, 1);
        return BaseRenditionIsHdr ? weight - 1 : weight;
    }
}

/// <summary>
/// An HDR gain map: a small image that, with <see cref="GainMapInfo"/>, turns an SDR base image into its HDR rendition (or the
/// reverse). Read from Ultra HDR, ISO 21496-1 and Apple JPEG files into <see cref="Metadata.RasterMetadata.GainMap"/>; written
/// back by the JPEG writer. Immutable, so rasters can share it.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Hdr.cs" region="gain-map"/>
/// </example>
public sealed partial class GainMap
{
    private readonly Lazy<byte[]> _pixels;

    /// <summary>Initializes a gain map from its metadata and image, whose pixels are copied.</summary>
    /// <param name="info">How the map applies.</param>
    /// <param name="image">The map: gray for one channel, anything else as three (red, green, blue), 8 bits a sample.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public GainMap(GainMapInfo info, Raster image)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(image);
        Info = info;
        Width = image.Width;
        Height = image.Height;
        Channels = image.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha ? 1 : 3;
        byte[] pixels = new byte[Width * Height * Channels];
        if (Channels == 1)
        {
            using Raster<Gray8> gray = image.As<Gray8>();
            for (int y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(gray.Row(y)).CopyTo(pixels.AsSpan(y * Width));
            }
        }
        else
        {
            using Raster<Rgb8> rgb = image.As<Rgb8>();
            for (int y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgb.Row(y)).CopyTo(pixels.AsSpan(y * Width * 3));
            }
        }

        _pixels = new Lazy<byte[]>(pixels);
    }

    /// <summary>Initializes a gain map from samples that are already 8-bit gray or RGB rows.</summary>
    internal GainMap(GainMapInfo info, int width, int height, int channels, byte[] pixels)
    {
        Info = info;
        Width = width;
        Height = height;
        Channels = channels;
        _pixels = new Lazy<byte[]>(pixels);
    }

    /// <summary>Initializes a gain map whose image is decoded from <paramref name="encoded"/> when first needed.</summary>
    internal GainMap(GainMapInfo info, int width, int height, int channels, ReadOnlyMemory<byte> encoded, Func<byte[]> decode)
    {
        Info = info;
        Width = width;
        Height = height;
        Channels = channels;
        Encoded = encoded;
        _pixels = new Lazy<byte[]>(decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets how the map applies.</summary>
    public GainMapInfo Info { get; }

    /// <summary>Gets the map's width, often a fraction of the base image's.</summary>
    public int Width { get; }

    /// <summary>Gets the map's height.</summary>
    public int Height { get; }

    /// <summary>Gets 1 for a gray map that boosts red, green and blue alike, or 3 for one boost each.</summary>
    public int Channels { get; }

    /// <summary>Gets the map as the file stored it (a JPEG), or empty when it was made or changed in memory.</summary>
    internal ReadOnlyMemory<byte> Encoded { get; }

    /// <summary>Gets the samples: rows of gray or RGB bytes.</summary>
    internal byte[] Pixels => _pixels.Value;

    /// <summary>Returns the map as a new raster: <see cref="Gray8"/> for one channel, <see cref="Rgb8"/> for three. The caller disposes it.</summary>
    /// <param name="context">The context for the raster, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The map's image.</returns>
    public Raster ToRaster(EgamiContext? context = null)
    {
        byte[] pixels = Pixels;
        if (Channels == 1)
        {
            Raster<Gray8> gray = Raster.Create<Gray8>(Width, Height, context);
            for (int y = 0; y < Height; y++)
            {
                pixels.AsSpan(y * Width, Width).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(gray.Row(y)));
            }

            return gray;
        }

        Raster<Rgb8> rgb = Raster.Create<Rgb8>(Width, Height, context);
        for (int y = 0; y < Height; y++)
        {
            pixels.AsSpan(y * Width * 3, Width * 3).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgb.Row(y)));
        }

        return rgb;
    }

    /// <summary>Returns the same map with other metadata.</summary>
    /// <param name="info">The new metadata.</param>
    /// <returns>The map.</returns>
    public GainMap WithInfo(GainMapInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return Encoded.IsEmpty ? new GainMap(info, Width, Height, Channels, Pixels) : new GainMap(info, Width, Height, Channels, Encoded, () => Pixels);
    }
}
