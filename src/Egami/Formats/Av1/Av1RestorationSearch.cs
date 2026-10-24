namespace Egami.Formats.Av1;

/// <summary>
/// The loop restoration search of a video reference frame: each 256x256 luma unit's separable symmetric Wiener filter solved in
/// closed form from second-order statistics of the CDEF output against the source, horizontal taps first and vertical taps on
/// the horizontally filtered unit after them, with no refinement (SVT-AV1's mid-preset choice and the normal equations of
/// libaom's pickrst.c are the idea; BSD); then the frame filtered with every unit's filter by the decoder's own restoration
/// (<see cref="Av1Restoration"/>), and each unit kept where its error falls by more than its taps cost at the frame's lambda.
/// Chroma is not restored. Units are visited in a fixed order, so the result does not depend on the thread count.
/// </summary>
internal static class Av1RestorationSearch
{
    /// <summary>The coded taps' ranges, outermost first (section 5.11.58).</summary>
    private static ReadOnlySpan<int> TapMin => [-5, -23, -17];

    private static ReadOnlySpan<int> TapMax => [10, 8, 46];

    /// <summary>Searches luma Wiener filters; leaves the frame restored and its header and parameters set when any unit is kept.</summary>
    internal static void Search(Av1Frame frame, Av1EncoderSource source, Av1SequenceHeader sequence, Av1FrameParameters parameters, ushort[][] deblocked, long rdmult)
    {
        int width = frame.Header.UpscaledWidth, height = frame.Header.FrameHeight;
        int stride = frame.Strides[0];
        parameters.LrType[0] = Av1RestorationType.Wiener;
        parameters.LrUnitShift = 2;
        Av1FrameHeader header = Av1FilterSearch.Reparse(sequence, parameters);
        int size = header.LoopRestorationSize[0];
        int horz = Math.Max((width + (size >> 1)) / size, 1), vert = Math.Max((height + (size >> 1)) / size, 1);
        Av1RestorationUnit[] units = new Av1RestorationUnit[horz * vert];
        ushort[] cdef = frame.Plane<ushort>(0);
        ushort[] target = source.Planes[0];
        frame.For(units.Length, u =>
        {
            (int x0, int x1, int y0, int y1) = Bounds(u % horz, u / horz, horz, vert, size, width, height);
            (units[u].H0, units[u].H1, units[u].H2, units[u].V0, units[u].V1, units[u].V2) = Solve(cdef, target, stride, x0, x1, y0, y1, width, height);
            units[u].Type = 1;
        });

        // Every unit filtered, then each kept where it pays.
        ushort[][] planes = [.. frame.Planes!.Select(p => (ushort[])p)];
        frame.Restoration[0] = new Av1RestorationPlane { Type = Av1RestorationType.Wiener, UnitSize = size, HorzUnits = horz, VertUnits = vert, Units = units };
        for (int plane = 1; plane < frame.Header.NumPlanes; plane++)
        {
            frame.Restoration[plane] = new Av1RestorationPlane { Type = Av1RestorationType.None, UnitSize = header.LoopRestorationSize[plane], HorzUnits = 1, VertUnits = 1 };
        }

        Av1Restoration.Apply(frame, planes, deblocked, frame.Strides);
        ushort[] restored = frame.Plane<ushort>(0);
        int kept = 0;
        for (int u = 0; u < units.Length; u++)
        {
            (int x0, int x1, int y0, int y1) = Bounds(u % horz, u / horz, horz, vert, size, width, height);
            long before = Av1MotionSearch.SquaredError(cdef, target, (y0 * stride) + x0, stride, x1 - x0, y1 - y0);
            long after = Av1MotionSearch.SquaredError(restored, target, (y0 * stride) + x0, stride, x1 - x0, y1 - y0);
            int scale = sequence.BitDepth == 8 ? 4 : sequence.BitDepth == 10 ? 0 : -4;
            long gain = scale >= 0 ? (before - after) << scale : (before - after) >> -scale;

            // About 4 bits a tap and 1 for the unit's flag.
            if (Av1Rate.RdCost(rdmult, 25 * Av1CostSink.BitCost, 0) < gain << 7)
            {
                kept++;
            }
            else
            {
                units[u] = default;
            }
        }

        frame.Planes![0] = cdef;
        if (kept == 0)
        {
            parameters.LrType[0] = Av1RestorationType.None;
            frame.Restoration[0] = new Av1RestorationPlane { Type = Av1RestorationType.None, UnitSize = size, HorzUnits = horz, VertUnits = vert };
            frame.Header = Av1FilterSearch.Reparse(sequence, parameters);
            return;
        }

        frame.Header = Av1FilterSearch.Reparse(sequence, parameters);
        Av1Restoration.Apply(frame, planes, deblocked, frame.Strides);
    }

