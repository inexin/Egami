using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The loop filters of <see cref="Vp9LoopFilter{T}"/> eight pixels along an edge at a time, one 16-bit lane each, with the
/// same values: every candidate output is computed for every lane, then each lane keeps the one its masks pick. Pixels across a
/// horizontal edge are rows, loaded directly; across a vertical edge they are gathered into rows first and written back after.
/// </summary>
[SkipLocalsInit]
internal static class Vp9LoopFilterSimd
{
    /// <summary>Gets whether the vector form runs here; otherwise the scalar one does.</summary>
    public static bool IsSupported => Sse2.IsSupported;

    /// <summary>Gets whether the dual filters run here: they need AVX2.</summary>
    public static bool IsDualSupported => Avx2.IsSupported && Vector256.IsHardwareAccelerated;

    /// <summary>
    /// Filters <paramref name="count"/> (8 or 16) pixels along an edge at <paramref name="s"/> with the 4-, 8- or 16-wide filter
    /// (<paramref name="width"/>), as Edge4, Edge8 and Edge16 do. <paramref name="across"/> is 1 for a vertical edge.
    /// </summary>
    public static void Filter(Span<byte> p, int s, int across, int along, int count, int width, int limit, int blimit, int thresh) =>
        Filter<Bytes>(ref MemoryMarshal.GetReference(p), p.Length, s, across, along, count, width, limit, blimit, thresh, 0);

