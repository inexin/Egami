using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The 10- and 12-bit form of <see cref="Vp9Convolve{T}"/> at a step of 16 (no scaling), with the same values: AVX2's multiply-add
/// of 16-bit pairs, each pair of neighboring pixels times its two taps summed into a 32-bit lane (a pixel below 4096 times a tap
/// within -128..127 cannot overflow it), eight outputs to each 128-bit half.
/// </summary>
[SkipLocalsInit]
internal static class Vp9ConvolveSimd16
{
    /// <summary>Gets whether the vector form runs here; otherwise the scalar one does.</summary>
    public static bool IsSupported => Avx2.IsSupported && Vector256.IsHardwareAccelerated;

    /// <summary>
    /// Predicts a <paramref name="w"/> by <paramref name="h"/> block as <see cref="Vp9Convolve{T}.Predict"/> does with both steps 16.
    /// <paramref name="mid"/> holds the horizontal pass, 64 samples a row; <paramref name="average"/> blocks go through
    /// <paramref name="predictTemp"/>, 64 samples a row.
    /// </summary>
    public static void Predict(ReadOnlySpan<ushort> src, int srcOffset, int srcStride, Span<ushort> dst, int dstOffset, int dstStride,
        int x0Q4, int y0Q4, int w, int h, bool average, ReadOnlySpan<short> kernel, int bitDepth, Span<ushort> mid, Span<ushort> predictTemp)
    {
        Span<ushort> outBuffer = average ? predictTemp : dst;
        int outOffset = average ? 0 : dstOffset;
        int outStride = average ? 64 : dstStride;
        int max = (1 << bitDepth) - 1;
        if (x0Q4 != 0 && y0Q4 != 0)
        {
            Horizontal(src, srcOffset - (3 * srcStride), srcStride, mid, 0, 64, kernel.Slice(x0Q4 * 8, 8), w, h + 7, max);
            Vertical(mid, 3 * 64, 64, outBuffer, outOffset, outStride, kernel.Slice(y0Q4 * 8, 8), w, h, max);
        }
        else if (x0Q4 != 0)
        {
            Horizontal(src, srcOffset, srcStride, outBuffer, outOffset, outStride, kernel.Slice(x0Q4 * 8, 8), w, h, max);
        }
        else if (y0Q4 != 0)
        {
            Vertical(src, srcOffset, srcStride, outBuffer, outOffset, outStride, kernel.Slice(y0Q4 * 8, 8), w, h, max);
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                src.Slice(srcOffset + (y * srcStride), w).CopyTo(outBuffer.Slice(outOffset + (y * outStride), w));
            }
        }

