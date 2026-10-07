using System.Numerics;

namespace Egami.Pixels;

/// <summary>
/// A pixel type: an unmanaged struct whose memory is exactly its channels, in the order its name gives.
/// Channel values are sRGB-encoded and alpha is straight (not premultiplied).
/// </summary>
/// <typeparam name="TSelf">The pixel type itself.</typeparam>
public interface IPixelType<TSelf> : IEquatable<TSelf>
    where TSelf : unmanaged, IPixelType<TSelf>
{
    /// <summary>Gets the memory layout of this pixel type.</summary>
    static abstract PixelLayoutInfo Layout { get; }

    /// <summary>
    /// Creates a pixel from red, green, blue and alpha scaled to 0..1, with straight alpha.
    /// Values outside 0..1 are clamped and NaN becomes 0. Gray types take the Rec. 709 luma of the color,
    /// weighted in linear light. Types without alpha drop it.
    /// </summary>
    /// <param name="rgba">The color, with each channel scaled to 0..1.</param>
    /// <returns>The nearest pixel of this type.</returns>
    static abstract TSelf FromScaled(Vector4 rgba);

    /// <summary>
    /// Creates a pixel from an 8-bit color. The conversion is exact wherever the type can hold the color.
    /// Gray types take the Rec. 709 luma, weighted in linear light. Types without alpha drop it.
    /// </summary>
    /// <param name="value">The color.</param>
    /// <returns>The pixel.</returns>
    static abstract TSelf FromRgba8(Rgba8 value);

    /// <summary>Returns red, green, blue and alpha scaled to 0..1. Types without alpha return an alpha of 1.</summary>
    /// <returns>The scaled color.</returns>
    Vector4 ToScaled();

    /// <summary>
    /// Returns the pixel as an 8-bit color. 16-bit channels are rounded to the nearest 8-bit value,
    /// and gray is copied to red, green and blue.
    /// </summary>
    /// <returns>The 8-bit color.</returns>
    Rgba8 ToRgba8();
}
