using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Compose;

/// <summary>
/// Draws another raster's first frame onto every frame with Porter–Duff source-over, in place. Only the overlap of the two is
/// touched. With straight alpha, <c>sa</c> = source alpha × opacity and <c>da</c> = destination alpha:
/// <c>out alpha = sa + da(1 − sa)</c> and <c>out color = (sc·sa + dc·da(1 − sa)) / out alpha</c>.
/// </summary>
internal sealed class DrawImageOperation(Raster image, Point position, float opacity) : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        Rect area = new Rect(position.X, position.Y, image.Width, image.Height).Intersect(new Rect(0, 0, input.Destination.Width, input.Destination.Height));
        if (area.IsEmpty || opacity == 0)
        {
            return;
        }

        // Drawing a raster onto itself would read rows this step has already changed, so it reads from a snapshot.
        Raster source = image.HasPlane(input.Source) || image.HasPlane(input.Destination) ? image.Derive(_ => { }) : image;
        try
        {
            if (typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8))
            {
                RunBytes(input, source, area);
            }
            else
            {
                RunFloat(input, source, area);
            }
        }
        finally
        {
            if (!ReferenceEquals(source, image))
            {
                source.Dispose();
            }
        }
    }

    private void RunBytes<TPixel>(OperationInput<TPixel> input, Raster source, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int opacity255 = (int)MathF.Round(opacity * 255);
        int sx = area.X - position.X;
        int sy = area.Y - position.Y;
        input.ForEachBand((start, end) =>
        {
            int from = Math.Max(start, area.Y);
            int to = Math.Min(end, area.Bottom);
            if (from >= to)
            {
                return;
            }

            TPixel[] buffer = ArrayPool<TPixel>.Shared.Rent(area.Width);
            try
            {
                Span<TPixel> row = buffer.AsSpan(0, area.Width);
                for (int y = from; y < to; y++)
                {
                    source.ReadRow(sy + y - area.Y, sx, row);
                    Span<TPixel> destination = input.Destination.Row(y).Slice(area.X, area.Width);
                    ComposeKernels.SourceOver8(MemoryMarshal.Cast<TPixel, uint>(row), MemoryMarshal.Cast<TPixel, uint>(destination), opacity255);
                }
            }
            finally
            {
                ArrayPool<TPixel>.Shared.Return(buffer);
            }
        });
    }

    private void RunFloat<TPixel>(OperationInput<TPixel> input, Raster source, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int sx = area.X - position.X;
        int sy = area.Y - position.Y;
        input.ForEachBand((start, end) =>
        {
            int from = Math.Max(start, area.Y);
            int to = Math.Min(end, area.Bottom);
            if (from >= to)
            {
                return;
            }

            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(2 * area.Width);
            try
            {
                Span<RgbaF32> top = buffer.AsSpan(0, area.Width);
                Span<RgbaF32> under = buffer.AsSpan(area.Width, area.Width);
                for (int y = from; y < to; y++)
                {
                    source.ReadRow(sy + y - area.Y, sx, top);
                    Span<TPixel> destination = input.Destination.Row(y).Slice(area.X, area.Width);
                    PixelConvert.Convert<TPixel, RgbaF32>(destination, under);
                    ComposeKernels.SourceOverFloat(top, under, opacity);
                    PixelConvert.Convert<RgbaF32, TPixel>(under, destination);
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }
}

/// <summary>Source-over blending of rows.</summary>
internal static class ComposeKernels
{
    /// <summary>
    /// Blends 4-byte pixels with alpha last (Rgba8 or Bgra8, both rows in the same order) in exact integer math: every result is
    /// the true value rounded to nearest. <paramref name="opacity255"/> scales the source alpha, 255 being fully opaque.
    /// </summary>
    internal static void SourceOver8(ReadOnlySpan<uint> source, Span<uint> destination, int opacity255)
    {
        for (int i = 0; i < source.Length; i++)
        {
            uint s = source[i];
            int sa = (int)(s >> 24);
            if (opacity255 != 255)
            {
                sa = Div255(sa * opacity255);
            }

            if (sa == 0)
            {
                continue;
            }

            if (sa == 255)
            {
                destination[i] = s | 0xFF000000u;
                continue;
            }

            destination[i] = Blend(s, destination[i], sa);
        }
    }

    /// <summary>Blends one pixel with source alpha <paramref name="sa"/> strictly between 0 and 255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Blend(uint s, uint d, int sa)
    {
        int da = (int)(d >> 24);
        if (da == 255)
        {
            // Opaque under: out alpha is 1 and the blend needs no division.
            int inv = 255 - sa;
            return 0xFF000000u
                | (uint)Div255(((int)(s & 0xFF) * sa) + ((int)(d & 0xFF) * inv))
                | ((uint)Div255(((int)((s >> 8) & 0xFF) * sa) + ((int)((d >> 8) & 0xFF) * inv)) << 8)
                | ((uint)Div255(((int)((s >> 16) & 0xFF) * sa) + ((int)((d >> 16) & 0xFF) * inv)) << 16);
        }

        // In units of 1/255²: the source weighs sa × 255, the destination da × (255 − sa).
        int ws = sa * 255;
        int wd = da * (255 - sa);
        int total = ws + wd;
        uint outA = (uint)((total + 127) / 255);
        if (outA == 0)
        {
            return 0;
        }

        int half = total / 2;
        uint r = (uint)(((((int)(s & 0xFF)) * ws) + (((int)(d & 0xFF)) * wd) + half) / total);
        uint g = (uint)(((((int)((s >> 8) & 0xFF)) * ws) + (((int)((d >> 8) & 0xFF)) * wd) + half) / total);
        uint b = (uint)(((((int)((s >> 16) & 0xFF)) * ws) + (((int)((d >> 16) & 0xFF)) * wd) + half) / total);
        return (outA << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>Blends float pixels in RGBA order onto <paramref name="destination"/>.</summary>
    internal static void SourceOverFloat(ReadOnlySpan<RgbaF32> source, Span<RgbaF32> destination, float opacity)
    {
        for (int i = 0; i < source.Length; i++)
        {
            RgbaF32 s = source[i];
            float sa = Math.Clamp(s.A, 0f, 1f) * opacity;
            if (sa <= 0f)
            {
                continue;
            }

            RgbaF32 d = destination[i];
            float wd = Math.Clamp(d.A, 0f, 1f) * (1f - sa);
            float outA = sa + wd;
            destination[i] = outA <= 0f
                ? default
                : new RgbaF32(((s.R * sa) + (d.R * wd)) / outA, ((s.G * sa) + (d.G * wd)) / outA, ((s.B * sa) + (d.B * wd)) / outA, outA);
        }
    }

    /// <summary>Divides by 255, rounding to nearest, for 0 ≤ <paramref name="x"/> ≤ 65025 + 127.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Div255(int x)
    {
        return ((x + 127) * 0x8081) >> 23;
    }
}
