using Egami.Metadata;

namespace Egami.Formats.Extended.SunRaster;

/// <summary>
/// Sun raster, the rasterfile format of SunOS: a 32-byte big-endian header, an optional colormap, then rows of 1, 8, 24 or 32 bits,
/// plain or run-length coded. Registered with <see cref="ExtendedContextExtensions.WithSunRaster"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="register-one"/>
/// </example>
public static class SunRasterFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "SUN",
        MimeTypes = ["image/x-sun-raster"],
        Extensions = ["ras", "sun"],
        SignatureLength = 4,
        Matches = header => header.StartsWith((ReadOnlySpan<byte>)[0x59, 0xA6, 0x6A, 0x95]),
        Reader = new SunRasterReader(),
        Writer = new SunRasterWriter(),
    };
}

/// <summary>Details only Sun raster has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="details"/>
/// </example>
public sealed class SunRasterDetails : FormatDetails
{
    /// <summary>Gets or sets the bits per pixel: 1, 8, 24 or 32.</summary>
    public int Depth { get; set; } = 24;

    /// <summary>Gets or sets whether the pixels were run-length coded (type 2, RT_BYTE_ENCODED).</summary>
    public bool Rle { get; set; }

    /// <summary>Gets or sets whether 24-bit and 32-bit pixels are stored red first (type 3, RT_FORMAT_RGB) rather than blue first.</summary>
    public bool RgbOrder { get; set; }

    /// <summary>Gets or sets the colormap, or empty when the file has none.</summary>
    public ReadOnlyMemory<Pixels.Rgb8> Colormap { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new SunRasterDetails { Depth = Depth, Rle = Rle, RgbOrder = RgbOrder, Colormap = Colormap.ToArray() };
    }
}

/// <summary>Options for writing Sun raster.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="options"/>
/// </example>
public sealed class SunRasterWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new SunRasterWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets whether to run-length code the pixels (type 2). Defaults to <see langword="false"/>: plain pixels (type 1), which every
    /// reader takes.
    /// </summary>
    public bool Rle { get; init; }
}
