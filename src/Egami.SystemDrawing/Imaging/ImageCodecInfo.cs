namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// One of the codecs <see cref="Image.Save(Stream, ImageCodecInfo, EncoderParameters?)"/> writes with, as GDI+ lists them:
/// BMP, JPEG, GIF, TIFF and PNG encoders, and those plus EMF, WMF and ICO decoders. Egami reads many more formats than the
/// decoders listed; the list matches GDI+'s so code that looks a codec up by its MIME type or GUID finds the same one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public sealed class ImageCodecInfo
{
    private const ImageCodecFlags Both = ImageCodecFlags.Encoder | ImageCodecFlags.Decoder | ImageCodecFlags.SupportBitmap | ImageCodecFlags.Builtin;
    private const ImageCodecFlags Reads = ImageCodecFlags.Decoder | ImageCodecFlags.SupportBitmap | ImageCodecFlags.Builtin;

    /// <summary>Initializes a new instance of the <see cref="ImageCodecInfo"/> class.</summary>
    private ImageCodecInfo()
    {
    }

    /// <summary>Gets or sets the codec's class id, which <see cref="Image.Save(Stream, ImageCodecInfo, EncoderParameters?)"/> and <see cref="Image.GetEncoderParameterList(Guid)"/> look the codec up by.</summary>
    public Guid Clsid { get; set; }

    /// <summary>Gets or sets the GUID of the codec's format, the same as its <see cref="ImageFormat.Guid"/>.</summary>
    public Guid FormatID { get; set; }

    /// <summary>Gets or sets the codec's name, such as "Built-in PNG Codec".</summary>
    public string? CodecName { get; set; }

    /// <summary>Gets or sets the file that holds the codec; empty for the built-in ones.</summary>
    public string? DllName { get; set; }

    /// <summary>Gets or sets the format's name, such as "PNG".</summary>
    public string? FormatDescription { get; set; }

    /// <summary>Gets or sets the file name extensions, such as "*.JPG;*.JPEG;*.JPE;*.JFIF".</summary>
    public string? FilenameExtension { get; set; }

    /// <summary>Gets or sets the MIME type, such as "image/png".</summary>
    public string? MimeType { get; set; }

    /// <summary>Gets or sets what the codec does.</summary>
    public ImageCodecFlags Flags { get; set; }

    /// <summary>Gets or sets the codec's version.</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets the bytes files of the format start with.</summary>
    public byte[][]? SignaturePatterns { get; set; }

    /// <summary>Gets or sets the masks the <see cref="SignaturePatterns"/> are compared through.</summary>
    public byte[][]? SignatureMasks { get; set; }

    /// <summary>Returns the decoders.</summary>
    /// <returns>A new array of the decoders.</returns>
    public static ImageCodecInfo[] GetImageDecoders() =>
        [Bmp(), Jpeg(), Gif(), Emf(), Wmf(), Tiff(Both), Png(), Ico()];

    /// <summary>Returns the encoders.</summary>
    /// <returns>A new array of the encoders.</returns>
    public static ImageCodecInfo[] GetImageEncoders() => [Bmp(), Jpeg(), Gif(), Tiff(Both), Png()];

    /// <summary>The encoder that writes a format, or null.</summary>
    internal static ImageCodecInfo? EncoderFor(Guid format) => GetImageEncoders().FirstOrDefault(c => c.FormatID == format);

    /// <summary>The encoder with a class id, or null.</summary>
    internal static ImageCodecInfo? EncoderById(Guid clsid) => GetImageEncoders().FirstOrDefault(c => c.Clsid == clsid);

    private static ImageCodecInfo Codec(int index, ImageFormat format, string name, string extensions, string mime, ImageCodecFlags flags, byte[][] patterns) => new()
    {
        Clsid = new Guid(0x557cf400 + index, 0x1a04, 0x11d3, 0x9a, 0x73, 0x00, 0x00, 0xf8, 0x1e, 0xf3, 0x2e),
        FormatID = format.Guid,
        CodecName = $"Built-in {name} Codec",
        DllName = null,
        FormatDescription = name,
        FilenameExtension = extensions,
        MimeType = mime,
        Flags = flags,
        Version = 1,
        SignaturePatterns = patterns,
        SignatureMasks = [.. patterns.Select(p => Mask(p))],
    };

    private static byte[] Mask(byte[] pattern) => [.. pattern.Select(b => (byte)0xFF)];

    private static ImageCodecInfo Bmp() => Codec(0, ImageFormat.Bmp, "BMP", "*.BMP;*.DIB;*.RLE", "image/bmp", Both, [[0x42, 0x4D]]);

    private static ImageCodecInfo Jpeg() => Codec(1, ImageFormat.Jpeg, "JPEG", "*.JPG;*.JPEG;*.JPE;*.JFIF", "image/jpeg", Both, [[0xFF, 0xD8]]);

    private static ImageCodecInfo Gif() => Codec(2, ImageFormat.Gif, "GIF", "*.GIF", "image/gif", Both, [[0x47, 0x49, 0x46, 0x38, 0x39, 0x61], [0x47, 0x49, 0x46, 0x38, 0x37, 0x61]]);

    private static ImageCodecInfo Emf()
    {
        // The EMF signature is " EMF" at offset 40; the 40 bytes before it are masked out.
        byte[] pattern = new byte[44];
        " EMF"u8.CopyTo(pattern.AsSpan(40));
        ImageCodecInfo codec = Codec(3, ImageFormat.Emf, "EMF", "*.EMF", "image/x-emf", Reads, [pattern]);
        byte[] mask = new byte[44];
        mask.AsSpan(40).Fill(0xFF);
        codec.SignatureMasks = [mask];
        return codec;
    }

    private static ImageCodecInfo Wmf() => Codec(4, ImageFormat.Wmf, "WMF", "*.WMF", "image/x-wmf", Reads, [[0xD7, 0xCD, 0xC6, 0x9A]]);

    private static ImageCodecInfo Tiff(ImageCodecFlags flags) => Codec(5, ImageFormat.Tiff, "TIFF", "*.TIF;*.TIFF", "image/tiff", flags, [[0x49, 0x49], [0x4D, 0x4D]]);

    private static ImageCodecInfo Png() => Codec(6, ImageFormat.Png, "PNG", "*.PNG", "image/png", Both, [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]]);

    private static ImageCodecInfo Ico() => Codec(7, ImageFormat.Icon, "ICO", "*.ICO", "image/x-icon", Reads, [[0x00, 0x00, 0x01, 0x00]]);
}
