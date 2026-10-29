using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// The deblocking filter of an MBAFF frame (8.7 with MbaffFrameFlag 1), macroblock by macroblock in MBAFF address order. A
/// macroblock is filtered in its own geometry: a field macroblock in its field, every other row of the frame. Where a frame and
/// a field pair meet (mixedModeEdgeFlag), the left edge takes its strength and QP row by row from the macroblock each row's left
/// samples are in, and a frame macroblock under a field pair filters its top edge twice, once per field.
/// </summary>
internal sealed partial class H264Deblock
{
    // Per row of a mixed left edge: bS and the left macroblock's raster index.
    private readonly int[] _rowBs = new int[16];
    private readonly int[] _rowMb = new int[16];

    /// <summary>Filters every macroblock of an MBAFF frame; the motion and the macroblock state are kept in raster order.</summary>
    internal void FilterMbaff(H264Picture frame, H264MbState mb, List<H264FilterParams> slices, int bitDepthY, int bitDepthC)
    {
        _nonzero = mb.Nonzero;
        _ref0 = frame.RefPicId[0];
        _ref1 = frame.RefPicId[1];
        _mv0 = frame.Mv[0];
        _mv1 = frame.Mv[1];
        _blocks = Math.Min(Math.Min(_ref0.Length, _ref1.Length), Math.Min(Math.Min(_mv0.Length, _mv1.Length), _nonzero.Length));
        _mb = mb;
        _widthMbs = mb.WidthMbs;
        _bitDepthY = bitDepthY;
        _bitDepthC = bitDepthC;
        _chroma = frame.ChromaFormatIdc;
        int w = mb.WidthMbs;
        for (int pairY = 0; pairY < mb.HeightMbs / 2; pairY++)
        {
            for (int pairX = 0; pairX < w; pairX++)
            {
                for (int bottom = 0; bottom < 2; bottom++)
                {
                    int addr = (((2 * pairY) + bottom) * w) + pairX;
                    int slice = mb.Slice[addr];
                    if (slice < 0 || slice >= slices.Count || slices[slice].DisableIdc == 1)
                    {
                        continue;
                    }

                    if (frame.Narrow)
                    {
                        FilterMbaffMacroblock<byte>(frame, pairX, pairY, bottom, addr, slices[slice]);
                    }
                    else
                    {
                        FilterMbaffMacroblock<ushort>(frame, pairX, pairY, bottom, addr, slices[slice]);
                    }
                }
            }
        }

        _pic = null!;
    }

