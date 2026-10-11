using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Webp;

/// <summary>
/// SSE4.1 versions of the VP8 block kernels: the forward and inverse DCT, quantization, squared error and the weighted
/// Hadamard transform of the spectral distortion. Each works in 32-bit lanes with the scalar code's exact integer steps, so
/// the results are identical; the 4 × 4 blocks are transposed between the row and column passes.
/// </summary>
[SkipLocalsInit]
internal static class Vp8TransformSimd
{
    private const int S = Vp8Dsp.Stride;

    /// <summary>Gets whether these kernels run (SSE4.1).</summary>
    internal static bool IsSupported => Sse41.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load4(ref byte at) => Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref at)).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3)
    {
        Vector128<int> t0 = Sse2.UnpackLow(r0, r1), t1 = Sse2.UnpackLow(r2, r3);
        Vector128<int> t2 = Sse2.UnpackHigh(r0, r1), t3 = Sse2.UnpackHigh(r2, r3);
        r0 = Sse2.UnpackLow(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r1 = Sse2.UnpackHigh(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r2 = Sse2.UnpackLow(t2.AsInt64(), t3.AsInt64()).AsInt32();
        r3 = Sse2.UnpackHigh(t2.AsInt64(), t3.AsInt64()).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(ref short at, Vector128<int> v) =>
        Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref at), Sse2.PackSignedSaturate(v, v).AsUInt64().ToScalar());

    /// <summary>Stores the low 16 bits of each lane, wrapping as a cast to short does.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreLow16(ref short at, Vector128<int> v) =>
        Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref at), Ssse3.Shuffle(v.AsByte(), Vector128.Create((byte)0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13)).AsUInt64().ToScalar());

    /// <summary>The forward DCT of <see cref="Vp8EncDsp.ForwardTransform"/>.</summary>
    internal static void ForwardTransform(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Span<short> output)
    {
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte rp = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), p);

        // Rows of differences, then columns: c_k holds d_k of each row.
        Vector128<int> c0 = Load4(ref rs) - Load4(ref rp);
        Vector128<int> c1 = Load4(ref Unsafe.Add(ref rs, S)) - Load4(ref Unsafe.Add(ref rp, S));
        Vector128<int> c2 = Load4(ref Unsafe.Add(ref rs, 2 * S)) - Load4(ref Unsafe.Add(ref rp, 2 * S));
        Vector128<int> c3 = Load4(ref Unsafe.Add(ref rs, 3 * S)) - Load4(ref Unsafe.Add(ref rp, 3 * S));
        Transpose(ref c0, ref c1, ref c2, ref c3);
        Vector128<int> a0 = c0 + c3, a1 = c1 + c2, a2 = c1 - c2, a3 = c0 - c3;
        Vector128<int> t0 = (a0 + a1) << 3;
        Vector128<int> t1 = ((a2 * 2217) + (a3 * 5352) + Vector128.Create(1812)) >> 9;
        Vector128<int> t2 = (a0 - a1) << 3;
        Vector128<int> t3 = ((a3 * 2217) - (a2 * 5352) + Vector128.Create(937)) >> 9;

        // t_k holds coefficient k of each row; transposed, r_k holds row k of the coefficients.
        Transpose(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t3;
        a1 = t1 + t2;
        a2 = t1 - t2;
        a3 = t0 - t3;
        ref short o = ref MemoryMarshal.GetReference(output);
        Vector128<int> seven = Vector128.Create(7);
        Store4(ref o, (a0 + a1 + seven) >> 4);
        Vector128<int> nonZero = Vector128.Create(1) & ~Vector128.Equals(a3, Vector128<int>.Zero);
        Store4(ref Unsafe.Add(ref o, 4), (((a2 * 2217) + (a3 * 5352) + Vector128.Create(12000)) >> 16) + nonZero);
        Store4(ref Unsafe.Add(ref o, 8), (a0 - a1 + seven) >> 4);
        Store4(ref Unsafe.Add(ref o, 12), ((a3 * 2217) - (a2 * 5352) + Vector128.Create(51000)) >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul1(Vector128<int> a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul2(Vector128<int> a) => (a * 35468) >> 16;

    /// <summary>The inverse DCT of <see cref="Vp8Dsp.InverseTransformAdd"/>, added to the block with clamping.</summary>
    internal static void InverseTransformAdd(ReadOnlySpan<short> input, Span<byte> buffer, int at)
    {
        ref short ri = ref MemoryMarshal.GetReference(input);
        Vector128<int> i0 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref ri))).AsInt16());
        Vector128<int> i1 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ri, 4)))).AsInt16());
        Vector128<int> i2 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ri, 8)))).AsInt16());
        Vector128<int> i3 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ri, 12)))).AsInt16());

        // Vertical pass over the columns (lanes), giving tmp rows i as columns of the 4 outputs.
        Vector128<int> a = i0 + i2, b = i0 - i2;
        Vector128<int> c = Mul2(i1) - Mul1(i3), d = Mul1(i1) + Mul2(i3);
        Vector128<int> t0 = a + d, t1 = b + c, t2 = b - c, t3 = a - d;

        // tmp[i * 4 + k] = t_k[i]; the second pass reads tmp[k * 4 + i] for lane i: transpose.
        Transpose(ref t0, ref t1, ref t2, ref t3);
        Vector128<int> dc = t0 + Vector128.Create(4);
        a = dc + t2;
        b = dc - t2;
        c = Mul2(t1) - Mul1(t3);
        d = Mul1(t1) + Mul2(t3);
        Vector128<int> r0 = (a + d) >> 3, r1 = (b + c) >> 3, r2 = (b - c) >> 3, r3 = (a - d) >> 3;

        // r_k holds output column k of each row; transpose to rows and add to the pixels.
        Transpose(ref r0, ref r1, ref r2, ref r3);
        ref byte row = ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), at);
        AddRow(ref row, r0);
        AddRow(ref Unsafe.Add(ref row, S), r1);
        AddRow(ref Unsafe.Add(ref row, 2 * S), r2);
        AddRow(ref Unsafe.Add(ref row, 3 * S), r3);

        static void AddRow(ref byte at, Vector128<int> residual)
        {
            Vector128<int> sum = Vector128.Clamp(Load4(ref at) + residual, Vector128<int>.Zero, Vector128.Create(255));
            Vector128<short> packed = Sse2.PackSignedSaturate(sum, sum);
            Unsafe.WriteUnaligned(ref at, Sse2.PackUnsignedSaturate(packed, packed).AsUInt32().ToScalar());
        }
    }

    /// <summary>
    /// <see cref="Vp8EncDsp.Quantize"/> for a whole block: the levels go out in zigzag order and the coefficients become their
    /// dequantized values. Coefficient 0 must already be 0 when the block's DC is coded elsewhere.
    /// </summary>
    internal static bool Quantize(Span<short> coefficients, Span<short> levels, Vp8Matrix m)
    {
        ref short c = ref MemoryMarshal.GetReference(coefficients);
        ref int sharpen = ref MemoryArrays.Ref(m.Sharpen);
        Vector128<int> acIq = Vector128.Create((int)m.IQ[1]), acBias = Vector128.Create((int)m.Bias[1]), acThresh = Vector128.Create((int)m.ZThresh[1]), acQ = Vector128.Create(m.Q[1]);
        Vector128<int> firstIq = acIq.WithElement(0, (int)m.IQ[0]), firstBias = acBias.WithElement(0, (int)m.Bias[0]);
        Vector128<int> firstThresh = acThresh.WithElement(0, (int)m.ZThresh[0]), firstQ = acQ.WithElement(0, m.Q[0]);
        Vector128<int> max = Vector128.Create(Vp8Costs.MaxLevel);
        Vector128<short> l03 = default, l47 = default;
        Vector128<int> any = Vector128<int>.Zero;
        for (int g = 0; g < 4; g++)
        {
            ref short at = ref Unsafe.Add(ref c, g * 4);
            Vector128<int> v = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref at))).AsInt16());
            Vector128<int> sign = v >> 31;
            Vector128<int> coeff = Vector128.Abs(v) + Vector128.LoadUnsafe(ref Unsafe.Add(ref sharpen, g * 4));
            (Vector128<int> iq, Vector128<int> bias, Vector128<int> thresh, Vector128<int> q) = g == 0 ? (firstIq, firstBias, firstThresh, firstQ) : (acIq, acBias, acThresh, acQ);
            Vector128<int> level = Vector128.Min(((coeff * iq) + bias) >>> Vp8EncDsp.QFix, max);
            level &= Vector128.GreaterThan(coeff, thresh);
            level = (level ^ sign) - sign;
            any |= level;
            StoreLow16(ref at, level * q);
            Vector128<short> packed = Sse2.PackSignedSaturate(level, level);
            switch (g)
            {
                case 0: l03 = packed; break;
                case 1: l03 = Sse2.UnpackLow(l03.AsInt64(), packed.AsInt64()).AsInt16(); break;
                case 2: l47 = packed; break;
                default: l47 = Sse2.UnpackLow(l47.AsInt64(), packed.AsInt64()).AsInt16(); break;
            }
        }

        // Raster to zigzag: 0 1 4 8 5 2 3 6 | 9 12 13 10 7 11 14 15, from raster 0–7 (low) and 8–15 (high).
        Vector128<byte> lowFromLow = Vector128.Create((byte)0, 1, 2, 3, 8, 9, 0xFF, 0xFF, 10, 11, 4, 5, 6, 7, 12, 13);
        Vector128<byte> lowFromHigh = Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Vector128<byte> highFromLow = Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 14, 15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Vector128<byte> highFromHigh = Vector128.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, 0xFF, 0xFF, 6, 7, 12, 13, 14, 15);
        Vector128<short> z0 = (Ssse3.Shuffle(l03.AsByte(), lowFromLow) | Ssse3.Shuffle(l47.AsByte(), lowFromHigh)).AsInt16();
        Vector128<short> z1 = (Ssse3.Shuffle(l03.AsByte(), highFromLow) | Ssse3.Shuffle(l47.AsByte(), highFromHigh)).AsInt16();
        ref short l = ref MemoryMarshal.GetReference(levels);
        z0.StoreUnsafe(ref l);
        z1.StoreUnsafe(ref Unsafe.Add(ref l, 8));
        return any != Vector128<int>.Zero;
    }

    /// <summary>The sum of squared differences over rows of 16 or 8 bytes (<paramref name="w"/>) at offset <paramref name="at"/>.</summary>
    internal static int Sse(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int at, int w, int h)
    {
        ref byte ra = ref Unsafe.Add(ref MemoryMarshal.GetReference(a), at);
        ref byte rb = ref Unsafe.Add(ref MemoryMarshal.GetReference(b), at);
        Vector128<int> sum = Vector128<int>.Zero;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x += 8)
            {
                Vector128<short> va = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref ra, x))).AsByte());
                Vector128<short> vb = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref rb, x))).AsByte());
                Vector128<short> d = va - vb;
                sum += Sse2.MultiplyAddAdjacent(d, d);
            }

            ra = ref Unsafe.Add(ref ra, S);
            rb = ref Unsafe.Add(ref rb, S);
        }

        return Vector128.Sum(sum);
    }

    /// <summary>The sum of squared differences of two 4 × 4 blocks at their own offsets.</summary>
    internal static int Sse4x4(ReadOnlySpan<byte> a, int ao, ReadOnlySpan<byte> b, int bo)
    {
        ref byte ra = ref Unsafe.Add(ref MemoryMarshal.GetReference(a), ao);
        ref byte rb = ref Unsafe.Add(ref MemoryMarshal.GetReference(b), bo);
        Vector128<int> d0 = Load4(ref ra) - Load4(ref rb);
        Vector128<int> d1 = Load4(ref Unsafe.Add(ref ra, S)) - Load4(ref Unsafe.Add(ref rb, S));
        Vector128<int> d2 = Load4(ref Unsafe.Add(ref ra, 2 * S)) - Load4(ref Unsafe.Add(ref rb, 2 * S));
        Vector128<int> d3 = Load4(ref Unsafe.Add(ref ra, 3 * S)) - Load4(ref Unsafe.Add(ref rb, 3 * S));
        return Vector128.Sum((d0 * d0) + (d1 * d1) + (d2 * d2) + (d3 * d3));
    }

    /// <summary>The weighted Hadamard magnitude of <see cref="Vp8EncDsp.Disto4x4"/> for one 4 × 4 block.</summary>
    internal static int HadamardWeighted(ReadOnlySpan<byte> input, int at)
    {
        ref byte r = ref Unsafe.Add(ref MemoryMarshal.GetReference(input), at);
        Vector128<int> c0 = Load4(ref r), c1 = Load4(ref Unsafe.Add(ref r, S)), c2 = Load4(ref Unsafe.Add(ref r, 2 * S)), c3 = Load4(ref Unsafe.Add(ref r, 3 * S));
        Transpose(ref c0, ref c1, ref c2, ref c3);
        Vector128<int> a0 = c0 + c2, a1 = c1 + c3, a2 = c1 - c3, a3 = c0 - c2;
        Vector128<int> t0 = a0 + a1, t1 = a3 + a2, t2 = a3 - a2, t3 = a0 - a1;
        Transpose(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t2;
        a1 = t1 + t3;
        a2 = t1 - t3;
        a3 = t0 - t2;
        Vector128<int> sum = (Vector128.Create(38, 32, 20, 9) * Vector128.Abs(a0 + a1))
            + (Vector128.Create(32, 28, 17, 7) * Vector128.Abs(a3 + a2))
            + (Vector128.Create(20, 17, 10, 4) * Vector128.Abs(a3 - a2))
            + (Vector128.Create(9, 7, 4, 2) * Vector128.Abs(a0 - a1));
        return Vector128.Sum(sum);
    }

    private static class MemoryArrays
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref int Ref(int[] array) => ref MemoryMarshal.GetArrayDataReference(array);
    }
}
