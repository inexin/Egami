using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Inter prediction (8.4.2): fractional sample interpolation from the reference pictures (6-tap quarter-sample luma, bilinear
/// eighth-sample chroma) and weighted sample prediction, written into the current picture. Predictions go through 16-sample
/// rows of <see cref="_pred0"/> and <see cref="_pred1"/>. At 8 bits the luma filters run eight samples at a time in 16-bit
/// lanes: a 6-tap sum of samples up to 255 lies between -2550 and 10710, so it fits a signed lane; the centre position's
/// second pass widens to 32 bits. Chroma's weights add to 64, so its sums fit an unsigned lane at 8 and 10 bits. The scalar
/// forms are the reference and run above 8 bits (luma) or without vector hardware.
/// </summary>
[SkipLocalsInit]
internal sealed class H264Inter
{
    private const int PredStride = 16;
    private const int EdgeStride = 32;

    private readonly ushort[] _pred0 = new ushort[(PredStride * 16) + PredStride];
    private readonly ushort[] _pred1 = new ushort[(PredStride * 16) + PredStride];
    private readonly ushort[] _temp = new ushort[(PredStride * 16) + PredStride];
    private readonly short[] _mid = new short[(PredStride * 21) + PredStride];
    private readonly int[] _half = new int[21 * 16];
    private readonly ushort[] _edge = new ushort[(EdgeStride * 22) + EdgeStride];

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

    /// <summary>Gets or sets whether the vector forms run; tests turn it off to compare with the scalar reference.</summary>
    internal static bool UseVectors { get; set; } = Vector128.IsHardwareAccelerated;

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
            sbyte[] refIdx = _pic.RefIdx[list];
            int[] mvs = _pic.Mv[list];
            sbyte r = refIdx[first];
            int mv = mvs[first];
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int b = first + (y * w4) + x;
                    if (refIdx[b] != r || (r >= 0 && mvs[b] != mv))
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
                Weighted(plane, at, stride, w, h, w0, w1, 1 << logWD, logWD + 1, (o0 + o1 + 1) >> 1, max);
            }
            else
            {
                int r = r0 >= 0 ? r0 : r1;
                int[] wt = r0 >= 0 ? wt0 : wt1;
                int wgt = wt[(r * 6) + (component * 2)], o = wt[(r * 6) + (component * 2) + 1] << bitShift;
                if (r0 < 0)
                {
                    _pred1.AsSpan().CopyTo(_pred0);
                }

                Weighted(plane, at, stride, w, h, wgt, 0, logWD >= 1 ? 1 << (logWD - 1) : 0, logWD, o, max);
            }

            return;
        }

        if (bi && _implicitMode)
        {
            int w0 = _implicit[(r0 * 32) + r1];
            Weighted(plane, at, stride, w, h, w0, 64 - w0, 32, 6, 0, max);
            return;
        }

        ushort[] first = r0 >= 0 ? _pred0 : _pred1;
        if (bi)
        {
            Average(_pred0, _pred1, w, h);
        }

        Store(first, plane, at, stride, w, h);
    }

    /// <summary>
    /// The weighted forms (8-270 to 8-301) in one: ((pred0 × w0 + pred1 × w1 + round) &gt;&gt; shift) + offset, clipped, into the
    /// picture; one-list prediction has w1 0. The products need 32-bit lanes, four samples to a vector.
    /// </summary>
    private void Weighted(ushort[] plane, int at, int stride, int w, int h, int w0, int w1, int round, int shift, int offset, int max)
    {
        if (at < 0 || at + ((h - 1) * stride) + w > plane.Length)
        {
            throw H264Errors.Corrupt("A prediction block lies outside the picture.");
        }

        if (!UseVectors || w < 4)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int v = (((_pred0[(y * PredStride) + x] * w0) + (_pred1[(y * PredStride) + x] * w1) + round) >> shift) + offset;
                    plane[at + (y * stride) + x] = (ushort)Math.Clamp(v, 0, max);
                }
            }

            return;
        }

        ref ushort a = ref MemoryMarshal.GetArrayDataReference(_pred0);
        ref ushort b = ref MemoryMarshal.GetArrayDataReference(_pred1);
        ref ushort d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), at);
        Vector128<int> vw0 = Vector128.Create(w0), vw1 = Vector128.Create(w1), vRound = Vector128.Create(round),
            vOffset = Vector128.Create(offset), vMax = Vector128.Create(max);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x += 8)
            {
                nuint i = (nuint)((y * PredStride) + x);
                Vector128<ushort> p0 = Vector128.LoadUnsafe(ref a, i), p1 = Vector128.LoadUnsafe(ref b, i);
                Vector128<int> lo = (((Vector128.WidenLower(p0).AsInt32() * vw0) + (Vector128.WidenLower(p1).AsInt32() * vw1) + vRound) >> shift) + vOffset;
                Vector128<int> hi = (((Vector128.WidenUpper(p0).AsInt32() * vw0) + (Vector128.WidenUpper(p1).AsInt32() * vw1) + vRound) >> shift) + vOffset;
                lo = Vector128.Min(Vector128.Max(lo, Vector128<int>.Zero), vMax);
                hi = Vector128.Min(Vector128.Max(hi, Vector128<int>.Zero), vMax);
                Vector128<ushort> v = Vector128.Narrow(lo.AsUInt32(), hi.AsUInt32());
                ref ushort to = ref Unsafe.Add(ref d, (y * stride) + x);
                if (w == 4)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref to), v.AsUInt64().ToScalar());
                }
                else
                {
                    v.StoreUnsafe(ref to);
                }
            }
        }
    }

    /// <summary>Writes a w by h prediction into the picture.</summary>
    private static void Store(ushort[] pred, ushort[] plane, int at, int stride, int w, int h)
    {
        if (at < 0 || at + ((h - 1) * stride) + w > plane.Length)
        {
            throw H264Errors.Corrupt("A prediction block lies outside the picture.");
        }

        ref ushort p = ref MemoryMarshal.GetArrayDataReference(pred);
        ref ushort d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), at);
        for (int y = 0; y < h; y++)
        {
            ref ushort from = ref Unsafe.Add(ref p, y * PredStride);
            ref ushort to = ref Unsafe.Add(ref d, y * stride);
            switch (w)
            {
                case 16:
                    Unsafe.CopyBlockUnaligned(ref Unsafe.As<ushort, byte>(ref to), ref Unsafe.As<ushort, byte>(ref from), 32);
                    break;
                case 8:
                    Unsafe.CopyBlockUnaligned(ref Unsafe.As<ushort, byte>(ref to), ref Unsafe.As<ushort, byte>(ref from), 16);
                    break;
                case 4:
                    Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref to), Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref from)));
                    break;
                default:
                    for (int x = 0; x < w; x++)
                    {
                        Unsafe.Add(ref to, x) = Unsafe.Add(ref from, x);
                    }

                    break;
            }
        }
    }

    /// <summary>Rounds the average of two predictions into the first: (a + b + 1) >> 1, which fits 16 bits at up to 15 bits.</summary>
    private static void Average(ushort[] into, ushort[] other, int w, int h)
    {
        if (UseVectors)
        {
            ref ushort a = ref MemoryMarshal.GetArrayDataReference(into);
            ref ushort b = ref MemoryMarshal.GetArrayDataReference(other);
            Vector128<ushort> one = Vector128<ushort>.One;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x += 8)
                {
                    nuint i = (nuint)((y * PredStride) + x);
                    Vector128<ushort> v = (Vector128.LoadUnsafe(ref a, i) + Vector128.LoadUnsafe(ref b, i) + one) >>> 1;
                    v.StoreUnsafe(ref a, i);
                }
            }

            return;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * PredStride) + x;
                into[i] = (ushort)((into[i] + other[i] + 1) >> 1);
            }
        }
    }

    /// <summary>Copies a w by h source area at (x0, y0) of a plane into <see cref="_edge"/>, clamping coordinates to the picture.</summary>
    private void FetchEdge(ushort[] plane, int origin, int stride, int width, int height, int x0, int y0, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int row = origin + (Math.Clamp(y0 + y, 0, height - 1) * stride);
            for (int x = 0; x < w; x++)
            {
                _edge[(y * EdgeStride) + x] = plane[row + Math.Clamp(x0 + x, 0, width - 1)];
            }
        }
    }

    /// <summary>Luma sample interpolation (8.4.2.2.1) of a w by h block at (x, y) moved by mv, into pred.</summary>
    private void Luma(H264Picture refPic, int x, int y, int w, int h, int mv, ushort[] pred)
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xInt = x + (mvx >> 2), yInt = y + (mvy >> 2);
        int frac = ((mvx & 3) << 2) | (mvy & 3);
        ushort[] src = refPic.Y;
        int stride = refPic.Stride, at = refPic.Origin + (yInt * stride) + xInt;
        int pad = H264Picture.Pad;
        if (xInt - 2 < -pad || yInt - 2 < -pad || xInt + w + 3 > refPic.Width + pad || yInt + h + 3 > refPic.Height + pad)
        {
            // Past the border: a clamped copy, wide enough for the vector loads to read past the block.
            FetchEdge(src, refPic.Origin, stride, refPic.Width, refPic.Height, xInt - 2, yInt - 2, Math.Min(w + 13, EdgeStride), h + 6);
            src = _edge;
            stride = EdgeStride;
            at = (2 * EdgeStride) + 2;
        }

        if (_shiftY != 0 || !UseVectors)
        {
            LumaScalar(src, at, stride, w, h, frac, pred);
            return;
        }

        int max = _maxY;
        switch (frac)
        {
            case 0x0:
                Copy(src, at, stride, w, h, pred);
                break;
            case 0x1:
                Copy(src, at, stride, w, h, pred);
                HalfV(src, at, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0x2:
                HalfV(src, at, stride, w, h, pred, max);
                break;
            case 0x3:
                Copy(src, at + stride, stride, w, h, pred);
                HalfV(src, at, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0x4:
                Copy(src, at, stride, w, h, pred);
                HalfH(src, at, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0x5:
                HalfH(src, at, stride, w, h, pred, max);
                HalfV(src, at, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0x6:
                HalfV(src, at, stride, w, h, pred, max);
                Center(src, at, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0x7:
                HalfV(src, at, stride, w, h, pred, max);
                HalfH(src, at + stride, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0x8:
                HalfH(src, at, stride, w, h, pred, max);
                break;
            case 0x9:
                HalfH(src, at, stride, w, h, pred, max);
                Center(src, at, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0xA:
                Center(src, at, stride, w, h, pred, max);
                break;
            case 0xB:
                Center(src, at, stride, w, h, pred, max);
                HalfH(src, at + stride, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0xC:
                Copy(src, at + 1, stride, w, h, pred);
                HalfH(src, at, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0xD:
                HalfH(src, at, stride, w, h, pred, max);
                HalfV(src, at + 1, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            case 0xE:
                Center(src, at, stride, w, h, pred, max);
                HalfV(src, at + 1, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
            default:
                HalfV(src, at + 1, stride, w, h, pred, max);
                HalfH(src, at + stride, stride, w, h, _temp, max);
                Average(pred, _temp, w, h);
                break;
        }
    }

    /// <summary>Checks that the samples a w by h block's filters read, rows -2 to h + 3 and columns -2 to w + 10 around at, lie in src.</summary>
    private static void CheckReach(ushort[] src, int at, int stride, int h)
    {
        if (at - (2 * stride) - 2 < 0 || at + ((h + 3) * stride) + 24 > src.Length)
        {
            throw H264Errors.Corrupt("A prediction block reaches outside its reference.");
        }
    }

    /// <summary>The full samples G, eight at a time.</summary>
    private static void Copy(ushort[] src, int at, int stride, int w, int h, ushort[] dst)
    {
        CheckReach(src, at, stride, h);
        ref ushort s = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at);
        ref ushort d = ref MemoryMarshal.GetArrayDataReference(dst);
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i += 8)
            {
                Vector128.LoadUnsafe(ref s, (nuint)((j * stride) + i)).StoreUnsafe(ref d, (nuint)((j * PredStride) + i));
            }
        }
    }

    private static Vector128<short> Tap(Vector128<short> a, Vector128<short> b, Vector128<short> c, Vector128<short> d, Vector128<short> e, Vector128<short> f) =>
        ((c + d) * (short)20) - ((b + e) * (short)5) + (a + f);

    private static Vector128<short> Load(ref ushort at, int offset) => Vector128.LoadUnsafe(ref Unsafe.Add(ref at, offset)).AsInt16();

    /// <summary>b: the horizontal half samples, rounded and clipped (8-8, 8-11, 8-18), eight at a time in 16-bit lanes.</summary>
    private static void HalfH(ushort[] src, int at, int stride, int w, int h, ushort[] dst, int max)
    {
        CheckReach(src, at, stride, h);
        ref ushort s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at - 2);
        ref ushort d = ref MemoryMarshal.GetArrayDataReference(dst);
        Vector128<short> round = Vector128.Create((short)16), top = Vector128.Create((short)max);
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i += 8)
            {
                ref ushort s = ref Unsafe.Add(ref s0, (j * stride) + i);
                Vector128<short> t = Tap(Load(ref s, 0), Load(ref s, 1), Load(ref s, 2), Load(ref s, 3), Load(ref s, 4), Load(ref s, 5));
                Vector128<short> v = Vector128.Min(Vector128.Max((t + round) >> 5, Vector128<short>.Zero), top);
                v.AsUInt16().StoreUnsafe(ref d, (nuint)((j * PredStride) + i));
            }
        }
    }

    /// <summary>h: the vertical half samples, rounded and clipped, eight at a time in 16-bit lanes.</summary>
    private static void HalfV(ushort[] src, int at, int stride, int w, int h, ushort[] dst, int max)
    {
        CheckReach(src, at, stride, h);
        ref ushort s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at - (2 * stride));
        ref ushort d = ref MemoryMarshal.GetArrayDataReference(dst);
        Vector128<short> round = Vector128.Create((short)16), top = Vector128.Create((short)max);
        for (int i = 0; i < w; i += 8)
        {
            ref ushort s = ref Unsafe.Add(ref s0, i);
            Vector128<short> a = Load(ref s, 0), b = Load(ref s, stride), c = Load(ref s, 2 * stride), e = Load(ref s, 3 * stride),
                f = Load(ref s, 4 * stride);
            for (int j = 0; j < h; j++)
            {
                Vector128<short> g = Load(ref s, (j + 5) * stride);
                Vector128<short> t = Tap(a, b, c, e, f, g);
                Vector128<short> v = Vector128.Min(Vector128.Max((t + round) >> 5, Vector128<short>.Zero), top);
                v.AsUInt16().StoreUnsafe(ref d, (nuint)((j * PredStride) + i));
                (a, b, c, e, f) = (b, c, e, f, g);
            }
        }
    }

    /// <summary>
    /// j: the centre half samples (8-9, 8-12): the unrounded horizontal sums b1 of rows -2 to h + 2 in 16-bit lanes, then the
    /// vertical filter over them in 32-bit lanes, rounded by 512 and shifted by 10.
    /// </summary>
    private void Center(ushort[] src, int at, int stride, int w, int h, ushort[] dst, int max)
    {
        CheckReach(src, at, stride, h);
        ref ushort s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at - (2 * stride) - 2);
        ref short m = ref MemoryMarshal.GetArrayDataReference(_mid);
        for (int j = 0; j < h + 5; j++)
        {
            for (int i = 0; i < w; i += 8)
            {
                ref ushort s = ref Unsafe.Add(ref s0, (j * stride) + i);
                Tap(Load(ref s, 0), Load(ref s, 1), Load(ref s, 2), Load(ref s, 3), Load(ref s, 4), Load(ref s, 5))
                    .StoreUnsafe(ref m, (nuint)((j * PredStride) + i));
            }
        }

        ref ushort d = ref MemoryMarshal.GetArrayDataReference(dst);
        Vector128<int> round = Vector128.Create(512), top = Vector128.Create(max);
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i += 8)
            {
                ref short r = ref Unsafe.Add(ref m, (j * PredStride) + i);
                Vector128<short> a = Vector128.LoadUnsafe(ref r), b = Vector128.LoadUnsafe(ref r, PredStride),
                    c = Vector128.LoadUnsafe(ref r, 2 * PredStride), e = Vector128.LoadUnsafe(ref r, 3 * PredStride),
                    f = Vector128.LoadUnsafe(ref r, 4 * PredStride), g = Vector128.LoadUnsafe(ref r, 5 * PredStride);
                Vector128<int> lo = Tap32(Vector128.WidenLower(a), Vector128.WidenLower(b), Vector128.WidenLower(c), Vector128.WidenLower(e), Vector128.WidenLower(f), Vector128.WidenLower(g));
                Vector128<int> hi = Tap32(Vector128.WidenUpper(a), Vector128.WidenUpper(b), Vector128.WidenUpper(c), Vector128.WidenUpper(e), Vector128.WidenUpper(f), Vector128.WidenUpper(g));
                lo = Vector128.Min(Vector128.Max((lo + round) >> 10, Vector128<int>.Zero), top);
                hi = Vector128.Min(Vector128.Max((hi + round) >> 10, Vector128<int>.Zero), top);
                Vector128.Narrow(lo, hi).AsUInt16().StoreUnsafe(ref d, (nuint)((j * PredStride) + i));
            }
        }
    }

    private static Vector128<int> Tap32(Vector128<int> a, Vector128<int> b, Vector128<int> c, Vector128<int> d, Vector128<int> e, Vector128<int> f) =>
        ((c + d) * 20) - ((b + e) * 5) + (a + f);

    private static int Tap(int e, int f, int g, int h, int i, int j) => e - (5 * f) + (20 * g) + (20 * h) - (5 * i) + j;

    /// <summary>The scalar luma interpolation, the reference the vector form matches; it runs above 8 bits.</summary>
    private void LumaScalar(ushort[] src, int at, int stride, int w, int h, int frac, ushort[] pred)
    {
        int max = _maxY;
        if (frac == 0)
        {
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    pred[(j * PredStride) + i] = src[at + (j * stride) + i];
                }
            }

            return;
        }

        // b1 for rows -2 to h + 2 (horizontal half samples between columns i and i + 1), for j and the s positions.
        for (int j = 0; j < h + 5; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int s = at + ((j - 2) * stride) + i - 2;
                _half[(j * w) + i] = Tap(src[s], src[s + 1], src[s + 2], src[s + 3], src[s + 4], src[s + 5]);
            }
        }

        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int g = src[at + (j * stride) + i];
                int v = frac switch
                {
                    0x1 => (g + HalfV(src, at, stride, i, j, max) + 1) >> 1, // d
                    0x2 => HalfV(src, at, stride, i, j, max), // h
                    0x3 => (src[at + ((j + 1) * stride) + i] + HalfV(src, at, stride, i, j, max) + 1) >> 1, // n
                    0x4 => (g + HalfH(i, j, w, max) + 1) >> 1, // a
                    0x5 => (HalfH(i, j, w, max) + HalfV(src, at, stride, i, j, max) + 1) >> 1, // e
                    0x6 => (HalfV(src, at, stride, i, j, max) + Center(i, j, w, max) + 1) >> 1, // i
                    0x7 => (HalfV(src, at, stride, i, j, max) + HalfH(i, j + 1, w, max) + 1) >> 1, // p
                    0x8 => HalfH(i, j, w, max), // b
                    0x9 => (HalfH(i, j, w, max) + Center(i, j, w, max) + 1) >> 1, // f
                    0xA => Center(i, j, w, max), // j
                    0xB => (Center(i, j, w, max) + HalfH(i, j + 1, w, max) + 1) >> 1, // q
                    0xC => (src[at + (j * stride) + i + 1] + HalfH(i, j, w, max) + 1) >> 1, // c
                    0xD => (HalfH(i, j, w, max) + HalfV(src, at, stride, i + 1, j, max) + 1) >> 1, // g
                    0xE => (Center(i, j, w, max) + HalfV(src, at, stride, i + 1, j, max) + 1) >> 1, // k
                    _ => (HalfV(src, at, stride, i + 1, j, max) + HalfH(i, j + 1, w, max) + 1) >> 1, // r
                };

                pred[(j * PredStride) + i] = (ushort)v;
            }
        }
    }

    /// <summary>b: the half sample right of (i, j).</summary>
    private int HalfH(int i, int j, int w, int max) => Math.Clamp((_half[((j + 2) * w) + i] + 16) >> 5, 0, max);

    /// <summary>h: the half sample below (i, j).</summary>
    private static int HalfV(ushort[] src, int at, int stride, int i, int j, int max)
    {
        int s = at + ((j - 2) * stride) + i;
        int h1 = Tap(src[s], src[s + stride], src[s + (2 * stride)], src[s + (3 * stride)], src[s + (4 * stride)], src[s + (5 * stride)]);
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
    private void Chroma(ushort[] plane, H264Picture refPic, int x, int y, int w, int h, int mv, ushort[] pred)
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

        ushort[] src = plane;
        int stride = refPic.ChromaStride, at = refPic.ChromaOrigin + (yInt * stride) + xInt;
        int pad = H264Picture.Pad;
        if (xInt < -pad || yInt < -pad || xInt + w + 1 > refPic.ChromaWidth + pad || yInt + h + 1 > refPic.ChromaHeight + pad)
        {
            FetchEdge(plane, refPic.ChromaOrigin, stride, refPic.ChromaWidth, refPic.ChromaHeight, xInt, yInt, Math.Min(w + 9, EdgeStride), h + 1);
            src = _edge;
            stride = EdgeStride;
            at = 0;
        }

        int wa = (8 - xFrac) * (8 - yFrac), wb = xFrac * (8 - yFrac), wc = (8 - xFrac) * yFrac, wd = xFrac * yFrac;
        if (UseVectors && w >= 4)
        {
            if (at < 0 || at + (h * stride) + 24 > src.Length)
            {
                throw H264Errors.Corrupt("A prediction block reaches outside its reference.");
            }

            // The four weights add to 64, so a sum of samples up to 1023 stays below 65536 in an unsigned lane.
            ref ushort s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at);
            ref ushort d = ref MemoryMarshal.GetArrayDataReference(pred);
            Vector128<ushort> va = Vector128.Create((ushort)wa), vb = Vector128.Create((ushort)wb), vc = Vector128.Create((ushort)wc),
                vd = Vector128.Create((ushort)wd), round = Vector128.Create((ushort)32);
            for (int j = 0; j < h; j++)
            {
                ref ushort s = ref Unsafe.Add(ref s0, j * stride);
                Vector128<ushort> v = (va * Vector128.LoadUnsafe(ref s)) + (vb * Vector128.LoadUnsafe(ref s, 1))
                    + (vc * Vector128.LoadUnsafe(ref s, (nuint)stride)) + (vd * Vector128.LoadUnsafe(ref s, (nuint)stride + 1)) + round;
                (v >>> 6).StoreUnsafe(ref d, (nuint)(j * PredStride));
            }

            return;
        }

        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int s = at + (j * stride) + i;
                pred[(j * PredStride) + i] = (ushort)(((wa * src[s]) + (wb * src[s + 1]) + (wc * src[s + stride]) + (wd * src[s + stride + 1]) + 32) >> 6);
            }
        }
    }
}
