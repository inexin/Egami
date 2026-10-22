using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Webp;

/// <summary>
/// AVX2 versions of the encoder's block kernels for two 4 × 4 blocks at once: each 128-bit half of the registers holds one
/// block, and every step (the unpacks, packs and byte shuffles included) stays within its half, so each half computes exactly
/// what the SSE4.1 kernels of <see cref="Vp8TransformSimd"/> compute. The two blocks are either two mode trials of one source
/// block or two blocks side by side; reconstructions always go side by side, 8 bytes a row.
/// </summary>
[SkipLocalsInit]
internal static class Vp8PairSimd
{
    private const int S = Vp8Dsp.Stride;

    /// <summary>Gets whether these kernels run (AVX2).</summary>
    internal static bool IsSupported => Avx2.IsSupported;

    /// <summary>
    /// Two trials of <see cref="Vp8TransformSimd.ReconstructBlock"/>: the source block at <paramref name="s"/> against the
    /// predictions at <paramref name="pa"/> and <paramref name="pb"/>. The levels go to <paramref name="levels"/> (the first trial's
    /// 16, then the second's) and the reconstructions side by side to <paramref name="output"/> at <paramref name="at"/>. Returns
    /// the masks of non-zero levels, the first trial's in the low 16 bits.
    /// </summary>
    internal static int ReconstructTrials(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int pa, int pb, Vp8Matrix m, Span<short> levels, Span<byte> output, int at, out int sseA, out int sseB, out int spectrumA, out int spectrumB)
    {
        _ = src.Slice(s, (3 * S) + 4);
        _ = pred.Slice(pa, (3 * S) + 4);
        _ = pred.Slice(pb, (3 * S) + 4);
        _ = output.Slice(at, (3 * S) + 8);
        _ = levels[31];
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte ra = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), pa);
        ref byte rb = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), pb);
        Vector256<int> s0 = Both(ref rs), s1 = Both(ref Unsafe.Add(ref rs, S)), s2 = Both(ref Unsafe.Add(ref rs, 2 * S)), s3 = Both(ref Unsafe.Add(ref rs, 3 * S));
        Vector256<int> p0 = Pair(ref ra, ref rb), p1 = Pair(ref Unsafe.Add(ref ra, S), ref Unsafe.Add(ref rb, S));
        Vector256<int> p2 = Pair(ref Unsafe.Add(ref ra, 2 * S), ref Unsafe.Add(ref rb, 2 * S)), p3 = Pair(ref Unsafe.Add(ref ra, 3 * S), ref Unsafe.Add(ref rb, 3 * S));
        Forward(s0 - p0, s1 - p1, s2 - p2, s3 - p3, out Vector256<int> c0, out Vector256<int> c1, out Vector256<int> c2, out Vector256<int> c3);
        int mask = Quantize(ref c0, ref c1, ref c2, ref c3, m, ref MemoryMarshal.GetReference(levels));
        Reconstruct(c0, c1, c2, c3, s0, s1, s2, s3, p0, p1, p2, p3, ref Unsafe.Add(ref MemoryMarshal.GetReference(output), at), out sseA, out sseB, out spectrumA, out spectrumB);
        return mask;
    }

    /// <summary>
    /// <see cref="Vp8TransformSimd.ForwardTransform"/> of two blocks side by side: the source at <paramref name="s"/> minus the
    /// prediction at <paramref name="p"/>, 8 bytes a row; the coefficients go to <paramref name="output"/>, the left block's 16 first.
    /// </summary>
    internal static void ForwardTransform(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Span<short> output)
    {
        _ = src.Slice(s, (3 * S) + 8);
        _ = pred.Slice(p, (3 * S) + 8);
        _ = output[31];
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte rp = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), p);
        Forward(
            Load8(ref rs) - Load8(ref rp),
            Load8(ref Unsafe.Add(ref rs, S)) - Load8(ref Unsafe.Add(ref rp, S)),
            Load8(ref Unsafe.Add(ref rs, 2 * S)) - Load8(ref Unsafe.Add(ref rp, 2 * S)),
            Load8(ref Unsafe.Add(ref rs, 3 * S)) - Load8(ref Unsafe.Add(ref rp, 3 * S)),
            out Vector256<int> c0,
            out Vector256<int> c1,
            out Vector256<int> c2,
            out Vector256<int> c3);

        // Packed, each half holds its block's rows 0–1 (low) or 2–3 (high); the values fit 16 bits.
        Vector256<short> rows01 = Avx2.PackSignedSaturate(c0, c1), rows23 = Avx2.PackSignedSaturate(c2, c3);
        ref short o = ref MemoryMarshal.GetReference(output);
        rows01.GetLower().StoreUnsafe(ref o);
        rows23.GetLower().StoreUnsafe(ref o, 8);
        rows01.GetUpper().StoreUnsafe(ref o, 16);
        rows23.GetUpper().StoreUnsafe(ref o, 24);
    }

    /// <summary>
    /// <see cref="Vp8TransformSimd.TransformBins"/> of two blocks side by side (8 bytes a row): the left block's 16 bins, then
    /// the right block's.
    /// </summary>
    internal static void TransformBins(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Span<byte> bins)
    {
        _ = src.Slice(s, (3 * S) + 8);
        _ = pred.Slice(p, (3 * S) + 8);
        _ = bins[31];
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte rp = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), p);
        Forward(
            Load8(ref rs) - Load8(ref rp),
            Load8(ref Unsafe.Add(ref rs, S)) - Load8(ref Unsafe.Add(ref rp, S)),
            Load8(ref Unsafe.Add(ref rs, 2 * S)) - Load8(ref Unsafe.Add(ref rp, 2 * S)),
            Load8(ref Unsafe.Add(ref rs, 3 * S)) - Load8(ref Unsafe.Add(ref rp, 3 * S)),
            out Vector256<int> c0,
            out Vector256<int> c1,
            out Vector256<int> c2,
            out Vector256<int> c3);
        Vector256<int> last = Vector256.Create(31);
        Vector256<short> low = Avx2.PackSignedSaturate(Vector256.Min(Vector256.Abs(c0) >> 3, last), Vector256.Min(Vector256.Abs(c1) >> 3, last));
        Vector256<short> high = Avx2.PackSignedSaturate(Vector256.Min(Vector256.Abs(c2) >> 3, last), Vector256.Min(Vector256.Abs(c3) >> 3, last));

        // In each half, the block's 16 bins in raster order.
        Avx2.PackUnsignedSaturate(low, high).StoreUnsafe(ref MemoryMarshal.GetReference(bins));
    }

    /// <summary>
    /// Quantizes and reconstructs two blocks side by side from their coefficients (<paramref name="coefficients"/>, the left
    /// block's 16 first, in raster order): the levels go to <paramref name="levels"/>, the coefficients dequantized, and the
    /// prediction at <paramref name="p"/> plus their inverse DCT to <paramref name="output"/> at <paramref name="at"/>, measured
    /// against the source at <paramref name="s"/> (all 8 bytes a row). With <paramref name="dcGiven"/>, coefficient 0 is not
    /// quantized (its level is 0) and the reconstruction takes it as given: the blocks of a 16 × 16 prediction, whose DCs are
    /// coded in the Y2 block. Returns the masks of non-zero levels, the left block's in the low 16 bits.
    /// </summary>
    internal static int ReconstructCoefficients(ReadOnlySpan<short> coefficients, bool dcGiven, ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Vp8Matrix m, Span<short> levels, Span<byte> output, int at, out int sseA, out int sseB, out int spectrumA, out int spectrumB)
    {
        _ = coefficients[31];
        _ = src.Slice(s, (3 * S) + 8);
        _ = pred.Slice(p, (3 * S) + 8);
        _ = output.Slice(at, (3 * S) + 8);
        _ = levels[31];
        ref short c = ref MemoryMarshal.GetReference(coefficients);
        Vector256<int> c0 = Rows(ref c, 0), c1 = Rows(ref c, 4), c2 = Rows(ref c, 8), c3 = Rows(ref c, 12);
        Vector256<int> dc = Vector256<int>.Zero;
        if (dcGiven)
        {
            Vector256<int> first = Vector256.Create(-1, 0, 0, 0, -1, 0, 0, 0);
            dc = c0 & first;
            c0 = Vector256.AndNot(c0, first);
        }

        int mask = Quantize(ref c0, ref c1, ref c2, ref c3, m, ref MemoryMarshal.GetReference(levels));
        ref byte rs = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
        ref byte rp = ref Unsafe.Add(ref MemoryMarshal.GetReference(pred), p);
        Vector256<int> s0 = Load8(ref rs), s1 = Load8(ref Unsafe.Add(ref rs, S)), s2 = Load8(ref Unsafe.Add(ref rs, 2 * S)), s3 = Load8(ref Unsafe.Add(ref rs, 3 * S));
        Vector256<int> p0 = Load8(ref rp), p1 = Load8(ref Unsafe.Add(ref rp, S)), p2 = Load8(ref Unsafe.Add(ref rp, 2 * S)), p3 = Load8(ref Unsafe.Add(ref rp, 3 * S));

        // A given coefficient 0 quantized to 0 (its sharpening is 0), so the DC goes back in by an or.
        Reconstruct(c0 | dc, c1, c2, c3, s0, s1, s2, s3, p0, p1, p2, p3, ref Unsafe.Add(ref MemoryMarshal.GetReference(output), at), out sseA, out sseB, out spectrumA, out spectrumB);
        return mask;
    }

    /// <summary>The forward DCT of rows of differences in each half (<see cref="Vp8TransformSimd"/>'s ForwardRows).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Forward(Vector256<int> c0, Vector256<int> c1, Vector256<int> c2, Vector256<int> c3, out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3)
    {
        Transpose(ref c0, ref c1, ref c2, ref c3);
        Vector256<int> a0 = c0 + c3, a1 = c1 + c2, a2 = c1 - c2, a3 = c0 - c3;
        Vector256<int> t0 = (a0 + a1) << 3;
        Vector256<int> t1 = ((a2 * 2217) + (a3 * 5352) + Vector256.Create(1812)) >> 9;
        Vector256<int> t2 = (a0 - a1) << 3;
        Vector256<int> t3 = ((a3 * 2217) - (a2 * 5352) + Vector256.Create(937)) >> 9;
        Transpose(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t3;
        a1 = t1 + t2;
        a2 = t1 - t2;
        a3 = t0 - t3;
        Vector256<int> seven = Vector256.Create(7);
        o0 = (a0 + a1 + seven) >> 4;
        o1 = (((a2 * 2217) + (a3 * 5352) + Vector256.Create(12000)) >> 16) + (Vector256.Create(1) & ~Vector256.Equals(a3, Vector256<int>.Zero));
        o2 = (a0 - a1 + seven) >> 4;
        o3 = ((a3 * 2217) - (a2 * 5352) + Vector256.Create(51000)) >> 16;
    }

    /// <summary>
    /// Quantizes the rows of each half (<see cref="Vp8TransformSimd"/>'s QuantizeRows): the levels go out in zigzag order, the
    /// low half's 16 then the high half's, and the rows become the dequantized values. Returns the masks of non-zero levels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quantize(ref Vector256<int> c0, ref Vector256<int> c1, ref Vector256<int> c2, ref Vector256<int> c3, Vp8Matrix m, ref short levels)
    {
        ref int sharpen = ref MemoryMarshal.GetArrayDataReference(m.Sharpen);
        Vector256<int> acIq = Vector256.Create((int)m.IQ[1]), acBias = Vector256.Create((int)m.Bias[1]), acThresh = Vector256.Create((int)m.ZThresh[1]), acQ = Vector256.Create(m.Q[1]);
        Vector256<int> first = Vector256.Create(-1, 0, 0, 0, -1, 0, 0, 0);
        Vector256<int> firstIq = Vector256.ConditionalSelect(first, Vector256.Create((int)m.IQ[0]), acIq);
        Vector256<int> firstBias = Vector256.ConditionalSelect(first, Vector256.Create((int)m.Bias[0]), acBias);
        Vector256<int> firstThresh = Vector256.ConditionalSelect(first, Vector256.Create((int)m.ZThresh[0]), acThresh);
        Vector256<int> firstQ = Vector256.ConditionalSelect(first, Vector256.Create(m.Q[0]), acQ);
        Vector256<int> max = Vector256.Create(Vp8Costs.MaxLevel);
        Vector256<int> level0 = QuantizeRow(ref c0, Both(ref sharpen), firstIq, firstBias, firstThresh, firstQ, max);
        Vector256<int> level1 = QuantizeRow(ref c1, Both(ref Unsafe.Add(ref sharpen, 4)), acIq, acBias, acThresh, acQ, max);
        Vector256<int> level2 = QuantizeRow(ref c2, Both(ref Unsafe.Add(ref sharpen, 8)), acIq, acBias, acThresh, acQ, max);
        Vector256<int> level3 = QuantizeRow(ref c3, Both(ref Unsafe.Add(ref sharpen, 12)), acIq, acBias, acThresh, acQ, max);
        Vector256<short> l03 = Avx2.PackSignedSaturate(level0, level1);
        Vector256<short> l47 = Avx2.PackSignedSaturate(level2, level3);

        // Raster to zigzag in each half, as Vp8TransformSimd's.
        Vector256<byte> lowFromLow = Vector256.Create((byte)0, 1, 2, 3, 8, 9, 0xFF, 0xFF, 10, 11, 4, 5, 6, 7, 12, 13, 0, 1, 2, 3, 8, 9, 0xFF, 0xFF, 10, 11, 4, 5, 6, 7, 12, 13);
        Vector256<byte> lowFromHigh = Vector256.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Vector256<byte> highFromLow = Vector256.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 14, 15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 14, 15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Vector256<byte> highFromHigh = Vector256.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, 0xFF, 0xFF, 6, 7, 12, 13, 14, 15, 2, 3, 8, 9, 10, 11, 4, 5, 0xFF, 0xFF, 6, 7, 12, 13, 14, 15);
        Vector256<short> z0 = (Avx2.Shuffle(l03.AsByte(), lowFromLow) | Avx2.Shuffle(l47.AsByte(), lowFromHigh)).AsInt16();
        Vector256<short> z1 = (Avx2.Shuffle(l03.AsByte(), highFromLow) | Avx2.Shuffle(l47.AsByte(), highFromHigh)).AsInt16();
        z0.GetLower().StoreUnsafe(ref levels);
        z1.GetLower().StoreUnsafe(ref levels, 8);
        z0.GetUpper().StoreUnsafe(ref levels, 16);
        z1.GetUpper().StoreUnsafe(ref levels, 24);
        return ~Avx2.MoveMask(Vector256.Equals(Avx2.PackSignedSaturate(l03, l47), Vector256<sbyte>.Zero).AsByte());
    }

    /// <summary>
    /// Adds the inverse DCT of the dequantized rows (as the 16-bit coefficients the scalar path keeps) to the prediction rows,
    /// stores the pixels side by side, and measures each half against the source rows: squared error and weighted Hadamard magnitude.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reconstruct(Vector256<int> c0, Vector256<int> c1, Vector256<int> c2, Vector256<int> c3, Vector256<int> s0, Vector256<int> s1, Vector256<int> s2, Vector256<int> s3, Vector256<int> p0, Vector256<int> p1, Vector256<int> p2, Vector256<int> p3, ref byte o, out int sseA, out int sseB, out int spectrumA, out int spectrumB)
    {
        Vector256<int> i0 = (c0 << 16) >> 16, i1 = (c1 << 16) >> 16, i2 = (c2 << 16) >> 16, i3 = (c3 << 16) >> 16;
        Vector256<int> a = i0 + i2, b = i0 - i2;
        Vector256<int> c = Mul2(i1) - Mul1(i3), d = Mul1(i1) + Mul2(i3);
        Vector256<int> t0 = a + d, t1 = b + c, t2 = b - c, t3 = a - d;
        Transpose(ref t0, ref t1, ref t2, ref t3);
        Vector256<int> dc = t0 + Vector256.Create(4);
        a = dc + t2;
        b = dc - t2;
        c = Mul2(t1) - Mul1(t3);
        d = Mul1(t1) + Mul2(t3);
        Vector256<int> r0 = (a + d) >> 3, r1 = (b + c) >> 3, r2 = (b - c) >> 3, r3 = (a - d) >> 3;
        Transpose(ref r0, ref r1, ref r2, ref r3);
        Vector256<int> top = Vector256.Create(255);
        r0 = Vector256.Clamp(p0 + r0, Vector256<int>.Zero, top);
        r1 = Vector256.Clamp(p1 + r1, Vector256<int>.Zero, top);
        r2 = Vector256.Clamp(p2 + r2, Vector256<int>.Zero, top);
        r3 = Vector256.Clamp(p3 + r3, Vector256<int>.Zero, top);
        StoreRow(ref o, r0);
        StoreRow(ref Unsafe.Add(ref o, S), r1);
        StoreRow(ref Unsafe.Add(ref o, 2 * S), r2);
        StoreRow(ref Unsafe.Add(ref o, 3 * S), r3);

        Vector256<int> d0 = r0 - s0, d1 = r1 - s1, d2 = r2 - s2, d3 = r3 - s3;
        Vector256<int> squares = (d0 * d0) + (d1 * d1) + (d2 * d2) + (d3 * d3);
        sseA = Vector128.Sum(squares.GetLower());
        sseB = Vector128.Sum(squares.GetUpper());

        // The weighted Hadamard magnitudes (Vp8TransformSimd's HadamardWeightedRows).
        Transpose(ref r0, ref r1, ref r2, ref r3);
        Vector256<int> a0 = r0 + r2, a1 = r1 + r3, a2 = r1 - r3, a3 = r0 - r2;
        t0 = a0 + a1;
        t1 = a3 + a2;
        t2 = a3 - a2;
        t3 = a0 - a1;
        Transpose(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t2;
        a1 = t1 + t3;
        a2 = t1 - t3;
        a3 = t0 - t2;
        Vector256<int> sum = (Vector256.Create(38, 32, 20, 9, 38, 32, 20, 9) * Vector256.Abs(a0 + a1))
            + (Vector256.Create(32, 28, 17, 7, 32, 28, 17, 7) * Vector256.Abs(a3 + a2))
            + (Vector256.Create(20, 17, 10, 4, 20, 17, 10, 4) * Vector256.Abs(a3 - a2))
            + (Vector256.Create(9, 7, 4, 2, 9, 7, 4, 2) * Vector256.Abs(a0 - a1));
        spectrumA = Vector128.Sum(sum.GetLower());
        spectrumB = Vector128.Sum(sum.GetUpper());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load4(ref byte at) => Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref at)).AsByte());

    /// <summary>The 8 bytes at <paramref name="at"/>: the first 4 in the low half, the next 4 in the high half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Load8(ref byte at) => Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref at)).AsByte());

    /// <summary>The 4 bytes at <paramref name="at"/> in both halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Both(ref byte at)
    {
        Vector128<int> v = Load4(ref at);
        return Vector256.Create(v, v);
    }

    /// <summary>The 4 ints at <paramref name="at"/> in both halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Both(ref int at)
    {
        Vector128<int> v = Vector128.LoadUnsafe(ref at);
        return Vector256.Create(v, v);
    }

    /// <summary>The 4 bytes at <paramref name="a"/> in the low half and at <paramref name="b"/> in the high half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Pair(ref byte a, ref byte b) => Vector256.Create(Load4(ref a), Load4(ref b));

    /// <summary>Row <paramref name="at"/> / 4 of both blocks of 16 coefficients at <paramref name="c"/>, widened to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Rows(ref short c, int at) => Vector256.Create(
        Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref c, at)))).AsInt16()),
        Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref c, at + 16)))).AsInt16()));

    /// <summary>Transposes the 4 × 4 block in each half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1), t1 = Avx2.UnpackLow(r2, r3);
        Vector256<int> t2 = Avx2.UnpackHigh(r0, r1), t3 = Avx2.UnpackHigh(r2, r3);
        r0 = Avx2.UnpackLow(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r1 = Avx2.UnpackHigh(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r2 = Avx2.UnpackLow(t2.AsInt64(), t3.AsInt64()).AsInt32();
        r3 = Avx2.UnpackHigh(t2.AsInt64(), t3.AsInt64()).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> QuantizeRow(ref Vector256<int> v, Vector256<int> sharpen, Vector256<int> iq, Vector256<int> bias, Vector256<int> thresh, Vector256<int> q, Vector256<int> max)
    {
        Vector256<int> sign = v >> 31;
        Vector256<int> coeff = Vector256.Abs(v) + sharpen;
        Vector256<int> level = Vector256.Min(((coeff * iq) + bias) >>> Vp8EncDsp.QFix, max);
        level &= Vector256.GreaterThan(coeff, thresh);
        level = (level ^ sign) - sign;
        v = level * q;
        return level;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Mul1(Vector256<int> a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Mul2(Vector256<int> a) => (a * 35468) >> 16;

    /// <summary>Stores a row of each half, already within 0 to 255, as 8 bytes: the low half's 4, then the high half's.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(ref byte at, Vector256<int> v)
    {
        Vector256<short> packed = Avx2.PackSignedSaturate(v, v);
        Vector256<byte> bytes = Avx2.PackUnsignedSaturate(packed, packed);
        Unsafe.WriteUnaligned(ref at, bytes.GetLower().AsUInt32().ToScalar());
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 4), bytes.GetUpper().AsUInt32().ToScalar());
    }
}
