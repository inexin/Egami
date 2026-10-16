namespace Egami.Formats.Av1;

/// <summary>
/// The deblocking loop filter, after libaom's av1_loopfilter.c and aom_dsp/loopfilter.c (BSD 2-clause, see
/// THIRD-PARTY-NOTICES): every vertical transform edge of a plane, then every horizontal one, with the 4-, 6-, 8- or 14-tap
/// filter the smaller transform beside the edge allows.
/// </summary>
internal static class Av1LoopFilter
{
    private static ReadOnlySpan<byte> FilterLengthLuma => [4, 8, 14, 14, 14];

    /// <summary>Filters the frame's planes in place.</summary>
    internal static void Apply(Av1Frame frame)
    {
        Av1FrameHeader h = frame.Header;
        if (h.LoopFilterLevel[0] == 0 && h.LoopFilterLevel[1] == 0)
        {
            return;
        }

        for (int plane = 0; plane < h.NumPlanes; plane++)
        {
            if ((plane == 1 && h.LoopFilterLevel[2] == 0) || (plane == 2 && h.LoopFilterLevel[3] == 0))
            {
                continue;
            }

            FilterPlane(frame, plane, vertical: true);
            FilterPlane(frame, plane, vertical: false);
        }
    }

    private static void FilterPlane(Av1Frame frame, int plane, bool vertical)
    {
        Av1FrameHeader h = frame.Header;
        int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
        int planeMiRows = (frame.MiRows + ssy) >> ssy, planeMiCols = (frame.MiCols + ssx) >> ssx;
        int width = (h.FrameWidth + ssx) >> ssx, height = (h.FrameHeight + ssy) >> ssy;
        ushort[] buf = frame.Planes![plane];
        int stride = frame.Strides[plane];
        int bitDepth = frame.Sequence.BitDepth;

        // Vertical edges touch only their own rows, horizontal edges only their own columns: bands of 16 run on their own.
        if (vertical)
        {
            frame.For((planeMiRows + 15) >> 4, band =>
            {
                for (int y = band * 16; y < Math.Min(planeMiRows, (band + 1) * 16); y++)
                {
                    for (int x = 0; x < planeMiCols;)
                    {
                        (int txSize, int length, int level) = EdgeParameters(frame, plane, true, x * 4, y * 4, width, height, ssx, ssy);
                        if (length != 0)
                        {
                            FilterEdge(buf, (y * 4 * stride) + (x * 4), 1, stride, length, level, h.LoopFilterSharpness, bitDepth);
                        }

                        x += Av1Tables.TxSizeWideUnit[txSize];
                    }
                }
            });
        }
        else
        {
            frame.For((planeMiCols + 15) >> 4, band =>
            {
                for (int x = band * 16; x < Math.Min(planeMiCols, (band + 1) * 16); x++)
                {
                    for (int y = 0; y < planeMiRows;)
                    {
                        (int txSize, int length, int level) = EdgeParameters(frame, plane, false, x * 4, y * 4, width, height, ssx, ssy);
                        if (length != 0)
                        {
                            FilterEdge(buf, (y * 4 * stride) + (x * 4), stride, 1, length, level, h.LoopFilterSharpness, bitDepth);
                        }

                        y += Av1Tables.TxSizeHighUnit[txSize];
                    }
                }
            });
        }
    }

