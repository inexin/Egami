using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Av1;

/// <summary>
/// The loop filter on 8-bit planes with AVX2, sixteen samples along an edge line at a time in 16-bit lanes: the edges of four
/// 4x4 units side by side, each lane with its unit's filter length and limits. The edges of one direction never touch each
/// other's samples, so the four filter together as <see cref="FilterEdge"/> filters each.
/// </summary>
internal static partial class Av1LoopFilter
{
    /// <summary>Byte i of the low half takes the low four 16-bit values' value i / 4, of the high half value 2 + i / 4.</summary>
    private static ReadOnlySpan<byte> ExpandPattern => [0, 1, 0, 1, 0, 1, 0, 1, 2, 3, 2, 3, 2, 3, 2, 3, 4, 5, 4, 5, 4, 5, 4, 5, 6, 7, 6, 7, 6, 7, 6, 7];

    /// <summary>Whether a plane of <typeparamref name="T"/> samples is filtered sixteen samples at a time.</summary>
    private static bool Runs<T>(int bitDepth)
        where T : unmanaged => Av1Samples.IsByte<T>() && bitDepth == 8 && Avx2.IsSupported;

    /// <summary>
    /// <see cref="FilterPlane"/> four units at a time: vertical edges a unit column of four unit rows at once, horizontal edges a
    /// unit row of four unit columns. A run that would read past the plane's rows falls back to <see cref="FilterEdge"/>.
    /// </summary>
    private static void FilterPlaneRuns(byte[] buf, int stride, int[] units, int planeMiRows, int planeMiCols, int plane, int width, int height, int sharpness,
        bool vertical, Av1Frame frame)
    {
        if (vertical)
        {
            frame.For((planeMiRows + 15) >> 4, band =>
            {
                Span<int> next = stackalloc int[4], lengths = stackalloc int[4], levels = stackalloc int[4];
                for (int y0 = band * 16; y0 < Math.Min(planeMiRows, (band + 1) * 16); y0 += 4)
                {
                    for (int r = 0; r < 4; r++)
                    {
                        next[r] = y0 + r < planeMiRows ? 0 : int.MaxValue;
                    }

                    while (true)
                    {
                        int x = Math.Min(Math.Min(next[0], next[1]), Math.Min(next[2], next[3]));
                        if (x >= planeMiCols)
                        {
                            break;
                        }

                        bool any = false;
                        for (int r = 0; r < 4; r++)
                        {
                            lengths[r] = 0;
                            if (next[r] == x)
                            {
                                (int txSize, lengths[r], levels[r]) = EdgeParameters(units, planeMiCols, plane, true, x, y0 + r, width, height);
                                next[r] += Av1Tables.TxSizeWideUnit[txSize];
                                any |= lengths[r] != 0;
                            }
                        }

                        if (!any)
                        {
                            continue;
                        }

                        if (x * 4 >= 8 && (x * 4) + 8 <= stride)
                        {
                            VerticalRun(buf, (y0 * 4 * stride) + (x * 4), stride, lengths, levels, sharpness);
                            continue;
                        }

                        for (int r = 0; r < 4; r++)
                        {
                            if (lengths[r] != 0)
                            {
                                FilterEdge(buf, ((y0 + r) * 4 * stride) + (x * 4), 1, stride, lengths[r], levels[r], sharpness, 8);
                            }
                        }
                    }
                }
            });
            return;
        }

        // A column's edges go top to bottom, and columns do not touch each other: rows of the band at a time, for the cache.
        frame.For((planeMiCols + 15) >> 4, band =>
        {
            int first = band * 16, count = Math.Min(planeMiCols - first, 16);
            Span<int> next = stackalloc int[16], lengths = stackalloc int[4], levels = stackalloc int[4];
            next.Clear();
            for (int y = 0; y < planeMiRows; y++)
            {
                for (int g = 0; g < count; g += 4)
                {
                    bool any = false;
                    for (int j = 0; j < 4; j++)
                    {
                        int i = g + j;
                        lengths[j] = 0;
                        if (i < count && next[i] == y)
                        {
                            (int txSize, lengths[j], levels[j]) = EdgeParameters(units, planeMiCols, plane, false, first + i, y, width, height);
                            next[i] = y + Av1Tables.TxSizeHighUnit[txSize];
                            any |= lengths[j] != 0;
                        }
                    }

                    if (any)
                    {
                        HorizontalRun(buf, (y * 4 * stride) + ((first + g) * 4), stride, lengths, levels, sharpness);
                    }
                }
            }
        });
    }

