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

        (int Row, double Weight)[] rows = format == ChromaFormat.Yuv422 ? [(y, 1)] : Taps(y, ch, siting == ChromaSiting.TopLeft, mode);
        (int Col, double Weight)[] cols = Taps(x, cw, siting != ChromaSiting.Center, mode);
        double sum = 0;
        foreach ((int r, double wr) in rows)
        {
            foreach ((int c, double wc) in cols)
            {
                sum += wr * wc * plane[r, c];
            }
        }

        return sum;

        static (int, double)[] Taps(int p, int n, bool cosited, ChromaUpsampling mode)
        {
            int i = p / 2;
            if (mode == ChromaUpsampling.Nearest)
            {
                return [(i, 1)];
            }

            if (cosited)
            {
                // Chroma sample i sits on luma 2i: even positions take it, odd ones the midpoint to the next.
                return p % 2 == 0 ? [(i, 1)] : [(i, 0.5), (Math.Min(i + 1, n - 1), 0.5)];
            }

            // Chroma sample i sits at luma 2i + 0.5: three quarters of the nearer, one quarter of the farther.
            return p % 2 == 0 ? [(i, 0.75), (Math.Max(i - 1, 0), 0.25)] : [(i, 0.75), (Math.Min(i + 1, n - 1), 0.25)];
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

        int cw = format == ChromaFormat.Yuv444 ? w : (w + 1) / 2;
        int ch = format == ChromaFormat.Yuv420 ? (h + 1) / 2 : h;
        int[,] u = new int[ch, cw], v = new int[ch, cw];
        for (int j = 0; j < ch; j++)
        {
            for (int i = 0; i < cw; i++)
            {
                (int, double)[] rows = format != ChromaFormat.Yuv420 ? [(j, 1)] : Taps(j, h, color.Siting == ChromaSiting.TopLeft, mode);
                (int, double)[] cols = format == ChromaFormat.Yuv444 ? [(i, 1)] : Taps(i, w, color.Siting != ChromaSiting.Center, mode);
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

        static (int, double)[] Taps(int i, int n, bool cosited, ChromaDownsampling mode)
        {
            if (mode == ChromaDownsampling.Fast)
            {
                return [(2 * i, 1)];
            }

            return cosited
                ? [(Math.Max((2 * i) - 1, 0), 0.25), (2 * i, 0.5), (Math.Min((2 * i) + 1, n - 1), 0.25)]
                : [(2 * i, 0.5), (Math.Min((2 * i) + 1, n - 1), 0.5)];
        }
    }
}
