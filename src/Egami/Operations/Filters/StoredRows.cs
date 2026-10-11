using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Moves rows between a pixel type and straight floats, four per pixel, for the filters that pick a neighbour's value (median,
/// morphology, despeckle, oil paint): no premultiplying and no curve, so 8 and 16-bit values come back exactly. CMYK types load
/// their four inks, as the other filters do.
/// </summary>
internal static class StoredRows<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private static readonly bool Ink = CmykInk.Is<TPixel>();

    /// <summary>Gets whether the type's channels are all whole bytes, stored straight, so a filter can work on the bytes themselves.</summary>
    internal static readonly bool Bytes = IsBytes();

    /// <summary>Gets the index of the alpha byte in a pixel when <see cref="Bytes"/>, or −1 when there is none.</summary>
    internal static readonly int AlphaByte = TPixel.Layout.ChannelOrder switch
    {
        PixelChannelOrder.Rgba or PixelChannelOrder.Bgra => 3,
        PixelChannelOrder.Argb or PixelChannelOrder.Abgr or PixelChannelOrder.Alpha => 0,
        PixelChannelOrder.GrayAlpha => 1,
        _ => -1,
    };

    /// <summary>Converts a row to straight floats.</summary>
    internal static void Load(ReadOnlySpan<TPixel> source, Span<float> destination)
    {
        if (Ink)
        {
            CmykInk.Load(source, destination);
        }
        else
        {
            PixelConvert.Convert(source, MemoryMarshal.Cast<float, RgbaF32>(destination)[..source.Length]);
        }
    }

    /// <summary>Converts straight floats back to a row.</summary>
    internal static void Store(ReadOnlySpan<float> source, Span<TPixel> destination)
    {
        if (Ink)
        {
            CmykInk.Store(source, destination);
        }
        else
        {
            PixelConvert.Convert<RgbaF32, TPixel>(MemoryMarshal.Cast<float, RgbaF32>(source)[..destination.Length], destination);
        }
    }

    private static bool IsBytes()
    {
        PixelLayoutInfo layout = TPixel.Layout;
        if (layout.ChannelType != PixelChannelType.Unorm8 || layout.Alpha == AlphaKind.Premultiplied || layout.ChannelOrder == PixelChannelOrder.Other)
        {
            return false;
        }

        foreach (int bits in layout.ChannelBits)
        {
            if (bits != 8)
            {
                return false;
            }
        }

        return System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>() == layout.ChannelCount;
    }
}
