using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Heic.Hevc;

/// <summary>
/// Inter prediction samples (H.265 8.5.3.3): fractional sample interpolation of luma (8-tap) and chroma (4-tap) from a reference
/// picture, reference samples outside it repeating its edge (8-228 to 8-231), then the default or explicit weighted prediction
/// of one or two lists into the current picture.
/// </summary>
/// <remarks>
/// The predicted samples are kept as 16-bit values less <see cref="Bias"/>: at bit depths up to 12 the 14-bit predictions of
/// 8.5.3.3.3 lie between about -16,900 and 33,200, which does not fit a short, but less 8192 it does (the reference decoder
/// keeps them the same way). With AVX2 the filters run 16 or 8 samples at a time: pairs of taps multiply-add into 32-bit sums,
/// and blocks whose taps reach outside the reference picture first copy their samples, edges repeated, into a buffer.
/// </remarks>
internal sealed class HevcInter
{
    private const int Bias = 8192;
    private const int PredStride = 64;
    private const int EdgeStride = 80;

    // fL (Table 8-11) for xFrac 1 to 3 and fC (Table 8-12) for xFracC 1 to 7.
    private static readonly int[][] LumaFilter =
    [
        [0, 0, 0, 64, 0, 0, 0, 0],
        [-1, 4, -10, 58, 17, -5, 1, 0],
        [-1, 4, -11, 40, 40, -11, 4, -1],
        [0, 1, -5, 17, 58, -10, 4, -1],
    ];

    private static readonly int[][] ChromaFilter =
    [
        [0, 64, 0, 0],
        [-2, 58, 10, -2],
        [-4, 54, 16, -2],
        [-6, 46, 28, -4],
        [-4, 36, 36, -4],
        [-4, 28, 46, -6],
        [-2, 16, 54, -4],
        [-2, 10, 58, -2],
    ];

    // The predicted samples of each list (less Bias), a block of up to 64x64, the rows the vertical pass filters, and the
    // reference samples of a block that reaches outside its picture.
    private readonly short[][] _pred = [new short[PredStride * 64], new short[PredStride * 64]];
    private readonly short[] _temp = new short[PredStride * (64 + 7)];
    private readonly ushort[] _edge = new ushort[EdgeStride * (64 + 7)];

    /// <summary>Gets or sets whether the filters use AVX2 (tests turn it off to check the scalar code against it).</summary>
    internal static bool UseVectors { get; set; } = Avx2.IsSupported;

    /// <summary>
    /// Predicts one prediction block (8.5.3.3) at (xPb, yPb), nPbW by nPbH luma samples, from its motion (predFlags with
    /// predFlagL0 in bit 0 and predFlagL1 in bit 1, the reference indices and the packed vectors), into every component of the
    /// picture.
    /// </summary>
    internal void Predict(HevcPicture picture, HevcSliceRefs refs, HevcSliceHeader slice, int xPb, int yPb, int nPbW, int nPbH,
        int predFlags, int refIdx0, int refIdx1, int mv0, int mv1)
    {
        HevcSps sps = picture.Sps;
        bool weighted = slice.Weights is not null
            && (slice.SliceType == HevcSliceHeader.P ? picture.Pps.WeightedPred : picture.Pps.WeightedBipred);
        bool vectors = UseVectors;

        // One list without weights: the filters round their output to samples and write them into the picture themselves.
        bool direct = vectors && !weighted && predFlags != 3;
        for (int c = 0; c < picture.Planes.Length; c++)
        {
            int subW = c == 0 ? 1 : sps.SubWidthC, subH = c == 0 ? 1 : sps.SubHeightC;
            int x0 = xPb / subW, y0 = yPb / subH, w = nPbW / subW, h = nPbH / subH;
            int bitDepth = c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
            if (vectors && !weighted && predFlags != 3 && CopyWhole(picture, refs.Lists[predFlags - 1][predFlags == 1 ? refIdx0 : refIdx1].Picture, c, x0, y0, w, h,
                predFlags == 1 ? mv0 : mv1, subW, subH))
            {
                continue;
            }

            for (int list = 0; list < 2; list++)
            {
                if ((predFlags & (1 << list)) == 0)
                {
                    continue;
                }

                HevcPicture reference = refs.Lists[list][list == 0 ? refIdx0 : refIdx1].Picture;
                int mv = list == 0 ? mv0 : mv1;
                int mvx = (short)mv, mvy = mv >> 16;
                if (c == 0)
                {
                    if (direct)
                    {
                        Luma(reference, x0, y0, w, h, mvx, mvy, bitDepth, ref PlaneAt(picture, c, x0, y0), picture.Widths[c], new PlaneWriter(bitDepth));
                    }
                    else if (vectors)
                    {
                        Luma(reference, x0, y0, w, h, mvx, mvy, bitDepth, ref MemoryMarshal.GetArrayDataReference(_pred[list]), PredStride, default(PredWriter));
                    }
                    else
                    {
                        LumaScalar(reference, x0, y0, w, h, mvx, mvy, bitDepth, _pred[list]);
                    }
                }
                else
                {
                    // mvC (8-228, 8-229) in units of 1/8 chroma sample: 2 / SubWidthC and 2 / SubHeightC of the luma vector.
                    int mvcx = mvx * 2 / subW, mvcy = mvy * 2 / subH;
                    if (direct)
                    {
                        Chroma(reference, c, x0, y0, w, h, mvcx, mvcy, bitDepth, ref PlaneAt(picture, c, x0, y0), picture.Widths[c], new PlaneWriter(bitDepth));
                    }
                    else if (vectors)
                    {
                        Chroma(reference, c, x0, y0, w, h, mvcx, mvcy, bitDepth, ref MemoryMarshal.GetArrayDataReference(_pred[list]), PredStride,
                            default(PredWriter));
                    }
                    else
                    {
                        ChromaScalar(reference, c, x0, y0, w, h, mvcx, mvcy, bitDepth, _pred[list]);
                    }
                }
            }

            if (direct)
            {
                continue;
            }

            ushort[] plane = picture.Planes[c];
            int stride = picture.Widths[c];
            if (weighted)
            {
                Explicit(plane, stride, x0, y0, w, h, predFlags, refIdx0, refIdx1, c, bitDepth, slice.Weights!);
            }
            else if (vectors)
            {
                Default(plane, stride, x0, y0, w, h, predFlags, bitDepth);
            }
            else
            {
                DefaultScalar(plane, stride, x0, y0, w, h, predFlags, bitDepth);
            }
        }
    }

