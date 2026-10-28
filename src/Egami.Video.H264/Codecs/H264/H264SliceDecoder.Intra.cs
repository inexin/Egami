using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.H264;

/// <summary>Intra prediction (8.3) and the reconstruction of intra macroblocks.</summary>
internal sealed partial class H264SliceDecoder
{
    // Neighbouring samples of the block being predicted: top (p[x, -1], up to 16), left (p[-1, y], up to 16), top-left.
    private readonly int[] _top = new int[32];
    private readonly int[] _left = new int[16];
    private int _topLeft;

    /// <summary>
    /// Whether the left neighbour's rows first to first + count - 1 may be used for intra prediction: in an MBAFF frame the
    /// rows can be in the two macroblocks of the left pair, each of which may be inter with constrained intra prediction.
    /// </summary>
    private bool LeftIntra(int first, int count)
    {
        if (!_mbaff)
        {
            return IntraAvailable(LeftMb);
        }

        if (!_leftIntraKnown)
        {
            FindLeftIntra();
        }

        int mask = ((1 << count) - 1) << first;
        return (_leftIntraLuma & mask) == mask;
    }

    private bool IntraAvailable(int addr) =>
        Available(addr) && (!_pps.ConstrainedIntraPred || H264MbState.IsIntra(_mb.Kind[addr]));

    private void ReconstructIntra()
    {
        if (_kind == H264MbKind.IPcm)
        {
            return;
        }

        if (_pic.Narrow)
        {
            ReconstructIntra<byte>();
        }
        else
        {
            ReconstructIntra<ushort>();
        }
    }

    private void ReconstructIntra<TSample>()
        where TSample : unmanaged
    {
        TSample[] plane = _pic.Y<TSample>();
        int stride = _pic.Stride;
        int origin = _pic.Origin + (_mbY * 16 * stride) + (_mbX * 16);
        int max = (1 << _bitDepthY) - 1;
        if (_kind == H264MbKind.I16x16)
        {
            PredictIntra16x16(plane, origin, stride);
            for (int blk = 0; blk < 16; blk++)
            {
                if ((_lumaCoded & (1 << blk)) != 0)
                {
                    int xy = H264Tables.BlockXY[blk];
                    H264Transform.Add4x4(_coef.AsSpan(blk * 16, 16), plane, origin + ((xy >> 2) * 4 * stride) + ((xy & 3) * 4), stride, max);
                }
            }
        }
        else if (_kind == H264MbKind.I8x8)
        {
            for (int i8 = 0; i8 < 4; i8++)
            {
                int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2;
                int at = origin + (by * 4 * stride) + (bx * 4);
                PredictIntraNxN(plane, at, stride, bx, by, 8, _mb.IntraModes[Block(bx, by)]);
                if ((_lumaCoded & (15 << (i8 * 4))) != 0)
                {
                    H264Transform.Add8x8(_coef.AsSpan(i8 * 64, 64), plane, at, stride, max);
                }
            }
        }
        else
        {
            for (int blk = 0; blk < 16; blk++)
            {
                int xy = H264Tables.BlockXY[blk];
                int bx = xy & 3, by = xy >> 2;
                int at = origin + (by * 4 * stride) + (bx * 4);
                PredictIntraNxN(plane, at, stride, bx, by, 4, _mb.IntraModes[Block(bx, by)]);
                if ((_lumaCoded & (1 << blk)) != 0)
                {
                    H264Transform.Add4x4(_coef.AsSpan(blk * 16, 16), plane, at, stride, max);
                }
            }
        }

        if (_chromaArrayType is 1 or 2)
        {
            PredictIntraChroma(_pic.Cb<TSample>());
            PredictIntraChroma(_pic.Cr<TSample>());
            AddChromaResidual<TSample>();
        }
    }

    private void ReconstructInterResidual()
    {
        if (_pic.Narrow)
        {
            ReconstructInterResidual<byte>();
        }
        else
        {
            ReconstructInterResidual<ushort>();
        }
    }

