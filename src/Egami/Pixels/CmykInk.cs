using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// The ink channels of CMYK pixel types, for operations that filter the four inks as numbers instead of going through RGB.
/// Any type with <see cref="PixelChannelOrder.Cmyk"/>, 8 or 16-bit channels, no alpha and the matching size takes this path.
/// </summary>
internal static class CmykInk
{
    /// <summary>Returns whether <typeparamref name="TPixel"/> stores four ink channels Egami can read directly.</summary>
    internal static bool Is<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelLayoutInfo layout = TPixel.Layout;
        return layout.ChannelOrder == PixelChannelOrder.Cmyk && layout.Alpha == AlphaKind.None
            && ((layout.ChannelType == PixelChannelType.Unorm8 && System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>() == 4)
                || (layout.ChannelType == PixelChannelType.Unorm16 && System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>() == 8));
    }

    /// <summary>Writes each pixel's inks, scaled to 0..1, as four floats.</summary>
    internal static void Load<TPixel>(ReadOnlySpan<TPixel> source, Span<float> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (TPixel.Layout.ChannelType == PixelChannelType.Unorm8)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
            for (int i = 0; i < bytes.Length; i++)
            {
                destination[i] = bytes[i] * (1f / 255f);
            }
        }
        else
        {
            ReadOnlySpan<ushort> words = MemoryMarshal.Cast<TPixel, ushort>(source);
            for (int i = 0; i < words.Length; i++)
            {
                destination[i] = words[i] * (1f / 65535f);
            }
        }
    }

    /// <summary>Stores four floats per pixel as inks, clamped to 0..1 and rounded.</summary>
    internal static void Store<TPixel>(ReadOnlySpan<float> source, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (TPixel.Layout.ChannelType == PixelChannelType.Unorm8)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(destination);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Scale.ToByte(source[i]);
            }
        }
        else
        {
            Span<ushort> words = MemoryMarshal.Cast<TPixel, ushort>(destination);
            for (int i = 0; i < words.Length; i++)
            {
                words[i] = Scale.ToUInt16(source[i]);
            }
        }
    }

    /// <summary>Converts an RGB color, scaled to 0..1, to inks with the plain formula: K = 1 − max(R, G, B), C = (1 − R − K) / (1 − K).</summary>
    internal static Vector4 FromRgb(Vector4 rgba)
    {
        float r = Scale.Clamp(rgba.X), g = Scale.Clamp(rgba.Y), b = Scale.Clamp(rgba.Z);
        float max = MathF.Max(r, MathF.Max(g, b));
        if (max <= 0f)
        {
            return new Vector4(0f, 0f, 0f, 1f);
        }

        return new Vector4((max - r) / max, (max - g) / max, (max - b) / max, 1f - max);
    }
}
