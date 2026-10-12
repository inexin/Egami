using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Webp;

/// <summary>
/// The VP8 loop filters on 16 positions at a time. With SSE2 the arithmetic is libvpx's: pixels biased to signed bytes and
/// saturating 8-bit adds and subtracts, which clamp exactly where the scalar filters (<see cref="Vp8Dsp"/>) clamp, with only the
/// macroblock edge's 27, 18 and 9 multiples in 16-bit lanes; elsewhere it is the scalar filters' arithmetic in 16-bit lanes.
/// Horizontal edges load their rows directly, vertical edges are transposed in and out (SSE2). The callers fall back to the
/// scalar filters when vectors are not accelerated.
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
        _ = p.Slice(at - (4 * stride), (7 * stride) + 16);
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at);
        Edge e = new()
        {
            P3 = Load(ref q, -4 * stride),
            P2 = Load(ref q, -3 * stride),
            P1 = Load(ref q, -2 * stride),
            P0 = Load(ref q, -stride),
            Q0 = Load(ref q, 0),
            Q1 = Load(ref q, stride),
            Q2 = Load(ref q, 2 * stride),
            Q3 = Load(ref q, 3 * stride),
        };
        e = Filter(e, kind, threshold, interior, hev);
        Store(ref q, -3 * stride, e.P2);
        Store(ref q, -2 * stride, e.P1);
        Store(ref q, -stride, e.P0);
        Store(ref q, 0, e.Q0);
        Store(ref q, stride, e.Q1);
        Store(ref q, 2 * stride, e.Q2);
    }

    /// <summary>Filters a horizontal edge of U and V together, 8 pixels of each.</summary>
    internal static void HorizontalEdge8(Span<byte> u, Span<byte> v, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        _ = u.Slice(at - (4 * stride), (7 * stride) + 8);
        _ = v.Slice(at - (4 * stride), (7 * stride) + 8);
        ref byte qu = ref Unsafe.Add(ref MemoryMarshal.GetReference(u), at);
        ref byte qv = ref Unsafe.Add(ref MemoryMarshal.GetReference(v), at);
        Edge e = new()
        {
            P3 = Load8(ref qu, ref qv, -4 * stride),
            P2 = Load8(ref qu, ref qv, -3 * stride),
            P1 = Load8(ref qu, ref qv, -2 * stride),
            P0 = Load8(ref qu, ref qv, -stride),
            Q0 = Load8(ref qu, ref qv, 0),
            Q1 = Load8(ref qu, ref qv, stride),
            Q2 = Load8(ref qu, ref qv, 2 * stride),
            Q3 = Load8(ref qu, ref qv, 3 * stride),
        };
        e = Filter(e, kind, threshold, interior, hev);
        Store8(ref qu, ref qv, -3 * stride, e.P2);
        Store8(ref qu, ref qv, -2 * stride, e.P1);
        Store8(ref qu, ref qv, -stride, e.P0);
        Store8(ref qu, ref qv, 0, e.Q0);
        Store8(ref qu, ref qv, stride, e.Q1);
        Store8(ref qu, ref qv, 2 * stride, e.Q2);
    }

    /// <summary>Filters a vertical edge 16 rows tall: the column at <paramref name="at"/> is q0, the one to its left p0.</summary>
    internal static void VerticalEdge16(Span<byte> p, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        _ = p.Slice(at - 4 + (15 * stride), 8);
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at - 4);
        ref byte lower = ref Unsafe.Add(ref q, 8 * stride);
        Edge e = Filter(Transpose(ref q, ref lower, stride), kind, threshold, interior, hev);
        TransposeBack(e, ref q, ref lower, stride);
    }

    /// <summary>
    /// Filters the three inner vertical edges of a 16 × 16 luma macroblock (columns 4, 8 and 12 from <paramref name="at"/>), in
    /// order, from one transpose of the macroblock's columns rather than one per edge.
    /// </summary>
    internal static void InnerVerticalEdges16(Span<byte> p, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        _ = p.Slice(at + (15 * stride), 16);
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at);
        ref byte r = ref Unsafe.Add(ref q, 8);
        ref byte qLower = ref Unsafe.Add(ref q, 8 * stride);
        ref byte rLower = ref Unsafe.Add(ref r, 8 * stride);

        // Columns 0 to 7 and 8 to 15; the middle edge straddles them.
        Edge left = Filter(Transpose(ref q, ref qLower, stride), kind, threshold, interior, hev);
        Edge right = Transpose(ref r, ref rLower, stride);
        Edge middle = Filter(new Edge { P3 = left.Q0, P2 = left.Q1, P1 = left.Q2, P0 = left.Q3, Q0 = right.P3, Q1 = right.P2, Q2 = right.P1, Q3 = right.P0 }, kind, threshold, interior, hev);
        left.Q0 = middle.P3;
        left.Q1 = middle.P2;
        left.Q2 = middle.P1;
        left.Q3 = middle.P0;
        right.P3 = middle.Q0;
        right.P2 = middle.Q1;
        right.P1 = middle.Q2;
        right.P0 = middle.Q3;
        right = Filter(right, kind, threshold, interior, hev);
        TransposeBack(left, ref q, ref qLower, stride);
        TransposeBack(right, ref r, ref rLower, stride);
    }

    /// <summary>Filters a vertical edge of U and V together, 8 rows of each.</summary>
    internal static void VerticalEdge8(Span<byte> u, Span<byte> v, int at, int stride, Kind kind, int threshold, int interior, int hev)
    {
        _ = u.Slice(at - 4 + (7 * stride), 8);
        _ = v.Slice(at - 4 + (7 * stride), 8);
        ref byte qu = ref Unsafe.Add(ref MemoryMarshal.GetReference(u), at - 4);
        ref byte qv = ref Unsafe.Add(ref MemoryMarshal.GetReference(v), at - 4);
        Edge e = Filter(Transpose(ref qu, ref qv, stride), kind, threshold, interior, hev);
        TransposeBack(e, ref qu, ref qv, stride);
    }

    /// <summary>The 8 rows or columns across an edge, p3 to q3, 16 positions each.</summary>
    private struct Edge
    {
        internal Vector128<byte> P3, P2, P1, P0, Q0, Q1, Q2, Q3;
    }

    /// <summary>The filter on 16 positions of <paramref name="e"/>; returns the edge with its changed pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Edge Filter(Edge e, Kind kind, int threshold, int interior, int hev)
    {
        if (Sse2.IsSupported)
        {
            return kind switch
            {
                Kind.Simple => FilterSimple(e, threshold),
                Kind.Inner => FilterInner(e, threshold, interior, hev),
                _ => FilterMacroblockEdge(e, threshold, interior, hev),
            };
        }

        Filter(ref e.P3, ref e.P2, ref e.P1, ref e.P0, ref e.Q0, ref e.Q1, ref e.Q2, ref e.Q3, kind, threshold, interior, hev);
        return e;
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

    /// <summary>The filter on 16 positions in 16-bit lanes, without SSE2: p3 to q3 are the 8 pixels across the edge at each.</summary>
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

    /// <summary>The positions to filter: 2 × |p0 − q0| + |p1 − q1| / 2 within <paramref name="threshold"/>, in 0xFF bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> EdgeMask(in Edge e, int threshold)
    {
        Vector128<byte> d = AbsDiff(e.P0, e.Q0);
        Vector128<byte> edge = Sse2.AddSaturate(Sse2.AddSaturate(d, d), Half(AbsDiff(e.P1, e.Q1)));
        return Sse2.CompareEqual(Sse2.SubtractSaturate(edge, Vector128.Create((byte)threshold)), Vector128<byte>.Zero);
    }

    /// <summary>The normal filter's mask: <see cref="EdgeMask"/> and every step from p3 to q3 within the interior limit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NormalMask(in Edge e, int threshold, int interior, Vector128<byte> p1p0, Vector128<byte> q1q0)
    {
        Vector128<byte> steps = Sse2.Max(Sse2.Max(Sse2.Max(AbsDiff(e.P3, e.P2), AbsDiff(e.P2, e.P1)), Sse2.Max(p1p0, q1q0)), Sse2.Max(AbsDiff(e.Q2, e.Q1), AbsDiff(e.Q3, e.Q2)));
        return EdgeMask(e, threshold) & Sse2.CompareEqual(Sse2.SubtractSaturate(steps, Vector128.Create((byte)interior)), Vector128<byte>.Zero);
    }

    /// <summary>The high-variance positions: |p1 − p0| or |q1 − q0| above the threshold.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> HighVariance(Vector128<byte> p1p0, Vector128<byte> q1q0, int hev) =>
        (~Sse2.CompareEqual(Sse2.SubtractSaturate(Sse2.Max(p1p0, q1q0), Vector128.Create((byte)hev)), Vector128<byte>.Zero)).AsSByte();

    /// <summary>3 × (q0 − p0) + <paramref name="outer"/>, saturating at each step as libvpx does (the same as clamping the sum).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> Taps3(Vector128<sbyte> outer, Vector128<sbyte> step) => Sse2.AddSaturate(Sse2.AddSaturate(Sse2.AddSaturate(outer, step), step), step);

    /// <summary>The simple filter in libvpx's 8-bit arithmetic: p0 and q0 change.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Edge FilterSimple(Edge e, int threshold)
    {
        Vector128<byte> bias = Vector128.Create((byte)0x80);
        Vector128<sbyte> ps1 = (e.P1 ^ bias).AsSByte(), ps0 = (e.P0 ^ bias).AsSByte(), qs0 = (e.Q0 ^ bias).AsSByte(), qs1 = (e.Q1 ^ bias).AsSByte();
        Vector128<sbyte> f = Taps3(Sse2.SubtractSaturate(ps1, qs1), Sse2.SubtractSaturate(qs0, ps0)) & EdgeMask(e, threshold).AsSByte();
        e.Q0 = Sse2.SubtractSaturate(qs0, ShiftRight3(Sse2.AddSaturate(f, Vector128.Create((sbyte)4)))).AsByte() ^ bias;
        e.P0 = Sse2.AddSaturate(ps0, ShiftRight3(Sse2.AddSaturate(f, Vector128.Create((sbyte)3)))).AsByte() ^ bias;
        return e;
    }

    /// <summary>The normal filter on an inner edge: p0 and q0 change, and p1 and q1 where the variance is low.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Edge FilterInner(Edge e, int threshold, int interior, int hev)
    {
        Vector128<byte> p1p0 = AbsDiff(e.P1, e.P0), q1q0 = AbsDiff(e.Q1, e.Q0);
        Vector128<sbyte> mask = NormalMask(e, threshold, interior, p1p0, q1q0).AsSByte();
        Vector128<sbyte> high = HighVariance(p1p0, q1q0, hev);
        Vector128<byte> bias = Vector128.Create((byte)0x80);
        Vector128<sbyte> ps1 = (e.P1 ^ bias).AsSByte(), ps0 = (e.P0 ^ bias).AsSByte(), qs0 = (e.Q0 ^ bias).AsSByte(), qs1 = (e.Q1 ^ bias).AsSByte();
        Vector128<sbyte> f = Taps3(Sse2.SubtractSaturate(ps1, qs1) & high, Sse2.SubtractSaturate(qs0, ps0)) & mask;
        Vector128<sbyte> f1 = ShiftRight3(Sse2.AddSaturate(f, Vector128.Create((sbyte)4)));
        Vector128<sbyte> f2 = ShiftRight3(Sse2.AddSaturate(f, Vector128.Create((sbyte)3)));
        e.Q0 = Sse2.SubtractSaturate(qs0, f1).AsByte() ^ bias;
        e.P0 = Sse2.AddSaturate(ps0, f2).AsByte() ^ bias;
        Vector128<sbyte> a = Sse2.AndNot(high, ShiftRight1(f1 + Vector128<sbyte>.One));
        e.Q1 = Sse2.SubtractSaturate(qs1, a).AsByte() ^ bias;
        e.P1 = Sse2.AddSaturate(ps1, a).AsByte() ^ bias;
        return e;
    }

    /// <summary>The normal filter on a macroblock edge: high-variance positions change p0 and q0 as the inner filter does, the others three pixels a side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Edge FilterMacroblockEdge(Edge e, int threshold, int interior, int hev)
    {
        Vector128<byte> p1p0 = AbsDiff(e.P1, e.P0), q1q0 = AbsDiff(e.Q1, e.Q0);
        Vector128<sbyte> mask = NormalMask(e, threshold, interior, p1p0, q1q0).AsSByte();
        Vector128<sbyte> high = HighVariance(p1p0, q1q0, hev);
        Vector128<byte> bias = Vector128.Create((byte)0x80);
        Vector128<sbyte> ps1 = (e.P1 ^ bias).AsSByte(), ps0 = (e.P0 ^ bias).AsSByte(), qs0 = (e.Q0 ^ bias).AsSByte(), qs1 = (e.Q1 ^ bias).AsSByte();
        Vector128<sbyte> w = Taps3(Sse2.SubtractSaturate(ps1, qs1), Sse2.SubtractSaturate(qs0, ps0)) & mask;
        Vector128<sbyte> h = w & high;
        qs0 = Sse2.SubtractSaturate(qs0, ShiftRight3(Sse2.AddSaturate(h, Vector128.Create((sbyte)4))));
        ps0 = Sse2.AddSaturate(ps0, ShiftRight3(Sse2.AddSaturate(h, Vector128.Create((sbyte)3))));
        w = Sse2.AndNot(high, w);
        (Vector128<short> lo, Vector128<short> hi) = Vector128.Widen(w);
        Vector128<sbyte> u27 = Taps(lo, hi, 27), u18 = Taps(lo, hi, 18), u9 = Taps(lo, hi, 9);
        Vector128<sbyte> ps2 = (e.P2 ^ bias).AsSByte(), qs2 = (e.Q2 ^ bias).AsSByte();
        e.Q0 = Sse2.SubtractSaturate(qs0, u27).AsByte() ^ bias;
        e.P0 = Sse2.AddSaturate(ps0, u27).AsByte() ^ bias;
        e.Q1 = Sse2.SubtractSaturate(qs1, u18).AsByte() ^ bias;
        e.P1 = Sse2.AddSaturate(ps1, u18).AsByte() ^ bias;
        e.Q2 = Sse2.SubtractSaturate(qs2, u9).AsByte() ^ bias;
        e.P2 = Sse2.AddSaturate(ps2, u9).AsByte() ^ bias;
        return e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AbsDiff(Vector128<byte> a, Vector128<byte> b) => Sse2.SubtractSaturate(a, b) | Sse2.SubtractSaturate(b, a);

    /// <summary>Halves unsigned bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Half(Vector128<byte> v) => Sse2.ShiftRightLogical(v.AsUInt16(), 1).AsByte() & Vector128.Create((byte)0x7F);

    /// <summary>Shifts signed bytes right by 3, rounding down: biased to unsigned, shifted, unbiased.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> ShiftRight3(Vector128<sbyte> v)
    {
        Vector128<byte> u = Sse2.ShiftRightLogical((v.AsByte() ^ Vector128.Create((byte)0x80)).AsUInt16(), 3).AsByte() & Vector128.Create((byte)0x1F);
        return (u - Vector128.Create((byte)16)).AsSByte();
    }

    /// <summary>Shifts signed bytes right by 1, rounding down.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> ShiftRight1(Vector128<sbyte> v)
    {
        Vector128<byte> u = Sse2.ShiftRightLogical((v.AsByte() ^ Vector128.Create((byte)0x80)).AsUInt16(), 1).AsByte() & Vector128.Create((byte)0x7F);
        return (u - Vector128.Create((byte)64)).AsSByte();
    }

    /// <summary>The wide filter's (63 + w × <paramref name="k"/>) >> 7, saturated to signed bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> Taps(Vector128<short> lo, Vector128<short> hi, short k)
    {
        Vector128<short> m = Vector128.Create(k), r = Vector128.Create((short)63);
        return Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.MultiplyLow(lo, m) + r, 7), Sse2.ShiftRightArithmetic(Sse2.MultiplyLow(hi, m) + r, 7));
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

    /// <summary>
    /// Transposes 16 rows of 8 bytes, 8 from <paramref name="a"/> then 8 from <paramref name="b"/> (rows 8 to 15 of a luma edge,
    /// or V after U), into the 8 columns of 16 bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Edge Transpose(ref byte a, ref byte b, int stride)
    {
        Vector128<byte> t0 = Sse2.UnpackLow(Row(ref a, 0), Row(ref a, stride)), t1 = Sse2.UnpackLow(Row(ref a, 2 * stride), Row(ref a, 3 * stride));
        Vector128<byte> t2 = Sse2.UnpackLow(Row(ref a, 4 * stride), Row(ref a, 5 * stride)), t3 = Sse2.UnpackLow(Row(ref a, 6 * stride), Row(ref a, 7 * stride));
        Vector128<byte> t4 = Sse2.UnpackLow(Row(ref b, 0), Row(ref b, stride)), t5 = Sse2.UnpackLow(Row(ref b, 2 * stride), Row(ref b, 3 * stride));
        Vector128<byte> t6 = Sse2.UnpackLow(Row(ref b, 4 * stride), Row(ref b, 5 * stride)), t7 = Sse2.UnpackLow(Row(ref b, 6 * stride), Row(ref b, 7 * stride));
        Vector128<short> u0 = Sse2.UnpackLow(t0.AsInt16(), t1.AsInt16()), u1 = Sse2.UnpackHigh(t0.AsInt16(), t1.AsInt16());
        Vector128<short> u2 = Sse2.UnpackLow(t2.AsInt16(), t3.AsInt16()), u3 = Sse2.UnpackHigh(t2.AsInt16(), t3.AsInt16());
        Vector128<short> u4 = Sse2.UnpackLow(t4.AsInt16(), t5.AsInt16()), u5 = Sse2.UnpackHigh(t4.AsInt16(), t5.AsInt16());
        Vector128<short> u6 = Sse2.UnpackLow(t6.AsInt16(), t7.AsInt16()), u7 = Sse2.UnpackHigh(t6.AsInt16(), t7.AsInt16());
        Vector128<int> v0 = Sse2.UnpackLow(u0.AsInt32(), u2.AsInt32()), v1 = Sse2.UnpackHigh(u0.AsInt32(), u2.AsInt32());
        Vector128<int> v2 = Sse2.UnpackLow(u1.AsInt32(), u3.AsInt32()), v3 = Sse2.UnpackHigh(u1.AsInt32(), u3.AsInt32());
        Vector128<int> w0 = Sse2.UnpackLow(u4.AsInt32(), u6.AsInt32()), w1 = Sse2.UnpackHigh(u4.AsInt32(), u6.AsInt32());
        Vector128<int> w2 = Sse2.UnpackLow(u5.AsInt32(), u7.AsInt32()), w3 = Sse2.UnpackHigh(u5.AsInt32(), u7.AsInt32());
        return new Edge
        {
            P3 = Sse2.UnpackLow(v0.AsInt64(), w0.AsInt64()).AsByte(),
            P2 = Sse2.UnpackHigh(v0.AsInt64(), w0.AsInt64()).AsByte(),
            P1 = Sse2.UnpackLow(v1.AsInt64(), w1.AsInt64()).AsByte(),
            P0 = Sse2.UnpackHigh(v1.AsInt64(), w1.AsInt64()).AsByte(),
            Q0 = Sse2.UnpackLow(v2.AsInt64(), w2.AsInt64()).AsByte(),
            Q1 = Sse2.UnpackHigh(v2.AsInt64(), w2.AsInt64()).AsByte(),
            Q2 = Sse2.UnpackLow(v3.AsInt64(), w3.AsInt64()).AsByte(),
            Q3 = Sse2.UnpackHigh(v3.AsInt64(), w3.AsInt64()).AsByte(),
        };
    }

    /// <summary>Transposes the 8 columns back into 16 rows of 8 bytes, written where <see cref="Transpose"/> read them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransposeBack(Edge e, ref byte a, ref byte b, int stride)
    {
        Vector128<short> a0 = Sse2.UnpackLow(e.P3, e.P2).AsInt16(), a1 = Sse2.UnpackHigh(e.P3, e.P2).AsInt16();
        Vector128<short> a2 = Sse2.UnpackLow(e.P1, e.P0).AsInt16(), a3 = Sse2.UnpackHigh(e.P1, e.P0).AsInt16();
        Vector128<short> a4 = Sse2.UnpackLow(e.Q0, e.Q1).AsInt16(), a5 = Sse2.UnpackHigh(e.Q0, e.Q1).AsInt16();
        Vector128<short> a6 = Sse2.UnpackLow(e.Q2, e.Q3).AsInt16(), a7 = Sse2.UnpackHigh(e.Q2, e.Q3).AsInt16();
        Vector128<int> b0 = Sse2.UnpackLow(a0, a2).AsInt32(), b1 = Sse2.UnpackHigh(a0, a2).AsInt32();
        Vector128<int> b2 = Sse2.UnpackLow(a1, a3).AsInt32(), b3 = Sse2.UnpackHigh(a1, a3).AsInt32();
        Vector128<int> b4 = Sse2.UnpackLow(a4, a6).AsInt32(), b5 = Sse2.UnpackHigh(a4, a6).AsInt32();
        Vector128<int> b6 = Sse2.UnpackLow(a5, a7).AsInt32(), b7 = Sse2.UnpackHigh(a5, a7).AsInt32();
        WriteRows(Sse2.UnpackLow(b0, b4).AsUInt64(), ref a, 0, stride);
        WriteRows(Sse2.UnpackHigh(b0, b4).AsUInt64(), ref a, 2 * stride, stride);
        WriteRows(Sse2.UnpackLow(b1, b5).AsUInt64(), ref a, 4 * stride, stride);
        WriteRows(Sse2.UnpackHigh(b1, b5).AsUInt64(), ref a, 6 * stride, stride);
        WriteRows(Sse2.UnpackLow(b2, b6).AsUInt64(), ref b, 0, stride);
        WriteRows(Sse2.UnpackHigh(b2, b6).AsUInt64(), ref b, 2 * stride, stride);
        WriteRows(Sse2.UnpackLow(b3, b7).AsUInt64(), ref b, 4 * stride, stride);
        WriteRows(Sse2.UnpackHigh(b3, b7).AsUInt64(), ref b, 6 * stride, stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Row(ref byte p, int offset) => Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset))).AsByte();

    /// <summary>Writes the two 8-byte halves of <paramref name="v"/> to two rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRows(Vector128<ulong> v, ref byte p, int offset, int stride)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, offset), v.ToScalar());
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, offset + stride), v.GetElement(1));
    }
}