    private void ReconstructInterResidual<TSample>()
        where TSample : unmanaged
    {
        TSample[] plane = _pic.Y<TSample>();
        int stride = _pic.Stride;
        int origin = _pic.Origin + (_mbY * 16 * stride) + (_mbX * 16);
        int max = (1 << _bitDepthY) - 1;
        if (_lumaCoded != 0)
        {
            if (_t8x8)
            {
                for (int i8 = 0; i8 < 4; i8++)
                {
                    if ((_lumaCoded & (15 << (i8 * 4))) != 0)
                    {
                        H264Transform.Add8x8(_coef.AsSpan(i8 * 64, 64), plane, origin + ((i8 >> 1) * 8 * stride) + ((i8 & 1) * 8), stride, max);
                    }
                }
            }
            else
            {
                for (int blk = 0; blk < 16; blk++)
                {
                    if ((_lumaCoded & (1 << blk)) != 0)
                    {
                        int xy = H264Tables.BlockXY[blk];
                        H264Transform.Add4x4(_coef.AsSpan(blk * 16, 16), plane, origin + ((xy >> 2) * 4 * stride) + ((xy & 3) * 4), stride, max);
                    }
                }
            }
        }

        if (_chromaArrayType is 1 or 2)
        {
            AddChromaResidual<TSample>();
        }
    }

    private void AddChromaResidual<TSample>()
        where TSample : unmanaged
    {
        if (_chromaAcCoded == 0)
        {
            return;
        }

        int stride = _pic.ChromaStride;
        int w = _sps.MbWidthC, h = _sps.MbHeightC;
        int origin = _pic.ChromaOrigin + (_mbY * h * stride) + (_mbX * w);
        int max = (1 << _bitDepthC) - 1;
        int blocks = _chromaArrayType == 1 ? 4 : 8;
        for (int c = 0; c < 2; c++)
        {
            TSample[] plane = c == 0 ? _pic.Cb<TSample>() : _pic.Cr<TSample>();
            for (int blk = 0; blk < blocks; blk++)
            {
                if ((_chromaAcCoded & (1 << ((c * 8) + blk))) != 0)
                {
                    H264Transform.Add4x4(_coefC.AsSpan(((c * 8) + blk) * 16, 16), plane, origin + ((blk >> 1) * 4 * stride) + ((blk & 1) * 4), stride, max);
                }
            }
        }
    }

    /// <summary>Intra4x4 (8.3.1.2) or Intra8x8 (8.3.2.2) prediction of the n by n block at 4x4 block (bx, by).</summary>
    private void PredictIntraNxN<TSample>(TSample[] plane, int at, int stride, int bx, int by, int n, int mode)
        where TSample : unmanaged
    {
        int bx4 = bx, by4 = by;
        bool left = bx4 > 0 || LeftIntra(by4 * 4, n);
        bool top = by4 > 0 || IntraAvailable(TopMb);
        bool topLeft = bx4 > 0 && by4 > 0 ? true
            : bx4 > 0 ? IntraAvailable(TopMb)
            : by4 > 0 ? LeftIntra((by4 * 4) - 1, 1)
            : IntraAvailable(_mbX > 0 && _mbY > 0 ? _mbAddr - _widthMbs - 1 : -1);
        int step = n / 4;
        bool topRight;
        if (by4 == 0)
        {
            topRight = bx4 + step < 4 ? IntraAvailable(TopMb) : IntraAvailable(_mbX < _widthMbs - 1 && _mbY > 0 ? _mbAddr - _widthMbs + 1 : -1);
        }
        else
        {
            topRight = bx4 + step < 4 && BlockIndex(bx4 + step, by4 - 1) < BlockIndex(bx4, by4);
        }

        int dc = 1 << (_bitDepthY - 1);
        if (top)
        {
            for (int x = 0; x < n; x++)
            {
                _top[x] = H264Samples.Get(plane, at - stride + x);
            }

            for (int x = n; x < 2 * n; x++)
            {
                _top[x] = topRight ? H264Samples.Get(plane, at - stride + x) : _top[n - 1];
            }
        }

        if (left)
        {
            for (int y = 0; y < n; y++)
            {
                _left[y] = H264Samples.Get(plane, at + (y * stride) - 1);
            }
        }

        _topLeft = topLeft ? H264Samples.Get(plane, at - stride - 1) : 0;
        if (n == 8)
        {
            FilterReferences8x8(left, top, topLeft);
        }

        Predict(plane, at, stride, n, mode, left, top, dc);
    }

