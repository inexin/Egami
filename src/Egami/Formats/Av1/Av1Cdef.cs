namespace Egami.Formats.Av1;

/// <summary>
/// The constrained directional enhancement filter, after libaom's cdef.c and cdef_block.c (BSD 2-clause, see
/// THIRD-PARTY-NOTICES): each 8x8 block that codes a residual is filtered along its luma direction, every tap read from the
/// deblocked frame and samples outside the frame left out.
/// </summary>
internal static class Av1Cdef
{
    private const int VeryLarge = 0x4000;

    private static ReadOnlySpan<int> DivTable => [0, 840, 420, 280, 210, 168, 140, 120, 105];

    private static ReadOnlySpan<sbyte> Conv422 => [7, 0, 2, 4, 5, 6, 6, 6];

    private static ReadOnlySpan<sbyte> Conv440 => [1, 2, 2, 2, 3, 4, 6, 0];

    /// <summary>cdef_directions: the two taps of each direction, as (row, column) offsets.</summary>
    private static ReadOnlySpan<sbyte> Directions =>
    [
        -1, 1, -2, 2, 0, 1, -1, 2, 0, 1, 0, 2, 0, 1, 1, 2, 1, 1, 2, 2, 1, 0, 2, 1, 1, 0, 2, 0, 1, 0, 2, -1,
    ];

