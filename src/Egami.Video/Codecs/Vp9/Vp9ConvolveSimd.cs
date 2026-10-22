using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The 8-bit form of <see cref="Vp9Convolve{T}"/> at a step of 16 (no scaling) in 16-bit lanes, eight or sixteen pixels at a
/// time, with the same values. A pass's sum of eight taps times pixels lies between -13770 and 46410 for every VP9 filter, so
/// with 16384 added it fits an unsigned 16-bit lane exactly, whatever the lanes wrap through on the way; and since 16384 is
/// 128 × 128, the rounded shift by 7 then just takes 128 off before the clamp to 0..255.
/// </summary>
[SkipLocalsInit]
internal static class Vp9ConvolveSimd
{
    private const int Bias = (1 << 14) + (1 << 6);

    /// <summary>Gets whether the vector form runs here; otherwise the scalar one does.</summary>
    public static bool IsSupported => Vector128.IsHardwareAccelerated;

    /// <summary>
    /// Predicts a <paramref name="w"/> by <paramref name="h"/> block (w 4 to 64) as <see cref="Vp9Convolve{T}.Predict"/> does with
    /// both steps 16. <paramref name="mid"/> holds the horizontal pass, 64 bytes a row; <paramref name="average"/> blocks go
    /// through <paramref name="predictTemp"/>, 64 bytes a row.
    /// </summary>
    public static void Predict(ReadOnlySpan<byte> src, int srcOffset, int srcStride, Span<byte> dst, int dstOffset, int dstStride,
        int x0Q4, int y0Q4, int w, int h, bool average, ReadOnlySpan<short> kernel, Span<byte> mid, Span<byte> predictTemp)
    {
        Span<byte> outBuffer = average ? predictTemp : dst;
        int outOffset = average ? 0 : dstOffset;
        int outStride = average ? 64 : dstStride;
        if (x0Q4 != 0 && y0Q4 != 0)
        {
            Horizontal(src, srcOffset - (3 * srcStride), srcStride, mid, 0, 64, kernel.Slice(x0Q4 * 8, 8), w, h + 7);
            Vertical(mid, 3 * 64, 64, outBuffer, outOffset, outStride, kernel.Slice(y0Q4 * 8, 8), w, h);
        }
        else if (x0Q4 != 0)
        {
            Horizontal(src, srcOffset, srcStride, outBuffer, outOffset, outStride, kernel.Slice(x0Q4 * 8, 8), w, h);
        }
        else if (y0Q4 != 0)
        {
            Vertical(src, srcOffset, srcStride, outBuffer, outOffset, outStride, kernel.Slice(y0Q4 * 8, 8), w, h);
        }
        else
        {
            Copy(src, srcOffset, srcStride, outBuffer, outOffset, outStride, w, h);
        }

        if (average)
        {
            Average(predictTemp, dst, dstOffset, dstStride, w, h);
        }
    }