    /// <summary>The Intra8x8 reference sample filter (8.3.2.2.1).</summary>
    private void FilterReferences8x8(bool left, bool top, bool topLeft)
    {
        Span<int> t = stackalloc int[16];
        Span<int> l = stackalloc int[8];
        int tl = _topLeft;
        if (top)
        {
            t[0] = topLeft ? (_topLeft + (2 * _top[0]) + _top[1] + 2) >> 2 : ((3 * _top[0]) + _top[1] + 2) >> 2;
            for (int x = 1; x < 15; x++)
            {
                t[x] = (_top[x - 1] + (2 * _top[x]) + _top[x + 1] + 2) >> 2;
            }

            t[15] = (_top[14] + (3 * _top[15]) + 2) >> 2;
        }

        if (topLeft)
        {
            tl = top && left ? (_top[0] + (2 * _topLeft) + _left[0] + 2) >> 2
                : top ? ((3 * _topLeft) + _top[0] + 2) >> 2
                : left ? ((3 * _topLeft) + _left[0] + 2) >> 2
                : _topLeft;
        }

        if (left)
        {
            l[0] = topLeft ? (_topLeft + (2 * _left[0]) + _left[1] + 2) >> 2 : ((3 * _left[0]) + _left[1] + 2) >> 2;
            for (int y = 1; y < 7; y++)
            {
                l[y] = (_left[y - 1] + (2 * _left[y]) + _left[y + 1] + 2) >> 2;
            }

            l[7] = (_left[6] + (3 * _left[7]) + 2) >> 2;
        }

        t.CopyTo(_top);
        l.CopyTo(_left);
        _topLeft = tl;
    }

    /// <summary>p[x, -1] for x from -1, p[-1, y] for y from -1 (the top-left sample at -1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int T(int x) => x < 0 ? _topLeft : _top[x];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int L(int y) => y < 0 ? _topLeft : _left[y];

    /// <summary>The nine Intra4x4 and Intra8x8 modes, which share their formulas for block size n (8.3.1.2.x, 8.3.2.2.x).</summary>
    private void Predict<TSample>(TSample[] plane, int at, int stride, int n, int mode, bool left, bool top, int dc)
        where TSample : unmanaged
    {
        for (int y = 0; y < n; y++)
        {
            int row = at + (y * stride);
            for (int x = 0; x < n; x++)
            {
                int v;
                switch (mode)
                {
                    case 0:
                        v = T(x);
                        break;
                    case 1:
                        v = L(y);
                        break;
                    case 2:
                        v = PredictDc(n, left, top, dc);
                        plane.AsSpan(row, n).Fill(H264Samples.Of<TSample>(v));
                        goto NextRow;
                    case 3:
                        v = x == n - 1 && y == n - 1 ? (T((2 * n) - 2) + (3 * T((2 * n) - 1)) + 2) >> 2
                            : (T(x + y) + (2 * T(x + y + 1)) + T(x + y + 2) + 2) >> 2;
                        break;
                    case 4:
                        v = x > y ? (T(x - y - 2) + (2 * T(x - y - 1)) + T(x - y) + 2) >> 2
                            : x < y ? (L(y - x - 2) + (2 * L(y - x - 1)) + L(y - x) + 2) >> 2
                            : (T(0) + (2 * _topLeft) + L(0) + 2) >> 2;
                        break;
                    case 5:
                        {
                            int z = (2 * x) - y;
                            v = z >= 0 && (z & 1) == 0 ? (T(x - (y >> 1) - 1) + T(x - (y >> 1)) + 1) >> 1
                                : z > 0 ? (T(x - (y >> 1) - 2) + (2 * T(x - (y >> 1) - 1)) + T(x - (y >> 1)) + 2) >> 2
                                : z == -1 ? (L(0) + (2 * _topLeft) + T(0) + 2) >> 2
                                : (L(y - (2 * x) - 1) + (2 * L(y - (2 * x) - 2)) + L(y - (2 * x) - 3) + 2) >> 2;
                            break;
                        }

                    case 6:
                        {
                            int z = (2 * y) - x;
                            v = z >= 0 && (z & 1) == 0 ? (L(y - (x >> 1) - 1) + L(y - (x >> 1)) + 1) >> 1
                                : z > 0 ? (L(y - (x >> 1) - 2) + (2 * L(y - (x >> 1) - 1)) + L(y - (x >> 1)) + 2) >> 2
                                : z == -1 ? (L(0) + (2 * _topLeft) + T(0) + 2) >> 2
                                : (T(x - (2 * y) - 1) + (2 * T(x - (2 * y) - 2)) + T(x - (2 * y) - 3) + 2) >> 2;
                            break;
                        }

                    case 7:
                        v = (y & 1) == 0 ? (T(x + (y >> 1)) + T(x + (y >> 1) + 1) + 1) >> 1
                            : (T(x + (y >> 1)) + (2 * T(x + (y >> 1) + 1)) + T(x + (y >> 1) + 2) + 2) >> 2;
                        break;
                    default:
                        {
                            int z = x + (2 * y);
                            int last = (2 * n) - 3;
                            v = z > last ? L(n - 1)
                                : z == last ? (L(n - 2) + (3 * L(n - 1)) + 2) >> 2
                                : (z & 1) == 0 ? (L(y + (x >> 1)) + L(y + (x >> 1) + 1) + 1) >> 1
                                : (L(y + (x >> 1)) + (2 * L(y + (x >> 1) + 1)) + L(y + (x >> 1) + 2) + 2) >> 2;
                            break;
                        }
                }

                H264Samples.Set(plane, row + x, v);
            }

        NextRow:;
        }
    }

