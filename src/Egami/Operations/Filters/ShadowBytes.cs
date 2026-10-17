using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// DropShadow's byte path (Phase 2.5.7) for four-byte straight pixels with alpha: the alpha is read straight from its byte rather
/// than through a float row of all four channels, and the shadow and the image are composed eight pixels at a time, a block that
/// is all opaque or all clear taking the shortcut its pixels allow. The math is the float path's, so the bytes are within a level
/// of it.
/// </summary>
internal static class ShadowBytes
{
    /// <summary>Gets whether <typeparamref name="TPixel"/> takes the byte path: four straight bytes, one of them alpha, not inks.</summary>
    internal static bool Supports<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return StoredRows<TPixel>.Bytes && Unsafe.SizeOf<TPixel>() == 4 && StoredRows<TPixel>.AlphaByte >= 0 && !CmykInk.Is<TPixel>();
    }

    /// <summary>
    /// Writes the image's alpha, moved by (<paramref name="shiftX"/>, <paramref name="shiftY"/>) with bilinear weights, into the
    /// plane, and 0 elsewhere: plane pixel (ix + i, j) is (1 − fy) M(j − iy, i) + fy M(j − iy − 1, i), where M(y, i) is
    /// (1 − fx) a(y, i) + fx a(y, i − 1).
    /// </summary>
    internal static void Fill<TPixel>(PixelPlane<GrayF32> plane, PixelPlane<TPixel> source, float shiftX, float shiftY, int maxParallelism, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int ix = (int)MathF.Floor(shiftX), iy = (int)MathF.Floor(shiftY);
        float fx = shiftX - ix, fy = shiftY - iy;
        int width = source.Width, height = source.Height, at = StoredRows<TPixel>.AlphaByte * 8;
        RowRunner.Run(plane.Height, (long)plane.Width * 4, maxParallelism, (start, end) =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(2 * (width + 1));
            try
            {
                Span<float> upper = buffer.AsSpan(0, width + 1), lower = buffer.AsSpan(width + 1, width + 1);
                int upperRow = int.MinValue, lowerRow = int.MinValue;
                for (int j = start; j < end; j++)
                {
                    if ((j & 63) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // Plane row j takes image row j − iy with weight 1 − fy and row j − iy − 1 with weight fy; a moved row is
                    // reused as the next plane row's upper one.
                    Span<float> row = MemoryMarshal.Cast<GrayF32, float>(plane.Row(j));
                    int y0 = j - iy;
                    bool hasUpper = y0 - 1 >= 0 && y0 - 1 < height, hasLower = y0 >= 0 && y0 < height;
                    if (!hasUpper && !hasLower)
                    {
                        row.Clear();
                        continue;
                    }

                    if (hasUpper && upperRow != y0 - 1)
                    {
                        if (lowerRow == y0 - 1)
                        {
                            Span<float> swap = upper;
                            upper = lower;
                            lower = swap;
                            (upperRow, lowerRow) = (lowerRow, upperRow);
                        }
                        else
                        {
                            Moved(source, y0 - 1, fx, at, upper);
                            upperRow = y0 - 1;
                        }
                    }

                    if (hasLower && lowerRow != y0)
                    {
                        Moved(source, y0, fx, at, lower);
                        lowerRow = y0;
                    }

                    row[..ix].Clear();
                    row[(ix + width + 1)..].Clear();
                    Mix(hasLower ? lower : default, hasUpper ? upper : default, 1 - fy, fy, row.Slice(ix, width + 1));
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }, cancellationToken);
    }

    /// <summary>Writes <paramref name="a"/> × <paramref name="lower"/> + <paramref name="b"/> × <paramref name="upper"/>, either row empty for zeros.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Mix(ReadOnlySpan<float> lower, ReadOnlySpan<float> upper, float a, float b, Span<float> target)
    {
        int i = 0;
        if (!lower.IsEmpty && !upper.IsEmpty)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<float> va = Vector256.Create(a), vb = Vector256.Create(b);
                for (; i <= target.Length - Vector256<float>.Count; i += Vector256<float>.Count)
                {
                    ((va * Vector256.Create(lower[i..])) + (vb * Vector256.Create(upper[i..]))).CopyTo(target[i..]);
                }
            }

            for (; i < target.Length; i++)
            {
                target[i] = (a * lower[i]) + (b * upper[i]);
            }

            return;
        }

        ReadOnlySpan<float> one = lower.IsEmpty ? upper : lower;
        float w = lower.IsEmpty ? b : a;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> vw = Vector256.Create(w);
            for (; i <= target.Length - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                ((vw * Vector256.Create(one[i..])) + Vector256<float>.Zero).CopyTo(target[i..]);
            }
        }

        for (; i < target.Length; i++)
        {
            target[i] = (w * one[i]) + 0f;
        }
    }

    /// <summary>Loads image row <paramref name="y"/>'s alpha (at bit <paramref name="at"/>) moved right by the fraction <paramref name="fx"/>: width + 1 values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Moved<TPixel>(PixelPlane<TPixel> source, int y, float fx, int at, Span<float> moved)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<TPixel, uint>(source.Row(y));
        int width = pixels.Length, i = 0;
        float keep = 1 - fx;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> low = Vector256.Create(0xFFu);
            Vector256<float> unit = Vector256.Create(1f / 255), vk = Vector256.Create(keep), vf = Vector256.Create(fx);
            float previous = 0;
            for (; i <= width - Vector256<uint>.Count; i += Vector256<uint>.Count)
            {
                Vector256<float> a = Vector256.ConvertToSingle(((Vector256.Create(pixels[i..]) >>> at) & low).AsInt32()) * unit;

                // Each value's left neighbour: the vector moved up a lane, the last one of the vector before coming in.
                Vector256<float> before = Vector256.Create(previous, a[0], a[1], a[2], a[3], a[4], a[5], a[6]);
                ((vk * a) + (vf * before)).CopyTo(moved[i..]);
                previous = a[7];
            }
        }

        float last = i > 0 ? ((pixels[i - 1] >>> at) & 0xFF) * (1f / 255) : 0;
        for (; i < width; i++)
        {
            float a = ((pixels[i] >>> at) & 0xFF) * (1f / 255);
            moved[i] = (keep * a) + (fx * last);
            last = a;
        }

        moved[width] = fx * last;
    }

    /// <summary>
    /// Fills each row with the shadow, <paramref name="color"/> at the shadow's alpha times the color's, and unless
    /// <paramref name="shadowOnly"/> draws the image over it source-over, as the float path does on premultiplied values.
    /// </summary>
    internal static void Compose<TPixel>(PixelPlane<GrayF32> plane, PixelPlane<TPixel> source, PixelPlane<TPixel> destination, Rgba8 color, bool shadowOnly, int extX, int extY, int left, int top, int maxParallelism, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = destination.Width, imageWidth = source.Width, alphaAt = StoredRows<TPixel>.AlphaByte;

        // The color's channels in the pixel type's byte order, straight, and the color's alpha as a fraction.
        Span<byte> order = stackalloc byte[4];
        PixelConvert.Convert<Rgba8, TPixel>([color with { A = 255 }], MemoryMarshal.Cast<byte, TPixel>(order));
        uint rgb = MemoryMarshal.Read<uint>(order) & ~(0xFFu << (alphaAt * 8));
        float[] straight = [order[0] / 255f, order[1] / 255f, order[2] / 255f, order[3] / 255f];
        float ca = color.A / 255f;
        RowRunner.Run(destination.Height, (long)width * 4, maxParallelism, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                if ((y & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ReadOnlySpan<float> shadow = MemoryMarshal.Cast<GrayF32, float>(plane.Row(y + extY)).Slice(extX, width);
                Span<uint> row = MemoryMarshal.Cast<TPixel, uint>(destination.Row(y));
                int sy = y - top;
                if (shadowOnly || sy < 0 || sy >= source.Height)
                {
                    Shadow(shadow, row, rgb, ca, alphaAt);
                    continue;
                }

                Shadow(shadow[..left], row[..left], rgb, ca, alphaAt);
                Shadow(shadow[(left + imageWidth)..], row[(left + imageWidth)..], rgb, ca, alphaAt);
                Over(MemoryMarshal.Cast<TPixel, uint>(source.Row(sy)), shadow.Slice(left, imageWidth), row.Slice(left, imageWidth), rgb, straight, ca, alphaAt);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Writes the shadow alone: the color's channels, alpha the shadow's times the color's. As the float path stores it, a pixel
    /// whose alpha is exactly 0 gets no color.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Shadow(ReadOnlySpan<float> shadow, Span<uint> row, uint rgb, float ca, int alphaAt)
    {
        int i = 0, shift = alphaAt * 8;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> vca = Vector256.Create(ca), zero = Vector256<float>.Zero, one = Vector256<float>.One;
            Vector256<float> scale = Vector256.Create(255f), half = Vector256.Create(0.5f);
            Vector256<uint> color = Vector256.Create(rgb);
            for (; i <= row.Length - Vector256<uint>.Count; i += Vector256<uint>.Count)
            {
                Vector256<float> a = Vector256.Min(Vector256.Max(Vector256.Create(shadow[i..]), zero), one) * vca;
                Vector256<uint> bytes = Vector256.ConvertToInt32((Vector256.Min(a, one) * scale) + half).AsUInt32() << shift;
                Vector256<uint> has = Vector256.GreaterThan(a, zero).AsUInt32();
                ((color & has) | bytes).CopyTo(row[i..]);
            }
        }

        for (; i < row.Length; i++)
        {
            float a = Math.Clamp(shadow[i], 0, 1) * ca;
            uint bytes = (uint)(int)((Math.Min(a, 1) * 255f) + 0.5f) << shift;
            row[i] = (a > 0 ? rgb : 0) | bytes;
        }
    }

    /// <summary>Draws the image's pixels over the shadow, eight at a time where they are all opaque or all clear.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Over(ReadOnlySpan<uint> image, ReadOnlySpan<float> shadow, Span<uint> row, uint rgb, float[] straight, float ca, int alphaAt)
    {
        int i = 0, shift = alphaAt * 8;
        uint mask = 0xFFu << shift;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> m = Vector256.Create(mask);
            for (; i <= row.Length - Vector256<uint>.Count; i += Vector256<uint>.Count)
            {
                Vector256<uint> pixels = Vector256.Create(image[i..]);
                Vector256<uint> alpha = pixels & m;
                if (alpha == m)
                {
                    pixels.CopyTo(row[i..]);
                }
                else if (alpha == Vector256<uint>.Zero)
                {
                    Shadow(shadow.Slice(i, Vector256<uint>.Count), row.Slice(i, Vector256<uint>.Count), rgb, ca, alphaAt);
                }
                else
                {
                    Blend(pixels, Vector256.Create(shadow[i..]), straight, ca, shift).CopyTo(row[i..]);
                }
            }
        }

        Blend(image[i..], shadow[i..], row[i..], straight, ca, alphaAt);
    }

    /// <summary><see cref="Blend(ReadOnlySpan{uint}, ReadOnlySpan{float}, Span{uint}, float[], float, int)"/> on eight pixels, with the same arithmetic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Blend(Vector256<uint> pixels, Vector256<float> shadow, float[] straight, float ca, int shift)
    {
        Vector256<uint> low = Vector256.Create(0xFFu);
        Vector256<float> unit = Vector256.Create(1f / 255), zero = Vector256<float>.Zero, one = Vector256<float>.One;
        Vector256<float> scale = Vector256.Create(255f), half = Vector256.Create(0.5f), vca = Vector256.Create(ca);
        Vector256<float> sa = Vector256.ConvertToSingle(((pixels >>> shift) & low).AsInt32()) * unit;
        Vector256<float> s = Vector256.Min(Vector256.Max(shadow, zero), one), rest = one - sa;
        Vector256<float> a = sa + ((s * vca) * rest);
        Vector256<float> positive = Vector256.GreaterThan(a, zero);
        Vector256<uint> result = Vector256<uint>.Zero;
        for (int k = 0; k < 4; k++)
        {
            int bits = k * 8;
            Vector256<float> value;
            if (bits == shift)
            {
                value = a;
            }
            else
            {
                Vector256<float> c = Vector256.ConvertToSingle(((pixels >>> bits) & low).AsInt32());
                Vector256<float> premultiplied = ((c * unit) * sa) + (((Vector256.Create(straight[k]) * vca) * s) * rest);
                value = Vector256.ConditionalSelect(positive, premultiplied / a, zero);
            }

            result |= Vector256.ConvertToInt32((Vector256.Min(Vector256.Max(value, zero), one) * scale) + half).AsUInt32() << bits;
        }

        return result;
    }

    /// <summary>
    /// Source-over on premultiplied values, as the float path computes it: the image (its colors times its alpha) plus the shadow
    /// (the color times the shadow's alpha times the color's) times one minus the image's alpha, then divided back to straight.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Blend(ReadOnlySpan<uint> image, ReadOnlySpan<float> shadow, Span<uint> row, float[] straight, float ca, int alphaAt)
    {
        int shift = alphaAt * 8;
        for (int i = 0; i < row.Length; i++)
        {
            uint pixel = image[i];
            float sa = ((pixel >> shift) & 0xFF) * (1f / 255);
            float clamped = Math.Clamp(shadow[i], 0, 1), rest = 1 - sa;
            float a = sa + ((clamped * ca) * rest);
            uint result = 0;
            for (int k = 0; k < 4; k++)
            {
                int bits = k * 8;
                float value;
                if (bits == shift)
                {
                    value = a;
                }
                else
                {
                    float premultiplied = (((pixel >> bits) & 0xFF) * (1f / 255) * sa) + (straight[k] * ca * clamped * rest);
                    value = a > 0f ? premultiplied / a : 0f;
                }

                result |= (uint)(int)((Math.Clamp(value, 0f, 1f) * 255f) + 0.5f) << bits;
            }

            row[i] = result;
        }
    }
}
