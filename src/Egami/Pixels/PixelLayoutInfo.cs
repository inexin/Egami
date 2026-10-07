using System.Diagnostics.CodeAnalysis;

namespace Egami.Pixels;

/// <summary>The order in which a pixel type stores its channels.</summary>
public enum PixelChannelOrder
{
    /// <summary>Red, green, blue, alpha.</summary>
    Rgba,

    /// <summary>Blue, green, red, alpha.</summary>
    Bgra,

    /// <summary>Red, green, blue.</summary>
    Rgb,

    /// <summary>Blue, green, red.</summary>
    Bgr,

    /// <summary>Luminance only.</summary>
    Gray,

    /// <summary>Luminance, then alpha.</summary>
    GrayAlpha,
}

/// <summary>The storage type of every channel in a pixel type.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Float32 names the channel encoding, as in GPU format names.")]
public enum PixelChannelType
{
    /// <summary>An 8-bit unsigned integer, 0 to 255, meaning 0 to 1.</summary>
    Unorm8,

    /// <summary>A 16-bit unsigned integer, 0 to 65535, meaning 0 to 1.</summary>
    Unorm16,

    /// <summary>A 32-bit IEEE float, nominally 0 to 1.</summary>
    Float32,
}

/// <summary>How a pixel type stores alpha.</summary>
public enum AlphaKind
{
    /// <summary>The type has no alpha channel. Every pixel is opaque.</summary>
    None,

    /// <summary>Straight (not premultiplied) alpha.</summary>
    Straight,
}

/// <summary>Describes the memory layout of a pixel type.</summary>
/// <param name="ChannelOrder">The order of the channels in memory.</param>
/// <param name="ChannelType">The storage type shared by every channel.</param>
/// <param name="Alpha">How alpha is stored, if at all.</param>
public readonly record struct PixelLayoutInfo(PixelChannelOrder ChannelOrder, PixelChannelType ChannelType, AlphaKind Alpha)
{
    /// <summary>Gets the number of channels, including alpha.</summary>
    public int ChannelCount => ChannelOrder switch
    {
        PixelChannelOrder.Rgba or PixelChannelOrder.Bgra => 4,
        PixelChannelOrder.Rgb or PixelChannelOrder.Bgr => 3,
        PixelChannelOrder.GrayAlpha => 2,
        _ => 1,
    };

    /// <summary>Gets the number of bits in each channel.</summary>
    public int BitsPerChannel => ChannelType switch
    {
        PixelChannelType.Unorm8 => 8,
        PixelChannelType.Unorm16 => 16,
        _ => 32,
    };

    /// <summary>Gets the size of one pixel in bytes.</summary>
    public int BytesPerPixel => ChannelCount * BitsPerChannel / 8;

    /// <summary>Gets a value indicating whether the type has an alpha channel.</summary>
    public bool HasAlpha => Alpha != AlphaKind.None;
}