    /// <summary>The 10- and 12-bit <see cref="Filter(Span{byte}, int, int, int, int, int, int, int, int)"/>, limits already scaled to the bit depth.</summary>
    public static void Filter(Span<ushort> p, int s, int across, int along, int count, int width, int limit, int blimit, int thresh, int bitDepth) =>
        Filter<Words>(ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(p)), p.Length, s, across, along, count, width, limit, blimit, thresh,
            bitDepth - 8);

    /// <summary>
    /// Filters 16 pixels along an edge at <paramref name="s"/>: two runs of eight, the second <paramref name="along"/> × 8 on,
    /// with their own filter levels' limits (libvpx's dual filters). <paramref name="width"/> is 4, 8 or 16.
    /// </summary>
    public static void Dual(Span<byte> p, int s, int across, int along, int width, int limit0, int blimit0, int thresh0, int limit1, int blimit1,
        int thresh1) =>
        Dual<Bytes>(ref MemoryMarshal.GetReference(p), p.Length, s, across, along, width, limit0, blimit0, thresh0, limit1, blimit1, thresh1, 0);

    /// <summary>The 10- and 12-bit <see cref="Dual(Span{byte}, int, int, int, int, int, int, int, int, int, int)"/>, limits already scaled.</summary>
    public static void Dual(Span<ushort> p, int s, int across, int along, int width, int limit0, int blimit0, int thresh0, int limit1, int blimit1,
        int thresh1, int bitDepth) =>
        Dual<Words>(ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(p)), p.Length, s, across, along, width, limit0, blimit0, thresh0,
            limit1, blimit1, thresh1, bitDepth - 8);

    /// <summary>Checks that an edge's pixels lie in the plane of <paramref name="length"/> samples.</summary>
    private static void Check(int length, int s, int across, int along, int reach, int count)
    {
        int first = s - (reach * across);
        int last = s + ((reach - 1) * across) + ((count - 1) * along);
        if (across <= 0 || along <= 0 || first < 0 || last >= length)
        {
            throw new ArgumentOutOfRangeException(nameof(s));
        }
    }

    private static void Filter<TP>(ref byte p, int length, int s, int across, int along, int count, int width, int limit, int blimit, int thresh,
        int shift)
        where TP : struct, IPixels
    {
        int reach = width == 16 ? 8 : 4;
        Check(length, s, across, along, reach, count);
        int size = TP.Size;
        Span<byte> rows = stackalloc byte[16 * 8 * 2];
        for (int start = 0; start < count; start += 8)
        {
            int at = s + (start * along);
            if (across == 1)
            {
                // A vertical edge: transpose the 8 rows by 2 × reach columns into rows of eight, filter, transpose back.
                ref byte src = ref Unsafe.Add(ref p, (at - reach) * size);
                ref byte buffer = ref MemoryMarshal.GetReference(rows);
                int pitch = 8 * size;
                for (int half = 0; half < reach / 4; half++)
                {
                    TP.Transpose8x8(ref Unsafe.Add(ref src, half * 8 * size), along * size, ref Unsafe.Add(ref buffer, half * 8 * pitch), pitch);
                }

                Run<TP>(ref buffer, pitch, reach, width, limit, blimit, thresh, shift);
                for (int half = 0; half < reach / 4; half++)
                {
                    TP.Transpose8x8(ref Unsafe.Add(ref buffer, half * 8 * pitch), pitch, ref Unsafe.Add(ref src, half * 8 * size), along * size);
                }
            }
            else
            {
                Run<TP>(ref Unsafe.Add(ref p, (at - (reach * across)) * size), across * size, reach, width, limit, blimit, thresh, shift);
            }
        }
    }

    private static void Dual<TP>(ref byte p, int length, int s, int across, int along, int width, int limit0, int blimit0, int thresh0,
        int limit1, int blimit1, int thresh1, int shift)
        where TP : struct, IPixels
    {
        int reach = width == 16 ? 8 : 4;
        Check(length, s, across, along, reach, 16);
        int size = TP.Size;
        Vector256<short> lim = Vector256.Create(Vector128.Create((short)limit0), Vector128.Create((short)limit1));
        Vector256<short> blim = Vector256.Create(Vector128.Create((short)blimit0), Vector128.Create((short)blimit1));
        Vector256<short> th = Vector256.Create(Vector128.Create((short)thresh0), Vector128.Create((short)thresh1));
        if (across != 1)
        {
            Run16<TP>(ref Unsafe.Add(ref p, (s - (reach * across)) * size), across * size, reach, width, lim, blim, th, shift);
            return;
        }

        // A vertical edge: 16 rows by 2 × reach columns transposed into rows of 16, filtered, transposed back.
        Span<byte> rows = stackalloc byte[16 * 16 * 2];
        ref byte src = ref Unsafe.Add(ref p, (s - reach) * size);
        ref byte buffer = ref MemoryMarshal.GetReference(rows);
        int pitch = 16 * size, stride = along * size;
        for (int half = 0; half < reach / 4; half++)
        {
            TP.Transpose8x8(ref Unsafe.Add(ref src, half * 8 * size), stride, ref Unsafe.Add(ref buffer, half * 8 * pitch), pitch);
            TP.Transpose8x8(ref Unsafe.Add(ref src, (half * 8 * size) + (8 * stride)), stride, ref Unsafe.Add(ref buffer, (half * 8 * pitch) + (8 * size)),
                pitch);
        }

        Run16<TP>(ref buffer, pitch, reach, width, lim, blim, th, shift);
        for (int half = 0; half < reach / 4; half++)
        {
            TP.Transpose8x8(ref Unsafe.Add(ref buffer, half * 8 * pitch), pitch, ref Unsafe.Add(ref src, half * 8 * size), stride);
            TP.Transpose8x8(ref Unsafe.Add(ref buffer, (half * 8 * pitch) + (8 * size)), pitch, ref Unsafe.Add(ref src, (half * 8 * size) + (8 * stride)),
                stride);
        }
    }

    /// <summary>
    /// Filters eight lanes whose 2 × <paramref name="reach"/> rows, p(reach - 1) first, start at <paramref name="row0"/>,
    /// <paramref name="pitch"/> bytes apart; <paramref name="shift"/> is the bit depth less 8, and the limits are scaled to it.
    /// </summary>
    private static void Run<TP>(ref byte row0, int pitch, int reach, int width, int limit, int blimit, int thresh, int shift)
        where TP : struct, IPixels
    {
        ref byte at = ref Unsafe.Add(ref row0, (reach - 4) * pitch);
        Vector128<short> p3 = TP.Load(ref at, 0), p2 = TP.Load(ref at, pitch), p1 = TP.Load(ref at, 2 * pitch), p0 = TP.Load(ref at, 3 * pitch);
        Vector128<short> q0 = TP.Load(ref at, 4 * pitch), q1 = TP.Load(ref at, 5 * pitch), q2 = TP.Load(ref at, 6 * pitch), q3 = TP.Load(ref at, 7 * pitch);
        Vector128<short> lim = Vector128.Create((short)limit);
        Vector128<short> mask = Le(Abs(p3 - p2), lim) & Le(Abs(p2 - p1), lim) & Le(Abs(p1 - p0), lim) & Le(Abs(q1 - q0), lim)
            & Le(Abs(q2 - q1), lim) & Le(Abs(q3 - q2), lim)
            & Le((Abs(p0 - q0) * 2) + Vector128.ShiftRightArithmetic(Abs(p1 - q1), 1), Vector128.Create((short)blimit));
        if (mask == Vector128<short>.Zero)
        {
            return;
        }

        // The 4-tap filter, in values centered on zero and clamped to the signed range of the bit depth.
        Vector128<short> c128 = Vector128.Create((short)(128 << shift));
        Vector128<short> lo = Vector128.Create((short)(-128 << shift)), hi = Vector128.Create((short)((128 << shift) - 1));
        Vector128<short> ps1 = p1 - c128, ps0 = p0 - c128, qs0 = q0 - c128, qs1 = q1 - c128;
        Vector128<short> th = Vector128.Create((short)thresh);
        Vector128<short> hev = Gt(Abs(p1 - p0), th) | Gt(Abs(q1 - q0), th);
        Vector128<short> filter = Clamp(lo, hi, ps1 - qs1) & hev;
        filter = Clamp(lo, hi, filter + (Vector128.Create((short)3) * (qs0 - ps0)));
        Vector128<short> filter1 = Vector128.ShiftRightArithmetic(Clamp(lo, hi, filter + Vector128.Create((short)4)), 3);
        Vector128<short> filter2 = Vector128.ShiftRightArithmetic(Clamp(lo, hi, filter + Vector128.Create((short)3)), 3);
        Vector128<short> f4q0 = Clamp(lo, hi, qs0 - filter1) + c128, f4p0 = Clamp(lo, hi, ps0 + filter2) + c128;
        Vector128<short> outer = Vector128.ShiftRightArithmetic(filter1 + Vector128<short>.One, 1);
        Vector128<short> f4q1 = Vector128.ConditionalSelect(hev, q1, Clamp(lo, hi, qs1 - outer) + c128);
        Vector128<short> f4p1 = Vector128.ConditionalSelect(hev, p1, Clamp(lo, hi, ps1 + outer) + c128);

        if (width == 4)
        {
            TP.Store(ref at, 2 * pitch, Vector128.ConditionalSelect(mask, f4p1, p1));
            TP.Store(ref at, 3 * pitch, Vector128.ConditionalSelect(mask, f4p0, p0));
            TP.Store(ref at, 4 * pitch, Vector128.ConditionalSelect(mask, f4q0, q0));
            TP.Store(ref at, 5 * pitch, Vector128.ConditionalSelect(mask, f4q1, q1));
            return;
        }

        Vector128<short> one = Vector128.Create((short)(1 << shift));
        Vector128<short> flat = mask & Le(Abs(p1 - p0), one) & Le(Abs(q1 - q0), one) & Le(Abs(p2 - p0), one) & Le(Abs(q2 - q0), one)
            & Le(Abs(p3 - p0), one) & Le(Abs(q3 - q0), one);
        if (flat == Vector128<short>.Zero)
        {
            TP.Store(ref at, 2 * pitch, Vector128.ConditionalSelect(mask, f4p1, p1));
            TP.Store(ref at, 3 * pitch, Vector128.ConditionalSelect(mask, f4p0, p0));
            TP.Store(ref at, 4 * pitch, Vector128.ConditionalSelect(mask, f4q0, q0));
            TP.Store(ref at, 5 * pitch, Vector128.ConditionalSelect(mask, f4q1, q1));
            return;
        }

        Vector128<short> filtered = mask & ~flat;

        // The 7-tap filter of flat lanes.
        Vector128<short> four = Vector128.Create((short)4);
        Vector128<short> o2 = Vector128.ShiftRightArithmetic(p3 + p3 + p3 + (p2 * 2) + p1 + p0 + q0 + four, 3);
        Vector128<short> o1 = Vector128.ShiftRightArithmetic(p3 + p3 + p2 + (p1 * 2) + p0 + q0 + q1 + four, 3);
        Vector128<short> o0 = Vector128.ShiftRightArithmetic(p3 + p2 + p1 + (p0 * 2) + q0 + q1 + q2 + four, 3);
        Vector128<short> u0 = Vector128.ShiftRightArithmetic(p2 + p1 + p0 + (q0 * 2) + q1 + q2 + q3 + four, 3);
        Vector128<short> u1 = Vector128.ShiftRightArithmetic(p1 + p0 + q0 + (q1 * 2) + q2 + q3 + q3 + four, 3);
        Vector128<short> u2 = Vector128.ShiftRightArithmetic(p0 + q0 + q1 + (q2 * 2) + q3 + q3 + q3 + four, 3);
        Vector128<short> np2 = Vector128.ConditionalSelect(flat, o2, p2);
        Vector128<short> np1 = Vector128.ConditionalSelect(flat, o1, Vector128.ConditionalSelect(filtered, f4p1, p1));
        Vector128<short> np0 = Vector128.ConditionalSelect(flat, o0, Vector128.ConditionalSelect(filtered, f4p0, p0));
        Vector128<short> nq0 = Vector128.ConditionalSelect(flat, u0, Vector128.ConditionalSelect(filtered, f4q0, q0));
        Vector128<short> nq1 = Vector128.ConditionalSelect(flat, u1, Vector128.ConditionalSelect(filtered, f4q1, q1));
        Vector128<short> nq2 = Vector128.ConditionalSelect(flat, u2, q2);

        if (width == 8)
        {
            TP.Store(ref at, pitch, np2);
            TP.Store(ref at, 2 * pitch, np1);
            TP.Store(ref at, 3 * pitch, np0);
            TP.Store(ref at, 4 * pitch, nq0);
            TP.Store(ref at, 5 * pitch, nq1);
            TP.Store(ref at, 6 * pitch, nq2);
            return;
        }

        // The 15-tap filter of lanes flat out to p7 and q7.
        Vector128<short> p7 = TP.Load(ref row0, 0), p6 = TP.Load(ref row0, pitch), p5 = TP.Load(ref row0, 2 * pitch), p4 = TP.Load(ref row0, 3 * pitch);
        Vector128<short> q4 = TP.Load(ref row0, 12 * pitch), q5 = TP.Load(ref row0, 13 * pitch), q6 = TP.Load(ref row0, 14 * pitch), q7 = TP.Load(ref row0, 15 * pitch);
        Vector128<short> flat2 = flat & Le(Abs(p4 - p0), one) & Le(Abs(q4 - q0), one) & Le(Abs(p5 - p0), one) & Le(Abs(q5 - q0), one)
            & Le(Abs(p6 - p0), one) & Le(Abs(q6 - q0), one) & Le(Abs(p7 - p0), one) & Le(Abs(q7 - q0), one);
        if (flat2 == Vector128<short>.Zero)
        {
            TP.Store(ref at, pitch, np2);
            TP.Store(ref at, 2 * pitch, np1);
            TP.Store(ref at, 3 * pitch, np0);
            TP.Store(ref at, 4 * pitch, nq0);
            TP.Store(ref at, 5 * pitch, nq1);
            TP.Store(ref at, 6 * pitch, nq2);
            return;
        }

        Vector128<short> eight = Vector128.Create((short)8);
        Vector128<short> w6 = Shift4(((p7 * 7) + (p6 * 2) + p5 + p4 + p3 + p2 + p1 + p0 + q0) + eight);
        Vector128<short> w5 = Shift4(((p7 * 6) + p6 + (p5 * 2) + p4 + p3 + p2 + p1 + p0 + q0 + q1) + eight);
        Vector128<short> w4 = Shift4(((p7 * 5) + p6 + p5 + (p4 * 2) + p3 + p2 + p1 + p0 + q0 + q1 + q2) + eight);
        Vector128<short> w3 = Shift4(((p7 * 4) + p6 + p5 + p4 + (p3 * 2) + p2 + p1 + p0 + q0 + q1 + q2 + q3) + eight);
        Vector128<short> w2 = Shift4(((p7 * 3) + p6 + p5 + p4 + p3 + (p2 * 2) + p1 + p0 + q0 + q1 + q2 + q3 + q4) + eight);
        Vector128<short> w1 = Shift4(((p7 * 2) + p6 + p5 + p4 + p3 + p2 + (p1 * 2) + p0 + q0 + q1 + q2 + q3 + q4 + q5) + eight);
        Vector128<short> w0 = Shift4((p7 + p6 + p5 + p4 + p3 + p2 + p1 + (p0 * 2) + q0 + q1 + q2 + q3 + q4 + q5 + q6) + eight);
        Vector128<short> v0 = Shift4((p6 + p5 + p4 + p3 + p2 + p1 + p0 + (q0 * 2) + q1 + q2 + q3 + q4 + q5 + q6 + q7) + eight);
        Vector128<short> v1 = Shift4((p5 + p4 + p3 + p2 + p1 + p0 + q0 + (q1 * 2) + q2 + q3 + q4 + q5 + q6 + (q7 * 2)) + eight);
        Vector128<short> v2 = Shift4((p4 + p3 + p2 + p1 + p0 + q0 + q1 + (q2 * 2) + q3 + q4 + q5 + q6 + (q7 * 3)) + eight);
        Vector128<short> v3 = Shift4((p3 + p2 + p1 + p0 + q0 + q1 + q2 + (q3 * 2) + q4 + q5 + q6 + (q7 * 4)) + eight);
        Vector128<short> v4 = Shift4((p2 + p1 + p0 + q0 + q1 + q2 + q3 + (q4 * 2) + q5 + q6 + (q7 * 5)) + eight);
        Vector128<short> v5 = Shift4((p1 + p0 + q0 + q1 + q2 + q3 + q4 + (q5 * 2) + q6 + (q7 * 6)) + eight);
        Vector128<short> v6 = Shift4((p0 + q0 + q1 + q2 + q3 + q4 + q5 + (q6 * 2) + (q7 * 7)) + eight);

        TP.Store(ref row0, pitch, Vector128.ConditionalSelect(flat2, w6, p6));
        TP.Store(ref row0, 2 * pitch, Vector128.ConditionalSelect(flat2, w5, p5));
        TP.Store(ref row0, 3 * pitch, Vector128.ConditionalSelect(flat2, w4, p4));
        TP.Store(ref at, 0, Vector128.ConditionalSelect(flat2, w3, p3));
        TP.Store(ref at, pitch, Vector128.ConditionalSelect(flat2, w2, np2));
        TP.Store(ref at, 2 * pitch, Vector128.ConditionalSelect(flat2, w1, np1));
        TP.Store(ref at, 3 * pitch, Vector128.ConditionalSelect(flat2, w0, np0));
        TP.Store(ref at, 4 * pitch, Vector128.ConditionalSelect(flat2, v0, nq0));
        TP.Store(ref at, 5 * pitch, Vector128.ConditionalSelect(flat2, v1, nq1));
        TP.Store(ref at, 6 * pitch, Vector128.ConditionalSelect(flat2, v2, nq2));
        TP.Store(ref at, 7 * pitch, Vector128.ConditionalSelect(flat2, v3, q3));
        TP.Store(ref row0, 12 * pitch, Vector128.ConditionalSelect(flat2, v4, q4));
        TP.Store(ref row0, 13 * pitch, Vector128.ConditionalSelect(flat2, v5, q5));
        TP.Store(ref row0, 14 * pitch, Vector128.ConditionalSelect(flat2, v6, q6));
    }

    /// <summary>The 16-lane <see cref="Run"/>, with each lane's own limits: filters sixteen lanes whose 2 × <paramref name="reach"/> rows, p(reach - 1) first, start at <paramref name="row0"/>, <paramref name="pitch"/> apart.</summary>
    private static void Run16<TP>(ref byte row0, int pitch, int reach, int width, Vector256<short> lim, Vector256<short> blim, Vector256<short> th,
        int shift)
        where TP : struct, IPixels
    {
        ref byte at = ref Unsafe.Add(ref row0, (reach - 4) * pitch);
        Vector256<short> p3 = TP.Load16(ref at, 0), p2 = TP.Load16(ref at, pitch), p1 = TP.Load16(ref at, 2 * pitch), p0 = TP.Load16(ref at, 3 * pitch);
        Vector256<short> q0 = TP.Load16(ref at, 4 * pitch), q1 = TP.Load16(ref at, 5 * pitch), q2 = TP.Load16(ref at, 6 * pitch), q3 = TP.Load16(ref at, 7 * pitch);
        Vector256<short> mask = Le16(Abs16(p3 - p2), lim) & Le16(Abs16(p2 - p1), lim) & Le16(Abs16(p1 - p0), lim) & Le16(Abs16(q1 - q0), lim)
            & Le16(Abs16(q2 - q1), lim) & Le16(Abs16(q3 - q2), lim)
            & Le16((Abs16(p0 - q0) * 2) + Vector256.ShiftRightArithmetic(Abs16(p1 - q1), 1), blim);
        if (mask == Vector256<short>.Zero)
        {
            return;
        }

        // The 4-tap filter, in values centered on zero and clamped to the signed range of the bit depth.
        Vector256<short> c128 = Vector256.Create((short)(128 << shift));
        Vector256<short> lo = Vector256.Create((short)(-128 << shift)), hi = Vector256.Create((short)((128 << shift) - 1));
        Vector256<short> ps1 = p1 - c128, ps0 = p0 - c128, qs0 = q0 - c128, qs1 = q1 - c128;
        Vector256<short> hev = Gt16(Abs16(p1 - p0), th) | Gt16(Abs16(q1 - q0), th);
        Vector256<short> filter = Clamp16(lo, hi, ps1 - qs1) & hev;
        filter = Clamp16(lo, hi, filter + (Vector256.Create((short)3) * (qs0 - ps0)));
        Vector256<short> filter1 = Vector256.ShiftRightArithmetic(Clamp16(lo, hi, filter + Vector256.Create((short)4)), 3);
        Vector256<short> filter2 = Vector256.ShiftRightArithmetic(Clamp16(lo, hi, filter + Vector256.Create((short)3)), 3);
        Vector256<short> f4q0 = Clamp16(lo, hi, qs0 - filter1) + c128, f4p0 = Clamp16(lo, hi, ps0 + filter2) + c128;
        Vector256<short> outer = Vector256.ShiftRightArithmetic(filter1 + Vector256<short>.One, 1);
        Vector256<short> f4q1 = Vector256.ConditionalSelect(hev, q1, Clamp16(lo, hi, qs1 - outer) + c128);
        Vector256<short> f4p1 = Vector256.ConditionalSelect(hev, p1, Clamp16(lo, hi, ps1 + outer) + c128);

        if (width == 4)
        {
            TP.Store16(ref at, 2 * pitch, Vector256.ConditionalSelect(mask, f4p1, p1));
            TP.Store16(ref at, 3 * pitch, Vector256.ConditionalSelect(mask, f4p0, p0));
            TP.Store16(ref at, 4 * pitch, Vector256.ConditionalSelect(mask, f4q0, q0));
            TP.Store16(ref at, 5 * pitch, Vector256.ConditionalSelect(mask, f4q1, q1));
            return;
        }

        Vector256<short> one = Vector256.Create((short)(1 << shift));
        Vector256<short> flat = mask & Le16(Abs16(p1 - p0), one) & Le16(Abs16(q1 - q0), one) & Le16(Abs16(p2 - p0), one) & Le16(Abs16(q2 - q0), one)
            & Le16(Abs16(p3 - p0), one) & Le16(Abs16(q3 - q0), one);
        if (flat == Vector256<short>.Zero)
        {
            TP.Store16(ref at, 2 * pitch, Vector256.ConditionalSelect(mask, f4p1, p1));
            TP.Store16(ref at, 3 * pitch, Vector256.ConditionalSelect(mask, f4p0, p0));
            TP.Store16(ref at, 4 * pitch, Vector256.ConditionalSelect(mask, f4q0, q0));
            TP.Store16(ref at, 5 * pitch, Vector256.ConditionalSelect(mask, f4q1, q1));
            return;
        }

        Vector256<short> filtered = mask & ~flat;

        // The 7-tap filter of flat lanes.
        Vector256<short> four = Vector256.Create((short)4);
        Vector256<short> o2 = Vector256.ShiftRightArithmetic(p3 + p3 + p3 + (p2 * 2) + p1 + p0 + q0 + four, 3);
        Vector256<short> o1 = Vector256.ShiftRightArithmetic(p3 + p3 + p2 + (p1 * 2) + p0 + q0 + q1 + four, 3);
        Vector256<short> o0 = Vector256.ShiftRightArithmetic(p3 + p2 + p1 + (p0 * 2) + q0 + q1 + q2 + four, 3);
        Vector256<short> u0 = Vector256.ShiftRightArithmetic(p2 + p1 + p0 + (q0 * 2) + q1 + q2 + q3 + four, 3);
        Vector256<short> u1 = Vector256.ShiftRightArithmetic(p1 + p0 + q0 + (q1 * 2) + q2 + q3 + q3 + four, 3);
        Vector256<short> u2 = Vector256.ShiftRightArithmetic(p0 + q0 + q1 + (q2 * 2) + q3 + q3 + q3 + four, 3);
        Vector256<short> np2 = Vector256.ConditionalSelect(flat, o2, p2);
        Vector256<short> np1 = Vector256.ConditionalSelect(flat, o1, Vector256.ConditionalSelect(filtered, f4p1, p1));
        Vector256<short> np0 = Vector256.ConditionalSelect(flat, o0, Vector256.ConditionalSelect(filtered, f4p0, p0));
        Vector256<short> nq0 = Vector256.ConditionalSelect(flat, u0, Vector256.ConditionalSelect(filtered, f4q0, q0));
        Vector256<short> nq1 = Vector256.ConditionalSelect(flat, u1, Vector256.ConditionalSelect(filtered, f4q1, q1));
        Vector256<short> nq2 = Vector256.ConditionalSelect(flat, u2, q2);

        if (width == 8)
        {
            TP.Store16(ref at, pitch, np2);
            TP.Store16(ref at, 2 * pitch, np1);
            TP.Store16(ref at, 3 * pitch, np0);
            TP.Store16(ref at, 4 * pitch, nq0);
            TP.Store16(ref at, 5 * pitch, nq1);
            TP.Store16(ref at, 6 * pitch, nq2);
            return;
        }

        // The 15-tap filter of lanes flat out to p7 and q7.
        Vector256<short> p7 = TP.Load16(ref row0, 0), p6 = TP.Load16(ref row0, pitch), p5 = TP.Load16(ref row0, 2 * pitch), p4 = TP.Load16(ref row0, 3 * pitch);
        Vector256<short> q4 = TP.Load16(ref row0, 12 * pitch), q5 = TP.Load16(ref row0, 13 * pitch), q6 = TP.Load16(ref row0, 14 * pitch), q7 = TP.Load16(ref row0, 15 * pitch);
        Vector256<short> flat2 = flat & Le16(Abs16(p4 - p0), one) & Le16(Abs16(q4 - q0), one) & Le16(Abs16(p5 - p0), one) & Le16(Abs16(q5 - q0), one)
            & Le16(Abs16(p6 - p0), one) & Le16(Abs16(q6 - q0), one) & Le16(Abs16(p7 - p0), one) & Le16(Abs16(q7 - q0), one);
        if (flat2 == Vector256<short>.Zero)
        {
            TP.Store16(ref at, pitch, np2);
            TP.Store16(ref at, 2 * pitch, np1);
            TP.Store16(ref at, 3 * pitch, np0);
            TP.Store16(ref at, 4 * pitch, nq0);
            TP.Store16(ref at, 5 * pitch, nq1);
            TP.Store16(ref at, 6 * pitch, nq2);
            return;
        }

        Vector256<short> eight = Vector256.Create((short)8);
        Vector256<short> w6 = Shift416(((p7 * 7) + (p6 * 2) + p5 + p4 + p3 + p2 + p1 + p0 + q0) + eight);
        Vector256<short> w5 = Shift416(((p7 * 6) + p6 + (p5 * 2) + p4 + p3 + p2 + p1 + p0 + q0 + q1) + eight);
        Vector256<short> w4 = Shift416(((p7 * 5) + p6 + p5 + (p4 * 2) + p3 + p2 + p1 + p0 + q0 + q1 + q2) + eight);
        Vector256<short> w3 = Shift416(((p7 * 4) + p6 + p5 + p4 + (p3 * 2) + p2 + p1 + p0 + q0 + q1 + q2 + q3) + eight);
        Vector256<short> w2 = Shift416(((p7 * 3) + p6 + p5 + p4 + p3 + (p2 * 2) + p1 + p0 + q0 + q1 + q2 + q3 + q4) + eight);
        Vector256<short> w1 = Shift416(((p7 * 2) + p6 + p5 + p4 + p3 + p2 + (p1 * 2) + p0 + q0 + q1 + q2 + q3 + q4 + q5) + eight);
        Vector256<short> w0 = Shift416((p7 + p6 + p5 + p4 + p3 + p2 + p1 + (p0 * 2) + q0 + q1 + q2 + q3 + q4 + q5 + q6) + eight);
        Vector256<short> v0 = Shift416((p6 + p5 + p4 + p3 + p2 + p1 + p0 + (q0 * 2) + q1 + q2 + q3 + q4 + q5 + q6 + q7) + eight);
        Vector256<short> v1 = Shift416((p5 + p4 + p3 + p2 + p1 + p0 + q0 + (q1 * 2) + q2 + q3 + q4 + q5 + q6 + (q7 * 2)) + eight);
        Vector256<short> v2 = Shift416((p4 + p3 + p2 + p1 + p0 + q0 + q1 + (q2 * 2) + q3 + q4 + q5 + q6 + (q7 * 3)) + eight);
        Vector256<short> v3 = Shift416((p3 + p2 + p1 + p0 + q0 + q1 + q2 + (q3 * 2) + q4 + q5 + q6 + (q7 * 4)) + eight);
        Vector256<short> v4 = Shift416((p2 + p1 + p0 + q0 + q1 + q2 + q3 + (q4 * 2) + q5 + q6 + (q7 * 5)) + eight);
        Vector256<short> v5 = Shift416((p1 + p0 + q0 + q1 + q2 + q3 + q4 + (q5 * 2) + q6 + (q7 * 6)) + eight);
        Vector256<short> v6 = Shift416((p0 + q0 + q1 + q2 + q3 + q4 + q5 + (q6 * 2) + (q7 * 7)) + eight);

        TP.Store16(ref row0, pitch, Vector256.ConditionalSelect(flat2, w6, p6));
        TP.Store16(ref row0, 2 * pitch, Vector256.ConditionalSelect(flat2, w5, p5));
        TP.Store16(ref row0, 3 * pitch, Vector256.ConditionalSelect(flat2, w4, p4));
        TP.Store16(ref at, 0, Vector256.ConditionalSelect(flat2, w3, p3));
        TP.Store16(ref at, pitch, Vector256.ConditionalSelect(flat2, w2, np2));
        TP.Store16(ref at, 2 * pitch, Vector256.ConditionalSelect(flat2, w1, np1));
        TP.Store16(ref at, 3 * pitch, Vector256.ConditionalSelect(flat2, w0, np0));
        TP.Store16(ref at, 4 * pitch, Vector256.ConditionalSelect(flat2, v0, nq0));
        TP.Store16(ref at, 5 * pitch, Vector256.ConditionalSelect(flat2, v1, nq1));
        TP.Store16(ref at, 6 * pitch, Vector256.ConditionalSelect(flat2, v2, nq2));
        TP.Store16(ref at, 7 * pitch, Vector256.ConditionalSelect(flat2, v3, q3));
        TP.Store16(ref row0, 12 * pitch, Vector256.ConditionalSelect(flat2, v4, q4));
        TP.Store16(ref row0, 13 * pitch, Vector256.ConditionalSelect(flat2, v5, q5));
        TP.Store16(ref row0, 14 * pitch, Vector256.ConditionalSelect(flat2, v6, q6));
    }

    /// <summary>Writes the transpose of the 8 by 8 bytes at <paramref name="src"/> (rows <paramref name="srcPitch"/> apart) to <paramref name="dst"/>.</summary>
    private static void TransposeBytes(ref byte src, int srcPitch, ref byte dst, int dstPitch)
    {
        Vector128<byte> a0 = Sse2.UnpackLow(Row8(ref src, 0), Row8(ref src, srcPitch));
        Vector128<byte> a1 = Sse2.UnpackLow(Row8(ref src, 2 * srcPitch), Row8(ref src, 3 * srcPitch));
        Vector128<byte> a2 = Sse2.UnpackLow(Row8(ref src, 4 * srcPitch), Row8(ref src, 5 * srcPitch));
        Vector128<byte> a3 = Sse2.UnpackLow(Row8(ref src, 6 * srcPitch), Row8(ref src, 7 * srcPitch));
        Vector128<short> b0 = Sse2.UnpackLow(a0.AsInt16(), a1.AsInt16()), b1 = Sse2.UnpackHigh(a0.AsInt16(), a1.AsInt16());
        Vector128<short> b2 = Sse2.UnpackLow(a2.AsInt16(), a3.AsInt16()), b3 = Sse2.UnpackHigh(a2.AsInt16(), a3.AsInt16());
        Vector128<ulong> c0 = Sse2.UnpackLow(b0.AsInt32(), b2.AsInt32()).AsUInt64(), c1 = Sse2.UnpackHigh(b0.AsInt32(), b2.AsInt32()).AsUInt64();
        Vector128<ulong> c2 = Sse2.UnpackLow(b1.AsInt32(), b3.AsInt32()).AsUInt64(), c3 = Sse2.UnpackHigh(b1.AsInt32(), b3.AsInt32()).AsUInt64();
        Unsafe.WriteUnaligned(ref dst, c0.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, dstPitch), c0.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2 * dstPitch), c1.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3 * dstPitch), c1.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 * dstPitch), c2.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 5 * dstPitch), c2.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 6 * dstPitch), c3.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 7 * dstPitch), c3.GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Row8(ref byte p, int offset) =>
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Abs(Vector128<short> v) => Vector128.Abs(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Le(Vector128<short> a, Vector128<short> b) => Vector128.LessThanOrEqual(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Gt(Vector128<short> a, Vector128<short> b) => Vector128.GreaterThan(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clamp(Vector128<short> lo, Vector128<short> hi, Vector128<short> v) => Vector128.Min(Vector128.Max(v, lo), hi);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Shift4(Vector128<short> v) => Vector128.ShiftRightLogical(v.AsUInt16(), 4).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Abs16(Vector256<short> v) => Vector256.Abs(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Le16(Vector256<short> a, Vector256<short> b) => Vector256.LessThanOrEqual(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Gt16(Vector256<short> a, Vector256<short> b) => Vector256.GreaterThan(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Clamp16(Vector256<short> lo, Vector256<short> hi, Vector256<short> v) => Vector256.Min(Vector256.Max(v, lo), hi);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Shift416(Vector256<short> v) => Vector256.ShiftRightLogical(v.AsUInt16(), 4).AsInt16();

    /// <summary>How a filter reads and writes its pixels: 8-bit bytes or 16-bit samples, eight or sixteen lanes at a time.</summary>
    private interface IPixels
    {
        /// <summary>Gets the size of a sample in bytes.</summary>
        static abstract int Size { get; }

        static abstract Vector128<short> Load(ref byte p, int offset);

        static abstract void Store(ref byte p, int offset, Vector128<short> value);

        static abstract Vector256<short> Load16(ref byte p, int offset);

        static abstract void Store16(ref byte p, int offset, Vector256<short> value);

        /// <summary>Writes the transpose of the 8 by 8 samples at <paramref name="src"/> to <paramref name="dst"/>; pitches in bytes.</summary>
        static abstract void Transpose8x8(ref byte src, int srcPitch, ref byte dst, int dstPitch);
    }

    private readonly struct Bytes : IPixels
    {
        public static int Size => 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> Load(ref byte p, int offset) =>
            Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset))).AsByte()).AsInt16();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store(ref byte p, int offset, Vector128<short> value) =>
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, offset), Vector128.Narrow(value.AsUInt16(), value.AsUInt16()).AsUInt64().ToScalar());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<short> Load16(ref byte p, int offset) => Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref Unsafe.Add(ref p, offset)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store16(ref byte p, int offset, Vector256<short> value) =>
            Vector256.Narrow(value.AsUInt16(), value.AsUInt16()).GetLower().StoreUnsafe(ref Unsafe.Add(ref p, offset));

        public static void Transpose8x8(ref byte src, int srcPitch, ref byte dst, int dstPitch) => TransposeBytes(ref src, srcPitch, ref dst, dstPitch);
    }

    private readonly struct Words : IPixels
    {
        public static int Size => 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> Load(ref byte p, int offset) => Vector128.LoadUnsafe(ref Unsafe.Add(ref p, offset)).AsInt16();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store(ref byte p, int offset, Vector128<short> value) => value.AsByte().StoreUnsafe(ref Unsafe.Add(ref p, offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<short> Load16(ref byte p, int offset) => Vector256.LoadUnsafe(ref Unsafe.Add(ref p, offset)).AsInt16();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store16(ref byte p, int offset, Vector256<short> value) => value.AsByte().StoreUnsafe(ref Unsafe.Add(ref p, offset));

        public static void Transpose8x8(ref byte src, int srcPitch, ref byte dst, int dstPitch)
        {
            Vector128<short> r0 = Load(ref src, 0), r1 = Load(ref src, srcPitch), r2 = Load(ref src, 2 * srcPitch), r3 = Load(ref src, 3 * srcPitch);
            Vector128<short> r4 = Load(ref src, 4 * srcPitch), r5 = Load(ref src, 5 * srcPitch), r6 = Load(ref src, 6 * srcPitch), r7 = Load(ref src, 7 * srcPitch);
            Vector128<int> a0 = Sse2.UnpackLow(r0, r1).AsInt32(), a1 = Sse2.UnpackHigh(r0, r1).AsInt32(), a2 = Sse2.UnpackLow(r2, r3).AsInt32();
            Vector128<int> a3 = Sse2.UnpackHigh(r2, r3).AsInt32(), a4 = Sse2.UnpackLow(r4, r5).AsInt32(), a5 = Sse2.UnpackHigh(r4, r5).AsInt32();
            Vector128<int> a6 = Sse2.UnpackLow(r6, r7).AsInt32(), a7 = Sse2.UnpackHigh(r6, r7).AsInt32();
            Vector128<long> b0 = Sse2.UnpackLow(a0, a2).AsInt64(), b1 = Sse2.UnpackHigh(a0, a2).AsInt64(), b2 = Sse2.UnpackLow(a1, a3).AsInt64();
            Vector128<long> b3 = Sse2.UnpackHigh(a1, a3).AsInt64(), b4 = Sse2.UnpackLow(a4, a6).AsInt64(), b5 = Sse2.UnpackHigh(a4, a6).AsInt64();
            Vector128<long> b6 = Sse2.UnpackLow(a5, a7).AsInt64(), b7 = Sse2.UnpackHigh(a5, a7).AsInt64();
            Store(ref dst, 0, Sse2.UnpackLow(b0, b4).AsInt16());
            Store(ref dst, dstPitch, Sse2.UnpackHigh(b0, b4).AsInt16());
            Store(ref dst, 2 * dstPitch, Sse2.UnpackLow(b1, b5).AsInt16());
            Store(ref dst, 3 * dstPitch, Sse2.UnpackHigh(b1, b5).AsInt16());
            Store(ref dst, 4 * dstPitch, Sse2.UnpackLow(b2, b6).AsInt16());
            Store(ref dst, 5 * dstPitch, Sse2.UnpackHigh(b2, b6).AsInt16());
            Store(ref dst, 6 * dstPitch, Sse2.UnpackLow(b3, b7).AsInt16());
            Store(ref dst, 7 * dstPitch, Sse2.UnpackHigh(b3, b7).AsInt16());
        }
    }
}
