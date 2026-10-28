namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// An image file format, by the GUID GDI+ gives it: what <see cref="Image.RawFormat"/> reports and what
/// <see cref="Image.Save(Stream, ImageFormat)"/> writes.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public sealed class ImageFormat
{
    private static readonly ImageFormat MemoryBmpFormat = new(new Guid("{b96b3caa-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat BmpFormat = new(new Guid("{b96b3cab-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat EmfFormat = new(new Guid("{b96b3cac-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat WmfFormat = new(new Guid("{b96b3cad-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat JpegFormat = new(new Guid("{b96b3cae-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat PngFormat = new(new Guid("{b96b3caf-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat GifFormat = new(new Guid("{b96b3cb0-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat TiffFormat = new(new Guid("{b96b3cb1-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat ExifFormat = new(new Guid("{b96b3cb2-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat IconFormat = new(new Guid("{b96b3cb5-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat HeifFormat = new(new Guid("{b96b3cb6-0728-11d3-9d7b-0000f81ef32e}"));
    private static readonly ImageFormat WebpFormat = new(new Guid("{b96b3cb7-0728-11d3-9d7b-0000f81ef32e}"));

    /// <summary>Initializes a new instance of the <see cref="ImageFormat"/> class from its GUID.</summary>
    /// <param name="guid">The GUID.</param>
    public ImageFormat(Guid guid)
    {
        Guid = guid;
    }

    /// <summary>Gets the GUID.</summary>
    public Guid Guid { get; }

    /// <summary>Gets the format of a bitmap made in memory rather than read from a file.</summary>
    public static ImageFormat MemoryBmp => MemoryBmpFormat;

    /// <summary>Gets BMP.</summary>
    public static ImageFormat Bmp => BmpFormat;

    /// <summary>Gets EMF.</summary>
    public static ImageFormat Emf => EmfFormat;

    /// <summary>Gets WMF.</summary>
    public static ImageFormat Wmf => WmfFormat;

    /// <summary>Gets JPEG.</summary>
    public static ImageFormat Jpeg => JpegFormat;

    /// <summary>Gets PNG.</summary>
    public static ImageFormat Png => PngFormat;

    /// <summary>Gets GIF.</summary>
    public static ImageFormat Gif => GifFormat;

    /// <summary>Gets TIFF.</summary>
    public static ImageFormat Tiff => TiffFormat;

    /// <summary>Gets EXIF (JPEG with EXIF data). GDI+ has no encoder for it.</summary>
    public static ImageFormat Exif => ExifFormat;

    /// <summary>Gets ICO.</summary>
    public static ImageFormat Icon => IconFormat;

    /// <summary>Gets HEIF. Egami reads HEIC through Egami.Heic when it is registered, and does not write it.</summary>
    public static ImageFormat Heif => HeifFormat;

    /// <summary>Gets WebP.</summary>
    public static ImageFormat Webp => WebpFormat;

    /// <inheritdoc/>
    public override bool Equals(object? o) => o is ImageFormat other && other.Guid == Guid;

    /// <inheritdoc/>
    public override int GetHashCode() => Guid.GetHashCode();

    /// <summary>Returns the format's name, such as Png or MemoryBMP, or its GUID in brackets.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name(Guid) ?? "[ImageFormat: " + Guid + "]";

    internal static string? Name(Guid guid) => guid switch
    {
        _ when guid == MemoryBmpFormat.Guid => "MemoryBMP",
        _ when guid == BmpFormat.Guid => "Bmp",
        _ when guid == EmfFormat.Guid => "Emf",
        _ when guid == WmfFormat.Guid => "Wmf",
        _ when guid == JpegFormat.Guid => "Jpeg",
        _ when guid == PngFormat.Guid => "Png",
        _ when guid == GifFormat.Guid => "Gif",
        _ when guid == TiffFormat.Guid => "Tiff",
        _ when guid == ExifFormat.Guid => "Exif",
        _ when guid == IconFormat.Guid => "Icon",
        _ when guid == HeifFormat.Guid => "Heif",
        _ when guid == WebpFormat.Guid => "Webp",
        _ => null,
    };
}
