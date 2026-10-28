using System.Buffers.Binary;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Formats.Bmp;

/// <summary>The BMP format, registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-info"/>
/// </example>
public static class BmpFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "BMP",
        MimeTypes = ["image/bmp", "image/x-bmp"],
        Extensions = ["bmp", "dib"],
        SignatureLength = 18,
        Matches = BmpReader.Matches,
        Reader = BmpReader.Instance,
        Writer = BmpWriter.Instance,
    };
}

/// <summary>How a BMP's pixels are stored.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-details"/>
/// </example>
public enum BmpCompression
{
    /// <summary>Uncompressed, with the default channel layout for the bit depth.</summary>
    None,

    /// <summary>Run-length encoded 8-bit indices.</summary>
    Rle8,

    /// <summary>Run-length encoded 4-bit indices.</summary>
    Rle4,

    /// <summary>Run-length encoded 24-bit colors (OS/2 2.x).</summary>
    Rle24,

    /// <summary>Uncompressed, with red, green and blue masks.</summary>
    Bitfields,

    /// <summary>Uncompressed, with red, green, blue and alpha masks.</summary>
    AlphaBitfields,

    /// <summary>A whole JPEG file.</summary>
    Jpeg,

    /// <summary>A whole PNG file.</summary>
    Png,
}

/// <summary>Details only BMP has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-details"/>
/// </example>
public sealed class BmpDetails : FormatDetails
{
    /// <summary>Gets or sets the size of the info header: 12, 16, 40, 52, 56, 64, 108 or 124. Defaults to 40.</summary>
    public int HeaderSize { get; set; } = 40;

    /// <summary>Gets or sets the bits a pixel: 1, 2, 4, 8, 16, 24 or 32 (0 for embedded JPEG or PNG).</summary>
    public int BitsPerPixel { get; set; }

    /// <summary>Gets or sets how the pixels were stored.</summary>
    public BmpCompression Compression { get; set; }

    /// <summary>Gets or sets whether the rows were stored top-down (a negative height) rather than bottom-up.</summary>
    public bool TopDown { get; set; }

    /// <summary>Gets or sets the palette. Empty if the file had none.</summary>
    public ReadOnlyMemory<Rgb8> Palette { get; set; }

    /// <summary>Gets or sets the red, green, blue and alpha masks the pixels were read with, or zeros where there were none.</summary>
    internal (uint Red, uint Green, uint Blue, uint Alpha) Masks { get; set; }

    /// <summary>Gets or sets the V4 or V5 color space type as stored, such as 0x73524742 ("sRGB"), or 0 when absent.</summary>
    internal uint ColorSpaceType { get; set; }

    /// <summary>Gets or sets the V4 or V5 endpoints and gamma values as stored (48 bytes). Empty if the header had none.</summary>
    internal ReadOnlyMemory<byte> Calibration { get; set; }

    /// <summary>Gets or sets the V5 rendering intent, or 0 when absent.</summary>
    internal uint RenderingIntent { get; set; }

    /// <summary>
    /// Gets the calibrated curve of an LCS_CALIBRATED_RGB header: the mean of its three 16.16 gamma values, when they are given.
    /// 1.0 ignored it, so it is not put in <see cref="RasterMetadata.Gamma"/>; the color profile uses it.
    /// </summary>
    internal override double? CalibratedGamma
    {
        get
        {
            ReadOnlySpan<byte> c = Calibration.Span;
            if (ColorSpaceType != 0 || c.Length != 48)
            {
                return null;
            }

            double sum = 0;
            for (int i = 0; i < 3; i++)
            {
                double g = BinaryPrimitives.ReadUInt32LittleEndian(c[(36 + (i * 4))..]) / 65536.0;
                if (!(g > 0))
                {
                    return null;
                }

                sum += g;
            }

            return sum / 3;
        }
    }

    /// <summary>Returns the primaries of the three CIEXYZ endpoints (2.30 fixed point), with their sum as white, or null when they cannot form a color space.</summary>
    internal static Colors.ColorPrimaries? Endpoints(ReadOnlySpan<byte> c)
    {
        Span<double> v = stackalloc double[9];
        for (int i = 0; i < 9; i++)
        {
            v[i] = BinaryPrimitives.ReadInt32LittleEndian(c[(i * 4)..]) / (double)(1 << 30);
        }

        Colors.ColorPrimaries primaries = new(Xy(v[0], v[1], v[2]), Xy(v[3], v[4], v[5]), Xy(v[6], v[7], v[8]), Xy(v[0] + v[3] + v[6], v[1] + v[4] + v[7], v[2] + v[5] + v[8]));
        return primaries.IsValid ? primaries : null;

        static Colors.Chromaticity Xy(double x, double y, double z) => x + y + z > 0 ? new(x / (x + y + z), y / (x + y + z)) : default;
    }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new BmpDetails
        {
            HeaderSize = HeaderSize,
            BitsPerPixel = BitsPerPixel,
            Compression = Compression,
            TopDown = TopDown,
            Palette = Palette.ToArray(),
            Masks = Masks,
            ColorSpaceType = ColorSpaceType,
            Calibration = Calibration.ToArray(),
            RenderingIntent = RenderingIntent,
        };
    }
}

/// <summary>The pixel layout a BMP is written with.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Formats.cs" region="bmp"/>
/// </example>
public enum BmpPixelFormat
{
    /// <summary>24-bit when every pixel is opaque, else 32-bit with alpha.</summary>
    Auto,

    /// <summary>A 2-color palette.</summary>
    Indexed1,

    /// <summary>A 4-color palette, a Windows CE layout many desktop readers cannot open.</summary>
    Indexed2,

    /// <summary>A palette of up to 16 colors.</summary>
    Indexed4,

    /// <summary>A palette of up to 256 colors.</summary>
    Indexed8,

    /// <summary>24-bit blue, green and red. Alpha is dropped, not blended onto a background.</summary>
    Bgr24,

    /// <summary>32-bit blue, green, red and alpha, with a V4 header and bit-field masks.</summary>
    Bgra32,

    /// <summary>16-bit 5-5-5 blue, green and red, the default 16-bit layout every reader opens. Alpha is dropped.</summary>
    Bgr555,

    /// <summary>16-bit 5-6-5 blue, green and red, with a V4 header and bit-field masks. Alpha is dropped.</summary>
    Bgr565,

    /// <summary>16-bit 5-5-5 blue, green and red and a 1-bit alpha, with a V4 header and bit-field masks.</summary>
    Bgra5551,
}

/// <summary>Options for writing BMP.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Formats.cs" region="bmp"/>
/// </example>
public sealed class BmpWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new BmpWriterOptions Default { get; } = new();

    /// <summary>Gets the pixel layout. Defaults to <see cref="BmpPixelFormat.Auto"/>.</summary>
    public BmpPixelFormat PixelFormat { get; init; }

    /// <summary>
    /// Gets how a palette is built for the indexed layouts when the image has more colors than fit: the color limit (capped at
    /// the layout's) and dithering. Alpha is dropped first, since BMP palettes are opaque.
    /// </summary>
    /// <remarks>Defaults to <see cref="PaletteOptions.Default"/>: up to 256 colors, Floyd–Steinberg dithering.</remarks>
    public PaletteOptions Palette { get; init; } = PaletteOptions.Default;
}
