namespace Egami.Heic.Hevc;

/// <summary>
/// Inter prediction samples (H.265 8.5.3.3): fractional sample interpolation of luma (8-tap) and chroma (4-tap) from a reference
/// picture, reference samples outside it repeating its edge (8-228 to 8-231), then the default or explicit weighted prediction
/// of one or two lists into the current picture.
/// </summary>
internal sealed class HevcInter
{
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

    // The predicted samples of each list at 14-bit precision, a block of up to 64x64, and the rows the vertical pass filters.
    private readonly int[][] _pred = [new int[64 * 64], new int[64 * 64]];
    private readonly int[] _temp = new int[(64 + 7) * 64];

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
        for (int c = 0; c < picture.Planes.Length; c++)
        {
            int subW = c == 0 ? 1 : sps.SubWidthC, subH = c == 0 ? 1 : sps.SubHeightC;
            int x0 = xPb / subW, y0 = yPb / subH, w = nPbW / subW, h = nPbH / subH;
            int bitDepth = c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
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
                    Luma(reference, x0, y0, w, h, mvx, mvy, bitDepth, _pred[list]);
                }
                else
                {
                    // mvC (8-228, 8-229) in units of 1/8 chroma sample: 2 / SubWidthC and 2 / SubHeightC of the luma vector.
                    Chroma(reference, c, x0, y0, w, h, mvx * 2 / subW, mvy * 2 / subH, bitDepth, _pred[list]);
                }
            }

            ushort[] plane = picture.Planes[c];
            int stride = picture.Widths[c];
            if (weighted)
            {
                Explicit(plane, stride, x0, y0, w, h, predFlags, refIdx0, refIdx1, c, bitDepth, slice.Weights!);
            }
            else
            {
                Default(plane, stride, x0, y0, w, h, predFlags, bitDepth);
            }
        }
    }

    /// <summary>The luma sample interpolation (8.5.3.3.3.1) of a w by h block into <paramref name="dst"/>, 64 a row.</summary>
    private void Luma(HevcPicture reference, int x0, int y0, int w, int h, int mvx, int mvy, int bitDepth, int[] dst)
    {
        ushort[] src = reference.Planes[0];
        int width = reference.Widths[0], height = reference.Heights[0];
        int xFrac = mvx & 3, yFrac = mvy & 3, xInt = x0 + (mvx >> 2), yInt = y0 + (mvy >> 2);
        int shift1 = Math.Min(4, bitDepth - 8), shift3 = Math.Max(2, 14 - bitDepth);
        int[] fx = LumaFilter[xFrac], fy = LumaFilter[yFrac];
        if (xFrac == 0 && yFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                int row = Math.Clamp(yInt + y, 0, height - 1) * width;
                for (int x = 0; x < w; x++)
                {
                    dst[(y * 64) + x] = src[row + Math.Clamp(xInt + x, 0, width - 1)] << shift3;
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

                    dst[(y * 64) + x] = sum >> shift1;
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

                    dst[(y * 64) + x] = sum >> shift1;
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

                _temp[(y * 64) + x] = sum >> shift1;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int i = 0; i < 8; i++)
                {
                    sum += fy[i] * _temp[((y + i) * 64) + x];
                }

                dst[(y * 64) + x] = sum >> 6;
            }
        }
    }

    /// <summary>The chroma sample interpolation (8.5.3.3.3.2), with a vector in 1/8 chroma samples.</summary>
    private void Chroma(HevcPicture reference, int c, int x0, int y0, int w, int h, int mvx, int mvy, int bitDepth, int[] dst)
    {
        ushort[] src = reference.Planes[c];
        int width = reference.Widths[c], height = reference.Heights[c];
        int xFrac = mvx & 7, yFrac = mvy & 7, xInt = x0 + (mvx >> 3), yInt = y0 + (mvy >> 3);
        int shift1 = Math.Min(4, bitDepth - 8), shift3 = Math.Max(2, 14 - bitDepth);
        int[] fx = ChromaFilter[xFrac], fy = ChromaFilter[yFrac];
        if (xFrac == 0 && yFrac == 0)
        {
            for (int y = 0; y < h; y++)
            {
                int row = Math.Clamp(yInt + y, 0, height - 1) * width;
                for (int x = 0; x < w; x++)
                {
                    dst[(y * 64) + x] = src[row + Math.Clamp(xInt + x, 0, width - 1)] << shift3;
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

                    dst[(y * 64) + x] = sum >> shift1;
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

                    dst[(y * 64) + x] = sum >> shift1;
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

                _temp[(y * 64) + x] = sum >> shift1;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int i = 0; i < 4; i++)
                {
                    sum += fy[i] * _temp[((y + i) * 64) + x];
                }

                dst[(y * 64) + x] = sum >> 6;
            }
        }
    }

    /// <summary>The default weighted sample prediction (8.5.3.3.4.2): one list rounded down to the bit depth, or two averaged.</summary>
    private void Default(ushort[] plane, int stride, int x0, int y0, int w, int h, int predFlags, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        if (predFlags == 3)
        {
            int shift2 = 15 - bitDepth, offset2 = 1 << (shift2 - 1);
            int[] a = _pred[0], b = _pred[1];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp((a[(y * 64) + x] + b[(y * 64) + x] + offset2) >> shift2, 0, max);
                }
            }

            return;
        }

        int shift1 = 14 - bitDepth, offset1 = shift1 > 0 ? 1 << (shift1 - 1) : 0;
        int[] p = _pred[predFlags == 1 ? 0 : 1];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp((p[(y * 64) + x] + offset1) >> shift1, 0, max);
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

        if (predFlags == 3)
        {
            int[] a = _pred[0], b = _pred[1];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int v = ((a[(y * 64) + x] * w0) + (b[(y * 64) + x] * w1) + ((o0 + o1 + 1) << log2Wd)) >> (log2Wd + 1);
                    plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp(v, 0, max);
                }
            }

            return;
        }

        int[] p = _pred[predFlags == 1 ? 0 : 1];
        int weight = predFlags == 1 ? w0 : w1, offset = predFlags == 1 ? o0 : o1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = log2Wd >= 1 ? ((p[(y * 64) + x] * weight) + (1 << (log2Wd - 1)) >> log2Wd) + offset : (p[(y * 64) + x] * weight) + offset;
                plane[((y0 + y) * stride) + x0 + x] = (ushort)Math.Clamp(v, 0, max);
            }
        }
    }
}
