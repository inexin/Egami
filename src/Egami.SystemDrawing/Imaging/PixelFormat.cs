namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// The memory layout of a bitmap's pixels, with System.Drawing's values: bits 8 to 15 hold the bits a pixel, bits 0 to 7 an
/// index, and the high bits the flags below.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pixel-formats"/>
/// </example>
public enum PixelFormat
{
    /// <summary>The pixels hold indices into the bitmap's palette.</summary>
    Indexed = 0x00010000,

    /// <summary>The pixels hold colors GDI can show.</summary>
    Gdi = 0x00020000,

    /// <summary>The pixels hold alpha.</summary>
    Alpha = 0x00040000,

    /// <summary>The pixels hold color premultiplied by alpha.</summary>
    PAlpha = 0x00080000,

    /// <summary>The pixels hold 16 bits a channel.</summary>
    Extended = 0x00100000,

    /// <summary>The default format, 32-bit ARGB.</summary>
    Canonical = 0x00200000,

    /// <summary>No format.</summary>
    Undefined = 0,

    /// <summary>No format; the same as <see cref="Undefined"/>.</summary>
    DontCare = 0,

    /// <summary>1 bit a pixel, an index into a 2-color palette.</summary>
    Format1bppIndexed = 196865,

    /// <summary>4 bits a pixel, an index into a 16-color palette.</summary>
    Format4bppIndexed = 197634,

    /// <summary>8 bits a pixel, an index into a 256-color palette.</summary>
    Format8bppIndexed = 198659,

    /// <summary>16 bits of gray a pixel. Bitmaps hold it, but drawing on one and reading its pixels throw, as in GDI+.</summary>
    Format16bppGrayScale = 1052676,

    /// <summary>16 bits a pixel, 5 each of blue, green and red from the lowest bit; the top bit unused. <see cref="Pixels.Bgr555"/>'s memory.</summary>
    Format16bppRgb555 = 135173,

    /// <summary>16 bits a pixel, 5 of blue, 6 of green and 5 of red from the lowest bit. <see cref="Pixels.Bgr565"/>'s memory.</summary>
    Format16bppRgb565 = 135174,

    /// <summary>16 bits a pixel, 5 each of blue, green and red from the lowest bit and 1 bit of alpha. <see cref="Pixels.Bgra5551"/>'s memory.</summary>
    Format16bppArgb1555 = 397319,

    /// <summary>24 bits a pixel: blue, green and red bytes. <see cref="Pixels.Bgr8"/>'s memory.</summary>
    Format24bppRgb = 137224,

    /// <summary>32 bits a pixel: blue, green and red bytes and one unused byte.</summary>
    Format32bppRgb = 139273,

    /// <summary>32 bits a pixel: blue, green, red and alpha bytes. <see cref="Pixels.Bgra8"/>'s memory; the default.</summary>
    Format32bppArgb = 2498570,

    /// <summary>32 bits a pixel: blue, green and red premultiplied by alpha, then alpha. <see cref="Pixels.PremulBgra8"/>'s memory.</summary>
    Format32bppPArgb = 925707,

    /// <summary>48 bits a pixel: blue, green and red in 16 bits each, linear light from 0 to 8192.</summary>
    Format48bppRgb = 1060876,

    /// <summary>64 bits a pixel: blue, green, red and alpha in 16 bits each, linear light from 0 to 8192.</summary>
    Format64bppArgb = 3424269,

    /// <summary>64 bits a pixel: linear blue, green and red premultiplied by alpha, then alpha, in 16 bits each from 0 to 8192.</summary>
    Format64bppPArgb = 1851406,

    /// <summary>The number of formats, one past the highest index.</summary>
    Max = 15,
}