    /// <summary>Gets a unit's samples: units of the size, the last of a row or column taking the rest when it is under half a unit, rows 8 samples up.</summary>
    private static (int X0, int X1, int Y0, int Y1) Bounds(int col, int row, int horz, int vert, int size, int width, int height)
    {
        // Rows of units start 8 rows up, with the stripes the decoder filters in (section 7.17).
        int x0 = col * size, y0 = Math.Max(0, (row * size) - 8);
        int x1 = col == horz - 1 ? width : x0 + size, y1 = row == vert - 1 ? height : ((row + 1) * size) - 8;
        return (x0, x1, y0, y1);
    }

    /// <summary>Solves a unit's horizontal then vertical taps, each a 3x3 system of normal equations.</summary>
    private static (int H0, int H1, int H2, int V0, int V1, int V2) Solve(ushort[] x, ushort[] s, int stride, int x0, int x1, int y0, int y1, int width, int height)
    {
        Span<double> a = stackalloc double[9];
        Span<double> b = stackalloc double[3];
        Span<double> f = stackalloc double[3];
        a.Clear();
        b.Clear();
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int px = x0; px < x1; px++)
            {
                int c = x[row + px];
                for (int k = 0; k < 3; k++)
                {
                    int d = 3 - k;
                    f[k] = x[row + Math.Max(px - d, 0)] + x[row + Math.Min(px + d, width - 1)] - (2 * c);
                }

                Accumulate(a, b, f, s[row + px] - c);
            }
        }

        Span<int> h = stackalloc int[3];
        Taps(a, b, h);

        // The unit horizontally filtered, three rows beyond it each way, then the vertical system on it.
        int top = Math.Max(y0 - 3, 0), bottom = Math.Min(y1 + 3, height), w = x1 - x0;
        double[] z = new double[(bottom - top) * w];
        for (int y = top; y < bottom; y++)
        {
            int row = y * stride;
            for (int px = x0; px < x1; px++)
            {
                int c = x[row + px];
                double sum = c;
                for (int k = 0; k < 3; k++)
                {
                    int d = 3 - k;
                    sum += h[k] / 128.0 * (x[row + Math.Max(px - d, 0)] + x[row + Math.Min(px + d, width - 1)] - (2 * c));
                }

                z[((y - top) * w) + px - x0] = sum;
            }
        }

        a.Clear();
        b.Clear();
        for (int y = y0; y < y1; y++)
        {
            for (int px = x0; px < x1; px++)
            {
                double c = z[((y - top) * w) + px - x0];
                for (int k = 0; k < 3; k++)
                {
                    int d = 3 - k;
                    int above = Math.Max(y - d, 0) - top, below = Math.Min(y + d, height - 1) - top;
                    f[k] = z[(above * w) + px - x0] + z[(below * w) + px - x0] - (2 * c);
                }

                Accumulate(a, b, f, s[(y * stride) + px] - c);
            }
        }

        Span<int> v = stackalloc int[3];
        Taps(a, b, v);
        return (h[0], h[1], h[2], v[0], v[1], v[2]);
    }

    private static void Accumulate(Span<double> a, Span<double> b, ReadOnlySpan<double> f, double t)
    {
        for (int i = 0; i < 3; i++)
        {
            b[i] += f[i] * t;
            for (int j = 0; j < 3; j++)
            {
                a[(i * 3) + j] += f[i] * f[j];
            }
        }
    }

    /// <summary>Solves the system, a little regularized, by Cramer's rule, and rounds the taps to 1/128 within their ranges.</summary>
    private static void Taps(Span<double> a, ReadOnlySpan<double> b, Span<int> taps)
    {
        double trace = a[0] + a[4] + a[8];
        for (int i = 0; i < 3; i++)
        {
            a[(i * 4)] += (trace * 1e-4) + 1e-9;
        }

        double det = Det(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]);
        taps.Clear();
        if (Math.Abs(det) < 1e-12)
        {
            return;
        }

        double h0 = Det(b[0], a[1], a[2], b[1], a[4], a[5], b[2], a[7], a[8]) / det;
        double h1 = Det(a[0], b[0], a[2], a[3], b[1], a[5], a[6], b[2], a[8]) / det;
        double h2 = Det(a[0], a[1], b[0], a[3], a[4], b[1], a[6], a[7], b[2]) / det;
        ReadOnlySpan<double> h = [h0, h1, h2];
        for (int k = 0; k < 3; k++)
        {
            taps[k] = Math.Clamp((int)Math.Round(h[k] * 128, MidpointRounding.AwayFromZero), TapMin[k], TapMax[k]);
        }

        static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
            (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
    }
}
