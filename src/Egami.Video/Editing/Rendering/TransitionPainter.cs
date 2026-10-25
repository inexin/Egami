using Egami.Pixels;

namespace Egami.Video.Editing;

/// <summary>Draws the built-in transitions: one frame from two frames and a progress from 0 (all "from") to 1 (all "to").</summary>
internal static class TransitionPainter
{
    public static void Paint(Transition transition, Raster<Rgba8> from, Raster<Rgba8> to, double progress, Raster<Rgba8> target)
    {
        double p = Math.Clamp(Keyframe.Ease(transition.Easing, Math.Clamp(progress, 0, 1)), 0, 1);
        switch (transition.Kind)
        {
            case TransitionKind.Custom:
                transition.Renderer!.Render(from, to, p, target);
                break;
            case TransitionKind.FadeThroughColor:
                FadeThrough(from, to, p, transition.Color, target);
                break;
            case TransitionKind.Dissolve:
                Dissolve(from, to, p, target);
                break;
            case TransitionKind.Wipe:
                Wipe(from, to, p, transition.Direction, transition.Softness, target);
                break;
            case TransitionKind.Slide:
                Slide(from, to, p, transition.Direction, target);
                break;
            case TransitionKind.CircleOpen:
            case TransitionKind.CircleClose:
                Circle(from, to, p, transition.Kind == TransitionKind.CircleOpen, target);
                break;
            case TransitionKind.Clock:
                Clock(from, to, p, target);
                break;
            case TransitionKind.Zoom:
                Zoom(from, to, p, target);
                break;
            case TransitionKind.Pixelize:
                Pixelize(from, to, p, target);
                break;
            case TransitionKind.Blur:
                Blur(from, to, p, target);
                break;
            default:
                Mix(from, to, p, target);
                break;
        }
    }

    /// <summary>Blends two frames: <c>from * (1 - p) + to * p</c>, rounded.</summary>
    public static void Mix(Raster<Rgba8> from, Raster<Rgba8> to, double p, Raster<Rgba8> target)
    {
        int w = (int)Math.Round(p * 65536);
        for (int y = 0; y < target.Height; y++)
        {
            MixRow(from.Row(y), to.Row(y), w, target.Row(y));
        }
    }

    /// <summary>Blends a frame towards a colour: <c>frame * (1 - p) + color * p</c>.</summary>
    public static void MixColor(Raster<Rgba8> frame, Rgba8 color, double p, Raster<Rgba8> target)
    {
        int w = (int)Math.Round(p * 65536), keep = 65536 - w;
        for (int y = 0; y < target.Height; y++)
        {
            Span<Rgba8> src = frame.Row(y), dst = target.Row(y);
            for (int x = 0; x < dst.Length; x++)
            {
                Rgba8 a = src[x];
                dst[x] = new Rgba8(
                    (byte)(((a.R * keep) + (color.R * w) + 32768) >> 16),
                    (byte)(((a.G * keep) + (color.G * w) + 32768) >> 16),
                    (byte)(((a.B * keep) + (color.B * w) + 32768) >> 16),
                    (byte)(((a.A * keep) + (color.A * w) + 32768) >> 16));
            }
        }
    }

    private static void MixRow(ReadOnlySpan<Rgba8> a, ReadOnlySpan<Rgba8> b, int w, Span<Rgba8> target)
    {
        int keep = 65536 - w;
        for (int x = 0; x < target.Length; x++)
        {
            Rgba8 s = a[x], t = b[x];
            target[x] = new Rgba8(
                (byte)(((s.R * keep) + (t.R * w) + 32768) >> 16),
                (byte)(((s.G * keep) + (t.G * w) + 32768) >> 16),
                (byte)(((s.B * keep) + (t.B * w) + 32768) >> 16),
                (byte)(((s.A * keep) + (t.A * w) + 32768) >> 16));
        }
    }

    private static void FadeThrough(Raster<Rgba8> from, Raster<Rgba8> to, double p, Rgba8 color, Raster<Rgba8> target)
    {
        if (p < 0.5)
        {
            MixColor(from, color, p * 2, target);
        }
        else
        {
            MixColor(to, color, 2 - (p * 2), target);
        }
    }

    private static void Dissolve(Raster<Rgba8> from, Raster<Rgba8> to, double p, Raster<Rgba8> target)
    {
        uint threshold = (uint)Math.Min(uint.MaxValue, p * uint.MaxValue);
        for (int y = 0; y < target.Height; y++)
        {
            Span<Rgba8> a = from.Row(y), b = to.Row(y), dst = target.Row(y);
            for (int x = 0; x < dst.Length; x++)
            {
                dst[x] = Hash(x, y) < threshold || p >= 1 ? b[x] : a[x];
            }
        }
    }

    private static uint Hash(int x, int y)
    {
        uint h = ((uint)x * 0x8DA6B343u) ^ ((uint)y * 0xD8163841u);
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        return h ^ (h >> 15);
    }

    private static void Wipe(Raster<Rgba8> from, Raster<Rgba8> to, double p, TransitionDirection direction, double softness, Raster<Rgba8> target)
    {
        bool horizontal = direction is TransitionDirection.Left or TransitionDirection.Right;
        int extent = horizontal ? target.Width : target.Height;
        double soft = Math.Clamp(softness, 0, 1) * extent;

        // The edge moves from one side to the other; the second clip shows behind it. With a soft edge it travels the
        // edge's width further, so both ends are clean.
        double edge = p * (extent + soft);
        for (int y = 0; y < target.Height; y++)
        {
            Span<Rgba8> a = from.Row(y), b = to.Row(y), dst = target.Row(y);
            for (int x = 0; x < dst.Length; x++)
            {
                int c = horizontal ? x : y;

                // Distance the pixel has been uncovered, measured from the side the edge starts at.
                double d = direction is TransitionDirection.Left or TransitionDirection.Up ? extent - c - 0.5 : c + 0.5;
                double weight = soft <= 0 ? (d < edge ? 1 : 0) : Math.Clamp((edge - d) / soft, 0, 1);
                dst[x] = weight >= 1 ? b[x] : weight <= 0 ? a[x] : Lerp(a[x], b[x], weight);
            }
        }
    }

