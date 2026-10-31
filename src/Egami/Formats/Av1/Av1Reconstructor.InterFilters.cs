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
internal sealed partial class Av1Reconstructor<T>
{
    private readonly T[] _edge = new T[((128 + 7) * (128 + 7)) + Av1Samples.Padding];
    private readonly short[] _inter16 = new short[(128 + 7) * 128];

    /// <summary>
    /// Predicts a block from an unscaled reference at (<paramref name="x"/>, <paramref name="y"/>) in 1/1024 samples, as
    /// <see cref="BlockInterPrediction"/> does with steps of 1024.
    /// </summary>
    private void UnscaledPrediction(T[] src, int stride, int lastX, int lastY, int x, int y, int w, int h, short[] fx, short[] fy,
        int round0, int round1, int[] pred, Picture? picture)
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
            T[] edge = _edge;
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

        if (fracX == 0 && fracY == 0 && picture is Picture copy)
        {
            // A whole-sample vector: the passes shift by 7 - round0 and 7, and round1 = 14 - round0 takes it all back, so the
            // prediction is the reference's samples.
            for (int r = 0; r < h; r++)
            {
                src.AsSpan(at + ((r + 3) * stride) + 3, w).CopyTo(copy.Samples.AsSpan(copy.At + (r * copy.Stride), w));
            }

            return;
        }

        short[] inter = _inter16;
        if (fracY == 0)
        {
            // Only the rows the vertical filter's middle tap reads, and that tap is 128: Round2(v * 128, round1).
            Horizontal(src, at + (3 * stride), stride, fracX, fx, w, h, round0, inter);
            if (picture is Picture whole)
            {
                CenterTapToPicture(inter, w, h, round1, whole);
                return;
            }

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
        if (picture is Picture target)
        {
            VerticalToPicture(inter, fracY, fy, w, h, round1, target);
        }
        else
        {
            Vertical(inter, fracY, fy, w, h, round1, pred);
        }
    }

