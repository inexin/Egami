namespace Egami.Formats.Jxl;

/// <summary>
/// The lossy encoder's forward transforms (Phase 2.9.7): each varblock transform of ISO/IEC 18181-1 I.4 run backwards, so that
/// <see cref="JxlDct.Inverse"/> gives the pixels back. Coefficients come out in the decoder's layout: the varblock's area in
/// pixel order, the lowest frequencies at the top left. The DCTs up to 64×64 with every rectangle use <see cref="JxlDct.Dct2D"/>
/// forwards (vectorized where it is); the 8×8 special transforms (DCT2, DCT4, IDENTITY, DCT4x8 and AFV) undo the decoder's steps
/// one by one. <see cref="LfOf"/> gives the varblock's LF samples, which the decoder turns back into its lowest frequencies.
/// </summary>
internal static class JxlForwardDct
{
    /// <summary>Replaces the varblock's pixels at <paramref name="offset"/> with its coefficients.</summary>
    internal static void Forward(JxlTransformType t, Span<float> data, int offset, int stride)
    {
        switch (t)
        {
            case JxlTransformType.Dct2:
                Fdct2(data, offset, stride, 8);
                Fdct2(data, offset, stride, 4);
                Fdct2(data, offset, stride, 2);
                break;
            case JxlTransformType.Dct4:
                Dct4(data, offset, stride);
                break;
            case JxlTransformType.Hornuss:
                Hornuss(data, offset, stride);
                break;
            case JxlTransformType.Dct4x8:
                Dct4x8(data, offset, stride, false);
                break;
            case JxlTransformType.Dct8x4:
                Dct4x8(data, offset, stride, true);
                break;
            case JxlTransformType.Afv0:
            case JxlTransformType.Afv1:
            case JxlTransformType.Afv2:
            case JxlTransformType.Afv3:
                Afv(data, offset, stride, t - JxlTransformType.Afv0);
                break;
            default:
                (int w, int h) = JxlTransforms8.Size(t);
                JxlDct.Dct2D(data, offset, stride, w * 8, h * 8, forward: true);
                break;
        }
    }

    /// <summary>
    /// Writes the varblock's LF samples (one per 8×8 block, row by row at <paramref name="lfStride"/>) from its coefficients:
    /// the first coefficient for the 8×8 transforms, else the inverse of the decoder's LLF step (I.6: a DCT of the LF samples
    /// divided by the LLF scales).
    /// </summary>
    internal static void LfOf(JxlTransformType t, ReadOnlySpan<float> coeffs, int offset, int stride, Span<float> lf, int lfOffset, int lfStride)
    {
        if (JxlTransforms8.IsSmall(t))
        {
            lf[lfOffset] = coeffs[offset];
            return;
        }

        (int bw, int bh) = JxlTransforms8.Size(t);
        int logW = System.Numerics.BitOperations.Log2((uint)bw);
        int logH = System.Numerics.BitOperations.Log2((uint)bh);
        Span<float> llf = stackalloc float[bw * bh];
        for (int y = 0; y < bh; y++)
        {
            for (int x = 0; x < bw; x++)
            {
                llf[(y * bw) + x] = coeffs[offset + (y * stride) + x] * JxlDct.Scale(y, 5 - logH) * JxlDct.Scale(x, 5 - logW);
            }
        }

        JxlDct.Dct2D(llf, 0, bw, bw, bh, forward: false);
        for (int y = 0; y < bh; y++)
        {
            llf.Slice(y * bw, bw).CopyTo(lf.Slice(lfOffset + (y * lfStride), bw));
        }
    }

    // One level of the DCT2 pyramid: each 2×2 group of the size×size corner to its four Hadamard terms, spread to the corner's
    // quadrants. The decoder's step is the same Hadamard matrix without the quarter.
    private static void Fdct2(Span<float> d, int o, int s, int size)
    {
        int half = size / 2;
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < size; y++)
        {
            d.Slice(o + (y * s), size).CopyTo(scratch.Slice(y * size, size));
        }

