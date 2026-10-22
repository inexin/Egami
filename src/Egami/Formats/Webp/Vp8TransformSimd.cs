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
        ForwardRows(
            Load4(ref rs) - Load4(ref rp),
            Load4(ref Unsafe.Add(ref rs, S)) - Load4(ref Unsafe.Add(ref rp, S)),
            Load4(ref Unsafe.Add(ref rs, 2 * S)) - Load4(ref Unsafe.Add(ref rp, 2 * S)),
            Load4(ref Unsafe.Add(ref rs, 3 * S)) - Load4(ref Unsafe.Add(ref rp, 3 * S)),
            out Vector128<int> o0,
            out Vector128<int> o1,
            out Vector128<int> o2,
            out Vector128<int> o3);
        ref short o = ref MemoryMarshal.GetReference(output);
        Store4(ref o, o0);
        Store4(ref Unsafe.Add(ref o, 4), o1);
        Store4(ref Unsafe.Add(ref o, 8), o2);
        Store4(ref Unsafe.Add(ref o, 12), o3);
    }

    /// <summary>The forward DCT of four rows of differences; the outputs are the rows of coefficients, in 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ForwardRows(Vector128<int> c0, Vector128<int> c1, Vector128<int> c2, Vector128<int> c3, out Vector128<int> o0, out Vector128<int> o1, out Vector128<int> o2, out Vector128<int> o3)
    {
        // Rows of differences, then columns: c_k holds d_k of each row.
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
        Vector128<int> seven = Vector128.Create(7);
        o0 = (a0 + a1 + seven) >> 4;
        Vector128<int> nonZero = Vector128.Create(1) & ~Vector128.Equals(a3, Vector128<int>.Zero);
        o1 = (((a2 * 2217) + (a3 * 5352) + Vector128.Create(12000)) >> 16) + nonZero;
        o2 = (a0 - a1 + seven) >> 4;
        o3 = ((a3 * 2217) - (a2 * 5352) + Vector128.Create(51000)) >> 16;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul1(Vector128<int> a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul2(Vector128<int> a) => (a * 35468) >> 16;

    /// <summary>The inverse DCT of <see cref="Vp8Dsp.InverseTransformAdd"/>, added to the block with clamping.</summary>
    internal static void InverseTransformAdd(ReadOnlySpan<short> input, Span<byte> buffer, int at, int stride = S)
    {
        _ = input[15];
        _ = buffer.Slice(at + (3 * stride), 4);
        InverseTransformAdd(ref MemoryMarshal.GetReference(input), ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), at), stride);
    }

    /// <summary>
    /// Adds the inverse DCT of two side-by-side blocks (32 coefficients at <paramref name="input"/>, the left block's first) to
    /// the 8 × 4 pixels at <paramref name="dst"/>, both blocks at once in 16-bit lanes, as libwebp's TransformTwo and libvpx's
    /// idct_dequant_full_2x do. pmulhw gives the scalar (a × k) >> 16 exactly (35468, past 16 bits, as a × (35468 − 65536) and a
    /// added). While every coefficient is within ±2048 no lane leaves 16 bits (the largest intermediate is about 30,330), so the
    /// result is the scalar transform's; a pair with a larger coefficient goes through the 32-bit kernel instead.
    /// </summary>
    internal static void InverseTransformAddTwo(ref short input, ref byte dst, int stride)
    {
        Vector128<short> q0 = Vector128.LoadUnsafe(ref input), q1 = Vector128.LoadUnsafe(ref input, 8);
        Vector128<short> q2 = Vector128.LoadUnsafe(ref input, 16), q3 = Vector128.LoadUnsafe(ref input, 24);
        Vector128<ushort> largest = Vector128.Max(Vector128.Max(Vector128.Abs(q0).AsUInt16(), Vector128.Abs(q1).AsUInt16()), Vector128.Max(Vector128.Abs(q2).AsUInt16(), Vector128.Abs(q3).AsUInt16()));
        if (Vector128.GreaterThanAny(largest, Vector128.Create((ushort)2048)))
        {
            InverseTransformAdd(ref input, ref dst, stride);
            InverseTransformAdd(ref Unsafe.Add(ref input, 16), ref Unsafe.Add(ref dst, 4), stride);
            return;
        }

        // Row k of both blocks: the left block's in lanes 0 to 3, the right block's in lanes 4 to 7.
        Vector128<short> i0 = Sse2.UnpackLow(q0.AsInt64(), q2.AsInt64()).AsInt16(), i1 = Sse2.UnpackHigh(q0.AsInt64(), q2.AsInt64()).AsInt16();
        Vector128<short> i2 = Sse2.UnpackLow(q1.AsInt64(), q3.AsInt64()).AsInt16(), i3 = Sse2.UnpackHigh(q1.AsInt64(), q3.AsInt64()).AsInt16();

        // The vertical pass over the columns (lanes), as the scalar one.
        Vector128<short> a = i0 + i2, b = i0 - i2;
        Vector128<short> c = Mul2(i1) - Mul1(i3), d = Mul1(i1) + Mul2(i3);
        Vector128<short> t0 = a + d, t1 = b + c, t2 = b - c, t3 = a - d;
        Transpose(ref t0, ref t1, ref t2, ref t3);

        // The horizontal pass, the lanes now the rows; then back to rows of pixels.
        Vector128<short> dc = t0 + Vector128.Create((short)4);
        a = dc + t2;
        b = dc - t2;
        c = Mul2(t1) - Mul1(t3);
        d = Mul1(t1) + Mul2(t3);
        Vector128<short> r0 = (a + d) >> 3, r1 = (b + c) >> 3, r2 = (b - c) >> 3, r3 = (a - d) >> 3;
        Transpose(ref r0, ref r1, ref r2, ref r3);
        AddRow8(ref dst, r0);
        AddRow8(ref Unsafe.Add(ref dst, stride), r1);
        AddRow8(ref Unsafe.Add(ref dst, 2 * stride), r2);
        AddRow8(ref Unsafe.Add(ref dst, 3 * stride), r3);
    }

    /// <summary>
    /// Adds the DC-only transforms of two side-by-side blocks, (<paramref name="dcLeft"/> + 4) >> 3 and
    /// (<paramref name="dcRight"/> + 4) >> 3, to the 8 × 4 pixels at <paramref name="dst"/>, clamped as the scalar code clamps.
    /// </summary>
    internal static void DcAddTwo(short dcLeft, short dcRight, ref byte dst, int stride)
    {
        // Two broadcasts and an unpack; building the vector from halves goes through the stack.
        Vector128<short> add = Sse2.UnpackLow(Vector128.Create((short)((dcLeft + 4) >> 3)).AsInt64(), Vector128.Create((short)((dcRight + 4) >> 3)).AsInt64()).AsInt16();
        AddRow8(ref dst, add);
        AddRow8(ref Unsafe.Add(ref dst, stride), add);
        AddRow8(ref Unsafe.Add(ref dst, 2 * stride), add);
        AddRow8(ref Unsafe.Add(ref dst, 3 * stride), add);
    }

    /// <summary>Adds 8 residuals to the 8 pixels at <paramref name="at"/>; packuswb clamps to 0 to 255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRow8(ref byte at, Vector128<short> residual)
    {
        Vector128<short> pixels = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref at)).AsByte(), Vector128<byte>.Zero).AsInt16();
        Unsafe.WriteUnaligned(ref at, Sse2.PackUnsignedSaturate(pixels + residual, pixels + residual).AsUInt64().ToScalar());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Mul1(Vector128<short> a) => Sse2.MultiplyHigh(a, Vector128.Create((short)20091)) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Mul2(Vector128<short> a) => Sse2.MultiplyHigh(a, Vector128.Create(unchecked((short)35468))) + a;

    /// <summary>Transposes the 4 × 4 block in lanes 0 to 3 of the four vectors, and the one in lanes 4 to 7.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector128<short> r0, ref Vector128<short> r1, ref Vector128<short> r2, ref Vector128<short> r3)
    {
        Vector128<short> left01 = Sse2.UnpackLow(r0, r1), left23 = Sse2.UnpackLow(r2, r3);
        Vector128<short> right01 = Sse2.UnpackHigh(r0, r1), right23 = Sse2.UnpackHigh(r2, r3);
        Vector128<int> left0 = Sse2.UnpackLow(left01.AsInt32(), left23.AsInt32()), left1 = Sse2.UnpackHigh(left01.AsInt32(), left23.AsInt32());
        Vector128<int> right0 = Sse2.UnpackLow(right01.AsInt32(), right23.AsInt32()), right1 = Sse2.UnpackHigh(right01.AsInt32(), right23.AsInt32());
        r0 = Sse2.UnpackLow(left0.AsInt64(), right0.AsInt64()).AsInt16();
        r1 = Sse2.UnpackHigh(left0.AsInt64(), right0.AsInt64()).AsInt16();
        r2 = Sse2.UnpackLow(left1.AsInt64(), right1.AsInt64()).AsInt16();
        r3 = Sse2.UnpackHigh(left1.AsInt64(), right1.AsInt64()).AsInt16();
    }

    private static void InverseTransformAdd(ref short ri, ref byte row, int stride)
    {
        Vector128<int> i0 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref ri))).AsInt16());
        Vector128<int> i1 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ri, 4)))).AsInt16());
        Vector128<int> i2 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ri, 8)))).AsInt16());
        Vector128<int> i3 = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ri, 12)))).AsInt16());
        InverseRows(i0, i1, i2, i3, out Vector128<int> r0, out Vector128<int> r1, out Vector128<int> r2, out Vector128<int> r3);
        AddRow(ref row, r0);
        AddRow(ref Unsafe.Add(ref row, stride), r1);
        AddRow(ref Unsafe.Add(ref row, 2 * stride), r2);
        AddRow(ref Unsafe.Add(ref row, 3 * stride), r3);

        static void AddRow(ref byte at, Vector128<int> residual) =>
            Store4Bytes(ref at, Vector128.Clamp(Load4(ref at) + residual, Vector128<int>.Zero, Vector128.Create(255)));
    }

    /// <summary>The inverse DCT of four rows of coefficients; the outputs are the rows of residuals, in 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InverseRows(Vector128<int> i0, Vector128<int> i1, Vector128<int> i2, Vector128<int> i3, out Vector128<int> r0, out Vector128<int> r1, out Vector128<int> r2, out Vector128<int> r3)
    {
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
        r0 = (a + d) >> 3;
        r1 = (b + c) >> 3;
        r2 = (b - c) >> 3;
        r3 = (a - d) >> 3;

        // r_k holds output column k of each row; transpose to rows.
        Transpose(ref r0, ref r1, ref r2, ref r3);
    }

    /// <summary>
    /// <see cref="Vp8EncDsp.Quantize"/> for a whole block: the levels go out in zigzag order and the coefficients become their
    /// dequantized values. Coefficient 0 must already be 0 when the block's DC is coded elsewhere.
    /// </summary>
    internal static bool Quantize(Span<short> coefficients, Span<short> levels, Vp8Matrix m)
    {
        _ = coefficients[15];
        _ = levels[15];
        ref short c = ref MemoryMarshal.GetReference(coefficients);
        Vector128<int> r0 = Load4Shorts(ref c), r1 = Load4Shorts(ref Unsafe.Add(ref c, 4)), r2 = Load4Shorts(ref Unsafe.Add(ref c, 8)), r3 = Load4Shorts(ref Unsafe.Add(ref c, 12));
        bool any = QuantizeRows(ref r0, ref r1, ref r2, ref r3, m, ref MemoryMarshal.GetReference(levels)) != 0;
        StoreLow16(ref c, r0);
        StoreLow16(ref Unsafe.Add(ref c, 4), r1);
        StoreLow16(ref Unsafe.Add(ref c, 8), r2);
        StoreLow16(ref Unsafe.Add(ref c, 12), r3);
        return any;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load4Shorts(ref short at) => Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref at))).AsInt16());

    /// <summary>
    /// Quantizes four rows of coefficients: the levels go out in zigzag order at <paramref name="levels"/>, and each row becomes
    /// its dequantized values (level × step, in 32 bits). Returns a mask of the non-zero levels, bit k for coefficient k in raster order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int QuantizeRows(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3, Vp8Matrix m, ref short levels)
    {
        ref int sharpen = ref MemoryArrays.Ref(m.Sharpen);
        Vector128<int> acIq = Vector128.Create((int)m.IQ[1]), acBias = Vector128.Create((int)m.Bias[1]), acThresh = Vector128.Create((int)m.ZThresh[1]), acQ = Vector128.Create(m.Q[1]);
        Vector128<int> firstIq = acIq.WithElement(0, (int)m.IQ[0]), firstBias = acBias.WithElement(0, (int)m.Bias[0]);
        Vector128<int> firstThresh = acThresh.WithElement(0, (int)m.ZThresh[0]), firstQ = acQ.WithElement(0, m.Q[0]);
        Vector128<int> max = Vector128.Create(Vp8Costs.MaxLevel);
        Vector128<int> level0 = QuantizeRow(ref r0, Vector128.LoadUnsafe(ref sharpen), firstIq, firstBias, firstThresh, firstQ, max);
        Vector128<int> level1 = QuantizeRow(ref r1, Vector128.LoadUnsafe(ref Unsafe.Add(ref sharpen, 4)), acIq, acBias, acThresh, acQ, max);
        Vector128<int> level2 = QuantizeRow(ref r2, Vector128.LoadUnsafe(ref Unsafe.Add(ref sharpen, 8)), acIq, acBias, acThresh, acQ, max);
        Vector128<int> level3 = QuantizeRow(ref r3, Vector128.LoadUnsafe(ref Unsafe.Add(ref sharpen, 12)), acIq, acBias, acThresh, acQ, max);
        Vector128<short> l03 = Sse2.PackSignedSaturate(level0, level1);
        Vector128<short> l47 = Sse2.PackSignedSaturate(level2, level3);

        // Raster to zigzag: 0 1 4 8 5 2 3 6 | 9 12 13 10 7 11 14 15, from raster 0–7 (low) and 8–15 (high).
        Vector128<byte> lowFromLow = Vector128.Create((byte)0, 1, 2, 3, 8, 9, 0xFF, 0xFF, 10, 11, 4, 5, 6, 7, 12, 13);
        Vector128<byte> lowFromHigh = Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Vector128<byte> highFromLow = Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 14, 15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Vector128<byte> highFromHigh = Vector128.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, 0xFF, 0xFF, 6, 7, 12, 13, 14, 15);
        Vector128<short> z0 = (Ssse3.Shuffle(l03.AsByte(), lowFromLow) | Ssse3.Shuffle(l47.AsByte(), lowFromHigh)).AsInt16();
        Vector128<short> z1 = (Ssse3.Shuffle(l03.AsByte(), highFromLow) | Ssse3.Shuffle(l47.AsByte(), highFromHigh)).AsInt16();
        z0.StoreUnsafe(ref levels);
        z1.StoreUnsafe(ref Unsafe.Add(ref levels, 8));
        return ~Sse2.MoveMask(Vector128.Equals(Sse2.PackSignedSaturate(l03, l47), Vector128<sbyte>.Zero).AsByte()) & 0xFFFF;

        static Vector128<int> QuantizeRow(ref Vector128<int> v, Vector128<int> sharpen, Vector128<int> iq, Vector128<int> bias, Vector128<int> thresh, Vector128<int> q, Vector128<int> max)
        {
            Vector128<int> sign = v >> 31;
            Vector128<int> coeff = Vector128.Abs(v) + sharpen;
            Vector128<int> level = Vector128.Min(((coeff * iq) + bias) >>> Vp8EncDsp.QFix, max);
            level &= Vector128.GreaterThan(coeff, thresh);
            level = (level ^ sign) - sign;
            v = level * q;
            return level;
        }
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
        return HadamardWeightedRows(Load4(ref r), Load4(ref Unsafe.Add(ref r, S)), Load4(ref Unsafe.Add(ref r, 2 * S)), Load4(ref Unsafe.Add(ref r, 3 * S)));
    }

    /// <summary>The weighted Hadamard magnitude of a 4 × 4 block given as four rows in 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HadamardWeightedRows(Vector128<int> c0, Vector128<int> c1, Vector128<int> c2, Vector128<int> c3)
    {
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

    /// <summary>
    /// One 4 × 4 trial of the encoder's mode search in registers: <see cref="ForwardTransform"/> of the source at
    /// <paramref name="s"/> minus the prediction at <paramref name="p"/>, <see cref="Quantize"/> (the levels go out in zigzag
    /// order), and the reconstruction, the prediction plus the inverse DCT of the dequantized levels, written to
    /// <paramref name="output"/> at <paramref name="at"/>. Returns the mask of non-zero levels (bit k for coefficient k in raster
    /// order), with the squared error of the reconstruction against the source and its weighted Hadamard magnitude, exactly as the
    /// kernels one after the other give them.
    /// </summary>
    internal static int ReconstructBlock(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Vp8Matrix m, Span<short> levels, Span<byte> output, int at, out int sse, out int spectrum)
    {
        _ = src.Slice(s, (3 * S) + 4);
        _ = pred.Slice(p, (3 * S) + 4);
        _ = output.Slice(at, (3 * S) + 4);
        _ = levels[15];
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte rp = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), p);
        Vector128<int> s0 = Load4(ref rs), s1 = Load4(ref Unsafe.Add(ref rs, S)), s2 = Load4(ref Unsafe.Add(ref rs, 2 * S)), s3 = Load4(ref Unsafe.Add(ref rs, 3 * S));
        Vector128<int> p0 = Load4(ref rp), p1 = Load4(ref Unsafe.Add(ref rp, S)), p2 = Load4(ref Unsafe.Add(ref rp, 2 * S)), p3 = Load4(ref Unsafe.Add(ref rp, 3 * S));
        ForwardRows(s0 - p0, s1 - p1, s2 - p2, s3 - p3, out Vector128<int> c0, out Vector128<int> c1, out Vector128<int> c2, out Vector128<int> c3);
        int nz = QuantizeRows(ref c0, ref c1, ref c2, ref c3, m, ref MemoryMarshal.GetReference(levels));
        Reconstruct(c0, c1, c2, c3, s0, s1, s2, s3, p0, p1, p2, p3, ref Unsafe.Add(ref MemoryMarshal.GetReference(output), at), out sse, out spectrum);
        return nz;
    }

    /// <summary>
    /// <see cref="ReconstructBlock"/> for a block of a 16 × 16 prediction, whose DC is coded in the Y2 block: the AC of
    /// <paramref name="coefficients"/> (the forward DCT) is quantized, and the reconstruction takes coefficient 0 as given there
    /// (the block's DC from the inverse Walsh-Hadamard transform).
    /// </summary>
    internal static int ReconstructAcBlock(ReadOnlySpan<short> coefficients, ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Vp8Matrix m, Span<short> levels, Span<byte> output, int at, out int sse, out int spectrum)
    {
        _ = coefficients[15];
        _ = src.Slice(s, (3 * S) + 4);
        _ = pred.Slice(p, (3 * S) + 4);
        _ = output.Slice(at, (3 * S) + 4);
        _ = levels[15];
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte rp = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), p);
        ref short c = ref MemoryMarshal.GetReference(coefficients);
        Vector128<int> c0 = Load4Shorts(ref c), c1 = Load4Shorts(ref Unsafe.Add(ref c, 4)), c2 = Load4Shorts(ref Unsafe.Add(ref c, 8)), c3 = Load4Shorts(ref Unsafe.Add(ref c, 12));
        Vector128<int> dc = c0 & Vector128.Create(-1, 0, 0, 0);
        c0 &= Vector128.Create(0, -1, -1, -1);
        int nz = QuantizeRows(ref c0, ref c1, ref c2, ref c3, m, ref MemoryMarshal.GetReference(levels));
        Vector128<int> s0 = Load4(ref rs), s1 = Load4(ref Unsafe.Add(ref rs, S)), s2 = Load4(ref Unsafe.Add(ref rs, 2 * S)), s3 = Load4(ref Unsafe.Add(ref rs, 3 * S));
        Vector128<int> p0 = Load4(ref rp), p1 = Load4(ref Unsafe.Add(ref rp, S)), p2 = Load4(ref Unsafe.Add(ref rp, 2 * S)), p3 = Load4(ref Unsafe.Add(ref rp, 3 * S));

        // Coefficient 0 quantizes to 0 (its sharpening is 0); the reconstruction takes the given DC there.
        Reconstruct(c0 | dc, c1, c2, c3, s0, s1, s2, s3, p0, p1, p2, p3, ref Unsafe.Add(ref MemoryMarshal.GetReference(output), at), out sse, out spectrum);
        return nz;
    }

    /// <summary>Adds the inverse DCT of dequantized rows to the prediction rows, stores the pixels and measures them against the source rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reconstruct(Vector128<int> c0, Vector128<int> c1, Vector128<int> c2, Vector128<int> c3, Vector128<int> s0, Vector128<int> s1, Vector128<int> s2, Vector128<int> s3, Vector128<int> p0, Vector128<int> p1, Vector128<int> p2, Vector128<int> p3, ref byte o, out int sse, out int spectrum)
    {
        // The dequantized values as the 16-bit coefficients the scalar path keeps.
        InverseRows((c0 << 16) >> 16, (c1 << 16) >> 16, (c2 << 16) >> 16, (c3 << 16) >> 16, out Vector128<int> r0, out Vector128<int> r1, out Vector128<int> r2, out Vector128<int> r3);
        Vector128<int> max = Vector128.Create(255);
        r0 = Vector128.Clamp(p0 + r0, Vector128<int>.Zero, max);
        r1 = Vector128.Clamp(p1 + r1, Vector128<int>.Zero, max);
        r2 = Vector128.Clamp(p2 + r2, Vector128<int>.Zero, max);
        r3 = Vector128.Clamp(p3 + r3, Vector128<int>.Zero, max);
        Store4Bytes(ref o, r0);
        Store4Bytes(ref Unsafe.Add(ref o, S), r1);
        Store4Bytes(ref Unsafe.Add(ref o, 2 * S), r2);
        Store4Bytes(ref Unsafe.Add(ref o, 3 * S), r3);
        Vector128<int> d0 = r0 - s0, d1 = r1 - s1, d2 = r2 - s2, d3 = r3 - s3;
        sse = Vector128.Sum((d0 * d0) + (d1 * d1) + (d2 * d2) + (d3 * d3));
        spectrum = HadamardWeightedRows(r0, r1, r2, r3);
    }

    /// <summary>
    /// The bins of <see cref="Vp8EncDsp.Alpha"/>'s histogram for one block: |coefficient| >> 3, at most 31, for each coefficient of
    /// the forward DCT of the source at <paramref name="s"/> minus the prediction at <paramref name="p"/>, in raster order.
    /// </summary>
    internal static void TransformBins(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Span<byte> bins)
    {
        _ = src.Slice(s, (3 * S) + 4);
        _ = pred.Slice(p, (3 * S) + 4);
        _ = bins[15];
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte rp = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), p);
        ForwardRows(
            Load4(ref rs) - Load4(ref rp),
            Load4(ref Unsafe.Add(ref rs, S)) - Load4(ref Unsafe.Add(ref rp, S)),
            Load4(ref Unsafe.Add(ref rs, 2 * S)) - Load4(ref Unsafe.Add(ref rp, 2 * S)),
            Load4(ref Unsafe.Add(ref rs, 3 * S)) - Load4(ref Unsafe.Add(ref rp, 3 * S)),
            out Vector128<int> o0,
            out Vector128<int> o1,
            out Vector128<int> o2,
            out Vector128<int> o3);
        Vector128<int> last = Vector128.Create(31);
        Vector128<short> low = Sse2.PackSignedSaturate(Vector128.Min(Vector128.Abs(o0) >> 3, last), Vector128.Min(Vector128.Abs(o1) >> 3, last));
        Vector128<short> high = Sse2.PackSignedSaturate(Vector128.Min(Vector128.Abs(o2) >> 3, last), Vector128.Min(Vector128.Abs(o3) >> 3, last));
        Sse2.PackUnsignedSaturate(low, high).StoreUnsafe(ref MemoryMarshal.GetReference(bins));
    }

    /// <summary>Stores lanes already within 0 to 255 as 4 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4Bytes(ref byte at, Vector128<int> v)
    {
        Vector128<short> packed = Sse2.PackSignedSaturate(v, v);
        Unsafe.WriteUnaligned(ref at, Sse2.PackUnsignedSaturate(packed, packed).AsUInt32().ToScalar());
    }

    private static class MemoryArrays
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref int Ref(int[] array) => ref MemoryMarshal.GetArrayDataReference(array);
    }
}
