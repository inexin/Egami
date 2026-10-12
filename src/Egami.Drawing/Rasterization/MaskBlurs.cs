using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Blurred coverage for <see cref="MaskBlur"/>: the general path rasterizes the shape's exact coverage over its bounds and the clip
/// grown by 3σ, blurs it with Egami's fast blur and keeps what the style asks, as Skia's blur mask filter; axis-aligned rectangles
/// and round rectangles with the normal style are computed from the error function instead.
/// </summary>
internal static class MaskBlurs
{
    // Eight-point Gauss–Legendre nodes and weights on [−1, 1].
    private static readonly double[] Nodes = [-0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498, 0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363];
    private static readonly double[] Weights = [0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620, 0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763];

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

    /// <summary>
    /// A device round rectangle blurred from the error function: rows whose blur reaches no corner are a rectangle's; rows that do
    /// integrate the horizontal profile, exact for each height's span, against the Gaussian's mean over the pixel row, by
    /// Gauss–Legendre quadrature split where the corners begin and end.
    /// </summary>
    internal static void FillRoundRect(RoundRect roundRect, float sigma, Rect clip, ISpanSink sink, int maxParallelism)
    {
        RectF rect = roundRect.Bounds;
        int pad = Pad(sigma);
        Rect area = Grow(rect.RoundOut(), pad).Intersect(clip);
        if (area.IsEmpty)
        {
            return;
        }

        CornerRadii radii = roundRect.Radii;
        float topCorners = MathF.Max(radii.TopLeft.Y, radii.TopRight.Y), bottomCorners = MathF.Max(radii.BottomLeft.Y, radii.BottomRight.Y);
        float[] cx = Profile(area.X, area.Width, rect.Left, rect.Right, sigma);
        float[] cy = Profile(area.Y, area.Height, rect.Top, rect.Bottom, sigma);

        double reach = 4.5 * sigma;

        // Only columns within the blur's reach of a corner's curve depend on the height; they are the zones.
        float insetLeft = MathF.Max(radii.TopLeft.X, radii.BottomLeft.X), insetRight = MathF.Max(radii.TopRight.X, radii.BottomRight.X);
        int zonePad = pad + 1;
        (int, int)[] zones = Zones(
            area,
            (int)MathF.Floor(rect.Left) - zonePad,
            (int)MathF.Ceiling(rect.Left + insetLeft) + zonePad,
            (int)MathF.Floor(rect.Right - insetRight) - zonePad,
            (int)MathF.Ceiling(rect.Right) + zonePad);
        EdgeTable edge = new(sigma, zonePad + MathF.Max(insetLeft, insetRight) + 2);
        // Rows are independent, so large areas spread them over cores; each row's pixels are the same whichever thread runs it.
        ParallelOptions options = new() { MaxDegreeOfParallelism = (long)area.Width * area.Height >= 65536 ? Math.Max(1, maxParallelism) : 1 };
        Parallel.For(
            0,
            area.Height,
            options,
            () => (Row: new byte[area.Width], Sum: new double[area.Width]),
            (j, _, local) =>
            {
                byte[] row = local.Row;
                double[] sum = local.Sum;
                Span<double> cuts = stackalloc double[6];
                Span<byte> c = row.AsSpan(0, area.Width);
                int py = area.Y + j;
                double lo = Math.Max(rect.Top, py - reach), hi = Math.Min(rect.Bottom, py + 1 + reach);
                bool corners = lo < rect.Top + topCorners || hi > rect.Bottom - bottomCorners;
                if (!corners || hi <= lo)
                {
                    float wy = cy[j] * 255;
                    for (int i = 0; i < c.Length; i++)
                    {
                        c[i] = (byte)Math.Clamp((int)((cx[i] * wy) + 0.5f), 0, 255);
                    }
                }
                else
                {
                    int n = 0;
                    cuts[n++] = lo;
                    foreach (double cut in (ReadOnlySpan<double>)[rect.Top + radii.TopLeft.Y, rect.Top + radii.TopRight.Y, rect.Bottom - radii.BottomLeft.Y, rect.Bottom - radii.BottomRight.Y])
                    {
                        if (cut > lo && cut < hi)
                        {
                            cuts[n++] = cut;
                        }
                    }

                    cuts[n++] = hi;
                    cuts[..n].Sort();
                    Array.Clear(sum);
                    double total = 0;
                    for (int s = 0; s + 1 < n; s++)
                    {
                        // Pieces of at most a sigma, so the quadrature follows the corner arcs and the row's weight.
                        double from = cuts[s], length = cuts[s + 1] - from;
                        int pieces = (int)Math.Ceiling(length / Math.Max(sigma, 0.5));
                        for (int piece = 0; piece < pieces; piece++)
                        {
                            double a = from + (length * piece / pieces), b = from + (length * (piece + 1) / pieces), half = (b - a) / 2, mid = (a + b) / 2;
                            for (int k = 0; k < Nodes.Length; k++)
                            {
                                // The Gaussian's mean over the pixel row [py, py + 1], seen from height t.
                                double t = mid + (half * Nodes[k]);
                                double w = Weights[k] * half * (Phi((py + 1 - t) / sigma) - Phi((py - t) / sigma));
                                total += w;
                                (double left, double right) = Span(roundRect, t);
                                foreach ((int start, int end) in zones)
                                {
                                    for (int i = start; i < end; i++)
                                    {
                                        int p = area.X + i;
                                        sum[i] += w * (edge.At(p - left) - edge.At(p - right));
                                    }
                                }
                            }
                        }
                    }

                    // Between the zones every height's span covers the column alike: the rectangle's profile times the row's weight.
                    float wy = (float)total * 255;
                    for (int i = 0; i < c.Length; i++)
                    {
                        c[i] = (byte)Math.Clamp((int)((cx[i] * wy) + 0.5f), 0, 255);
                    }

                    foreach ((int start, int end) in zones)
                    {
                        for (int i = start; i < end; i++)
                        {
                            c[i] = (byte)Math.Clamp((int)((sum[i] * 255) + 0.5), 0, 255);
                        }
                    }
                }

                Send(sink, area.Y + j, area.X, c);
                return local;
            },
            _ => { });
    }