    /// <summary>The vertical edges at sample <paramref name="at"/> and the fifteen rows below it: one unit column of four unit rows.</summary>
    private static void VerticalRun(byte[] s, int at, int stride, ReadOnlySpan<int> lengths, ReadOnlySpan<int> levels, int sharpness)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)(at + (15 * stride) + 8), (uint)s.Length, nameof(at));
        ArgumentOutOfRangeException.ThrowIfLessThan(at - 8, 0, nameof(at));
        Span<Vector128<byte>> rows = stackalloc Vector128<byte>[16];
        ref byte origin = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s), at - 8);
        for (int r = 0; r < 16; r++)
        {
            rows[r] = Vector128.LoadUnsafe(ref origin, (nuint)(r * stride));
        }

        // Column j of the sixteen, at -8 + j, is v[j - 1] once turned: v[7 + k] is q_k, v[6 - k] is p_k.
        Transpose16(rows);
        int taps = Taps(lengths);
        Span<Vector256<short>> v = stackalloc Vector256<short>[14];
        for (int k = -taps; k < taps; k++)
        {
            v[7 + k] = Avx2.ConvertToVector256Int16(rows[8 + k]);
        }

        Span<Vector256<short>> o = stackalloc Vector256<short>[14];
        if (!Filter16(v, o, taps, lengths, levels, sharpness))
        {
            return;
        }

        for (int k = -taps; k < taps; k++)
        {
            rows[8 + k] = Sse2.PackUnsignedSaturate(o[7 + k].GetLower(), o[7 + k].GetUpper());
        }

        Transpose16(rows);
        for (int r = 0; r < 16; r++)
        {
            rows[r].StoreUnsafe(ref origin, (nuint)(r * stride));
        }
    }

    /// <summary>The horizontal edges of four units side by side, the first's top-left sample at <paramref name="at"/>.</summary>
    private static void HorizontalRun(byte[] s, int at, int stride, ReadOnlySpan<int> lengths, ReadOnlySpan<int> levels, int sharpness)
    {
        int taps = Taps(lengths);
        ArgumentOutOfRangeException.ThrowIfLessThan(at - (taps * stride), 0, nameof(at));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)(at + ((taps - 1) * stride) + 16), (uint)s.Length, nameof(at));
        ref byte origin = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s), at);
        Span<Vector256<short>> v = stackalloc Vector256<short>[14];
        for (int k = -taps; k < taps; k++)
        {
            v[7 + k] = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref Unsafe.Add(ref origin, k * stride)));
        }

        Span<Vector256<short>> o = stackalloc Vector256<short>[14];
        if (!Filter16(v, o, taps, lengths, levels, sharpness))
        {
            return;
        }

        for (int k = -taps; k < taps; k++)
        {
            Sse2.PackUnsignedSaturate(o[7 + k].GetLower(), o[7 + k].GetUpper()).StoreUnsafe(ref Unsafe.Add(ref origin, k * stride));
        }
    }

    /// <summary>The samples on each side the longest of the four filters reads.</summary>
    private static int Taps(ReadOnlySpan<int> lengths)
    {
        int longest = Math.Max(Math.Max(lengths[0], lengths[1]), Math.Max(lengths[2], lengths[3]));
        return longest == 14 ? 7 : longest == 8 ? 4 : longest == 6 ? 3 : 2;
    }

    /// <summary>Four values, one a unit, as sixteen lanes, four a unit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Expand(int a, int b, int c, int d)
    {
        long packed = (ushort)a | ((long)(ushort)b << 16) | ((long)(ushort)c << 32) | ((long)(ushort)d << 48);
        return Avx2.Shuffle(Vector256.Create(packed).AsByte(), Vector256.Create(ExpandPattern)).AsInt16();
    }

    /// <summary>
    /// <see cref="FilterEdgeVector"/> on sixteen lanes in 16-bit: the masks of each lane's filter length, every filter the longest
    /// length allows, each lane given the one its masks choose. Returns false, leaving <paramref name="o"/> unset, when no lane
    /// changes.
    /// </summary>
    private static bool Filter16(ReadOnlySpan<Vector256<short>> v, Span<Vector256<short>> o, int taps, ReadOnlySpan<int> lengths, ReadOnlySpan<int> levels,
        int sharpness)
    {
        Span<int> limits = stackalloc int[12];
        for (int i = 0; i < 4; i++)
        {
            (limits[i], limits[4 + i], limits[8 + i]) = Limits(levels[i], sharpness);
        }

        Vector256<short> length = Expand(lengths[0], lengths[1], lengths[2], lengths[3]);
        Vector256<short> limit = Expand(limits[0], limits[1], limits[2], limits[3]);
        Vector256<short> blimit = Expand(limits[4], limits[5], limits[6], limits[7]);
        Vector256<short> one = Vector256<short>.One;
        Vector256<short> p0 = v[6], p1 = v[5], q0 = v[7], q1 = v[8];
        // Lanes failing a limit; then the lanes that filter.
        Vector256<short> fails = Gt(Vector256.Abs(p1 - p0), limit) | Gt(Vector256.Abs(q1 - q0), limit)
            | Gt((Vector256.Abs(p0 - q0) * 2) + Vector256.ShiftRightArithmetic(Vector256.Abs(p1 - q1), 1), blimit);
        Vector256<short> flat = Vector256<short>.Zero, flat2 = Vector256<short>.Zero;
        if (taps >= 3)
        {
            // Lanes of length 6 or more; 8 or more; 14.
            Vector256<short> six = Gt(length, Vector256.Create((short)5));
            Vector256<short> p2 = v[4], q2 = v[9];
            fails |= six & (Gt(Vector256.Abs(p2 - p1), limit) | Gt(Vector256.Abs(q2 - q1), limit));
            Vector256<short> rough = Gt(Vector256.Abs(p1 - p0), one) | Gt(Vector256.Abs(q1 - q0), one) | Gt(Vector256.Abs(p2 - p0), one) | Gt(Vector256.Abs(q2 - q0), one);
            if (taps >= 4)
            {
                Vector256<short> eight = Gt(length, Vector256.Create((short)7));
                Vector256<short> p3 = v[3], q3 = v[10];
                fails |= eight & (Gt(Vector256.Abs(p3 - p2), limit) | Gt(Vector256.Abs(q3 - q2), limit));
                rough |= eight & (Gt(Vector256.Abs(p3 - p0), one) | Gt(Vector256.Abs(q3 - q0), one));
            }

            flat = Vector256.AndNot(six, rough);
            if (taps == 7)
            {
                Vector256<short> rough2 = Vector256<short>.Zero;
                for (int k = 4; k < 7; k++)
                {
                    rough2 |= Gt(Vector256.Abs(v[6 - k] - p0), one) | Gt(Vector256.Abs(v[7 + k] - q0), one);
                }

                flat2 = Vector256.AndNot(Vector256.Equals(length, Vector256.Create((short)14)) & flat, rough2);
            }
        }

        Vector256<short> mask = Vector256.AndNot(~Vector256.Equals(length, Vector256<short>.Zero), fails);
        if (mask == Vector256<short>.Zero)
        {
            return false;
        }

        v.CopyTo(o);
        Filter4(v, o, Vector256.AndNot(mask, flat), Expand(limits[8], limits[9], limits[10], limits[11]));
        Vector256<short> f = mask & flat;
        if (f != Vector256<short>.Zero)
        {
            Vector256<short> p2 = v[4], q2 = v[9], four = Vector256.Create((short)4);
            f = Vector256.AndNot(f, flat2);
            if (taps == 3)
            {
                // Only chroma has the 6-tap filter, and chroma has none longer.
                o[5] = Select(f, Vector256.ShiftRightArithmetic((p2 * 3) + (p1 * 2) + (p0 * 2) + q0 + four, 3), o[5]);
                o[6] = Select(f, Vector256.ShiftRightArithmetic(p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + four, 3), o[6]);
                o[7] = Select(f, Vector256.ShiftRightArithmetic(p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + four, 3), o[7]);
                o[8] = Select(f, Vector256.ShiftRightArithmetic(p0 + (q0 * 2) + (q1 * 2) + (q2 * 3) + four, 3), o[8]);
            }
            else
            {
                Vector256<short> p3 = v[3], q3 = v[10];
                o[4] = Select(f, Vector256.ShiftRightArithmetic(p3 + p3 + p3 + (p2 * 2) + p1 + p0 + q0 + four, 3), o[4]);
                o[5] = Select(f, Vector256.ShiftRightArithmetic(p3 + p3 + p2 + (p1 * 2) + p0 + q0 + q1 + four, 3), o[5]);
                o[6] = Select(f, Vector256.ShiftRightArithmetic(p3 + p2 + p1 + (p0 * 2) + q0 + q1 + q2 + four, 3), o[6]);
                o[7] = Select(f, Vector256.ShiftRightArithmetic(p2 + p1 + p0 + (q0 * 2) + q1 + q2 + q3 + four, 3), o[7]);
                o[8] = Select(f, Vector256.ShiftRightArithmetic(p1 + p0 + q0 + (q1 * 2) + q2 + q3 + q3 + four, 3), o[8]);
                o[9] = Select(f, Vector256.ShiftRightArithmetic(p0 + q0 + q1 + (q2 * 2) + q3 + q3 + q3 + four, 3), o[9]);
                Vector256<short> w = mask & flat2;
                if (w != Vector256<short>.Zero)
                {
                    Wide(v, o, w);
                }
            }
        }

        return true;

        static Vector256<short> Gt(Vector256<short> a, Vector256<short> b) => Vector256.GreaterThan(a, b);

        static Vector256<short> Select(Vector256<short> m, Vector256<short> a, Vector256<short> b) => Vector256.ConditionalSelect(m, a, b);
    }

    /// <summary>An 8-bit edge's limit, blimit and high edge variance threshold at <paramref name="level"/>, as <see cref="FilterEdge"/> works them out.</summary>
    private static (int Limit, int Blimit, int Thresh) Limits(int level, int sharpness)
    {
        int limit = level >> ((sharpness > 0 ? 1 : 0) + (sharpness > 4 ? 1 : 0));
        if (sharpness > 0 && limit > 9 - sharpness)
        {
            limit = 9 - sharpness;
        }

        limit = Math.Max(limit, 1);
        return (limit, (2 * (level + 2)) + limit, level >> 4);
    }

    /// <summary>filter4 on 8-bit lanes, applied where <paramref name="m"/> is set.</summary>
    private static void Filter4(ReadOnlySpan<Vector256<short>> v, Span<Vector256<short>> o, Vector256<short> m, Vector256<short> threshold)
    {
        Vector256<short> offset = Vector256.Create((short)0x80), lo = Vector256.Create((short)-128), hi = Vector256.Create((short)127);
        Vector256<short> ps1 = v[5] - offset, ps0 = v[6] - offset, qs0 = v[7] - offset, qs1 = v[8] - offset;
        Vector256<short> hev = Vector256.GreaterThan(Vector256.Abs(ps1 - ps0), threshold) | Vector256.GreaterThan(Vector256.Abs(qs1 - qs0), threshold);
        Vector256<short> filter = hev & Clamp(ps1 - qs1, lo, hi);
        filter = Clamp(filter + ((qs0 - ps0) * 3), lo, hi);
        Vector256<short> filter1 = Vector256.ShiftRightArithmetic(Clamp(filter + Vector256.Create((short)4), lo, hi), 3);
        Vector256<short> filter2 = Vector256.ShiftRightArithmetic(Clamp(filter + Vector256.Create((short)3), lo, hi), 3);
        o[7] = Vector256.ConditionalSelect(m, Clamp(qs0 - filter1, lo, hi) + offset, o[7]);
        o[6] = Vector256.ConditionalSelect(m, Clamp(ps0 + filter2, lo, hi) + offset, o[6]);
        filter = Vector256.AndNot(Vector256.ShiftRightArithmetic(filter1 + Vector256<short>.One, 1), hev);
        o[8] = Vector256.ConditionalSelect(m, Clamp(qs1 - filter, lo, hi) + offset, o[8]);
        o[5] = Vector256.ConditionalSelect(m, Clamp(ps1 + filter, lo, hi) + offset, o[5]);

        static Vector256<short> Clamp(Vector256<short> x, Vector256<short> lo, Vector256<short> hi) => Vector256.Min(Vector256.Max(x, lo), hi);
    }

    /// <summary>The 14-tap filter's outputs on 8-bit lanes, applied where <paramref name="m"/> is set.</summary>
    private static void Wide(ReadOnlySpan<Vector256<short>> v, Span<Vector256<short>> o, Vector256<short> m)
    {
        Vector256<short> p6 = v[0], p5 = v[1], p4 = v[2], p3 = v[3], p2 = v[4], p1 = v[5], p0 = v[6];
        Vector256<short> q0 = v[7], q1 = v[8], q2 = v[9], q3 = v[10], q4 = v[11], q5 = v[12], q6 = v[13];
        Vector256<short> eight = Vector256.Create((short)8);
        o[1] = Pick(m, (p6 * 7) + (p5 * 2) + (p4 * 2) + p3 + p2 + p1 + p0 + q0 + eight, o[1]);
        o[2] = Pick(m, (p6 * 5) + (p5 * 2) + (p4 * 2) + (p3 * 2) + p2 + p1 + p0 + q0 + q1 + eight, o[2]);
        o[3] = Pick(m, (p6 * 4) + p5 + (p4 * 2) + (p3 * 2) + (p2 * 2) + p1 + p0 + q0 + q1 + q2 + eight, o[3]);
        o[4] = Pick(m, (p6 * 3) + p5 + p4 + (p3 * 2) + (p2 * 2) + (p1 * 2) + p0 + q0 + q1 + q2 + q3 + eight, o[4]);
        o[5] = Pick(m, (p6 * 2) + p5 + p4 + p3 + (p2 * 2) + (p1 * 2) + (p0 * 2) + q0 + q1 + q2 + q3 + q4 + eight, o[5]);
        o[6] = Pick(m, p6 + p5 + p4 + p3 + p2 + (p1 * 2) + (p0 * 2) + (q0 * 2) + q1 + q2 + q3 + q4 + q5 + eight, o[6]);
        o[7] = Pick(m, p5 + p4 + p3 + p2 + p1 + (p0 * 2) + (q0 * 2) + (q1 * 2) + q2 + q3 + q4 + q5 + q6 + eight, o[7]);
        o[8] = Pick(m, p4 + p3 + p2 + p1 + p0 + (q0 * 2) + (q1 * 2) + (q2 * 2) + q3 + q4 + q5 + (q6 * 2) + eight, o[8]);
        o[9] = Pick(m, p3 + p2 + p1 + p0 + q0 + (q1 * 2) + (q2 * 2) + (q3 * 2) + q4 + q5 + (q6 * 3) + eight, o[9]);
        o[10] = Pick(m, p2 + p1 + p0 + q0 + q1 + (q2 * 2) + (q3 * 2) + (q4 * 2) + q5 + (q6 * 4) + eight, o[10]);
        o[11] = Pick(m, p1 + p0 + q0 + q1 + q2 + (q3 * 2) + (q4 * 2) + (q5 * 2) + (q6 * 5) + eight, o[11]);
        o[12] = Pick(m, p0 + q0 + q1 + q2 + q3 + (q4 * 2) + (q5 * 2) + (q6 * 7) + eight, o[12]);

        static Vector256<short> Pick(Vector256<short> m, Vector256<short> sum, Vector256<short> other) =>
            Vector256.ConditionalSelect(m, Vector256.ShiftRightArithmetic(sum, 4), other);
    }

    /// <summary>
    /// Transposes sixteen rows of sixteen bytes in place. Each pass interleaves row i with row i + 8, which rotates the bits of
    /// (row, column) left by one; four passes swap them.
    /// </summary>
    private static void Transpose16(Span<Vector128<byte>> rows)
    {
        Span<Vector128<byte>> t = stackalloc Vector128<byte>[16];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < 8; i++)
            {
                t[2 * i] = Sse2.UnpackLow(rows[i], rows[i + 8]);
                t[(2 * i) + 1] = Sse2.UnpackHigh(rows[i], rows[i + 8]);
            }

            for (int i = 0; i < 8; i++)
            {
                rows[2 * i] = Sse2.UnpackLow(t[i], t[i + 8]);
                rows[(2 * i) + 1] = Sse2.UnpackHigh(t[i], t[i + 8]);
            }
        }
    }
}
