using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Webp;

/// <summary>
/// The VP8 loop filters on 16 positions at a time. The arithmetic is the scalar filters' (<see cref="Vp8Dsp"/>), done in 16-bit
/// lanes so it is exact; horizontal edges load their rows directly, vertical edges are transposed in and out (SSE2). The callers
/// fall back to the scalar filters when vectors are not accelerated.
/// </summary>
internal static class Vp8FilterSimd
{
    /// <summary>The kind of edge: the simple filter, a macroblock edge or an inner edge of the normal filter.</summary>
    internal enum Kind
    {
        Simple,
        MacroblockEdge,
        Inner,
    }

    /// <summary>Gets whether horizontal edges are filtered with vectors.</summary>
    internal static bool Rows => Vector128.IsHardwareAccelerated;

    /// <summary>Gets whether vertical edges are filtered with vectors (the transposes need SSE2).</summary>
    internal static bool Columns => Sse2.IsSupported;

    /// <summary>Filters a horizontal edge 16 pixels wide: the row at <paramref name="at"/> is q0, the row above p0.</summary>
    internal static void HorizontalEdge16(Span<byte> p, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at);
        Vector128<byte> p3 = Load(ref q, -4 * stride), p2 = Load(ref q, -3 * stride), p1 = Load(ref q, -2 * stride), p0 = Load(ref q, -stride);
        Vector128<byte> q0 = Load(ref q, 0), q1 = Load(ref q, stride), q2 = Load(ref q, 2 * stride), q3 = Load(ref q, 3 * stride);
        Filter(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, kind, threshold, interior, hev);
        Store(ref q, -3 * stride, p2);
        Store(ref q, -2 * stride, p1);
        Store(ref q, -stride, p0);
        Store(ref q, 0, q0);
        Store(ref q, stride, q1);
        Store(ref q, 2 * stride, q2);
    }

    /// <summary>Filters a horizontal edge of U and V together, 8 pixels of each.</summary>
    internal static void HorizontalEdge8(Span<byte> u, Span<byte> v, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        ref byte qu = ref Unsafe.Add(ref MemoryMarshal.GetReference(u), at);
        ref byte qv = ref Unsafe.Add(ref MemoryMarshal.GetReference(v), at);
        Vector128<byte> p3 = Load8(ref qu, ref qv, -4 * stride), p2 = Load8(ref qu, ref qv, -3 * stride), p1 = Load8(ref qu, ref qv, -2 * stride), p0 = Load8(ref qu, ref qv, -stride);
        Vector128<byte> q0 = Load8(ref qu, ref qv, 0), q1 = Load8(ref qu, ref qv, stride), q2 = Load8(ref qu, ref qv, 2 * stride), q3 = Load8(ref qu, ref qv, 3 * stride);
        Filter(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, kind, threshold, interior, hev);
        Store8(ref qu, ref qv, -3 * stride, p2);
        Store8(ref qu, ref qv, -2 * stride, p1);
        Store8(ref qu, ref qv, -stride, p0);
        Store8(ref qu, ref qv, 0, q0);
        Store8(ref qu, ref qv, stride, q1);
        Store8(ref qu, ref qv, 2 * stride, q2);
    }

    /// <summary>Filters a vertical edge 16 rows tall: the column at <paramref name="at"/> is q0, the one to its left p0.</summary>
    internal static void VerticalEdge16(Span<byte> p, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at - 4);
        Span<ulong> rows = stackalloc ulong[16];
        for (int r = 0; r < 16; r++)
        {
            rows[r] = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref q, r * stride));
        }

        Transpose(rows, out Vector128<byte> p3, out Vector128<byte> p2, out Vector128<byte> p1, out Vector128<byte> p0, out Vector128<byte> q0, out Vector128<byte> q1, out Vector128<byte> q2, out Vector128<byte> q3);
        Filter(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, kind, threshold, interior, hev);
        TransposeBack(p3, p2, p1, p0, q0, q1, q2, q3, rows);
        for (int r = 0; r < 16; r++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, r * stride), rows[r]);
        }
    }

    /// <summary>Filters a vertical edge of U and V together, 8 rows of each.</summary>
    internal static void VerticalEdge8(Span<byte> u, Span<byte> v, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        ref byte qu = ref Unsafe.Add(ref MemoryMarshal.GetReference(u), at - 4);
        ref byte qv = ref Unsafe.Add(ref MemoryMarshal.GetReference(v), at - 4);
        Span<ulong> rows = stackalloc ulong[16];
        for (int r = 0; r < 8; r++)
        {
            rows[r] = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qu, r * stride));
            rows[r + 8] = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qv, r * stride));
        }

        Transpose(rows, out Vector128<byte> p3, out Vector128<byte> p2, out Vector128<byte> p1, out Vector128<byte> p0, out Vector128<byte> q0, out Vector128<byte> q1, out Vector128<byte> q2, out Vector128<byte> q3);
        Filter(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, kind, threshold, interior, hev);
        TransposeBack(p3, p2, p1, p0, q0, q1, q2, q3, rows);
        for (int r = 0; r < 8; r++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref qu, r * stride), rows[r]);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref qv, r * stride), rows[r + 8]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load(ref byte q, int offset) => Vector128.LoadUnsafe(ref Unsafe.Add(ref q, offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref byte q, int offset, Vector128<byte> value) => value.StoreUnsafe(ref Unsafe.Add(ref q, offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load8(ref byte u, ref byte v, int offset) =>
        Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref u, offset)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref v, offset))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(ref byte u, ref byte v, int offset, Vector128<byte> value)
    {
        Vector128<ulong> halves = value.AsUInt64();
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref u, offset), halves.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref v, offset), halves.GetElement(1));
    }

    /// <summary>The filter on 16 positions: p3 to q3 are the 8 pixels across the edge at each.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter(
        ref Vector128<byte> p3, ref Vector128<byte> p2, ref Vector128<byte> p1, ref Vector128<byte> p0,
        ref Vector128<byte> q0, ref Vector128<byte> q1, ref Vector128<byte> q2, ref Vector128<byte> q3,
        Kind kind, int threshold, int interior, int hev)
    {
        (Vector128<ushort> p3l, Vector128<ushort> p3h) = Vector128.Widen(p3);
        (Vector128<ushort> p2l, Vector128<ushort> p2h) = Vector128.Widen(p2);
        (Vector128<ushort> p1l, Vector128<ushort> p1h) = Vector128.Widen(p1);
        (Vector128<ushort> p0l, Vector128<ushort> p0h) = Vector128.Widen(p0);
        (Vector128<ushort> q0l, Vector128<ushort> q0h) = Vector128.Widen(q0);
        (Vector128<ushort> q1l, Vector128<ushort> q1h) = Vector128.Widen(q1);
        (Vector128<ushort> q2l, Vector128<ushort> q2h) = Vector128.Widen(q2);
        (Vector128<ushort> q3l, Vector128<ushort> q3h) = Vector128.Widen(q3);
        Pixels lo = new(p3l.AsInt16(), p2l.AsInt16(), p1l.AsInt16(), p0l.AsInt16(), q0l.AsInt16(), q1l.AsInt16(), q2l.AsInt16(), q3l.AsInt16());
        Pixels hi = new(p3h.AsInt16(), p2h.AsInt16(), p1h.AsInt16(), p0h.AsInt16(), q0h.AsInt16(), q1h.AsInt16(), q2h.AsInt16(), q3h.AsInt16());
        FilterHalf(ref lo, kind, threshold, interior, hev);
        FilterHalf(ref hi, kind, threshold, interior, hev);
        p2 = Narrow(lo.P2, hi.P2);
        p1 = Narrow(lo.P1, hi.P1);
        p0 = Narrow(lo.P0, hi.P0);
        q0 = Narrow(lo.Q0, hi.Q0);
        q1 = Narrow(lo.Q1, hi.Q1);
        q2 = Narrow(lo.Q2, hi.Q2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Narrow(Vector128<short> lo, Vector128<short> hi)
    {
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> max = Vector128.Create((short)255);
        return Vector128.Narrow(Vector128.Min(Vector128.Max(lo, zero), max).AsUInt16(), Vector128.Min(Vector128.Max(hi, zero), max).AsUInt16());
    }

    private struct Pixels(Vector128<short> p3, Vector128<short> p2, Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1, Vector128<short> q2, Vector128<short> q3)
    {
        public Vector128<short> P3 = p3, P2 = p2, P1 = p1, P0 = p0, Q0 = q0, Q1 = q1, Q2 = q2, Q3 = q3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clamp(Vector128<short> v, short min, short max) => Vector128.Min(Vector128.Max(v, Vector128.Create(min)), Vector128.Create(max));

    /// <summary>The scalar filters' arithmetic on 8 lanes, each lane choosing its filter by its own masks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterHalf(ref Pixels x, Kind kind, int threshold, int interior, int hevThreshold)
    {
        Vector128<short> threshold2 = Vector128.Create((short)((2 * threshold) + 1));
        Vector128<short> needs = Vector128.LessThanOrEqual((Vector128.Abs(x.P0 - x.Q0) * 4) + Vector128.Abs(x.P1 - x.Q1), threshold2);
        Vector128<short> p1q1 = Clamp(x.P1 - x.Q1, -128, 127);
        Vector128<short> three = (x.Q0 - x.P0) * 3;
        if (kind == Kind.Simple)
        {
            Filter2(ref x, three + p1q1, needs);
            return;
        }

        Vector128<short> i = Vector128.Create((short)interior);
        needs &= Vector128.LessThanOrEqual(Vector128.Abs(x.P3 - x.P2), i) & Vector128.LessThanOrEqual(Vector128.Abs(x.P2 - x.P1), i)
            & Vector128.LessThanOrEqual(Vector128.Abs(x.P1 - x.P0), i) & Vector128.LessThanOrEqual(Vector128.Abs(x.Q3 - x.Q2), i)
            & Vector128.LessThanOrEqual(Vector128.Abs(x.Q2 - x.Q1), i) & Vector128.LessThanOrEqual(Vector128.Abs(x.Q1 - x.Q0), i);
        Vector128<short> h = Vector128.Create((short)hevThreshold);
        Vector128<short> hev = Vector128.GreaterThan(Vector128.Abs(x.P1 - x.P0), h) | Vector128.GreaterThan(Vector128.Abs(x.Q1 - x.Q0), h);
        Vector128<short> p0 = x.P0, q0 = x.Q0;

        // High-variance lanes: two pixels changed.
        Pixels two = x;
        Filter2(ref two, three + p1q1, needs & hev);
        Vector128<short> notHev = needs & ~hev;
        if (kind == Kind.MacroblockEdge)
        {
            Vector128<short> w = Clamp(three + p1q1, -128, 127);
            Vector128<short> c63 = Vector128.Create((short)63);
            Vector128<short> a1 = Vector128.ShiftRightArithmetic((w * 27) + c63, 7);
            Vector128<short> a2 = Vector128.ShiftRightArithmetic((w * 18) + c63, 7);
            Vector128<short> a3 = Vector128.ShiftRightArithmetic((w * 9) + c63, 7);
            x.P2 = Vector128.ConditionalSelect(notHev, x.P2 + a3, x.P2);
            x.P1 = Vector128.ConditionalSelect(notHev, x.P1 + a2, x.P1);
            x.P0 = Vector128.ConditionalSelect(notHev, p0 + a1, two.P0);
            x.Q0 = Vector128.ConditionalSelect(notHev, q0 - a1, two.Q0);
            x.Q1 = Vector128.ConditionalSelect(notHev, x.Q1 - a2, x.Q1);
            x.Q2 = Vector128.ConditionalSelect(notHev, x.Q2 - a3, x.Q2);
        }
        else
        {
            Vector128<short> a1 = Clamp(Vector128.ShiftRightArithmetic(three + Vector128.Create((short)4), 3), -16, 15);
            Vector128<short> a2 = Clamp(Vector128.ShiftRightArithmetic(three + Vector128.Create((short)3), 3), -16, 15);
            Vector128<short> a3 = Vector128.ShiftRightArithmetic(a1 + Vector128<short>.One, 1);
            x.P1 = Vector128.ConditionalSelect(notHev, x.P1 + a3, x.P1);
            x.P0 = Vector128.ConditionalSelect(notHev, p0 + a2, two.P0);
            x.Q0 = Vector128.ConditionalSelect(notHev, q0 - a1, two.Q0);
            x.Q1 = Vector128.ConditionalSelect(notHev, x.Q1 - a3, x.Q1);
        }
    }

    /// <summary>The two-pixel filter on the lanes of <paramref name="mask"/>: a is 3 × (q0 − p0) plus p1 − q1 clamped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter2(ref Pixels x, Vector128<short> a, Vector128<short> mask)
    {
        Vector128<short> a1 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)4), 3), -16, 15);
        Vector128<short> a2 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)3), 3), -16, 15);
        x.P0 = Vector128.ConditionalSelect(mask, Clamp(x.P0 + a2, 0, 255), x.P0);
        x.Q0 = Vector128.ConditionalSelect(mask, Clamp(x.Q0 - a1, 0, 255), x.Q0);
    }

    /// <summary>Transposes 16 rows of 8 bytes into the 8 columns of 16 bytes.</summary>
    private static void Transpose(
        ReadOnlySpan<ulong> rows, out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3,
        out Vector128<byte> c4, out Vector128<byte> c5, out Vector128<byte> c6, out Vector128<byte> c7)
    {
        Span<Vector128<byte>> t = stackalloc Vector128<byte>[8];
        for (int k = 0; k < 8; k++)
        {
            t[k] = Sse2.UnpackLow(Vector128.CreateScalar(rows[2 * k]).AsByte(), Vector128.CreateScalar(rows[(2 * k) + 1]).AsByte());
        }

        Vector128<short> u0 = Sse2.UnpackLow(t[0].AsInt16(), t[1].AsInt16()), u1 = Sse2.UnpackHigh(t[0].AsInt16(), t[1].AsInt16());
        Vector128<short> u2 = Sse2.UnpackLow(t[2].AsInt16(), t[3].AsInt16()), u3 = Sse2.UnpackHigh(t[2].AsInt16(), t[3].AsInt16());
        Vector128<short> u4 = Sse2.UnpackLow(t[4].AsInt16(), t[5].AsInt16()), u5 = Sse2.UnpackHigh(t[4].AsInt16(), t[5].AsInt16());
        Vector128<short> u6 = Sse2.UnpackLow(t[6].AsInt16(), t[7].AsInt16()), u7 = Sse2.UnpackHigh(t[6].AsInt16(), t[7].AsInt16());
        Vector128<int> v0 = Sse2.UnpackLow(u0.AsInt32(), u2.AsInt32()), v1 = Sse2.UnpackHigh(u0.AsInt32(), u2.AsInt32());
        Vector128<int> v2 = Sse2.UnpackLow(u1.AsInt32(), u3.AsInt32()), v3 = Sse2.UnpackHigh(u1.AsInt32(), u3.AsInt32());
        Vector128<int> w0 = Sse2.UnpackLow(u4.AsInt32(), u6.AsInt32()), w1 = Sse2.UnpackHigh(u4.AsInt32(), u6.AsInt32());
        Vector128<int> w2 = Sse2.UnpackLow(u5.AsInt32(), u7.AsInt32()), w3 = Sse2.UnpackHigh(u5.AsInt32(), u7.AsInt32());
        c0 = Sse2.UnpackLow(v0.AsInt64(), w0.AsInt64()).AsByte();
        c1 = Sse2.UnpackHigh(v0.AsInt64(), w0.AsInt64()).AsByte();
        c2 = Sse2.UnpackLow(v1.AsInt64(), w1.AsInt64()).AsByte();
        c3 = Sse2.UnpackHigh(v1.AsInt64(), w1.AsInt64()).AsByte();
        c4 = Sse2.UnpackLow(v2.AsInt64(), w2.AsInt64()).AsByte();
        c5 = Sse2.UnpackHigh(v2.AsInt64(), w2.AsInt64()).AsByte();
        c6 = Sse2.UnpackLow(v3.AsInt64(), w3.AsInt64()).AsByte();
        c7 = Sse2.UnpackHigh(v3.AsInt64(), w3.AsInt64()).AsByte();
    }

    /// <summary>Transposes the 8 columns of 16 bytes back into 16 rows of 8 bytes.</summary>
    private static void TransposeBack(
        Vector128<byte> c0, Vector128<byte> c1, Vector128<byte> c2, Vector128<byte> c3,
        Vector128<byte> c4, Vector128<byte> c5, Vector128<byte> c6, Vector128<byte> c7, Span<ulong> rows)
    {
        Vector128<short> a0 = Sse2.UnpackLow(c0, c1).AsInt16(), a1 = Sse2.UnpackHigh(c0, c1).AsInt16();
        Vector128<short> a2 = Sse2.UnpackLow(c2, c3).AsInt16(), a3 = Sse2.UnpackHigh(c2, c3).AsInt16();
        Vector128<short> a4 = Sse2.UnpackLow(c4, c5).AsInt16(), a5 = Sse2.UnpackHigh(c4, c5).AsInt16();
        Vector128<short> a6 = Sse2.UnpackLow(c6, c7).AsInt16(), a7 = Sse2.UnpackHigh(c6, c7).AsInt16();
        Vector128<int> b0 = Sse2.UnpackLow(a0, a2).AsInt32(), b1 = Sse2.UnpackHigh(a0, a2).AsInt32();
        Vector128<int> b2 = Sse2.UnpackLow(a1, a3).AsInt32(), b3 = Sse2.UnpackHigh(a1, a3).AsInt32();
        Vector128<int> b4 = Sse2.UnpackLow(a4, a6).AsInt32(), b5 = Sse2.UnpackHigh(a4, a6).AsInt32();
        Vector128<int> b6 = Sse2.UnpackLow(a5, a7).AsInt32(), b7 = Sse2.UnpackHigh(a5, a7).AsInt32();
        Vector128<ulong> r01 = Sse2.UnpackLow(b0, b4).AsUInt64(), r23 = Sse2.UnpackHigh(b0, b4).AsUInt64();
        Vector128<ulong> r45 = Sse2.UnpackLow(b1, b5).AsUInt64(), r67 = Sse2.UnpackHigh(b1, b5).AsUInt64();
        Vector128<ulong> r89 = Sse2.UnpackLow(b2, b6).AsUInt64(), r1011 = Sse2.UnpackHigh(b2, b6).AsUInt64();
        Vector128<ulong> r1213 = Sse2.UnpackLow(b3, b7).AsUInt64(), r1415 = Sse2.UnpackHigh(b3, b7).AsUInt64();
        rows[0] = r01.GetElement(0);
        rows[1] = r01.GetElement(1);
        rows[2] = r23.GetElement(0);
        rows[3] = r23.GetElement(1);
        rows[4] = r45.GetElement(0);
        rows[5] = r45.GetElement(1);
        rows[6] = r67.GetElement(0);
        rows[7] = r67.GetElement(1);
        rows[8] = r89.GetElement(0);
        rows[9] = r89.GetElement(1);
        rows[10] = r1011.GetElement(0);
        rows[11] = r1011.GetElement(1);
        rows[12] = r1213.GetElement(0);
        rows[13] = r1213.GetElement(1);
        rows[14] = r1415.GetElement(0);
        rows[15] = r1415.GetElement(1);
    }
}
