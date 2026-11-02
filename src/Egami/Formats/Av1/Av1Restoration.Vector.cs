using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Av1;

/// <summary>
/// Loop restoration with AVX2: a stripe of a unit is first copied into a window of 16-bit samples with its edges and the
/// deblocked rows beyond the stripe in place, so the filters read it without the checks <c>Source</c> makes for every tap. The
/// Wiener passes sum tap pairs with vpmaddwd; the self-guided filter works out its box sums, a and b, eight positions at a time.
/// Every step is the scalar filter's arithmetic, wrapping where it wraps.
/// </summary>
internal static partial class Av1Restoration
{
    /// <summary>x / (x + 1) at every z, as 32-bit values to gather.</summary>
    private static readonly int[] XByXPlus1 = [.. Av1Tables.XByXPlus1.Select(v => (int)v)];

    private sealed partial class PlaneFilter<T>
    {
        // Per row of units: the window, the Wiener intermediate rows, and the self-guided sums and outputs, grown as needed.
        private short[] _window = [];
        private short[] _temp = [];
        private int[] _columns = [], _squareColumns = [], _a = [], _b = [], _flt0 = [], _flt1 = [];

        /// <summary>
        /// Copies the stripe rows <paramref name="v"/> - 3 to <paramref name="v"/> + <paramref name="h"/> + 2 of columns
        /// <paramref name="hStart"/> - 3 to <paramref name="hStart"/> + <paramref name="width"/> + 2, as <c>Source</c> sees them,
        /// into the window, <paramref name="ws"/> apart. Columns past those are left as they are.
        /// </summary>
        private void Window(int hStart, int width, int v, int h, int ws)
        {
            Grow(ref _window, (ws * (h + 7)) + 32);
            for (int r = 0; r < h + 6; r++)
            {
                int y = Math.Clamp(v + r - 3, 0, _planeH - 1);
                T[] src = _cdef;
                if (y < _stripeStart)
                {
                    src = _deblocked;
                    y = Math.Max(_stripeStart - 2, y);
                }
                else if (y > _stripeEnd)
                {
                    src = _deblocked;
                    y = Math.Min(_stripeEnd + 2, y);
                }

                Span<short> to = _window.AsSpan(r * ws, width + 6);
                int row = y * _stride, x0 = hStart - 3;
                // Columns from lo to hi read the row itself; those before and after repeat its first and last samples.
                int lo = Math.Max(0, -x0), hi = Math.Min(width + 6, _planeW - x0);
                to[..lo].Fill((short)Av1Samples.Get(src[row]));
                to[hi..].Fill((short)Av1Samples.Get(src[row + _planeW - 1]));
                if (Av1Samples.IsByte<T>())
                {
                    ref byte from = ref Unsafe.Add(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(src)), row + x0);
                    ref short into = ref MemoryMarshal.GetReference(to);
                    int x = lo;
                    for (; x + 16 <= hi; x += 16)
                    {
                        Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref from, (nuint)x)).StoreUnsafe(ref into, (nuint)x);
                    }

