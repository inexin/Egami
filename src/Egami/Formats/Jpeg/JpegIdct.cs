namespace Egami.Formats.Jpeg;

/// <summary>
/// The accurate 8×8 inverse DCT in fixed point: the Loeffler-Ligtenberg-Moschytz factorization (ICASSP 1989), 12 multiplies per
/// 1D pass, with 13-bit constants and two extra bits kept between passes. Dequantization is folded into the first pass, and the
/// output is level-shifted by 128 and clamped to 0..255. This is the scaling libjpeg's accurate integer IDCT uses, so the result
/// matches djpeg's default decode.
/// </summary>
internal static class JpegIdct
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

    // The rotation constants of the factorization, cos and sin terms scaled by 2^13.
    private const int Fix0_298631336 = 2446;
    private const int Fix0_390180644 = 3196;
    private const int Fix0_541196100 = 4433;
    private const int Fix0_765366865 = 6270;
    private const int Fix0_899976223 = 7373;
    private const int Fix1_175875602 = 9633;
    private const int Fix1_501321110 = 12299;
    private const int Fix1_847759065 = 15137;
    private const int Fix1_961570560 = 16069;
    private const int Fix2_053119869 = 16819;
    private const int Fix2_562915447 = 20995;
    private const int Fix3_072711026 = 25172;

    /// <summary>
    /// Transforms one block. <paramref name="coefficients"/> and <paramref name="quant"/> are in natural (row-major) order;
    /// <paramref name="output"/> receives 8 rows of 8 samples, <paramref name="stride"/> bytes apart.
    /// </summary>
    internal static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> output, int stride)
    {
        Span<int> work = stackalloc int[64];
        coefficients = coefficients[..64];
        quant = quant[..64];

        // Pass 1: columns, dequantizing as each coefficient is read.
        for (int c = 0; c < 8; c++)
        {
            if (coefficients[8 + c] == 0 && coefficients[16 + c] == 0 && coefficients[24 + c] == 0 && coefficients[32 + c] == 0
                && coefficients[40 + c] == 0 && coefficients[48 + c] == 0 && coefficients[56 + c] == 0)
            {
                // A column with only its DC term is flat.
                int dc = coefficients[c] * quant[c] << Pass1Bits;
                for (int r = 0; r < 8; r++)
                {
                    work[(r * 8) + c] = dc;
                }

                continue;
            }

            Butterfly(
                coefficients[c] * quant[c], coefficients[8 + c] * quant[8 + c], coefficients[16 + c] * quant[16 + c], coefficients[24 + c] * quant[24 + c],
                coefficients[32 + c] * quant[32 + c], coefficients[40 + c] * quant[40 + c], coefficients[48 + c] * quant[48 + c], coefficients[56 + c] * quant[56 + c],
                out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7);
            int shift = ConstBits - Pass1Bits;
            work[c] = Descale(o0, shift);
            work[8 + c] = Descale(o1, shift);
            work[16 + c] = Descale(o2, shift);
            work[24 + c] = Descale(o3, shift);
            work[32 + c] = Descale(o4, shift);
            work[40 + c] = Descale(o5, shift);
            work[48 + c] = Descale(o6, shift);
            work[56 + c] = Descale(o7, shift);
        }

        // Pass 2: rows. The outputs carry 2 extra bits and a factor of 8 from the 2D transform's scaling.
        for (int r = 0; r < 8; r++)
        {
            Span<int> w = work.Slice(r * 8, 8);
            Span<byte> o = output.Slice(r * stride, 8);
            if (w[1] == 0 && w[2] == 0 && w[3] == 0 && w[4] == 0 && w[5] == 0 && w[6] == 0 && w[7] == 0)
            {
                byte flat = Clamp(Descale(w[0], Pass1Bits + 3));
                o.Fill(flat);
                continue;
            }

            // Adding 2^(Pass1Bits + 2) to the DC term here rounds the final shift, as the descale would.
            Butterfly(
                w[0] + (1 << (Pass1Bits + 2)), w[1], w[2], w[3], w[4], w[5], w[6], w[7],
                out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7);
            int shift = ConstBits + Pass1Bits + 3;
            o[0] = Clamp(o0 >> shift);
            o[1] = Clamp(o1 >> shift);
            o[2] = Clamp(o2 >> shift);
            o[3] = Clamp(o3 >> shift);
            o[4] = Clamp(o4 >> shift);
            o[5] = Clamp(o5 >> shift);
            o[6] = Clamp(o6 >> shift);
            o[7] = Clamp(o7 >> shift);
        }
    }

    /// <summary>One 1D pass: inputs in frequency order 0..7, outputs in sample order 0..7, scaled by 2^13.</summary>
    private static void Butterfly(
        int s0, int s1, int s2, int s3, int s4, int s5, int s6, int s7,
        out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7)
    {
        // Even part: the 4-point IDCT of inputs 0, 2, 4 and 6, with one rotation of 2 and 6.
        int z1 = (s2 + s6) * Fix0_541196100;
        int tmp2 = z1 + (s6 * -Fix1_847759065);
        int tmp3 = z1 + (s2 * Fix0_765366865);
        int tmp0 = (s0 + s4) << ConstBits;
        int tmp1 = (s0 - s4) << ConstBits;
        int tmp10 = tmp0 + tmp3;
        int tmp13 = tmp0 - tmp3;
        int tmp11 = tmp1 + tmp2;
        int tmp12 = tmp1 - tmp2;

        // Odd part: inputs 7, 5, 3 and 1, with the shared rotation by cos(3π/16).
        int a0 = s7;
        int a1 = s5;
        int a2 = s3;
        int a3 = s1;
        int b1 = a0 + a3;
        int b2 = a1 + a2;
        int b3 = a0 + a2;
        int b4 = a1 + a3;
        int b5 = (b3 + b4) * Fix1_175875602;
        a0 *= Fix0_298631336;
        a1 *= Fix2_053119869;
        a2 *= Fix3_072711026;
        a3 *= Fix1_501321110;
        b1 *= -Fix0_899976223;
        b2 *= -Fix2_562915447;
        b3 = (b3 * -Fix1_961570560) + b5;
        b4 = (b4 * -Fix0_390180644) + b5;
        a0 += b1 + b3;
        a1 += b2 + b4;
        a2 += b2 + b3;
        a3 += b1 + b4;

        o0 = tmp10 + a3;
        o7 = tmp10 - a3;
        o1 = tmp11 + a2;
        o6 = tmp11 - a2;
        o2 = tmp12 + a1;
        o5 = tmp12 - a1;
        o3 = tmp13 + a0;
        o4 = tmp13 - a0;
    }

    private static int Descale(int x, int n)
    {
        return (x + (1 << (n - 1))) >> n;
    }

    /// <summary>Level-shifts by 128 and clamps to a byte.</summary>
    private static byte Clamp(int value)
    {
        value += 128;
        return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }
}
