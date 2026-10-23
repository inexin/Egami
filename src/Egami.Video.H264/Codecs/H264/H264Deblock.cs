namespace Egami.Video.Codecs.H264;

/// <summary>The filter settings of one slice: disable_deblocking_filter_idc and FilterOffsetA and B.</summary>
internal readonly record struct H264FilterParams(int DisableIdc, int OffsetA, int OffsetB);

/// <summary>The deblocking filter (8.7) of a decoded frame, macroblock by macroblock in address order.</summary>
internal sealed class H264Deblock
{
    // Boundary strengths of the macroblock: [direction 0 vertical, 1 horizontal][edge 0..3][segment 0..3].
    private readonly int[] _bs = new int[2 * 4 * 4];

    private H264Picture _pic = null!;
    private H264MbState _mb = null!;
    private int _widthMbs;
    private int _bitDepthY;
    private int _bitDepthC;
    private int _chroma;

    /// <summary>Filters the macroblock rows from <paramref name="firstRow"/> to before <paramref name="endRow"/>.</summary>
    internal void Filter(H264Picture pic, H264MbState mb, IReadOnlyList<H264FilterParams> slices, int bitDepthY, int bitDepthC, int firstRow, int endRow)
    {
        _pic = pic;
        _mb = mb;
        _widthMbs = mb.WidthMbs;
        _bitDepthY = bitDepthY;
        _bitDepthC = bitDepthC;
        _chroma = pic.ChromaFormatIdc;
        for (int mbY = firstRow; mbY < endRow; mbY++)
        {
            for (int mbX = 0; mbX < _widthMbs; mbX++)
            {
                int addr = (mbY * _widthMbs) + mbX;
                int slice = mb.Slice[addr];
                if (slice < 0 || slice >= slices.Count)
                {
                    continue;
                }

                H264FilterParams p = slices[slice];
                if (p.DisableIdc == 1)
                {
                    continue;
                }

                bool left = mbX > 0 && mb.Slice[addr - 1] >= 0 && (p.DisableIdc != 2 || mb.Slice[addr - 1] == slice);
                bool top = mbY > 0 && mb.Slice[addr - _widthMbs] >= 0 && (p.DisableIdc != 2 || mb.Slice[addr - _widthMbs] == slice);
                FilterMacroblock(mbX, mbY, addr, left, top, p);
            }
        }
    }

