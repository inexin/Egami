using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// Blends rows of pixels with a <see cref="BlendMode"/>, for code that composites outside an operation chain: a custom
/// <see cref="IRasterOperation"/>, a rasterizer filling spans, a layer merge. It runs the kernels DrawImage runs, so
/// <see cref="Blend{TPixel}(BlendMode, ReadOnlySpan{TPixel}, Span{TPixel}, float, ColorSpace)"/> on a row gives the pixels
/// DrawImage gives there. Every method works for every pixel type, allocates nothing once warm, and is safe to call from many
/// threads at once.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Blending.cs" region="compositor"/>
/// </example>
public static class Compositor
{
    /// <summary>Blends <paramref name="source"/> onto <paramref name="destination"/>, pixel by pixel, in place.</summary>
    /// <typeparam name="TPixel">The pixel type of both rows.</typeparam>
    /// <param name="mode">How each source pixel combines with the destination pixel under it.</param>
    /// <param name="source">The pixels to blend on top.</param>
    /// <param name="destination">The pixels to change; the same length as <paramref name="source"/>.</param>
    /// <param name="opacity">Multiplies the source's alpha before the mode: 1 uses it as it is.</param>
    /// <param name="colorSpace">Where pixels are blended. Defaults to <see cref="ColorSpace.Encoded"/>; <see cref="ColorSpace.Linear"/> uses the sRGB curve.</param>
    /// <exception cref="ArgumentException">The rows differ in length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0..1, or an enum value is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Blending.cs" region="compositor"/>
    /// </example>
    public static void Blend<TPixel>(BlendMode mode, ReadOnlySpan<TPixel> source, Span<TPixel> destination, float opacity = 1, ColorSpace colorSpace = ColorSpace.Encoded)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Check(mode, opacity, colorSpace);
        CheckLength(source.Length, destination.Length, nameof(source));
        if (mode == BlendMode.Destination || (opacity == 0 && BlendReference.KeepsDestinationAtZeroOpacity(mode)))
        {
            return;
        }

        if (mode == BlendMode.SourceOver && colorSpace == ColorSpace.Encoded)
        {
            SourceOver(source, destination, opacity);
            return;
        }

