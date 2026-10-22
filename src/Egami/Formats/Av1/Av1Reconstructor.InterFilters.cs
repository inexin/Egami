using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Av1;

/// <summary>
/// The block inter prediction process for an unscaled reference, eight outputs at a time: the filter phase is the same across the
/// block, a source window inside the reference needs no clamping (others are copied with their edges first), and a whole-sample
/// direction skips its filter, whose only tap is 128. The intermediate rows fit 16 bits at every bit depth (InterRound0 keeps them
/// under 2^15), so both passes sum tap pairs with vpmaddwd.
/// </summary>
internal sealed partial class Av1Reconstructor
{
    private readonly ushort[] _edge = new ushort[(128 + 7) * (128 + 7)];
    private readonly short[] _inter16 = new short[(128 + 7) * 128];

    /// <summary>
    /// Predicts a block from an unscaled reference at (<paramref name="x"/>, <paramref name="y"/>) in 1/1024 samples, as
    /// <see cref="BlockInterPrediction"/> does with steps of 1024.
    /// </summary>
    private void UnscaledPrediction(ushort[] src, int stride, int lastX, int lastY, int x, int y, int w, int h, short[] fx, short[] fy,
        int round0, int round1, int[] pred)
    {
        int fracX = (x >> 6) & 15, fracY = (y >> 6) & 15;
        int x0 = (x >> 10) - 3, y0 = (y >> 10) - 3;
        int windowW = w + 7, windowH = h + 7;
        int at;
        if (x0 >= 0 && y0 >= 0 && x0 + windowW - 1 <= lastX && y0 + windowH - 1 <= lastY)
        {
            at = (y0 * stride) + x0;
        }
        else
        {
            // Copy the window with the reference's edges repeated past them.
            ushort[] edge = _edge;
            int left = Math.Clamp(-x0, 0, windowW), right = Math.Clamp(x0 + windowW - 1 - lastX, 0, windowW - left);
            int middle = windowW - left - right;
            for (int r = 0; r < windowH; r++)
            {
                int row = Math.Clamp(y0 + r, 0, lastY) * stride, to = r * windowW;
                edge.AsSpan(to, left).Fill(src[row]);
                if (middle > 0)
                {
                    src.AsSpan(row + x0 + left, middle).CopyTo(edge.AsSpan(to + left));
                }

                edge.AsSpan(to + left + middle, right).Fill(src[row + lastX]);
            }

            src = edge;
            stride = windowW;
            at = 0;
        }

        short[] inter = _inter16;
        if (fracY == 0)
        {
            // Only the rows the vertical filter's middle tap reads, and that tap is 128: Round2(v * 128, round1).
            Horizontal(src, at + (3 * stride), stride, fracX, fx, w, h, round0, inter);
            int half = 1 << (round1 - 1);
            int i = 0;
            if (Avx2.IsSupported)
            {
                Vector256<int> halfV = Vector256.Create(half);
                ref short i0 = ref MemoryMarshal.GetArrayDataReference(inter);
                for (; i + 8 <= w * h; i += 8)
                {
                    Vector256<int> v = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref i0, (nuint)i));
                    Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(v, 7) + halfV, round1).StoreUnsafe(ref pred[0], (nuint)i);
                }
            }

            for (; i < w * h; i++)
            {
                pred[i] = ((inter[i] * 128) + half) >> round1;
            }

            return;
        }

        Horizontal(src, at, stride, fracX, fx, w, windowH, round0, inter);
        Vertical(inter, fracY, fy, w, h, round1, pred);
    }

    /// <summary>The horizontal pass over <paramref name="rows"/> rows of the window: 8-tap, or a copy shifted to the filter's scale.</summary>
    private static void Horizontal(ushort[] src, int at, int stride, int frac, short[] filters, int w, int rows, int round0, short[] inter)
    {
        if (frac == 0)
        {
            int shift = 7 - round0;
            for (int r = 0; r < rows; r++)
            {
                int s = at + (r * stride) + 3, d = r * w;
                int c = 0;
                if (Vector128.IsHardwareAccelerated)
                {
                    ref ushort row = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), s);
                    for (; c + 8 <= w; c += 8)
                    {
                        Vector128.ShiftLeft(Vector128.LoadUnsafe(ref row, (nuint)c), shift).AsInt16().StoreUnsafe(ref inter[0], (nuint)(d + c));
                    }
                }

                for (; c < w; c++)
                {
                    inter[d + c] = (short)(src[s + c] << shift);
                }
            }

            return;
        }

        ReadOnlySpan<short> f = filters.AsSpan(frac * 8, 8);
        int half = 1 << (round0 - 1);
        int c0 = 0;
        if (Avx2.IsSupported && w >= 8)
        {
            // Samples fit 16 bits: vpmaddwd sums tap pairs (m, m + 1) of eight outputs at once.
            Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]), c45 = Pair(f[4], f[5]), c67 = Pair(f[6], f[7]);
            Vector256<int> halfV = Vector256.Create(half);
            ref short s0 = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(src));
            ref short d0 = ref MemoryMarshal.GetArrayDataReference(inter);
            for (int r = 0; r < rows; r++)
            {
                int s = at + (r * stride), d = r * w;
                int c = 0;
                for (; c + 16 <= w; c += 16)
                {
                    // Sixteen outputs: in-lane unpacks give outputs 0-3 and 8-11 in one madd, 4-7 and 12-15 in the other, and the
                    // in-lane pack puts them back in order.
                    ref short p = ref Unsafe.Add(ref s0, s + c);
                    Vector256<int> lo = halfV, hi = halfV;
                    Interleave16(ref p, 0, c01, ref lo, ref hi);
                    Interleave16(ref p, 2, c23, ref lo, ref hi);
                    Interleave16(ref p, 4, c45, ref lo, ref hi);
                    Interleave16(ref p, 6, c67, ref lo, ref hi);
                    Avx2.PackSignedSaturate(Vector256.ShiftRightArithmetic(lo, round0), Vector256.ShiftRightArithmetic(hi, round0))
                        .StoreUnsafe(ref Unsafe.Add(ref d0, d + c));
                }

                for (; c + 8 <= w; c += 8)
                {
                    ref short p = ref Unsafe.Add(ref s0, s + c);
                    Vector256<int> acc = Avx2.Add(Avx2.Add(Avx2.MultiplyAddAdjacent(Interleave(ref p, 0), c01), Avx2.MultiplyAddAdjacent(Interleave(ref p, 2), c23)),
                        Avx2.Add(Avx2.MultiplyAddAdjacent(Interleave(ref p, 4), c45), Avx2.MultiplyAddAdjacent(Interleave(ref p, 6), c67)));
                    acc = Vector256.ShiftRightArithmetic(acc + halfV, round0);
                    Sse2.PackSignedSaturate(acc.GetLower(), acc.GetUpper()).StoreUnsafe(ref Unsafe.Add(ref d0, d + c));
                }
            }

            c0 = w & ~7;
        }

        if (Sse2.IsSupported && w - c0 >= 4)
        {
            // Four outputs at a time (4-wide chroma, and 8-wide blocks when there is no AVX2).
            Vector128<short> c01 = Pair(f[0], f[1]).GetLower(), c23 = Pair(f[2], f[3]).GetLower();
            Vector128<short> c45 = Pair(f[4], f[5]).GetLower(), c67 = Pair(f[6], f[7]).GetLower();
            Vector128<int> halfV = Vector128.Create(half);
            ref short s0 = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(src));
            ref short d0 = ref MemoryMarshal.GetArrayDataReference(inter);
            for (int r = 0; r < rows; r++)
            {
                int s = at + (r * stride), d = r * w;
                for (int c = c0; c + 4 <= w; c += 4)
                {
                    ref short p = ref Unsafe.Add(ref s0, s + c);
                    Vector128<int> acc = Sse2.Add(Sse2.Add(Sse2.MultiplyAddAdjacent(Interleave4(ref p, 0), c01), Sse2.MultiplyAddAdjacent(Interleave4(ref p, 2), c23)),
                        Sse2.Add(Sse2.MultiplyAddAdjacent(Interleave4(ref p, 4), c45), Sse2.MultiplyAddAdjacent(Interleave4(ref p, 6), c67)));
                    acc = Vector128.ShiftRightArithmetic(acc + halfV, round0);
                    Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref d0, d + c)), Sse2.PackSignedSaturate(acc, acc).AsUInt64().ToScalar());
                }
            }

            c0 = w & ~3;
        }

        for (int r = 0; r < rows; r++)
        {
            int s = at + (r * stride), d = r * w;
            for (int c = c0; c < w; c++)
            {
                int p = s + c;
                int sum = (f[0] * src[p]) + (f[1] * src[p + 1]) + (f[2] * src[p + 2]) + (f[3] * src[p + 3])
                    + (f[4] * src[p + 4]) + (f[5] * src[p + 5]) + (f[6] * src[p + 6]) + (f[7] * src[p + 7]);
                inter[d + c] = (short)((sum + half) >> round0);
            }
        }
    }

    /// <summary>The vertical 8-tap pass over the intermediate rows, rows (m, m + 1) interleaved for vpmaddwd.</summary>
    private static void Vertical(short[] inter, int frac, short[] filters, int w, int h, int round1, int[] pred)
    {
        ReadOnlySpan<short> f = filters.AsSpan(frac * 8, 8);
        int half = 1 << (round1 - 1);
        int c0 = 0;
        ref short i0 = ref MemoryMarshal.GetArrayDataReference(inter);
        ref int p0 = ref MemoryMarshal.GetArrayDataReference(pred);
        if (Avx2.IsSupported && w >= 16)
        {
            Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]), c45 = Pair(f[4], f[5]), c67 = Pair(f[6], f[7]);
            Vector256<int> halfV = Vector256.Create(half);
            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c + 16 <= w; c += 16)
                {
                    ref short p = ref Unsafe.Add(ref i0, (r * w) + c);
                    Vector256<int> lo = halfV, hi = halfV;
                    Rows16(ref p, 0, w, c01, ref lo, ref hi);
                    Rows16(ref p, 2, w, c23, ref lo, ref hi);
                    Rows16(ref p, 4, w, c45, ref lo, ref hi);
                    Rows16(ref p, 6, w, c67, ref lo, ref hi);
                    lo = Vector256.ShiftRightArithmetic(lo, round1);
                    hi = Vector256.ShiftRightArithmetic(hi, round1);
                    Avx2.Permute2x128(lo, hi, 0x20).StoreUnsafe(ref Unsafe.Add(ref p0, (r * w) + c));
                    Avx2.Permute2x128(lo, hi, 0x31).StoreUnsafe(ref Unsafe.Add(ref p0, (r * w) + c + 8));
                }
            }

            c0 = w & ~15;
        }

        if (Sse2.IsSupported && w - c0 >= 4)
        {
            Vector128<short> c01 = Pair(f[0], f[1]).GetLower(), c23 = Pair(f[2], f[3]).GetLower();
            Vector128<short> c45 = Pair(f[4], f[5]).GetLower(), c67 = Pair(f[6], f[7]).GetLower();
            Vector128<int> halfV = Vector128.Create(half);
            for (int r = 0; r < h; r++)
            {
                int c = c0;
                for (; c + 8 <= w; c += 8)
                {
                    ref short p = ref Unsafe.Add(ref i0, (r * w) + c);
                    Vector128<int> lo = halfV, hi = halfV;
                    Rows8(ref p, 0, w, c01, ref lo, ref hi);
                    Rows8(ref p, 2, w, c23, ref lo, ref hi);
                    Rows8(ref p, 4, w, c45, ref lo, ref hi);
                    Rows8(ref p, 6, w, c67, ref lo, ref hi);
                    Vector128.ShiftRightArithmetic(lo, round1).StoreUnsafe(ref Unsafe.Add(ref p0, (r * w) + c));
                    Vector128.ShiftRightArithmetic(hi, round1).StoreUnsafe(ref Unsafe.Add(ref p0, (r * w) + c + 4));
                }

                if (c + 4 <= w)
                {
                    ref short p = ref Unsafe.Add(ref i0, (r * w) + c);
                    Vector128<int> acc = Sse2.Add(Sse2.Add(Sse2.MultiplyAddAdjacent(Rows4(ref p, 0, w), c01), Sse2.MultiplyAddAdjacent(Rows4(ref p, 2, w), c23)),
                        Sse2.Add(Sse2.MultiplyAddAdjacent(Rows4(ref p, 4, w), c45), Sse2.MultiplyAddAdjacent(Rows4(ref p, 6, w), c67)));
                    Vector128.ShiftRightArithmetic(acc + halfV, round1).StoreUnsafe(ref Unsafe.Add(ref p0, (r * w) + c));
                }
            }

            c0 = w & ~3;
        }

        for (int r = 0; r < h; r++)
        {
            for (int c = c0; c < w; c++)
            {
                int p = (r * w) + c;
                int sum = (f[0] * inter[p]) + (f[1] * inter[p + w]) + (f[2] * inter[p + (2 * w)]) + (f[3] * inter[p + (3 * w)])
                    + (f[4] * inter[p + (4 * w)]) + (f[5] * inter[p + (5 * w)]) + (f[6] * inter[p + (6 * w)]) + (f[7] * inter[p + (7 * w)]);
                pred[p] = (sum + half) >> round1;
            }
        }
    }

    /// <summary>Adds taps m and m + 1 of sixteen columns: columns 0-3 and 8-11 to <paramref name="lo"/>, 4-7 and 12-15 to <paramref name="hi"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rows16(ref short p, int m, int w, Vector256<short> taps, ref Vector256<int> lo, ref Vector256<int> hi)
    {
        Vector256<short> a = Vector256.LoadUnsafe(ref p, (nuint)(m * w)), b = Vector256.LoadUnsafe(ref p, (nuint)((m + 1) * w));
        lo = Avx2.Add(lo, Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a, b), taps));
        hi = Avx2.Add(hi, Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a, b), taps));
    }

    /// <summary>Adds taps m and m + 1 of eight columns: columns 0-3 to <paramref name="lo"/>, 4-7 to <paramref name="hi"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rows8(ref short p, int m, int w, Vector128<short> taps, ref Vector128<int> lo, ref Vector128<int> hi)
    {
        Vector128<short> a = Vector128.LoadUnsafe(ref p, (nuint)(m * w)), b = Vector128.LoadUnsafe(ref p, (nuint)((m + 1) * w));
        lo = Sse2.Add(lo, Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a, b), taps));
        hi = Sse2.Add(hi, Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a, b), taps));
    }

    /// <summary>Rows m and m + 1 of four columns, interleaved.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Rows4(ref short p, int m, int w) => Sse2.UnpackLow(
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref p, m * w)))).AsInt16(),
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref p, (m + 1) * w)))).AsInt16());

    /// <summary>Two taps repeated for vpmaddwd: the first in the low half of each 32-bit lane.</summary>
    private static Vector256<short> Pair(short first, short second) => Vector256.Create((int)(ushort)first | (second << 16)).AsInt16();

    /// <summary>Samples m and m + 1 of eight outputs, interleaved: [p[m], p[m + 1], p[m + 1], p[m + 2], ...].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Interleave(ref short p, int m)
    {
        Vector128<short> a = Vector128.LoadUnsafe(ref p, (nuint)m), b = Vector128.LoadUnsafe(ref p, (nuint)(m + 1));
        return Vector256.Create(Sse2.UnpackLow(a, b), Sse2.UnpackHigh(a, b));
    }

    /// <summary>Adds taps m and m + 1 of sixteen outputs: outputs 0-3 and 8-11 to <paramref name="lo"/>, 4-7 and 12-15 to <paramref name="hi"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave16(ref short p, int m, Vector256<short> taps, ref Vector256<int> lo, ref Vector256<int> hi)
    {
        Vector256<short> a = Vector256.LoadUnsafe(ref p, (nuint)m), b = Vector256.LoadUnsafe(ref p, (nuint)(m + 1));
        lo = Avx2.Add(lo, Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a, b), taps));
        hi = Avx2.Add(hi, Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a, b), taps));
    }

    /// <summary>Samples m and m + 1 of four outputs, interleaved, reading no further than the fourth output's taps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Interleave4(ref short p, int m) => Sse2.UnpackLow(
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref p, m)))).AsInt16(),
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref p, m + 1)))).AsInt16());
}