    // ---------------------------------------------------------------- AVX2

    /// <summary>
    /// One list without weights at a full-sample position inside the reference picture predicts the samples themselves (shifted
    /// up to 14 bits and back down): copies them, and gives true; else false.
    /// </summary>
    private static bool CopyWhole(HevcPicture picture, HevcPicture reference, int c, int x0, int y0, int w, int h, int mv, int subW, int subH)
    {
        int mvx = (short)mv, mvy = mv >> 16;
        int xFull = 4 * subW, yFull = 4 * subH;
        if ((mvx & (xFull - 1)) != 0 || (mvy & (yFull - 1)) != 0)
        {
            return false;
        }

        // A luma vector in quarter samples moves the chroma of 4:2:0 by an eighth: whole chroma samples need multiples of 8.
        int x = x0 + (mvx / xFull), y = y0 + (mvy / yFull);
        int width = reference.Widths[c], height = reference.Heights[c];
        if (x < 0 || y < 0 || x + w > width || y + h > height)
        {
            return false;
        }

        ushort[] src = reference.Planes[c], dst = picture.Planes[c];
        int stride = picture.Widths[c];
        for (int r = 0; r < h; r++)
        {
            src.AsSpan(((y + r) * width) + x, w).CopyTo(dst.AsSpan(((y0 + r) * stride) + x0, w));
        }

        return true;
    }


    /// <summary>The luma sample interpolation (8.5.3.3.3.1) of a w by h block into <paramref name="d"/>, rows <paramref name="dstStride"/> apart, through <paramref name="writer"/>.</summary>
    private void Luma<TWriter>(HevcPicture reference, int x0, int y0, int w, int h, int mvx, int mvy, int bitDepth, ref short d, int dstStride,
        TWriter writer)
        where TWriter : struct, IWriter
    {
        int xFrac = mvx & 3, yFrac = mvy & 3, xInt = x0 + (mvx >> 2), yInt = y0 + (mvy >> 2);
        int shift1 = bitDepth - 8;

        // The filters read whole groups of 8 samples: 7 columns more than that and 7 rows more than the block.
        ref short src = ref Source(reference, 0, xInt - 3, yInt - 3, ((w + 7) & ~7) + 7, h + 7, out int stride);
        if (xFrac == 0 && yFrac == 0)
        {
            Copy(ref Unsafe.Add(ref src, (3 * stride) + 3), stride, ref d, dstStride, w, h, 14 - bitDepth, writer);
        }
        else if (yFrac == 0)
        {
            Horizontal8(ref Unsafe.Add(ref src, 3 * stride), stride, ref d, dstStride, w, h, LumaFilter[xFrac], shift1, Bias, writer);
        }
        else if (xFrac == 0)
        {
            Vertical8(ref Unsafe.Add(ref src, 3), stride, ref d, dstStride, w, h, LumaFilter[yFrac], shift1, Bias, writer);
        }
        else
        {
            // Both fractions: h + 7 rows filtered horizontally (shift1), then vertically (shift2 = 6).
            ref short temp = ref MemoryMarshal.GetArrayDataReference(_temp);
            Horizontal8(ref src, stride, ref temp, PredStride, w, h + 7, LumaFilter[xFrac], shift1, 0, default(PredWriter));
            Vertical8(ref temp, PredStride, ref d, dstStride, w, h, LumaFilter[yFrac], 6, Bias, writer);
        }
    }

