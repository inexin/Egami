using System.Buffers;
using System.Runtime.CompilerServices;

using Egami.Operations.Geometry;
using Egami.Operations.Scopes;
using Egami.Pixels;

namespace Egami.Operations.Compose;

/// <summary>
/// ImageMagick's -fuzz color distance, pinned against ImageMagick 7.1.2 Q16 (Build Plan task 1): on straight samples in 0..1, with
/// αp and αq the two alphas, d² = 3(αp − αq)² + αp·αq·((Rp − Rq)² + (Gp − Gq)² + (Bp − Bq)²), and two colors match when
/// d² ≤ 3·max(t, √½ / 65535)² for a tolerance t.
/// </summary>
internal readonly struct FuzzColor
{
    private readonly float _r, _g, _b, _a;

    internal FuzzColor(RgbaF32 color)
    {
        (_r, _g, _b, _a) = (color.R, color.G, color.B, color.A);
    }

    /// <summary>Gets the threshold d² is compared with for <paramref name="tolerance"/>.</summary>
    internal static double Threshold(float tolerance)
    {
        double fuzz = Math.Max(tolerance, Math.Sqrt(0.5) / 65535);
        return 3 * fuzz * fuzz;
    }

    /// <summary>Returns d² between this color and <paramref name="p"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double Distance2(RgbaF32 p)
    {
        double da = p.A - (double)_a;
        double dr = p.R - (double)_r, dg = p.G - (double)_g, db = p.B - (double)_b;
        return (3 * da * da) + ((double)p.A * _a * ((dr * dr) + (dg * dg) + (db * db)));
    }
}

/// <summary>How flood fill moves from pixel to pixel.</summary>
internal static class FloodFill
{
    /// <summary>
    /// Marks every pixel connected to <paramref name="seed"/> whose color matches the seed's, a scanline fill on one thread. The
    /// visited set is one bit a pixel, from <paramref name="memory"/>, so it counts against MaxTotalBytes.
    /// </summary>
    /// <returns>The marked pixels, one bit each, row by row; the caller disposes it.</returns>
    internal static IMemoryOwner<byte> Region<TPixel>(PixelPlane<TPixel> plane, Point seed, float tolerance, Connectivity connectivity, IPixelMemorySource memory)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = plane.Width, height = plane.Height;
        long bits = (long)width * height;
        IMemoryOwner<byte> owner = memory.Allocate((int)((bits + 7) / 8), ClearMode.Zero);
        Span<byte> marked = owner.Memory.Span;
        RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
        try
        {
            Span<RgbaF32> row = buffer.AsSpan(0, width);
            PixelConvert.Convert<TPixel, RgbaF32>(plane.Row(seed.Y), row);
            FuzzColor target = new(row[seed.X]);
            double threshold = FuzzColor.Threshold(tolerance);
            int cachedRow = seed.Y;
            Stack<(int X, int Y)> pending = new();
            pending.Push((seed.X, seed.Y));
            bool diagonal = connectivity == Connectivity.Eight;
            while (pending.Count > 0)
            {
                (int x, int y) = pending.Pop();
                if (Marked(marked, ((long)y * width) + x))
                {
                    continue;
                }

                if (cachedRow != y)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(plane.Row(y), row);
                    cachedRow = y;
                }

                if (target.Distance2(row[x]) > threshold)
                {
                    continue;
                }

                // Extend left and right along the row, then queue the spans above and below.
                int left = x, right = x;
                while (left > 0 && !Marked(marked, ((long)y * width) + left - 1) && target.Distance2(row[left - 1]) <= threshold)
                {
                    left--;
                }

                while (right < width - 1 && !Marked(marked, ((long)y * width) + right + 1) && target.Distance2(row[right + 1]) <= threshold)
                {
                    right++;
                }

                for (int i = left; i <= right; i++)
                {
                    Mark(marked, ((long)y * width) + i);
                }

                int from = diagonal ? Math.Max(0, left - 1) : left, to = diagonal ? Math.Min(width - 1, right + 1) : right;
                foreach (int ny in new[] { y - 1, y + 1 })
                {
                    if ((uint)ny >= (uint)height)
                    {
                        continue;
                    }

                    for (int i = from; i <= to; i++)
                    {
                        if (!Marked(marked, ((long)ny * width) + i))
                        {
                            pending.Push((i, ny));
                        }
                    }
                }
            }
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(buffer);
        }

        return owner;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Marked(ReadOnlySpan<byte> bits, long i) => (bits[(int)(i >> 3)] & (1 << (int)(i & 7))) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Mark(Span<byte> bits, long i) => bits[(int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
}

/// <summary>
/// Makes pixels within tolerance of a color transparent (ImageMagick's -fuzz with -transparent), with an optional soft ramp of
/// alpha over the next band of distance. Per pixel, so it runs on views inside scopes.
/// </summary>
internal sealed class MakeTransparentOperation(Rgba8 color, float tolerance, float softness) : IRasterOperation, IRunsOnViews
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        if (TPixel.Layout.Alpha == AlphaKind.None)
        {
            return;
        }

        FuzzColor target = new(RgbaF32.FromRgba8(color));
        double threshold = FuzzColor.Threshold(tolerance);
        Keying.ForEachRow(input, (row, rgba) =>
        {
            bool changed = false;
            for (int x = 0; x < rgba.Length; x++)
            {
                double d2 = target.Distance2(rgba[x]);
                if (d2 <= threshold)
                {
                    rgba[x] = rgba[x] with { A = 0 };
                    changed = true;
                }
                else if (softness > 0)
                {
                    // The ramp runs on the distance itself, √(d² / 3), from the tolerance to tolerance + softness.
                    double d = Math.Sqrt(d2 / 3);
                    double t = (d - tolerance) / softness;
                    if (t < 1)
                    {
                        rgba[x] = rgba[x] with { A = (float)(rgba[x].A * t) };
                        changed = true;
                    }
                }
            }

            return changed;
        });
    }
}

