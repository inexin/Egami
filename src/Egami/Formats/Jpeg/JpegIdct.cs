using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The accurate 8×8 inverse DCT in fixed point: the Loeffler-Ligtenberg-Moschytz factorization (ICASSP 1989), 12 multiplies per
/// 1D pass, with 13-bit constants and two extra bits kept between passes. Dequantization is folded into the first pass, and the
/// output is level-shifted by 128 and clamped to 0..255. This is the scaling libjpeg's accurate integer IDCT uses, so the result
/// matches djpeg's default decode.
/// </summary>
internal static class JpegIdct
{
    internal const int ConstBits = 13;
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
        if (Avx2.IsSupported)
        {
            TransformAvx2(coefficients, quant, output, stride);
        }
        else
        {
            TransformScalar(coefficients, quant, output, stride);
        }
    }

    /// <summary>
    /// Fills the output of a block with only a DC term, <paramref name="dc"/> after dequantization: what <see cref="Transform"/>
    /// gives for it, through the same shortcuts and the same integer steps, without the transform.
    /// </summary>
    /// <remarks>
    /// The 4×4 and 2×2 reduced transforms take the same shortcuts with the same arithmetic, so a flat block of
    /// <paramref name="size"/> 4 or 2 gets the same value; the 1×1 transform rounds differently and is not filled here.
    /// </remarks>
    internal static void FillDc(int dc, Span<byte> output, int stride, int size = 8)
    {
        byte value = Clamp(Descale(dc << Pass1Bits, Pass1Bits + 3));
        for (int r = 0; r < size; r++)
        {
            output.Slice(r * stride, size).Fill(value);
        }
    }

    /// <summary>The scalar transform, which the vector one must equal exactly.</summary>
    internal static void TransformScalar(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> output, int stride)
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

    /// <summary>
    /// The transform with AVX2, eight columns or rows at a time in 32-bit lanes: the same integer operations as the scalar one,
    /// including its shortcuts for columns and rows with only a DC term (as lane selects), so the result is identical for every
    /// input, even coefficients that overflow.
    /// </summary>
    [SkipLocalsInit]
    internal static void TransformAvx2(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> output, int stride)
    {
        coefficients = coefficients[..64];
        quant = quant[..64];
        _ = output.Slice(7 * stride, 8);
        ref short c = ref MemoryMarshal.GetReference(coefficients);
        ref ushort q = ref MemoryMarshal.GetReference(quant);

        // Rows of dequantized coefficients: lane u of row v is coefficient (u, v).
        Vector256<int> r0 = Dequantize(ref c, ref q, 0);
        Vector256<int> r1 = Dequantize(ref c, ref q, 8);
        Vector256<int> r2 = Dequantize(ref c, ref q, 16);
        Vector256<int> r3 = Dequantize(ref c, ref q, 24);
        Vector256<int> r4 = Dequantize(ref c, ref q, 32);
        Vector256<int> r5 = Dequantize(ref c, ref q, 40);
        Vector256<int> r6 = Dequantize(ref c, ref q, 48);
        Vector256<int> r7 = Dequantize(ref c, ref q, 56);

        // Pass 1, all columns at once; a column with only its DC term takes the scalar shortcut's value.
        Vector256<int> flat = Vector256.Equals(r1 | r2 | r3 | r4 | r5 | r6 | r7, Vector256<int>.Zero);
        Vector256<int> dc = r0 << Pass1Bits;
        Butterfly(r0, r1, r2, r3, r4, r5, r6, r7, out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3, out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7);
        Vector256<int> round = Vector256.Create(1 << (ConstBits - Pass1Bits - 1));
        const int Shift1 = ConstBits - Pass1Bits;
        r0 = Vector256.ConditionalSelect(flat, dc, (o0 + round) >> Shift1);
        r1 = Vector256.ConditionalSelect(flat, dc, (o1 + round) >> Shift1);
        r2 = Vector256.ConditionalSelect(flat, dc, (o2 + round) >> Shift1);
        r3 = Vector256.ConditionalSelect(flat, dc, (o3 + round) >> Shift1);
        r4 = Vector256.ConditionalSelect(flat, dc, (o4 + round) >> Shift1);
        r5 = Vector256.ConditionalSelect(flat, dc, (o5 + round) >> Shift1);
        r6 = Vector256.ConditionalSelect(flat, dc, (o6 + round) >> Shift1);
        r7 = Vector256.ConditionalSelect(flat, dc, (o7 + round) >> Shift1);

        // Pass 2 on the transpose: vector u holds frequency u of every row, so each lane is one row.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        flat = Vector256.Equals(r1 | r2 | r3 | r4 | r5 | r6 | r7, Vector256<int>.Zero);
        Vector256<int> center = Vector256.Create(128);
        Vector256<int> rounded = r0 + Vector256.Create(1 << (Pass1Bits + 2));
        dc = (rounded >> (Pass1Bits + 3)) + center;
        Butterfly(rounded, r1, r2, r3, r4, r5, r6, r7, out o0, out o1, out o2, out o3, out o4, out o5, out o6, out o7);
        const int Shift2 = ConstBits + Pass1Bits + 3;
        r0 = Vector256.ConditionalSelect(flat, dc, (o0 >> Shift2) + center);
        r1 = Vector256.ConditionalSelect(flat, dc, (o1 >> Shift2) + center);
        r2 = Vector256.ConditionalSelect(flat, dc, (o2 >> Shift2) + center);
        r3 = Vector256.ConditionalSelect(flat, dc, (o3 >> Shift2) + center);
        r4 = Vector256.ConditionalSelect(flat, dc, (o4 >> Shift2) + center);
        r5 = Vector256.ConditionalSelect(flat, dc, (o5 >> Shift2) + center);
        r6 = Vector256.ConditionalSelect(flat, dc, (o6 >> Shift2) + center);
        r7 = Vector256.ConditionalSelect(flat, dc, (o7 >> Shift2) + center);

        // Vector x now holds output column x; transpose back to rows and pack with saturation, which is the clamp to 0..255.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Vector256<int> order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);
        Vector256<ulong> top = Avx2.PermuteVar8x32(Avx2.PackUnsignedSaturate(Avx2.PackSignedSaturate(r0, r1), Avx2.PackSignedSaturate(r2, r3)).AsInt32(), order).AsUInt64();
        Vector256<ulong> bottom = Avx2.PermuteVar8x32(Avx2.PackUnsignedSaturate(Avx2.PackSignedSaturate(r4, r5), Avx2.PackSignedSaturate(r6, r7)).AsInt32(), order).AsUInt64();
        ref byte o = ref MemoryMarshal.GetReference(output);
        Unsafe.WriteUnaligned(ref o, top.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, stride), top.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 2 * stride), top.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 3 * stride), top.GetElement(3));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 4 * stride), bottom.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 5 * stride), bottom.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 6 * stride), bottom.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 7 * stride), bottom.GetElement(3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<int> Dequantize(ref short coefficients, ref ushort quant, int at)
    {
        Vector256<int> values = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref coefficients, (nuint)at));
        Vector256<int> scale = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref quant, (nuint)at));
        return values * scale;
    }

    /// <summary>Transposes an 8×8 block of 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose(
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
    internal static void Butterfly(
        Vector256<int> s0, Vector256<int> s1, Vector256<int> s2, Vector256<int> s3, Vector256<int> s4, Vector256<int> s5, Vector256<int> s6, Vector256<int> s7,
        out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3, out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7)
    {
        Vector256<int> z1 = (s2 + s6) * Vector256.Create(Fix0_541196100);
        Vector256<int> tmp2 = z1 + (s6 * Vector256.Create(-Fix1_847759065));
        Vector256<int> tmp3 = z1 + (s2 * Vector256.Create(Fix0_765366865));
        Vector256<int> tmp0 = (s0 + s4) << ConstBits;
        Vector256<int> tmp1 = (s0 - s4) << ConstBits;
        Vector256<int> tmp10 = tmp0 + tmp3;
        Vector256<int> tmp13 = tmp0 - tmp3;
        Vector256<int> tmp11 = tmp1 + tmp2;
        Vector256<int> tmp12 = tmp1 - tmp2;

        Vector256<int> b1 = s7 + s1;
        Vector256<int> b2 = s5 + s3;
        Vector256<int> b3 = s7 + s3;
        Vector256<int> b4 = s5 + s1;
        Vector256<int> b5 = (b3 + b4) * Vector256.Create(Fix1_175875602);
        Vector256<int> a0 = s7 * Vector256.Create(Fix0_298631336);
        Vector256<int> a1 = s5 * Vector256.Create(Fix2_053119869);
        Vector256<int> a2 = s3 * Vector256.Create(Fix3_072711026);
        Vector256<int> a3 = s1 * Vector256.Create(Fix1_501321110);
        b1 *= Vector256.Create(-Fix0_899976223);
        b2 *= Vector256.Create(-Fix2_562915447);
        b3 = (b3 * Vector256.Create(-Fix1_961570560)) + b5;
        b4 = (b4 * Vector256.Create(-Fix0_390180644)) + b5;
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

    /// <summary>One 1D pass: inputs in frequency order 0..7, outputs in sample order 0..7, scaled by 2^13.</summary>
    internal static void Butterfly(
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

    // Constants of the reduced transforms: averaged cosine terms scaled by 2^13.
    private const int Fix0_211164243 = 1730;
    private const int Fix0_509795579 = 4176;
    private const int Fix0_601344887 = 4926;
    private const int Fix0_720959822 = 5906;
    private const int Fix0_850430095 = 6967;
    private const int Fix1_061594337 = 8697;
    private const int Fix1_272758580 = 10426;
    private const int Fix1_451774981 = 11893;
    private const int Fix2_172734803 = 17799;
    private const int Fix3_624509785 = 29692;

    /// <summary>
    /// Transforms one block to 4×4 samples, for a 1/2-scale decode: the 8-point inverse DCT averaged over pairs of output samples,
    /// which drops coefficient 4 of each direction (it averages to zero) and folds the odd terms into four constants. The scaling
    /// and constants are those of libjpeg's reduced IDCT, so the result matches <c>djpeg -scale 1/2</c>.
    /// </summary>
    internal static void Transform4(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> output, int stride)
    {
        Span<int> work = stackalloc int[32];
        coefficients = coefficients[..64];
        quant = quant[..64];
        for (int c = 0; c < 8; c++)
        {
            if (c == 4)
            {
                continue;
            }

            if (coefficients[8 + c] == 0 && coefficients[16 + c] == 0 && coefficients[24 + c] == 0
                && coefficients[40 + c] == 0 && coefficients[48 + c] == 0 && coefficients[56 + c] == 0)
            {
                int dc = coefficients[c] * quant[c] << Pass1Bits;
                work[c] = work[8 + c] = work[16 + c] = work[24 + c] = dc;
                continue;
            }

            Reduced4(
                coefficients[c] * quant[c], coefficients[8 + c] * quant[8 + c], coefficients[16 + c] * quant[16 + c], coefficients[24 + c] * quant[24 + c],
                coefficients[40 + c] * quant[40 + c], coefficients[48 + c] * quant[48 + c], coefficients[56 + c] * quant[56 + c],
                out int o0, out int o1, out int o2, out int o3);
            int shift = ConstBits - Pass1Bits + 1;
            work[c] = Descale(o0, shift);
            work[8 + c] = Descale(o1, shift);
            work[16 + c] = Descale(o2, shift);
            work[24 + c] = Descale(o3, shift);
        }

        for (int r = 0; r < 4; r++)
        {
            Span<int> row = work.Slice(r * 8, 8);
            Span<byte> target = output.Slice(r * stride, 4);
            if (row[1] == 0 && row[2] == 0 && row[3] == 0 && row[5] == 0 && row[6] == 0 && row[7] == 0)
            {
                target.Fill(Clamp(Descale(row[0], Pass1Bits + 3)));
                continue;
            }

            Reduced4(row[0], row[1], row[2], row[3], row[5], row[6], row[7], out int o0, out int o1, out int o2, out int o3);
            int shift = ConstBits + Pass1Bits + 3 + 1;
            target[0] = Clamp(Descale(o0, shift));
            target[1] = Clamp(Descale(o1, shift));
            target[2] = Clamp(Descale(o2, shift));
            target[3] = Clamp(Descale(o3, shift));
        }
    }

    /// <summary>
    /// Transforms one block to 2×2 samples, for a 1/4-scale decode: the 8-point inverse DCT averaged over runs of four samples,
    /// where only the DC and the odd coefficients remain, as in libjpeg's reduced IDCT (<c>djpeg -scale 1/4</c>).
    /// </summary>
    internal static void Transform2(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> output, int stride)
    {
        Span<int> work = stackalloc int[16];
        coefficients = coefficients[..64];
        quant = quant[..64];
        for (int c = 0; c < 8; c++)
        {
            if (c is 2 or 4 or 6)
            {
                continue;
            }

            if (coefficients[8 + c] == 0 && coefficients[24 + c] == 0 && coefficients[40 + c] == 0 && coefficients[56 + c] == 0)
            {
                work[c] = work[8 + c] = coefficients[c] * quant[c] << Pass1Bits;
                continue;
            }

            Reduced2(
                coefficients[c] * quant[c], coefficients[8 + c] * quant[8 + c], coefficients[24 + c] * quant[24 + c],
                coefficients[40 + c] * quant[40 + c], coefficients[56 + c] * quant[56 + c], out int o0, out int o1);
            int shift = ConstBits - Pass1Bits + 2;
            work[c] = Descale(o0, shift);
            work[8 + c] = Descale(o1, shift);
        }

        for (int r = 0; r < 2; r++)
        {
            Span<int> row = work.Slice(r * 8, 8);
            Span<byte> target = output.Slice(r * stride, 2);
            if (row[1] == 0 && row[3] == 0 && row[5] == 0 && row[7] == 0)
            {
                target.Fill(Clamp(Descale(row[0], Pass1Bits + 3)));
                continue;
            }

            Reduced2(row[0], row[1], row[3], row[5], row[7], out int o0, out int o1);
            int shift = ConstBits + Pass1Bits + 3 + 2;
            target[0] = Clamp(Descale(o0, shift));
            target[1] = Clamp(Descale(o1, shift));
        }
    }

    /// <summary>Transforms one block to its average, one sample, for a 1/8-scale decode.</summary>
    internal static void Transform1(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> output, int stride)
    {
        _ = stride;
        output[0] = Clamp(Descale(coefficients[0] * quant[0], 3));
    }

    /// <summary>One 1D pass of the 4-point reduced transform (inputs 0 to 7 without 4).</summary>
    internal static void Reduced4(int i0, int i1, int i2, int i3, int i5, int i6, int i7, out int o0, out int o1, out int o2, out int o3)
    {
        int tmp0 = i0 << (ConstBits + 1);
        int tmp2 = (i2 * Fix1_847759065) - (i6 * Fix0_765366865);
        int tmp10 = tmp0 + tmp2;
        int tmp12 = tmp0 - tmp2;

        int odd0 = (i7 * -Fix0_211164243) + (i5 * Fix1_451774981) + (i3 * -Fix2_172734803) + (i1 * Fix1_061594337);
        int odd2 = (i7 * -Fix0_509795579) + (i5 * -Fix0_601344887) + (i3 * Fix0_899976223) + (i1 * Fix2_562915447);
        o0 = tmp10 + odd2;
        o3 = tmp10 - odd2;
        o1 = tmp12 + odd0;
        o2 = tmp12 - odd0;
    }

    /// <summary>One 1D pass of the 2-point reduced transform (inputs 0, 1, 3, 5 and 7).</summary>
    internal static void Reduced2(int i0, int i1, int i3, int i5, int i7, out int o0, out int o1)
    {
        int tmp10 = i0 << (ConstBits + 2);
        int odd = (i7 * -Fix0_720959822) + (i5 * Fix0_850430095) + (i3 * -Fix1_272758580) + (i1 * Fix3_624509785);
        o0 = tmp10 + odd;
        o1 = tmp10 - odd;
    }

    internal static int Descale(int x, int n)
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