    /// <summary>set_lpf_parameters: the transform size at a 4x4 unit, and the filter length and level of the edge it starts.</summary>
    private static (int TxSize, int Length, int Level) EdgeParameters(Av1Frame frame, int plane, bool vertical, int x, int y, int width, int height,
        int ssx, int ssy)
    {
        if (x >= width || y >= height)
        {
            return (Tx.T4x4, 0, 0);
        }

        int miRow = ssy | ((y << ssy) >> 2), miCol = ssx | ((x << ssx) >> 2);
        Av1BlockInfo b = frame.Grid[(miRow * frame.MiCols) + miCol]!;
        int ts = TransformSize(frame, b, miRow, miCol, plane, ssx, ssy);
        int coord = vertical ? x : y;
        int mask = (vertical ? Av1Tables.TxSizeWide[ts] : Av1Tables.TxSizeHigh[ts]) - 1;
        if ((coord & mask) != 0 || coord == 0)
        {
            return (ts, 0, 0);
        }

        int level = FilterLevel(frame.Header, b, plane, vertical ? 0 : 1);
        bool skipped = b.Skip && b.IsInter;
        int pvRow = vertical ? miRow : miRow - (1 << ssy), pvCol = vertical ? miCol - (1 << ssx) : miCol;
        Av1BlockInfo prev = frame.Grid[(pvRow * frame.MiCols) + pvCol]!;
        int pvTs = TransformSize(frame, prev, pvRow, pvCol, plane, ssx, ssy);
        int pvLevel = FilterLevel(frame.Header, prev, plane, vertical ? 0 : 1);
        bool pvSkipped = prev.Skip && prev.IsInter;
        int bsize = plane == 0 ? b.Size : Av1Tables.PlaneBlockSize(b.Size, ssx, ssy);
        int predMask = (vertical ? Av1Tables.BlockSizeWide[bsize] : Av1Tables.BlockSizeHigh[bsize]) - 1;
        bool puEdge = (coord & predMask) == 0;
        if ((level == 0 && pvLevel == 0) || (pvSkipped && skipped && !puEdge))
        {
            return (ts, 0, 0);
        }

        int dim = vertical
            ? Math.Min(Av1Tables.TxSizeWideLog2[ts], Av1Tables.TxSizeWideLog2[pvTs]) - 2
            : Math.Min(Av1Tables.TxSizeHighLog2[ts], Av1Tables.TxSizeHighLog2[pvTs]) - 2;
        int length = plane > 0 ? (dim == 0 ? 4 : 6) : FilterLengthLuma[dim];
        return (ts, length, level != 0 ? level : pvLevel);
    }

    private static int TransformSize(Av1Frame frame, Av1BlockInfo b, int miRow, int miCol, int plane, int ssx, int ssy)
    {
        if (frame.Header.LosslessArray[b.SegmentId])
        {
            return Tx.T4x4;
        }

        return plane == 0 ? frame.TxSizeMap[(miRow * frame.MiCols) + miCol] : Av1Tables.MaxUvTxSize(b.Size, ssx, ssy);
    }

    /// <summary>get_filter_level for an intra frame's block: the reference is always intra.</summary>
    private static int FilterLevel(Av1FrameHeader h, Av1BlockInfo b, int plane, int dir)
    {
        int baseLevel = plane == 0 ? h.LoopFilterLevel[dir] : h.LoopFilterLevel[plane + 1];
        int level = baseLevel;
        if (h.DeltaLfPresent)
        {
            int delta = h.DeltaLfMulti ? b.DeltaLf[plane == 0 ? dir : plane + 1] : b.DeltaLf[0];
            level = Math.Clamp(delta + baseLevel, 0, 63);
        }

        int feature = plane == 0 ? 1 + dir : plane + 2;
        if (h.SegmentationEnabled && h.FeatureEnabled[b.SegmentId, feature])
        {
            level = Math.Clamp(level + h.FeatureData[b.SegmentId, feature], 0, 63);
        }

        if (h.LoopFilterDeltaEnabled)
        {
            level = Math.Clamp(level + (h.LoopFilterRefDeltas[0] * (1 << (level >> 5))), 0, 63);
        }

        return level;
    }