/// <summary>
/// Makes pixels whose chroma lies within a distance of a key color's transparent: the distance is taken between Rec. 709 Cb and Cr,
/// so luma is left out and an unevenly lit screen stays close to the key. Per pixel, so it runs on views inside scopes.
/// </summary>
internal sealed class ChromaKeyOperation(Rgba8 color, float similarity, float blend) : IRasterOperation, IRunsOnViews
{
    // Rec. 709 luma weights and the scales that bring B − Y and R − Y to −0.5..0.5.
    private const float Kr = 0.2126f, Kb = 0.0722f, Kg = 1 - Kr - Kb;
    private const float CbScale = 1 / (2 * (1 - Kb)), CrScale = 1 / (2 * (1 - Kr));

    /// <summary>The largest chroma distance in the RGB cube, between green and magenta, which a similarity of 1 reaches.</summary>
    internal static readonly float MaxDistance = 2 * MathF.Sqrt((Kg * CbScale * Kg * CbScale) + (Kg * CrScale * Kg * CrScale));

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        if (TPixel.Layout.Alpha == AlphaKind.None)
        {
            return;
        }

        RgbaF32 key = RgbaF32.FromRgba8(color);
        (float keyCb, float keyCr) = Chroma(key);
        float inner = similarity * MaxDistance;
        float band = blend * MaxDistance;
        Keying.ForEachRow(input, (row, rgba) =>
        {
            bool changed = false;
            for (int x = 0; x < rgba.Length; x++)
            {
                (float cb, float cr) = Chroma(rgba[x]);
                float dcb = cb - keyCb, dcr = cr - keyCr;
                float d = MathF.Sqrt((dcb * dcb) + (dcr * dcr));
                if (d <= inner)
                {
                    rgba[x] = rgba[x] with { A = 0 };
                    changed = true;
                }
                else if (band > 0 && d < inner + band)
                {
                    rgba[x] = rgba[x] with { A = rgba[x].A * ((d - inner) / band) };
                    changed = true;
                }
            }

            return changed;
        });
    }

    internal static (float Cb, float Cr) Chroma(RgbaF32 p)
    {
        float y = (Kr * p.R) + (Kg * p.G) + (Kb * p.B);
        return ((p.B - y) * CbScale, (p.R - y) * CrScale);
    }
}