    /// <summary>Copies a whole-pixel block a row at a time, in one or two moves a row.</summary>
    private static void Copy(ReadOnlySpan<byte> src, int srcOffset, int srcStride, Span<byte> dst, int dstOffset, int dstStride, int w, int h)
    {
        if (srcOffset < 0 || srcOffset + ((h - 1) * srcStride) + w > src.Length || dstOffset < 0 || dstOffset + ((h - 1) * dstStride) + w > dst.Length)
        {
            throw new CorruptDataException("VP9", -1, "A prediction block reaches outside its reference.");
        }

        ref byte s = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), srcOffset);
        ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), dstOffset);
        for (int y = 0; y < h; y++)
        {
            ref byte from = ref Unsafe.Add(ref s, y * srcStride);
            ref byte to = ref Unsafe.Add(ref d, y * dstStride);
            switch (w)
            {
                case 4:
                    Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<uint>(ref from));
                    break;
                case 8:
                    Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<ulong>(ref from));
                    break;
                case 16:
                    Vector128.LoadUnsafe(ref from).StoreUnsafe(ref to);
                    break;
                case 32:
                    Vector128.LoadUnsafe(ref from).StoreUnsafe(ref to);
                    Vector128.LoadUnsafe(ref from, 16).StoreUnsafe(ref to, 16);
                    break;
                default:
                    Unsafe.CopyBlockUnaligned(ref to, ref from, (uint)w);
                    break;
            }
        }
    }

    /// <summary>The horizontal pass: rows of <paramref name="w"/> outputs from the eight pixels from 3 left of each.</summary>
    private static void Horizontal(ReadOnlySpan<byte> src, int srcOffset, int srcStride, Span<byte> dst, int dstOffset, int dstStride,
        ReadOnlySpan<short> taps, int w, int h)
    {
        // Every byte read and written lies inside the spans: checked once here, so the loops index without checks.
        int first = srcOffset - 3, last = srcOffset + ((h - 1) * srcStride) + w + 4;
        if (first < 0 || last > src.Length || dstOffset < 0 || dstOffset + ((h - 1) * dstStride) + w > dst.Length)
        {
            throw new CorruptDataException("VP9", -1, "A prediction block reaches outside its reference.");
        }

        ref byte s0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), first);
        ref byte d0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), dstOffset);
        if (Avx2.IsSupported && first + ((h - 1) * srcStride) + Math.Max(w, 8) + 8 <= src.Length)
        {
            HorizontalPairs(ref s0, srcStride, ref d0, dstStride, taps, w, h);
            return;
        }

        Vector128<short> t0 = Vector128.Create(taps[0]), t1 = Vector128.Create(taps[1]), t2 = Vector128.Create(taps[2]),
            t3 = Vector128.Create(taps[3]), t4 = Vector128.Create(taps[4]), t5 = Vector128.Create(taps[5]),
            t6 = Vector128.Create(taps[6]), t7 = Vector128.Create(taps[7]);
        for (int y = 0; y < h; y++)
        {
            ref byte s = ref Unsafe.Add(ref s0, y * srcStride);
            ref byte d = ref Unsafe.Add(ref d0, y * dstStride);
            if (w == 4)
            {
                Vector128<short> sum = Vector128.Create((short)Bias);
                sum += t0 * Load4(ref s, 0);
                sum += t1 * Load4(ref s, 1);
                sum += t2 * Load4(ref s, 2);
                sum += t3 * Load4(ref s, 3);
                sum += t4 * Load4(ref s, 4);
                sum += t5 * Load4(ref s, 5);
                sum += t6 * Load4(ref s, 6);
                sum += t7 * Load4(ref s, 7);
                Unsafe.WriteUnaligned(ref d, Pack(sum).AsUInt32().ToScalar());
                continue;
            }

            int x = 0;
            if (Avx2.IsSupported)
            {
                for (; x + 16 <= w; x += 16)
                {
                    ref byte p = ref Unsafe.Add(ref s, x);
                    Vector256<short> sum = Vector256.Create((short)Bias);
                    sum += Vector256.Create(t0) * Load16(ref p, 0);
                    sum += Vector256.Create(t1) * Load16(ref p, 1);
                    sum += Vector256.Create(t2) * Load16(ref p, 2);
                    sum += Vector256.Create(t3) * Load16(ref p, 3);
                    sum += Vector256.Create(t4) * Load16(ref p, 4);
                    sum += Vector256.Create(t5) * Load16(ref p, 5);
                    sum += Vector256.Create(t6) * Load16(ref p, 6);
                    sum += Vector256.Create(t7) * Load16(ref p, 7);
                    Pack(sum).StoreUnsafe(ref Unsafe.Add(ref d, x));
                }
            }

            for (; x < w; x += 8)
            {
                ref byte p = ref Unsafe.Add(ref s, x);
                Vector128<short> sum = Vector128.Create((short)Bias);
                sum += t0 * Load8(ref p, 0);
                sum += t1 * Load8(ref p, 1);
                sum += t2 * Load8(ref p, 2);
                sum += t3 * Load8(ref p, 3);
                sum += t4 * Load8(ref p, 4);
                sum += t5 * Load8(ref p, 5);
                sum += t6 * Load8(ref p, 6);
                sum += t7 * Load8(ref p, 7);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x), Pack(sum).AsUInt64().ToScalar());
            }
        }
    }

    /// <summary>
    /// The horizontal pass with SSSE3's multiply-add of byte pairs, as libvpx's AVX2 filters do: each pixel pair times its two
    /// taps, summed into a 16-bit lane. No pair sum of a VP9 filter (other than the unfiltered position 0) passes 32385, so
    /// the multiply-add never saturates. Reads 16 bytes from 3 left of each group of eight outputs, which the caller checked.
    /// </summary>
    private static void HorizontalPairs(ref byte s0, int srcStride, ref byte d0, int dstStride, ReadOnlySpan<short> taps, int w, int h)
    {
        Vector256<sbyte> k01 = Vector256.Create((ushort)((byte)taps[0] | ((byte)taps[1] << 8))).AsSByte();
        Vector256<sbyte> k23 = Vector256.Create((ushort)((byte)taps[2] | ((byte)taps[3] << 8))).AsSByte();
        Vector256<sbyte> k45 = Vector256.Create((ushort)((byte)taps[4] | ((byte)taps[5] << 8))).AsSByte();
        Vector256<sbyte> k67 = Vector256.Create((ushort)((byte)taps[6] | ((byte)taps[7] << 8))).AsSByte();
        Vector256<byte> m01 = Vector256.Create((byte)0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8);
        Vector256<byte> m23 = m01 + Vector256.Create((byte)2), m45 = m01 + Vector256.Create((byte)4), m67 = m01 + Vector256.Create((byte)6);
        if (w <= 8)
        {
            // Two rows a vector, one in each 128-bit half.
            for (int y = 0; y < h; y += 2)
            {
                ref byte s = ref Unsafe.Add(ref s0, y * srcStride);
                Vector128<byte> top = Vector128.LoadUnsafe(ref s);
                Vector128<byte> bottom = y + 1 < h ? Vector128.LoadUnsafe(ref Unsafe.Add(ref s, srcStride)) : top;
                Vector128<byte> packed = Pairs(Vector256.Create(top, bottom), m01, m23, m45, m67, k01, k23, k45, k67);
                ref byte d = ref Unsafe.Add(ref d0, y * dstStride);
                if (w == 4)
                {
                    Unsafe.WriteUnaligned(ref d, packed.AsUInt32().GetElement(0));
                    if (y + 1 < h)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, dstStride), packed.AsUInt32().GetElement(2));
                    }
                }
                else
                {
                    Unsafe.WriteUnaligned(ref d, packed.AsUInt64().GetElement(0));
                    if (y + 1 < h)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, dstStride), packed.AsUInt64().GetElement(1));
                    }
                }
            }

            return;
        }

        for (int y = 0; y < h; y++)
        {
            ref byte s = ref Unsafe.Add(ref s0, y * srcStride);
            ref byte d = ref Unsafe.Add(ref d0, y * dstStride);
            for (int x = 0; x < w; x += 16)
            {
                ref byte p = ref Unsafe.Add(ref s, x);
                Vector256<byte> source = Vector256.Create(Vector128.LoadUnsafe(ref p), Vector128.LoadUnsafe(ref Unsafe.Add(ref p, 8)));
                Pairs(source, m01, m23, m45, m67, k01, k23, k45, k67).StoreUnsafe(ref Unsafe.Add(ref d, x));
            }
        }
    }

    /// <summary>Filters the 8 outputs of each 128-bit half of <paramref name="source"/> (16 pixels from 3 left of the first).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Pairs(Vector256<byte> source, Vector256<byte> m01, Vector256<byte> m23, Vector256<byte> m45, Vector256<byte> m67,
        Vector256<sbyte> k01, Vector256<sbyte> k23, Vector256<sbyte> k45, Vector256<sbyte> k67)
    {
        Vector256<short> sum = Vector256.Create((short)Bias)
            + Avx2.MultiplyAddAdjacent(Avx2.Shuffle(source, m01), k01)
            + Avx2.MultiplyAddAdjacent(Avx2.Shuffle(source, m23), k23)
            + Avx2.MultiplyAddAdjacent(Avx2.Shuffle(source, m45), k45)
            + Avx2.MultiplyAddAdjacent(Avx2.Shuffle(source, m67), k67);
        return PackHalves(sum);
    }

    /// <summary>The vertical pass: columns of <paramref name="h"/> outputs from the eight rows from 3 above each, the rows kept in registers as it goes down.</summary>
    private static void Vertical(ReadOnlySpan<byte> src, int srcOffset, int srcStride, Span<byte> dst, int dstOffset, int dstStride,
        ReadOnlySpan<short> taps, int w, int h)
    {
        int first = srcOffset - (3 * srcStride), last = srcOffset + ((h + 3) * srcStride) + w;
        if (first < 0 || last > src.Length || dstOffset < 0 || dstOffset + ((h - 1) * dstStride) + w > dst.Length)
        {
            throw new CorruptDataException("VP9", -1, "A prediction block reaches outside its reference.");
        }

        ref byte s0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), first);
        ref byte d0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), dstOffset);
        if (Avx2.IsSupported && first + ((h + 6) * srcStride) + Math.Max(w, 8) <= src.Length)
        {
            VerticalPairs(ref s0, srcStride, ref d0, dstStride, taps, w, h);
            return;
        }

        Vector128<short> t0 = Vector128.Create(taps[0]), t1 = Vector128.Create(taps[1]), t2 = Vector128.Create(taps[2]),
            t3 = Vector128.Create(taps[3]), t4 = Vector128.Create(taps[4]), t5 = Vector128.Create(taps[5]),
            t6 = Vector128.Create(taps[6]), t7 = Vector128.Create(taps[7]);
        bool narrow = w == 4;
        int x = 0;
        if (Avx2.IsSupported)
        {
            for (; x + 16 <= w; x += 16)
            {
                ref byte s = ref Unsafe.Add(ref s0, x);
                ref byte d = ref Unsafe.Add(ref d0, x);
                Vector256<short> r0 = Load16(ref s, 0), r1 = Load16(ref s, srcStride), r2 = Load16(ref s, 2 * srcStride),
                    r3 = Load16(ref s, 3 * srcStride), r4 = Load16(ref s, 4 * srcStride), r5 = Load16(ref s, 5 * srcStride),
                    r6 = Load16(ref s, 6 * srcStride);
                Vector256<short> u0 = Vector256.Create(t0), u1 = Vector256.Create(t1), u2 = Vector256.Create(t2), u3 = Vector256.Create(t3),
                    u4 = Vector256.Create(t4), u5 = Vector256.Create(t5), u6 = Vector256.Create(t6), u7 = Vector256.Create(t7);
                for (int y = 0; y < h; y++)
                {
                    Vector256<short> r7 = Load16(ref s, (y + 7) * srcStride);
                    Vector256<short> sum = Vector256.Create((short)Bias) + (u0 * r0) + (u1 * r1) + (u2 * r2) + (u3 * r3) + (u4 * r4)
                        + (u5 * r5) + (u6 * r6) + (u7 * r7);
                    Pack(sum).StoreUnsafe(ref Unsafe.Add(ref d, y * dstStride));
                    r0 = r1;
                    r1 = r2;
                    r2 = r3;
                    r3 = r4;
                    r4 = r5;
                    r5 = r6;
                    r6 = r7;
                }
            }
        }

        for (; x < w; x += 8)
        {
            ref byte s = ref Unsafe.Add(ref s0, x);
            ref byte d = ref Unsafe.Add(ref d0, x);
            Vector128<short> r0 = Row(ref s, 0, narrow), r1 = Row(ref s, srcStride, narrow), r2 = Row(ref s, 2 * srcStride, narrow),
                r3 = Row(ref s, 3 * srcStride, narrow), r4 = Row(ref s, 4 * srcStride, narrow), r5 = Row(ref s, 5 * srcStride, narrow),
                r6 = Row(ref s, 6 * srcStride, narrow);
            for (int y = 0; y < h; y++)
            {
                Vector128<short> r7 = Row(ref s, (y + 7) * srcStride, narrow);
                Vector128<short> sum = Vector128.Create((short)Bias) + (t0 * r0) + (t1 * r1) + (t2 * r2) + (t3 * r3) + (t4 * r4) + (t5 * r5)
                    + (t6 * r6) + (t7 * r7);
                Vector128<byte> packed = Pack(sum);
                if (narrow)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, y * dstStride), packed.AsUInt32().ToScalar());
                }
                else
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, y * dstStride), packed.AsUInt64().ToScalar());
                }

                r0 = r1;
                r1 = r2;
                r2 = r3;
                r3 = r4;
                r4 = r5;
                r5 = r6;
                r6 = r7;
            }
        }
    }

    /// <summary>
    /// The vertical pass with SSSE3's multiply-add of byte pairs: the pixels of rows k and k + 1 interleaved, times taps k and
    /// k + 1; each new output row interleaves one new row with the one before. Reads at least 8 bytes a row, which the caller checked.
    /// </summary>
    private static void VerticalPairs(ref byte s0, int srcStride, ref byte d0, int dstStride, ReadOnlySpan<short> taps, int w, int h)
    {
        Vector256<sbyte> k01 = Vector256.Create((ushort)((byte)taps[0] | ((byte)taps[1] << 8))).AsSByte();
        Vector256<sbyte> k23 = Vector256.Create((ushort)((byte)taps[2] | ((byte)taps[3] << 8))).AsSByte();
        Vector256<sbyte> k45 = Vector256.Create((ushort)((byte)taps[4] | ((byte)taps[5] << 8))).AsSByte();
        Vector256<sbyte> k67 = Vector256.Create((ushort)((byte)taps[6] | ((byte)taps[7] << 8))).AsSByte();
        if (w <= 8)
        {
            // Eight columns: a row is 8 bytes, a pair of rows one 128-bit half; two output rows a vector.
            ref byte s = ref s0;
            Vector128<byte> r0 = Row8(ref s, 0), r1 = Row8(ref s, srcStride), r2 = Row8(ref s, 2 * srcStride), r3 = Row8(ref s, 3 * srcStride),
                r4 = Row8(ref s, 4 * srcStride), r5 = Row8(ref s, 5 * srcStride), r6 = Row8(ref s, 6 * srcStride);
            Vector128<byte> p01 = Sse2.UnpackLow(r0, r1), p12 = Sse2.UnpackLow(r1, r2), p23 = Sse2.UnpackLow(r2, r3), p34 = Sse2.UnpackLow(r3, r4),
                p45 = Sse2.UnpackLow(r4, r5), p56 = Sse2.UnpackLow(r5, r6);
            Vector128<byte> previous = r6;
            for (int y = 0; y < h; y += 2)
            {
                Vector128<byte> r7 = Row8(ref s, (y + 7) * srcStride);
                Vector128<byte> r8 = y + 1 < h ? Row8(ref s, (y + 8) * srcStride) : r7;
                Vector128<byte> p67 = Sse2.UnpackLow(previous, r7), p78 = Sse2.UnpackLow(r7, r8);
                Vector128<byte> packed = Sum(Vector256.Create(p01, p12), Vector256.Create(p23, p34), Vector256.Create(p45, p56),
                    Vector256.Create(p67, p78), k01, k23, k45, k67);
                ref byte d = ref Unsafe.Add(ref d0, y * dstStride);
                if (w == 4)
                {
                    Unsafe.WriteUnaligned(ref d, packed.AsUInt32().GetElement(0));
                    if (y + 1 < h)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, dstStride), packed.AsUInt32().GetElement(2));
                    }
                }
                else
                {
                    Unsafe.WriteUnaligned(ref d, packed.AsUInt64().GetElement(0));
                    if (y + 1 < h)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, dstStride), packed.AsUInt64().GetElement(1));
                    }
                }

                p01 = p23;
                p12 = p34;
                p23 = p45;
                p34 = p56;
                p45 = p67;
                p56 = p78;
                previous = r8;
            }

            return;
        }

        for (int x = 0; x < w; x += 16)
        {
            // Sixteen columns: the low eight pairs in one 128-bit half, the high eight in the other.
            ref byte s = ref Unsafe.Add(ref s0, x);
            ref byte d = ref Unsafe.Add(ref d0, x);
            Vector128<byte> previous = Vector128.LoadUnsafe(ref s);
            Vector256<byte> q0 = default, q1 = default, q2 = default, q3 = default, q4 = default, q5 = default;
            for (int k = 1; k < 7; k++)
            {
                Vector128<byte> row = Vector128.LoadUnsafe(ref Unsafe.Add(ref s, k * srcStride));
                Vector256<byte> pair = Vector256.Create(Sse2.UnpackLow(previous, row), Sse2.UnpackHigh(previous, row));
                (q0, q1, q2, q3, q4, q5) = (q1, q2, q3, q4, q5, pair);
                previous = row;
            }

            for (int y = 0; y < h; y++)
            {
                Vector128<byte> row = Vector128.LoadUnsafe(ref Unsafe.Add(ref s, (y + 7) * srcStride));
                Vector256<byte> q6 = Vector256.Create(Sse2.UnpackLow(previous, row), Sse2.UnpackHigh(previous, row));
                Sum(q0, q2, q4, q6, k01, k23, k45, k67).StoreUnsafe(ref Unsafe.Add(ref d, y * dstStride));
                (q0, q1, q2, q3, q4, q5) = (q1, q2, q3, q4, q5, q6);
                previous = row;
            }
        }
    }

    /// <summary>Sums four pair products into lanes and packs each 128-bit half's eight outputs, low half first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Sum(Vector256<byte> a, Vector256<byte> b, Vector256<byte> c, Vector256<byte> d,
        Vector256<sbyte> k01, Vector256<sbyte> k23, Vector256<sbyte> k45, Vector256<sbyte> k67)
    {
        Vector256<short> sum = Vector256.Create((short)Bias) + Avx2.MultiplyAddAdjacent(a, k01) + Avx2.MultiplyAddAdjacent(b, k23)
            + Avx2.MultiplyAddAdjacent(c, k45) + Avx2.MultiplyAddAdjacent(d, k67);
        return PackHalves(sum);
    }

    /// <summary>
    /// The biased sums to bytes, each 128-bit half's eight outputs in turn: shift by 7 as unsigned and take off 128; the
    /// saturating pack clamps to 0..255. It packs within each half, so the two halves' results are joined after.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PackHalves(Vector256<short> sum)
    {
        Vector256<short> value = Vector256.ShiftRightLogical(sum.AsUInt16(), 7).AsInt16() - Vector256.Create((short)128);
        Vector256<byte> bytes = Avx2.PackUnsignedSaturate(value, value);
        return Avx2.Permute4x64(bytes.AsUInt64(), 0b_00_00_10_00).GetLower().AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Row8(ref byte p, int offset) =>
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset))).AsByte();

    /// <summary>The compound block's rounded average of the two predictions, (a + b + 1) / 2.</summary>
    private static void Average(ReadOnlySpan<byte> prediction, Span<byte> dst, int dstOffset, int dstStride, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            Span<byte> d = dst.Slice(dstOffset + (y * dstStride), w);
            ReadOnlySpan<byte> p = prediction.Slice(y * 64, w);
            int x = 0;
            if (Sse2.IsSupported)
            {
                for (; x + 16 <= w; x += 16)
                {
                    Sse2.Average(Vector128.Create(d[x..]), Vector128.Create(p[x..])).CopyTo(d[x..]);
                }
            }

            for (; x < w; x++)
            {
                d[x] = (byte)((d[x] + p[x] + 1) >> 1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load8(ref byte p, int offset) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset))).AsByte()).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load4(ref byte p, int offset) =>
        Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, offset))).AsByte()).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Load16(ref byte p, int offset) =>
        Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, offset)));

    /// <summary>The 16-lane <see cref="Pack(Vector128{short})"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Pack(Vector256<short> sum)
    {
        Vector256<short> value = Vector256.ShiftRightLogical(sum.AsUInt16(), 7).AsInt16() - Vector256.Create((short)128);
        value = Vector256.Min(Vector256.Max(value, Vector256<short>.Zero), Vector256.Create((short)255));
        return Vector256.Narrow(value.AsUInt16(), value.AsUInt16()).GetLower();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Row(ref byte p, int offset, bool narrow) => narrow ? Load4(ref p, offset) : Load8(ref p, offset);

    /// <summary>Takes the biased sums to clamped bytes: shift by 7 as unsigned, take off 128, clamp to 0..255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Pack(Vector128<short> sum)
    {
        Vector128<short> value = Vector128.ShiftRightLogical(sum.AsUInt16(), 7).AsInt16() - Vector128.Create((short)128);
        value = Vector128.Min(Vector128.Max(value, Vector128<short>.Zero), Vector128.Create((short)255));
        return Vector128.Narrow(value.AsUInt16(), value.AsUInt16());
    }
}
