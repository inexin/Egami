using System.Diagnostics.CodeAnalysis;
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
    /// <see cref="Forward"/> with AVX2 for one block: the two-block kernel with the block in both halves, keeping the first.
    /// </summary>
    [SkipLocalsInit]
    internal static void ForwardAvx2(ReadOnlySpan<byte> samples, int stride, ReadOnlySpan<ushort> reciprocals, Span<short> output)
    {
        _ = samples.Slice(7 * stride, 8);
        Span<short> unused = stackalloc short[64];
        Forward2(ref MemoryMarshal.GetReference(samples), stride, false, reciprocals, output, unused);
    }

    /// <summary>
    /// <see cref="Forward"/> with AVX2 for two blocks side by side: <paramref name="first"/> from the first 8 columns of
    /// <paramref name="samples"/> and <paramref name="second"/> from the next 8. The output is identical to the scalar code.
    /// </summary>
    internal static void ForwardAvx2(ReadOnlySpan<byte> samples, int stride, ReadOnlySpan<ushort> reciprocals, Span<short> first, Span<short> second)
    {
        _ = samples.Slice(7 * stride, 16);
        Forward2(ref MemoryMarshal.GetReference(samples), stride, true, reciprocals, first, second);
    }

    /// <summary>
    /// Both passes in 16-bit lanes, one block in each 128-bit half. Sums and differences are 16-bit adds; every rotation is a
    /// multiply-add of interleaved pairs into 32 bits, rounded and shifted as <see cref="Descale"/> does, then packed back. For 8-bit
    /// samples every 16-bit value fits (the tests bound them), and the multiply-adds expand the scalar products exactly, so the
    /// result is the scalar code's. Quantization uses <see cref="Reciprocals"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Forward2(ref byte s, int stride, bool pair, ReadOnlySpan<ushort> reciprocals, Span<short> first, Span<short> second)
    {
        reciprocals = reciprocals[..192];
        first = first[..64];
        second = second[..64];
        Vector256<short> center = Vector256.Create((short)128);
        Vector256<short> r0 = Row(ref s, 0, pair) - center;
        Vector256<short> r1 = Row(ref s, stride, pair) - center;
        Vector256<short> r2 = Row(ref s, 2 * stride, pair) - center;
        Vector256<short> r3 = Row(ref s, 3 * stride, pair) - center;
        Vector256<short> r4 = Row(ref s, 4 * stride, pair) - center;
        Vector256<short> r5 = Row(ref s, 5 * stride, pair) - center;
        Vector256<short> r6 = Row(ref s, 6 * stride, pair) - center;
        Vector256<short> r7 = Row(ref s, 7 * stride, pair) - center;

        // Pass 1 on the transpose, so each lane is one row: vector k holds sample k of every row.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Butterfly(r0, r1, r2, r3, r4, r5, r6, r7, ConstBits - Pass1Bits, out Vector256<short> e0, out Vector256<short> e4, out r2, out r6, out r1, out r3, out r5, out r7);
        r0 = e0 << Pass1Bits;
        r4 = e4 << Pass1Bits;

        // Pass 2 on the transpose back: vector u holds frequency u of every row, lanes the rows; outputs are rows of frequencies.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Butterfly(r0, r1, r2, r3, r4, r5, r6, r7, ConstBits + Pass1Bits, out e0, out e4, out r2, out r6, out r1, out r3, out r5, out r7);
        Vector256<short> roundDc = Vector256.Create((short)(1 << (Pass1Bits - 1)));
        r0 = (e0 + roundDc) >> Pass1Bits;
        r4 = (e4 + roundDc) >> Pass1Bits;

        // Rows in natural order, the first block's from the low halves and the second's from the high halves.
        ref ushort table = ref MemoryMarshal.GetReference(reciprocals);
        ref short a = ref MemoryMarshal.GetReference(first);
        ref short b = ref MemoryMarshal.GetReference(second);
        Quantize(Avx2.Permute2x128(r0, r1, 0x20), ref table, 0).StoreUnsafe(ref a, 0);
        Quantize(Avx2.Permute2x128(r2, r3, 0x20), ref table, 16).StoreUnsafe(ref a, 16);
        Quantize(Avx2.Permute2x128(r4, r5, 0x20), ref table, 32).StoreUnsafe(ref a, 32);
        Quantize(Avx2.Permute2x128(r6, r7, 0x20), ref table, 48).StoreUnsafe(ref a, 48);
        Quantize(Avx2.Permute2x128(r0, r1, 0x31), ref table, 0).StoreUnsafe(ref b, 0);
        Quantize(Avx2.Permute2x128(r2, r3, 0x31), ref table, 16).StoreUnsafe(ref b, 16);
        Quantize(Avx2.Permute2x128(r4, r5, 0x31), ref table, 32).StoreUnsafe(ref b, 32);
        Quantize(Avx2.Permute2x128(r6, r7, 0x31), ref table, 48).StoreUnsafe(ref b, 48);

        // The DC is not limited to 10 bits; restore it from the unclamped result.
        a = (short)Quantize(r0.GetElement(0), reciprocals);
        b = (short)Quantize(r0.GetElement(8), reciprocals);
    }

    /// <summary>Runs the vector quantization (AC coefficients, limited to 10 bits) on one value, for tests.</summary>
    internal static int QuantizeVectorForTest(int value, int divisor)
    {
        ushort[] table = Reciprocals([.. Enumerable.Repeat(divisor, 64)]);
        return Quantize(Vector256.Create((short)value), ref table[0], 0).ToScalar();
    }

    /// <summary>
    /// Returns, for each divisor (natural order), the three 16-bit factors the AVX2 kernels quantizes with: a reciprocal
    /// at 0, a correction at 64 and a scale at 128. Adding the correction to the magnitude, then keeping the high halves of the
    /// products with the reciprocal and the scale, divides with rounding exactly as <see cref="Quantize(int, int)"/> does for
    /// every magnitude the DCT gives (the tests check all of them); this is the reciprocal scheme of libjpeg-turbo's SIMD code.
    /// </summary>
    internal static ushort[] Reciprocals(ReadOnlySpan<int> divisors)
    {
        ushort[] table = new ushort[192];
        for (int i = 0; i < 64; i++)
        {
            int divisor = divisors[i];
            int r = 16 + (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)divisor));
            long reciprocal = (1L << r) / divisor;
            long fraction = (1L << r) % divisor;
            int correction = divisor / 2;
            if (fraction == 0)
            {
                // A power of two: the reciprocal would need 17 bits.
                reciprocal >>= 1;
                r--;
            }
            else if (fraction <= divisor / 2)
            {
                correction++;
            }
            else
            {
                reciprocal++;
            }

            table[i] = (ushort)reciprocal;
            table[64 + i] = (ushort)correction;
            table[128 + i] = (ushort)(1 << (32 - r));
        }

        return table;
    }

    /// <summary>Quantizes 16 coefficients with the factors at <paramref name="at"/> of the table, limiting them to 10 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Quantize(Vector256<short> values, ref ushort table, int at)
    {
        Vector256<short> sign = values >> 15;
        Vector256<ushort> magnitude = Vector256.Abs(values).AsUInt16() + Vector256.LoadUnsafe(ref table, (nuint)(64 + at));
        magnitude = Avx2.MultiplyHigh(magnitude, Vector256.LoadUnsafe(ref table, (nuint)at));
        magnitude = Avx2.MultiplyHigh(magnitude, Vector256.LoadUnsafe(ref table, (nuint)(128 + at)));
        Vector256<short> quotient = (magnitude.AsInt16() ^ sign) - sign;

        // Baseline AC tables code at most 10 bits of magnitude.
        return Vector256.Min(Vector256.Max(quotient, Vector256.Create((short)-1023)), Vector256.Create((short)1023));
    }

    /// <summary>The DC with the same factors, unlimited, for the one coefficient the vector path clamps but must not.</summary>
    private static int Quantize(int value, ReadOnlySpan<ushort> table)
    {
        uint magnitude = (uint)Math.Abs(value) + table[64];
        magnitude = (magnitude * table[0]) >> 16;
        magnitude = (magnitude * table[128]) >> 16;
        return value < 0 ? -(int)magnitude : (int)magnitude;
    }

    /// <summary>Row <paramref name="offset"/> of both blocks, or of one block twice, widened to 16 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Row(ref byte samples, int offset, bool pair)
    {
        ref byte row = ref Unsafe.Add(ref samples, offset);
        Vector128<byte> bytes = pair ? Vector128.LoadUnsafe(ref row) : Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref row)).AsByte();
        return Avx2.ConvertToVector256Int16(bytes);
    }

    /// <summary>Transposes the 8×8 block of 16-bit lanes in each 128-bit half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector256<short> r0, ref Vector256<short> r1, ref Vector256<short> r2, ref Vector256<short> r3,
        ref Vector256<short> r4, ref Vector256<short> r5, ref Vector256<short> r6, ref Vector256<short> r7)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1).AsInt32();
        Vector256<int> t1 = Avx2.UnpackHigh(r0, r1).AsInt32();
        Vector256<int> t2 = Avx2.UnpackLow(r2, r3).AsInt32();
        Vector256<int> t3 = Avx2.UnpackHigh(r2, r3).AsInt32();
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5).AsInt32();
        Vector256<int> t5 = Avx2.UnpackHigh(r4, r5).AsInt32();
        Vector256<int> t6 = Avx2.UnpackLow(r6, r7).AsInt32();
        Vector256<int> t7 = Avx2.UnpackHigh(r6, r7).AsInt32();
        Vector256<long> u0 = Avx2.UnpackLow(t0, t2).AsInt64();
        Vector256<long> u1 = Avx2.UnpackHigh(t0, t2).AsInt64();
        Vector256<long> u2 = Avx2.UnpackLow(t1, t3).AsInt64();
        Vector256<long> u3 = Avx2.UnpackHigh(t1, t3).AsInt64();
        Vector256<long> u4 = Avx2.UnpackLow(t4, t6).AsInt64();
        Vector256<long> u5 = Avx2.UnpackHigh(t4, t6).AsInt64();
        Vector256<long> u6 = Avx2.UnpackLow(t5, t7).AsInt64();
        Vector256<long> u7 = Avx2.UnpackHigh(t5, t7).AsInt64();
        r0 = Avx2.UnpackLow(u0, u4).AsInt16();
        r1 = Avx2.UnpackHigh(u0, u4).AsInt16();
        r2 = Avx2.UnpackLow(u1, u5).AsInt16();
        r3 = Avx2.UnpackHigh(u1, u5).AsInt16();
        r4 = Avx2.UnpackLow(u2, u6).AsInt16();
        r5 = Avx2.UnpackHigh(u2, u6).AsInt16();
        r6 = Avx2.UnpackLow(u3, u7).AsInt16();
        r7 = Avx2.UnpackHigh(u3, u7).AsInt16();
    }

    /// <summary>
    /// The scalar 1D pass on 16 lanes. The even outputs 0 and 4 come back unscaled. Each rotated output is expanded to one
    /// constant per input (the scalar code's shared products multiplied out, exact in integers): one or two multiply-adds of
    /// interleaved pairs per half, descaled by <paramref name="shift"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(
        Vector256<short> d0, Vector256<short> d1, Vector256<short> d2, Vector256<short> d3, Vector256<short> d4, Vector256<short> d5, Vector256<short> d6, Vector256<short> d7, [ConstantExpected] int shift,
        out Vector256<short> even0, out Vector256<short> even4, out Vector256<short> o2, out Vector256<short> o6, out Vector256<short> o1, out Vector256<short> o3, out Vector256<short> o5, out Vector256<short> o7)
    {
        Vector256<short> tmp0 = d0 + d7;
        Vector256<short> tmp7 = d0 - d7;
        Vector256<short> tmp1 = d1 + d6;
        Vector256<short> tmp6 = d1 - d6;
        Vector256<short> tmp2 = d2 + d5;
        Vector256<short> tmp5 = d2 - d5;
        Vector256<short> tmp3 = d3 + d4;
        Vector256<short> tmp4 = d3 - d4;

        Vector256<short> tmp10 = tmp0 + tmp3;
        Vector256<short> tmp13 = tmp0 - tmp3;
        Vector256<short> tmp11 = tmp1 + tmp2;
        Vector256<short> tmp12 = tmp1 - tmp2;
        even0 = tmp10 + tmp11;
        even4 = tmp10 - tmp11;

        // o2 = tmp13 × (0.541 + 0.765) + tmp12 × 0.541 and o6 = tmp13 × 0.541 + tmp12 × (0.541 − 1.848).
        Vector256<int> round = Vector256.Create(1 << (shift - 1));
        Vector256<short> low = Avx2.UnpackLow(tmp13, tmp12);
        Vector256<short> high = Avx2.UnpackHigh(tmp13, tmp12);
        o2 = Rotate(low, high, Pair(Fix0_541196100 + Fix0_765366865, Fix0_541196100), round, shift);
        o6 = Rotate(low, high, Pair(Fix0_541196100, Fix0_541196100 - Fix1_847759065), round, shift);

        // Each odd output as a sum over tmp4..tmp7: its own factor, those of the a1..a4 sums it takes, and the shared 1.176.
        Vector256<short> low45 = Avx2.UnpackLow(tmp4, tmp5);
        Vector256<short> high45 = Avx2.UnpackHigh(tmp4, tmp5);
        Vector256<short> low67 = Avx2.UnpackLow(tmp6, tmp7);
        Vector256<short> high67 = Avx2.UnpackHigh(tmp6, tmp7);
        o7 = Rotate(
            low45, high45, Pair(Fix0_298631336 - Fix0_899976223 - Fix1_961570560 + Fix1_175875602, Fix1_175875602),
            low67, high67, Pair(Fix1_175875602 - Fix1_961570560, Fix1_175875602 - Fix0_899976223), round, shift);
        o5 = Rotate(
            low45, high45, Pair(Fix1_175875602, Fix2_053119869 - Fix2_562915447 - Fix0_390180644 + Fix1_175875602),
            low67, high67, Pair(Fix1_175875602 - Fix2_562915447, Fix1_175875602 - Fix0_390180644), round, shift);
        o3 = Rotate(
            low45, high45, Pair(Fix1_175875602 - Fix1_961570560, Fix1_175875602 - Fix2_562915447),
            low67, high67, Pair(Fix3_072711026 - Fix2_562915447 - Fix1_961570560 + Fix1_175875602, Fix1_175875602), round, shift);
        o1 = Rotate(
            low45, high45, Pair(Fix1_175875602 - Fix0_899976223, Fix1_175875602 - Fix0_390180644),
            low67, high67, Pair(Fix1_175875602, Fix1_501321110 - Fix0_899976223 - Fix0_390180644 + Fix1_175875602), round, shift);
    }

    /// <summary>Two 16-bit constants for the first and second value of interleaved pairs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Pair(int first, int second)
    {
        return Vector256.Create((int)(ushort)first | (second << 16)).AsInt16();
    }

    /// <summary>One rotation of interleaved pairs, descaled and packed back to the lane order of the inputs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Rotate(Vector256<short> low, Vector256<short> high, Vector256<short> factors, Vector256<int> round, [ConstantExpected] int shift)
    {
        Vector256<int> l = Avx2.MultiplyAddAdjacent(low, factors);
        Vector256<int> h = Avx2.MultiplyAddAdjacent(high, factors);
        return Avx2.PackSignedSaturate((l + round) >> shift, (h + round) >> shift);
    }

    /// <summary>Two rotations of interleaved pairs summed, descaled and packed back.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Rotate(
        Vector256<short> low1, Vector256<short> high1, Vector256<short> factors1,
        Vector256<short> low2, Vector256<short> high2, Vector256<short> factors2, Vector256<int> round, [ConstantExpected] int shift)
    {
        Vector256<int> l = Avx2.MultiplyAddAdjacent(low1, factors1) + Avx2.MultiplyAddAdjacent(low2, factors2);
        Vector256<int> h = Avx2.MultiplyAddAdjacent(high1, factors1) + Avx2.MultiplyAddAdjacent(high2, factors2);
        return Avx2.PackSignedSaturate((l + round) >> shift, (h + round) >> shift);
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
