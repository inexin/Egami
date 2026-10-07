using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>Converts spans of pixels from one pixel type to another.</summary>
public static class PixelConvert
{
    /// <summary>
    /// Converts every pixel in <paramref name="source"/> and writes the results to the start of <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Conversions between integer types where either side has 8-bit channels go through <see cref="Rgba8"/>, so they are exact:
    /// 8 to 16 bits multiplies by 257, and 16 to 8 bits rounds to the nearest value. All other conversions go through
    /// red, green, blue and alpha scaled to 0..1.
    /// </para>
    /// <para>
    /// Converting to a gray type takes the Rec. 709 luma of the color, weighted in linear light; gray colors keep their value.
    /// Converting to a type without alpha drops alpha, and converting from one gives every pixel full alpha.
    /// </para>
    /// <para>
    /// Common pairs use SIMD: channel reordering between the 8-bit RGB layouts, 8 to 16 bits and back, and gray to RGB.
    /// Results are identical with and without SIMD.
    /// </para>
    /// <para>The spans must not overlap unless both are the same type and start at the same address.</para>
    /// </remarks>
    /// <typeparam name="TFrom">The source pixel type.</typeparam>
    /// <typeparam name="TTo">The destination pixel type.</typeparam>
    /// <param name="source">The pixels to convert.</param>
    /// <param name="destination">Where to write the converted pixels. It must be at least as long as <paramref name="source"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="source"/>.</exception>
    public static void Convert<TFrom, TTo>(ReadOnlySpan<TFrom> source, Span<TTo> destination)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        if (typeof(TFrom) == typeof(TTo))
        {
            MemoryMarshal.Cast<TFrom, TTo>(source).CopyTo(destination);
            return;
        }

        int done = PixelKernels.Convert(source, destination, PixelKernels.HardwareBits);
        ConvertScalar(source[done..], destination[done..]);
    }

    /// <summary>Converts with the scalar path only. SIMD kernels must match its output exactly.</summary>
    internal static void ConvertScalar<TFrom, TTo>(ReadOnlySpan<TFrom> source, Span<TTo> destination)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        if (ConvertsThroughRgba8(TFrom.Layout.ChannelType, TTo.Layout.ChannelType))
        {
            ConvertThroughRgba8(source, destination);
        }
        else
        {
            ConvertThroughScaled(source, destination);
        }
    }

    private static bool ConvertsThroughRgba8(PixelChannelType from, PixelChannelType to)
    {
        return from != PixelChannelType.Float32
            && to != PixelChannelType.Float32
            && (from == PixelChannelType.Unorm8 || to == PixelChannelType.Unorm8);
    }

    private static void ConvertThroughRgba8<TFrom, TTo>(ReadOnlySpan<TFrom> source, Span<TTo> destination)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = TTo.FromRgba8(source[i].ToRgba8());
        }
    }

    private static void ConvertThroughScaled<TFrom, TTo>(ReadOnlySpan<TFrom> source, Span<TTo> destination)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = TTo.FromScaled(source[i].ToScaled());
        }
    }
}