    private void FilterMbaffMacroblock<T>(H264Picture frame, int pairX, int pairY, int bottom, int addr, H264FilterParams p)
        where T : unmanaged
    {
        H264MbState mb = _mb;
        int w = _widthMbs, w4 = mb.W4;
        bool field = mb.FieldMb[addr];
        _fieldMbs = field;
        _far = FarLimit(field);
        H264Picture pic = field ? frame.Field(bottom) : frame;
        int row = field ? pairY : (2 * pairY) + bottom;
        int b0 = (((2 * pairY) + bottom) * 4 * w4) + (pairX * 4);
        bool intra = frame.MbIntra[addr];
        int slice = mb.Slice[addr];
        int leftPair = Neighbor(pairX - 1, pairY, slice, p);
        int abovePair = Neighbor(pairX, pairY - 1, slice, p);

        // Internal edges of the macroblock's own blocks, and bS of the edges the left and top neighbours share when they are
        // coded as this macroblock is.
        bool columnZero = ComputeMbaffStrengths(addr, b0, intra, pairX > 0);
        bool leftMixed = false;
        int leftMb = -1;
        if (leftPair >= 0)
        {
            bool leftField = mb.FieldMb[leftPair];
            if (leftField == field)
            {
                // The left macroblock in the same geometry is the one before in raster order, its blocks just left of these.
                leftMb = leftPair + (bottom * w);
                if (intra || frame.MbIntra[leftMb])
                {
                    _bs[0] = _bs[1] = _bs[2] = _bs[3] = 4;
                }
                else if (!columnZero && mb.FlatInside[addr] && mb.FlatInside[leftMb] && UseVectors)
                {
                    // Both flat: the four segments join the same two motions.
                    _bs[0] = _bs[1] = _bs[2] = _bs[3] = MotionStrength(b0 - 1, b0);
                }
                else if (!columnZero)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        _bs[k] = StrengthOf(b0 + (k * w4) - 1, b0 + (k * w4));
                    }
                }
            }
            else
            {
                // Each row's left samples are in the macroblock of the left pair that holds that frame row (6.4.12.2).
                leftMixed = true;
                for (int r = 0; r < 16; r++)
                {
                    int frameRow = field ? (2 * r) + bottom : (16 * bottom) + r;
                    int n = field ? leftPair + (frameRow >= 16 ? w : 0) : leftPair + ((frameRow & 1) * w);
                    int nRow = field ? frameRow & 15 : frameRow >> 1;
                    int pb = FirstBlock(n) + ((nRow >> 2) * w4) + 3, qb = b0 + ((r >> 2) * w4);
                    _rowMb[r] = n;
                    _rowBs[r] = intra || frame.MbIntra[n] ? 4 : (_nonzero[pb] | _nonzero[qb]) != 0 ? 2 : 1;
                }
            }
        }

        // The top edge: the macroblock above in the same geometry, or a frame macroblock under a field pair (filtered per
        // field below), or a field macroblock under a frame pair (mixed).
        int topMb = -1, topUp = 0;
        bool topDouble = false;
        if (!field && bottom == 1)
        {
            topMb = addr - w;
            topUp = 1;
        }
        else if (abovePair >= 0)
        {
            bool aboveField = mb.FieldMb[abovePair];
            topDouble = !field && aboveField;
            topMb = topDouble ? -1 : field && aboveField ? abovePair + (bottom * w) : abovePair + w;

            // How many macroblock rows of the frame up topMb is.
            topUp = field && aboveField ? 2 : bottom + 1;
        }

        bool topMixed = false;
        if (topMb >= 0)
        {
            topMixed = field != mb.FieldMb[topMb];
            int tb0 = b0 - (topUp * 4 * w4) + (3 * w4);
            if (!intra && !frame.MbIntra[topMb] && !topMixed && UseVectors)
            {
                Strengths(b0, b0 - tb0).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_bs), 16);
            }
            else
            {
                for (int k = 0; k < 4; k++)
                {
                    // A horizontal macroblock edge with an intra side has bS 4 only between frame macroblocks.
                    _bs[16 + k] = intra || frame.MbIntra[topMb] ? (field || topMixed ? 3 : 4)
                        : topMixed ? 1 + ((_nonzero[tb0 + k] | _nonzero[b0 + k]) != 0 ? 1 : 0)
                        : StrengthOf(tb0 + k, b0 + k);
                }
            }
        }

        if (!leftMixed && leftMb < 0)
        {
            _bs[0] = _bs[1] = _bs[2] = _bs[3] = 0;
        }

        if (topMb < 0)
        {
            _bs[16] = _bs[17] = _bs[18] = _bs[19] = 0;
        }

        if (!leftMixed && !topDouble && !topMixed && UseVectors && UseWideVectors)
        {
            // Every edge as in a picture of this macroblock's geometry: the progressive filter with these neighbours.
            if (!ReferenceEquals(_pic, pic))
            {
                _pic = pic;
            }

            _leftMb = leftMb;
            _topMb = topMb;
            FilterWide<T>(pairX, row, addr, leftMb >= 0, topMb >= 0, p);
            return;
        }

        // Luma: vertical edges, then horizontal ones.
        T[] luma = pic.Y<T>();
        int stride = pic.Stride;
        int origin = pic.Origin + (row * 16 * stride) + (pairX * 16);
        int qp = mb.Qp[addr];
        bool t8 = mb.Transform8x8[addr];
        if (leftMixed)
        {
            for (int r = 0; r < 16; r++)
            {
                FilterSamples(luma, origin + (r * stride), _rowBs[r], (qp + mb.Qp[_rowMb[r]] + 1) >> 1, p, false, _bitDepthY);
            }
        }
        else if (leftMb >= 0)
        {
            FilterEdge(luma, origin, stride, 0, 16, (qp + mb.Qp[leftMb] + 1) >> 1, p, 0, false, 4, _bitDepthY);
        }

        for (int edge = 1; edge < 4; edge++)
        {
            if (!t8 || (edge & 1) == 0)
            {
                FilterEdge(luma, origin + (edge * 4), stride, 0, 16, qp, p, edge, false, 4, _bitDepthY);
            }
        }

        if (topDouble)
        {
            FilterTopPerField<T>(frame, pairX, pairY, addr, b0, intra, p, false);
        }
        else if (topMb >= 0)
        {
            FilterEdge(luma, origin, stride, 1, 16, (qp + mb.Qp[topMb] + 1) >> 1, p, 0, false, 4, _bitDepthY);
        }

        for (int edge = 1; edge < 4; edge++)
        {
            if (!t8 || (edge & 1) == 0)
            {
                FilterEdge(luma, origin + (edge * 4 * stride), stride, 1, 16, qp, p, edge, false, 4, _bitDepthY);
            }
        }

        if (_chroma == 0)
        {
            return;
        }

        int ch = _chroma == 1 ? 8 : 16;
        int cStride = pic.ChromaStride;
        int cOrigin = pic.ChromaOrigin + (row * ch * cStride) + (pairX * 8);
        for (int c = 0; c < 2; c++)
        {
            T[] plane = c == 0 ? pic.Cb<T>() : pic.Cr<T>();
            sbyte[] qpc = mb.QpC[c];
            if (leftMixed)
            {
                for (int k = 0; k < ch; k++)
                {
                    // The luma row whose strength and left macroblock the chroma row takes.
                    int r = _chroma == 2 ? k : field ? 2 * k : (4 * (k >> 1)) + (k & 1);
                    FilterSamples(plane, cOrigin + (k * cStride), _rowBs[r], (qpc[addr] + qpc[_rowMb[r]] + 1) >> 1, p, true, _bitDepthC);
                }
            }
            else if (leftMb >= 0)
            {
                FilterEdge(plane, cOrigin, cStride, 0, ch, (qpc[addr] + qpc[leftMb] + 1) >> 1, p, 0, true, _chroma == 1 ? 2 : 4, _bitDepthC);
            }

            FilterEdge(plane, cOrigin + 4, cStride, 0, ch, qpc[addr], p, 2, true, _chroma == 1 ? 2 : 4, _bitDepthC);
            if (topDouble)
            {
                // The luma pass left the strengths of the second field; both fields' are worked out again per plane.
                FilterTopPerField<T>(frame, pairX, pairY, addr, b0, intra, p, true, c);
            }
            else if (topMb >= 0)
            {
                FilterEdge(plane, cOrigin, cStride, 1, 8, (qpc[addr] + qpc[topMb] + 1) >> 1, p, 0, true, 2, _bitDepthC);
            }

            for (int ce = 1; ce < ch / 4; ce++)
            {
                FilterEdge(plane, cOrigin + (ce * 4 * cStride), cStride, 1, 8, qpc[addr], p, _chroma == 1 ? ce * 2 : ce, true, 2, _bitDepthC);
            }
        }
    }

    /// <summary>
    /// Sets bS of the macroblock's internal edges; returns whether the vertical edge 0 holds bS against the blocks just left of
    /// the macroblock, as the vector form works out, and the top edge's are left for the caller.
    /// </summary>
    private bool ComputeMbaffStrengths(int addr, int b0, bool intra, bool hasLeft)
    {
        int w4 = _mb.W4;
        ref int bs = ref MemoryMarshal.GetArrayDataReference(_bs);
        if (intra)
        {
            _bs.AsSpan().Fill(3);
            return false;
        }

        if (UseVectors && _mb.FlatInside[addr])
        {
            _bs.AsSpan().Clear();
            return false;
        }

        if (!UseVectors)
        {
            _bs.AsSpan().Clear();
            for (int edge = 1; edge < 4; edge++)
            {
                for (int k = 0; k < 4; k++)
                {
                    _bs[(edge * 4) + k] = StrengthOf(b0 + (k * w4) + edge - 1, b0 + (k * w4) + edge);
                    _bs[16 + (edge * 4) + k] = StrengthOf(b0 + ((edge - 1) * w4) + k, b0 + (edge * w4) + k);
                }
            }

            return false;
        }

        // As ComputeStrengths: rows of blocks transposed into the vertical edges, then the horizontal ones.
        int back = hasLeft ? 1 : 0;
        Vector128<int> r0 = Strengths(b0, back), r1 = Strengths(b0 + w4, back);
        Vector128<int> r2 = Strengths(b0 + (2 * w4), back), r3 = Strengths(b0 + (3 * w4), back);
        Vector128<long> t0 = Lo(r0, r1).AsInt64(), t1 = Lo(r2, r3).AsInt64(), t2 = Hi(r0, r1).AsInt64(), t3 = Hi(r2, r3).AsInt64();
        Lo(t0, t1).AsInt32().StoreUnsafe(ref bs, 0);
        Hi(t0, t1).AsInt32().StoreUnsafe(ref bs, 4);
        Lo(t2, t3).AsInt32().StoreUnsafe(ref bs, 8);
        Hi(t2, t3).AsInt32().StoreUnsafe(ref bs, 12);
        Vector128<int>.Zero.StoreUnsafe(ref bs, 16);
        Strengths(b0 + w4, w4).StoreUnsafe(ref bs, 20);
        Strengths(b0 + (2 * w4), w4).StoreUnsafe(ref bs, 24);
        Strengths(b0 + (3 * w4), w4).StoreUnsafe(ref bs, 28);
        return hasLeft;
    }

    /// <summary>
    /// The top edge of a frame macroblock under a field pair (8.7, fieldModeInFrameFilteringFlag): filtered once in each field,
    /// the macroblock's rows of that field against the field macroblock above, with mixed-edge strengths.
    /// </summary>
    private void FilterTopPerField<T>(H264Picture frame, int pairX, int pairY, int addr, int b0, bool intra, H264FilterParams p, bool chroma, int plane = 0)
        where T : unmanaged
    {
        H264MbState mb = _mb;
        int w = _widthMbs, w4 = mb.W4;
        for (int f = 0; f < 2; f++)
        {
            int above = (((2 * (pairY - 1)) + f) * w) + pairX;
            int tb0 = FirstBlock(above) + (3 * w4);
            for (int k = 0; k < 4; k++)
            {
                _bs[16 + k] = intra || frame.MbIntra[above] ? 3 : 1 + ((_nonzero[tb0 + k] | _nonzero[b0 + k]) != 0 ? 1 : 0);
            }

            H264Picture view = frame.Field(f);
            if (!chroma)
            {
                int qpAv = (mb.Qp[addr] + mb.Qp[above] + 1) >> 1;
                FilterEdge(view.Y<T>(), view.Origin + (pairY * 16 * view.Stride) + (pairX * 16), view.Stride, 1, 16, qpAv, p, 0, false, 4, _bitDepthY);
            }
            else
            {
                int ch = _chroma == 1 ? 8 : 16;
                sbyte[] qpc = mb.QpC[plane];
                int at = view.ChromaOrigin + (pairY * ch * view.ChromaStride) + (pairX * 8);
                FilterEdge(plane == 0 ? view.Cb<T>() : view.Cr<T>(), at, view.ChromaStride, 1, 8, (qpc[addr] + qpc[above] + 1) >> 1, p, 0, true, 2, _bitDepthC);
            }
        }
    }

    /// <summary>Gets the index in the block arrays of the first block of the macroblock at a raster index.</summary>
    private int FirstBlock(int mb) => ((mb / _widthMbs) * 4 * _mb.W4) + ((mb % _widthMbs) * 4);

    /// <summary>The raster index of the top macroblock of a pair the edge may be filtered against, or -1.</summary>
    private int Neighbor(int pairX, int pairY, int slice, H264FilterParams p)
    {
        if (pairX < 0 || pairY < 0)
        {
            return -1;
        }

        int top = (2 * pairY * _widthMbs) + pairX;
        int s = _mb.Slice[top];
        return s < 0 || (p.DisableIdc == 2 && s != slice) ? -1 : top;
    }

    /// <summary>Filters the samples of one row across a vertical edge, q0 at q0At, with the row's own bS and QP.</summary>
    private void FilterSamples<T>(T[] plane, int q0At, int bS, int qpAv, H264FilterParams p, bool chroma, int bitDepth)
        where T : unmanaged
    {
        if (bS == 0)
        {
            return;
        }

        int saved = _bs[0];
        _bs[0] = bS;
        FilterEdgeScalar(plane, q0At, 1, 0, 1, qpAv, p, 0, chroma, 16, bitDepth);
        _bs[0] = saved;
    }
}