                    for (; x < hi; x++)
                    {
                        to[x] = Unsafe.Add(ref from, x);
                    }
                }
                else
                {
                    MemoryMarshal.Cast<T, short>(src.AsSpan(row + x0 + lo, hi - lo)).CopyTo(to[lo..]);
                }
            }
        }

        /// <summary><see cref="Wiener"/> on the window: sixteen outputs a step in both passes.</summary>
        private void WienerVector(ref Av1RestorationUnit unit, int hStart, int hEnd, int v, int h)
        {
            int bd = _bitDepth;
            int round0 = bd == 12 ? 5 : 3, round1 = bd == 12 ? 9 : 11;
            int width = hEnd - hStart;
            int tw = (width + 15) & ~15, ws = tw + 16;
            Window(hStart, width, v, h, ws);
            Grow(ref _temp, (tw * (h + 7)) + 16);

            // The middle tap with the 128 the filter adds the source with.
            Vector256<short> h01 = Pair(unit.H0, unit.H1), h23 = Pair(unit.H2, 128 - (2 * (unit.H0 + unit.H1 + unit.H2))), h45 = Pair(unit.H2, unit.H1), h6 = Pair(unit.H0, 0);
            Vector256<int> offset0 = Vector256.Create((1 << (bd + 6)) + (1 << (round0 - 1)));
            Vector256<int> limit = Vector256.Create((1 << (bd + 1 + 7 - round0)) - 1);
            ref short w0 = ref MemoryMarshal.GetArrayDataReference(_window);
            ref short t0 = ref MemoryMarshal.GetArrayDataReference(_temp);
            for (int r = 0; r < h + 6; r++)
            {
                for (int x = 0; x < tw; x += 16)
                {
                    ref short p = ref Unsafe.Add(ref w0, (r * ws) + x);
                    Vector256<int> lo = offset0, hi = offset0;
                    Taps(ref p, 0, 1, h01, ref lo, ref hi);
                    Taps(ref p, 2, 1, h23, ref lo, ref hi);
                    Taps(ref p, 4, 1, h45, ref lo, ref hi);
                    Taps(ref p, 6, 1, h6, ref lo, ref hi);
                    lo = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(lo, round0), Vector256<int>.Zero), limit);
                    hi = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(hi, round0), Vector256<int>.Zero), limit);
                    Avx2.PackSignedSaturate(lo, hi).StoreUnsafe(ref Unsafe.Add(ref t0, (r * tw) + x));
                }
            }

            Vector256<short> v01 = Pair(unit.V0, unit.V1), v23 = Pair(unit.V2, 128 - (2 * (unit.V0 + unit.V1 + unit.V2))), v45 = Pair(unit.V2, unit.V1), v6 = Pair(unit.V0, 0);
            Vector256<int> offset1 = Vector256.Create((1 << (round1 - 1)) - (1 << (bd + round1 - 1)));
            Vector256<int> max = Vector256.Create((1 << bd) - 1);
            Span<T> tail = stackalloc T[16];
            for (int r = 0; r < h; r++)
            {
                Span<T> line = _output.AsSpan(((v + r) * _stride) + hStart, width);
                for (int x = 0; x < tw; x += 16)
                {
                    ref short p = ref Unsafe.Add(ref t0, (r * tw) + x);
                    Vector256<int> lo = offset1, hi = offset1;
                    Taps(ref p, 0, tw, v01, ref lo, ref hi);
                    Taps(ref p, 2, tw, v23, ref lo, ref hi);
                    Taps(ref p, 4, tw, v45, ref lo, ref hi);
                    Taps(ref p, 6, tw, v6, ref lo, ref hi);
                    lo = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(lo, round1), Vector256<int>.Zero), max);
                    hi = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(hi, round1), Vector256<int>.Zero), max);
                    Store16(Avx2.PackSignedSaturate(lo, hi), line, x, tail);
                }
            }
        }

        /// <summary><see cref="SelfGuided"/> on the window.</summary>
        private void SelfGuidedVector(ref Av1RestorationUnit unit, int hStart, int hEnd, int v, int h)
        {
            ReadOnlySpan<int> parameters = SgrParams.Slice(unit.SgrSet * 4, 4);
            int r0 = parameters[0], r1 = parameters[1];
            int width = hEnd - hStart;
            int w8 = (width + 2 + 7) & ~7, bs = w8 + 8, ws = bs + 8;
            Window(hStart, width, v, h, ws);
            int count = (bs * (h + 2)) + 16;
            Grow(ref _a, count);
            Grow(ref _b, count);
            Grow(ref _columns, ws + 16);
            Grow(ref _squareColumns, ws + 16);
            int fw = (width + 7) & ~7;
            if (r0 > 0)
            {
                Grow(ref _flt0, (fw * h) + 16);
                BoxVector(ws, bs, w8, width, h, r0, parameters[2], fast: true, _flt0);
            }

            if (r1 > 0)
            {
                Grow(ref _flt1, (fw * h) + 16);
                BoxVector(ws, bs, w8, width, h, r1, parameters[3], fast: false, _flt1);
            }

            // av1_decode_xq
            int xq0 = r0 == 0 ? 0 : unit.Xqd0;
            int xq1 = r0 == 0 ? 128 - unit.Xqd1 : r1 == 0 ? 0 : 128 - unit.Xqd0 - unit.Xqd1;
            Vector256<int> xq0V = Vector256.Create(xq0), xq1V = Vector256.Create(xq1), round = Vector256.Create(1 << 10);
            Vector256<int> max = Vector256.Create((1 << _bitDepth) - 1);
            ref short w0 = ref MemoryMarshal.GetArrayDataReference(_window);
            ref int f0 = ref MemoryMarshal.GetArrayDataReference(_flt0), f1 = ref MemoryMarshal.GetArrayDataReference(_flt1);
            Span<T> tail = stackalloc T[16];
            for (int i = 0; i < h; i++)
            {
                Span<T> line = _output.AsSpan(((v + i) * _stride) + hStart, width);
                for (int j = 0; j < fw; j += 8)
                {
                    Vector256<int> u = Vector256.ShiftLeft(Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref w0, (nuint)(((i + 3) * ws) + j + 3))), 4);
                    Vector256<int> value = Vector256.ShiftLeft(u, 7);
                    if (r0 > 0)
                    {
                        value += xq0V * (Vector256.LoadUnsafe(ref f0, (nuint)((i * fw) + j)) - u);
                    }

                    if (r1 > 0)
                    {
                        value += xq1V * (Vector256.LoadUnsafe(ref f1, (nuint)((i * fw) + j)) - u);
                    }

                    // (short) of the rounded value, then clamped.
                    Vector256<int> w = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(Vector256.ShiftRightArithmetic(value + round, 11), 16), 16);
                    w = Vector256.Min(Vector256.Max(w, Vector256<int>.Zero), max);
                    Store8(w, line, j, tail);
                }
            }
        }

        /// <summary>
        /// <see cref="Box"/> eight positions at a time: column sums of the 2r + 1 rows, then the box sums from them, a and b at
        /// positions -1 to <paramref name="width"/> of each row the radius needs, then the weighted neighbours.
        /// </summary>
        private unsafe void BoxVector(int ws, int bs, int w8, int width, int height, int r, int s, bool fast, int[] output)
        {
            int n = ((2 * r) + 1) * ((2 * r) + 1);
            int bd = _bitDepth;
            ref short w0 = ref MemoryMarshal.GetArrayDataReference(_window);
            ref int c0 = ref MemoryMarshal.GetArrayDataReference(_columns), q0 = ref MemoryMarshal.GetArrayDataReference(_squareColumns);
            ref int a0 = ref MemoryMarshal.GetArrayDataReference(_a), b0 = ref MemoryMarshal.GetArrayDataReference(_b);
            Vector256<int> nV = Vector256.Create(n), sV = Vector256.Create(s), half20 = Vector256.Create(1 << 19), max255 = Vector256.Create(255);
            Vector256<int> v256 = Vector256.Create(256), oneByX = Vector256.Create((int)Av1Tables.OneByX[n - 1]), half12 = Vector256.Create(1 << 11);
            int sumBits = bd - 8, squareBits = 2 * (bd - 8);
            fixed (int* table = XByXPlus1)
            {
                for (int i = -1; i < height + 1; i += fast ? 2 : 1)
                {
                    // Window columns 0 to bs + 4 summed over rows i - r to i + r (window rows i - r + 3 on).
                    for (int x = 0; x < bs + 8; x += 16)
                    {
                        Vector256<int> sumLo = Vector256<int>.Zero, sumHi = Vector256<int>.Zero, sqLo = Vector256<int>.Zero, sqHi = Vector256<int>.Zero;
                        for (int dy = -r; dy <= r; dy++)
                        {
                            Vector256<short> row = Vector256.LoadUnsafe(ref w0, (nuint)(((i + dy + 3) * ws) + x));
                            Vector256<int> lo = Avx2.ConvertToVector256Int32(row.GetLower()), hi = Avx2.ConvertToVector256Int32(row.GetUpper());
                            sumLo += lo;
                            sumHi += hi;
                            sqLo += lo * lo;
                            sqHi += hi * hi;
                        }

                        sumLo.StoreUnsafe(ref c0, (nuint)x);
                        sumHi.StoreUnsafe(ref c0, (nuint)(x + 8));
                        sqLo.StoreUnsafe(ref q0, (nuint)x);
                        sqHi.StoreUnsafe(ref q0, (nuint)(x + 8));
                    }

                    // Position j (from -1) sums columns j - r + 3 to j + r + 3, stored at k = (i + 1) * bs + j + 1.
                    int rowAt = (i + 1) * bs;
                    for (int j = 0; j < w8; j += 8)
                    {
                        Vector256<int> sum = Vector256<int>.Zero, squares = Vector256<int>.Zero;
                        for (int dx = -r; dx <= r; dx++)
                        {
                            sum += Vector256.LoadUnsafe(ref c0, (nuint)(j + 2 + dx));
                            squares += Vector256.LoadUnsafe(ref q0, (nuint)(j + 2 + dx));
                        }

                        Vector256<int> aa = RoundPower(squares, squareBits), bb = RoundPower(sum, sumBits);
                        Vector256<int> an = aa * nV, b2 = bb * bb;
                        Vector256<int> p = Vector256.AndNot(an - b2, Vector256.LessThan(an.AsUInt32(), b2.AsUInt32()).AsInt32());
                        Vector256<uint> z = Vector256.ShiftRightLogical(((p * sV) + half20).AsUInt32(), 20);
                        Vector256<int> a = Avx2.GatherVector256(table, Vector256.Min(z, max255.AsUInt32()).AsInt32(), 4);
                        a.StoreUnsafe(ref a0, (nuint)(rowAt + j));
                        Vector256.ShiftRightLogical(((v256 - a) * sum * oneByX + half12).AsUInt32(), 12).AsInt32().StoreUnsafe(ref b0, (nuint)(rowAt + j));
                    }
                }
            }

            int fw = (width + 7) & ~7;
            ref int o0 = ref MemoryMarshal.GetArrayDataReference(output);
            Vector256<int> three = Vector256.Create(3), four = Vector256.Create(4), five = Vector256.Create(5), six = Vector256.Create(6);
            for (int i = 0; i < height; i++)
            {
                int shift = !fast || (i & 1) == 0 ? 9 : 8;
                Vector256<int> half = Vector256.Create(1 << (shift - 1));
                for (int j = 0; j < fw; j += 8)
                {
                    int k = ((i + 1) * bs) + j + 1;
                    Vector256<int> source = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref w0, (nuint)(((i + 3) * ws) + j + 3)));
                    Vector256<int> av, bv;
                    if (!fast)
                    {
                        av = (Cross(ref a0, k, bs) * four) + (Corners(ref a0, k, bs) * three);
                        bv = (Cross(ref b0, k, bs) * four) + (Corners(ref b0, k, bs) * three);
                    }
                    else if ((i & 1) == 0)
                    {
                        av = ((At(ref a0, k - bs) + At(ref a0, k + bs)) * six) + (Corners(ref a0, k, bs) * five);
                        bv = ((At(ref b0, k - bs) + At(ref b0, k + bs)) * six) + (Corners(ref b0, k, bs) * five);
                    }
                    else
                    {
                        av = (At(ref a0, k) * six) + ((At(ref a0, k - 1) + At(ref a0, k + 1)) * five);
                        bv = (At(ref b0, k) * six) + ((At(ref b0, k - 1) + At(ref b0, k + 1)) * five);
                    }

                    Vector256.ShiftRightArithmetic((av * source) + bv + half, shift).StoreUnsafe(ref o0, (nuint)((i * fw) + j));
                }
            }

            static Vector256<int> At(ref int p, int k) => Vector256.LoadUnsafe(ref p, (nuint)k);

            static Vector256<int> Cross(ref int p, int k, int bs) => At(ref p, k) + At(ref p, k - 1) + At(ref p, k + 1) + At(ref p, k - bs) + At(ref p, k + bs);

            static Vector256<int> Corners(ref int p, int k, int bs) => At(ref p, k - 1 - bs) + At(ref p, k - 1 + bs) + At(ref p, k + 1 - bs) + At(ref p, k + 1 + bs);

            static Vector256<int> RoundPower(Vector256<int> value, int bits) =>
                bits == 0 ? value : Vector256.ShiftRightArithmetic(value + Vector256.Create(1 << (bits - 1)), bits);
        }

        /// <summary>Adds taps m and m + 1 of sixteen outputs, from samples <paramref name="step"/> apart: outputs 0-3 and 8-11 to <paramref name="lo"/>, 4-7 and 12-15 to <paramref name="hi"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Taps(ref short p, int m, int step, Vector256<short> taps, ref Vector256<int> lo, ref Vector256<int> hi)
        {
            Vector256<short> a = Vector256.LoadUnsafe(ref p, (nuint)(m * step)), b = Vector256.LoadUnsafe(ref p, (nuint)((m + 1) * step));
            lo = Avx2.Add(lo, Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a, b), taps));
            hi = Avx2.Add(hi, Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a, b), taps));
        }

        /// <summary>Two taps repeated for vpmaddwd: the first in the low half of each 32-bit lane.</summary>
        private static Vector256<short> Pair(int first, int second) => Vector256.Create((int)(ushort)first | (second << 16)).AsInt16();

        /// <summary>Stores sixteen samples at <paramref name="x"/> of <paramref name="line"/>, or those before its end.</summary>
        private static void Store16(Vector256<short> samples, Span<T> line, int x, Span<T> tail)
        {
            Span<T> to = x + 16 <= line.Length ? line.Slice(x, 16) : tail;
            if (Av1Samples.IsByte<T>())
            {
                Sse2.PackUnsignedSaturate(samples.GetLower(), samples.GetUpper()).StoreUnsafe(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(to)));
            }
            else
            {
                samples.StoreUnsafe(ref Unsafe.As<T, short>(ref MemoryMarshal.GetReference(to)));
            }

            if (x + 16 > line.Length)
            {
                tail[..(line.Length - x)].CopyTo(line[x..]);
            }
        }

        /// <summary>Stores eight samples, each 0 to the largest value, at <paramref name="x"/> of <paramref name="line"/>, or those before its end.</summary>
        private static void Store8(Vector256<int> samples, Span<T> line, int x, Span<T> tail)
        {
            Span<T> to = x + 8 <= line.Length ? line.Slice(x, 8) : tail;
            Vector128<ushort> words = Sse41.PackUnsignedSaturate(samples.GetLower(), samples.GetUpper());
            if (Av1Samples.IsByte<T>())
            {
                Unsafe.WriteUnaligned(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(to)), Sse2.PackUnsignedSaturate(words.AsInt16(), words.AsInt16()).AsUInt64().ToScalar());
            }
            else
            {
                words.StoreUnsafe(ref Unsafe.As<T, ushort>(ref MemoryMarshal.GetReference(to)));
            }

            if (x + 8 > line.Length)
            {
                tail[..(line.Length - x)].CopyTo(line[x..]);
            }
        }

        private static void Grow<TItem>(ref TItem[] array, int length)
        {
            if (array.Length < length)
            {
                array = new TItem[length];
            }
        }
    }
}
