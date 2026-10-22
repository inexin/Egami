using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>
/// Phase 2.1.4 task 1: the exact double-precision implementation of ITU-T H.273's YUV formulas that every conversion is gated
/// against. It shares no code with Egami's kernels: only the published Kr and Kb values, the quantization equations, and the
/// chroma filters the design names (bilinear upsampling by siting, siting-matched averaging), all evaluated in double and rounded
/// once, half up.
/// </summary>
internal static class YuvReference
{
    private static (double Kr, double Kb) Coefficients(YuvMatrix matrix) => matrix switch
    {
        YuvMatrix.Bt601 => (0.299, 0.114),
        YuvMatrix.Bt709 => (0.2126, 0.0722),
        YuvMatrix.Bt2020Ncl => (0.2627, 0.0593),
        _ => (0, 0),
    };

    /// <summary>H.273 quantization: (scale, offset) of luma and of chroma at a bit depth, for E' values 0..1 and -0.5..0.5.</summary>
    private static (double LumaScale, double LumaOffset, double ChromaScale, double ChromaOffset) Quantization(YuvColor color, int depth)
    {
        double max = (1 << depth) - 1, d = 1 << (depth - 8);
        bool full = color.Range == YuvRange.Full;
        (double ls, double lo) = full ? (max, 0) : (219 * d, 16 * d);
        if (color.Matrix == YuvMatrix.Identity)
        {
            return (ls, lo, ls, lo);
        }

        return (ls, lo, full ? max : 224 * d, 1 << (depth - 1));
    }

    /// <summary>Returns exact R'G'B' (0..1, unclamped) for one sample triple; chroma may be fractional (upsampled).</summary>
    internal static (double R, double G, double B) ToRgb(double y, double cb, double cr, YuvColor color, int depth)
    {
        (double ls, double lo, double cs, double co) = Quantization(color, depth);
        double ey = (y - lo) / ls, epb = (cb - co) / cs, epr = (cr - co) / cs;
        if (color.Matrix == YuvMatrix.Identity)
        {
            return (epr, ey, epb);
        }

        (double kr, double kb) = Coefficients(color.Matrix);
        double r = ey + (2 * (1 - kr) * epr);
        double b = ey + (2 * (1 - kb) * epb);
        double g = (ey - (kr * r) - (kb * b)) / (1 - kr - kb);
        return (r, g, b);
    }

    /// <summary>Returns exact (Y, Cb, Cr) samples, unrounded, for R'G'B' 0..1.</summary>
    internal static (double Y, double Cb, double Cr) FromRgb(double r, double g, double b, YuvColor color, int depth)
    {
        (double ls, double lo, double cs, double co) = Quantization(color, depth);
        if (color.Matrix == YuvMatrix.Identity)
        {
            return ((g * ls) + lo, (b * cs) + co, (r * cs) + co);
        }

        (double kr, double kb) = Coefficients(color.Matrix);
        double ey = (kr * r) + ((1 - kr - kb) * g) + (kb * b);
        double epb = (b - ey) / (2 * (1 - kb)), epr = (r - ey) / (2 * (1 - kr));
        return ((ey * ls) + lo, (epb * cs) + co, (epr * cs) + co);
    }

    internal static int Round(double value, int max) => (int)Math.Clamp(Math.Floor(value + 0.5), 0, max);

    /// <summary>Upsampled chroma at luma position (x, y), exact, by the design's bilinear-by-siting or nearest rule.</summary>
    internal static double ChromaAt(int[,] plane, ChromaFormat format, ChromaSiting siting, ChromaUpsampling mode, int x, int y)
    {
        int ch = plane.GetLength(0), cw = plane.GetLength(1);
        if (format == ChromaFormat.Yuv444)
        {
            return plane[y, x];
        }

        (int sx, int sy) = Factors(format);
        (int Row, double Weight)[] rows = sy == 1 ? [(y, 1)] : Taps(y, sy, ch, siting == ChromaSiting.TopLeft, mode);
        (int Col, double Weight)[] cols = Taps(x, sx, cw, siting != ChromaSiting.Center, mode);
        double sum = 0;
        foreach ((int r, double wr) in rows)
        {
            foreach ((int c, double wc) in cols)
            {
                sum += wr * wc * plane[r, c];
            }
        }

        return sum;

        static (int, double)[] Taps(int p, int factor, int n, bool cosited, ChromaUpsampling mode)
        {
            if (mode == ChromaUpsampling.Nearest)
            {
                return [(p / factor, 1)];
            }

            // Chroma sample i sits on luma factor × i when co-sited, else at the middle of the luma samples it covers; linear
            // interpolation between the two samples around p, clamped to the plane.
            double at = cosited ? (double)p / factor : ((p + 0.5) / factor) - 0.5;
            int i = (int)Math.Floor(at);
            double f = at - i;
            return [(Math.Clamp(i, 0, n - 1), 1 - f), (Math.Clamp(i + 1, 0, n - 1), f)];
        }
    }