    private void FilterMacroblock(int mbX, int mbY, int addr, bool left, bool top, H264FilterParams p)
    {
        ComputeStrengths(mbX, mbY, addr, left, top);
        bool t8 = _mb.Transform8x8[addr];
        int stride = _pic.Stride;
        int origin = _pic.Origin + (mbY * 16 * stride) + (mbX * 16);
        int qp = _mb.Qp[addr];
        for (int dir = 0; dir < 2; dir++)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                if ((edge == 0 && !(dir == 0 ? left : top)) || (t8 && (edge & 1) != 0))
                {
                    continue;
                }

                int qpP = edge == 0 ? _mb.Qp[dir == 0 ? addr - 1 : addr - _widthMbs] : qp;
                int qpAv = (qpP + qp + 1) >> 1;
                int at = dir == 0 ? origin + (edge * 4) : origin + (edge * 4 * stride);
                FilterEdge(_pic.Y, at, stride, dir, 16, qpAv, p, edge, false, 4, _bitDepthY);
            }
        }

        if (_chroma == 0)
        {
            return;
        }

        int cStride = _pic.ChromaStride;
        int cw = 8, ch = _chroma == 1 ? 8 : 16;
        int cOrigin = _pic.ChromaOrigin + (mbY * ch * cStride) + (mbX * cw);
        for (int c = 0; c < 2; c++)
        {
            ushort[] plane = c == 0 ? _pic.Cb : _pic.Cr;
            sbyte[] qpc = _mb.QpC[c];
            for (int dir = 0; dir < 2; dir++)
            {
                int edges = dir == 0 ? 2 : ch / 4;
                for (int ce = 0; ce < edges; ce++)
                {
                    if (ce == 0 && !(dir == 0 ? left : top))
                    {
                        continue;
                    }

                    // The luma edge whose strengths the chroma edge takes.
                    int lumaEdge = dir == 0 ? ce * 2 : _chroma == 1 ? ce * 2 : ce;
                    int qpP = ce == 0 ? qpc[dir == 0 ? addr - 1 : addr - _widthMbs] : qpc[addr];
                    int qpAv = (qpP + qpc[addr] + 1) >> 1;
                    int at = dir == 0 ? cOrigin + (ce * 4) : cOrigin + (ce * 4 * cStride);
                    int length = dir == 0 ? ch : cw;
                    int segment = dir == 0 ? (_chroma == 1 ? 2 : 4) : 2;
                    FilterEdge(plane, at, cStride, dir, length, qpAv, p, lumaEdge, true, segment, _bitDepthC);
                }
            }
        }
    }

    /// <summary>
    /// Filters one edge of <paramref name="length"/> samples. The strength of sample k is bS[dir][edge][k / segment]: a luma edge has
    /// four segments of 4 samples; a chroma edge takes its luma edge's strengths over fewer samples.
    /// </summary>
    private void FilterEdge(ushort[] plane, int at, int stride, int dir, int length, int qpAv, H264FilterParams p, int edge, bool chroma, int segment, int bitDepth)
    {
        int indexA = Math.Clamp(qpAv + p.OffsetA, 0, 51);
        int indexB = Math.Clamp(qpAv + p.OffsetB, 0, 51);
        int scale = 1 << (bitDepth - 8);
        int alpha = H264Tables.Alpha[indexA] * scale;
        int beta = H264Tables.Beta[indexB] * scale;
        if (alpha == 0 || beta == 0)
        {
            return;
        }

        int max = (1 << bitDepth) - 1;

        // Across the edge (step) and along it (along).
        int step = dir == 0 ? 1 : stride;
        int along = dir == 0 ? stride : 1;
        for (int k = 0; k < length; k++)
        {
            int bS = _bs[(dir * 16) + (edge * 4) + (k / segment)];
            if (bS == 0)
            {
                continue;
            }

            int q0At = at + (k * along);
            int p0 = plane[q0At - step], q0 = plane[q0At];
            int p1 = plane[q0At - (2 * step)], q1 = plane[q0At + step];
            if (Math.Abs(p0 - q0) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta)
            {
                continue;
            }

            if (chroma)
            {
                if (bS < 4)
                {
                    int tc = (H264Tables.Tc0[(indexA * 3) + bS - 1] * scale) + 1;
                    int delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                    plane[q0At - step] = (ushort)Math.Clamp(p0 + delta, 0, max);
                    plane[q0At] = (ushort)Math.Clamp(q0 - delta, 0, max);
                }
                else
                {
                    plane[q0At - step] = (ushort)(((2 * p1) + p0 + q1 + 2) >> 2);
                    plane[q0At] = (ushort)(((2 * q1) + q0 + p1 + 2) >> 2);
                }

                continue;
            }

            int p2 = plane[q0At - (3 * step)], q2 = plane[q0At + (2 * step)];
            int ap = Math.Abs(p2 - p0), aq = Math.Abs(q2 - q0);
            if (bS < 4)
            {
                int tc0 = H264Tables.Tc0[(indexA * 3) + bS - 1] * scale;
                int tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
                int delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                plane[q0At - step] = (ushort)Math.Clamp(p0 + delta, 0, max);
                plane[q0At] = (ushort)Math.Clamp(q0 - delta, 0, max);
                if (ap < beta)
                {
                    plane[q0At - (2 * step)] = (ushort)(p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
                }

                if (aq < beta)
                {
                    plane[q0At + step] = (ushort)(q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
                }

                continue;
            }

            bool strong = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
            if (ap < beta && strong)
            {
                int p3 = plane[q0At - (4 * step)];
                plane[q0At - step] = (ushort)((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3);
                plane[q0At - (2 * step)] = (ushort)((p2 + p1 + p0 + q0 + 2) >> 2);
                plane[q0At - (3 * step)] = (ushort)(((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3);
            }
            else
            {
                plane[q0At - step] = (ushort)(((2 * p1) + p0 + q1 + 2) >> 2);
            }

            if (aq < beta && strong)
            {
                int q3 = plane[q0At + (3 * step)];
                plane[q0At] = (ushort)((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3);
                plane[q0At + step] = (ushort)((p0 + q0 + q1 + q2 + 2) >> 2);
                plane[q0At + (2 * step)] = (ushort)(((2 * q3) + (3 * q2) + q1 + q0 + p0 + 4) >> 3);
            }
            else
            {
                plane[q0At] = (ushort)(((2 * q1) + q0 + p1 + 2) >> 2);
            }
        }
    }

    /// <summary>Derives bS (8.7.2.1) for every 4-sample segment of the macroblock's luma edges, internal ones included.</summary>
    private void ComputeStrengths(int mbX, int mbY, int addr, bool left, bool top)
    {
        int w4 = _mb.W4;
        bool intraQ = _pic.MbIntra[addr];
        for (int dir = 0; dir < 2; dir++)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                bool mbEdge = edge == 0;
                if (mbEdge && !(dir == 0 ? left : top))
                {
                    continue;
                }

                int addrP = !mbEdge ? addr : dir == 0 ? addr - 1 : addr - _widthMbs;
                bool intra = intraQ || _pic.MbIntra[addrP];
                for (int k = 0; k < 4; k++)
                {
                    int bs;
                    if (intra)
                    {
                        bs = mbEdge ? 4 : 3;
                    }
                    else
                    {
                        int qx = (mbX * 4) + (dir == 0 ? edge : k), qy = (mbY * 4) + (dir == 0 ? k : edge);
                        int q = (qy * w4) + qx;
                        int pb = dir == 0 ? q - 1 : q - w4;
                        bs = _mb.Nonzero[q] != 0 || _mb.Nonzero[pb] != 0 ? 2 : MotionDiffers(pb, q) ? 1 : 0;
                    }

                    _bs[(dir * 16) + (edge * 4) + k] = bs;
                }
            }
        }
    }

    /// <summary>Whether the two blocks use different reference pictures, a different number of vectors or vectors 4 or more apart.</summary>
    private bool MotionDiffers(int p, int q)
    {
        int p0 = _pic.RefPicId[0][p], p1 = _pic.RefPicId[1][p];
        int q0 = _pic.RefPicId[0][q], q1 = _pic.RefPicId[1][q];
        int countP = (p0 >= 0 ? 1 : 0) + (p1 >= 0 ? 1 : 0), countQ = (q0 >= 0 ? 1 : 0) + (q1 >= 0 ? 1 : 0);
        if (countP != countQ)
        {
            return true;
        }

        if (countP == 1)
        {
            int refP = p0 >= 0 ? p0 : p1, refQ = q0 >= 0 ? q0 : q1;
            if (refP != refQ)
            {
                return true;
            }

            return Far(_pic.Mv[p0 >= 0 ? 0 : 1][p], _pic.Mv[q0 >= 0 ? 0 : 1][q]);
        }

        if (!((p0 == q0 && p1 == q1) || (p0 == q1 && p1 == q0)))
        {
            return true;
        }

        int mvP0 = _pic.Mv[0][p], mvP1 = _pic.Mv[1][p], mvQ0 = _pic.Mv[0][q], mvQ1 = _pic.Mv[1][q];
        if (p0 != p1)
        {
            // Two different pictures: compare the vectors for the same picture.
            return p0 == q0 ? Far(mvP0, mvQ0) || Far(mvP1, mvQ1) : Far(mvP0, mvQ1) || Far(mvP1, mvQ0);
        }

        // Both vectors name the same picture: either pairing may match.
        return (Far(mvP0, mvQ0) || Far(mvP1, mvQ1)) && (Far(mvP0, mvQ1) || Far(mvP1, mvQ0));
    }

    private static bool Far(int a, int b) =>
        Math.Abs(H264SliceDecoder.MvX(a) - H264SliceDecoder.MvX(b)) >= 4 || Math.Abs(H264SliceDecoder.MvY(a) - H264SliceDecoder.MvY(b)) >= 4;
}
