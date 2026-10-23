namespace Egami.Video.Codecs.H264;

/// <summary>
/// Inter prediction (8.4.2): fractional sample interpolation from the reference pictures (6-tap quarter-sample luma, bilinear
/// eighth-sample chroma) and weighted sample prediction, written into the current picture.
/// </summary>
internal sealed class H264Inter
{
    private readonly int[] _pred0 = new int[16 * 16];
    private readonly int[] _pred1 = new int[16 * 16];
    private readonly int[] _src = new int[21 * 21];
    private readonly int[] _half = new int[21 * 21];

    private H264Picture _pic = null!;
    private H264SliceHeader _h = null!;
    private H264Picture[] _list0 = [];
    private H264Picture[] _list1 = [];
    private int[] _implicit = [];
    private int _maxY;
    private int _maxC;
    private int _chroma;
    private int _shiftY;
    private int _shiftC;
    private bool _explicit;
    private bool _implicitMode;

    internal void Setup(H264Picture pic, H264SliceHeader h, H264Picture[] list0, H264Picture[] list1, int[] implicitWeights)
    {
        _pic = pic;
        _h = h;
        _list0 = list0;
        _list1 = list1;
        _implicit = implicitWeights;
        _maxY = (1 << h.Sps.BitDepthLuma) - 1;
        _maxC = (1 << h.Sps.BitDepthChroma) - 1;
        _shiftY = h.Sps.BitDepthLuma - 8;
        _shiftC = h.Sps.BitDepthChroma - 8;
        _chroma = h.Sps.ChromaArrayType;
        _explicit = h.ExplicitWeights;
        _implicitMode = h.IsB && h.Pps.WeightedBipredIdc == 2;
    }

    /// <summary>Gets whether all 16 blocks of the macroblock at 4x4 block (x0, y0) have the same motion in both lists.</summary>
    internal bool Uniform(int x0, int y0)
    {
        int w4 = _pic.WidthMbs * 4;
        int first = (y0 * w4) + x0;
        for (int list = 0; list < 2; list++)
        {
            sbyte r = _pic.RefIdx[list][first];
            int mv = _pic.Mv[list][first];
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int b = first + (y * w4) + x;
                    if (_pic.RefIdx[list][b] != r || (r >= 0 && _pic.Mv[list][b] != mv))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Predicts the w4 by h4 4x4 blocks at 4x4 block (x4, y4) from the motion stored for its first block.</summary>
    internal void Predict(int x4, int y4, int w4, int h4)
    {
        int b = (y4 * _pic.WidthMbs * 4) + x4;
        int r0 = _pic.RefIdx[0][b], r1 = _pic.RefIdx[1][b];
        int mv0 = _pic.Mv[0][b], mv1 = _pic.Mv[1][b];
        H264Picture? p0 = r0 >= 0 ? _list0[r0] : null;
        H264Picture? p1 = r1 >= 0 ? _list1[r1] : null;
        int w = w4 * 4, h = h4 * 4, x = x4 * 4, y = y4 * 4;
        if (p0 is not null)
        {
            Luma(p0, x, y, w, h, mv0, _pred0);
        }

        if (p1 is not null)
        {
            Luma(p1, x, y, w, h, mv1, _pred1);
        }

        Combine(_pic.Y, _pic.Origin + (y * _pic.Stride) + x, _pic.Stride, w, h, r0, r1, 0, _maxY, _shiftY);
        if (_chroma == 0)
        {
            return;
        }

        int cw = w / 2, ch = _chroma == 1 ? h / 2 : h, cx = x / 2, cy = _chroma == 1 ? y / 2 : y;
        for (int c = 0; c < 2; c++)
        {
            if (p0 is not null)
            {
                Chroma(c == 0 ? p0.Cb : p0.Cr, p0, cx, cy, cw, ch, mv0, _pred0);
            }

            if (p1 is not null)
            {
                Chroma(c == 0 ? p1.Cb : p1.Cr, p1, cx, cy, cw, ch, mv1, _pred1);
            }

            ushort[] plane = c == 0 ? _pic.Cb : _pic.Cr;
            Combine(plane, _pic.ChromaOrigin + (cy * _pic.ChromaStride) + cx, _pic.ChromaStride, cw, ch, r0, r1, 1 + c, _maxC, _shiftC);
        }
    }

    /// <summary>Weighted sample prediction (8.4.2.3): default, explicit or implicit, into the picture.</summary>
    private void Combine(ushort[] plane, int at, int stride, int w, int h, int r0, int r1, int component, int max, int bitShift)
    {
        bool bi = r0 >= 0 && r1 >= 0;
        if (_explicit)
        {
            int logWD = component == 0 ? _h.LumaLog2WeightDenom : _h.ChromaLog2WeightDenom;
            int[] wt0 = _h.Weights[0], wt1 = _h.Weights[1];
            if (bi)
            {
                int w0 = wt0[(r0 * 6) + (component * 2)], o0 = wt0[(r0 * 6) + (component * 2) + 1] << bitShift;
                int w1 = wt1[(r1 * 6) + (component * 2)], o1 = wt1[(r1 * 6) + (component * 2) + 1] << bitShift;
                int round = 1 << logWD, offset = (o0 + o1 + 1) >> 1;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int v = ((((_pred0[(y * w) + x] * w0) + (_pred1[(y * w) + x] * w1) + round) >> (logWD + 1)) + offset);
                        plane[at + (y * stride) + x] = (ushort)Math.Clamp(v, 0, max);
                    }
                }
            }
            else
            {
                int list = r0 >= 0 ? 0 : 1;
                int r = r0 >= 0 ? r0 : r1;
                int[] wt = list == 0 ? wt0 : wt1;
                int[] pred = list == 0 ? _pred0 : _pred1;
                int wgt = wt[(r * 6) + (component * 2)], o = wt[(r * 6) + (component * 2) + 1] << bitShift;
                int round = logWD >= 1 ? 1 << (logWD - 1) : 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int v = logWD >= 1 ? ((pred[(y * w) + x] * wgt) + round >> logWD) + o : (pred[(y * w) + x] * wgt) + o;
                        plane[at + (y * stride) + x] = (ushort)Math.Clamp(v, 0, max);
                    }
                }
            }