    /// <summary>Filters the frame's planes in place.</summary>
    internal static void Apply(Av1Frame frame)
    {
        Av1FrameHeader h = frame.Header;
        if (h.CodedLossless || h.AllowIntrabc)
        {
            return;
        }

        bool any = h.CdefBits != 0 || h.CdefYPriStrength[0] != 0 || h.CdefYSecStrength[0] != 0 || h.CdefUvPriStrength[0] != 0
            || h.CdefUvSecStrength[0] != 0;
        if (!any)
        {
            return;
        }

        int planes = h.NumPlanes;
        ushort[][] source = new ushort[planes][];
        for (int plane = 0; plane < planes; plane++)
        {
            source[plane] = (ushort[])frame.Planes![plane].Clone();
        }

        int coeffShift = frame.Sequence.BitDepth - 8;
        int ssx = frame.Sequence.SubsamplingX, ssy = frame.Sequence.SubsamplingY;
        int rows64 = (frame.MiRows + 15) >> 4, cols64 = (frame.MiCols + 15) >> 4;
        // Every tap reads the deblocked copy, so the rows of 64x64 units filter on their own.
        frame.For(rows64, fbr =>
        {
            Span<int> dirs = stackalloc int[64];
            Span<int> vars = stackalloc int[64];
            Span<byte> list = stackalloc byte[64];
            for (int fbc = 0; fbc < cols64; fbc++)
            {
                int index = frame.CdefIndex[(fbr * frame.CdefStride) + fbc];
                if (index < 0)
                {
                    continue;
                }

                int yPri = h.CdefYPriStrength[index], ySec = h.CdefYSecStrength[index];
                int uvPri = h.CdefUvPriStrength[index], uvSec = h.CdefUvSecStrength[index];
                if (yPri == 0 && ySec == 0 && (planes == 1 || (uvPri == 0 && uvSec == 0)))
                {
                    continue;
                }

                // av1_cdef_compute_sb_list: the 8x8 blocks with a residual somewhere in them.
                int maxr = Math.Min(frame.MiRows - (fbr * 16), 16), maxc = Math.Min(frame.MiCols - (fbc * 16), 16);
                int count = 0;
                for (int r = 0; r < maxr; r += 2)
                {
                    for (int c = 0; c < maxc; c += 2)
                    {
                        if (!AllSkip(frame, (fbr * 16) + r, (fbc * 16) + c))
                        {
                            list[count++] = (byte)(((r >> 1) << 3) | (c >> 1));
                        }
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                for (int plane = 0; plane < planes; plane++)
                {
                    int pri = plane == 0 ? yPri : uvPri, sec = plane == 0 ? ySec : uvSec;
                    if (plane > 0 && pri == 0 && sec == 0)
                    {
                        continue;
                    }

                    int xdec = plane > 0 ? ssx : 0, ydec = plane > 0 ? ssy : 0;
                    FilterUnit(frame, source[plane], plane, fbr, fbc, list[..count], dirs, vars, pri, sec, xdec, ydec, coeffShift, ssx, ssy);
                }
            }
        });
    }

    private static bool AllSkip(Av1Frame frame, int miRow, int miCol)
    {
        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 2; c++)
            {
                if (!frame.Grid[((miRow + r) * frame.MiCols) + miCol + c]!.Skip)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void FilterUnit(Av1Frame frame, ushort[] src, int plane, int fbr, int fbc, ReadOnlySpan<byte> list, Span<int> dirs, Span<int> vars,
        int level, int secStrength, int xdec, int ydec, int coeffShift, int ssx, int ssy)
    {
        ushort[] dst = frame.Planes![plane];
        int stride = frame.Strides[plane];
        int width = (frame.MiCols * 4) >> xdec, height = (frame.MiRows * 4) >> ydec;
        int priStrength = level << coeffShift;
        secStrength <<= coeffShift;
        int damping = frame.Header.CdefDamping + coeffShift - (plane > 0 ? 1 : 0);
        int bw = 8 >> xdec, bh = 8 >> ydec;
        int unitX = (fbc * 64) >> xdec, unitY = (fbr * 64) >> ydec;
        if (plane == 0)
        {
            foreach (byte entry in list)
            {
                int by = entry >> 3, bx = entry & 7;
                dirs[entry] = FindDirection(src, ((unitY + (by * 8)) * stride) + unitX + (bx * 8), stride, coeffShift, out vars[entry]);
            }
        }
        else if (plane == 1 && xdec != ydec)
        {
            foreach (byte entry in list)
            {
                dirs[entry] = (xdec != 0 ? Conv422 : Conv440)[dirs[entry]];
            }
        }

        foreach (byte entry in list)
        {
            int by = entry >> 3, bx = entry & 7;
            int t = plane > 0 ? priStrength : AdjustStrength(priStrength, vars[entry]);
            int dir = priStrength != 0 ? dirs[entry] : 0;
            FilterBlock(src, dst, stride, unitX + (bx * bw), unitY + (by * bh), bw, bh, width, height, t, secStrength, dir, damping, coeffShift);
        }
    }

    private static int AdjustStrength(int strength, int variance)
    {
        int i = variance >> 6 != 0 ? Math.Min(31 - int.LeadingZeroCount(variance >> 6), 12) : 0;
        return variance != 0 ? ((strength * (4 + i)) + 8) >> 4 : 0;
    }

    /// <summary>cdef_find_dir: the direction of an 8x8 luma block, and how much more variance it has across than along.</summary>
    private static int FindDirection(ushort[] img, int at, int stride, int coeffShift, out int variance)
    {
        Span<int> cost = stackalloc int[8];
        Span<int> partial = stackalloc int[8 * 15];
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                int x = (img[at + (i * stride) + j] >> coeffShift) - 128;
                partial[(0 * 15) + i + j] += x;
                partial[(1 * 15) + i + (j / 2)] += x;
                partial[(2 * 15) + i] += x;
                partial[(3 * 15) + 3 + i - (j / 2)] += x;
                partial[(4 * 15) + 7 + i - j] += x;
                partial[(5 * 15) + 3 - (i / 2) + j] += x;
                partial[(6 * 15) + j] += x;
                partial[(7 * 15) + (i / 2) + j] += x;
            }
        }

        for (int i = 0; i < 8; i++)
        {
            cost[2] += partial[(2 * 15) + i] * partial[(2 * 15) + i];
            cost[6] += partial[(6 * 15) + i] * partial[(6 * 15) + i];
        }

        cost[2] *= DivTable[8];
        cost[6] *= DivTable[8];
        for (int i = 0; i < 7; i++)
        {
            cost[0] += ((partial[i] * partial[i]) + (partial[14 - i] * partial[14 - i])) * DivTable[i + 1];
            cost[4] += ((partial[(4 * 15) + i] * partial[(4 * 15) + i]) + (partial[(4 * 15) + 14 - i] * partial[(4 * 15) + 14 - i])) * DivTable[i + 1];
        }

        cost[0] += partial[7] * partial[7] * DivTable[8];
        cost[4] += partial[(4 * 15) + 7] * partial[(4 * 15) + 7] * DivTable[8];
        for (int i = 1; i < 8; i += 2)
        {
            for (int j = 0; j < 5; j++)
            {
                cost[i] += partial[(i * 15) + 3 + j] * partial[(i * 15) + 3 + j];
            }

            cost[i] *= DivTable[8];
            for (int j = 0; j < 3; j++)
            {
                cost[i] += ((partial[(i * 15) + j] * partial[(i * 15) + j]) + (partial[(i * 15) + 10 - j] * partial[(i * 15) + 10 - j])) * DivTable[(2 * j) + 2];
            }
        }

        int bestCost = 0, bestDir = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cost[i] > bestCost)
            {
                bestCost = cost[i];
                bestDir = i;
            }
        }

