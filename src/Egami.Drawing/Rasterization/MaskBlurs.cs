using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Blurred coverage for <see cref="MaskBlur"/>: the general path rasterizes the shape's exact coverage over its bounds and the clip
/// grown by 3σ, blurs it with Egami's fast blur and keeps what the style asks, as Skia's blur mask filter; axis-aligned rectangles
/// with the normal style are computed from the error function instead.
/// </summary>
internal static class MaskBlurs
{
    /// <summary>How far past a shape its blur reaches, in whole pixels: 3σ and one more.</summary>
    internal static int Pad(float sigma) => (int)MathF.Ceiling(3 * sigma) + 1;

    /// <summary>Fills edges through a blur of their coverage.</summary>
    internal static void Fill(DrawTools tools, EdgeList edges, FillRule rule, bool antialias, MaskBlur blur, Rect clip, ISpanSink sink)
    {
        int pad = Pad(blur.Sigma);
        Rect area = Grow(edges.Bounds.RoundOut(), pad).Intersect(Grow(clip, pad));
        if (area.IsEmpty)
        {
            return;
        }

        int size = area.Width * area.Height;
        IMemoryOwner<byte> owner = tools.Context.Memory.Allocate(size, ClearMode.Zero);
        try
        {
            Memory<byte> coverage = owner.Memory[..size];
            tools.Rasterizer.Fill(edges, area, rule, antialias, new BufferSink(coverage, area), 1);
            using Raster<Alpha8> original = Raster.Create<Alpha8>(area.Width, area.Height, tools.Context);
            for (int y = 0; y < area.Height; y++)
            {
                coverage.Span.Slice(y * area.Width, area.Width).CopyTo(MemoryMarshal.AsBytes(original.Row(y)));
            }

            using Raster<Alpha8> blurred = original.Derive(o => o.Blur(blur.Sigma, ColorSpace.Encoded, BlurMethod.Fast));
            Rect visible = area.Intersect(clip);
            byte[] row = ArrayPool<byte>.Shared.Rent(visible.Width);
            try
            {
                for (int y = visible.Y; y < visible.Bottom; y++)
                {
                    Span<byte> o = MemoryMarshal.AsBytes(original.Row(y - area.Y)).Slice(visible.X - area.X, visible.Width);
                    Span<byte> b = MemoryMarshal.AsBytes(blurred.Row(y - area.Y)).Slice(visible.X - area.X, visible.Width);
                    Span<byte> c = row.AsSpan(0, visible.Width);
                    Combine(blur.Style, o, b, c);
                    Send(sink, y, visible.X, c);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(row);
            }
        }
        finally
        {
            owner.Dispose();
        }
    }

    /// <summary>A device rectangle blurred exactly: each pixel the mean over its square of the rectangle convolved with a Gaussian.</summary>
    internal static void FillRect(RectF rect, float sigma, Rect clip, ISpanSink sink)
    {
        int pad = Pad(sigma);
        Rect area = Grow(rect.RoundOut(), pad).Intersect(clip);
        if (area.IsEmpty)
        {
            return;
        }

        float[] cx = Profile(area.X, area.Width, rect.Left, rect.Right, sigma);
        float[] cy = Profile(area.Y, area.Height, rect.Top, rect.Bottom, sigma);
        byte[] row = ArrayPool<byte>.Shared.Rent(area.Width);
        try
        {
            for (int j = 0; j < area.Height; j++)
            {
                Span<byte> c = row.AsSpan(0, area.Width);
                float wy = cy[j] * 255;
                for (int i = 0; i < c.Length; i++)
                {
                    c[i] = (byte)Math.Clamp((int)((cx[i] * wy) + 0.5f), 0, 255);
                }

                Send(sink, area.Y + j, area.X, c);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    /// <summary>The error function by Abramowitz and Stegun's formula 7.1.26, within 1.5e-7: far below one 8-bit level.</summary>
    internal static double Erf(double x)
    {
        double t = 1 / (1 + (0.3275911 * Math.Abs(x)));
        double poly = ((((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t) - 0.284496736) * t) + 0.254829592) * t;
        double y = 1 - (poly * Math.Exp(-x * x));
        return x >= 0 ? y : -y;
    }

    private static Rect Grow(Rect r, int pad) => r.IsEmpty ? r : new Rect(r.X - pad, r.Y - pad, r.Width + (2 * pad), r.Height + (2 * pad));

    private static void Send(ISpanSink sink, int y, int x, Span<byte> coverage)
    {
        if (coverage.ContainsAnyExcept((byte)0))
        {
            sink.Blend(y, x, coverage);
        }
    }

    private static void Combine(BlurStyle style, ReadOnlySpan<byte> original, ReadOnlySpan<byte> blurred, Span<byte> result)
    {
        for (int i = 0; i < result.Length; i++)
        {
            int o = original[i], b = blurred[i];
            result[i] = style switch
            {
                BlurStyle.Solid => (byte)(o + Mul(b, 255 - o)),
                BlurStyle.Outer => Mul(b, 255 - o),
                BlurStyle.Inner => Mul(b, o),
                _ => (byte)b,
            };
        }
    }

    private static byte Mul(int a, int b)
    {
        int p = (a * b) + 128;
        return (byte)((p + (p >> 8)) >> 8);
    }

    /// <summary>The blurred box's mean over each pixel [p, p + 1] along one axis.</summary>
    private static float[] Profile(int start, int count, float from, float to, float sigma)
    {
        float[] profile = new float[count];
        for (int i = 0; i < count; i++)
        {
            profile[i] = (float)Math.Clamp(Box(start + i, from, to, sigma), 0, 1);
        }

        return profile;
    }

    /// <summary>
    /// ∫ from p to p + 1 of Φ((t − from)/σ) − Φ((t − to)/σ) dt, through the antiderivative of Φ, F(u) = u·Φ(u) + φ(u).
    /// </summary>
    private static double Box(int p, double from, double to, float sigma)
    {
        double s = sigma;
        return s * (F((p + 1 - from) / s) - F((p - from) / s) - F((p + 1 - to) / s) + F((p - to) / s));
    }

    private static double F(double u) => (u * 0.5 * (1 + Erf(u / Math.Sqrt(2)))) + (Math.Exp(-0.5 * u * u) / Math.Sqrt(2 * Math.PI));
}