        new BlendRows<TPixel>(mode, opacity, Curve(colorSpace)).Blend(source, destination);
    }

    /// <summary>
    /// Blends <paramref name="source"/> onto <paramref name="destination"/> through per-pixel coverage, as an antialiased edge
    /// needs: each result is mixed with the original destination pixel by its coverage, so 0 leaves the pixel as it was and 255
    /// gives the full blend.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of both rows.</typeparam>
    /// <param name="mode">How each source pixel combines with the destination pixel under it.</param>
    /// <param name="source">The pixels to blend on top.</param>
    /// <param name="destination">The pixels to change; the same length as <paramref name="source"/>.</param>
    /// <param name="coverage">One coverage value per pixel, 0 to 255; the same length as <paramref name="destination"/>.</param>
    /// <param name="opacity">Multiplies the source's alpha before the mode: 1 uses it as it is.</param>
    /// <param name="colorSpace">Where pixels are blended. Defaults to <see cref="ColorSpace.Encoded"/>.</param>
    /// <exception cref="ArgumentException">The rows or the coverage differ in length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0..1, or an enum value is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Blending.cs" region="compositor"/>
    /// </example>
    public static void Blend<TPixel>(
        BlendMode mode, ReadOnlySpan<TPixel> source, Span<TPixel> destination, ReadOnlySpan<byte> coverage, float opacity = 1, ColorSpace colorSpace = ColorSpace.Encoded)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Check(mode, opacity, colorSpace);
        CheckLength(source.Length, destination.Length, nameof(source));
        CheckLength(coverage.Length, destination.Length, nameof(coverage));
        if (mode == BlendMode.Destination || (opacity == 0 && BlendReference.KeepsDestinationAtZeroOpacity(mode)))
        {
            return;
        }

        new BlendRows<TPixel>(mode, opacity, Curve(colorSpace)).Blend(source, destination, coverage);
    }

    /// <summary>
    /// Blends one color onto every pixel of <paramref name="destination"/> through per-pixel coverage: the span fill a rasterizer
    /// draws a shape with. Coverage 0 leaves a pixel as it was.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the row.</typeparam>
    /// <param name="mode">How the color combines with each pixel.</param>
    /// <param name="color">The color, with straight alpha.</param>
    /// <param name="destination">The pixels to change.</param>
    /// <param name="coverage">One coverage value per pixel, 0 to 255; the same length as <paramref name="destination"/>.</param>
    /// <param name="opacity">Multiplies the color's alpha before the mode: 1 uses it as it is.</param>
    /// <param name="colorSpace">Where pixels are blended. Defaults to <see cref="ColorSpace.Encoded"/>.</param>
    /// <exception cref="ArgumentException">The coverage differs in length from the row.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0..1, or an enum value is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Blending.cs" region="compositor"/>
    /// </example>
    public static void Blend<TPixel>(
        BlendMode mode, Rgba8 color, Span<TPixel> destination, ReadOnlySpan<byte> coverage, float opacity = 1, ColorSpace colorSpace = ColorSpace.Encoded)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Check(mode, opacity, colorSpace);
        CheckLength(coverage.Length, destination.Length, nameof(coverage));
        if (mode == BlendMode.Destination || (opacity == 0 && BlendReference.KeepsDestinationAtZeroOpacity(mode)))
        {
            return;
        }

        new BlendRows<TPixel>(mode, opacity, Curve(colorSpace)).BlendSolid(color, destination, coverage);
    }

    /// <summary>Blends one pixel onto another and returns the result, as DrawImage would for that pair.</summary>
    /// <param name="mode">How the pixels combine.</param>
    /// <param name="source">The pixel on top.</param>
    /// <param name="destination">The pixel under it.</param>
    /// <returns>The blended pixel.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
    public static Rgba8 Blend(BlendMode mode, Rgba8 source, Rgba8 destination)
    {
        Span<Rgba8> d = [destination];
        Blend(mode, [source], d);
        return d[0];
    }

    /// <summary>Blends one float pixel onto another and returns the result, as DrawImage would for that pair.</summary>
    /// <param name="mode">How the pixels combine.</param>
    /// <param name="source">The pixel on top.</param>
    /// <param name="destination">The pixel under it.</param>
    /// <returns>The blended pixel.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
    public static RgbaF32 Blend(BlendMode mode, RgbaF32 source, RgbaF32 destination)
    {
        Span<RgbaF32> d = [destination];
        Blend(mode, [source], d);
        return d[0];
    }

    /// <summary>Source-over on stored values with the 1.x DrawImage kernels, so a row blends exactly as DrawImage blends it.</summary>
    private static void SourceOver<TPixel>(ReadOnlySpan<TPixel> source, Span<TPixel> destination, float opacity)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int opacity255 = (int)MathF.Round(opacity * 255);
        if (typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8))
        {
            ComposeKernels.SourceOver8(MemoryMarshal.Cast<TPixel, uint>(source), MemoryMarshal.Cast<TPixel, uint>(destination), opacity255);
            return;
        }

        if (typeof(TPixel) == typeof(PremulRgba8) || typeof(TPixel) == typeof(PremulBgra8))
        {
            ComposeKernels.PremultipliedOver8(MemoryMarshal.Cast<TPixel, uint>(source), MemoryMarshal.Cast<TPixel, uint>(destination), opacity255);
            return;
        }

        if (typeof(TPixel) == typeof(PremulRgbaF32))
        {
            ComposeKernels.PremultipliedOverFloat(MemoryMarshal.Cast<TPixel, float>(source), MemoryMarshal.Cast<TPixel, float>(destination), opacity);
            return;
        }

        RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(2 * destination.Length);
        try
        {
            Span<RgbaF32> top = buffer.AsSpan(0, destination.Length);
            Span<RgbaF32> under = buffer.AsSpan(destination.Length, destination.Length);
            PixelConvert.Convert(source, top);
            if (CmykInk.Is<TPixel>())
            {
                ComposeKernels.SourceOverInk(top, destination, MemoryMarshal.Cast<RgbaF32, float>(under), opacity);
                return;
            }

            PixelConvert.Convert<TPixel, RgbaF32>(destination, under);
            ComposeKernels.SourceOverFloat(top, under, opacity);
            PixelConvert.Convert<RgbaF32, TPixel>(under, destination);
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(buffer);
        }
    }

    private static Colors.LinearCurve? Curve(ColorSpace colorSpace)
    {
        return colorSpace == ColorSpace.Linear ? Colors.LinearCurve.Srgb : null;
    }

    private static void Check(BlendMode mode, float opacity, ColorSpace colorSpace)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The blend mode is not defined.");
        }

        if (!(opacity >= 0 && opacity <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be from 0 to 1.");
        }

        if (!Enum.IsDefined(colorSpace))
        {
            throw new ArgumentOutOfRangeException(nameof(colorSpace), colorSpace, null);
        }
    }

    private static void CheckLength(int length, int expected, string name)
    {
        if (length != expected)
        {
            throw new ArgumentException($"The span holds {length} values; the destination holds {expected} pixels.", name);
        }
    }
}