        variance = (bestCost - cost[(bestDir + 4) & 7]) >> 10;
        return bestDir;
    }

    /// <summary>constrain() with the threshold's shift worked out once a block: the difference pulled toward 0 as it grows.</summary>
    private static int Constrain(int diff, int threshold, int shift)
    {
        int abs = Math.Abs(diff);
        int magnitude = Math.Min(Math.Max(threshold - (abs >> shift), 0), abs);
        return diff < 0 ? -magnitude : magnitude;
    }

    private static int DampingShift(int threshold, int damping) => threshold == 0 ? 0 : Math.Max(0, damping - (31 - int.LeadingZeroCount(threshold)));

    private static void FilterBlock(ushort[] src, ushort[] dst, int stride, int x0, int y0, int bw, int bh, int width, int height, int priStrength,
        int secStrength, int dir, int damping, int coeffShift)
    {
        bool primary = priStrength != 0, secondary = secStrength != 0;
        bool clipping = primary && secondary;
        int priTap0 = ((priStrength >> coeffShift) & 1) == 0 ? 4 : 3, priTap1 = ((priStrength >> coeffShift) & 1) == 0 ? 2 : 3;
        int priShift = DampingShift(priStrength, damping), secShift = DampingShift(secStrength, damping);
        ReadOnlySpan<sbyte> d = Directions;
        int dirP = dir * 4, dirS1 = ((dir + 2) & 7) * 4, dirS2 = ((dir + 6) & 7) * 4;
        if (x0 >= 2 && y0 >= 2 && x0 + bw + 2 <= width && y0 + bh + 2 <= height)
        {
            // Every tap is inside the frame: offsets instead of coordinates, and no unavailable samples to leave out.
            int p0 = (d[dirP] * stride) + d[dirP + 1], p1 = (d[dirP + 2] * stride) + d[dirP + 3];
            int a0 = (d[dirS1] * stride) + d[dirS1 + 1], a1 = (d[dirS1 + 2] * stride) + d[dirS1 + 3];
            int b0 = (d[dirS2] * stride) + d[dirS2 + 1], b1 = (d[dirS2 + 2] * stride) + d[dirS2 + 3];
            for (int i = 0; i < bh; i++)
            {
                int row = ((y0 + i) * stride) + x0;
                for (int j = 0; j < bw; j++)
                {
                    int at = row + j;
                    int value = src[at];
                    int sum = 0, max = value, min = value;
                    if (primary)
                    {
                        int q0 = src[at + p0], q1 = src[at - p0], q2 = src[at + p1], q3 = src[at - p1];
                        sum += priTap0 * (Constrain(q0 - value, priStrength, priShift) + Constrain(q1 - value, priStrength, priShift));
                        sum += priTap1 * (Constrain(q2 - value, priStrength, priShift) + Constrain(q3 - value, priStrength, priShift));
                        if (clipping)
                        {
                            max = Math.Max(Math.Max(q0, q1), Math.Max(Math.Max(q2, q3), max));
                            min = Math.Min(Math.Min(q0, q1), Math.Min(Math.Min(q2, q3), min));
                        }
                    }

                    if (secondary)
                    {
                        int s0 = src[at + a0], s1 = src[at - a0], s2 = src[at + b0], s3 = src[at - b0];
                        int s4 = src[at + a1], s5 = src[at - a1], s6 = src[at + b1], s7 = src[at - b1];
                        sum += 2 * (Constrain(s0 - value, secStrength, secShift) + Constrain(s1 - value, secStrength, secShift)
                            + Constrain(s2 - value, secStrength, secShift) + Constrain(s3 - value, secStrength, secShift));
                        sum += Constrain(s4 - value, secStrength, secShift) + Constrain(s5 - value, secStrength, secShift)
                            + Constrain(s6 - value, secStrength, secShift) + Constrain(s7 - value, secStrength, secShift);
                        if (clipping)
                        {
                            max = Math.Max(Math.Max(Math.Max(s0, s1), Math.Max(s2, s3)), Math.Max(Math.Max(Math.Max(s4, s5), Math.Max(s6, s7)), max));
                            min = Math.Min(Math.Min(Math.Min(s0, s1), Math.Min(s2, s3)), Math.Min(Math.Min(Math.Min(s4, s5), Math.Min(s6, s7)), min));
                        }
                    }

                    int result = value + ((8 + sum - (sum < 0 ? 1 : 0)) >> 4);
                    dst[at] = (ushort)(clipping ? Math.Clamp(result, min, max) : result);
                }
            }

            return;
        }

        for (int i = 0; i < bh; i++)
        {
            int y = y0 + i;
            for (int j = 0; j < bw; j++)
            {
                int x = x0 + j;
                int value = src[(y * stride) + x];
                int sum = 0, max = value, min = value;
                for (int k = 0; k < 2; k++)
                {
                    int tapPri = k == 0 ? priTap0 : priTap1, tapSec = k == 0 ? 2 : 1;
                    if (primary)
                    {
                        int p0 = Fetch(src, stride, width, height, y + d[dirP + (2 * k)], x + d[dirP + (2 * k) + 1]);
                        int p1 = Fetch(src, stride, width, height, y - d[dirP + (2 * k)], x - d[dirP + (2 * k) + 1]);
                        sum += tapPri * Constrain(p0 - value, priStrength, priShift);
                        sum += tapPri * Constrain(p1 - value, priStrength, priShift);
                        if (clipping)
                        {
                            max = MaxAvailable(p0, MaxAvailable(p1, max));
                            min = Math.Min(p0, Math.Min(p1, min));
                        }
                    }

                    if (secondary)
                    {
                        int s0 = Fetch(src, stride, width, height, y + d[dirS1 + (2 * k)], x + d[dirS1 + (2 * k) + 1]);
                        int s1 = Fetch(src, stride, width, height, y - d[dirS1 + (2 * k)], x - d[dirS1 + (2 * k) + 1]);
                        int s2 = Fetch(src, stride, width, height, y + d[dirS2 + (2 * k)], x + d[dirS2 + (2 * k) + 1]);
                        int s3 = Fetch(src, stride, width, height, y - d[dirS2 + (2 * k)], x - d[dirS2 + (2 * k) + 1]);
                        if (clipping)
                        {
                            max = MaxAvailable(s0, MaxAvailable(s1, MaxAvailable(s2, MaxAvailable(s3, max))));
                            min = Math.Min(Math.Min(s0, s1), Math.Min(Math.Min(s2, s3), min));
                        }

                        sum += tapSec * Constrain(s0 - value, secStrength, secShift);
                        sum += tapSec * Constrain(s1 - value, secStrength, secShift);
                        sum += tapSec * Constrain(s2 - value, secStrength, secShift);
                        sum += tapSec * Constrain(s3 - value, secStrength, secShift);
                    }
                }

                int result = value + ((8 + sum - (sum < 0 ? 1 : 0)) >> 4);
                if (clipping)
                {
                    result = Math.Clamp(result, min, max);
                }

                dst[(y * stride) + x] = (ushort)result;
            }
        }

        static int MaxAvailable(int sample, int max) => sample != VeryLarge && sample > max ? sample : max;
    }

    private static int Fetch(ushort[] src, int stride, int width, int height, int y, int x) =>
        (uint)x < (uint)width && (uint)y < (uint)height ? src[(y * stride) + x] : VeryLarge;
}
