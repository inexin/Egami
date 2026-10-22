using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

            int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
            int planeMiRows = (frame.MiRows + ssy) >> ssy, planeMiCols = (frame.MiCols + ssx) >> ssx;
            int[] units = System.Buffers.ArrayPool<int>.Shared.Rent(planeMiRows * planeMiCols);
            try
            {
                Units(frame, plane, ssx, ssy, planeMiRows, planeMiCols, units);
                FilterPlane(frame, plane, units, vertical: true);
                FilterPlane(frame, plane, units, vertical: false);
            }
            finally
            {
                System.Buffers.ArrayPool<int>.Shared.Return(units);
            }
        }
    }

    /// <summary>
    /// What the edges of each 4x4 unit of a plane need from its block, packed: the transform size (bits 0-7), the filter
    /// levels across vertical and horizontal edges (8-13 and 14-19), whether it is a skipped inter block (20) and the log2 of
    /// the plane block's width and height (21-24 and 25-28).
    /// </summary>
    private static void Units(Av1Frame frame, int plane, int ssx, int ssy, int planeMiRows, int planeMiCols, int[] units)
    {
        Av1FrameHeader h = frame.Header;
        frame.For(planeMiRows, y =>
        {
            int miRow = ssy | (y << ssy);
            Av1BlockInfo? last = null;
            int blockBits = 0;
            for (int x = 0; x < planeMiCols; x++)
            {
                int miCol = ssx | (x << ssx);
                if (miRow >= frame.MiRows || miCol >= frame.MiCols)
                {
                    units[(y * planeMiCols) + x] = 0;
                    continue;
                }

                Av1BlockInfo b = frame.Grid[(miRow * frame.MiCols) + miCol]!;
                if (b != last)
                {
                    // What depends on the block alone, worked out once for the units along its row.
                    int bsize = plane == 0 ? b.Size : Av1Tables.PlaneBlockSize(b.Size, ssx, ssy);
                    blockBits = (FilterLevel(h, b, plane, 0) << 8) | (FilterLevel(h, b, plane, 1) << 14) | ((b.Skip && b.IsInter ? 1 : 0) << 20)
                        | (System.Numerics.BitOperations.Log2((uint)Av1Tables.BlockSizeWide[bsize]) << 21) | (System.Numerics.BitOperations.Log2((uint)Av1Tables.BlockSizeHigh[bsize]) << 25);
                    last = b;
                }

                units[(y * planeMiCols) + x] = TransformSize(frame, b, miRow, miCol, plane, ssx, ssy) | blockBits;
            }
        });
    }

    private static void FilterPlane(Av1Frame frame, int plane, int[] units, bool vertical)
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
                        (int txSize, int length, int level) = EdgeParameters(units, planeMiCols, plane, true, x, y, width, height);
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
            // A column's edges go top to bottom, and columns do not touch each other: rows of the band at a time, for the cache.
            frame.For((planeMiCols + 15) >> 4, band =>
            {
                int first = band * 16, count = Math.Min(planeMiCols - first, 16);
                Span<int> next = stackalloc int[16];
                next.Clear();
                for (int y = 0; y < planeMiRows; y++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (next[i] != y)
                        {
                            continue;
                        }

                        int x = first + i;
                        (int txSize, int length, int level) = EdgeParameters(units, planeMiCols, plane, false, x, y, width, height);
                        if (length != 0)
                        {
                            FilterEdge(buf, (y * 4 * stride) + (x * 4), stride, 1, length, level, h.LoopFilterSharpness, bitDepth);
                        }

                        next[i] = y + Av1Tables.TxSizeHighUnit[txSize];
                    }
                }
            });
        }
    }

    /// <summary>set_lpf_parameters: the transform size at 4x4 unit (x, y), and the filter length and level of the edge it starts.</summary>
    private static (int TxSize, int Length, int Level) EdgeParameters(int[] units, int stride, int plane, bool vertical, int x, int y, int width, int height)
    {
        if (x * 4 >= width || y * 4 >= height)
        {
            return (Tx.T4x4, 0, 0);
        }

        int unit = units[(y * stride) + x];
        int ts = unit & 0xff;
        int coord = (vertical ? x : y) * 4;
        int mask = (vertical ? Av1Tables.TxSizeWide[ts] : Av1Tables.TxSizeHigh[ts]) - 1;
        if ((coord & mask) != 0 || coord == 0)
        {
            return (ts, 0, 0);
        }

        int shift = vertical ? 8 : 14;
        int level = (unit >> shift) & 63;
        bool skipped = (unit & (1 << 20)) != 0;
        int previous = units[vertical ? (y * stride) + x - 1 : ((y - 1) * stride) + x];
        int pvTs = previous & 0xff;
        int pvLevel = (previous >> shift) & 63;
        bool pvSkipped = (previous & (1 << 20)) != 0;
        int predMask = (1 << ((unit >> (vertical ? 21 : 25)) & 15)) - 1;
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

    /// <summary>av1_get_filter_level: the level with the block's delta, its segment's and the reference and mode deltas.</summary>
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
            int scale = 1 << (level >> 5);
            int rf = Math.Max(b.RefFrame0, Ref.Intra);
            level += h.LoopFilterRefDeltas[rf] * scale;
            if (rf > Ref.Intra)
            {
                // mode_lf_lut: the global modes take delta 0, every other inter mode delta 1.
                level += h.LoopFilterModeDeltas[b.YMode is InterMode.Global or InterMode.GlobalGlobal ? 0 : 1] * scale;
            }

            level = Math.Clamp(level, 0, 63);
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
        if (Vector128.IsHardwareAccelerated)
        {
            FilterEdgeVector(s, at, step, along, length, new Thresholds(limit, blimit, thresh, flatThresh), bitDepth);
            return;
        }

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

    /// <summary>An edge's limits: its level's limit and blimit, the high edge variance threshold and the flatness threshold.</summary>
    private readonly record struct Thresholds(int Limit, int Blimit, int Thresh, int Flat);

    /// <summary>
    /// <see cref="FilterEdge"/> on its four rows (or columns) at once, a lane each: every filter the length allows worked out,
    /// then each lane given the one its masks choose, as the scalar filter branches. The lanes never touch each other's samples.
    /// </summary>
    private static void FilterEdgeVector(ushort[] s, int at, int step, int along, int length, Thresholds t, int bitDepth)
    {
        ref ushort origin = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s), at);
        int taps = length == 14 ? 7 : length == 4 ? 2 : length == 6 ? 3 : 4;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)(at - (taps * step)), (uint)s.Length, nameof(at));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)(at + ((taps - 1) * step) + (3 * along)), (uint)s.Length, nameof(at));

        Span<Vector128<int>> v = stackalloc Vector128<int>[14];
        for (int k = -taps; k < taps; k++)
        {
            v[7 + k] = Load(ref origin, k * step, along);
        }

        // v[7 + k]: q_k for k >= 0, p_(-1-k) for k < 0.
        Vector128<int> p0 = v[6], p1 = v[5], q0 = v[7], q1 = v[8];
        Vector128<int> limit = Vector128.Create(t.Limit), flatThresh = Vector128.Create(t.Flat);
        Vector128<int> mask = Le(Vector128.Abs(p1 - p0), limit) & Le(Vector128.Abs(q1 - q0), limit)
            & Le((Vector128.Abs(p0 - q0) * 2) + Vector128.ShiftRightArithmetic(Vector128.Abs(p1 - q1), 1), Vector128.Create(t.Blimit));
        Vector128<int> flat = Vector128<int>.Zero, flat2 = Vector128<int>.Zero;
        if (length >= 6)
        {
            Vector128<int> p2 = v[4], q2 = v[9];
            mask &= Le(Vector128.Abs(p2 - p1), limit) & Le(Vector128.Abs(q2 - q1), limit);
            flat = Le(Vector128.Abs(p1 - p0), flatThresh) & Le(Vector128.Abs(q1 - q0), flatThresh) & Le(Vector128.Abs(p2 - p0), flatThresh)
                & Le(Vector128.Abs(q2 - q0), flatThresh);
            if (length >= 8)
            {
                Vector128<int> p3 = v[3], q3 = v[10];
                mask &= Le(Vector128.Abs(p3 - p2), limit) & Le(Vector128.Abs(q3 - q2), limit);
                flat &= Le(Vector128.Abs(p3 - p0), flatThresh) & Le(Vector128.Abs(q3 - q0), flatThresh);
            }

            if (length == 14)
            {
                flat2 = flat;
                for (int k = 4; k < 7; k++)
                {
                    flat2 &= Le(Vector128.Abs(v[6 - k] - p0), flatThresh) & Le(Vector128.Abs(v[7 + k] - q0), flatThresh);
                }
            }
        }

        if (mask == Vector128<int>.Zero)
        {
            return;
        }

        // The narrow filter, for every lane the wider ones leave.
        Span<Vector128<int>> o = stackalloc Vector128<int>[14];
        v.CopyTo(o);
        Filter4(v, o, mask & ~flat, t.Thresh, bitDepth);
        if (length >= 6 && (mask & flat) != Vector128<int>.Zero)
        {
            Vector128<int> f = mask & flat & ~flat2;
            if (length == 6)
            {
                Vector128<int> p2 = v[4], q2 = v[9];
                Vector128<int> four = Vector128.Create(4);
                o[5] = Select(f, Vector128.ShiftRightArithmetic((p2 * 3) + (p1 * 2) + (p0 * 2) + q0 + four, 3), o[5]);
                o[6] = Select(f, Vector128.ShiftRightArithmetic(p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + four, 3), o[6]);
                o[7] = Select(f, Vector128.ShiftRightArithmetic(p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + four, 3), o[7]);
                o[8] = Select(f, Vector128.ShiftRightArithmetic(p0 + (q0 * 2) + (q1 * 2) + (q2 * 3) + four, 3), o[8]);
            }
            else
            {
                Vector128<int> p2 = v[4], q2 = v[9], p3 = v[3], q3 = v[10];
                Vector128<int> four = Vector128.Create(4);
                o[4] = Select(f, Vector128.ShiftRightArithmetic(p3 + p3 + p3 + (2 * p2) + p1 + p0 + q0 + four, 3), o[4]);
                o[5] = Select(f, Vector128.ShiftRightArithmetic(p3 + p3 + p2 + (2 * p1) + p0 + q0 + q1 + four, 3), o[5]);
                o[6] = Select(f, Vector128.ShiftRightArithmetic(p3 + p2 + p1 + (2 * p0) + q0 + q1 + q2 + four, 3), o[6]);
                o[7] = Select(f, Vector128.ShiftRightArithmetic(p2 + p1 + p0 + (2 * q0) + q1 + q2 + q3 + four, 3), o[7]);
                o[8] = Select(f, Vector128.ShiftRightArithmetic(p1 + p0 + q0 + (2 * q1) + q2 + q3 + q3 + four, 3), o[8]);
                o[9] = Select(f, Vector128.ShiftRightArithmetic(p0 + q0 + q1 + (2 * q2) + q3 + q3 + q3 + four, 3), o[9]);
                if (length == 14 && (mask & flat2) != Vector128<int>.Zero)
                {
                    Wide(v, o, mask & flat2);
                }
            }
        }

        for (int k = -taps; k < taps; k++)
        {
            Store(ref origin, k * step, along, o[7 + k]);
        }

        static Vector128<int> Le(Vector128<int> a, Vector128<int> b) => Vector128.LessThanOrEqual(a, b);

        static Vector128<int> Select(Vector128<int> m, Vector128<int> a, Vector128<int> b) => Vector128.ConditionalSelect(m, a, b);

        static Vector128<int> Load(ref ushort origin, int offset, int along) => along == 1
            ? Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref origin, offset)))).AsUInt16()).AsInt32()
            : Vector128.Create(Unsafe.Add(ref origin, offset), Unsafe.Add(ref origin, offset + along), Unsafe.Add(ref origin, offset + (2 * along)),
                Unsafe.Add(ref origin, offset + (3 * along)));

        static void Store(ref ushort origin, int offset, int along, Vector128<int> value)
        {
            if (along == 1)
            {
                Vector128<ushort> narrow = Vector128.Narrow(value.AsUInt32(), value.AsUInt32());
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref origin, offset)), narrow.AsUInt64().ToScalar());
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                Unsafe.Add(ref origin, offset + (i * along)) = (ushort)value.GetElement(i);
            }
        }
    }

    /// <summary>highbd_filter4 on lanes, applied where <paramref name="m"/> is set.</summary>
    private static void Filter4(ReadOnlySpan<Vector128<int>> v, Span<Vector128<int>> o, Vector128<int> m, int thresh, int bitDepth)
    {
        int shift = bitDepth - 8;
        Vector128<int> offset = Vector128.Create(0x80 << shift);
        Vector128<int> lo = Vector128.Create(-128 << shift), hi = Vector128.Create((128 << shift) - 1);
        Vector128<int> ps1 = v[5] - offset, ps0 = v[6] - offset, qs0 = v[7] - offset, qs1 = v[8] - offset;
        Vector128<int> threshold = Vector128.Create(thresh);
        Vector128<int> hev = Vector128.GreaterThan(Vector128.Abs(ps1 - ps0), threshold) | Vector128.GreaterThan(Vector128.Abs(qs1 - qs0), threshold);
        Vector128<int> filter = hev & Clamp(ps1 - qs1, lo, hi);
        filter = Clamp(filter + (qs0 - ps0) * 3, lo, hi);
        Vector128<int> filter1 = Vector128.ShiftRightArithmetic(Clamp(filter + Vector128.Create(4), lo, hi), 3);
        Vector128<int> filter2 = Vector128.ShiftRightArithmetic(Clamp(filter + Vector128.Create(3), lo, hi), 3);
        o[7] = Vector128.ConditionalSelect(m, Clamp(qs0 - filter1, lo, hi) + offset, o[7]);
        o[6] = Vector128.ConditionalSelect(m, Clamp(ps0 + filter2, lo, hi) + offset, o[6]);
        filter = Vector128.AndNot(Vector128.ShiftRightArithmetic(filter1 + Vector128<int>.One, 1), hev);
        o[8] = Vector128.ConditionalSelect(m, Clamp(qs1 - filter, lo, hi) + offset, o[8]);
        o[5] = Vector128.ConditionalSelect(m, Clamp(ps1 + filter, lo, hi) + offset, o[5]);

        static Vector128<int> Clamp(Vector128<int> x, Vector128<int> lo, Vector128<int> hi) => Vector128.Min(Vector128.Max(x, lo), hi);
    }

    /// <summary>The 14-tap filter's outputs on lanes, applied where <paramref name="m"/> is set.</summary>
    private static void Wide(ReadOnlySpan<Vector128<int>> v, Span<Vector128<int>> o, Vector128<int> m)
    {
        Vector128<int> p6 = v[0], p5 = v[1], p4 = v[2], p3 = v[3], p2 = v[4], p1 = v[5], p0 = v[6];
        Vector128<int> q0 = v[7], q1 = v[8], q2 = v[9], q3 = v[10], q4 = v[11], q5 = v[12], q6 = v[13];
        Vector128<int> eight = Vector128.Create(8);
        o[1] = Pick(m, (p6 * 7) + (p5 * 2) + (p4 * 2) + p3 + p2 + p1 + p0 + q0 + eight, o[1]);
        o[2] = Pick(m, (p6 * 5) + (p5 * 2) + (p4 * 2) + (p3 * 2) + p2 + p1 + p0 + q0 + q1 + eight, o[2]);
        o[3] = Pick(m, (p6 * 4) + p5 + (p4 * 2) + (p3 * 2) + (p2 * 2) + p1 + p0 + q0 + q1 + q2 + eight, o[3]);
        o[4] = Pick(m, (p6 * 3) + p5 + p4 + (p3 * 2) + (p2 * 2) + (p1 * 2) + p0 + q0 + q1 + q2 + q3 + eight, o[4]);
        o[5] = Pick(m, (p6 * 2) + p5 + p4 + p3 + (p2 * 2) + (p1 * 2) + (p0 * 2) + q0 + q1 + q2 + q3 + q4 + eight, o[5]);
        o[6] = Pick(m, p6 + p5 + p4 + p3 + p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + q2 + q3 + q4 + q5 + eight, o[6]);
        o[7] = Pick(m, p5 + p4 + p3 + p2 + p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + q3 + q4 + q5 + q6 + eight, o[7]);
        o[8] = Pick(m, p4 + p3 + p2 + p1 + p0 + (q0 * 2) + (q1 * 2) + (q2 * 2) + q3 + q4 + q5 + (q6 * 2) + eight, o[8]);
        o[9] = Pick(m, p3 + p2 + p1 + p0 + q0 + (q1 * 2) + (q2 * 2) + (q3 * 2) + q4 + q5 + (q6 * 3) + eight, o[9]);
        o[10] = Pick(m, p2 + p1 + p0 + q0 + q1 + (q2 * 2) + (q3 * 2) + (q4 * 2) + q5 + (q6 * 4) + eight, o[10]);
        o[11] = Pick(m, p1 + p0 + q0 + q1 + q2 + (q3 * 2) + (q4 * 2) + (q5 * 2) + (q6 * 5) + eight, o[11]);
        o[12] = Pick(m, p0 + q0 + q1 + q2 + q3 + (q4 * 2) + (q5 * 2) + (q6 * 7) + eight, o[12]);

        static Vector128<int> Pick(Vector128<int> m, Vector128<int> sum, Vector128<int> other) =>
            Vector128.ConditionalSelect(m, Vector128.ShiftRightArithmetic(sum, 4), other);
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