    /// <summary>The chroma sample interpolation (8.5.3.3.3.2), with a vector in 1/8 chroma samples.</summary>
    private void Chroma<TWriter>(HevcPicture reference, int c, int x0, int y0, int w, int h, int mvx, int mvy, int bitDepth, ref short d, int dstStride,
        TWriter writer)
        where TWriter : struct, IWriter
    {
        int xFrac = mvx & 7, yFrac = mvy & 7, xInt = x0 + (mvx >> 3), yInt = y0 + (mvy >> 3);
        int shift1 = bitDepth - 8;
        ref short src = ref Source(reference, c, xInt - 1, yInt - 1, ((w + 7) & ~7) + 3, h + 3, out int stride);
        if (xFrac == 0 && yFrac == 0)
        {
            Copy(ref Unsafe.Add(ref src, stride + 1), stride, ref d, dstStride, w, h, 14 - bitDepth, writer);
        }
        else if (yFrac == 0)
        {
            Horizontal4(ref Unsafe.Add(ref src, stride), stride, ref d, dstStride, w, h, ChromaFilter[xFrac], shift1, Bias, writer);
        }
        else if (xFrac == 0)
        {
            Vertical4(ref Unsafe.Add(ref src, 1), stride, ref d, dstStride, w, h, ChromaFilter[yFrac], shift1, Bias, writer);
        }
        else
        {
            ref short temp = ref MemoryMarshal.GetArrayDataReference(_temp);
            Horizontal4(ref src, stride, ref temp, PredStride, w, h + 3, ChromaFilter[xFrac], shift1, 0, default(PredWriter));
            Vertical4(ref temp, PredStride, ref d, dstStride, w, h, ChromaFilter[yFrac], 6, Bias, writer);
        }
    }