    private int PredictDc(int n, bool left, bool top, int dc)
    {
        int sum = 0;
        if (top)
        {
            for (int i = 0; i < n; i++)
            {
                sum += _top[i];
            }
        }

        if (left)
        {
            for (int i = 0; i < n; i++)
            {
                sum += _left[i];
            }
        }

        int shift = n == 4 ? 2 : n == 8 ? 3 : 4;
        return left && top ? (sum + n) >> (shift + 1) : left || top ? (sum + (n >> 1)) >> shift : dc;
    }

    private void PredictIntra16x16<TSample>(TSample[] plane, int at, int stride)
        where TSample : unmanaged
    {
        bool left = LeftIntra(0, 16), top = IntraAvailable(TopMb);
        bool topLeft = IntraAvailable(_mbX > 0 && _mbY > 0 ? _mbAddr - _widthMbs - 1 : -1);
        for (int i = 0; i < 16; i++)
        {
            _top[i] = top ? H264Samples.Get(plane, at - stride + i) : 0;
            _left[i] = left ? H264Samples.Get(plane, at + (i * stride) - 1) : 0;
        }

        _topLeft = topLeft ? H264Samples.Get(plane, at - stride - 1) : 0;
        int max = (1 << _bitDepthY) - 1;
        switch (_i16Mode)
        {
            case 0:
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        H264Samples.Set(plane, at + (y * stride) + x, _top[x]);
                    }
                }

                break;
            case 1:
                for (int y = 0; y < 16; y++)
                {
                    plane.AsSpan(at + (y * stride), 16).Fill(H264Samples.Of<TSample>(_left[y]));
                }

                break;
            case 2:
                {
                    int v = PredictDc(16, left, top, 1 << (_bitDepthY - 1));
                    for (int y = 0; y < 16; y++)
                    {
                        plane.AsSpan(at + (y * stride), 16).Fill(H264Samples.Of<TSample>(v));
                    }

                    break;
                }

            default:
                {
                    int hSum = 0, vSum = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        hSum += (i + 1) * (_top[8 + i] - T(6 - i));
                        vSum += (i + 1) * (_left[8 + i] - L(6 - i));
                    }