            return;
        }

        if (bi)
        {
            if (_implicitMode)
            {
                int w0 = _implicit[(r0 * 32) + r1], w1 = 64 - w0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int v = ((_pred0[(y * w) + x] * w0) + (_pred1[(y * w) + x] * w1) + 32) >> 6;
                        plane[at + (y * stride) + x] = (ushort)Math.Clamp(v, 0, max);
                    }
                }

                return;
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    plane[at + (y * stride) + x] = (ushort)((_pred0[(y * w) + x] + _pred1[(y * w) + x] + 1) >> 1);
                }
            }

            return;
        }

        int[] only = r0 >= 0 ? _pred0 : _pred1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                plane[at + (y * stride) + x] = (ushort)only[(y * w) + x];
            }
        }
    }

    /// <summary>Copies the source area (x0, y0, w, h) of a plane into <see cref="_src"/>, clamping coordinates to the picture.</summary>
    private void Fetch(ushort[] plane, int origin, int stride, int width, int height, int x0, int y0, int w, int h)
    {
        int pad = H264Picture.Pad;
        if (x0 >= -pad && y0 >= -pad && x0 + w <= width + pad && y0 + h <= height + pad)
        {
            for (int y = 0; y < h; y++)
            {
                int row = origin + ((y0 + y) * stride) + x0;
                for (int x = 0; x < w; x++)
                {
                    _src[(y * w) + x] = plane[row + x];
                }
            }

            return;
        }

        for (int y = 0; y < h; y++)
        {
            int row = origin + (Math.Clamp(y0 + y, 0, height - 1) * stride);
            for (int x = 0; x < w; x++)
            {
                _src[(y * w) + x] = plane[row + Math.Clamp(x0 + x, 0, width - 1)];
            }
        }
    }

    private static int Tap(int e, int f, int g, int h, int i, int j) => e - (5 * f) + (20 * g) + (20 * h) - (5 * i) + j;

    /// <summary>Luma sample interpolation (8.4.2.2.1) of a w by h block at (x, y) moved by mv, into pred.</summary>
    private void Luma(H264Picture refPic, int x, int y, int w, int h, int mv, int[] pred)
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xInt = x + (mvx >> 2), yInt = y + (mvy >> 2);
        int xFrac = mvx & 3, yFrac = mvy & 3;
        int sw = w + 5, sh = h + 5;
        Fetch(refPic.Y, refPic.Origin, refPic.Stride, refPic.Width, refPic.Height, xInt - 2, yInt - 2, sw, sh);
        int max = _maxY;
        if (xFrac == 0 && yFrac == 0)
        {
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    pred[(j * w) + i] = _src[((j + 2) * sw) + i + 2];
                }
            }

            return;
        }

        // b1 for every source row (horizontal half samples between columns i and i + 1), for j and the s positions.
        for (int j = 0; j < sh; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int s = (j * sw) + i;
                _half[(j * w) + i] = Tap(_src[s], _src[s + 1], _src[s + 2], _src[s + 3], _src[s + 4], _src[s + 5]);
            }
        }

        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int g = _src[((j + 2) * sw) + i + 2];
                int v;
                switch ((xFrac << 2) | yFrac)
                {
                    case 0x1:
                        v = (g + HalfV(i, j, sw, max) + 1) >> 1; // d
                        break;
                    case 0x2:
                        v = HalfV(i, j, sw, max); // h
                        break;
                    case 0x3:
                        v = (_src[((j + 3) * sw) + i + 2] + HalfV(i, j, sw, max) + 1) >> 1; // n
                        break;
                    case 0x4:
                        v = (g + HalfH(i, j, w, max) + 1) >> 1; // a
                        break;
                    case 0x5:
                        v = (HalfH(i, j, w, max) + HalfV(i, j, sw, max) + 1) >> 1; // e
                        break;
                    case 0x6:
                        v = (HalfV(i, j, sw, max) + Center(i, j, w, max) + 1) >> 1; // i
                        break;
                    case 0x7:
                        v = (HalfV(i, j, sw, max) + HalfH(i, j + 1, w, max) + 1) >> 1; // p
                        break;
                    case 0x8:
                        v = HalfH(i, j, w, max); // b
                        break;
                    case 0x9:
                        v = (HalfH(i, j, w, max) + Center(i, j, w, max) + 1) >> 1; // f
                        break;
                    case 0xA:
                        v = Center(i, j, w, max); // j
                        break;
                    case 0xB:
                        v = (Center(i, j, w, max) + HalfH(i, j + 1, w, max) + 1) >> 1; // q
                        break;
                    case 0xC:
                        v = (_src[((j + 2) * sw) + i + 3] + HalfH(i, j, w, max) + 1) >> 1; // c
                        break;
                    case 0xD:
                        v = (HalfH(i, j, w, max) + HalfV(i + 1, j, sw, max) + 1) >> 1; // g
                        break;
                    case 0xE:
                        v = (Center(i, j, w, max) + HalfV(i + 1, j, sw, max) + 1) >> 1; // k
                        break;
                    default:
                        v = (HalfV(i + 1, j, sw, max) + HalfH(i, j + 1, w, max) + 1) >> 1; // r
                        break;
                }

                pred[(j * w) + i] = v;
            }
        }
    }

    /// <summary>b: the half sample right of (i, j).</summary>
    private int HalfH(int i, int j, int w, int max) => Math.Clamp((_half[((j + 2) * w) + i] + 16) >> 5, 0, max);

    /// <summary>h: the half sample below (i, j).</summary>
    private int HalfV(int i, int j, int sw, int max)
    {
        int s = (j * sw) + i + 2;
        int h1 = Tap(_src[s], _src[s + sw], _src[s + (2 * sw)], _src[s + (3 * sw)], _src[s + (4 * sw)], _src[s + (5 * sw)]);
        return Math.Clamp((h1 + 16) >> 5, 0, max);
    }

    /// <summary>j: the centre half sample, from the unrounded horizontal half samples above and below.</summary>
    private int Center(int i, int j, int w, int max)
    {
        int s = (j * w) + i;
        int j1 = Tap(_half[s], _half[s + w], _half[s + (2 * w)], _half[s + (3 * w)], _half[s + (4 * w)], _half[s + (5 * w)]);
        return Math.Clamp((j1 + 512) >> 10, 0, max);
    }

    /// <summary>Chroma sample interpolation (8.4.2.2.2) of a w by h chroma block at (x, y).</summary>
    private void Chroma(ushort[] plane, H264Picture refPic, int x, int y, int w, int h, int mv, int[] pred)
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xInt = x + (mvx >> 3), xFrac = mvx & 7;
        int yInt, yFrac;
        if (_chroma == 1)
        {
            yInt = y + (mvy >> 3);
            yFrac = mvy & 7;
        }
        else
        {
            yInt = y + (mvy >> 2);
            yFrac = (mvy & 3) << 1;
        }

        int sw = w + 1;
        Fetch(plane, refPic.ChromaOrigin, refPic.ChromaStride, refPic.ChromaWidth, refPic.ChromaHeight, xInt, yInt, sw, h + 1);
        int wa = (8 - xFrac) * (8 - yFrac), wb = xFrac * (8 - yFrac), wc = (8 - xFrac) * yFrac, wd = xFrac * yFrac;
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int s = (j * sw) + i;
                pred[(j * w) + i] = ((wa * _src[s]) + (wb * _src[s + 1]) + (wc * _src[s + sw]) + (wd * _src[s + sw + 1]) + 32) >> 6;
            }
        }
    }
}
