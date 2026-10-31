using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Compose;

/// <summary>
/// Draws an area of another raster's first frame onto every frame with a <see cref="BlendMode"/>, in place. Only the overlap of
/// the area, placed at the position, and the destination is touched; the source rows are read from an offset, so no copy is made.
/// Source-over on stored values keeps the 1.x kernels: with straight alpha, <c>sa</c> = source alpha × opacity and <c>da</c> =
/// destination alpha, <c>out alpha = sa + da(1 − sa)</c> and <c>out color = (sc·sa + dc·da(1 − sa)) / out alpha</c>. Every other
/// mode, and linear light, runs through <see cref="BlendRows{TPixel}"/>.
/// </summary>
internal sealed class DrawImageOperation(
    Raster image, Rect sourceArea, Point position, float opacity, BlendMode mode = BlendMode.SourceOver, bool linear = false, Colors.LinearCurve? curve = null)
    : IRasterOperation, ILinearLight, Scopes.IAreaTranslatable
{
    /// <summary>Initializes a new instance of the <see cref="DrawImageOperation"/> class that draws the whole image.</summary>
    public DrawImageOperation(Raster image, Point position, float opacity, BlendMode mode = BlendMode.SourceOver, bool linear = false)
        : this(image, new Rect(0, 0, image.Width, image.Height), position, opacity, mode, linear)
    {
    }

    /// <summary>Gets the mode.</summary>
    internal BlendMode Mode => mode;

    public bool UsesLinearLight => linear;

    public IRasterOperation WithCurve(Colors.LinearCurve curve) => new DrawImageOperation(image, sourceArea, position, opacity, mode, linear, curve);

    public IRasterOperation? InArea(Rect area) =>
        new DrawImageOperation(image, sourceArea, new Point(position.X - area.X, position.Y - area.Y), opacity, mode, linear, curve);

    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        Rect area = new Rect(position.X, position.Y, sourceArea.Width, sourceArea.Height).Intersect(new Rect(0, 0, input.Destination.Width, input.Destination.Height));
        if (area.IsEmpty || (opacity == 0 && BlendReference.KeepsDestinationAtZeroOpacity(mode)) || mode == BlendMode.Destination)
        {
            return;
        }

        // Drawing a raster onto itself would read rows this step has already changed, so it reads from a snapshot.
        Raster source = image.HasPlane(input.Source) || image.HasPlane(input.Destination) ? image.Derive(_ => { }) : image;
        try
        {
            if (mode != BlendMode.SourceOver || linear)
            {
                RunBlend(input, source, area);
            }
            else if (typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8))
            {
                RunBytes(input, source, area);
            }
            else if (RowConverter<TPixel>.Premul8 || RowConverter<TPixel>.PremulFloat)
            {
                RunPremultiplied(input, source, area);
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

    /// <summary>Any mode, or linear light, blended by <see cref="BlendRows{TPixel}"/>, which says what type to read the source rows as.</summary>
    private void RunBlend<TPixel>(OperationInput<TPixel> input, Raster source, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        BlendRows<TPixel> rows = new(mode, opacity, linear ? curve ?? Colors.LinearCurve.Srgb : null);
        int sx = sourceArea.X + area.X - position.X;
        int sy = sourceArea.Y + area.Y - position.Y;
        input.ForEachBand((start, end) =>
        {
            int from = Math.Max(start, area.Y);
            int to = Math.Min(end, area.Bottom);
            if (from >= to)
            {
                return;
            }

            if (rows.UsesBytes && source is Raster<TPixel> typed)
            {
                // The same 8-bit type: the source's own rows go to the kernel as they are, with no copy.
                for (int y = from; y < to; y++)
                {
                    rows.Blend(typed.Row(sy + y - area.Y).Slice(sx, area.Width), input.Destination.Row(y).Slice(area.X, area.Width), default, area.X, y);
                }

                return;
            }

            if (rows.UsesBytes)
            {
                TPixel[] buffer = ArrayPool<TPixel>.Shared.Rent(area.Width);
                try
                {
                    Span<TPixel> row = buffer.AsSpan(0, area.Width);
                    for (int y = from; y < to; y++)
                    {
                        source.ReadRow(sy + y - area.Y, sx, row);
                        rows.Blend(row, input.Destination.Row(y).Slice(area.X, area.Width), default, area.X, y);
                    }
                }
                finally
                {
                    ArrayPool<TPixel>.Shared.Return(buffer);
                }

                return;
            }

            RgbaF32[] straight = ArrayPool<RgbaF32>.Shared.Rent(area.Width);
            try
            {
                Span<RgbaF32> row = straight.AsSpan(0, area.Width);
                for (int y = from; y < to; y++)
                {
                    source.ReadRow(sy + y - area.Y, sx, row);
                    rows.BlendStraight(row, input.Destination.Row(y).Slice(area.X, area.Width), default, area.X, y);
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(straight);
            }
        });
    }

    private void RunBytes<TPixel>(OperationInput<TPixel> input, Raster source, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int opacity255 = (int)MathF.Round(opacity * 255);
        int sx = sourceArea.X + area.X - position.X;
        int sy = sourceArea.Y + area.Y - position.Y;
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

    /// <summary>Premultiplied source-over, dst = src + dst × (1 − src.a): the source is read as the destination's own type.</summary>
    private void RunPremultiplied<TPixel>(OperationInput<TPixel> input, Raster source, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int opacity255 = (int)MathF.Round(opacity * 255);
        int sx = sourceArea.X + area.X - position.X;
        int sy = sourceArea.Y + area.Y - position.Y;
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
                    if (RowConverter<TPixel>.Premul8)
                    {
                        ComposeKernels.PremultipliedOver8(MemoryMarshal.Cast<TPixel, uint>(row), MemoryMarshal.Cast<TPixel, uint>(destination), opacity255);
                    }
                    else
                    {
                        ComposeKernels.PremultipliedOverFloat(MemoryMarshal.Cast<TPixel, float>(row), MemoryMarshal.Cast<TPixel, float>(destination), opacity);
                    }
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
        int sx = sourceArea.X + area.X - position.X;
        int sy = sourceArea.Y + area.Y - position.Y;
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
                    if (CmykInk.Is<TPixel>())
                    {
                        ComposeKernels.SourceOverInk(top, destination, MemoryMarshal.Cast<RgbaF32, float>(under), opacity);
                        continue;
                    }

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
    /// Premultiplied source-over on 8-bit pixels with alpha last, in integers: the source is scaled by
    /// <paramref name="opacity255"/>, then dst = src + round(dst × (255 − src.a) / 255) for every channel.
    /// </summary>
    internal static void PremultipliedOver8(ReadOnlySpan<uint> source, Span<uint> destination, int opacity255)
    {
        for (int i = 0; i < source.Length; i++)
        {
            uint s = source[i];
            if (opacity255 != 255)
            {
                s = Scale8(s, (uint)opacity255);
            }

            uint sa = s >> 24;
            if (sa == 255)
            {
                destination[i] = s;
                continue;
            }

            if (s == 0)
            {
                continue;
            }

            uint d = destination[i];
            uint keep = 255 - sa;
            uint result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                uint dc = (d >> shift) & 0xFF;
                uint sc = (s >> shift) & 0xFF;
                uint mixed = sc + ((((dc * keep) + 127) * 0x8081) >> 23);
                result |= Math.Min(mixed, 255u) << shift;
            }

            destination[i] = result;
        }

        static uint Scale8(uint p, uint factor)
        {
            uint result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                result |= (((((p >> shift) & 0xFF) * factor) + 127) * 0x8081 >> 23) << shift;
            }

            return result;
        }
    }

    /// <summary>Premultiplied source-over on float pixels: dst = src × o + dst × (1 − src.a × o), with o the opacity.</summary>
    internal static void PremultipliedOverFloat(ReadOnlySpan<float> source, Span<float> destination, float opacity)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            float keep = 1f - (source[i + 3] * opacity);
            destination[i] = (source[i] * opacity) + (destination[i] * keep);
            destination[i + 1] = (source[i + 1] * opacity) + (destination[i + 1] * keep);
            destination[i + 2] = (source[i + 2] * opacity) + (destination[i + 2] * keep);
            destination[i + 3] = (source[i + 3] * opacity) + (destination[i + 3] * keep);
        }
    }

    /// <summary>
    /// Blends RGBA source pixels onto CMYK pixels: each source color becomes inks with the plain formula, then every ink mixes as
    /// ink = source × a + destination × (1 − a), with a the source alpha times <paramref name="opacity"/>.
    /// </summary>
    internal static void SourceOverInk<TPixel>(ReadOnlySpan<RgbaF32> source, Span<TPixel> destination, Span<float> scratch, float opacity)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Span<float> inks = scratch[..(destination.Length * 4)];
        CmykInk.Load<TPixel>(destination, inks);
        for (int i = 0; i < destination.Length; i++)
        {
            RgbaF32 s = source[i];
            float a = Math.Clamp(s.A * opacity, 0f, 1f);
            System.Numerics.Vector4 ink = CmykInk.FromRgb(s.ToScaled());
            int o = i * 4;
            inks[o] += (ink.X - inks[o]) * a;
            inks[o + 1] += (ink.Y - inks[o + 1]) * a;
            inks[o + 2] += (ink.Z - inks[o + 2]) * a;
            inks[o + 3] += (ink.W - inks[o + 3]) * a;
        }

        CmykInk.Store<TPixel>(inks, destination);
    }

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