    /// <summary>Converts planes to RGB (codes 0..<paramref name="outMax"/>), returned as [y, x, channel] with alpha opaque.</summary>
    internal static int[,,] PlanesToRgb(int[,] luma, int[,]? u, int[,]? v, ChromaFormat format, YuvColor color, int depth, ChromaUpsampling mode, int outMax)
    {
        int h = luma.GetLength(0), w = luma.GetLength(1);
        int[,,] rgb = new int[h, w, 3];
        int neutral = 1 << (depth - 1);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double cb, cr;
                if (format == ChromaFormat.Yuv400)
                {
                    cb = cr = color.Matrix == YuvMatrix.Identity ? luma[y, x] : neutral;
                }
                else
                {
                    cb = ChromaAt(u!, format, color.Siting, mode, x, y);
                    cr = ChromaAt(v!, format, color.Siting, mode, x, y);
                }

                (double r, double g, double b) = ToRgb(luma[y, x], cb, cr, color, depth);
                rgb[y, x, 0] = Round(r * outMax, outMax);
                rgb[y, x, 1] = Round(g * outMax, outMax);
                rgb[y, x, 2] = Round(b * outMax, outMax);
            }
        }

        return rgb;
    }

    /// <summary>Converts RGB codes ([y, x, channel], 0..<paramref name="inMax"/>) to planes by the design's downsampling rules.</summary>
    internal static (int[,] Y, int[,]? U, int[,]? V) RgbToPlanes(int[,,] rgb, int inMax, ChromaFormat format, YuvColor color, int depth, ChromaDownsampling mode)
    {
        int h = rgb.GetLength(0), w = rgb.GetLength(1), max = (1 << depth) - 1;
        int[,] luma = new int[h, w];
        double[,] cb = new double[h, w], cr = new double[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (double yy, double b, double r) = FromRgb(rgb[y, x, 0] / (double)inMax, rgb[y, x, 1] / (double)inMax, rgb[y, x, 2] / (double)inMax, color, depth);
                luma[y, x] = Round(yy, max);
                cb[y, x] = b;
                cr[y, x] = r;
            }
        }

        if (format == ChromaFormat.Yuv400)
        {
            return (luma, null, null);
        }

        (int sx, int sy) = Factors(format);
        int cw = (w + sx - 1) / sx, ch = (h + sy - 1) / sy;
        int[,] u = new int[ch, cw], v = new int[ch, cw];
        for (int j = 0; j < ch; j++)
        {
            for (int i = 0; i < cw; i++)
            {
                (int, double)[] rows = sy == 1 ? [(j, 1)] : Taps(j, sy, h, color.Siting == ChromaSiting.TopLeft, mode);
                (int, double)[] cols = sx == 1 ? [(i, 1)] : Taps(i, sx, w, color.Siting != ChromaSiting.Center, mode);
                double su = 0, sv = 0;
                foreach ((int r, double wr) in rows)
                {
                    foreach ((int c, double wc) in cols)
                    {
                        su += wr * wc * cb[r, c];
                        sv += wr * wc * cr[r, c];
                    }
                }

                u[j, i] = Round(su, max);
                v[j, i] = Round(sv, max);
            }
        }

        return (luma, u, v);

        static (int, double)[] Taps(int i, int factor, int n, bool cosited, ChromaDownsampling mode)
        {
            if (mode == ChromaDownsampling.Fast)
            {
                return [(factor * i, 1)];
            }

            // Co-sited: a triangle of half-width factor on luma factor × i; centered: a box over the luma samples it covers.
            int at = factor * i;
            return cosited
                ? [.. Enumerable.Range(1 - factor, (2 * factor) - 1).Select(k => (Math.Clamp(at + k, 0, n - 1), (factor - Math.Abs(k)) / (double)(factor * factor)))]
                : [.. Enumerable.Range(0, factor).Select(k => (Math.Min(at + k, n - 1), 1.0 / factor))];
        }
    }

    /// <summary>Returns how many luma samples one chroma sample covers across and down.</summary>
    internal static (int X, int Y) Factors(ChromaFormat format) => format switch
    {
        ChromaFormat.Yuv420 => (2, 2),
        ChromaFormat.Yuv422 => (2, 1),
        ChromaFormat.Yuv411 => (4, 1),
        ChromaFormat.Yuv410 => (4, 4),
        _ => (1, 1),
    };
}