/// <summary>
/// Holds the screen's channel down to a level taken from the other two, removing the colored cast a green or blue screen leaves on
/// edges, hair and see-through pixels. Per pixel, so it runs on views inside scopes.
/// </summary>
internal sealed class DespillOperation(DespillColor screen, float mix, float expand) : IRasterOperation, IRunsOnViews
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        bool green = screen == DespillColor.Green;
        float keep = 1 - expand;
        Keying.ForEachRow(input, (row, rgba) =>
        {
            bool changed = false;
            for (int x = 0; x < rgba.Length; x++)
            {
                RgbaF32 p = rgba[x];
                float a = p.R, b = green ? p.B : p.G, s = green ? p.G : p.B;
                float low = (a + b) * 0.5f;
                float limit = (low + ((MathF.Max(a, b) - low) * mix)) * keep;
                if (s > limit)
                {
                    rgba[x] = green ? p with { G = limit } : p with { B = limit };
                    changed = true;
                }
            }

            return changed;
        });
    }
}

/// <summary>Replaces pixels within tolerance of one color with another (ImageMagick's -fuzz with -fill and -opaque).</summary>
internal sealed class ReplaceColorOperation(Rgba8 from, Rgba8 to, float tolerance) : IRasterOperation, IRunsOnViews
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        FuzzColor target = new(RgbaF32.FromRgba8(from));
        double threshold = FuzzColor.Threshold(tolerance);
        RgbaF32 fill = RgbaF32.FromRgba8(to);
        Keying.ForEachRow(input, (row, rgba) =>
        {
            bool changed = false;
            for (int x = 0; x < rgba.Length; x++)
            {
                if (target.Distance2(rgba[x]) <= threshold)
                {
                    rgba[x] = fill;
                    changed = true;
                }
            }

            return changed;
        });
    }
}

/// <summary>Fills the region connected to a seed whose colors match the seed's (ImageMagick's -fuzz with -floodfill).</summary>
internal sealed class FloodFillOperation(Point seed, Rgba8 fill, float tolerance, Connectivity connectivity) : IRasterOperation, IAreaTranslatable
{
    public Size OutputSize(Size input)
    {
        if ((uint)seed.X >= (uint)input.Width || (uint)seed.Y >= (uint)input.Height)
        {
            throw new ArgumentOutOfRangeException($"The flood fill seed {seed} lies outside the {input.Width}×{input.Height} image.", innerException: null);
        }

        return input;
    }

    public IRasterOperation? InArea(Rect area)
    {
        // Inside a scope the region is found in the area alone, and a seed outside it fills nothing.
        return area.Contains(seed) ? new FloodFillOperation(new Point(seed.X - area.X, seed.Y - area.Y), fill, tolerance, connectivity) : null;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        PixelPlane<TPixel> plane = input.Destination;
        using IMemoryOwner<byte> region = FloodFill.Region(plane, seed, tolerance, connectivity, (IPixelMemorySource?)input.Memory ?? input.Context.Memory);
        TPixel color = TPixel.FromRgba8(fill);
        ReadOnlySpan<byte> marked = region.Memory.Span;
        for (int y = 0; y < plane.Height; y++)
        {
            Span<TPixel> row = plane.Row(y);
            long start = (long)y * plane.Width;
            for (int x = 0; x < row.Length; x++)
            {
                if (FloodFill.Marked(marked, start + x))
                {
                    row[x] = color;
                }
            }
        }
    }
}

/// <summary>Shared row loop for the keying steps: rows as straight RgbaF32, converted back only when a pixel changed.</summary>
internal static class Keying
{
    internal delegate bool RowEdit(int y, Span<RgbaF32> rgba);

    internal static void ForEachRow<TPixel>(OperationInput<TPixel> input, RowEdit edit)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Destination.Width;
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> rgba = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> row = input.Destination.Row(y);
                    PixelConvert.Convert<TPixel, RgbaF32>(row, rgba);
                    if (edit(y, rgba))
                    {
                        PixelConvert.Convert<RgbaF32, TPixel>(rgba, row);
                    }
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }
}
