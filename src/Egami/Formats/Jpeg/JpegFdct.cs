using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    /// (the quantization table times 8, natural order) into <paramref name="output"/>, in natural order too.
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

        output[0] = (short)Quantize(work[0], divisors[0]);
        for (int n = 1; n < 64; n++)
        {
            // Baseline AC tables code at most 10 bits of magnitude.
            output[n] = (short)Math.Clamp(Quantize(work[n], divisors[n]), -1023, 1023);
        }
    }

    /// <summary>
    /// <see cref="Forward"/> with AVX2: both passes on eight rows or columns at a time in 32-bit lanes, the same integer steps as the
    /// scalar code, and quantization by a float reciprocal corrected by one step to the exact quotient. The output is identical.
    /// </summary>
    [SkipLocalsInit]
    internal static void ForwardAvx2(ReadOnlySpan<byte> samples, int stride, ReadOnlySpan<int> divisors, ReadOnlySpan<float> reciprocals, Span<short> output)
    {
        _ = samples.Slice(7 * stride, 8);
        divisors = divisors[..64];
        reciprocals = reciprocals[..64];
        output = output[..64];
        ref byte s = ref MemoryMarshal.GetReference(samples);
        Vector256<int> center = Vector256.Create(128);
        Vector256<int> r0 = Row(ref s, 0) - center;
        Vector256<int> r1 = Row(ref s, stride) - center;
        Vector256<int> r2 = Row(ref s, 2 * stride) - center;
        Vector256<int> r3 = Row(ref s, 3 * stride) - center;
        Vector256<int> r4 = Row(ref s, 4 * stride) - center;
        Vector256<int> r5 = Row(ref s, 5 * stride) - center;
        Vector256<int> r6 = Row(ref s, 6 * stride) - center;
        Vector256<int> r7 = Row(ref s, 7 * stride) - center;

        // Pass 1 on the transpose, so each lane is one row: vector k holds sample k of every row.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Butterfly(r0, r1, r2, r3, r4, r5, r6, r7, out Vector256<int> e0, out Vector256<int> e4, out Vector256<int> o2, out Vector256<int> o6, out Vector256<int> o1, out Vector256<int> o3, out Vector256<int> o5, out Vector256<int> o7);
        const int Shift1 = ConstBits - Pass1Bits;
        Vector256<int> round1 = Vector256.Create(1 << (Shift1 - 1));
        r0 = e0 << Pass1Bits;
        r4 = e4 << Pass1Bits;
        r2 = (o2 + round1) >> Shift1;
        r6 = (o6 + round1) >> Shift1;
        r1 = (o1 + round1) >> Shift1;
        r3 = (o3 + round1) >> Shift1;
        r5 = (o5 + round1) >> Shift1;
        r7 = (o7 + round1) >> Shift1;

        // Pass 2 on the transpose back: vector u holds frequency u of every row, lanes the rows; outputs are rows of frequencies.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Butterfly(r0, r1, r2, r3, r4, r5, r6, r7, out e0, out e4, out o2, out o6, out o1, out o3, out o5, out o7);
        const int Shift2 = ConstBits + Pass1Bits;
        Vector256<int> round2 = Vector256.Create(1 << (Shift2 - 1));
        Vector256<int> roundDc = Vector256.Create(1 << (Pass1Bits - 1));
        Quantize((e0 + roundDc) >> Pass1Bits, divisors, reciprocals, output, 0);
        Quantize((o1 + round2) >> Shift2, divisors, reciprocals, output, 8);
        Quantize((o2 + round2) >> Shift2, divisors, reciprocals, output, 16);
        Quantize((o3 + round2) >> Shift2, divisors, reciprocals, output, 24);
        Quantize((e4 + roundDc) >> Pass1Bits, divisors, reciprocals, output, 32);
        Quantize((o5 + round2) >> Shift2, divisors, reciprocals, output, 40);
        Quantize((o6 + round2) >> Shift2, divisors, reciprocals, output, 48);
        Quantize((o7 + round2) >> Shift2, divisors, reciprocals, output, 56);
    }

    /// <summary>Runs the vector quantization on one value, so tests can check it against <see cref="Quantize(int, int)"/>.</summary>
    internal static int QuantizeVectorForTest(int value, int divisor)
    {
        int[] divisors = Enumerable.Repeat(divisor, 8).ToArray();
        Span<short> output = stackalloc short[8];
        Quantize(Vector256.Create(value), divisors, Reciprocals([.. Enumerable.Repeat(divisor, 64)]).AsSpan(0, 8), output, 0);
        return output[0];
    }

    /// <summary>Returns the reciprocal of each divisor, for <see cref="ForwardAvx2"/>.</summary>
    internal static float[] Reciprocals(ReadOnlySpan<int> divisors)
    {
        float[] reciprocals = new float[64];
        for (int i = 0; i < 64; i++)
        {
            reciprocals[i] = 1f / divisors[i];
        }

        return reciprocals;
    }

    /// <summary>
    /// Quantizes eight coefficients as <see cref="Quantize(int, int)"/> does: the float estimate of the quotient is off by at most
    /// one for these ranges, and the remainder says which way.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Quantize(Vector256<int> values, ReadOnlySpan<int> divisors, ReadOnlySpan<float> reciprocals, Span<short> output, int at)
    {
        Vector256<int> divisor = Vector256.Create(divisors.Slice(at, 8));
        Vector256<int> magnitude = Vector256.Abs(values) + (divisor >> 1);
        Vector256<int> quotient = Vector256.ConvertToInt32(Vector256.ConvertToSingle(magnitude) * Vector256.Create(reciprocals.Slice(at, 8)));
        Vector256<int> remainder = magnitude - (quotient * divisor);

        // Comparisons give -1 for true: subtracting adds one when the remainder is too big, adding takes one off when negative.
        quotient = quotient - Vector256.GreaterThanOrEqual(remainder, divisor) + Vector256.LessThan(remainder, Vector256<int>.Zero);
        Vector256<int> negative = Vector256.LessThan(values, Vector256<int>.Zero);
        quotient = (quotient ^ negative) - negative;

        // Baseline AC tables code at most 10 bits of magnitude; the DC (lane 0 of the first row) is not limited.
        Vector256<int> clamped = Vector256.Min(Vector256.Max(quotient, Vector256.Create(-1023)), Vector256.Create(1023));
        quotient = at == 0 ? Vector256.ConditionalSelect(Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0), quotient, clamped) : clamped;
        Vector256<short> packed = Avx2.PackSignedSaturate(quotient, quotient);
        Vector128.Create(packed.GetLower().GetLower(), packed.GetUpper().GetLower()).CopyTo(output.Slice(at, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Row(ref byte samples, int offset)
    {
        return Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, offset))).AsByte());
    }

    /// <summary>Transposes an 8×8 block of 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3,
        ref Vector256<int> r4, ref Vector256<int> r5, ref Vector256<int> r6, ref Vector256<int> r7)
    {
        Vector256<long> t0 = Avx2.UnpackLow(r0, r1).AsInt64();
        Vector256<long> t1 = Avx2.UnpackHigh(r0, r1).AsInt64();
        Vector256<long> t2 = Avx2.UnpackLow(r2, r3).AsInt64();
        Vector256<long> t3 = Avx2.UnpackHigh(r2, r3).AsInt64();
        Vector256<long> t4 = Avx2.UnpackLow(r4, r5).AsInt64();
        Vector256<long> t5 = Avx2.UnpackHigh(r4, r5).AsInt64();
        Vector256<long> t6 = Avx2.UnpackLow(r6, r7).AsInt64();
        Vector256<long> t7 = Avx2.UnpackHigh(r6, r7).AsInt64();
        Vector256<int> u0 = Avx2.UnpackLow(t0, t2).AsInt32();
        Vector256<int> u1 = Avx2.UnpackHigh(t0, t2).AsInt32();
        Vector256<int> u2 = Avx2.UnpackLow(t1, t3).AsInt32();
        Vector256<int> u3 = Avx2.UnpackHigh(t1, t3).AsInt32();
        Vector256<int> u4 = Avx2.UnpackLow(t4, t6).AsInt32();
        Vector256<int> u5 = Avx2.UnpackHigh(t4, t6).AsInt32();
        Vector256<int> u6 = Avx2.UnpackLow(t5, t7).AsInt32();
        Vector256<int> u7 = Avx2.UnpackHigh(t5, t7).AsInt32();
        r0 = Avx2.Permute2x128(u0, u4, 0x20);
        r1 = Avx2.Permute2x128(u1, u5, 0x20);
        r2 = Avx2.Permute2x128(u2, u6, 0x20);
        r3 = Avx2.Permute2x128(u3, u7, 0x20);
        r4 = Avx2.Permute2x128(u0, u4, 0x31);
        r5 = Avx2.Permute2x128(u1, u5, 0x31);
        r6 = Avx2.Permute2x128(u2, u6, 0x31);
        r7 = Avx2.Permute2x128(u3, u7, 0x31);
    }

    /// <summary>The scalar 1D pass on eight lanes, operation for operation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(
        Vector256<int> d0, Vector256<int> d1, Vector256<int> d2, Vector256<int> d3, Vector256<int> d4, Vector256<int> d5, Vector256<int> d6, Vector256<int> d7,
        out Vector256<int> even0, out Vector256<int> even4, out Vector256<int> o2, out Vector256<int> o6, out Vector256<int> o1, out Vector256<int> o3, out Vector256<int> o5, out Vector256<int> o7)
    {
        Vector256<int> tmp0 = d0 + d7;
        Vector256<int> tmp7 = d0 - d7;
        Vector256<int> tmp1 = d1 + d6;
        Vector256<int> tmp6 = d1 - d6;
        Vector256<int> tmp2 = d2 + d5;
        Vector256<int> tmp5 = d2 - d5;
        Vector256<int> tmp3 = d3 + d4;
        Vector256<int> tmp4 = d3 - d4;

        Vector256<int> tmp10 = tmp0 + tmp3;
        Vector256<int> tmp13 = tmp0 - tmp3;
        Vector256<int> tmp11 = tmp1 + tmp2;
        Vector256<int> tmp12 = tmp1 - tmp2;
        even0 = tmp10 + tmp11;
        even4 = tmp10 - tmp11;
        Vector256<int> z1 = (tmp12 + tmp13) * Vector256.Create(Fix0_541196100);
        o2 = z1 + (tmp13 * Vector256.Create(Fix0_765366865));
        o6 = z1 + (tmp12 * Vector256.Create(-Fix1_847759065));

        Vector256<int> a1 = tmp4 + tmp7;
        Vector256<int> a2 = tmp5 + tmp6;
        Vector256<int> a3 = tmp4 + tmp6;
        Vector256<int> a4 = tmp5 + tmp7;
        Vector256<int> a5 = (a3 + a4) * Vector256.Create(Fix1_175875602);
        tmp4 *= Vector256.Create(Fix0_298631336);
        tmp5 *= Vector256.Create(Fix2_053119869);
        tmp6 *= Vector256.Create(Fix3_072711026);
        tmp7 *= Vector256.Create(Fix1_501321110);
        a1 *= Vector256.Create(-Fix0_899976223);
        a2 *= Vector256.Create(-Fix2_562915447);
        a3 = (a3 * Vector256.Create(-Fix1_961570560)) + a5;
        a4 = (a4 * Vector256.Create(-Fix0_390180644)) + a5;
        o7 = tmp4 + a1 + a3;
        o5 = tmp5 + a2 + a4;
        o3 = tmp6 + a2 + a3;
        o1 = tmp7 + a1 + a4;
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
