namespace Egami.Heic.Hevc;

/// <summary>
/// The in-loop filters of a decoded picture (H.265 8.7): the deblocking filter (8.7.2), every vertical edge of the picture then
/// every horizontal one, and sample adaptive offset (8.7.3) on the deblocked samples.
/// </summary>
internal static class HevcLoopFilter
{
    private static readonly byte[] Beta =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24, 26, 28, 30, 32, 34,
        36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64,
    ];

    private static readonly byte[] Tc =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6,
        7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 22, 24,
    ];

    /// <summary>
    /// Deblocks, then applies SAO to, a decoded picture, on up to <paramref name="parallelism"/> threads by CTB rows. Vertical
    /// edges of different rows touch different samples, and so do horizontal edges 8 rows apart (each reads 4 and changes 3
    /// samples on either side), so the output does not depend on the number of threads.
    /// </summary>
    internal static void Apply(HevcPicture picture, int parallelism = 1, CancellationToken cancellationToken = default)
    {
        HevcSps sps = picture.Sps;
        int rows = sps.HeightInCtbs, rowHeight4 = 1 << (sps.CtbLog2Size - 2);
        foreach (bool vertical in (ReadOnlySpan<bool>)[true, false])
        {
            HevcThreads.For(rows, parallelism, row =>
                Deblock(picture, vertical, row * rowHeight4, Math.Min((row + 1) * rowHeight4, picture.Height4)), cancellationToken);
        }

        Sao(picture, parallelism, cancellationToken);
    }

    // ---------------------------------------------------------------- deblocking (8.7.2)

    private static void Deblock(HevcPicture picture, bool vertical, int y4Start, int y4End)
    {
        HevcSps sps = picture.Sps;
        HevcPps pps = picture.Pps;
        int w4 = picture.Width4;
        byte edgeFlag = vertical ? HevcPicture.EdgeLeft : HevcPicture.EdgeTop;

        // Luma edges on the 8x8 grid, in segments of four lines.
        for (int y4 = y4Start; y4 < y4End; y4++)
        {
            for (int x4 = 0; x4 < w4; x4++)
            {
                if ((vertical ? x4 : y4) % 2 != 0 || (vertical ? x4 : y4) == 0)
                {
                    continue;
                }

                int q = (y4 * w4) + x4;
                if ((picture.Flags[q] & edgeFlag) == 0)
                {
                    continue;
                }

                int p = vertical ? q - 1 : q - w4;
                if (!FilterEdge(picture, x4 << 2, y4 << 2, vertical, out HevcSliceHeader slice))
                {
                    continue;
                }

                int qpL = (picture.QpY[q] + picture.QpY[p] + 1) >> 1;
                bool noP = NoFilter(picture, p), noQ = NoFilter(picture, q);
                LumaEdge(picture, x4 << 2, y4 << 2, vertical, qpL, slice, noP, noQ);
                if (sps.ChromaArrayType != 0)
                {
                    int sub = vertical ? sps.SubWidthC : sps.SubHeightC;
                    int lumaPosition = vertical ? x4 << 2 : y4 << 2;
                    if ((lumaPosition / sub) % 8 == 0)
                    {
                        for (int c = 1; c <= 2; c++)
                        {
                            int offset = c == 1 ? pps.CbQpOffset : pps.CrQpOffset;
                            ChromaEdge(picture, c, x4 << 2, y4 << 2, vertical, qpL + offset, slice, noP, noQ);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets filterEdgeFlag for the edge left of or above (x, y) (8.7.2.3): not at the picture boundary, not across a tile
    /// boundary or a slice boundary that does not allow it, and not when the slice of q0 turns the filter off.
    /// </summary>
    private static bool FilterEdge(HevcPicture picture, int x, int y, bool vertical, out HevcSliceHeader slice)
    {
        HevcSps sps = picture.Sps;
        int widthCtbs = sps.WidthInCtbs;
        int ctbQ = ((y >> sps.CtbLog2Size) * widthCtbs) + (x >> sps.CtbLog2Size);
        int xP = vertical ? x - 1 : x, yP = vertical ? y : y - 1;
        int ctbP = ((yP >> sps.CtbLog2Size) * widthCtbs) + (xP >> sps.CtbLog2Size);
        slice = picture.CtbSliceHeader[ctbQ]!;
        if (slice is null || slice.DeblockingDisabled)
        {
            return false;
        }

        if (ctbP != ctbQ)
        {
            HevcLayout layout = picture.Layout;
            if (!picture.Pps.LoopFilterAcrossTiles && layout.TileId[layout.RsToTs[ctbP]] != layout.TileId[layout.RsToTs[ctbQ]])
            {
                return false;
            }

            if (picture.CtbSlice[ctbP] != picture.CtbSlice[ctbQ] && !slice.LoopFilterAcrossSlices)
            {
                return false;
            }
        }

        return true;
    }

    private static bool NoFilter(HevcPicture picture, int block)
    {
        byte flags = picture.Flags[block];
        return (flags & HevcPicture.Bypass) != 0 || ((flags & HevcPicture.Pcm) != 0 && picture.Sps.PcmLoopFilterDisabled);
    }

    private static void LumaEdge(HevcPicture picture, int x, int y, bool vertical, int qpL, HevcSliceHeader slice, bool noP, bool noQ)
    {
        HevcSps sps = picture.Sps;
        int bitDepth = sps.BitDepthLuma;
        int beta = Beta[Math.Clamp(qpL + (slice.BetaOffsetDiv2 << 1), 0, 51)] << (bitDepth - 8);
        int tc = Tc[Math.Clamp(qpL + 2 + (slice.TcOffsetDiv2 << 1), 0, 53)] << (bitDepth - 8);
        if (tc == 0 && beta == 0)
        {
            return;
        }

        ushort[] plane = picture.Planes[0];
        int stride = picture.Widths[0];

        // Along the edge (step) and across it (offset).
        int step = vertical ? stride : 1, offset = vertical ? 1 : stride;
        int at = (y * stride) + x;
        int max = (1 << bitDepth) - 1;
        int line0 = at, line3 = at + (3 * step);
        int dp0 = Math.Abs(plane[line0 - (3 * offset)] - (2 * plane[line0 - (2 * offset)]) + plane[line0 - offset]);
        int dp3 = Math.Abs(plane[line3 - (3 * offset)] - (2 * plane[line3 - (2 * offset)]) + plane[line3 - offset]);
        int dq0 = Math.Abs(plane[line0 + (2 * offset)] - (2 * plane[line0 + offset]) + plane[line0]);
        int dq3 = Math.Abs(plane[line3 + (2 * offset)] - (2 * plane[line3 + offset]) + plane[line3]);
        int dpq0 = dp0 + dq0, dpq3 = dp3 + dq3, dp = dp0 + dp3, dq = dq0 + dq3;
        int d = dpq0 + dpq3;
        if (d >= beta)
        {
            return;
        }

        bool strong = Strong(plane, line0, offset, 2 * dpq0, beta, tc) && Strong(plane, line3, offset, 2 * dpq3, beta, tc);
        bool dEp = dp < ((beta + (beta >> 1)) >> 3), dEq = dq < ((beta + (beta >> 1)) >> 3);
        for (int k = 0; k < 4; k++)
        {
            int i = at + (k * step);
            int p0 = plane[i - offset], p1 = plane[i - (2 * offset)], p2 = plane[i - (3 * offset)], p3 = plane[i - (4 * offset)];
            int q0 = plane[i], q1 = plane[i + offset], q2 = plane[i + (2 * offset)], q3 = plane[i + (3 * offset)];
            if (strong)
            {
                int tc2 = 2 * tc;
                if (!noP)
                {
                    plane[i - offset] = (ushort)Math.Clamp((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3, p0 - tc2, p0 + tc2);
                    plane[i - (2 * offset)] = (ushort)Math.Clamp((p2 + p1 + p0 + q0 + 2) >> 2, p1 - tc2, p1 + tc2);
                    plane[i - (3 * offset)] = (ushort)Math.Clamp(((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3, p2 - tc2, p2 + tc2);
                }

                if (!noQ)
                {
                    plane[i] = (ushort)Math.Clamp((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3, q0 - tc2, q0 + tc2);
                    plane[i + offset] = (ushort)Math.Clamp((p0 + q0 + q1 + q2 + 2) >> 2, q1 - tc2, q1 + tc2);
                    plane[i + (2 * offset)] = (ushort)Math.Clamp((p0 + q0 + q1 + (3 * q2) + (2 * q3) + 4) >> 3, q2 - tc2, q2 + tc2);
                }
            }
            else
            {
                int delta = ((9 * (q0 - p0)) - (3 * (q1 - p1)) + 8) >> 4;
                if (Math.Abs(delta) >= tc * 10)
                {
                    continue;
                }

                delta = Math.Clamp(delta, -tc, tc);
                if (!noP)
                {
                    plane[i - offset] = (ushort)Math.Clamp(p0 + delta, 0, max);
                    if (dEp)
                    {
                        int deltaP = Math.Clamp(((((p2 + p0 + 1) >> 1) - p1) + delta) >> 1, -(tc >> 1), tc >> 1);
                        plane[i - (2 * offset)] = (ushort)Math.Clamp(p1 + deltaP, 0, max);
                    }
                }

                if (!noQ)
                {
                    plane[i] = (ushort)Math.Clamp(q0 - delta, 0, max);
                    if (dEq)
                    {
                        int deltaQ = Math.Clamp(((((q2 + q0 + 1) >> 1) - q1) - delta) >> 1, -(tc >> 1), tc >> 1);
                        plane[i + offset] = (ushort)Math.Clamp(q1 + deltaQ, 0, max);
                    }
                }
            }
        }
    }

    private static bool Strong(ushort[] plane, int i, int offset, int dpq, int beta, int tc)
    {
        int p0 = plane[i - offset], p3 = plane[i - (4 * offset)], q0 = plane[i], q3 = plane[i + (3 * offset)];
        return dpq < (beta >> 2) && Math.Abs(p3 - p0) + Math.Abs(q0 - q3) < (beta >> 3) && Math.Abs(p0 - q0) < ((5 * tc) + 1) >> 1;
    }

    private static void ChromaEdge(HevcPicture picture, int c, int x, int y, bool vertical, int qpi, HevcSliceHeader slice, bool noP, bool noQ)
    {
        HevcSps sps = picture.Sps;
        int bitDepth = sps.BitDepthChroma;
        int qpc = sps.ChromaArrayType == 1 ? HevcSliceDecoder.ChromaQp(qpi) : Math.Min(qpi, 51);
        int tc = Tc[Math.Clamp(qpc + 2 + (slice.TcOffsetDiv2 << 1), 0, 53)] << (bitDepth - 8);
        if (tc == 0)
        {
            return;
        }

        ushort[] plane = picture.Planes[c];
        int stride = picture.Widths[c];
        int xc = x / sps.SubWidthC, yc = y / sps.SubHeightC;
        int lines = 4 / (vertical ? sps.SubHeightC : sps.SubWidthC);
        int step = vertical ? stride : 1, offset = vertical ? 1 : stride;
        int at = (yc * stride) + xc;
        int max = (1 << bitDepth) - 1;
        for (int k = 0; k < lines; k++)
        {
            int i = at + (k * step);
            int p0 = plane[i - offset], p1 = plane[i - (2 * offset)], q0 = plane[i], q1 = plane[i + offset];
            int delta = Math.Clamp(((((q0 - p0) << 2) + p1 - q1) + 4) >> 3, -tc, tc);
            if (!noP)
            {
                plane[i - offset] = (ushort)Math.Clamp(p0 + delta, 0, max);
            }

            if (!noQ)
            {
                plane[i] = (ushort)Math.Clamp(q0 - delta, 0, max);
            }
        }
    }

    // ---------------------------------------------------------------- SAO (8.7.3)

    private static readonly int[][] EdgeOffsets = [[-1, 0, 1, 0], [0, -1, 0, 1], [-1, -1, 1, 1], [1, -1, -1, 1]];

    private static void Sao(HevcPicture picture, int parallelism, CancellationToken cancellationToken)
    {
        HevcSps sps = picture.Sps;
        if (!sps.SaoEnabled)
        {
            return;
        }

        int ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
        bool any = false;
        for (int i = 0; i < ctbs && !any; i++)
        {
            HevcSliceHeader? slice = picture.CtbSliceHeader[i];
            any = slice is not null && (slice.SaoLuma || slice.SaoChroma);
        }

        if (!any)
        {
            return;
        }

        int components = picture.Planes.Length;
        for (int c = 0; c < components; c++)
        {
            // Each CTB reads the deblocked samples and writes only its own.
            ushort[] deblocked = (ushort[])picture.Planes[c].Clone();
            int component = c;
            HevcThreads.For(sps.HeightInCtbs, parallelism, row =>
            {
                for (int ctb = row * sps.WidthInCtbs; ctb < (row + 1) * sps.WidthInCtbs; ctb++)
                {
                    HevcSliceHeader? slice = picture.CtbSliceHeader[ctb];
                    if (slice is null || (component == 0 ? !slice.SaoLuma : !slice.SaoChroma))
                    {
                        continue;
                    }

                    HevcSaoParameters sao = picture.Sao[ctb];
                    if (sao.Type(component) != 0)
                    {
                        SaoCtb(picture, component, ctb, sao, deblocked);
                    }
                }
            }, cancellationToken);
        }
    }

    private static void SaoCtb(HevcPicture picture, int c, int ctb, HevcSaoParameters sao, ushort[] deblocked)
    {
        HevcSps sps = picture.Sps;
        int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
        int ctbSize = 1 << sps.CtbLog2Size;
        int width = picture.Widths[c], height = picture.Heights[c];
        int x0 = ((ctb % sps.WidthInCtbs) * ctbSize) / sx, y0 = ((ctb / sps.WidthInCtbs) * ctbSize) / sy;
        int w = Math.Min(ctbSize / sx, width - x0), h = Math.Min(ctbSize / sy, height - y0);
        int bitDepth = c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
        int max = (1 << bitDepth) - 1;
        ushort[] plane = picture.Planes[c];
        HevcSaoParameters.OffsetSet offsets = sao.Offsets(c);
        bool checkFlags = sps.PcmLoopFilterDisabled || picture.Pps.TransquantBypassEnabled;
        if (sao.Type(c) == 1)
        {
            Span<int> table = stackalloc int[32];
            table.Clear();
            int position = sao.Class(c);
            for (int k = 0; k < 4; k++)
            {
                table[(k + position) & 31] = k + 1;
            }

            int shift = bitDepth - 5;
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    if (checkFlags && Skip(picture, x * sx, y * sy))
                    {
                        continue;
                    }

                    int i = (y * width) + x;
                    int band = table[deblocked[i] >> shift];
                    if (band != 0)
                    {
                        plane[i] = (ushort)Math.Clamp(deblocked[i] + offsets[band - 1], 0, max);
                    }
                }
            }

            return;
        }

        int[] direction = EdgeOffsets[sao.Class(c)];
        HevcLayout layout = picture.Layout;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int ax = x + direction[0], ay = y + direction[1], bx = x + direction[2], by = y + direction[3];
                if (ax < 0 || ay < 0 || bx < 0 || by < 0 || ax >= width || bx >= width || ay >= height || by >= height)
                {
                    continue;
                }

                if (checkFlags && Skip(picture, x * sx, y * sy))
                {
                    continue;
                }

                if (!NeighbourAllowed(picture, layout, x * sx, y * sy, ax * sx, ay * sy) || !NeighbourAllowed(picture, layout, x * sx, y * sy, bx * sx, by * sy))
                {
                    continue;
                }

                int i = (y * width) + x;
                int value = deblocked[i];
                int edge = 2 + Math.Sign(value - deblocked[(ay * width) + ax]) + Math.Sign(value - deblocked[(by * width) + bx]);
                int index = edge switch { 0 => 1, 1 => 2, 2 => 0, _ => edge };
                if (index != 0)
                {
                    plane[i] = (ushort)Math.Clamp(value + offsets[index - 1], 0, max);
                }
            }
        }
    }

    /// <summary>Gets whether SAO leaves a sample unchanged: PCM with the loop filter off, or transquant bypass.</summary>
    private static bool Skip(HevcPicture picture, int xLuma, int yLuma)
    {
        byte flags = picture.Flags[((yLuma >> 2) * picture.Width4) + (xLuma >> 2)];
        return (flags & HevcPicture.Bypass) != 0 || ((flags & HevcPicture.Pcm) != 0 && picture.Sps.PcmLoopFilterDisabled);
    }

    /// <summary>
    /// Gets whether an edge offset neighbour may be used (8.7.3.2): not across a slice boundary the later slice forbids, nor
    /// across a tile boundary when the PPS forbids it.
    /// </summary>
    private static bool NeighbourAllowed(HevcPicture picture, HevcLayout layout, int xC, int yC, int xN, int yN)
    {
        HevcSps sps = picture.Sps;
        int ctbC = ((yC >> sps.CtbLog2Size) * sps.WidthInCtbs) + (xC >> sps.CtbLog2Size);
        int ctbN = ((yN >> sps.CtbLog2Size) * sps.WidthInCtbs) + (xN >> sps.CtbLog2Size);
        if (ctbC == ctbN)
        {
            return true;
        }

        if (picture.CtbSlice[ctbC] != picture.CtbSlice[ctbN])
        {
            int zC = picture.ZScan[((yC >> 2) * picture.Width4) + (xC >> 2)], zN = picture.ZScan[((yN >> 2) * picture.Width4) + (xN >> 2)];
            HevcSliceHeader later = zN < zC ? picture.CtbSliceHeader[ctbC]! : picture.CtbSliceHeader[ctbN]!;
            if (later is null || !later.LoopFilterAcrossSlices)
            {
                return false;
            }
        }

        return picture.Pps.LoopFilterAcrossTiles || layout.TileId[layout.RsToTs[ctbC]] == layout.TileId[layout.RsToTs[ctbN]];
    }
}
