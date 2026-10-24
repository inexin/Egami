namespace Egami.Formats.Av1;

/// <summary>
/// The deblocking search's single pass over every level (rav1e's deblock_filter_optimize is the idea, written from its
/// description): for each line across an edge, the squared error the samples the filter may change leave against the source
/// under each outcome the filter can take (none, the narrow filter with and without high edge variance, the flat filter of the
/// edge's length), and the levels at which the outcome changes; the errors go into an array by level as differences, whose
/// running sum is every level's total error. The lines are taken on the plane as it stands, so each edge's neighbours count
/// unfiltered.
/// </summary>
internal static partial class Av1LoopFilter
{
    /// <summary>
    /// Gets the squared error, against <paramref name="source"/>, of the samples one direction's edges of a plane may change, at
    /// each filter level 0 to 63 with sharpness 0, the plane as it stands.
    /// </summary>
    internal static long[] LevelErrors(Av1Frame frame, int plane, bool vertical, ushort[] source)
    {
        long[] total = new long[64];
        Av1FrameHeader h = frame.Header;
        int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
        int planeMiRows = (frame.MiRows + ssy) >> ssy, planeMiCols = (frame.MiCols + ssx) >> ssx;
        int width = (h.FrameWidth + ssx) >> ssx, height = (h.FrameHeight + ssy) >> ssy;
        ushort[] buf = frame.Plane<ushort>(plane);
        int stride = frame.Strides[plane];
        int bitDepth = frame.Sequence.BitDepth;
        int[] units = System.Buffers.ArrayPool<int>.Shared.Rent(planeMiRows * planeMiCols);
        try
        {
            // Every edge's structure as a level of 1 gives it; the level itself the search varies.
            UnitsAtLevel(frame, plane, ssx, ssy, planeMiRows, planeMiCols, units);
            int bands = vertical ? (planeMiRows + 15) >> 4 : (planeMiCols + 15) >> 4;
            long[][] parts = new long[bands][];
            frame.For(bands, band =>
            {
                long[] diff = parts[band] = new long[65];
                if (vertical)
                {
                    for (int y = band * 16; y < Math.Min(planeMiRows, (band + 1) * 16); y++)
                    {
                        for (int x = 0; x < planeMiCols;)
                        {
                            (int txSize, int length, _) = EdgeParameters(units, planeMiCols, plane, true, x, y, width, height);
                            if (length != 0)
                            {
                                EdgeErrors(buf, source, (y * 4 * stride) + (x * 4), 1, stride, length, bitDepth, diff);
                            }

                            x += Av1Tables.TxSizeWideUnit[txSize];
                        }
                    }
                }
                else
                {
                    for (int x = band * 16; x < Math.Min(planeMiCols, (band + 1) * 16); x++)
                    {
                        for (int y = 0; y < planeMiRows;)
                        {
                            (int txSize, int length, _) = EdgeParameters(units, planeMiCols, plane, false, x, y, width, height);
                            if (length != 0)
                            {
                                EdgeErrors(buf, source, (y * 4 * stride) + (x * 4), stride, 1, length, bitDepth, diff);
                            }

                            y += Av1Tables.TxSizeHighUnit[txSize];
                        }
                    }
                }
            });

            long running = 0;
            for (int level = 0; level < 64; level++)
            {
                foreach (long[] part in parts)
                {
                    running += part[level];
                }

                total[level] = running;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(units);
        }

        return total;
    }

    /// <summary>The units of <see cref="Units"/> with every block's level 1, so every edge the transforms make is found.</summary>
    private static void UnitsAtLevel(Av1Frame frame, int plane, int ssx, int ssy, int planeMiRows, int planeMiCols, int[] units)
    {
        frame.For(planeMiRows, y =>
        {
            int miRow = ssy | (y << ssy);
            for (int x = 0; x < planeMiCols; x++)
            {
                int miCol = ssx | (x << ssx);
                if (miRow >= frame.MiRows || miCol >= frame.MiCols)
                {
                    units[(y * planeMiCols) + x] = 0;
                    continue;
                }

                Av1BlockInfo b = frame.Grid[(miRow * frame.MiCols) + miCol]!;
                int bsize = plane == 0 ? b.Size : Av1Tables.PlaneBlockSize(b.Size, ssx, ssy);
                int blockBits = (1 << 8) | (1 << 14) | ((b.Skip && b.IsInter ? 1 : 0) << 20)
                    | (System.Numerics.BitOperations.Log2((uint)Av1Tables.BlockSizeWide[bsize]) << 21) | (System.Numerics.BitOperations.Log2((uint)Av1Tables.BlockSizeHigh[bsize]) << 25);
                units[(y * planeMiCols) + x] = TransformSize(frame, b, miRow, miCol, plane, ssx, ssy) | blockBits;
            }
        });
    }

    /// <summary>Adds an edge's four lines to <paramref name="diff"/>: each line's error with no filter at level 0, and its changes from the levels where its outcome changes.</summary>
    private static void EdgeErrors(ushort[] s, ushort[] source, int at, int step, int along, int length, int bitDepth, long[] diff)
    {
        int shift = bitDepth - 8;
        int taps = length == 14 ? 7 : length == 4 ? 2 : length == 6 ? 3 : 4;
        int changed = length == 14 ? 6 : length == 8 ? 3 : 2;
        Span<int> v = stackalloc int[14];
        Span<int> o = stackalloc int[14];
        for (int i = 0; i < 4; i++, at += along)
        {
            for (int k = -taps; k < taps; k++)
            {
                v[7 + k] = s[at + (k * step)];
            }

            int p0 = v[6], q0 = v[7], p1 = v[5], q1 = v[8];
            long none = Error(v, source, at, step, changed);
            diff[0] += none;

            // The level from which the mask lets the line be filtered: every neighbour difference within the limit (the level,
            // at least 1) and the edge's own within the larger limit (2 × (level + 2) + level).
            int inner = Math.Max(Math.Abs(p1 - p0), Math.Abs(q1 - q0));
            if (length >= 6)
            {
                inner = Math.Max(inner, Math.Max(Math.Abs(v[4] - p1), Math.Abs(v[9] - q1)));
            }

            if (length >= 8)
            {
                inner = Math.Max(inner, Math.Max(Math.Abs(v[3] - v[4]), Math.Abs(v[10] - v[9])));
            }

            int edge = (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) / 2);
            int from = Math.Max(1, (inner + (1 << shift) - 1) >> shift);
            int byEdge = (int)Math.Ceiling(((edge / (double)(1 << shift)) - 4) / 3);
            from = Math.Max(from, byEdge);
            if (from > 63)
            {
                continue;
            }

            int flatThresh = 1 << shift;
            bool flat = length >= 6 && Math.Abs(p1 - p0) <= flatThresh && Math.Abs(q1 - q0) <= flatThresh && Math.Abs(v[4] - p0) <= flatThresh
                && Math.Abs(v[9] - q0) <= flatThresh && (length < 8 || (Math.Abs(v[3] - p0) <= flatThresh && Math.Abs(v[10] - q0) <= flatThresh));
            if (flat)
            {
                v.CopyTo(o);
                Flat(o, length, flatThresh);
                diff[from] += Error(o, source, at, step, changed) - none;
                continue;
            }

            // The narrow filter: with high edge variance while the level's threshold (level >> 4) is below the neighbours'
            // differences, without from there on.
            int variance = Math.Max(Math.Abs(p1 - p0), Math.Abs(q1 - q0));
            int hevUntil = variance == 0 ? 0 : Math.Min(64, 16 * (((variance - 1) >> shift) + 1));
            long withHev = 0, withoutHev = 0;
            if (from < hevUntil)
            {
                v.CopyTo(o);
                Narrow(o, true, bitDepth);
                withHev = Error(o, source, at, step, changed);
            }

            if (hevUntil < 64)
            {
                v.CopyTo(o);
                Narrow(o, false, bitDepth);
                withoutHev = Error(o, source, at, step, changed);
            }

            if (from < hevUntil)
            {
                diff[from] += withHev - none;
                if (hevUntil < 64)
                {
                    diff[hevUntil] += withoutHev - withHev;
                }
            }
            else
            {
                diff[from] += withoutHev - none;
            }
        }
    }

