using Egami.Metadata;
using Egami.Quantization;

namespace Egami.Formats.Tga;

/// <summary>
/// The TGA (Truevision Targa) format, registered in <see cref="EgamiContext.Default"/>. TGA has no signature, so it is detected
/// last and only when the whole 18-byte header is consistent.
/// </summary>
public static class TgaFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature check, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "TGA",
        MimeTypes = ["image/x-tga", "image/x-targa"],
        Extensions = ["tga", "tpic", "vda", "icb", "vst"],
        SignatureLength = 18,
        Matches = TgaReader.Matches,
        Reader = TgaReader.Instance,
        Writer = TgaWriter.Instance,
    };
}

/// <summary>Where a TGA's first stored pixel goes.</summary>
public enum TgaOrigin
{
    /// <summary>Rows go from the bottom up, pixels left to right. The default and the most widely read.</summary>
    BottomLeft,

    /// <summary>Rows go from the bottom up, pixels right to left.</summary>
    BottomRight,

    /// <summary>Rows go from the top down, pixels left to right.</summary>
    TopLeft,

    /// <summary>Rows go from the top down, pixels right to left.</summary>
    TopRight,
}

/// <summary>The TGA 2.0 alpha type from the extension area.</summary>
public enum TgaAlphaType
{
    /// <summary>No alpha data.</summary>
    None = 0,

    /// <summary>Undefined data in the alpha bits, to be ignored.</summary>
    UndefinedIgnore = 1,

    /// <summary>Undefined data in the alpha bits, to be kept.</summary>
    UndefinedKeep = 2,

    /// <summary>Straight alpha.</summary>
    Straight = 3,

    /// <summary>Premultiplied alpha. Egami un-premultiplies it on load.</summary>
    Premultiplied = 4,
}

/// <summary>Details only TGA has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
public sealed class TgaDetails : FormatDetails
{
    /// <summary>Gets or sets the image type from the header: 1, 2 or 3, or 9, 10 or 11 when run-length encoded.</summary>
    public int ImageType { get; set; }

    /// <summary>Gets or sets the bits a pixel: 8, 15, 16, 24 or 32.</summary>
    public int BitsPerPixel { get; set; }

    /// <summary>Gets or sets the number of attribute (alpha) bits a pixel from the image descriptor.</summary>
    public int AttributeBits { get; set; }

    /// <summary>Gets or sets where the first stored pixel goes.</summary>
    public TgaOrigin Origin { get; set; }

    /// <summary>Gets or sets the image ID field, up to 255 bytes. Empty if the file had none.</summary>
    public ReadOnlyMemory<byte> ImageId { get; set; }

    /// <summary>Gets or sets the alpha type from the TGA 2.0 extension area, or <see langword="null"/> if the file had none.</summary>
    public TgaAlphaType? AlphaType { get; set; }

    /// <summary>Gets or sets the date and time from the extension area, or <see langword="null"/> if absent or invalid.</summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>Gets or sets the job name from the extension area. Empty if absent.</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Gets or sets the software ID from the extension area. Empty if absent.</summary>
    public string Software { get; set; } = string.Empty;

    /// <summary>Gets or sets the pixel aspect ratio (width over height) from the extension area, or <see langword="null"/> if absent.</summary>
    public double? PixelAspectRatio { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new TgaDetails
        {
            ImageType = ImageType,
            BitsPerPixel = BitsPerPixel,
            AttributeBits = AttributeBits,
            Origin = Origin,
            ImageId = ImageId.ToArray(),
            AlphaType = AlphaType,
            Timestamp = Timestamp,
            JobName = JobName,
            Software = Software,
            PixelAspectRatio = PixelAspectRatio,
        };
    }
}

/// <summary>The pixel layout a TGA is written with.</summary>
public enum TgaBits
{
    /// <summary>8-bit gray for gray types without alpha, 24-bit when every pixel is opaque, else 32-bit with alpha.</summary>
    Auto,

    /// <summary>8-bit gray (image type 3).</summary>
    Gray8,

    /// <summary>8-bit indices into a color map of up to 256 colors, with alpha in the map when the image has any.</summary>
    Indexed8,

    /// <summary>24-bit blue, green and red; alpha is dropped.</summary>
    Bgr24,

    /// <summary>32-bit blue, green, red and alpha.</summary>
    Bgra32,
}

/// <summary>Options for writing TGA.</summary>
public sealed class TgaWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new TgaWriterOptions Default { get; } = new();

    /// <summary>Gets whether to run-length encode the pixels. Defaults to <see langword="true"/>.</summary>
    public bool Rle { get; init; } = true;

    /// <summary>Gets the pixel layout. Defaults to <see cref="TgaBits.Auto"/>.</summary>
    public TgaBits Bits { get; init; }

    /// <summary>Gets how the color map is built for <see cref="TgaBits.Indexed8"/> when the image has more than 256 colors.</summary>
    public PaletteOptions Palette { get; init; } = PaletteOptions.Default;
}
