using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Bmp;

/// <summary>The BMP format, registered in <see cref="EgamiContext.Default"/>.</summary>
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
    };
}

/// <summary>How a BMP's pixels are stored.</summary>
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
public sealed class BmpDetails : FormatDetails
{
    /// <summary>Gets or sets the size of the info header: 12, 16, 40, 52, 56, 64, 108 or 124.</summary>
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
    public (uint Red, uint Green, uint Blue, uint Alpha) Masks { get; set; }

    /// <summary>Gets or sets the V4 or V5 color space type as stored, such as 0x73524742 ("sRGB"), or 0 when absent.</summary>
    public uint ColorSpaceType { get; set; }

    /// <summary>Gets or sets the V4 or V5 endpoints and gamma values as stored (48 bytes). Empty if the header had none.</summary>
    public ReadOnlyMemory<byte> Calibration { get; set; }

    /// <summary>Gets or sets the V5 rendering intent, or 0 when absent.</summary>
    public uint RenderingIntent { get; set; }

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