        if (average)
        {
            for (int y = 0; y < h; y++)
            {
                Span<ushort> d = dst.Slice(dstOffset + (y * dstStride), w);
                ReadOnlySpan<ushort> p = predictTemp.Slice(y * 64, w);
                int x = 0;
                for (; x + 8 <= w; x += 8)
                {
                    Sse2.Average(Vector128.Create(d[x..]), Vector128.Create(p[x..])).CopyTo(d[x..]);
                }

                for (; x < w; x++)
                {
                    d[x] = (ushort)((d[x] + p[x] + 1) >> 1);
                }
            }
        }
    }

    private static void Horizontal(ReadOnlySpan<ushort> src, int srcOffset, int srcStride, Span<ushort> dst, int dstOffset, int dstStride,
        ReadOnlySpan<short> taps, int w, int h, int max)
    {
        // Each group of eight outputs reads 16 samples from 3 left of its first.
        int first = srcOffset - 3, last = first + ((h - 1) * srcStride) + Math.Max(w, 8) + 8;
        if (first < 0 || last > src.Length || dstOffset < 0 || dstOffset + ((h - 1) * dstStride) + w > dst.Length)
        {
            throw new CorruptDataException("VP9", -1, "A prediction block reaches outside its reference.");
        }

        ref ushort s0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), first);
        ref ushort d0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), dstOffset);
        Vector256<short> k01 = Pair(taps[0], taps[1]), k23 = Pair(taps[2], taps[3]), k45 = Pair(taps[4], taps[5]), k67 = Pair(taps[6], taps[7]);
        Vector256<int> maxValue = Vector256.Create(max);
        for (int y = 0; y < h; y++)
        {
            ref ushort s = ref Unsafe.Add(ref s0, y * srcStride);
            ref ushort d = ref Unsafe.Add(ref d0, y * dstStride);
            for (int x = 0; x < w; x += 8)
            {
                // A(k) is the eight samples from k on; a tap pair's products pair A(k) and A(k + 1) lane by lane.
                ref ushort p = ref Unsafe.Add(ref s, x);
                Vector128<short> a = Vector128.LoadUnsafe(ref p).AsInt16(), b = Vector128.LoadUnsafe(ref p, 8).AsInt16();
                Vector256<int> sum = Avx2.MultiplyAddAdjacent(Interleave(a, Ssse3.AlignRight(b, a, 2)), k01)
                    + Avx2.MultiplyAddAdjacent(Interleave(Ssse3.AlignRight(b, a, 4), Ssse3.AlignRight(b, a, 6)), k23)
                    + Avx2.MultiplyAddAdjacent(Interleave(Ssse3.AlignRight(b, a, 8), Ssse3.AlignRight(b, a, 10)), k45)
                    + Avx2.MultiplyAddAdjacent(Interleave(Ssse3.AlignRight(b, a, 12), Ssse3.AlignRight(b, a, 14)), k67);
                Store(ref Unsafe.Add(ref d, x), sum, maxValue, w - x);
            }
        }
    }

    private static void Vertical(ReadOnlySpan<ushort> src, int srcOffset, int srcStride, Span<ushort> dst, int dstOffset, int dstStride,
        ReadOnlySpan<short> taps, int w, int h, int max)
    {
        int first = srcOffset - (3 * srcStride), last = srcOffset + ((h + 3) * srcStride) + Math.Max(w, 8);
        if (first < 0 || last > src.Length || dstOffset < 0 || dstOffset + ((h - 1) * dstStride) + w > dst.Length)
        {
            throw new CorruptDataException("VP9", -1, "A prediction block reaches outside its reference.");
        }

        ref ushort s0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), first);
        ref ushort d0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), dstOffset);
        Vector256<short> k01 = Pair(taps[0], taps[1]), k23 = Pair(taps[2], taps[3]), k45 = Pair(taps[4], taps[5]), k67 = Pair(taps[6], taps[7]);
        Vector256<int> maxValue = Vector256.Create(max);
        for (int x = 0; x < w; x += 8)
        {
            // Rows k and k + 1 interleaved pair each column's samples: columns 0-3 in the low half, 4-7 in the high.
            ref ushort s = ref Unsafe.Add(ref s0, x);
            ref ushort d = ref Unsafe.Add(ref d0, x);
            Vector128<short> r0 = Row(ref s, 0), r1 = Row(ref s, srcStride), r2 = Row(ref s, 2 * srcStride), r3 = Row(ref s, 3 * srcStride),
                r4 = Row(ref s, 4 * srcStride), r5 = Row(ref s, 5 * srcStride), r6 = Row(ref s, 6 * srcStride);
            Vector256<short> p01 = Interleave(r0, r1), p23 = Interleave(r2, r3), p45 = Interleave(r4, r5);
            Vector256<short> p12 = Interleave(r1, r2), p34 = Interleave(r3, r4), p56 = Interleave(r5, r6);
            Vector128<short> previous = r6;
            for (int y = 0; y < h; y++)
            {
                Vector128<short> r7 = Row(ref s, (y + 7) * srcStride);
                Vector256<short> p67 = Interleave(previous, r7);
                Vector256<int> sum = Avx2.MultiplyAddAdjacent(p01, k01) + Avx2.MultiplyAddAdjacent(p23, k23) + Avx2.MultiplyAddAdjacent(p45, k45)
                    + Avx2.MultiplyAddAdjacent(p67, k67);
                Store(ref Unsafe.Add(ref d, y * dstStride), sum, maxValue, w - x);
                (p01, p12, p23, p34, p45, p56) = (p12, p23, p34, p45, p56, p67);
                previous = r7;
            }
        }
    }

    /// <summary>Two taps as a repeated pair of 16-bit lanes.</summary>
    private static Vector256<short> Pair(short a, short b) => Vector256.Create((int)(ushort)a | (b << 16)).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Row(ref ushort p, int offset) => Vector128.LoadUnsafe(ref Unsafe.Add(ref p, offset)).AsInt16();

    /// <summary>Interleaves two vectors' lanes: lanes 0-3 of each in the low half, 4-7 in the high.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Interleave(Vector128<short> a, Vector128<short> b) => Vector256.Create(Sse2.UnpackLow(a, b), Sse2.UnpackHigh(a, b));

    /// <summary>Rounds the sums by 7 bits, clamps them to 0..max and stores <paramref name="count"/> of the eight (4 or 8).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref ushort d, Vector256<int> sum, Vector256<int> max, int count)
    {
        Vector256<int> value = Vector256.ShiftRightArithmetic(sum + Vector256.Create(64), 7);
        value = Vector256.Min(Vector256.Max(value, Vector256<int>.Zero), max);
        Vector128<ushort> packed = Sse41.PackUnsignedSaturate(value.GetLower(), value.GetUpper());
        if (count >= 8)
        {
            packed.StoreUnsafe(ref d);
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref d), packed.AsUInt64().ToScalar());
        }
    }
}
