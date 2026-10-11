namespace Egami.Pixels;

/// <summary>
/// The interleaved YUV layouts that operating system decoders, GPUs and webcams hand out, which a <see cref="YuvRaster"/> copies
/// to and from its planes with <see cref="YuvRaster.CopyFromPacked"/> and <see cref="YuvRaster.CopyToPacked"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="packed"/>
/// </example>
public enum YuvPackedFormat
{
    /// <summary>8-bit 4:2:0 in two planes: Y, then U and V interleaved (U first) at half width and height.</summary>
    Nv12,

    /// <summary>8-bit 4:2:0 in two planes: Y, then V and U interleaved (V first) at half width and height.</summary>
    Nv21,

    /// <summary>
    /// 10-bit 4:2:0 in two planes of little-endian 16-bit words, samples in the high 10 bits: Y, then U and V interleaved.
    /// </summary>
    P010,

    /// <summary>
    /// 4:2:0 in two planes of little-endian 16-bit words, samples in the high bits (16, or 10 or 12 with zero low bits): Y, then
    /// U and V interleaved.
    /// </summary>
    P016,

    /// <summary>8-bit 4:2:2 in one plane, four bytes for two pixels: Y0, U, Y1, V (also called YUY2).</summary>
    Yuyv,

    /// <summary>8-bit 4:2:2 in one plane, four bytes for two pixels: U, Y0, V, Y1.</summary>
    Uyvy,
}