    /// <summary>Filters four rows (or columns) across an edge: <paramref name="step"/> crosses it, <paramref name="along"/> follows it.</summary>
    private static void FilterEdge(ushort[] s, int at, int step, int along, int length, int level, int sharpness, int bitDepth)
    {
        int shift = bitDepth - 8;
        int limit = level >> ((sharpness > 0 ? 1 : 0) + (sharpness > 4 ? 1 : 0));
        if (sharpness > 0 && limit > 9 - sharpness)
        {
            limit = 9 - sharpness;
        }

        limit = Math.Max(limit, 1);
        int blimit = ((2 * (level + 2)) + limit) << shift;
        limit <<= shift;
        int thresh = (level >> 4) << shift;
        int flatThresh = 1 << shift;
        for (int i = 0; i < 4; i++, at += along)
        {
            int p0 = s[at - step], q0 = s[at], p1 = s[at - (2 * step)], q1 = s[at + step];
            bool mask;
            switch (length)
            {
                case 4:
                    mask = Math.Abs(p1 - p0) <= limit && Math.Abs(q1 - q0) <= limit && (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) / 2) <= blimit;
                    if (mask)
                    {
                        Filter4(s, at, step, thresh, bitDepth);
                    }

                    break;
                case 6:
                    {
                        int p2 = s[at - (3 * step)], q2 = s[at + (2 * step)];
                        mask = Math.Abs(p2 - p1) <= limit && Math.Abs(p1 - p0) <= limit && Math.Abs(q1 - q0) <= limit && Math.Abs(q2 - q1) <= limit
                            && (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) / 2) <= blimit;
                        if (!mask)
                        {
                            break;
                        }

                        bool flat = Math.Abs(p1 - p0) <= flatThresh && Math.Abs(q1 - q0) <= flatThresh && Math.Abs(p2 - p0) <= flatThresh
                            && Math.Abs(q2 - q0) <= flatThresh;
                        if (flat)
                        {
                            s[at - (2 * step)] = (ushort)(((p2 * 3) + (p1 * 2) + (p0 * 2) + q0 + 4) >> 3);
                            s[at - step] = (ushort)((p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + 4) >> 3);
                            s[at] = (ushort)((p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + 4) >> 3);
                            s[at + step] = (ushort)((p0 + (q0 * 2) + (q1 * 2) + (q2 * 3) + 4) >> 3);
                        }
                        else
                        {
                            Filter4(s, at, step, thresh, bitDepth);
                        }

                        break;
                    }

                default:
                    {
                        int p2 = s[at - (3 * step)], q2 = s[at + (2 * step)], p3 = s[at - (4 * step)], q3 = s[at + (3 * step)];
                        mask = Math.Abs(p3 - p2) <= limit && Math.Abs(p2 - p1) <= limit && Math.Abs(p1 - p0) <= limit && Math.Abs(q1 - q0) <= limit
                            && Math.Abs(q2 - q1) <= limit && Math.Abs(q3 - q2) <= limit && (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) / 2) <= blimit;
                        if (!mask)
                        {
                            break;
                        }

                        bool flat = Math.Abs(p1 - p0) <= flatThresh && Math.Abs(q1 - q0) <= flatThresh && Math.Abs(p2 - p0) <= flatThresh
                            && Math.Abs(q2 - q0) <= flatThresh && Math.Abs(p3 - p0) <= flatThresh && Math.Abs(q3 - q0) <= flatThresh;
                        if (length == 14 && flat)
                        {
                            int p4 = s[at - (5 * step)], p5 = s[at - (6 * step)], p6 = s[at - (7 * step)];
                            int q4 = s[at + (4 * step)], q5 = s[at + (5 * step)], q6 = s[at + (6 * step)];
                            bool flat2 = Math.Abs(p4 - p0) <= flatThresh && Math.Abs(q4 - q0) <= flatThresh && Math.Abs(p5 - p0) <= flatThresh
                                && Math.Abs(q5 - q0) <= flatThresh && Math.Abs(p6 - p0) <= flatThresh && Math.Abs(q6 - q0) <= flatThresh;
                            if (flat2)
                            {
                                s[at - (6 * step)] = (ushort)(((p6 * 7) + (p5 * 2) + (p4 * 2) + p3 + p2 + p1 + p0 + q0 + 8) >> 4);
                                s[at - (5 * step)] = (ushort)(((p6 * 5) + (p5 * 2) + (p4 * 2) + (p3 * 2) + p2 + p1 + p0 + q0 + q1 + 8) >> 4);
                                s[at - (4 * step)] = (ushort)(((p6 * 4) + p5 + (p4 * 2) + (p3 * 2) + (p2 * 2) + p1 + p0 + q0 + q1 + q2 + 8) >> 4);
                                s[at - (3 * step)] = (ushort)(((p6 * 3) + p5 + p4 + (p3 * 2) + (p2 * 2) + (p1 * 2) + p0 + q0 + q1 + q2 + q3 + 8) >> 4);
                                s[at - (2 * step)] = (ushort)(((p6 * 2) + p5 + p4 + p3 + (p2 * 2) + (p1 * 2) + (p0 * 2) + q0 + q1 + q2 + q3 + q4 + 8) >> 4);
                                s[at - step] = (ushort)((p6 + p5 + p4 + p3 + p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + q2 + q3 + q4 + q5 + 8) >> 4);
                                s[at] = (ushort)((p5 + p4 + p3 + p2 + p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + q3 + q4 + q5 + q6 + 8) >> 4);
                                s[at + step] = (ushort)((p4 + p3 + p2 + p1 + p0 + (q0 * 2) + (q1 * 2) + (q2 * 2) + q3 + q4 + q5 + (q6 * 2) + 8) >> 4);
                                s[at + (2 * step)] = (ushort)((p3 + p2 + p1 + p0 + q0 + (q1 * 2) + (q2 * 2) + (q3 * 2) + q4 + q5 + (q6 * 3) + 8) >> 4);
                                s[at + (3 * step)] = (ushort)((p2 + p1 + p0 + q0 + q1 + (q2 * 2) + (q3 * 2) + (q4 * 2) + q5 + (q6 * 4) + 8) >> 4);
                                s[at + (4 * step)] = (ushort)((p1 + p0 + q0 + q1 + q2 + (q3 * 2) + (q4 * 2) + (q5 * 2) + (q6 * 5) + 8) >> 4);
                                s[at + (5 * step)] = (ushort)((p0 + q0 + q1 + q2 + q3 + (q4 * 2) + (q5 * 2) + (q6 * 7) + 8) >> 4);
                                break;
                            }
                        }

                        if (flat)
                        {
                            s[at - (3 * step)] = (ushort)((p3 + p3 + p3 + (2 * p2) + p1 + p0 + q0 + 4) >> 3);
                            s[at - (2 * step)] = (ushort)((p3 + p3 + p2 + (2 * p1) + p0 + q0 + q1 + 4) >> 3);
                            s[at - step] = (ushort)((p3 + p2 + p1 + (2 * p0) + q0 + q1 + q2 + 4) >> 3);
                            s[at] = (ushort)((p2 + p1 + p0 + (2 * q0) + q1 + q2 + q3 + 4) >> 3);
                            s[at + step] = (ushort)((p1 + p0 + q0 + (2 * q1) + q2 + q3 + q3 + 4) >> 3);
                            s[at + (2 * step)] = (ushort)((p0 + q0 + q1 + (2 * q2) + q3 + q3 + q3 + 4) >> 3);
                        }
                        else
                        {
                            Filter4(s, at, step, thresh, bitDepth);
                        }

                        break;
                    }
            }
        }
    }

    /// <summary>highbd_filter4: the narrow filter, its taps limited when the edge has high variance.</summary>
    private static void Filter4(ushort[] s, int at, int step, int thresh, int bitDepth)
    {
        int shift = bitDepth - 8;
        int offset = 0x80 << shift;
        int lo = -128 << shift, hi = (128 << shift) - 1;
        int ps1 = s[at - (2 * step)] - offset, ps0 = s[at - step] - offset, qs0 = s[at] - offset, qs1 = s[at + step] - offset;
        bool hev = Math.Abs(ps1 - ps0) > thresh || Math.Abs(qs1 - qs0) > thresh;
        int filter = hev ? Math.Clamp(ps1 - qs1, lo, hi) : 0;
        filter = Math.Clamp(filter + (3 * (qs0 - ps0)), lo, hi);
        int filter1 = Math.Clamp(filter + 4, lo, hi) >> 3;
        int filter2 = Math.Clamp(filter + 3, lo, hi) >> 3;
        s[at] = (ushort)(Math.Clamp(qs0 - filter1, lo, hi) + offset);
        s[at - step] = (ushort)(Math.Clamp(ps0 + filter2, lo, hi) + offset);
        filter = hev ? 0 : (filter1 + 1) >> 1;
        s[at + step] = (ushort)(Math.Clamp(qs1 - filter, lo, hi) + offset);
        s[at - (2 * step)] = (ushort)(Math.Clamp(ps1 + filter, lo, hi) + offset);
    }
}
