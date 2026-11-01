using System.Buffers;

using Egami.Operations;

namespace Egami.Analysis;

/// <summary>
/// Compares two rasters row by row in double precision, premultiplied unless asked otherwise. Bands of rows run in parallel; each band's
/// sums land in its own slot and are added in band order, so the result does not depend on the thread count.
/// </summary>
internal static class Comparer
{
    /// <summary>The sums of one band: per channel Σ|d|, Σd² and max |d|, and the pixels that differ.</summary>
    private sealed class Sums
    {
        internal readonly double[] Abs = new double[4];
        internal readonly double[] Square = new double[4];
        internal readonly double[] Peak = new double[4];
        internal long Different;
    }

    internal static CompareResult Compare(Raster a, Raster b, CompareOptions options, CancellationToken cancellationToken)
    {
        Rect region = Region(a, b, options.Region, options.Frame);
        bool withAlpha = a.Layout.HasAlpha || b.Layout.HasAlpha;
        bool premultiply = options.Alpha == AlphaCompare.Premultiplied;
        int width = region.Width;
        long rowBytes = (long)width * 32;
        int bandRows = RowRunner.BandRows(rowBytes);
        Sums[] bands = new Sums[(region.Height + bandRows - 1) / bandRows];
        double fuzz = options.Fuzz;
        RowRunner.Run(region.Height, rowBytes, a.Context.MaxParallelism, (start, end) =>
        {
            Sums sums = new();
            RgbaD[] pa = ArrayPool<RgbaD>.Shared.Rent(width);
            RgbaD[] pb = ArrayPool<RgbaD>.Shared.Rent(width);
            using RowReader readA = new(a, options.Frame, width);
            using RowReader readB = new(b, options.Frame, width);
            try
            {
                Span<double> abs = stackalloc double[4];
                Span<double> square = stackalloc double[4];
                Span<double> peak = stackalloc double[4];
                peak.Clear();
                for (int y = start; y < end; y++)
                {
                    Span<RgbaD> ra = pa.AsSpan(0, width);
                    Span<RgbaD> rb = pb.AsSpan(0, width);
                    readA.Read(region.Y + y, region.X, ra);
                    readB.Read(region.Y + y, region.X, rb);
                    abs.Clear();
                    square.Clear();
                    for (int x = 0; x < width; x++)
                    {
                        RgbaD p = ra[x], q = rb[x];
                        double sa = premultiply ? p.A : 1, da = premultiply ? q.A : 1;
                        double dr = (p.R * sa) - (q.R * da);
                        double dg = (p.G * sa) - (q.G * da);
                        double db = (p.B * sa) - (q.B * da);
                        double dalpha = p.A - q.A;
                        dr = Math.Abs(dr);
                        dg = Math.Abs(dg);
                        db = Math.Abs(db);
                        dalpha = Math.Abs(dalpha);
                        abs[0] += dr;
                        abs[1] += dg;
                        abs[2] += db;
                        abs[3] += dalpha;
                        square[0] += dr * dr;
                        square[1] += dg * dg;
                        square[2] += db * db;
                        square[3] += dalpha * dalpha;
                        peak[0] = Math.Max(peak[0], dr);
                        peak[1] = Math.Max(peak[1], dg);
                        peak[2] = Math.Max(peak[2], db);
                        peak[3] = Math.Max(peak[3], dalpha);
                        if (dr > fuzz || dg > fuzz || db > fuzz || dalpha > fuzz)
                        {
                            sums.Different++;
                        }
                    }

                    for (int c = 0; c < 4; c++)
                    {
                        sums.Abs[c] += abs[c];
                        sums.Square[c] += square[c];
                    }
                }

                peak.CopyTo(sums.Peak);
            }
            finally
            {
                ArrayPool<RgbaD>.Shared.Return(pa);
                ArrayPool<RgbaD>.Shared.Return(pb);
            }

            bands[start / bandRows] = sums;
        }, cancellationToken);

        Sums total = new();
        foreach (Sums band in bands)
        {
            for (int c = 0; c < 4; c++)
            {
                total.Abs[c] += band.Abs[c];
                total.Square[c] += band.Square[c];
                total.Peak[c] = Math.Max(total.Peak[c], band.Peak[c]);
            }

            total.Different += band.Different;
        }

        double[] ssim = options.Ssim ? Ssim.Compute(a, b, region, options.Frame, premultiply, withAlpha, cancellationToken) : [double.NaN, double.NaN, double.NaN, double.NaN];
        long pixels = (long)region.Width * region.Height;
        ChannelDifference Channel(int c) => new() { Mae = total.Abs[c] / pixels, Mse = total.Square[c] / pixels, PeakError = total.Peak[c], Ssim = ssim[c] };
        ChannelDifference alpha = withAlpha ? Channel(3) : new ChannelDifference { Ssim = options.Ssim ? 1 : double.NaN };
        (double butteraugli, double norm) = options.Butteraugli ? Analysis.Butteraugli.Compare(a, b, region, options.Frame, withAlpha) : (double.NaN, double.NaN);
        return new CompareResult(Channel(0), Channel(1), Channel(2), alpha, withAlpha, total.Different, pixels, butteraugli, norm);
    }

    /// <summary>Checks the two rasters, the frame and the region, and returns the rectangle compared.</summary>
    internal static Rect Region(Raster a, Raster b, Rect? region, int frame)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"Both images must have the same size; they are {a.Width}×{a.Height} and {b.Width}×{b.Height}.", nameof(b));
        }

        if (frame >= a.FrameCount || frame >= b.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), frame, $"The images have {a.FrameCount} and {b.FrameCount} frames.");
        }

        Rect whole = new(0, 0, a.Width, a.Height);
        if (region is not Rect r)
        {
            return whole;
        }

        if (r.IsEmpty || !whole.Contains(r))
        {
            throw new ArgumentOutOfRangeException(nameof(region), r, $"The region must be a non-empty rectangle inside the {a.Width}×{a.Height} image.");
        }

        return r;
    }
}