    /// <summary>The horizontal pass over <paramref name="rows"/> rows of the window: 8-tap, or a copy shifted to the filter's scale.</summary>
    internal static void Horizontal(T[] src, int at, int stride, int frac, short[] filters, int w, int rows, int round0, short[] inter)
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
                    ref T row = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), s);
                    for (; c + 8 <= w; c += 8)
                    {
                        Vector128.ShiftLeft(Av1Samples.Load8(ref row, c), shift).StoreUnsafe(ref inter[0], (nuint)(d + c));
                    }
                }

                for (; c < w; c++)
                {
                    inter[d + c] = (short)(Av1Samples.Get(src[s + c]) << shift);
                }
            }

            return;
        }

        ReadOnlySpan<short> f = filters.AsSpan(frac * 8, 8);
        int half = 1 << (round0 - 1);
        int c0 = 0;
        if (Av1Samples.IsByte<T>() && Avx2.IsSupported && w >= 4)
        {
            HorizontalBytes(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(src)), at, stride, f, w, rows, round0, inter);
            return;
        }

        if (Avx2.IsSupported && w >= 8)
        {
            // Samples fit 16 bits: vpmaddwd sums tap pairs (m, m + 1) of eight outputs at once.
            Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]), c45 = Pair(f[4], f[5]), c67 = Pair(f[6], f[7]);
            Vector256<int> halfV = Vector256.Create(half);
            ref T s0 = ref MemoryMarshal.GetArrayDataReference(src);
            ref short d0 = ref MemoryMarshal.GetArrayDataReference(inter);
            for (int r = 0; r < rows; r++)
            {
                int s = at + (r * stride), d = r * w;
                int c = 0;
                for (; c + 16 <= w; c += 16)
                {
                    // Sixteen outputs: in-lane unpacks give outputs 0-3 and 8-11 in one madd, 4-7 and 12-15 in the other, and the
                    // in-lane pack puts them back in order.
                    ref T p = ref Unsafe.Add(ref s0, s + c);
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
                    ref T p = ref Unsafe.Add(ref s0, s + c);
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
            ref T s0 = ref MemoryMarshal.GetArrayDataReference(src);
            ref short d0 = ref MemoryMarshal.GetArrayDataReference(inter);
            for (int r = 0; r < rows; r++)
            {
                int s = at + (r * stride), d = r * w;
                for (int c = c0; c + 4 <= w; c += 4)
                {
                    ref T p = ref Unsafe.Add(ref s0, s + c);
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
                int sum = (f[0] * Av1Samples.Get(src[p])) + (f[1] * Av1Samples.Get(src[p + 1])) + (f[2] * Av1Samples.Get(src[p + 2])) + (f[3] * Av1Samples.Get(src[p + 3]))
                    + (f[4] * Av1Samples.Get(src[p + 4])) + (f[5] * Av1Samples.Get(src[p + 5])) + (f[6] * Av1Samples.Get(src[p + 6])) + (f[7] * Av1Samples.Get(src[p + 7]));
                inter[d + c] = (short)((sum + half) >> round0);
            }
        }
    }

    /// <summary>
    /// The horizontal pass of 8-bit samples, 16 outputs at a time with vpmaddubsw: every AV1 filter tap is even, so the halved taps
    /// fit a signed byte, a sum of eight of them times 8-bit samples fits 16 bits, and Round2(sum, round0) is Round2(sum / 2,
    /// round0 - 1). Each 128-bit lane shuffles its eight outputs' sample pairs out of sixteen bytes; the sixteenth is read but
    /// never used, which the planes' padding (<see cref="Av1Samples.Padding"/>) allows.
    /// </summary>
    private static void HorizontalBytes(ref byte src, int at, int stride, ReadOnlySpan<short> f, int w, int rows, int round0, short[] inter)
    {
        Vector256<sbyte> c01 = BytePair(f[0], f[1]), c23 = BytePair(f[2], f[3]), c45 = BytePair(f[4], f[5]), c67 = BytePair(f[6], f[7]);
        Vector256<byte> s01 = PairShuffle(0), s23 = PairShuffle(2), s45 = PairShuffle(4), s67 = PairShuffle(6);
        Vector256<short> halfV = Vector256.Create((short)(1 << (round0 - 2)));
        int shift = round0 - 1;
        ref short d0 = ref MemoryMarshal.GetArrayDataReference(inter);
        if (w == 4)
        {
            // Two rows at a time, a lane each, of which the first four outputs are kept.
            for (int r = 0; r < rows; r += 2)
            {
                ref byte row = ref Unsafe.Add(ref src, at + (r * stride));
                Vector128<byte> next = r + 1 < rows ? Vector128.LoadUnsafe(ref row, (nuint)stride) : Vector128<byte>.Zero;
                Vector256<byte> window = Vector256.Create(Vector128.LoadUnsafe(ref row), next);
                Vector256<short> sum = Avx2.Add(
                    Avx2.Add(Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s01), c01), Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s23), c23)),
                    Avx2.Add(Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s45), c45), Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s67), c67)));
                Vector256<ulong> outputs = Vector256.ShiftRightArithmetic(sum + halfV, shift).AsUInt64();
                Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref d0, r * 4)), outputs.GetElement(0));
                if (r + 1 < rows)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref d0, (r + 1) * 4)), outputs.GetElement(2));
                }
            }

            return;
        }

        for (int r = 0; r < rows; r++)
        {
            ref byte row = ref Unsafe.Add(ref src, at + (r * stride));
            ref short d = ref Unsafe.Add(ref d0, r * w);
            int c = 0;
            for (; c + 16 <= w; c += 16)
            {
                // Lane 0 holds samples c to c + 15 for outputs c to c + 7, lane 1 samples c + 8 to c + 23 for the next eight.
                Vector256<byte> window = Vector256.Create(Vector128.LoadUnsafe(ref row, (nuint)c), Vector128.LoadUnsafe(ref row, (nuint)(c + 8)));
                Vector256<short> sum = Avx2.Add(
                    Avx2.Add(Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s01), c01), Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s23), c23)),
                    Avx2.Add(Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s45), c45), Avx2.MultiplyAddAdjacent(Avx2.Shuffle(window, s67), c67)));
                Vector256.ShiftRightArithmetic(sum + halfV, shift).StoreUnsafe(ref d, (nuint)c);
            }

            if (c < w)
            {
                // The last eight outputs of a block 8 samples wide.
                Vector128<byte> window = Vector128.LoadUnsafe(ref row, (nuint)c);
                Vector128<short> sum = Sse2.Add(
                    Sse2.Add(Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(window, s01.GetLower()), c01.GetLower()),
                        Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(window, s23.GetLower()), c23.GetLower())),
                    Sse2.Add(Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(window, s45.GetLower()), c45.GetLower()),
                        Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(window, s67.GetLower()), c67.GetLower())));
                Vector128.ShiftRightArithmetic(sum + halfV.GetLower(), shift).StoreUnsafe(ref d, (nuint)c);
            }
        }

        static Vector256<sbyte> BytePair(short first, short second) =>
            Vector256.Create((short)((byte)(sbyte)(first >> 1) | ((sbyte)(second >> 1) << 8))).AsSByte();

        // Bytes (m, m + 1), (m + 1, m + 2) ... (m + 7, m + 8) of a lane: taps m and m + 1 of its eight outputs.
        static Vector256<byte> PairShuffle(int m)
        {
            Vector128<byte> lane = Vector128.Create((byte)m, (byte)(m + 1), (byte)(m + 1), (byte)(m + 2), (byte)(m + 2), (byte)(m + 3), (byte)(m + 3),
                (byte)(m + 4), (byte)(m + 4), (byte)(m + 5), (byte)(m + 5), (byte)(m + 6), (byte)(m + 6), (byte)(m + 7), (byte)(m + 7), (byte)(m + 8));
            return Vector256.Create(lane, lane);
        }
    }

    /// <summary>
    /// <see cref="Vertical"/> for a single prediction, written straight into the frame: its last rounding gives samples, which are
    /// only clamped, so the 32-bit prediction and the copy out of it are skipped.
    /// </summary>
    private static void VerticalToPicture(short[] inter, int frac, short[] filters, int w, int h, int round1, Picture picture)
    {
        ReadOnlySpan<short> f = filters.AsSpan(frac * 8, 8);
        int half = 1 << (round1 - 1);
        ref short i0 = ref MemoryMarshal.GetArrayDataReference(inter);
        ref T d0 = ref MemoryMarshal.GetArrayDataReference(picture.Samples);
        int c0 = 0;
        if (Avx2.IsSupported && w >= 16)
        {
            Vector256<short> c01 = Pair(f[0], f[1]), c23 = Pair(f[2], f[3]), c45 = Pair(f[4], f[5]), c67 = Pair(f[6], f[7]);
            Vector256<int> halfV = Vector256.Create(half), maxV = Vector256.Create(picture.Max);
            for (int r = 0; r < h; r++)
            {
                int to = picture.At + (r * picture.Stride);
                for (int c = 0; c + 16 <= w; c += 16)
                {
                    ref short p = ref Unsafe.Add(ref i0, (r * w) + c);
                    Vector256<int> lo = halfV, hi = halfV;
                    Rows16(ref p, 0, w, c01, ref lo, ref hi);
                    Rows16(ref p, 2, w, c23, ref lo, ref hi);
                    Rows16(ref p, 4, w, c45, ref lo, ref hi);
                    Rows16(ref p, 6, w, c67, ref lo, ref hi);
                    lo = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(lo, round1), Vector256<int>.Zero), maxV);
                    hi = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(hi, round1), Vector256<int>.Zero), maxV);
                    Av1Samples.Store8(ref d0, to + c, Av1Samples.Pack(Avx2.Permute2x128(lo, hi, 0x20)));
                    Av1Samples.Store8(ref d0, to + c + 8, Av1Samples.Pack(Avx2.Permute2x128(lo, hi, 0x31)));
                }
            }

            c0 = w & ~15;
        }

        if (Sse41.IsSupported && w - c0 >= 8)
        {
            Vector128<short> c01 = Pair(f[0], f[1]).GetLower(), c23 = Pair(f[2], f[3]).GetLower();
            Vector128<short> c45 = Pair(f[4], f[5]).GetLower(), c67 = Pair(f[6], f[7]).GetLower();
            Vector128<int> halfV = Vector128.Create(half), maxV = Vector128.Create(picture.Max);
            for (int r = 0; r < h; r++)
            {
                int to = picture.At + (r * picture.Stride);
                for (int c = c0; c + 8 <= w; c += 8)
                {
                    ref short p = ref Unsafe.Add(ref i0, (r * w) + c);
                    Vector128<int> lo = halfV, hi = halfV;
                    Rows8(ref p, 0, w, c01, ref lo, ref hi);
                    Rows8(ref p, 2, w, c23, ref lo, ref hi);
                    Rows8(ref p, 4, w, c45, ref lo, ref hi);
                    Rows8(ref p, 6, w, c67, ref lo, ref hi);
                    lo = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(lo, round1), Vector128<int>.Zero), maxV);
                    hi = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(hi, round1), Vector128<int>.Zero), maxV);
                    Av1Samples.Store8(ref d0, to + c, Sse41.PackUnsignedSaturate(lo, hi));
                }
            }

            c0 += (w - c0) & ~7;
        }

        if (Sse41.IsSupported && w - c0 >= 4)
        {
            Vector128<short> c01 = Pair(f[0], f[1]).GetLower(), c23 = Pair(f[2], f[3]).GetLower();
            Vector128<short> c45 = Pair(f[4], f[5]).GetLower(), c67 = Pair(f[6], f[7]).GetLower();
            Vector128<int> halfV = Vector128.Create(half), maxV = Vector128.Create(picture.Max);
            for (int r = 0; r < h; r++)
            {
                ref short p = ref Unsafe.Add(ref i0, (r * w) + c0);
                Vector128<int> acc = Sse2.Add(Sse2.Add(Sse2.MultiplyAddAdjacent(Rows4(ref p, 0, w), c01), Sse2.MultiplyAddAdjacent(Rows4(ref p, 2, w), c23)),
                    Sse2.Add(Sse2.MultiplyAddAdjacent(Rows4(ref p, 4, w), c45), Sse2.MultiplyAddAdjacent(Rows4(ref p, 6, w), c67)));
                acc = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(acc + halfV, round1), Vector128<int>.Zero), maxV);
                Av1Samples.Store4(ref d0, picture.At + (r * picture.Stride) + c0, Av1Samples.Pack(acc));
            }

            c0 += 4;
        }

        for (int r = 0; r < h; r++)
        {
            int to = picture.At + (r * picture.Stride);
            for (int c = c0; c < w; c++)
            {
                ref short p = ref Unsafe.Add(ref i0, (r * w) + c);
                int sum = (f[0] * p) + (f[1] * Unsafe.Add(ref p, w)) + (f[2] * Unsafe.Add(ref p, 2 * w)) + (f[3] * Unsafe.Add(ref p, 3 * w))
                    + (f[4] * Unsafe.Add(ref p, 4 * w)) + (f[5] * Unsafe.Add(ref p, 5 * w)) + (f[6] * Unsafe.Add(ref p, 6 * w)) + (f[7] * Unsafe.Add(ref p, 7 * w));
                Unsafe.Add(ref d0, to + c) = Av1Samples.From<T>(Math.Clamp((sum + half) >> round1, 0, picture.Max));
            }
        }
    }

    /// <summary>A single prediction whose vertical position is whole: the middle tap alone, 128, written straight into the frame.</summary>
    private static void CenterTapToPicture(short[] inter, int w, int h, int round1, Picture picture)
    {
        int half = 1 << (round1 - 1);
        ref short i0 = ref MemoryMarshal.GetArrayDataReference(inter);
        ref T d0 = ref MemoryMarshal.GetArrayDataReference(picture.Samples);
        for (int r = 0; r < h; r++)
        {
            int to = picture.At + (r * picture.Stride);
            int c = 0;
            if (Avx2.IsSupported)
            {
                Vector256<int> halfV = Vector256.Create(half), maxV = Vector256.Create(picture.Max);
                for (; c + 8 <= w; c += 8)
                {
                    Vector256<int> v = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref i0, (nuint)((r * w) + c)));
                    v = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(v, 7) + halfV, round1), Vector256<int>.Zero), maxV);
                    Av1Samples.Store8(ref d0, to + c, Av1Samples.Pack(v));
                }
            }

            for (; c < w; c++)
            {
                Unsafe.Add(ref d0, to + c) = Av1Samples.From<T>(Math.Clamp(((Unsafe.Add(ref i0, (r * w) + c) * 128) + half) >> round1, 0, picture.Max));
            }
        }
    }

    /// <summary>Where a single prediction is written in the frame, and the largest sample value.</summary>
    private readonly record struct Picture(T[] Samples, int At, int Stride, int Max);

    /// <summary>The vertical 8-tap pass over the intermediate rows, rows (m, m + 1) interleaved for vpmaddwd.</summary>
    internal static void Vertical(short[] inter, int frac, short[] filters, int w, int h, int round1, int[] pred)
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
    private static Vector256<short> Interleave(ref T p, int m)
    {
        Vector128<short> a = Av1Samples.Load8(ref p, m), b = Av1Samples.Load8(ref p, m + 1);
        return Vector256.Create(Sse2.UnpackLow(a, b), Sse2.UnpackHigh(a, b));
    }

    /// <summary>Adds taps m and m + 1 of sixteen outputs: outputs 0-3 and 8-11 to <paramref name="lo"/>, 4-7 and 12-15 to <paramref name="hi"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave16(ref T p, int m, Vector256<short> taps, ref Vector256<int> lo, ref Vector256<int> hi)
    {
        Vector256<short> a = Av1Samples.Load16(ref p, m), b = Av1Samples.Load16(ref p, m + 1);
        lo = Avx2.Add(lo, Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a, b), taps));
        hi = Avx2.Add(hi, Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a, b), taps));
    }

    /// <summary>Samples m and m + 1 of four outputs, interleaved, reading no further than the fourth output's taps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Interleave4(ref T p, int m) => Sse2.UnpackLow(Av1Samples.Load4(ref p, m), Av1Samples.Load4(ref p, m + 1));
}