    /// <summary>The error function by Abramowitz and Stegun's formula 7.1.26, within 1.5e-7: far below one 8-bit level.</summary>
    internal static double Erf(double x)
    {
        double t = 1 / (1 + (0.3275911 * Math.Abs(x)));
        double poly = ((((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t) - 0.284496736) * t) + 0.254829592) * t;
        double y = 1 - (poly * Math.Exp(-x * x));
        return x >= 0 ? y : -y;
    }

    /// <summary>The column ranges, relative to the area, of the left and right zones, merged when they meet.</summary>
    private static (int Start, int End)[] Zones(Rect area, int leftFrom, int leftTo, int rightFrom, int rightTo)
    {
        int a0 = Math.Clamp(leftFrom - area.X, 0, area.Width), a1 = Math.Clamp(leftTo - area.X, 0, area.Width);
        int b0 = Math.Clamp(rightFrom - area.X, 0, area.Width), b1 = Math.Clamp(rightTo - area.X, 0, area.Width);
        return b0 <= a1 ? [(a0, Math.Max(a1, b1))] : [(a0, a1), (b0, b1)];
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

    private static double Phi(double u) => 0.5 * (1 + Erf(u / Math.Sqrt(2)));

    private static double F(double u) => (u * 0.5 * (1 + Erf(u / Math.Sqrt(2)))) + (Math.Exp(-0.5 * u * u) / Math.Sqrt(2 * Math.PI));

    /// <summary>The round rectangle's horizontal extent at height <paramref name="t"/>.</summary>
    private static (double Left, double Right) Span(RoundRect rr, double t)
    {
        RectF r = rr.Bounds;
        CornerRadii radii = rr.Radii;
        return (r.Left + Inset(r, radii.TopLeft, radii.BottomLeft, t), r.Right - Inset(r, radii.TopRight, radii.BottomRight, t));
    }

    private static double Inset(RectF r, System.Numerics.Vector2 top, System.Numerics.Vector2 bottom, double t)
    {
        double dy;
        System.Numerics.Vector2 radius;
        if (t < r.Top + top.Y && top.Y > 0)
        {
            dy = (r.Top + top.Y - t) / top.Y;
            radius = top;
        }
        else if (t > r.Bottom - bottom.Y && bottom.Y > 0)
        {
            dy = (t - (r.Bottom - bottom.Y)) / bottom.Y;
            radius = bottom;
        }
        else
        {
            return 0;
        }

        dy = Math.Clamp(dy, 0, 1);
        return radius.X * (1 - Math.Sqrt(1 - (dy * dy)));
    }

    /// <summary>
    /// The blurred step's mean over a pixel, ∫ from u to u + 1 of Φ(s / σ) ds for a pixel starting u past an edge, tabulated every
    /// 1/32 pixel and read linearly: within 1e-5, and two lookups where the error function would cost four evaluations.
    /// </summary>
    private sealed class EdgeTable
    {
        private const int PerPixel = 32;
        private readonly float[] _values;
        private readonly double _limit;

        internal EdgeTable(float sigma, double limit)
        {
            _limit = limit;
            int count = (int)Math.Ceiling(2 * limit * PerPixel) + 2;
            _values = new float[count];
            for (int i = 0; i < count; i++)
            {
                double u = -limit + (i / (double)PerPixel);
                _values[i] = (float)(sigma * (F((u + 1) / sigma) - F(u / sigma)));
            }
        }

        internal double At(double u)
        {
            double x = (u + _limit) * PerPixel;
            if (x <= 0)
            {
                return _values[0];
            }

            int i = (int)x;
            if (i >= _values.Length - 1)
            {
                return _values[^1];
            }

            double f = x - i;
            return _values[i] + ((_values[i + 1] - _values[i]) * f);
        }
    }
}