                    int a = 16 * (_left[15] + _top[15]);
                    int b = ((5 * hSum) + 32) >> 6, c = ((5 * vSum) + 32) >> 6;
                    for (int y = 0; y < 16; y++)
                    {
                        for (int x = 0; x < 16; x++)
                        {
                            H264Samples.Set(plane, at + (y * stride) + x, Math.Clamp((a + (b * (x - 7)) + (c * (y - 7)) + 16) >> 5, 0, max));
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>Chroma intra prediction (8.3.4) of one plane of the macroblock.</summary>
    private void PredictIntraChroma<TSample>(TSample[] plane)
        where TSample : unmanaged
    {
        int stride = _pic.ChromaStride;
        int w = _sps.MbWidthC, h = _sps.MbHeightC;
        int at = _pic.ChromaOrigin + (_mbY * h * stride) + (_mbX * w);
        if (_mbaff && !_leftIntraKnown)
        {
            FindLeftIntra();
        }

        bool left = _mbaff ? (_leftIntraChroma & ((1 << h) - 1)) == (1 << h) - 1 : IntraAvailable(LeftMb), top = IntraAvailable(TopMb);
        bool topLeft = IntraAvailable(_mbX > 0 && _mbY > 0 ? _mbAddr - _widthMbs - 1 : -1);
        for (int i = 0; i < w; i++)
        {
            _top[i] = top ? H264Samples.Get(plane, at - stride + i) : 0;
        }

        for (int i = 0; i < h; i++)
        {
            _left[i] = left || (_mbaff && (_leftIntraChroma & (1 << i)) != 0) ? H264Samples.Get(plane, at + (i * stride) - 1) : 0;
        }

        _topLeft = topLeft ? H264Samples.Get(plane, at - stride - 1) : 0;
        int max = (1 << _bitDepthC) - 1;
        int dcDefault = 1 << (_bitDepthC - 1);
        switch (_mb.ChromaPredMode[_mbAddr])
        {
            case 0:
                for (int yo = 0; yo < h; yo += 4)
                {
                    for (int xo = 0; xo < w; xo += 4)
                    {
                        int sumT = 0, sumL = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            sumT += _top[xo + i];
                            sumL += _left[yo + i];
                        }

                        // DC takes the left samples of its own four rows, which an MBAFF frame may have when others are not.
                        bool rowsLeft = _mbaff ? ((_leftIntraChroma >> yo) & 15) == 15 : left;

                        int v;
                        if ((xo == 0 && yo == 0) || (xo > 0 && yo > 0))
                        {
                            v = top && rowsLeft ? (sumT + sumL + 4) >> 3 : rowsLeft ? (sumL + 2) >> 2 : top ? (sumT + 2) >> 2 : dcDefault;
                        }
                        else if (xo > 0)
                        {
                            v = top ? (sumT + 2) >> 2 : rowsLeft ? (sumL + 2) >> 2 : dcDefault;
                        }
                        else
                        {
                            v = rowsLeft ? (sumL + 2) >> 2 : top ? (sumT + 2) >> 2 : dcDefault;
                        }

                        for (int y = 0; y < 4; y++)
                        {
                            plane.AsSpan(at + ((yo + y) * stride) + xo, 4).Fill(H264Samples.Of<TSample>(v));
                        }
                    }
                }

                break;
            case 1:
                for (int y = 0; y < h; y++)
                {
                    plane.AsSpan(at + (y * stride), w).Fill(H264Samples.Of<TSample>(_left[y]));
                }

                break;
            case 2:
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        H264Samples.Set(plane, at + (y * stride) + x, _top[x]);
                    }
                }

                break;
            default:
                {
                    int xCF = 0, yCF = _chromaArrayType != 1 ? 4 : 0;
                    int hSum = 0, vSum = 0;
                    for (int x = 0; x <= 3 + xCF; x++)
                    {
                        hSum += (x + 1) * (_top[4 + xCF + x] - T(2 + xCF - x));
                    }

                    for (int y = 0; y <= 3 + yCF; y++)
                    {
                        vSum += (y + 1) * (_left[4 + yCF + y] - L(2 + yCF - y));
                    }

                    int a = 16 * (_left[h - 1] + _top[w - 1]);
                    int b = ((34 * hSum) + 32) >> 6;
                    int c = (((_chromaArrayType != 1 ? 5 : 34) * vSum) + 32) >> 6;
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            H264Samples.Set(plane, at + (y * stride) + x, Math.Clamp((a + (b * (x - 3 - xCF)) + (c * (y - 3 - yCF)) + 16) >> 5, 0, max));
                        }
                    }

                    break;
                }
        }
    }
}
