using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The inverse DCTs of <see cref="JpegIdct"/> for 12-bit samples: the same factorizations and constants with one extra bit kept
/// between passes instead of two, so the 32-bit lanes cannot overflow on valid data, and output level-shifted by 2048 and clamped
/// to 0..4095. This is the scaling libjpeg's accurate integer IDCT uses at 12 bits, so the result matches djpeg's.
/// </summary>
internal static class JpegIdct12
{
    private const int ConstBits = JpegIdct.ConstBits;
    private const int Pass1Bits = 1;
    private const int Center = 2048;
    private const int MaxSample = 4095;

    /// <summary>
    /// Transforms one block. <paramref name="coefficients"/> and <paramref name="quant"/> are in natural (row-major) order;
    /// <paramref name="output"/> receives 8 rows of 8 samples, <paramref name="stride"/> samples apart.
    /// </summary>
    internal static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<ushort> output, int stride)
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

    /// <summary>Fills the output of a block with only a DC term, <paramref name="dc"/> after dequantization, as the transforms give it.</summary>
    internal static void FillDc(int dc, Span<ushort> output, int stride, int size = 8)
    {
        ushort value = Clamp(JpegIdct.Descale(dc << Pass1Bits, Pass1Bits + 3));
        for (int r = 0; r < size; r++)
        {
            output.Slice(r * stride, size).Fill(value);
        }
    }

    /// <summary>The scalar transform, which the vector one must equal exactly.</summary>
    internal static void TransformScalar(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<ushort> output, int stride)
    {
        Span<int> work = stackalloc int[64];
        coefficients = coefficients[..64];
        quant = quant[..64];
        for (int c = 0; c < 8; c++)
        {
            if (coefficients[8 + c] == 0 && coefficients[16 + c] == 0 && coefficients[24 + c] == 0 && coefficients[32 + c] == 0
                && coefficients[40 + c] == 0 && coefficients[48 + c] == 0 && coefficients[56 + c] == 0)
            {
                int dc = coefficients[c] * quant[c] << Pass1Bits;
                for (int r = 0; r < 8; r++)
                {
                    work[(r * 8) + c] = dc;
                }

                continue;
            }

            JpegIdct.Butterfly(
                coefficients[c] * quant[c], coefficients[8 + c] * quant[8 + c], coefficients[16 + c] * quant[16 + c], coefficients[24 + c] * quant[24 + c],
                coefficients[32 + c] * quant[32 + c], coefficients[40 + c] * quant[40 + c], coefficients[48 + c] * quant[48 + c], coefficients[56 + c] * quant[56 + c],
                out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7);
            const int Shift = ConstBits - Pass1Bits;
            work[c] = JpegIdct.Descale(o0, Shift);
            work[8 + c] = JpegIdct.Descale(o1, Shift);
            work[16 + c] = JpegIdct.Descale(o2, Shift);
            work[24 + c] = JpegIdct.Descale(o3, Shift);
            work[32 + c] = JpegIdct.Descale(o4, Shift);
            work[40 + c] = JpegIdct.Descale(o5, Shift);
            work[48 + c] = JpegIdct.Descale(o6, Shift);
            work[56 + c] = JpegIdct.Descale(o7, Shift);
        }

        for (int r = 0; r < 8; r++)
        {
            Span<int> w = work.Slice(r * 8, 8);
            Span<ushort> o = output.Slice(r * stride, 8);
            if (w[1] == 0 && w[2] == 0 && w[3] == 0 && w[4] == 0 && w[5] == 0 && w[6] == 0 && w[7] == 0)
            {
                o.Fill(Clamp(JpegIdct.Descale(w[0], Pass1Bits + 3)));
                continue;
            }

            JpegIdct.Butterfly(
                w[0] + (1 << (Pass1Bits + 2)), w[1], w[2], w[3], w[4], w[5], w[6], w[7],
                out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7);
            const int Shift = ConstBits + Pass1Bits + 3;
            o[0] = Clamp(o0 >> Shift);
            o[1] = Clamp(o1 >> Shift);
            o[2] = Clamp(o2 >> Shift);
            o[3] = Clamp(o3 >> Shift);
            o[4] = Clamp(o4 >> Shift);
            o[5] = Clamp(o5 >> Shift);
            o[6] = Clamp(o6 >> Shift);
            o[7] = Clamp(o7 >> Shift);
        }
    }

    /// <summary>The transform with AVX2, eight columns or rows at a time, operation for operation as the scalar one.</summary>
    [SkipLocalsInit]
    internal static void TransformAvx2(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<ushort> output, int stride)
    {
        coefficients = coefficients[..64];
        quant = quant[..64];
        _ = output.Slice(7 * stride, 8);
        ref short c = ref MemoryMarshal.GetReference(coefficients);
        ref ushort q = ref MemoryMarshal.GetReference(quant);
        Vector256<int> r0 = JpegIdct.Dequantize(ref c, ref q, 0);
        Vector256<int> r1 = JpegIdct.Dequantize(ref c, ref q, 8);
        Vector256<int> r2 = JpegIdct.Dequantize(ref c, ref q, 16);
        Vector256<int> r3 = JpegIdct.Dequantize(ref c, ref q, 24);
        Vector256<int> r4 = JpegIdct.Dequantize(ref c, ref q, 32);
        Vector256<int> r5 = JpegIdct.Dequantize(ref c, ref q, 40);
        Vector256<int> r6 = JpegIdct.Dequantize(ref c, ref q, 48);
        Vector256<int> r7 = JpegIdct.Dequantize(ref c, ref q, 56);

        Vector256<int> flat = Vector256.Equals(r1 | r2 | r3 | r4 | r5 | r6 | r7, Vector256<int>.Zero);
        Vector256<int> dc = r0 << Pass1Bits;
        JpegIdct.Butterfly(r0, r1, r2, r3, r4, r5, r6, r7, out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3, out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7);
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

        JpegIdct.Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        flat = Vector256.Equals(r1 | r2 | r3 | r4 | r5 | r6 | r7, Vector256<int>.Zero);
        Vector256<int> center = Vector256.Create(Center);
        Vector256<int> rounded = r0 + Vector256.Create(1 << (Pass1Bits + 2));
        dc = (rounded >> (Pass1Bits + 3)) + center;
        JpegIdct.Butterfly(rounded, r1, r2, r3, r4, r5, r6, r7, out o0, out o1, out o2, out o3, out o4, out o5, out o6, out o7);
        const int Shift2 = ConstBits + Pass1Bits + 3;
        r0 = Vector256.ConditionalSelect(flat, dc, (o0 >> Shift2) + center);
        r1 = Vector256.ConditionalSelect(flat, dc, (o1 >> Shift2) + center);
        r2 = Vector256.ConditionalSelect(flat, dc, (o2 >> Shift2) + center);
        r3 = Vector256.ConditionalSelect(flat, dc, (o3 >> Shift2) + center);
        r4 = Vector256.ConditionalSelect(flat, dc, (o4 >> Shift2) + center);
        r5 = Vector256.ConditionalSelect(flat, dc, (o5 >> Shift2) + center);
        r6 = Vector256.ConditionalSelect(flat, dc, (o6 >> Shift2) + center);
        r7 = Vector256.ConditionalSelect(flat, dc, (o7 >> Shift2) + center);

        // Vector x holds output column x; back to rows, clamped to 0..4095 and packed to 16 bits.
        JpegIdct.Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        ref ushort o = ref MemoryMarshal.GetReference(output);
        StoreRows(ref o, stride, r0, r1);
        StoreRows(ref Unsafe.Add(ref o, 2 * stride), stride, r2, r3);
        StoreRows(ref Unsafe.Add(ref o, 4 * stride), stride, r4, r5);
        StoreRows(ref Unsafe.Add(ref o, 6 * stride), stride, r6, r7);
    }

    /// <summary>Clamps two rows of eight samples to 0..4095 and stores them as 16-bit values, <paramref name="stride"/> apart.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRows(ref ushort row, int stride, Vector256<int> first, Vector256<int> second)
    {
        Vector256<int> max = Vector256.Create(MaxSample);
        first = Vector256.Min(Vector256.Max(first, Vector256<int>.Zero), max);
        second = Vector256.Min(Vector256.Max(second, Vector256<int>.Zero), max);

        // The pack interleaves 128-bit lanes; the permute puts each row's eight samples together.
        Vector256<ushort> packed = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(first, second).AsInt64(), 0b11_01_10_00).AsUInt16();
        packed.GetLower().StoreUnsafe(ref row);
        packed.GetUpper().StoreUnsafe(ref Unsafe.Add(ref row, stride));
    }

    /// <summary>Transforms one block to 4×4 samples, for a 1/2-scale decode, as <see cref="JpegIdct.Transform4"/> at 12 bits.</summary>
    internal static void Transform4(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<ushort> output, int stride)
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

            JpegIdct.Reduced4(
                coefficients[c] * quant[c], coefficients[8 + c] * quant[8 + c], coefficients[16 + c] * quant[16 + c], coefficients[24 + c] * quant[24 + c],
                coefficients[40 + c] * quant[40 + c], coefficients[48 + c] * quant[48 + c], coefficients[56 + c] * quant[56 + c],
                out int o0, out int o1, out int o2, out int o3);
            const int Shift = ConstBits - Pass1Bits + 1;
            work[c] = JpegIdct.Descale(o0, Shift);
            work[8 + c] = JpegIdct.Descale(o1, Shift);
            work[16 + c] = JpegIdct.Descale(o2, Shift);
            work[24 + c] = JpegIdct.Descale(o3, Shift);
        }

        for (int r = 0; r < 4; r++)
        {
            Span<int> row = work.Slice(r * 8, 8);
            Span<ushort> target = output.Slice(r * stride, 4);
            if (row[1] == 0 && row[2] == 0 && row[3] == 0 && row[5] == 0 && row[6] == 0 && row[7] == 0)
            {
                target.Fill(Clamp(JpegIdct.Descale(row[0], Pass1Bits + 3)));
                continue;
            }

            JpegIdct.Reduced4(row[0], row[1], row[2], row[3], row[5], row[6], row[7], out int o0, out int o1, out int o2, out int o3);
            const int Shift = ConstBits + Pass1Bits + 3 + 1;
            target[0] = Clamp(JpegIdct.Descale(o0, Shift));
            target[1] = Clamp(JpegIdct.Descale(o1, Shift));
            target[2] = Clamp(JpegIdct.Descale(o2, Shift));
            target[3] = Clamp(JpegIdct.Descale(o3, Shift));
        }
    }

    /// <summary>Transforms one block to 2×2 samples, for a 1/4-scale decode, as <see cref="JpegIdct.Transform2"/> at 12 bits.</summary>
    internal static void Transform2(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<ushort> output, int stride)
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

            JpegIdct.Reduced2(
                coefficients[c] * quant[c], coefficients[8 + c] * quant[8 + c], coefficients[24 + c] * quant[24 + c],
                coefficients[40 + c] * quant[40 + c], coefficients[56 + c] * quant[56 + c], out int o0, out int o1);
            const int Shift = ConstBits - Pass1Bits + 2;
            work[c] = JpegIdct.Descale(o0, Shift);
            work[8 + c] = JpegIdct.Descale(o1, Shift);
        }

        for (int r = 0; r < 2; r++)
        {
            Span<int> row = work.Slice(r * 8, 8);
            Span<ushort> target = output.Slice(r * stride, 2);
            if (row[1] == 0 && row[3] == 0 && row[5] == 0 && row[7] == 0)
            {
                target.Fill(Clamp(JpegIdct.Descale(row[0], Pass1Bits + 3)));
                continue;
            }

            JpegIdct.Reduced2(row[0], row[1], row[3], row[5], row[7], out int o0, out int o1);
            const int Shift = ConstBits + Pass1Bits + 3 + 2;
            target[0] = Clamp(JpegIdct.Descale(o0, Shift));
            target[1] = Clamp(JpegIdct.Descale(o1, Shift));
        }
    }

    /// <summary>Transforms one block to its average, one sample, for a 1/8-scale decode.</summary>
    internal static void Transform1(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<ushort> output, int stride)
    {
        _ = stride;
        output[0] = Clamp(JpegIdct.Descale(coefficients[0] * quant[0], 3));
    }

    /// <summary>Level-shifts by 2048 and clamps to 12 bits.</summary>
    private static ushort Clamp(int value)
    {
        value += Center;
        return (ushort)(value < 0 ? 0 : value > MaxSample ? MaxSample : value);
    }
}