    /// <summary>Gets the squared error against the source of the <paramref name="changed"/> samples each side of the edge a line may change.</summary>
    private static long Error(ReadOnlySpan<int> v, ushort[] source, int at, int step, int changed)
    {
        long error = 0;
        for (int k = -changed; k < changed; k++)
        {
            int d = v[7 + k] - source[at + (k * step)];
            error += d * d;
        }

        return error;
    }

    /// <summary>The narrow filter of a line (filter4), with or without high edge variance.</summary>
    private static void Narrow(Span<int> v, bool hev, int bitDepth)
    {
        int shift = bitDepth - 8;
        int offset = 0x80 << shift;
        int lo = -128 << shift, hi = (128 << shift) - 1;
        int ps1 = v[5] - offset, ps0 = v[6] - offset, qs0 = v[7] - offset, qs1 = v[8] - offset;
        int filter = hev ? Math.Clamp(ps1 - qs1, lo, hi) : 0;
        filter = Math.Clamp(filter + (3 * (qs0 - ps0)), lo, hi);
        int filter1 = Math.Clamp(filter + 4, lo, hi) >> 3;
        int filter2 = Math.Clamp(filter + 3, lo, hi) >> 3;
        v[7] = Math.Clamp(qs0 - filter1, lo, hi) + offset;
        v[6] = Math.Clamp(ps0 + filter2, lo, hi) + offset;
        filter = hev ? 0 : (filter1 + 1) >> 1;
        v[8] = Math.Clamp(qs1 - filter, lo, hi) + offset;
        v[5] = Math.Clamp(ps1 + filter, lo, hi) + offset;
    }