        for (int y = 0; y < half; y++)
        {
            for (int x = 0; x < half; x++)
            {
                float p00 = scratch[(2 * y * size) + (2 * x)];
                float p01 = scratch[(2 * y * size) + (2 * x) + 1];
                float p10 = scratch[(((2 * y) + 1) * size) + (2 * x)];
                float p11 = scratch[(((2 * y) + 1) * size) + (2 * x) + 1];
                d[o + (y * s) + x] = (p00 + p01 + p10 + p11) * 0.25f;
                d[o + (y * s) + x + half] = (p00 + p01 - p10 - p11) * 0.25f;
                d[o + ((y + half) * s) + x] = (p00 - p01 + p10 - p11) * 0.25f;
                d[o + ((y + half) * s) + x + half] = (p00 - p01 - p10 + p11) * 0.25f;
            }
        }
    }

    private static void Dct4(Span<float> d, int o, int s)
    {
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Span<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        block[(iy * 4) + ix] = d[o + (((y * 4) + iy) * s) + (x * 4) + ix];
                    }
                }

                JxlDct.Dct2D(block, 0, 4, 4, 4, forward: true);
            }
        }

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                ReadOnlySpan<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        // The decoder reads each 4×4 transposed, as libjxl does.
                        d[o + ((y + (iy * 2)) * s) + x + (ix * 2)] = block[(ix * 4) + iy];
                    }
                }
            }
        }

        Fdct2(d, o, s, 2);
    }

    private static void Hornuss(Span<float> d, int o, int s)
    {
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Span<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        block[(iy * 4) + ix] = d[o + (((y * 4) + iy) * s) + (x * 4) + ix];
                    }
                }

                // The decoder adds one average to all 16, moving coefficient 5 to the first place and leaving the average at
                // place 5; its first coefficient is the average plus the mean of the other fifteen.
                float avg = block[5];
                float first = block[0];
                float residual = 0;
                for (int k = 1; k < 16; k++)
                {
                    block[k] -= avg;
                }

                block[5] = first - avg;
                for (int k = 1; k < 16; k++)
                {
                    residual += block[k];
                }

                block[0] = avg + (residual / 16);
            }
        }

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                ReadOnlySpan<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        d[o + ((y + (iy * 2)) * s) + x + (ix * 2)] = block[(iy * 4) + ix];
                    }
                }
            }
        }

        Fdct2(d, o, s, 2);
    }

    private static void Dct4x8(Span<float> d, int o, int s, bool transposed)
    {
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                scratch[(y * 8) + x] = transposed ? d[o + (x * s) + y] : d[o + (y * s) + x];
            }
        }

        for (int idx = 0; idx < 2; idx++)
        {
            Span<float> half = scratch.Slice(idx * 32, 32);
            JxlDct.Dct2D(half, 0, 8, 8, 4, forward: true);
            for (int iy = 0; iy < 4; iy++)
            {
                for (int ix = 0; ix < 8; ix++)
                {
                    d[o + (((iy * 2) + idx) * s) + ix] = half[(iy * 8) + ix];
                }
            }
        }

        float a = d[o];
        float b = d[o + s];
        d[o] = (a + b) * 0.5f;
        d[o + s] = (a - b) * 0.5f;
    }

    private static void Afv(Span<float> d, int o, int s, int n)
    {
        int flipX = n % 2;
        int flipY = n / 2;

        // The AFV corner's 16 samples, unflipped, projected on the orthonormal AFV basis.
        Span<float> samples = stackalloc float[16];
        for (int iy = 0; iy < 4; iy++)
        {
            int ay = flipY == 0 ? iy : 3 - iy;
            for (int ix = 0; ix < 4; ix++)
            {
                int ax = flipX == 0 ? ix : 3 - ix;
                samples[(ay * 4) + ax] = d[o + (((flipY * 4) + iy) * s) + (flipX * 4) + ix];
            }
        }

        Span<float> coeffs = stackalloc float[16];
        for (int k = 0; k < 16; k++)
        {
            float sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum = MathF.FusedMultiplyAdd(samples[i], JxlDct.AfvBasisValue((k * 16) + i), sum);
            }

            coeffs[k] = sum;
        }

        // The other 4×4 of the AFV rows, and the 8×4 of the other rows.
        Span<float> s4x4 = stackalloc float[16];
        Span<float> s4x8 = stackalloc float[32];
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                s4x4[(iy * 4) + ix] = d[o + (((flipY * 4) + iy) * s) + ((1 - flipX) * 4) + ix];
            }

            d.Slice(o + ((((1 - flipY) * 4) + iy) * s), 8).CopyTo(s4x8.Slice(iy * 8, 8));
        }

        JxlDct.Dct2D(s4x4, 0, 4, 4, 4, forward: true);
        JxlDct.Dct2D(s4x8, 0, 8, 8, 4, forward: true);
        for (int k = 1; k < 16; k++)
        {
            d[o + (2 * (k / 4) * s) + (2 * (k % 4))] = coeffs[k];
        }

        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                if ((ix | iy) != 0)
                {
                    d[o + (2 * iy * s) + (2 * ix) + 1] = s4x4[(ix * 4) + iy];
                }
            }

            for (int ix = 0; ix < 8; ix++)
            {
                if ((ix | iy) != 0)
                {
                    d[o + (((2 * iy) + 1) * s) + ix] = s4x8[(iy * 8) + ix];
                }
            }
        }

        // The decoder mixes the three DC terms from the first three coefficients: (c00 + c01 + c10) × 4 for the AFV basis, c00 −
        // c01 + c10 for the 4×4 and c00 − c10 for the 8×4.
        float afv = coeffs[0] / 4;
        float four = s4x4[0];
        float eight = s4x8[0];
        float c01 = (afv - four) / 2;
        float sum0010 = (afv + four) / 2;
        d[o] = (sum0010 + eight) / 2;
        d[o + 1] = c01;
        d[o + s] = (sum0010 - eight) / 2;
    }
}
