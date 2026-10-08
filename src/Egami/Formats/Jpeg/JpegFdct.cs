namespace Egami.Formats.Jpeg;

/// <summary>
/// The accurate 8×8 forward DCT in fixed point: the Loeffler-Ligtenberg-Moschytz factorization (ICASSP 1989) run the other way,
/// with 13-bit constants and two extra bits kept between the passes, and quantization with rounding to nearest. The output is
/// scaled by 8, which the quantizer divides out; this is the scaling libjpeg's accurate integer forward DCT uses, so files match
/// <c>cjpeg</c>'s default.
/// </summary>
internal static class JpegFdct
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

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
    /// Transforms 8 rows of 8 samples (<paramref name="stride"/> bytes apart) and quantizes them with <paramref name="divisors"/>
    /// (the quantization table times 8, natural order) into <paramref name="output"/> in zigzag order.
    /// </summary>
    internal static void Forward(ReadOnlySpan<byte> samples, int stride, ReadOnlySpan<int> divisors, Span<short> output)
    {
        Span<int> work = stackalloc int[64];

        // Pass 1: rows, level-shifted to -128..127, scaled up by 2^Pass1Bits.
        for (int r = 0; r < 8; r++)
        {
            ReadOnlySpan<byte> s = samples.Slice(r * stride, 8);
            Butterfly(
                s[0] - 128, s[1] - 128, s[2] - 128, s[3] - 128, s[4] - 128, s[5] - 128, s[6] - 128, s[7] - 128,
                out int even0, out int even4, out int o2, out int o6, out int o1, out int o3, out int o5, out int o7);
            Span<int> w = work.Slice(r * 8, 8);
            w[0] = even0 << Pass1Bits;
            w[4] = even4 << Pass1Bits;
            int shift = ConstBits - Pass1Bits;
            w[2] = Descale(o2, shift);
            w[6] = Descale(o6, shift);
            w[1] = Descale(o1, shift);
            w[3] = Descale(o3, shift);
            w[5] = Descale(o5, shift);
            w[7] = Descale(o7, shift);
        }

        // Pass 2: columns, removing the extra bits; the result is 8 times the true DCT.
        for (int c = 0; c < 8; c++)
        {
            Butterfly(
                work[c], work[8 + c], work[16 + c], work[24 + c], work[32 + c], work[40 + c], work[48 + c], work[56 + c],
                out int even0, out int even4, out int o2, out int o6, out int o1, out int o3, out int o5, out int o7);
            int shift = ConstBits + Pass1Bits;
            work[c] = Descale(even0, Pass1Bits);
            work[32 + c] = Descale(even4, Pass1Bits);
            work[16 + c] = Descale(o2, shift);
            work[48 + c] = Descale(o6, shift);
            work[8 + c] = Descale(o1, shift);
            work[24 + c] = Descale(o3, shift);
            work[40 + c] = Descale(o5, shift);
            work[56 + c] = Descale(o7, shift);
        }

        ReadOnlySpan<byte> zigzag = JpegTables.ZigZag;
        for (int k = 0; k < 64; k++)
        {
            int n = zigzag[k];
            output[k] = (short)Quantize(work[n], divisors[n]);
        }
    }

    /// <summary>Divides with rounding to nearest, halves away from zero.</summary>
    internal static int Quantize(int value, int divisor)
    {
        int half = divisor >> 1;
        return value < 0 ? -((half - value) / divisor) : (value + half) / divisor;
    }

    /// <summary>
    /// One 1D pass. The even outputs 0 and 4 are plain sums; outputs 2 and 6 and the odd ones are scaled by 2^13 and need
    /// descaling.
    /// </summary>
    private static void Butterfly(
        int d0, int d1, int d2, int d3, int d4, int d5, int d6, int d7,
        out int even0, out int even4, out int o2, out int o6, out int o1, out int o3, out int o5, out int o7)
    {
        int tmp0 = d0 + d7;
        int tmp7 = d0 - d7;
        int tmp1 = d1 + d6;
        int tmp6 = d1 - d6;
        int tmp2 = d2 + d5;
        int tmp5 = d2 - d5;
        int tmp3 = d3 + d4;
        int tmp4 = d3 - d4;

        // Even part: the 4-point DCT of the sums, with one rotation for outputs 2 and 6.
        int tmp10 = tmp0 + tmp3;
        int tmp13 = tmp0 - tmp3;
        int tmp11 = tmp1 + tmp2;
        int tmp12 = tmp1 - tmp2;
        even0 = tmp10 + tmp11;
        even4 = tmp10 - tmp11;
        int z1 = (tmp12 + tmp13) * Fix0_541196100;
        o2 = z1 + (tmp13 * Fix0_765366865);
        o6 = z1 + (tmp12 * -Fix1_847759065);

        // Odd part: the differences, with the shared rotation by cos(3π/16).
        int a1 = tmp4 + tmp7;
        int a2 = tmp5 + tmp6;
        int a3 = tmp4 + tmp6;
        int a4 = tmp5 + tmp7;
        int a5 = (a3 + a4) * Fix1_175875602;
        tmp4 *= Fix0_298631336;
        tmp5 *= Fix2_053119869;
        tmp6 *= Fix3_072711026;
        tmp7 *= Fix1_501321110;
        a1 *= -Fix0_899976223;
        a2 *= -Fix2_562915447;
        a3 = (a3 * -Fix1_961570560) + a5;
        a4 = (a4 * -Fix0_390180644) + a5;
        o7 = tmp4 + a1 + a3;
        o5 = tmp5 + a2 + a4;
        o3 = tmp6 + a2 + a3;
        o1 = tmp7 + a1 + a4;
    }

    private static int Descale(int x, int n)
    {
        return (x + (1 << (n - 1))) >> n;
    }
}
