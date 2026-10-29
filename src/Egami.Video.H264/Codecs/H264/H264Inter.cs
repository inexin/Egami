using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

    // Where a 4:2:0 Cr prediction starts in the prediction buffers: after Cb's eight rows.
    private const int CrRows = PredStride * 8;

    // How a byte prediction meets the destination: written as it is, averaged with the first list's or weighted explicitly.
    private const int Single = 0;
    private const int Averaged = 1;
    private const int Scaled = 2;

    private readonly ushort[] _pred0 = new ushort[(PredStride * 16) + PredStride];
    private readonly ushort[] _pred1 = new ushort[(PredStride * 16) + PredStride];
    private readonly ushort[] _temp = new ushort[(PredStride * 16) + PredStride];
    private readonly short[] _mid = new short[(PredStride * 21) + PredStride];
    private readonly int[] _half = new int[21 * 16];
    private readonly ushort[] _edge = new ushort[(EdgeStride * 22) + EdgeStride];
    private readonly byte[] _edge8 = new byte[(EdgeStride * 22) + EdgeStride];
    private readonly byte[] _bytesA = new byte[(PredStride * 16) + PredStride];
    private readonly byte[] _bytesB = new byte[(PredStride * 16) + PredStride];
    private readonly byte[] _bytes0 = new byte[(PredStride * 16) + PredStride];

    private H264Picture _pic = null!;

    // The picture's motion arrays per list, and its row length in 4x4 blocks.
    private sbyte[] _refIdx0 = [];
    private sbyte[] _refIdx1 = [];
    private int[] _mv0 = [];
    private int[] _mv1 = [];
    private int _w4;
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

    // The parity of the field being predicted (0 top, 1 bottom), or -1 for a frame macroblock.
    private int _parity = -1;

    // Whether 4:2:0 chroma of a field is predicted, whose vector moves with the reference field's parity.
    private bool _fieldChroma;

    // How far reference indices shift to index the explicit weights: 1 for an MBAFF field macroblock.
    private int _weightShift;

    /// <summary>Gets the edge buffer of the sample type.</summary>
    private T[] Edge<T>()
        where T : unmanaged => H264Samples.Narrow<T>() ? Unsafe.As<T[]>(_edge8) : Unsafe.As<T[]>(_edge);

    /// <summary>Gets or sets whether the vector forms run; tests turn it off to compare with the scalar reference.</summary>
    internal static bool UseVectors { get; set; } = Vector128.IsHardwareAccelerated;

    /// <summary>Gets or sets whether the 256-bit forms run; tests turn them off to check the 128-bit ones.</summary>
    internal static bool UseWideVectors { get; set; } = Avx2.IsSupported;

    internal void Setup(H264Picture pic, H264SliceHeader h, H264Picture[] list0, H264Picture[] list1, int[] implicitWeights)
    {
        _pic = pic;
        _w4 = pic.WidthMbs * 4;
        _refIdx0 = pic.RefIdx[0];
        _refIdx1 = pic.RefIdx[1];
        _mv0 = pic.Mv[0];
        _mv1 = pic.Mv[1];
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
        _parity = pic.Parity;
        _fieldChroma = _parity >= 0 && _chroma == 1;
        _weightShift = 0;
    }

    /// <summary>
    /// Points an MBAFF macroblock's prediction at its picture (the frame, or the field of a field macroblock), the reference
    /// lists and implicit weights for it, and the window its motion is in, w4 blocks a row. Explicit weights of a field
    /// macroblock are its frame reference's (8.4.2.3).
    /// </summary>
    internal void SetMacroblock(H264Picture pic, H264Picture[] list0, H264Picture[] list1, int[] implicitWeights, H264Picture motion, int w4)
    {
        if (ReferenceEquals(_pic, pic) && ReferenceEquals(_list0, list0) && ReferenceEquals(_refIdx0, motion.RefIdx[0]))
        {
            // The same as the last macroblock's (an MBAFF frame sets this per macroblock); list1 and the weights go with list0.
            return;
        }

        _pic = pic;
        _list0 = list0;
        _list1 = list1;
        _implicit = implicitWeights;
        _parity = pic.Parity;
        _fieldChroma = _parity >= 0 && _chroma == 1;
        _weightShift = pic.Parity >= 0 ? 1 : 0;
        _refIdx0 = motion.RefIdx[0];
        _refIdx1 = motion.RefIdx[1];
        _mv0 = motion.Mv[0];
        _mv1 = motion.Mv[1];
        _w4 = w4;
    }

    /// <summary>Gets whether all 16 blocks of the macroblock at 4x4 block (x0, y0) have the same motion in both lists.</summary>
    internal bool Uniform(int x0, int y0)
    {
        int w4 = _w4;
        int first = (y0 * w4) + x0;
        for (int list = 0; list < 2; list++)
        {
            sbyte[] refIdx = list == 0 ? _refIdx0 : _refIdx1;
            int[] mvs = list == 0 ? _mv0 : _mv1;
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
        if (_pic.Narrow)
        {
            Predict<byte>(x4, y4, w4, h4);
        }
        else
        {
            Predict<ushort>(x4, y4, w4, h4);
        }
    }

    private void Predict<T>(int x4, int y4, int w4, int h4)
        where T : unmanaged
    {
        // The slice decoder wrote this block's motion and checked its reference indices against the lists.
        int b = (y4 * _w4) + x4;
        Debug.Assert(b < _mv0.Length, "The block lies in the picture.");
        int r0 = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_refIdx0), b), r1 = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_refIdx1), b);
        int mv0 = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_mv0), b), mv1 = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_mv1), b);
        H264Picture? p0 = r0 >= 0 ? _list0[r0] : null;
        H264Picture? p1 = r1 >= 0 ? _list1[r1] : null;
        int w = w4 * 4, h = h4 * 4, x = x4 * 4, y = y4 * 4;
        if ((x4 & 3) == 0 && (y4 & 3) == 0)
        {
            // The first partition of a macroblock: the macroblocks after it likely move the same way, so the reference
            // samples of the one two ahead are fetched into the cache while this one is predicted.
            if (p0 is not null)
            {
                Prefetch<T>(p0, x + 32, y, mv0);
            }

            if (p1 is not null)
            {
                Prefetch<T>(p1, x + 32, y, mv1);
            }
        }

        bool bi = p0 is not null && p1 is not null;
        bool bytes = H264Samples.Narrow<T>() && w == 16 && UseVectors && UseWideVectors
            && (_explicit ? !bi : !bi || !_implicitMode || _implicit[(r0 * 32) + r1] == 32);
        int mode = bi ? Averaged : Single;
        ByteWeights weights = default;
        if (bytes && _explicit)
        {
            // One list with explicit weights: a reference without them (weight 2^logWD, offset 0) predicts as it is.
            int r = (p0 is not null ? r0 : r1) >> _weightShift;
            int[] wt = _h.Weights[p0 is not null ? 0 : 1];
            int logWD = _h.LumaLog2WeightDenom;
            if (wt[r * 6] != 1 << logWD || wt[(r * 6) + 1] != 0)
            {
                mode = Scaled;
                weights = new ByteWeights(wt[r * 6], wt[r * 6], wt[(r * 6) + 1], wt[(r * 6) + 1], logWD);
            }
        }

        if (bytes)
        {
            // 8 bits without weights: bytes from the filters to the picture, one list as it is or two averaged.
            byte[] luma = _pic.Y<byte>();
            int lumaAt = _pic.Origin + (y * _pic.Stride) + x;
            Debug.Assert(lumaAt >= 0 && lumaAt + ((h - 1) * _pic.Stride) + 16 <= luma.Length, "The block lies in the picture.");
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(luma), lumaAt);
            ref byte first = ref MemoryMarshal.GetArrayDataReference(_bytes0);
            if (!bi)
            {
                Luma16Bytes(p0 ?? p1!, x, y, h, p0 is not null ? mv0 : mv1, ref dst, _pic.Stride, ref first, mode, weights);
            }
            else
            {
                Luma16Bytes(p0!, x, y, h, mv0, ref first, 16, ref first, Single, weights);
                Luma16Bytes(p1!, x, y, h, mv1, ref dst, _pic.Stride, ref first, Averaged, weights);
            }
        }
        else
        {
            if (p0 is not null)
            {
                Luma<T>(p0, x, y, w, h, mv0, _pred0);
            }

            if (p1 is not null)
            {
                Luma<T>(p1, x, y, w, h, mv1, _pred1);
            }

            Combine(_pic.Y<T>(), _pic.Origin + (y * _pic.Stride) + x, _pic.Stride, w, h, r0, r1, 0, _maxY, _shiftY, 0);
        }
        if (_chroma == 0)
        {
            return;
        }

        if (_fieldChroma)
        {
            // A field predicted from the field of the other parity: the chroma vector moves a quarter row (8.4.1.4, Table 8-10).
            mv0 += p0 is not null ? (2 * (_parity - p0.Parity)) << 16 : 0;
            mv1 += p1 is not null ? (2 * (_parity - p1.Parity)) << 16 : 0;
        }

        int cw = w / 2, ch = _chroma == 1 ? h / 2 : h, cx = x / 2, cy = _chroma == 1 ? y / 2 : y;
        if (_chroma == 1)
        {
            if (bytes && _explicit)
            {
                int r = (p0 is not null ? r0 : r1) >> _weightShift;
                int[] wt = _h.Weights[p0 is not null ? 0 : 1];
                int logWD = _h.ChromaLog2WeightDenom, unit = 1 << logWD;
                if (wt[(r * 6) + 2] != unit || wt[(r * 6) + 3] != 0 || wt[(r * 6) + 4] != unit || wt[(r * 6) + 5] != 0)
                {
                    mode = Scaled;
                    weights = new ByteWeights(wt[(r * 6) + 2], wt[(r * 6) + 4], wt[(r * 6) + 3], wt[(r * 6) + 5], logWD);
                }
                else
                {
                    mode = Single;
                }
            }

            if (bytes && (p0 is null || ChromaInside(p0, cx, cy, ch, mv0)) && (p1 is null || ChromaInside(p1, cx, cy, ch, mv1)))
            {
                // As for luma: bytes from the filter to both planes, one list as it is or two averaged.
                int chromaAt = _pic.ChromaOrigin + (cy * _pic.ChromaStride) + cx;
                byte[] cbPlane = _pic.Cb<byte>(), crPlane = _pic.Cr<byte>();
                Debug.Assert(chromaAt >= 0 && chromaAt + ((ch - 1) * _pic.ChromaStride) + 8 <= Math.Min(cbPlane.Length, crPlane.Length), "The block lies in the picture.");
                ref byte dstCb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cbPlane), chromaAt);
                ref byte dstCr = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(crPlane), chromaAt);
                ref byte first = ref MemoryMarshal.GetArrayDataReference(_bytes0);
                if (!bi)
                {
                    ChromaBytes(p0 ?? p1!, cx, cy, ch, p0 is not null ? mv0 : mv1, ref dstCb, ref dstCr, _pic.ChromaStride, ref first, mode, weights);
                }
                else
                {
                    ChromaBytes(p0!, cx, cy, ch, mv0, ref first, ref Unsafe.Add(ref first, 8), 16, ref first, Single, weights);
                    ChromaBytes(p1!, cx, cy, ch, mv1, ref dstCb, ref dstCr, _pic.ChromaStride, ref first, Averaged, weights);
                }

                return;
            }

            // 4:2:0 blocks are at most 8 rows: Cb's prediction takes rows 0 to 7 of the buffers and Cr's rows 8 to 15.
            if (p0 is not null)
            {
                ChromaPair<T>(p0, cx, cy, cw, ch, mv0, _pred0);
            }

            if (p1 is not null)
            {
                ChromaPair<T>(p1, cx, cy, cw, ch, mv1, _pred1);
            }

            int at = _pic.ChromaOrigin + (cy * _pic.ChromaStride) + cx;
            if (p0 is not null && p1 is not null && !_explicit && cw == 8 && _maxC == 255 && UseVectors && UseWideVectors)
            {
                // Both planes take the same weights without explicit ones: their rows share a vector.
                int w0 = _implicitMode ? _implicit[(r0 * 32) + r1] : 32;
                WeightedPair(_pic.Cb<T>(), _pic.Cr<T>(), at, _pic.ChromaStride, ch, w0, 64 - w0);
                return;
            }

            Combine(_pic.Cb<T>(), at, _pic.ChromaStride, cw, ch, r0, r1, 1, _maxC, _shiftC, 0);
            Combine(_pic.Cr<T>(), at, _pic.ChromaStride, cw, ch, r0, r1, 2, _maxC, _shiftC, CrRows);
            return;
        }

        for (int c = 0; c < 2; c++)
        {
            if (p0 is not null)
            {
                Chroma(c == 0 ? p0.Cb<T>() : p0.Cr<T>(), p0, cx, cy, cw, ch, mv0, _pred0, 0);
            }

            if (p1 is not null)
            {
                Chroma(c == 0 ? p1.Cb<T>() : p1.Cr<T>(), p1, cx, cy, cw, ch, mv1, _pred1, 0);
            }

            T[] plane = c == 0 ? _pic.Cb<T>() : _pic.Cr<T>();
            Combine(plane, _pic.ChromaOrigin + (cy * _pic.ChromaStride) + cx, _pic.ChromaStride, cw, ch, r0, r1, 1 + c, _maxC, _shiftC, 0);
        }
    }

    /// <summary>
    /// Predicts a 4:2:0 chroma block of both planes from one reference, Cb at the start of pred and Cr <see cref="CrRows"/> after
    /// it; blocks four or eight wide interpolate a row of each plane in one 256-bit vector.
    /// </summary>
    private void ChromaPair<T>(H264Picture refPic, int x, int y, int w, int h, int mv, ushort[] pred)
        where T : unmanaged
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xInt = x + (mvx >> 3), xFrac = mvx & 7, yInt = y + (mvy >> 3), yFrac = mvy & 7;
        int pad = H264Picture.Pad, padY = refPic.PadY, stride = refPic.ChromaStride;
        int at = refPic.ChromaOrigin + (yInt * stride) + xInt;
        // One unsigned compare per axis tests both sides: the block and its extra row and column lie in the padded plane.
        if (w < 4 || !UseVectors || !UseWideVectors
            || (uint)(xInt + pad) > (uint)(refPic.ChromaWidth + (2 * pad) - w - 1) || (uint)(yInt + padY) > (uint)(refPic.ChromaSpanY - h - 1))
        {
            Chroma(refPic.Cb<T>(), refPic, x, y, w, h, mv, pred, 0);
            Chroma(refPic.Cr<T>(), refPic, x, y, w, h, mv, pred, CrRows);
            return;
        }

        // Each row is first interpolated across, (8 - xFrac) a + xFrac b, then two rows down, which is the four-weight sum of
        // 8-266 exactly: no rounding comes between. The sums stay below 65536 at up to 10 bits.
        ref T cb = ref H264Samples.At(refPic.Cb<T>(), at);
        ref T cr = ref H264Samples.At(refPic.Cr<T>(), at);
        ref ushort d = ref MemoryMarshal.GetArrayDataReference(pred);
        Vector256<ushort> wTop = Vector256.Create((ushort)(8 - yFrac)), wBottom = Vector256.Create((ushort)yFrac), round = Vector256.Create((ushort)32);
        if ((xFrac | yFrac) == 0)
        {
            // Full samples: the block itself.
            for (int j = 0; j < h; j++)
            {
                H264Samples.Load8(ref cb).AsUInt16().StoreUnsafe(ref d);
                H264Samples.Load8(ref cr).AsUInt16().StoreUnsafe(ref d, CrRows);
                cb = ref Unsafe.Add(ref cb, stride);
                cr = ref Unsafe.Add(ref cr, stride);
                d = ref Unsafe.Add(ref d, PredStride);
            }

            return;
        }

        if (H264Samples.Narrow<T>())
        {
            // Bytes: each sample and its right neighbour interleave into pairs, and one multiply-add of the pairs by
            // (8 - xFrac, xFrac) gives a row of both planes across.
            ref byte b = ref Unsafe.As<T, byte>(ref cb), r = ref Unsafe.As<T, byte>(ref cr);
            Vector256<sbyte> across = Vector256.Create((short)((xFrac << 8) | (8 - xFrac))).AsSByte();
            Vector256<ushort> top = Across(ref b, ref r, across);
            for (int j = 0; j < h; j++)
            {
                b = ref Unsafe.Add(ref b, stride);
                r = ref Unsafe.Add(ref r, stride);
                Vector256<ushort> bottom = Across(ref b, ref r, across);
                Vector256<ushort> v = ((wTop * top) + (wBottom * bottom) + round) >>> 6;
                v.GetLower().StoreUnsafe(ref d);
                v.GetUpper().StoreUnsafe(ref d, CrRows);
                d = ref Unsafe.Add(ref d, PredStride);
                top = bottom;
            }

            return;
        }

        Vector256<ushort> wa = Vector256.Create((ushort)(8 - xFrac)), wb = Vector256.Create((ushort)xFrac);
        Vector256<ushort> above = (wa * Vector256.Create(H264Samples.Load8(ref cb).AsUInt16(), H264Samples.Load8(ref cr).AsUInt16()))
            + (wb * Vector256.Create(H264Samples.Load8(ref cb, (nint)(1)).AsUInt16(), H264Samples.Load8(ref cr, (nint)(1)).AsUInt16()));
        for (int j = 0; j < h; j++)
        {
            nuint next = (nuint)((j + 1) * stride);
            Vector256<ushort> below = (wa * Vector256.Create(H264Samples.Load8(ref cb, (nint)(next)).AsUInt16(), H264Samples.Load8(ref cr, (nint)(next)).AsUInt16()))
                + (wb * Vector256.Create(H264Samples.Load8(ref cb, (nint)(next + 1)).AsUInt16(), H264Samples.Load8(ref cr, (nint)(next + 1)).AsUInt16()));
            Vector256<ushort> v = ((wTop * above) + (wBottom * below) + round) >>> 6;
            v.GetLower().StoreUnsafe(ref d, (nuint)(j * PredStride));
            v.GetUpper().StoreUnsafe(ref d, (nuint)((j * PredStride) + CrRows));
            above = below;
        }
    }

    /// <summary>Whether an 8-wide 4:2:0 block at (x, y) moved by mv, with its extra row and column, lies in the padded planes.</summary>
    private static bool ChromaInside(H264Picture refPic, int x, int y, int h, int mv)
    {
        int xInt = x + (H264SliceDecoder.MvX(mv) >> 3), yInt = y + (H264SliceDecoder.MvY(mv) >> 3), pad = H264Picture.Pad, padY = refPic.PadY;
        return (uint)(xInt + pad) <= (uint)(refPic.ChromaWidth + (2 * pad) - 9) && (uint)(yInt + padY) <= (uint)(refPic.ChromaSpanY - h - 1);
    }

    /// <summary>
    /// The 4:2:0 chroma prediction of an 8-wide block of an 8-bit picture in bytes: each row of Cb and Cr is packed to bytes
    /// and written to the destinations, or averaged with the first list's prediction (Cb in bytes 0 to 7 of each 16, Cr in 8 to 15).
    /// </summary>
    private static void ChromaBytes(H264Picture refPic, int x, int y, int h, int mv, ref byte dstCb, ref byte dstCr, int dstStride, ref byte first, int mode, in ByteWeights weights)
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xFrac = mvx & 7, yFrac = mvy & 7, stride = refPic.ChromaStride;
        int at = refPic.ChromaOrigin + ((y + (mvy >> 3)) * stride) + x + (mvx >> 3);
        byte[] cbPlane = refPic.Cb<byte>(), crPlane = refPic.Cr<byte>();
        Debug.Assert(at >= 0 && at + (h * stride) + 9 <= Math.Min(cbPlane.Length, crPlane.Length), "ChromaInside keeps the block in the padded planes.");
        ref byte b = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cbPlane), at);
        ref byte r = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(crPlane), at);
        if ((xFrac | yFrac) == 0)
        {
            for (int j = 0; j < h; j++)
            {
                Vector128<byte> v = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref b), Unsafe.ReadUnaligned<ulong>(ref r)).AsByte();
                StoreChroma(v, ref dstCb, ref dstCr, ref first, mode, weights);
                b = ref Unsafe.Add(ref b, stride);
                r = ref Unsafe.Add(ref r, stride);
                dstCb = ref Unsafe.Add(ref dstCb, dstStride);
                dstCr = ref Unsafe.Add(ref dstCr, dstStride);
                first = ref Unsafe.Add(ref first, 16);
            }

            return;
        }

        Vector256<ushort> wTop = Vector256.Create((ushort)(8 - yFrac)), wBottom = Vector256.Create((ushort)yFrac), round = Vector256.Create((ushort)32);
        Vector256<sbyte> across = Vector256.Create((short)((xFrac << 8) | (8 - xFrac))).AsSByte();
        Vector256<ushort> top = Across(ref b, ref r, across);
        for (int j = 0; j < h; j++)
        {
            b = ref Unsafe.Add(ref b, stride);
            r = ref Unsafe.Add(ref r, stride);
            Vector256<ushort> bottom = Across(ref b, ref r, across);
            Vector256<short> v = (((wTop * top) + (wBottom * bottom) + round) >>> 6).AsInt16();
            StoreChroma(Sse2.PackUnsignedSaturate(v.GetLower(), v.GetUpper()), ref dstCb, ref dstCr, ref first, mode, weights);
            dstCb = ref Unsafe.Add(ref dstCb, dstStride);
            dstCr = ref Unsafe.Add(ref dstCr, dstStride);
            first = ref Unsafe.Add(ref first, 16);
            top = bottom;
        }
    }

    /// <summary>Writes a row of Cb (bytes 0 to 7) and Cr (8 to 15), averaged with the first list's row or weighted as the mode says.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreChroma(Vector128<byte> v, ref byte dstCb, ref byte dstCr, ref byte first, int mode, in ByteWeights weights)
    {
        if (mode == Averaged)
        {
            v = Sse2.Average(v, Vector128.LoadUnsafe(ref first));
        }
        else if (mode == Scaled)
        {
            v = weights.Apply(v);
        }

        Unsafe.WriteUnaligned(ref dstCb, v.AsUInt64().ToScalar());
        Unsafe.WriteUnaligned(ref dstCr, v.AsUInt64().GetElement(1));
    }

    /// <summary>The horizontal pass of 4:2:0 chroma for eight samples of a Cb row and a Cr row: (8 - xFrac) a + xFrac b.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> Across(ref byte cb, ref byte cr, Vector256<sbyte> weights)
    {
        Vector128<byte> pairsB = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref cb)).AsByte(),
            Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref cb, 1))).AsByte());
        Vector128<byte> pairsR = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref cr)).AsByte(),
            Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref cr, 1))).AsByte());
        return Avx2.MultiplyAddAdjacent(Vector256.Create(pairsB, pairsR), weights).AsUInt16();
    }

    /// <summary>Asks the cache for the reference samples a 16x16 block at (x, y) moved by mv reads, luma and chroma.</summary>
    private unsafe void Prefetch<T>(H264Picture refPic, int x, int y, int mv)
        where T : unmanaged
    {
        if (!Sse.IsSupported)
        {
            return;
        }

        int pad = H264Picture.Pad;
        int xs = Math.Clamp(x + (H264SliceDecoder.MvX(mv) >> 2) - 2, -pad, refPic.Width + pad - 24);
        int ys = Math.Clamp(y + (H264SliceDecoder.MvY(mv) >> 2) - 2, refPic.PrefetchTop, refPic.PrefetchBottom);
        // Byte addresses, so each prefetch takes its address as an operand. A prefetch never faults: if the collector moves
        // the plane meanwhile, the hint is only wasted.
        byte* luma = (byte*)Unsafe.AsPointer(ref H264Samples.At(refPic.Y<T>(), refPic.Origin + (ys * refPic.Stride) + xs));
        nint stride = refPic.Stride * sizeof(T), across = 23 * sizeof(T);
        for (int row = 0; row < 21; row++)
        {
            Sse.Prefetch0(luma);
            Sse.Prefetch0(luma + across);
            luma += stride;
        }

        if (_chroma != 1)
        {
            return;
        }

        int cx = Math.Clamp((x >> 1) + (H264SliceDecoder.MvX(mv) >> 3), -pad, refPic.ChromaWidth + pad - 9);
        int cy = Math.Clamp((y >> 1) + (H264SliceDecoder.MvY(mv) >> 3), refPic.PrefetchTop, refPic.PrefetchChromaBottom);
        int c = refPic.ChromaOrigin + (cy * refPic.ChromaStride) + cx;
        byte* cb = (byte*)Unsafe.AsPointer(ref H264Samples.At(refPic.Cb<T>(), c));
        byte* cr = (byte*)Unsafe.AsPointer(ref H264Samples.At(refPic.Cr<T>(), c));
        nint cStride = refPic.ChromaStride * sizeof(T), cAcross = 9 * sizeof(T);
        for (int row = 0; row < 9; row++)
        {
            Sse.Prefetch0(cb);
            Sse.Prefetch0(cb + cAcross);
            Sse.Prefetch0(cr);
            Sse.Prefetch0(cr + cAcross);
            cb += cStride;
            cr += cStride;
        }
    }

    /// <summary>Weighted sample prediction (8.4.2.3): default, explicit or implicit, into the picture.</summary>
    private void Combine<T>(T[] plane, int at, int stride, int w, int h, int r0, int r1, int component, int max, int bitShift, int po)
        where T : unmanaged
    {
        bool bi = r0 >= 0 && r1 >= 0;
        if (_explicit)
        {
            int logWD = component == 0 ? _h.LumaLog2WeightDenom : _h.ChromaLog2WeightDenom;
            int[] wt0 = _h.Weights[0], wt1 = _h.Weights[1];
            if (bi)
            {
                int e0 = r0 >> _weightShift, e1 = r1 >> _weightShift;
                int w0 = wt0[(e0 * 6) + (component * 2)], o0 = wt0[(e0 * 6) + (component * 2) + 1] << bitShift;
                int w1 = wt1[(e1 * 6) + (component * 2)], o1 = wt1[(e1 * 6) + (component * 2) + 1] << bitShift;
                Weighted(plane, at, stride, w, h, w0, w1, 1 << logWD, logWD + 1, (o0 + o1 + 1) >> 1, max, po);
            }
            else
            {
                int r = (r0 >= 0 ? r0 : r1) >> _weightShift;
                int[] wt = r0 >= 0 ? wt0 : wt1;
                int wgt = wt[(r * 6) + (component * 2)], o = wt[(r * 6) + (component * 2) + 1] << bitShift;
                if (r0 < 0)
                {
                    _pred1.AsSpan().CopyTo(_pred0);
                }

                Weighted(plane, at, stride, w, h, wgt, 0, logWD >= 1 ? 1 << (logWD - 1) : 0, logWD, o, max, po);
            }

            return;
        }

        if (bi && _implicitMode)
        {
            int w0 = _implicit[(r0 * 32) + r1];
            Weighted(plane, at, stride, w, h, w0, 64 - w0, 32, 6, 0, max, po);
            return;
        }

        ushort[] first = r0 >= 0 ? _pred0 : _pred1;
        if (bi)
        {
            Average(_pred0, _pred1, w, h, po);
        }

        Store(first, plane, at, stride, w, h, po);
    }

    /// <summary>
    /// The weighted forms (8-270 to 8-301) in one: ((pred0 × w0 + pred1 × w1 + round) &gt;&gt; shift) + offset, clipped, into the
    /// picture; one-list prediction has w1 0. The products need 32-bit lanes, four samples to a vector.
    /// </summary>
    private void Weighted<T>(T[] plane, int at, int stride, int w, int h, int w0, int w1, int round, int shift, int offset, int max, int po)
        where T : unmanaged
    {
        // The block's position comes from a macroblock address the slice decoder checked.
        Debug.Assert(at >= 0 && at + ((h - 1) * stride) + w <= plane.Length, "The block lies in the picture.");
        if (!UseVectors || w < 4)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int v = (((_pred0[(y * PredStride) + x + po] * w0) + (_pred1[(y * PredStride) + x + po] * w1) + round) >> shift) + offset;
                    H264Samples.Set(plane, at + (y * stride) + x, Math.Clamp(v, 0, max));
                }
            }

            return;
        }

        ref ushort a = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_pred0), po);
        ref ushort b = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_pred1), po);
        ref T d = ref H264Samples.At(plane, at);
        if (max == 255 && Math.Max(w0, 0) + Math.Max(w1, 0) <= 128 && Math.Max(-w0, 0) + Math.Max(-w1, 0) <= 128 && round <= 64)
        {
            // At 8 bits with these weights the weighted sum lies in a signed 16-bit lane; the products may wrap on the way, but
            // the sum they wrap to is the true one.
            Vector128<short> sw0 = Vector128.Create((short)w0), sw1 = Vector128.Create((short)w1), sRound = Vector128.Create((short)round),
                sOffset = Vector128.Create((short)offset), sMax = Vector128.Create((short)max);
            bool average = w0 == w1 && offset == 0 && shift >= 1 && w0 == 1 << (shift - 1) && round == w0;
            if (w == 16 && UseWideVectors)
            {
                Vector256<short> lw0 = Vector256.Create((short)w0), lw1 = Vector256.Create((short)w1), lRound = Vector256.Create((short)round),
                    lOffset = Vector256.Create((short)offset), lMax = Vector256.Create((short)max);
                if (average)
                {
                    for (int y = 0; y < h; y++)
                    {
                        nuint i = (nuint)(y * PredStride);
                        H264Samples.Store16(ref d, y * stride, ((Vector256.LoadUnsafe(ref a, i) + Vector256.LoadUnsafe(ref b, i) + Vector256<ushort>.One) >>> 1).AsInt16());
                    }

                    return;
                }

                for (int y = 0; y < h; y++)
                {
                    nuint i = (nuint)(y * PredStride);
                    Vector256<short> p0 = Vector256.LoadUnsafe(ref a, i).AsInt16(), p1 = Vector256.LoadUnsafe(ref b, i).AsInt16();
                    Vector256<short> v = (((p0 * lw0) + (p1 * lw1) + lRound) >> shift) + lOffset;

                    // Bytes saturate to 0 to 255 as they are packed.
                    H264Samples.Store16(ref d, y * stride, H264Samples.Narrow<T>() ? v : Vector256.Min(Vector256.Max(v, Vector256<short>.Zero), lMax));
                }

                return;
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x += 8)
                {
                    nuint i = (nuint)((y * PredStride) + x);
                    Vector128<short> p0 = Vector128.LoadUnsafe(ref a, i).AsInt16(), p1 = Vector128.LoadUnsafe(ref b, i).AsInt16();
                    Vector128<short> v;
                    if (average)
                    {
                        // Equal weights of half the denominator: the rounded average.
                        v = ((p0 + p1 + Vector128<short>.One) >>> 1);
                    }
                    else
                    {
                        v = (((p0 * sw0) + (p1 * sw1) + sRound) >> shift) + sOffset;
                        v = Vector128.Min(Vector128.Max(v, Vector128<short>.Zero), sMax);
                    }

                    if (w == 4)
                    {
                        H264Samples.Store4(ref d, (y * stride) + x, v.AsInt16());
                    }
                    else
                    {
                        H264Samples.Store8(ref d, (y * stride) + x, v.AsInt16());
                    }
                }
            }

            return;
        }

        if (Sse2.IsSupported)
        {
            // Two lists: the products of each sample pair add in one multiply-add into 32-bit lanes.
            Vector128<short> weights = Vector128.Create((int)((ushort)w0 | ((uint)(ushort)w1 << 16))).AsInt16();
            Vector128<int> iRound = Vector128.Create(round), iOffset = Vector128.Create(offset), iMax = Vector128.Create(max);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x += 8)
                {
                    nuint i = (nuint)((y * PredStride) + x);
                    Vector128<short> p0 = Vector128.LoadUnsafe(ref a, i).AsInt16(), p1 = Vector128.LoadUnsafe(ref b, i).AsInt16();
                    Vector128<int> lo = ((Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(p0, p1), weights) + iRound) >> shift) + iOffset;
                    Vector128<int> hi = ((Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(p0, p1), weights) + iRound) >> shift) + iOffset;
                    lo = Vector128.Min(Vector128.Max(lo, Vector128<int>.Zero), iMax);
                    hi = Vector128.Min(Vector128.Max(hi, Vector128<int>.Zero), iMax);
                    Vector128<ushort> v = Vector128.Narrow(lo.AsUInt32(), hi.AsUInt32());
                    if (w == 4)
                    {
                        H264Samples.Store4(ref d, (y * stride) + x, v.AsInt16());
                    }
                    else
                    {
                        H264Samples.Store8(ref d, (y * stride) + x, v.AsInt16());
                    }
                }
            }

            return;
        }

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
                if (w == 4)
                {
                    H264Samples.Store4(ref d, (y * stride) + x, v.AsInt16());
                }
                else
                {
                    H264Samples.Store8(ref d, (y * stride) + x, v.AsInt16());
                }
            }
        }
    }

    /// <summary>
    /// Implicit or default two-list weighting of an 8-wide 4:2:0 chroma block at 8 bits, Cb's row and Cr's row in one vector:
    /// ((p0 w0 + p1 w1 + 32) &gt;&gt; 6) with w0 + w1 = 64, which equal weights make the rounded average.
    /// </summary>
    private void WeightedPair<T>(T[] cb, T[] cr, int at, int stride, int h, int w0, int w1)
        where T : unmanaged
    {
        Debug.Assert(at >= 0 && at + ((h - 1) * stride) + 8 <= Math.Min(cb.Length, cr.Length) && w0 is >= -64 and <= 128, "The block lies in the picture.");

        ref ushort a = ref MemoryMarshal.GetArrayDataReference(_pred0);
        ref ushort b = ref MemoryMarshal.GetArrayDataReference(_pred1);
        ref T db = ref H264Samples.At(cb, at), dr = ref H264Samples.At(cr, at);
        Vector256<short> lw0 = Vector256.Create((short)w0), lw1 = Vector256.Create((short)w1), round = Vector256.Create((short)32);
        Vector256<short> top = Vector256.Create((short)255);
        bool average = w0 == 32;
        for (int j = 0; j < h; j++)
        {
            Vector256<short> p0 = Vector256.Create(Vector128.LoadUnsafe(ref a), Vector128.LoadUnsafe(ref a, CrRows)).AsInt16();
            Vector256<short> p1 = Vector256.Create(Vector128.LoadUnsafe(ref b), Vector128.LoadUnsafe(ref b, CrRows)).AsInt16();
            Vector256<short> v = average ? (p0 + p1 + Vector256<short>.One) >>> 1 : ((p0 * lw0) + (p1 * lw1) + round) >> 6;
            if (!H264Samples.Narrow<T>())
            {
                v = Vector256.Min(Vector256.Max(v, Vector256<short>.Zero), top);
            }

            H264Samples.Store8(ref db, 0, v.GetLower());
            H264Samples.Store8(ref dr, 0, v.GetUpper());
            a = ref Unsafe.Add(ref a, PredStride);
            b = ref Unsafe.Add(ref b, PredStride);
            db = ref Unsafe.Add(ref db, stride);
            dr = ref Unsafe.Add(ref dr, stride);
        }
    }

    /// <summary>Writes a w by h prediction into the picture.</summary>
    private static void Store<T>(ushort[] pred, T[] plane, int at, int stride, int w, int h, int po)
        where T : unmanaged
    {
        Debug.Assert(at >= 0 && at + ((h - 1) * stride) + w <= plane.Length, "The block lies in the picture.");
        ref ushort p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pred), po);
        ref T d = ref H264Samples.At(plane, at);
        switch (w)
        {
            case 16:
                for (int y = 0; y < h; y++, p = ref Unsafe.Add(ref p, PredStride), d = ref Unsafe.Add(ref d, stride))
                {
                    H264Samples.Store16(ref d, 0, Vector256.LoadUnsafe(ref p).AsInt16());
                }

                break;
            case 8:
                for (int y = 0; y < h; y++, p = ref Unsafe.Add(ref p, PredStride), d = ref Unsafe.Add(ref d, stride))
                {
                    H264Samples.Store8(ref d, 0, Vector128.LoadUnsafe(ref p).AsInt16());
                }

                break;
            case 4:
                for (int y = 0; y < h; y++, p = ref Unsafe.Add(ref p, PredStride), d = ref Unsafe.Add(ref d, stride))
                {
                    H264Samples.Store4(ref d, 0, Vector128.LoadUnsafe(ref p).AsInt16());
                }

                break;
            default:
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        H264Samples.Set(plane, at + (y * stride) + x, Unsafe.Add(ref p, (y * PredStride) + x));
                    }
                }

                break;
        }
    }

    /// <summary>Rounds the average of two predictions into the first: (a + b + 1) >> 1, which fits 16 bits at up to 15 bits.</summary>
    private static void Average(ushort[] into, ushort[] other, int w, int h, int po = 0)
    {
        if (UseVectors)
        {
            ref ushort a = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(into), po);
            ref ushort b = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(other), po);
            Vector128<ushort> one = Vector128<ushort>.One;
            if (w == 16 && UseWideVectors)
            {
                for (int y = 0; y < h; y++)
                {
                    nuint i = (nuint)(y * PredStride);
                    ((Vector256.LoadUnsafe(ref a, i) + Vector256.LoadUnsafe(ref b, i) + Vector256<ushort>.One) >>> 1).StoreUnsafe(ref a, i);
                }

                return;
            }

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
                int i = (y * PredStride) + x + po;
                into[i] = (ushort)((into[i] + other[i] + 1) >> 1);
            }
        }
    }

    /// <summary>Copies a w by h source area at (x0, y0) of a plane into <see cref="_edge"/>, clamping coordinates to the picture.</summary>
    private void FetchEdge<T>(T[] plane, int origin, int stride, int width, int height, int x0, int y0, int w, int h)
        where T : unmanaged
    {
        T[] edge = Edge<T>();
        for (int y = 0; y < h; y++)
        {
            int row = origin + (Math.Clamp(y0 + y, 0, height - 1) * stride);
            for (int x = 0; x < w; x++)
            {
                edge[(y * EdgeStride) + x] = plane[row + Math.Clamp(x0 + x, 0, width - 1)];
            }
        }
    }

    /// <summary>Luma sample interpolation (8.4.2.2.1) of a w by h block at (x, y) moved by mv, into pred.</summary>
    private void Luma<T>(H264Picture refPic, int x, int y, int w, int h, int mv, ushort[] pred)
        where T : unmanaged
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xInt = x + (mvx >> 2), yInt = y + (mvy >> 2);
        int frac = ((mvx & 3) << 2) | (mvy & 3);
        T[] src = refPic.Y<T>();
        int stride = refPic.Stride, at = refPic.Origin + (yInt * stride) + xInt;
        int pad = H264Picture.Pad, padY = refPic.PadY;
        if ((uint)(xInt - 2 + pad) > (uint)(refPic.Width + (2 * pad) - w - 5) || (uint)(yInt - 2 + padY) > (uint)(refPic.SpanY - h - 5))
        {
            // Past the border: a clamped copy, wide enough for the vector loads to read past the block.
            FetchEdge(src, refPic.Origin, stride, refPic.Width, refPic.Height, xInt - 2, yInt - 2, Math.Min(w + 13, EdgeStride), h + 6);
            src = Edge<T>();
            stride = EdgeStride;
            at = (2 * EdgeStride) + 2;
        }

        CheckReach(src, at, stride, h);
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

    /// <summary>
    /// The luma prediction of a 16-wide block of an 8-bit picture without weights, kept in bytes from end to end: each filter
    /// packs its row to bytes, which clips it, quarter samples are the rounded average of two such rows (one pavgb), and the
    /// last pass writes the destination, averaging with the first list's prediction for two lists.
    /// </summary>
    private void Luma16Bytes(H264Picture refPic, int x, int y, int h, int mv, ref byte dst, int dstStride, ref byte first, int mode, in ByteWeights weights)
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xInt = x + (mvx >> 2), yInt = y + (mvy >> 2);
        int frac = ((mvx & 3) << 2) | (mvy & 3);
        byte[] src = refPic.Y<byte>();
        int stride = refPic.Stride, at = refPic.Origin + (yInt * stride) + xInt;
        int pad = H264Picture.Pad, padY = refPic.PadY;
        if ((uint)(xInt - 2 + pad) > (uint)(refPic.Width + (2 * pad) - 21) || (uint)(yInt - 2 + padY) > (uint)(refPic.SpanY - h - 5))
        {
            FetchEdge(src, refPic.Origin, stride, refPic.Width, refPic.Height, xInt - 2, yInt - 2, EdgeStride, h + 6);
            src = _edge8;
            stride = EdgeStride;
            at = (2 * EdgeStride) + 2;
        }

        CheckReach(src, at, stride, h);
        ref byte s = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at);
        ref byte a = ref MemoryMarshal.GetArrayDataReference(_bytesA), b = ref MemoryMarshal.GetArrayDataReference(_bytesB);
        int aStride = 16;
        bool two = true;
        switch (frac)
        {
            case 0x0:
                // Full samples: the last pass reads the reference itself.
                a = ref s;
                aStride = stride;
                two = false;
                break;
            case 0x1:
                CopyBytes(ref s, stride, h, ref a);
                HalfVBytes(ref s, stride, h, ref b);
                break;
            case 0x2:
                HalfVBytes(ref s, stride, h, ref a);
                two = false;
                break;
            case 0x3:
                CopyBytes(ref Unsafe.Add(ref s, stride), stride, h, ref a);
                HalfVBytes(ref s, stride, h, ref b);
                break;
            case 0x4:
                CopyBytes(ref s, stride, h, ref a);
                HalfHBytes(ref s, stride, h, ref b);
                break;
            case 0x5:
                HalfHBytes(ref s, stride, h, ref a);
                HalfVBytes(ref s, stride, h, ref b);
                break;
            case 0x6:
                HalfVBytes(ref s, stride, h, ref a);
                CenterBytes(ref s, stride, h, ref b);
                break;
            case 0x7:
                HalfVBytes(ref s, stride, h, ref a);
                HalfHBytes(ref Unsafe.Add(ref s, stride), stride, h, ref b);
                break;
            case 0x8:
                HalfHBytes(ref s, stride, h, ref a);
                two = false;
                break;
            case 0x9:
                HalfHBytes(ref s, stride, h, ref a);
                CenterBytes(ref s, stride, h, ref b);
                break;
            case 0xA:
                CenterBytes(ref s, stride, h, ref a);
                two = false;
                break;
            case 0xB:
                CenterBytes(ref s, stride, h, ref a);
                HalfHBytes(ref Unsafe.Add(ref s, stride), stride, h, ref b);
                break;
            case 0xC:
                CopyBytes(ref Unsafe.Add(ref s, 1), stride, h, ref a);
                HalfHBytes(ref s, stride, h, ref b);
                break;
            case 0xD:
                HalfHBytes(ref s, stride, h, ref a);
                HalfVBytes(ref Unsafe.Add(ref s, 1), stride, h, ref b);
                break;
            case 0xE:
                CenterBytes(ref s, stride, h, ref a);
                HalfVBytes(ref Unsafe.Add(ref s, 1), stride, h, ref b);
                break;
            default:
                HalfVBytes(ref Unsafe.Add(ref s, 1), stride, h, ref a);
                HalfHBytes(ref Unsafe.Add(ref s, stride), stride, h, ref b);
                break;
        }

        // One loop per case keeps the rows free of branches.
        if (mode == Scaled)
        {
            for (int j = 0; j < h; j++)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref a);
                if (two)
                {
                    v = Sse2.Average(v, Vector128.LoadUnsafe(ref b));
                }

                weights.Apply(v).StoreUnsafe(ref dst);
                a = ref Unsafe.Add(ref a, aStride);
                b = ref Unsafe.Add(ref b, 16);
                dst = ref Unsafe.Add(ref dst, dstStride);
            }
        }
        else if (two)
        {
            if (mode == Averaged)
            {
                for (int j = 0; j < h; j++)
                {
                    Vector128<byte> v = Sse2.Average(Vector128.LoadUnsafe(ref a), Vector128.LoadUnsafe(ref b));
                    Sse2.Average(v, Vector128.LoadUnsafe(ref first)).StoreUnsafe(ref dst);
                    a = ref Unsafe.Add(ref a, aStride);
                    b = ref Unsafe.Add(ref b, 16);
                    first = ref Unsafe.Add(ref first, 16);
                    dst = ref Unsafe.Add(ref dst, dstStride);
                }
            }
            else
            {
                for (int j = 0; j < h; j++)
                {
                    Sse2.Average(Vector128.LoadUnsafe(ref a), Vector128.LoadUnsafe(ref b)).StoreUnsafe(ref dst);
                    a = ref Unsafe.Add(ref a, aStride);
                    b = ref Unsafe.Add(ref b, 16);
                    dst = ref Unsafe.Add(ref dst, dstStride);
                }
            }
        }
        else if (mode == Averaged)
        {
            for (int j = 0; j < h; j++)
            {
                Sse2.Average(Vector128.LoadUnsafe(ref a), Vector128.LoadUnsafe(ref first)).StoreUnsafe(ref dst);
                a = ref Unsafe.Add(ref a, aStride);
                first = ref Unsafe.Add(ref first, 16);
                dst = ref Unsafe.Add(ref dst, dstStride);
            }
        }
        else
        {
            for (int j = 0; j < h; j++)
            {
                Vector128.LoadUnsafe(ref a).StoreUnsafe(ref dst);
                a = ref Unsafe.Add(ref a, aStride);
                dst = ref Unsafe.Add(ref dst, dstStride);
            }
        }
    }

    private static void CopyBytes(ref byte s, int stride, int h, ref byte d)
    {
        for (int j = 0; j < h; j++)
        {
            Vector128.LoadUnsafe(ref s).StoreUnsafe(ref d);
            s = ref Unsafe.Add(ref s, stride);
            d = ref Unsafe.Add(ref d, 16);
        }
    }

    /// <summary>Packs 16 lanes of 16-bit results to bytes, clipping them to 0 to 255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PackBytes(Vector256<short> v) => Sse2.PackUnsignedSaturate(v.GetLower(), v.GetUpper());

    private static void HalfHBytes(ref byte s, int stride, int h, ref byte d)
    {
        Vector256<short> round = Vector256.Create((short)16);
        ref byte r = ref Unsafe.Subtract(ref s, 2);
        for (int j = 0; j < h; j++)
        {
            Vector256<short> t = Tap(Load16(ref r, 0), Load16(ref r, 1), Load16(ref r, 2), Load16(ref r, 3), Load16(ref r, 4), Load16(ref r, 5));
            PackBytes((t + round) >> 5).StoreUnsafe(ref d);
            r = ref Unsafe.Add(ref r, stride);
            d = ref Unsafe.Add(ref d, 16);
        }
    }

    private static void HalfVBytes(ref byte s, int stride, int h, ref byte d)
    {
        Vector256<short> round = Vector256.Create((short)16);
        ref byte r = ref Unsafe.Subtract(ref s, 2 * stride);
        Vector256<short> a = Load16(ref r, 0), b = Load16(ref r, stride), c = Load16(ref r, 2 * stride), e = Load16(ref r, 3 * stride),
            f = Load16(ref r, 4 * stride);
        r = ref Unsafe.Add(ref r, 5 * stride);
        for (int j = 0; j < h; j++)
        {
            Vector256<short> g = Load16(ref r, 0);
            PackBytes((Tap(a, b, c, e, f, g) + round) >> 5).StoreUnsafe(ref d);
            (a, b, c, e, f) = (b, c, e, f, g);
            r = ref Unsafe.Add(ref r, stride);
            d = ref Unsafe.Add(ref d, 16);
        }
    }

    /// <summary>j in bytes: the unrounded horizontal sums of rows -2 to h + 2, then the vertical filter in 32-bit lanes.</summary>
    private void CenterBytes(ref byte s, int stride, int h, ref byte d)
    {
        ref byte r = ref Unsafe.Subtract(ref s, (2 * stride) + 2);
        ref short m = ref MemoryMarshal.GetArrayDataReference(_mid);
        ref short mr = ref m;
        for (int j = 0; j < h + 5; j++)
        {
            Tap(Load16(ref r, 0), Load16(ref r, 1), Load16(ref r, 2), Load16(ref r, 3), Load16(ref r, 4), Load16(ref r, 5)).StoreUnsafe(ref mr);
            r = ref Unsafe.Add(ref r, stride);
            mr = ref Unsafe.Add(ref mr, PredStride);
        }

        Vector256<int> round = Vector256.Create(512);
        for (int j = 0; j < h; j++)
        {
            Vector256<short> a = Vector256.LoadUnsafe(ref m), b = Vector256.LoadUnsafe(ref m, PredStride), c = Vector256.LoadUnsafe(ref m, 2 * PredStride),
                e = Vector256.LoadUnsafe(ref m, 3 * PredStride), f = Vector256.LoadUnsafe(ref m, 4 * PredStride), g = Vector256.LoadUnsafe(ref m, 5 * PredStride);
            Vector256<int> lo = (Tap32(Vector256.WidenLower(a), Vector256.WidenLower(b), Vector256.WidenLower(c), Vector256.WidenLower(e), Vector256.WidenLower(f), Vector256.WidenLower(g)) + round) >> 10;
            Vector256<int> hi = (Tap32(Vector256.WidenUpper(a), Vector256.WidenUpper(b), Vector256.WidenUpper(c), Vector256.WidenUpper(e), Vector256.WidenUpper(f), Vector256.WidenUpper(g)) + round) >> 10;

            // Widening splits the lanes in order, low eight then high eight, so the signed pack and the byte pack keep the order.
            Vector128<short> low = Sse2.PackSignedSaturate(lo.GetLower(), lo.GetUpper()), high = Sse2.PackSignedSaturate(hi.GetLower(), hi.GetUpper());
            Sse2.PackUnsignedSaturate(low, high).StoreUnsafe(ref d);
            m = ref Unsafe.Add(ref m, PredStride);
            d = ref Unsafe.Add(ref d, 16);
        }
    }

    private static Vector256<int> Tap32(Vector256<int> a, Vector256<int> b, Vector256<int> c, Vector256<int> d, Vector256<int> e, Vector256<int> f) =>
        ((c + d) * 20) - ((b + e) * 5) + (a + f);

    /// <summary>
    /// Checks that the samples a block's filters read, rows -2 to h + 2 around at and up to 24 columns past it, lie in src. The
    /// vector loads read past a narrow block's last column; at the last rows of the plane that lands in its slack.
    /// </summary>
    [Conditional("DEBUG")]
    private static void CheckReach<T>(T[] src, int at, int stride, int h)
        where T : unmanaged =>
        Debug.Assert(at - (2 * stride) - 2 >= 0 && at + ((h + 2) * stride) + 24 <= src.Length, "The border test in Luma keeps the filters in the padded plane.");

    /// <summary>The full samples G, eight at a time.</summary>
    private static void Copy<T>(T[] src, int at, int stride, int w, int h, ushort[] dst)
        where T : unmanaged
    {
        ref T s = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at);
        ref ushort d = ref MemoryMarshal.GetArrayDataReference(dst);
        if (w == 16 && UseWideVectors)
        {
            for (int j = 0; j < h; j++)
            {
                H264Samples.Load16(ref s, j * stride).AsUInt16().StoreUnsafe(ref d, (nuint)(j * PredStride));
            }

            return;
        }

        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i += 8)
            {
                H264Samples.Load8(ref s, (j * stride) + i).AsUInt16().StoreUnsafe(ref d, (nuint)((j * PredStride) + i));
            }
        }
    }

    private static Vector128<short> Tap(Vector128<short> a, Vector128<short> b, Vector128<short> c, Vector128<short> d, Vector128<short> e, Vector128<short> f) =>
        ((c + d) * (short)20) - ((b + e) * (short)5) + (a + f);

    private static Vector256<short> Tap(Vector256<short> a, Vector256<short> b, Vector256<short> c, Vector256<short> d, Vector256<short> e, Vector256<short> f) =>
        ((c + d) * (short)20) - ((b + e) * (short)5) + (a + f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Load16<T>(ref T at, int offset)
        where T : unmanaged => H264Samples.Load16(ref at, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load<T>(ref T at, int offset)
        where T : unmanaged => H264Samples.Load8(ref at, offset);

    /// <summary>b: the horizontal half samples, rounded and clipped (8-8, 8-11, 8-18), eight at a time in 16-bit lanes.</summary>
    private static void HalfH<T>(T[] src, int at, int stride, int w, int h, ushort[] dst, int max)
        where T : unmanaged
    {
        ref T s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at - 2);
        ref ushort d = ref MemoryMarshal.GetArrayDataReference(dst);
        if (w == 16 && UseWideVectors)
        {
            Vector256<short> wideRound = Vector256.Create((short)16), wideTop = Vector256.Create((short)max);
            for (int j = 0; j < h; j++)
            {
                ref T s = ref Unsafe.Add(ref s0, j * stride);
                Vector256<short> t = Tap(Load16(ref s, 0), Load16(ref s, 1), Load16(ref s, 2), Load16(ref s, 3), Load16(ref s, 4), Load16(ref s, 5));
                Vector256.Min(Vector256.Max((t + wideRound) >> 5, Vector256<short>.Zero), wideTop).AsUInt16().StoreUnsafe(ref d, (nuint)(j * PredStride));
            }

            return;
        }

        Vector128<short> round = Vector128.Create((short)16), top = Vector128.Create((short)max);
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i += 8)
            {
                ref T s = ref Unsafe.Add(ref s0, (j * stride) + i);
                Vector128<short> t = Tap(Load(ref s, 0), Load(ref s, 1), Load(ref s, 2), Load(ref s, 3), Load(ref s, 4), Load(ref s, 5));
                Vector128<short> v = Vector128.Min(Vector128.Max((t + round) >> 5, Vector128<short>.Zero), top);
                v.AsUInt16().StoreUnsafe(ref d, (nuint)((j * PredStride) + i));
            }
        }
    }

    /// <summary>h: the vertical half samples, rounded and clipped, eight at a time in 16-bit lanes.</summary>
    private static void HalfV<T>(T[] src, int at, int stride, int w, int h, ushort[] dst, int max)
        where T : unmanaged
    {
        ref T s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at - (2 * stride));
        ref ushort d = ref MemoryMarshal.GetArrayDataReference(dst);
        if (w == 16 && UseWideVectors)
        {
            Vector256<short> wideRound = Vector256.Create((short)16), wideTop = Vector256.Create((short)max);
            Vector256<short> a = Load16(ref s0, 0), b = Load16(ref s0, stride), c = Load16(ref s0, 2 * stride), e = Load16(ref s0, 3 * stride),
                f = Load16(ref s0, 4 * stride);
            for (int j = 0; j < h; j++)
            {
                Vector256<short> g = Load16(ref s0, (j + 5) * stride);
                Vector256<short> t = Tap(a, b, c, e, f, g);
                Vector256.Min(Vector256.Max((t + wideRound) >> 5, Vector256<short>.Zero), wideTop).AsUInt16().StoreUnsafe(ref d, (nuint)(j * PredStride));
                (a, b, c, e, f) = (b, c, e, f, g);
            }

            return;
        }

        Vector128<short> round = Vector128.Create((short)16), top = Vector128.Create((short)max);
        for (int i = 0; i < w; i += 8)
        {
            ref T s = ref Unsafe.Add(ref s0, i);
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
    private void Center<T>(T[] src, int at, int stride, int w, int h, ushort[] dst, int max)
        where T : unmanaged
    {
        ref T s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at - (2 * stride) - 2);
        ref short m = ref MemoryMarshal.GetArrayDataReference(_mid);
        for (int j = 0; j < h + 5; j++)
        {
            for (int i = 0; i < w; i += 8)
            {
                ref T s = ref Unsafe.Add(ref s0, (j * stride) + i);
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
    private void LumaScalar<T>(T[] src, int at, int stride, int w, int h, int frac, ushort[] pred)
        where T : unmanaged
    {
        int max = _maxY;
        if (frac == 0)
        {
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    pred[(j * PredStride) + i] = (ushort)H264Samples.Get(src, at + (j * stride) + i);
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
                _half[(j * w) + i] = Tap(H264Samples.Get(src, s), H264Samples.Get(src, s + 1), H264Samples.Get(src, s + 2), H264Samples.Get(src, s + 3), H264Samples.Get(src, s + 4), H264Samples.Get(src, s + 5));
            }
        }

        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int g = H264Samples.Get(src, at + (j * stride) + i);
                int v = frac switch
                {
                    0x1 => (g + HalfV(src, at, stride, i, j, max) + 1) >> 1, // d
                    0x2 => HalfV(src, at, stride, i, j, max), // h
                    0x3 => (H264Samples.Get(src, at + ((j + 1) * stride) + i) + HalfV(src, at, stride, i, j, max) + 1) >> 1, // n
                    0x4 => (g + HalfH(i, j, w, max) + 1) >> 1, // a
                    0x5 => (HalfH(i, j, w, max) + HalfV(src, at, stride, i, j, max) + 1) >> 1, // e
                    0x6 => (HalfV(src, at, stride, i, j, max) + Center(i, j, w, max) + 1) >> 1, // i
                    0x7 => (HalfV(src, at, stride, i, j, max) + HalfH(i, j + 1, w, max) + 1) >> 1, // p
                    0x8 => HalfH(i, j, w, max), // b
                    0x9 => (HalfH(i, j, w, max) + Center(i, j, w, max) + 1) >> 1, // f
                    0xA => Center(i, j, w, max), // j
                    0xB => (Center(i, j, w, max) + HalfH(i, j + 1, w, max) + 1) >> 1, // q
                    0xC => (H264Samples.Get(src, at + (j * stride) + i + 1) + HalfH(i, j, w, max) + 1) >> 1, // c
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
    private static int HalfV<T>(T[] src, int at, int stride, int i, int j, int max)
        where T : unmanaged
    {
        int s = at + ((j - 2) * stride) + i;
        int h1 = Tap(H264Samples.Get(src, s), H264Samples.Get(src, s + stride), H264Samples.Get(src, s + (2 * stride)), H264Samples.Get(src, s + (3 * stride)), H264Samples.Get(src, s + (4 * stride)), H264Samples.Get(src, s + (5 * stride)));
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
    private void Chroma<T>(T[] plane, H264Picture refPic, int x, int y, int w, int h, int mv, ushort[] pred, int po)
        where T : unmanaged
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

        T[] src = plane;
        int stride = refPic.ChromaStride, at = refPic.ChromaOrigin + (yInt * stride) + xInt;
        int pad = H264Picture.Pad, padY = refPic.PadY;
        if ((uint)(xInt + pad) > (uint)(refPic.ChromaWidth + (2 * pad) - w - 1) || (uint)(yInt + padY) > (uint)(refPic.ChromaSpanY - h - 1))
        {
            FetchEdge(plane, refPic.ChromaOrigin, stride, refPic.ChromaWidth, refPic.ChromaHeight, xInt, yInt, Math.Min(w + 9, EdgeStride), h + 1);
            src = Edge<T>();
            stride = EdgeStride;
            at = 0;
        }

        int wa = (8 - xFrac) * (8 - yFrac), wb = xFrac * (8 - yFrac), wc = (8 - xFrac) * yFrac, wd = xFrac * yFrac;
        if (UseVectors && w >= 4)
        {
            Debug.Assert(at >= 0 && at + (h * stride) + 24 <= src.Length, "The border test keeps the block in the padded plane.");

            // The four weights add to 64, so a sum of samples up to 1023 stays below 65536 in an unsigned lane.
            ref T s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at);
            ref ushort d = ref MemoryMarshal.GetArrayDataReference(pred);
            Vector128<ushort> va = Vector128.Create((ushort)wa), vb = Vector128.Create((ushort)wb), vc = Vector128.Create((ushort)wc),
                vd = Vector128.Create((ushort)wd), round = Vector128.Create((ushort)32);
            for (int j = 0; j < h; j++)
            {
                ref T s = ref Unsafe.Add(ref s0, j * stride);
                Vector128<ushort> v = (va * H264Samples.Load8(ref s).AsUInt16()) + (vb * H264Samples.Load8(ref s, 1).AsUInt16())
                    + (vc * H264Samples.Load8(ref s, stride).AsUInt16()) + (vd * H264Samples.Load8(ref s, stride + 1).AsUInt16()) + round;
                (v >>> 6).StoreUnsafe(ref d, (nuint)((j * PredStride) + po));
            }

            return;
        }

        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int s = at + (j * stride) + i;
                pred[(j * PredStride) + i + po] = (ushort)(((wa * H264Samples.Get(src, s)) + (wb * H264Samples.Get(src, s + 1)) + (wc * H264Samples.Get(src, s + stride)) + (wd * H264Samples.Get(src, s + stride + 1)) + 32) >> 6);
            }
        }
    }

    /// <summary>
    /// The explicit weights of one list for 16 bytes, ((w p + 2^(logWD - 1)) &gt;&gt; logWD) + o clipped (8-270): each byte pairs
    /// with 1 and one multiply-add by (w, round) gives the rounded product, which fits 16 bits for 8-bit samples. Lanes 0 to 7
    /// take the first weight and offset, lanes 8 to 15 the second, so a Cb and a Cr row weigh together.
    /// </summary>
    private readonly struct ByteWeights
    {
        private readonly Vector256<sbyte> _pairs;
        private readonly Vector256<short> _offsets;
        private readonly int _shift;

        public ByteWeights(int weightLow, int weightHigh, int offsetLow, int offsetHigh, int logWD)
        {
            int round = logWD >= 1 ? 1 << (logWD - 1) : 0;
            _pairs = Vector256.Create(Vector128.Create((short)((round << 8) | (weightLow & 0xFF))), Vector128.Create((short)((round << 8) | (weightHigh & 0xFF)))).AsSByte();
            _offsets = Vector256.Create(Vector128.Create((short)offsetLow), Vector128.Create((short)offsetHigh));
            _shift = logWD;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<byte> Apply(Vector128<byte> v)
        {
            Vector128<byte> one = Vector128.Create((byte)1);
            Vector256<short> sums = Avx2.MultiplyAddAdjacent(Vector256.Create(Sse2.UnpackLow(v, one), Sse2.UnpackHigh(v, one)), _pairs);
            return PackBytes((sums >> _shift) + _offsets);
        }
    }
}