    private static void Slide(Raster<Rgba8> from, Raster<Rgba8> to, double p, TransitionDirection direction, Raster<Rgba8> target)
    {
        int w = target.Width, h = target.Height;
        bool horizontal = direction is TransitionDirection.Left or TransitionDirection.Right;
        int shift = (int)Math.Round(p * (horizontal ? w : h));
        for (int y = 0; y < h; y++)
        {
            Span<Rgba8> dst = target.Row(y);
            for (int x = 0; x < w; x++)
            {
                // Position in a strip of "from" followed by "to" in the direction of travel.
                (int sx, int sy) = direction switch
                {
                    TransitionDirection.Left => (x + shift, y),
                    TransitionDirection.Right => (x - shift, y),
                    TransitionDirection.Up => (x, y + shift),
                    _ => (x, y - shift),
                };
                if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                {
                    dst[x] = from.Row(sy)[sx];
                }
                else
                {
                    dst[x] = to.Row(Wrap(sy, h))[Wrap(sx, w)];
                }
            }
        }
    }

    private static int Wrap(int value, int size) => ((value % size) + size) % size;

    private static void Circle(Raster<Rgba8> from, Raster<Rgba8> to, double p, bool open, Raster<Rgba8> target)
    {
        double cx = target.Width / 2.0, cy = target.Height / 2.0;
        double max = Math.Sqrt((cx * cx) + (cy * cy));
        double radius = (open ? p : 1 - p) * max;
        double r2 = radius * radius;
        for (int y = 0; y < target.Height; y++)
        {
            Span<Rgba8> a = from.Row(y), b = to.Row(y), dst = target.Row(y);
            double dy = y + 0.5 - cy;
            for (int x = 0; x < dst.Length; x++)
            {
                double dx = x + 0.5 - cx;
                bool inside = (dx * dx) + (dy * dy) < r2;
                dst[x] = inside == open ? b[x] : a[x];
            }
        }
    }

    private static void Clock(Raster<Rgba8> from, Raster<Rgba8> to, double p, Raster<Rgba8> target)
    {
        double cx = target.Width / 2.0, cy = target.Height / 2.0;
        for (int y = 0; y < target.Height; y++)
        {
            Span<Rgba8> a = from.Row(y), b = to.Row(y), dst = target.Row(y);
            for (int x = 0; x < dst.Length; x++)
            {
                // Clockwise from twelve o'clock, 0 to 1.
                double angle = Math.Atan2(x + 0.5 - cx, cy - (y + 0.5)) / (2 * Math.PI);
                if (angle < 0)
                {
                    angle += 1;
                }

                dst[x] = angle < p ? b[x] : a[x];
            }
        }
    }

    private static void Zoom(Raster<Rgba8> from, Raster<Rgba8> to, double p, Raster<Rgba8> target)
    {
        int w = target.Width, h = target.Height;
        double scale = 1 + p;
        int weight = (int)Math.Round(p * 65536);
        Span<Rgba8> row = new Rgba8[w];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Clamp((int)(((y + 0.5 - (h / 2.0)) / scale) + (h / 2.0)), 0, h - 1);
            Span<Rgba8> source = from.Row(sy);
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Clamp((int)(((x + 0.5 - (w / 2.0)) / scale) + (w / 2.0)), 0, w - 1);
                row[x] = source[sx];
            }

            MixRow(row, to.Row(y), weight, target.Row(y));
        }
    }

    private static void Pixelize(Raster<Rgba8> from, Raster<Rgba8> to, double p, Raster<Rgba8> target)
    {
        Raster<Rgba8> source = p < 0.5 ? from : to;
        int size = 1 + (int)Math.Round(Math.Sin(p * Math.PI) * Math.Max(target.Width, target.Height) / 24.0);
        for (int by = 0; by < target.Height; by += size)
        {
            for (int bx = 0; bx < target.Width; bx += size)
            {
                int bw = Math.Min(size, target.Width - bx), bh = Math.Min(size, target.Height - by);
                long r = 0, g = 0, b = 0, a = 0;
                for (int y = by; y < by + bh; y++)
                {
                    foreach (Rgba8 c in source.Row(y).Slice(bx, bw))
                    {
                        r += c.R;
                        g += c.G;
                        b += c.B;
                        a += c.A;
                    }
                }

                int n = bw * bh;
                Rgba8 average = new((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                for (int y = by; y < by + bh; y++)
                {
                    target.Row(y).Slice(bx, bw).Fill(average);
                }
            }
        }
    }

    private static void Blur(Raster<Rgba8> from, Raster<Rgba8> to, double p, Raster<Rgba8> target)
    {
        float sigma = (float)(Math.Sin(p * Math.PI) * Math.Max(target.Width, target.Height) / 60.0);
        if (sigma < 0.3f)
        {
            Mix(from, to, p, target);
            return;
        }

        using Raster<Rgba8> a = from.Derive(ops => ops.Blur(sigma));
        using Raster<Rgba8> b = to.Derive(ops => ops.Blur(sigma));
        Mix(a, b, p, target);
    }

    private static Rgba8 Lerp(Rgba8 a, Rgba8 b, double t) => new(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)),
        (byte)Math.Round(a.A + ((b.A - a.A) * t)));
}