    /// <summary>
    /// Gets the reference samples from (x, y), cols by rows of them, as shorts: in the plane itself when they lie inside it,
    /// else copied into the edge buffer with the picture's edge samples repeated (8-228 to 8-231).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref short Source(HevcPicture reference, int c, int x, int y, int cols, int rows, out int stride)
    {
        ushort[] plane = reference.Planes[c];
        int width = reference.Widths[c], height = reference.Heights[c];
        if (x >= 0 && y >= 0 && x + cols <= width && y + rows <= height)
        {
            stride = width;
            return ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), (y * width) + x));
        }

        stride = EdgeStride;
        return ref Edge(plane, width, height, x, y, cols, rows);
    }

    /// <summary>Copies reference samples that reach outside the picture into the edge buffer, the edge samples repeated.</summary>
    private ref short Edge(ushort[] plane, int width, int height, int x, int y, int cols, int rows)
    {

        // Columns left of the picture repeat its first sample, columns right of it its last.
        int left = Math.Clamp(-x, 0, cols), right = Math.Clamp(x + cols - width, 0, cols - left), inside = cols - left - right;
        for (int r = 0; r < rows; r++)
        {
            int row = Math.Clamp(y + r, 0, height - 1) * width;
            Span<ushort> line = _edge.AsSpan(r * EdgeStride, cols);
            line[..left].Fill(plane[row]);
            if (inside > 0)
            {
                plane.AsSpan(row + x + left, inside).CopyTo(line[left..]);
            }
            line[(left + inside)..].Fill(plane[row + width - 1]);
        }

        return ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(_edge));
    }

    /// <summary>Full-sample positions: each sample shifted up to 14 bits, less <see cref="Bias"/>.</summary>
    private static void Copy<TWriter>(ref short src, int stride, ref short dst, int dstStride, int w, int h, int shift, TWriter writer)
        where TWriter : struct, IWriter
    {
        Vector256<short> bias = Vector256.Create((short)Bias);
        for (int y = 0; y < h; y++)
        {
            ref short s = ref Unsafe.Add(ref src, y * stride);
            ref short d = ref Unsafe.Add(ref dst, y * dstStride);
            int x = 0;
            for (; w - x >= 16; x += 16)
            {
                writer.Write(ref d, x, (Vector256.LoadUnsafe(ref s, (nuint)x) << shift) - bias);
            }

            for (; x < w; x += 8)
            {
                writer.Write(ref d, x, (Vector128.LoadUnsafe(ref s, (nuint)x) << shift) - bias.GetLower(), w - x);
            }
        }
    }

    /// <summary>A tap pair as the multiplier of <see cref="Avx2.MultiplyAddAdjacent(Vector256{short}, Vector256{short})"/>.</summary>
    private static Vector256<short> Pair(int first, int second) => Vector256.Create((first & 0xFFFF) | (second << 16)).AsInt16();

    /// <summary>
    /// The 8-tap filter along rows: each output sample from the 8 samples at x to x + 7 of <paramref name="src"/>, the sum shifted
    /// down by <paramref name="shift"/>, less <paramref name="bias"/>. Interleaving the loads at x + k and x + k + 1 pairs each
    /// output's taps k and k + 1; the 32-bit sums come out in the order the pack puts back.
    /// </summary>
    private static void Horizontal8<TWriter>(ref short src, int stride, ref short dst, int dstStride, int w, int h, int[] f, int shift, int bias,
        TWriter writer)
        where TWriter : struct, IWriter
    {
        Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]), c45 = Pair(f[4], f[5]), c67 = Pair(f[6], f[7]);
        Vector256<int> b = Vector256.Create(bias);
        for (int y = 0; y < h; y++)
        {
            ref short row = ref Unsafe.Add(ref src, y * stride);
            ref short d = ref Unsafe.Add(ref dst, y * dstStride);
            int x = 0;
            for (; w - x >= 16; x += 16)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector256<short> a0 = Vector256.LoadUnsafe(ref s), a1 = Vector256.LoadUnsafe(ref s, 1);
                Vector256<short> a2 = Vector256.LoadUnsafe(ref s, 2), a3 = Vector256.LoadUnsafe(ref s, 3);
                Vector256<short> a4 = Vector256.LoadUnsafe(ref s, 4), a5 = Vector256.LoadUnsafe(ref s, 5);
                Vector256<short> a6 = Vector256.LoadUnsafe(ref s, 6), a7 = Vector256.LoadUnsafe(ref s, 7);
                Vector256<int> lo = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a2, a3), c23)
                    + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a4, a5), c45) + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a6, a7), c67);
                Vector256<int> hi = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a2, a3), c23)
                    + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a4, a5), c45) + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a6, a7), c67);
                writer.Write(ref d, x, Avx2.PackSignedSaturate((lo >> shift) - b, (hi >> shift) - b));
            }

            for (; x < w; x += 8)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector128<short> a0 = Vector128.LoadUnsafe(ref s), a1 = Vector128.LoadUnsafe(ref s, 1);
                Vector128<short> a2 = Vector128.LoadUnsafe(ref s, 2), a3 = Vector128.LoadUnsafe(ref s, 3);
                Vector128<short> a4 = Vector128.LoadUnsafe(ref s, 4), a5 = Vector128.LoadUnsafe(ref s, 5);
                Vector128<short> a6 = Vector128.LoadUnsafe(ref s, 6), a7 = Vector128.LoadUnsafe(ref s, 7);
                Vector128<int> lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a2, a3), c23.GetLower())
                    + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a4, a5), c45.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a6, a7), c67.GetLower());
                Vector128<int> hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a2, a3), c23.GetLower())
                    + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a4, a5), c45.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a6, a7), c67.GetLower());
                writer.Write(ref d, x, Sse2.PackSignedSaturate((lo >> shift) - b.GetLower(), (hi >> shift) - b.GetLower()), w - x);
            }
        }
    }

    /// <summary>The 8-tap filter down columns: each output sample from the samples of rows y to y + 7, as <see cref="Horizontal8"/>.</summary>
    private static void Vertical8<TWriter>(ref short src, int stride, ref short dst, int dstStride, int w, int h, int[] f, int shift, int bias,
        TWriter writer)
        where TWriter : struct, IWriter
    {
        Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]), c45 = Pair(f[4], f[5]), c67 = Pair(f[6], f[7]);
        Vector256<int> b = Vector256.Create(bias);
        nuint s1 = (nuint)stride, s2 = 2 * s1, s3 = 3 * s1, s4 = 4 * s1, s5 = 5 * s1, s6 = 6 * s1, s7 = 7 * s1;
        for (int y = 0; y < h; y++)
        {
            ref short row = ref Unsafe.Add(ref src, y * stride);
            ref short d = ref Unsafe.Add(ref dst, y * dstStride);
            int x = 0;
            for (; w - x >= 16; x += 16)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector256<short> a0 = Vector256.LoadUnsafe(ref s), a1 = Vector256.LoadUnsafe(ref s, s1);
                Vector256<short> a2 = Vector256.LoadUnsafe(ref s, s2), a3 = Vector256.LoadUnsafe(ref s, s3);
                Vector256<short> a4 = Vector256.LoadUnsafe(ref s, s4), a5 = Vector256.LoadUnsafe(ref s, s5);
                Vector256<short> a6 = Vector256.LoadUnsafe(ref s, s6), a7 = Vector256.LoadUnsafe(ref s, s7);
                Vector256<int> lo = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a2, a3), c23)
                    + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a4, a5), c45) + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a6, a7), c67);
                Vector256<int> hi = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a2, a3), c23)
                    + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a4, a5), c45) + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a6, a7), c67);
                writer.Write(ref d, x, Avx2.PackSignedSaturate((lo >> shift) - b, (hi >> shift) - b));
            }

            for (; x < w; x += 8)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector128<short> a0 = Vector128.LoadUnsafe(ref s), a1 = Vector128.LoadUnsafe(ref s, s1);
                Vector128<short> a2 = Vector128.LoadUnsafe(ref s, s2), a3 = Vector128.LoadUnsafe(ref s, s3);
                Vector128<short> a4 = Vector128.LoadUnsafe(ref s, s4), a5 = Vector128.LoadUnsafe(ref s, s5);
                Vector128<short> a6 = Vector128.LoadUnsafe(ref s, s6), a7 = Vector128.LoadUnsafe(ref s, s7);
                Vector128<int> lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a2, a3), c23.GetLower())
                    + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a4, a5), c45.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a6, a7), c67.GetLower());
                Vector128<int> hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a2, a3), c23.GetLower())
                    + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a4, a5), c45.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a6, a7), c67.GetLower());
                writer.Write(ref d, x, Sse2.PackSignedSaturate((lo >> shift) - b.GetLower(), (hi >> shift) - b.GetLower()), w - x);
            }
        }
    }

    /// <summary>The 4-tap chroma filter along rows, as <see cref="Horizontal8"/>.</summary>
    private static void Horizontal4<TWriter>(ref short src, int stride, ref short dst, int dstStride, int w, int h, int[] f, int shift, int bias,
        TWriter writer)
        where TWriter : struct, IWriter
    {
        Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]);
        Vector256<int> b = Vector256.Create(bias);
        for (int y = 0; y < h; y++)
        {
            ref short row = ref Unsafe.Add(ref src, y * stride);
            ref short d = ref Unsafe.Add(ref dst, y * dstStride);
            int x = 0;
            for (; w - x >= 16; x += 16)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector256<short> a0 = Vector256.LoadUnsafe(ref s), a1 = Vector256.LoadUnsafe(ref s, 1);
                Vector256<short> a2 = Vector256.LoadUnsafe(ref s, 2), a3 = Vector256.LoadUnsafe(ref s, 3);
                Vector256<int> lo = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a2, a3), c23);
                Vector256<int> hi = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a2, a3), c23);
                writer.Write(ref d, x, Avx2.PackSignedSaturate((lo >> shift) - b, (hi >> shift) - b));
            }

            for (; x < w; x += 8)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector128<short> a0 = Vector128.LoadUnsafe(ref s), a1 = Vector128.LoadUnsafe(ref s, 1);
                Vector128<short> a2 = Vector128.LoadUnsafe(ref s, 2), a3 = Vector128.LoadUnsafe(ref s, 3);
                Vector128<int> lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a2, a3), c23.GetLower());
                Vector128<int> hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a2, a3), c23.GetLower());
                writer.Write(ref d, x, Sse2.PackSignedSaturate((lo >> shift) - b.GetLower(), (hi >> shift) - b.GetLower()), w - x);
            }
        }
    }

    /// <summary>The 4-tap chroma filter down columns, as <see cref="Horizontal8"/>.</summary>
    private static void Vertical4<TWriter>(ref short src, int stride, ref short dst, int dstStride, int w, int h, int[] f, int shift, int bias,
        TWriter writer)
        where TWriter : struct, IWriter
    {
        Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]);
        Vector256<int> b = Vector256.Create(bias);
        nuint s1 = (nuint)stride, s2 = 2 * s1, s3 = 3 * s1;
        for (int y = 0; y < h; y++)
        {
            ref short row = ref Unsafe.Add(ref src, y * stride);
            ref short d = ref Unsafe.Add(ref dst, y * dstStride);
            int x = 0;
            for (; w - x >= 16; x += 16)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector256<short> a0 = Vector256.LoadUnsafe(ref s), a1 = Vector256.LoadUnsafe(ref s, s1);
                Vector256<short> a2 = Vector256.LoadUnsafe(ref s, s2), a3 = Vector256.LoadUnsafe(ref s, s3);
                Vector256<int> lo = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a2, a3), c23);
                Vector256<int> hi = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a0, a1), c01) + Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a2, a3), c23);
                writer.Write(ref d, x, Avx2.PackSignedSaturate((lo >> shift) - b, (hi >> shift) - b));
            }

            for (; x < w; x += 8)
            {
                ref short s = ref Unsafe.Add(ref row, x);
                Vector128<short> a0 = Vector128.LoadUnsafe(ref s), a1 = Vector128.LoadUnsafe(ref s, s1);
                Vector128<short> a2 = Vector128.LoadUnsafe(ref s, s2), a3 = Vector128.LoadUnsafe(ref s, s3);
                Vector128<int> lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a2, a3), c23.GetLower());
                Vector128<int> hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a0, a1), c01.GetLower()) + Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a2, a3), c23.GetLower());
                writer.Write(ref d, x, Sse2.PackSignedSaturate((lo >> shift) - b.GetLower(), (hi >> shift) - b.GetLower()), w - x);
            }
        }
    }

    /// <summary>The default weighted sample prediction (8.5.3.3.4.2): one list rounded down to the bit depth, or two averaged.</summary>
    private void Default(ushort[] plane, int stride, int x0, int y0, int w, int h, int predFlags, int bitDepth)
    {
        Vector256<short> max = Vector256.Create((short)((1 << bitDepth) - 1));
        ref short out0 = ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), (y0 * stride) + x0));
        if (predFlags == 3)
        {
            // (a + b + offset2) >> shift2 in 32 bits: the multiply-add of the interleaved lists by ones sums each pair.
            int shift2 = 15 - bitDepth;
            Vector256<int> offset = Vector256.Create((2 * Bias) + (1 << (shift2 - 1)));
            Vector256<short> ones = Vector256.Create((short)1);
            ref short a = ref MemoryMarshal.GetArrayDataReference(_pred[0]);
            ref short b = ref MemoryMarshal.GetArrayDataReference(_pred[1]);
            for (int y = 0; y < h; y++)
            {
                ref short ra = ref Unsafe.Add(ref a, y * PredStride), rb = ref Unsafe.Add(ref b, y * PredStride);
                ref short d = ref Unsafe.Add(ref out0, y * stride);
                int x = 0;
                for (; w - x >= 16; x += 16)
                {
                    Vector256<short> va = Vector256.LoadUnsafe(ref ra, (nuint)x), vb = Vector256.LoadUnsafe(ref rb, (nuint)x);
                    Vector256<int> lo = (Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(va, vb), ones) + offset) >> shift2;
                    Vector256<int> hi = (Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(va, vb), ones) + offset) >> shift2;
                    Vector256.Min(Vector256.Max(Avx2.PackSignedSaturate(lo, hi), Vector256<short>.Zero), max).StoreUnsafe(ref d, (nuint)x);
                }

                for (; x < w; x += 8)
                {
                    Vector128<short> va = Vector128.LoadUnsafe(ref ra, (nuint)x), vb = Vector128.LoadUnsafe(ref rb, (nuint)x);
                    Vector128<int> lo = (Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(va, vb), ones.GetLower()) + offset.GetLower()) >> shift2;
                    Vector128<int> hi = (Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(va, vb), ones.GetLower()) + offset.GetLower()) >> shift2;
                    Store(ref Unsafe.Add(ref d, x), Vector128.Min(Vector128.Max(Sse2.PackSignedSaturate(lo, hi), Vector128<short>.Zero), max.GetLower()), w - x);
                }
            }

            return;
        }

        // (p + Bias + offset1) >> shift1, which is ((p + offset1) >> shift1) + (Bias >> shift1) as Bias is a multiple of 64.
        int shift1 = 14 - bitDepth;
        Vector256<short> round = Vector256.Create((short)(1 << (shift1 - 1))), bias = Vector256.Create((short)(Bias >> shift1));
        ref short p = ref MemoryMarshal.GetArrayDataReference(_pred[predFlags == 1 ? 0 : 1]);
        for (int y = 0; y < h; y++)
        {
            ref short rp = ref Unsafe.Add(ref p, y * PredStride);
            ref short d = ref Unsafe.Add(ref out0, y * stride);
            int x = 0;
            for (; w - x >= 16; x += 16)
            {
                Vector256<short> v = ((Vector256.LoadUnsafe(ref rp, (nuint)x) + round) >> shift1) + bias;
                Vector256.Min(Vector256.Max(v, Vector256<short>.Zero), max).StoreUnsafe(ref d, (nuint)x);
            }

            for (; x < w; x += 8)
            {
                Vector128<short> v = ((Vector128.LoadUnsafe(ref rp, (nuint)x) + round.GetLower()) >> shift1) + bias.GetLower();
                Store(ref Unsafe.Add(ref d, x), Vector128.Min(Vector128.Max(v, Vector128<short>.Zero), max.GetLower()), w - x);
            }
        }
    }

    /// <summary>Stores the first <paramref name="count"/> (2, 4, 6 or 8 or more) samples of a vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref short d, Vector128<short> v, int count)
    {
        if (count >= 8)
        {
            v.StoreUnsafe(ref d);
            return;
        }

        if ((count & 4) != 0)
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref d), v.AsUInt64().ToScalar());
            if ((count & 2) != 0)
            {
                Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref d, 4)), v.AsUInt32().GetElement(2));
            }

            return;
        }

        Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref d), v.AsUInt32().ToScalar());
    }

    /// <summary>The sample (x, y) of a plane of the picture, as a short.</summary>
    private static ref short PlaneAt(HevcPicture picture, int c, int x, int y) =>
        ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(picture.Planes[c]), (y * picture.Widths[c]) + x));

    /// <summary>Where the filters put their output: 16 samples, or the first <c>count</c> (2, 4, 6, 8 or more) of 8.</summary>
    private interface IWriter
    {
        void Write(ref short row, int x, Vector256<short> v);

        void Write(ref short row, int x, Vector128<short> v, int count);
    }

    /// <summary>Into the prediction buffers, whose rows have room for whole vectors.</summary>
    private readonly struct PredWriter : IWriter
    {
        public void Write(ref short row, int x, Vector256<short> v) => v.StoreUnsafe(ref row, (nuint)x);

        public void Write(ref short row, int x, Vector128<short> v, int count) => v.StoreUnsafe(ref row, (nuint)x);
    }

    /// <summary>
    /// Into the picture as samples: the default weighted prediction of one list (8-262), (v + Bias + offset1) &gt;&gt; shift1
    /// clipped, which is ((v + offset1) &gt;&gt; shift1) + (Bias &gt;&gt; shift1).
    /// </summary>
    private readonly struct PlaneWriter : IWriter
    {
        private readonly Vector256<short> _round, _bias, _max;
        private readonly int _shift;

        internal PlaneWriter(int bitDepth)
        {
            _shift = 14 - bitDepth;
            _round = Vector256.Create((short)(1 << (_shift - 1)));
            _bias = Vector256.Create((short)(Bias >> _shift));
            _max = Vector256.Create((short)((1 << bitDepth) - 1));
        }

        public void Write(ref short row, int x, Vector256<short> v) =>
            Vector256.Min(Vector256.Max(((v + _round) >> _shift) + _bias, Vector256<short>.Zero), _max).StoreUnsafe(ref row, (nuint)x);

        public void Write(ref short row, int x, Vector128<short> v, int count) =>
            Store(ref Unsafe.Add(ref row, x), Vector128.Min(Vector128.Max(((v + _round.GetLower()) >> _shift) + _bias.GetLower(), Vector128<short>.Zero), _max.GetLower()),
                count);
    }

    // ---------------------------------------------------------------- scalar

    private void LumaScalar(HevcPicture reference, int x0, int y0, int w, int h, int mvx, int mvy, int bitDepth, short[] dst)
    {
        ushort[] src = reference.Planes[0];
        int width = reference.Widths[0], height = reference.Heights[0];
        int xFrac = mvx & 3, yFrac = mvy & 3, xInt = x0 + (mvx >> 2), yInt = y0 + (mvy >> 2);
        int shift1 = bitDepth - 8, shift3 = 14 - bitDepth;
        int[] fx = LumaFilter[xFrac], fy = LumaFilter[yFrac];
        if (xFrac == 0 && yFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                int row = Math.Clamp(yInt + y, 0, height - 1) * width;
                for (int x = 0; x < w; x++)
                {
                    dst[(y * PredStride) + x] = (short)((src[row + Math.Clamp(xInt + x, 0, width - 1)] << shift3) - Bias);
                }
            }

            return;
        }

        if (yFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                int row = Math.Clamp(yInt + y, 0, height - 1) * width;
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        sum += fx[i] * src[row + Math.Clamp(xInt + x + i - 3, 0, width - 1)];
                    }

                    dst[(y * PredStride) + x] = (short)((sum >> shift1) - Bias);
                }
            }

            return;
        }

        if (xFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int column = Math.Clamp(xInt + x, 0, width - 1), sum = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        sum += fy[i] * src[(Math.Clamp(yInt + y + i - 3, 0, height - 1) * width) + column];
                    }

                    dst[(y * PredStride) + x] = (short)((sum >> shift1) - Bias);
                }
            }

            return;
        }

        // Both fractions: h + 7 rows filtered horizontally (shift1), then vertically (shift2 = 6).
        for (int y = 0; y < h + 7; y++)
        {
            int row = Math.Clamp(yInt + y - 3, 0, height - 1) * width;
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int i = 0; i < 8; i++)
                {
                    sum += fx[i] * src[row + Math.Clamp(xInt + x + i - 3, 0, width - 1)];
                }

                _temp[(y * PredStride) + x] = (short)(sum >> shift1);
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int i = 0; i < 8; i++)
                {
                    sum += fy[i] * _temp[((y + i) * PredStride) + x];
                }

                dst[(y * PredStride) + x] = (short)((sum >> 6) - Bias);
            }
        }
    }

    private void ChromaScalar(HevcPicture reference, int c, int x0, int y0, int w, int h, int mvx, int mvy, int bitDepth, short[] dst)
    {
        ushort[] src = reference.Planes[c];
        int width = reference.Widths[c], height = reference.Heights[c];
        int xFrac = mvx & 7, yFrac = mvy & 7, xInt = x0 + (mvx >> 3), yInt = y0 + (mvy >> 3);
        int shift1 = bitDepth - 8, shift3 = 14 - bitDepth;
        int[] fx = ChromaFilter[xFrac], fy = ChromaFilter[yFrac];
        if (xFrac == 0 && yFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                int row = Math.Clamp(yInt + y, 0, height - 1) * width;
                for (int x = 0; x < w; x++)
                {
                    dst[(y * PredStride) + x] = (short)((src[row + Math.Clamp(xInt + x, 0, width - 1)] << shift3) - Bias);
                }
            }

            return;
        }

        if (yFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                int row = Math.Clamp(yInt + y, 0, height - 1) * width;
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        sum += fx[i] * src[row + Math.Clamp(xInt + x + i - 1, 0, width - 1)];
                    }

                    dst[(y * PredStride) + x] = (short)((sum >> shift1) - Bias);
                }
            }

            return;
        }

        if (xFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int column = Math.Clamp(xInt + x, 0, width - 1), sum = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        sum += fy[i] * src[(Math.Clamp(yInt + y + i - 1, 0, height - 1) * width) + column];
                    }

                    dst[(y * PredStride) + x] = (short)((sum >> shift1) - Bias);
                }
            }

            return;
        }

        for (int y = 0; y < h + 3; y++)
        {
            int row = Math.Clamp(yInt + y - 1, 0, height - 1) * width;
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int i = 0; i < 4; i++)
                {
                    sum += fx[i] * src[row + Math.Clamp(xInt + x + i - 1, 0, width - 1)];
                }

                _temp[(y * PredStride) + x] = (short)(sum >> shift1);
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int i = 0; i < 4; i++)
                {
                    sum += fy[i] * _temp[((y + i) * PredStride) + x];
                }

                dst[(y * PredStride) + x] = (short)((sum >> 6) - Bias);
            }
        }
    }

    private void DefaultScalar(ushort[] plane, int stride, int x0, int y0, int w, int h, int predFlags, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        if (predFlags == 3)
        {
            int shift2 = 15 - bitDepth, offset2 = (1 << (shift2 - 1)) + (2 * Bias);
            short[] a = _pred[0], b = _pred[1];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp((a[(y * PredStride) + x] + b[(y * PredStride) + x] + offset2) >> shift2, 0, max);
                }
            }

            return;
        }

        int shift1 = 14 - bitDepth, offset1 = (1 << (shift1 - 1)) + Bias;
        short[] p = _pred[predFlags == 1 ? 0 : 1];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp((p[(y * PredStride) + x] + offset1) >> shift1, 0, max);
            }
        }
    }

    /// <summary>The explicit weighted sample prediction (8.5.3.3.4.3) with the slice's weights and offsets.</summary>
    private void Explicit(ushort[] plane, int stride, int x0, int y0, int w, int h, int predFlags, int refIdx0, int refIdx1, int c, int bitDepth,
        HevcPredWeights weights)
    {
        int max = (1 << bitDepth) - 1, shift1 = 14 - bitDepth;
        int log2Wd = (c == 0 ? weights.LumaLog2Denom : weights.ChromaLog2Denom) + shift1;
        int o = c * 2;
        int w0 = 0, o0 = 0, w1 = 0, o1 = 0;
        if ((predFlags & 1) != 0)
        {
            w0 = weights.Values[0][(refIdx0 * 6) + o];
            o0 = weights.Values[0][(refIdx0 * 6) + o + 1];
        }

        if ((predFlags & 2) != 0)
        {
            w1 = weights.Values[1][(refIdx1 * 6) + o];
            o1 = weights.Values[1][(refIdx1 * 6) + o + 1];
        }

        if (UseVectors)
        {
            // Both cases as (a w0 + b w1 + add) >> shift, one list's with its other weight 0 and its offset in add.
            ref short out0 = ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), (y0 * stride) + x0));
            if (predFlags == 3)
            {
                Weight(_pred[0], _pred[1], w0, w1, ((o0 + o1 + 1) << log2Wd) + (Bias * (w0 + w1)), log2Wd + 1, max, ref out0, stride, w, h);
            }
            else
            {
                int weight1 = predFlags == 1 ? w0 : w1, offset1 = predFlags == 1 ? o0 : o1;
                short[] single = _pred[predFlags == 1 ? 0 : 1];
                Weight(single, single, weight1, 0, (1 << (log2Wd - 1)) + (Bias * weight1) + (offset1 << log2Wd), log2Wd, max, ref out0, stride, w, h);
            }

            return;
        }

        if (predFlags == 3)
        {
            short[] a = _pred[0], b = _pred[1];
            int add = ((o0 + o1 + 1) << log2Wd) + (Bias * (w0 + w1));
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int v = ((a[(y * PredStride) + x] * w0) + (b[(y * PredStride) + x] * w1) + add) >> (log2Wd + 1);
                    plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp(v, 0, max);
                }
            }

            return;
        }

        short[] p = _pred[predFlags == 1 ? 0 : 1];
        int weight = predFlags == 1 ? w0 : w1, offset = predFlags == 1 ? o0 : o1;
        int round = (log2Wd >= 1 ? 1 << (log2Wd - 1) : 0) + (Bias * weight);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = (((p[(y * PredStride) + x] * weight) + round) >> log2Wd) + offset;
                plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp(v, 0, max);
            }
        }
    }

    /// <summary>Explicit weighting with AVX2: (a w0 + b w1 + add) &gt;&gt; shift, clipped, a multiply-add of the interleaved lists.</summary>
    private static void Weight(short[] a, short[] b, int w0, int w1, int add, int shift, int max, ref short out0, int stride, int w, int h)
    {
        Vector256<short> weights = Pair(w0, w1), vmax = Vector256.Create((short)max);
        Vector256<int> vadd = Vector256.Create(add);
        ref short a0 = ref MemoryMarshal.GetArrayDataReference(a), b0 = ref MemoryMarshal.GetArrayDataReference(b);
        for (int y = 0; y < h; y++)
        {
            ref short ra = ref Unsafe.Add(ref a0, y * PredStride), rb = ref Unsafe.Add(ref b0, y * PredStride);
            ref short d = ref Unsafe.Add(ref out0, y * stride);
            int x = 0;
            for (; w - x >= 16; x += 16)
            {
                Vector256<short> va = Vector256.LoadUnsafe(ref ra, (nuint)x), vb = Vector256.LoadUnsafe(ref rb, (nuint)x);
                Vector256<int> lo = (Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(va, vb), weights) + vadd) >> shift;
                Vector256<int> hi = (Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(va, vb), weights) + vadd) >> shift;
                Vector256.Min(Vector256.Max(Avx2.PackSignedSaturate(lo, hi), Vector256<short>.Zero), vmax).StoreUnsafe(ref d, (nuint)x);
            }

            for (; x < w; x += 8)
            {
                Vector128<short> va = Vector128.LoadUnsafe(ref ra, (nuint)x), vb = Vector128.LoadUnsafe(ref rb, (nuint)x);
                Vector128<int> lo = (Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(va, vb), weights.GetLower()) + vadd.GetLower()) >> shift;
                Vector128<int> hi = (Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(va, vb), weights.GetLower()) + vadd.GetLower()) >> shift;
                Store(ref Unsafe.Add(ref d, x), Vector128.Min(Vector128.Max(Sse2.PackSignedSaturate(lo, hi), Vector128<short>.Zero), vmax.GetLower()), w - x);
            }
        }
    }
}