    /// <summary>The flat filter of a line of an edge's length (6, 8 or 14 taps, 14 only where the outer samples are flat too, else 8).</summary>
    private static void Flat(Span<int> v, int length, int flatThresh)
    {
        int p0 = v[6], p1 = v[5], p2 = v[4], q0 = v[7], q1 = v[8], q2 = v[9];
        if (length == 6)
        {
            v[5] = ((p2 * 3) + (p1 * 2) + (p0 * 2) + q0 + 4) >> 3;
            v[6] = (p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + 4) >> 3;
            v[7] = (p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + 4) >> 3;
            v[8] = (p0 + (q0 * 2) + (q1 * 2) + (q2 * 3) + 4) >> 3;
            return;
        }

        int p3 = v[3], q3 = v[10];
        if (length == 14)
        {
            int p4 = v[2], p5 = v[1], p6 = v[0], q4 = v[11], q5 = v[12], q6 = v[13];
            bool flat2 = Math.Abs(p4 - p0) <= flatThresh && Math.Abs(q4 - q0) <= flatThresh && Math.Abs(p5 - p0) <= flatThresh
                && Math.Abs(q5 - q0) <= flatThresh && Math.Abs(p6 - p0) <= flatThresh && Math.Abs(q6 - q0) <= flatThresh;
            if (flat2)
            {
                v[1] = ((p6 * 7) + (p5 * 2) + (p4 * 2) + p3 + p2 + p1 + p0 + q0 + 8) >> 4;
                v[2] = ((p6 * 5) + (p5 * 2) + (p4 * 2) + (p3 * 2) + p2 + p1 + p0 + q0 + q1 + 8) >> 4;
                v[3] = ((p6 * 4) + p5 + (p4 * 2) + (p3 * 2) + (p2 * 2) + p1 + p0 + q0 + q1 + q2 + 8) >> 4;
                v[4] = ((p6 * 3) + p5 + p4 + (p3 * 2) + (p2 * 2) + (p1 * 2) + p0 + q0 + q1 + q2 + q3 + 8) >> 4;
                v[5] = ((p6 * 2) + p5 + p4 + p3 + (p2 * 2) + (p1 * 2) + (p0 * 2) + q0 + q1 + q2 + q3 + q4 + 8) >> 4;
                v[6] = (p6 + p5 + p4 + p3 + p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + q2 + q3 + q4 + q5 + 8) >> 4;
                v[7] = (p5 + p4 + p3 + p2 + p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + q3 + q4 + q5 + q6 + 8) >> 4;
                v[8] = (p4 + p3 + p2 + p1 + p0 + (q0 * 2) + (q1 * 2) + (q2 * 2) + q3 + q4 + q5 + (q6 * 2) + 8) >> 4;
                v[9] = (p3 + p2 + p1 + p0 + q0 + (q1 * 2) + (q2 * 2) + (q3 * 2) + q4 + q5 + (q6 * 3) + 8) >> 4;
                v[10] = (p2 + p1 + p0 + q0 + q1 + (q2 * 2) + (q3 * 2) + (q4 * 2) + q5 + (q6 * 4) + 8) >> 4;
                v[11] = (p1 + p0 + q0 + q1 + q2 + (q3 * 2) + (q4 * 2) + (q5 * 2) + (q6 * 5) + 8) >> 4;
                v[12] = (p0 + q0 + q1 + q2 + q3 + (q4 * 2) + (q5 * 2) + (q6 * 7) + 8) >> 4;
                return;
            }
        }

        v[4] = (p3 + p3 + p3 + (2 * p2) + p1 + p0 + q0 + 4) >> 3;
        v[5] = (p3 + p3 + p2 + (2 * p1) + p0 + q0 + q1 + 4) >> 3;
        v[6] = (p3 + p2 + p1 + (2 * p0) + q0 + q1 + q2 + 4) >> 3;
        v[7] = (p2 + p1 + p0 + (2 * q0) + q1 + q2 + q3 + 4) >> 3;
        v[8] = (p1 + p0 + q0 + (2 * q1) + q2 + q3 + q3 + 4) >> 3;
        v[9] = (p0 + q0 + q1 + (2 * q2) + q3 + q3 + q3 + 4) >> 3;
    }
}
