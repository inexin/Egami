using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.H264;

/// <summary>The filter settings of one slice: disable_deblocking_filter_idc and FilterOffsetA and B.</summary>
internal readonly record struct H264FilterParams(int DisableIdc, int OffsetA, int OffsetB);

/// <summary>
/// The deblocking filter (8.7) of a decoded frame, macroblock by macroblock in address order. The vector form filters eight
/// samples of an edge at a time in 16-bit lanes, which hold every sum the filter makes at up to 10 bits (the largest, 8 × 1023
/// + 4, is below 32768); vertical edges go through an 8 by 8 transpose. The scalar form is the reference.
/// </summary>
[SkipLocalsInit]
internal sealed class H264Deblock
{
    // Boundary strengths of the macroblock: [direction 0 vertical, 1 horizontal][edge 0..3][segment 0..3].
    private readonly int[] _bs = new int[2 * 4 * 4];

    private H264Picture _pic = null!;

    /// <summary>Gets whether the vector form runs: 128-bit vectors with the unpack operations the transpose needs.</summary>
    internal static bool VectorsSupported => Vector128.IsHardwareAccelerated && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported);

    /// <summary>Gets or sets whether the vector form runs; tests turn it off to compare with the scalar reference.</summary>
    internal static bool UseVectors { get; set; } = VectorsSupported;
    private H264MbState _mb = null!;
    private int _widthMbs;
    private int _bitDepthY;
    private int _bitDepthC;
    private int _chroma;

    /// <summary>Filters the macroblock rows from <paramref name="firstRow"/> to before <paramref name="endRow"/>.</summary>
    internal void Filter(H264Picture pic, H264MbState mb, IReadOnlyList<H264FilterParams> slices, int bitDepthY, int bitDepthC, int firstRow, int endRow)
    {
        for (int mbY = firstRow; mbY < endRow; mbY++)
        {
            for (int mbX = 0; mbX < mb.WidthMbs; mbX++)
            {
                FilterOne(pic, mb, slices, bitDepthY, bitDepthC, mbX, mbY);
            }
        }
    }

    /// <summary>Filters the edges of macroblock (mbX, mbY): its left and top edges and its internal ones.</summary>
    internal void FilterOne(H264Picture pic, H264MbState mb, IReadOnlyList<H264FilterParams> slices, int bitDepthY, int bitDepthC, int mbX, int mbY)
    {
        _pic = pic;
        _mb = mb;
        _widthMbs = mb.WidthMbs;
        _bitDepthY = bitDepthY;
        _bitDepthC = bitDepthC;
        _chroma = pic.ChromaFormatIdc;
        int addr = (mbY * _widthMbs) + mbX;
        int slice = mb.Slice[addr];
        if (slice < 0 || slice >= slices.Count)
        {
            return;
        }

        H264FilterParams p = slices[slice];
        if (p.DisableIdc == 1)
        {
            return;
        }

        bool left = mbX > 0 && mb.Slice[addr - 1] >= 0 && (p.DisableIdc != 2 || mb.Slice[addr - 1] == slice);
        bool top = mbY > 0 && mb.Slice[addr - _widthMbs] >= 0 && (p.DisableIdc != 2 || mb.Slice[addr - _widthMbs] == slice);
        FilterMacroblock(mbX, mbY, addr, left, top, p);
    }

    private void FilterMacroblock(int mbX, int mbY, int addr, bool left, bool top, H264FilterParams p)
    {
        if (_pic.Narrow)
        {
            FilterMacroblock<byte>(mbX, mbY, addr, left, top, p);
        }
        else
        {
            FilterMacroblock<ushort>(mbX, mbY, addr, left, top, p);
        }
    }

    private void FilterMacroblock<T>(int mbX, int mbY, int addr, bool left, bool top, H264FilterParams p)
        where T : unmanaged
    {
        if (UseVectors && Avx2.IsSupported)
        {
            FilterMacroblockWide<T>(mbX, mbY, addr, left, top, p);
            return;
        }

        ComputeStrengths(mbX, mbY, addr, left, top);
        bool t8 = _mb.Transform8x8[addr];
        int stride = _pic.Stride;
        int origin = _pic.Origin + (mbY * 16 * stride) + (mbX * 16);
        int qp = _mb.Qp[addr];
        for (int dir = 0; dir < 2; dir++)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                if ((edge == 0 && !(dir == 0 ? left : top)) || (t8 && (edge & 1) != 0))
                {
                    continue;
                }

                int qpP = edge == 0 ? _mb.Qp[dir == 0 ? addr - 1 : addr - _widthMbs] : qp;
                int qpAv = (qpP + qp + 1) >> 1;
                int at = dir == 0 ? origin + (edge * 4) : origin + (edge * 4 * stride);
                FilterEdge(_pic.Y<T>(), at, stride, dir, 16, qpAv, p, edge, false, 4, _bitDepthY);
            }
        }

        if (_chroma != 0)
        {
            FilterChromaPlanes<T>(mbX, mbY, addr, left, top, p);
        }
    }

    /// <summary>Filters the chroma edges of the macroblock, plane by plane, with the strengths of its luma edges.</summary>
    private void FilterChromaPlanes<T>(int mbX, int mbY, int addr, bool left, bool top, H264FilterParams p)
        where T : unmanaged
    {
        int cStride = _pic.ChromaStride;
        int cw = 8, ch = _chroma == 1 ? 8 : 16;
        int cOrigin = _pic.ChromaOrigin + (mbY * ch * cStride) + (mbX * cw);
        for (int c = 0; c < 2; c++)
        {
            T[] plane = c == 0 ? _pic.Cb<T>() : _pic.Cr<T>();
            sbyte[] qpc = _mb.QpC[c];
            for (int dir = 0; dir < 2; dir++)
            {
                int edges = dir == 0 ? 2 : ch / 4;
                for (int ce = 0; ce < edges; ce++)
                {
                    if (ce == 0 && !(dir == 0 ? left : top))
                    {
                        continue;
                    }

                    // The luma edge whose strengths the chroma edge takes.
                    int lumaEdge = dir == 0 ? ce * 2 : _chroma == 1 ? ce * 2 : ce;
                    int qpP = ce == 0 ? qpc[dir == 0 ? addr - 1 : addr - _widthMbs] : qpc[addr];
                    int qpAv = (qpP + qpc[addr] + 1) >> 1;
                    int at = dir == 0 ? cOrigin + (ce * 4) : cOrigin + (ce * 4 * cStride);
                    int length = dir == 0 ? ch : cw;
                    int segment = dir == 0 ? (_chroma == 1 ? 2 : 4) : 2;
                    FilterEdge(plane, at, cStride, dir, length, qpAv, p, lumaEdge, true, segment, _bitDepthC);
                }
            }
        }
    }

    /// <summary>
    /// <see cref="FilterMacroblock"/> with 256-bit vectors: a luma edge's 16 samples at once, and the Cb and Cr edges of 4:2:0
    /// side by side in one vector, so each filter runs half as often as in eight lanes.
    /// </summary>
    private void FilterMacroblockWide<T>(int mbX, int mbY, int addr, bool left, bool top, H264FilterParams p)
        where T : unmanaged
    {
        ComputeStrengths(mbX, mbY, addr, left, top);
        ref int bs = ref MemoryMarshal.GetArrayDataReference(_bs);
        bool t8 = _mb.Transform8x8[addr];
        sbyte[] qps = _mb.Qp;
        int stride = _pic.Stride;
        int origin = _pic.Origin + (mbY * 16 * stride) + (mbX * 16);
        T[] luma = _pic.Y<T>();
        if (origin - (4 * stride) - 4 < 0 || origin + (19 * stride) + 16 > luma.Length)
        {
            throw H264Errors.Corrupt("A deblocking edge lies outside the picture.");
        }

        ref T y0 = ref H264Samples.At(luma, origin);
        int qp = qps[addr];
        int scaleY = 1 << (_bitDepthY - 8);
        Vector256<short> maxY = Vector256.Create((short)((1 << _bitDepthY) - 1));
        for (int dir = 0; dir < 2; dir++)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                if ((edge == 0 && !(dir == 0 ? left : top)) || (t8 && (edge & 1) != 0))
                {
                    continue;
                }

                Vector128<int> b4 = Vector128.LoadUnsafe(ref bs, (nuint)((dir * 16) + (edge * 4)));
                if (b4 == Vector128<int>.Zero)
                {
                    continue;
                }

                int qpP = edge == 0 ? qps[dir == 0 ? addr - 1 : addr - _widthMbs] : qp;
                int qpAv = (qpP + qp + 1) >> 1;
                int indexA = Math.Clamp(qpAv + p.OffsetA, 0, 51), indexB = Math.Clamp(qpAv + p.OffsetB, 0, 51);
                int alpha = H264Tables.Alpha[indexA] * scaleY, beta = H264Tables.Beta[indexB] * scaleY;
                if (alpha == 0 || beta == 0)
                {
                    continue;
                }

                Vector256<short> bs16 = Vector256.Create(
                    Vector128.Shuffle(b4.AsByte(), Vector128.Create((byte)0, 16, 0, 16, 0, 16, 0, 16, 4, 16, 4, 16, 4, 16, 4, 16)).AsInt16(),
                    Vector128.Shuffle(b4.AsByte(), Vector128.Create((byte)8, 16, 8, 16, 8, 16, 8, 16, 12, 16, 12, 16, 12, 16, 12, 16)).AsInt16());
                Vector256<short> tc0 = TcLanes(bs16, indexA, scaleY);
                if (dir == 1)
                {
                    ref T q = ref Unsafe.Add(ref y0, edge * 4 * stride);
                    Vector256<short> p3 = Row256(ref q, -4 * stride), p2 = Row256(ref q, -3 * stride), p1 = Row256(ref q, -2 * stride),
                        p0 = Row256(ref q, -stride), q0 = Row256(ref q, 0), q1 = Row256(ref q, stride), q2 = Row256(ref q, 2 * stride),
                        q3 = Row256(ref q, 3 * stride);
                    FilterLuma(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, bs16, tc0, Vector256.Create((short)alpha), Vector256.Create((short)beta), maxY);
                    Store256(p2, ref q, -3 * stride);
                    Store256(p1, ref q, -2 * stride);
                    Store256(p0, ref q, -stride);
                    Store256(q0, ref q, 0);
                    Store256(q1, ref q, stride);
                    Store256(q2, ref q, 2 * stride);
                }
                else
                {
                    // Sixteen rows of eight samples across the edge, transposed in two halves into eight columns of sixteen.
                    ref T r = ref Unsafe.Add(ref y0, (edge * 4) - 4);
                    ref T s = ref Unsafe.Add(ref r, 8 * stride);
                    Vector128<short> a0 = Row(ref r, 0), a1 = Row(ref r, stride), a2 = Row(ref r, 2 * stride), a3 = Row(ref r, 3 * stride),
                        a4 = Row(ref r, 4 * stride), a5 = Row(ref r, 5 * stride), a6 = Row(ref r, 6 * stride), a7 = Row(ref r, 7 * stride);
                    Vector128<short> c0 = Row(ref s, 0), c1 = Row(ref s, stride), c2 = Row(ref s, 2 * stride), c3 = Row(ref s, 3 * stride),
                        c4 = Row(ref s, 4 * stride), c5 = Row(ref s, 5 * stride), c6 = Row(ref s, 6 * stride), c7 = Row(ref s, 7 * stride);
                    Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
                    Transpose(ref c0, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7);
                    Vector256<short> p3 = Vector256.Create(a0, c0), p2 = Vector256.Create(a1, c1), p1 = Vector256.Create(a2, c2),
                        p0 = Vector256.Create(a3, c3), q0 = Vector256.Create(a4, c4), q1 = Vector256.Create(a5, c5),
                        q2 = Vector256.Create(a6, c6), q3 = Vector256.Create(a7, c7);
                    FilterLuma(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, bs16, tc0, Vector256.Create((short)alpha), Vector256.Create((short)beta), maxY);
                    a0 = p3.GetLower();
                    a1 = p2.GetLower();
                    a2 = p1.GetLower();
                    a3 = p0.GetLower();
                    a4 = q0.GetLower();
                    a5 = q1.GetLower();
                    a6 = q2.GetLower();
                    a7 = q3.GetLower();
                    c0 = p3.GetUpper();
                    c1 = p2.GetUpper();
                    c2 = p1.GetUpper();
                    c3 = p0.GetUpper();
                    c4 = q0.GetUpper();
                    c5 = q1.GetUpper();
                    c6 = q2.GetUpper();
                    c7 = q3.GetUpper();
                    Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
                    Transpose(ref c0, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7);
                    StoreRows(ref r, stride, a0, a1, a2, a3, a4, a5, a6, a7);
                    StoreRows(ref s, stride, c0, c1, c2, c3, c4, c5, c6, c7);
                }
            }
        }

        if (_chroma == 1)
        {
            FilterChromaWide<T>(mbX, mbY, addr, left, top, p);
        }
        else if (_chroma != 0)
        {
            FilterChromaPlanes<T>(mbX, mbY, addr, left, top, p);
        }
    }

    /// <summary>The 4:2:0 chroma edges of the macroblock, Cb in the low half of each vector and Cr in the high half.</summary>
    private void FilterChromaWide<T>(int mbX, int mbY, int addr, bool left, bool top, H264FilterParams p)
        where T : unmanaged
    {
        ref int bs = ref MemoryMarshal.GetArrayDataReference(_bs);
        int stride = _pic.ChromaStride;
        int origin = _pic.ChromaOrigin + (mbY * 8 * stride) + (mbX * 8);
        T[] cbPlane = _pic.Cb<T>(), crPlane = _pic.Cr<T>();
        if (origin - (4 * stride) - 4 < 0 || origin + (11 * stride) + 8 > Math.Min(cbPlane.Length, crPlane.Length))
        {
            throw H264Errors.Corrupt("A deblocking edge lies outside the picture.");
        }

        ref T cb0 = ref H264Samples.At(cbPlane, origin);
        ref T cr0 = ref H264Samples.At(crPlane, origin);
        sbyte[] qpCb = _mb.QpC[0], qpCr = _mb.QpC[1];
        int scale = 1 << (_bitDepthC - 8);
        Vector256<short> max = Vector256.Create((short)((1 << _bitDepthC) - 1));
        for (int dir = 0; dir < 2; dir++)
        {
            for (int ce = 0; ce < 2; ce++)
            {
                if (ce == 0 && !(dir == 0 ? left : top))
                {
                    continue;
                }

                // The chroma edge takes the strengths of luma edge 2ce, two samples to a segment.
                Vector128<int> b4 = Vector128.LoadUnsafe(ref bs, (nuint)((dir * 16) + (ce * 8)));
                if (b4 == Vector128<int>.Zero)
                {
                    continue;
                }

                int pAddr = ce != 0 ? addr : dir == 0 ? addr - 1 : addr - _widthMbs;
                (int alphaCb, int betaCb, int indexCb) = Thresholds((qpCb[pAddr] + qpCb[addr] + 1) >> 1, p, scale);
                (int alphaCr, int betaCr, int indexCr) = Thresholds((qpCr[pAddr] + qpCr[addr] + 1) >> 1, p, scale);
                Vector128<short> bs8 = Vector128.Shuffle(b4.AsByte(), Vector128.Create((byte)0, 16, 0, 16, 4, 16, 4, 16, 8, 16, 8, 16, 12, 16, 12, 16)).AsInt16();
                Vector128<short> bsCb = alphaCb == 0 || betaCb == 0 ? Vector128<short>.Zero : bs8;
                Vector128<short> bsCr = alphaCr == 0 || betaCr == 0 ? Vector128<short>.Zero : bs8;
                if ((bsCb | bsCr) == Vector128<short>.Zero)
                {
                    continue;
                }

                Vector256<short> bs16 = Vector256.Create(bsCb, bsCr);
                Vector256<short> tc0 = Vector256.Create(TcLanes(Vector256.Create(bsCb, bsCb), indexCb, scale).GetLower(), TcLanes(Vector256.Create(bsCr, bsCr), indexCr, scale).GetLower());
                Vector256<short> alpha = Vector256.Create(Vector128.Create((short)alphaCb), Vector128.Create((short)alphaCr));
                Vector256<short> beta = Vector256.Create(Vector128.Create((short)betaCb), Vector128.Create((short)betaCr));
                if (dir == 1)
                {
                    ref T qb = ref Unsafe.Add(ref cb0, ce * 4 * stride), qr = ref Unsafe.Add(ref cr0, ce * 4 * stride);
                    Vector256<short> p1 = Vector256.Create(Row(ref qb, -2 * stride), Row(ref qr, -2 * stride));
                    Vector256<short> p0 = Vector256.Create(Row(ref qb, -stride), Row(ref qr, -stride));
                    Vector256<short> q0 = Vector256.Create(Row(ref qb, 0), Row(ref qr, 0));
                    Vector256<short> q1 = Vector256.Create(Row(ref qb, stride), Row(ref qr, stride));
                    FilterChroma(p1, ref p0, ref q0, q1, bs16, tc0, alpha, beta, max);
                    Store(p0.GetLower(), ref qb, -stride);
                    Store(q0.GetLower(), ref qb, 0);
                    Store(p0.GetUpper(), ref qr, -stride);
                    Store(q0.GetUpper(), ref qr, 0);
                }
                else
                {
                    ref T rb = ref Unsafe.Add(ref cb0, (ce * 4) - 4), rr = ref Unsafe.Add(ref cr0, (ce * 4) - 4);
                    Vector128<short> a0 = Row(ref rb, 0), a1 = Row(ref rb, stride), a2 = Row(ref rb, 2 * stride), a3 = Row(ref rb, 3 * stride),
                        a4 = Row(ref rb, 4 * stride), a5 = Row(ref rb, 5 * stride), a6 = Row(ref rb, 6 * stride), a7 = Row(ref rb, 7 * stride);
                    Vector128<short> c0 = Row(ref rr, 0), c1 = Row(ref rr, stride), c2 = Row(ref rr, 2 * stride), c3 = Row(ref rr, 3 * stride),
                        c4 = Row(ref rr, 4 * stride), c5 = Row(ref rr, 5 * stride), c6 = Row(ref rr, 6 * stride), c7 = Row(ref rr, 7 * stride);
                    Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
                    Transpose(ref c0, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7);
                    Vector256<short> p0 = Vector256.Create(a3, c3), q0 = Vector256.Create(a4, c4);
                    FilterChroma(Vector256.Create(a2, c2), ref p0, ref q0, Vector256.Create(a5, c5), bs16, tc0, alpha, beta, max);
                    a3 = p0.GetLower();
                    a4 = q0.GetLower();
                    c3 = p0.GetUpper();
                    c4 = q0.GetUpper();
                    Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
                    Transpose(ref c0, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7);
                    StoreRows(ref rb, stride, a0, a1, a2, a3, a4, a5, a6, a7);
                    StoreRows(ref rr, stride, c0, c1, c2, c3, c4, c5, c6, c7);
                }
            }
        }
    }

    /// <summary>alpha, beta and indexA of an edge (8.7.2.2), scaled to the bit depth.</summary>
    private static (int Alpha, int Beta, int IndexA) Thresholds(int qpAv, H264FilterParams p, int scale)
    {
        int indexA = Math.Clamp(qpAv + p.OffsetA, 0, 51), indexB = Math.Clamp(qpAv + p.OffsetB, 0, 51);
        return (H264Tables.Alpha[indexA] * scale, H264Tables.Beta[indexB] * scale, indexA);
    }

    /// <summary>tC0 (Table 8-17) for each lane's bS: 0 for bS 0 and 4.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> TcLanes(Vector256<short> bs, int indexA, int scale)
    {
        ReadOnlySpan<byte> tc = H264Tables.Tc0;
        return (Vector256.Equals(bs, Vector256.Create((short)1)) & Vector256.Create((short)(tc[indexA * 3] * scale)))
            | (Vector256.Equals(bs, Vector256.Create((short)2)) & Vector256.Create((short)(tc[(indexA * 3) + 1] * scale)))
            | (Vector256.Equals(bs, Vector256.Create((short)3)) & Vector256.Create((short)(tc[(indexA * 3) + 2] * scale)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Row256<T>(ref T at, int offset)
        where T : unmanaged => H264Samples.Load16(ref at, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store256<T>(Vector256<short> v, ref T at, int offset)
        where T : unmanaged => H264Samples.Store16(ref at, offset, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRows<T>(ref T r, int stride, Vector128<short> a0, Vector128<short> a1, Vector128<short> a2, Vector128<short> a3,
        Vector128<short> a4, Vector128<short> a5, Vector128<short> a6, Vector128<short> a7)
        where T : unmanaged
    {
        Store(a0, ref r, 0);
        Store(a1, ref r, stride);
        Store(a2, ref r, 2 * stride);
        Store(a3, ref r, 3 * stride);
        Store(a4, ref r, 4 * stride);
        Store(a5, ref r, 5 * stride);
        Store(a6, ref r, 6 * stride);
        Store(a7, ref r, 7 * stride);
    }

    /// <summary><see cref="FilterLuma(Vector128{short}, ref Vector128{short}, ref Vector128{short}, ref Vector128{short}, ref Vector128{short}, ref Vector128{short}, ref Vector128{short}, Vector128{short}, Vector128{short}, Vector128{short}, int, int, int)"/> for sixteen lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterLuma(Vector256<short> p3, ref Vector256<short> p2, ref Vector256<short> p1, ref Vector256<short> p0,
        ref Vector256<short> q0, ref Vector256<short> q1, ref Vector256<short> q2, Vector256<short> q3, Vector256<short> bs,
        Vector256<short> tc0, Vector256<short> vAlpha, Vector256<short> vBeta, Vector256<short> top)
    {
        Vector256<short> filter = Vector256.GreaterThan(bs, Vector256<short>.Zero)
            & Vector256.LessThan(Vector256.Abs(p0 - q0), vAlpha)
            & Vector256.LessThan(Vector256.Abs(p1 - p0), vBeta)
            & Vector256.LessThan(Vector256.Abs(q1 - q0), vBeta);
        if (filter == Vector256<short>.Zero)
        {
            return;
        }

        Vector256<short> one = Vector256<short>.One, two = Vector256.Create((short)2), four = Vector256.Create((short)4);
        Vector256<short> ap = Vector256.LessThan(Vector256.Abs(p2 - p0), vBeta);
        Vector256<short> aq = Vector256.LessThan(Vector256.Abs(q2 - q0), vBeta);
        Vector256<short> strongBs = Vector256.Equals(bs, four);

        Vector256<short> tc = tc0 - ap - aq;
        Vector256<short> delta = Vector256.Min(Vector256.Max((((q0 - p0) << 2) + (p1 - q1) + four) >> 3, -tc), tc);
        Vector256<short> np0 = Vector256.Min(Vector256.Max(p0 + delta, Vector256<short>.Zero), top);
        Vector256<short> nq0 = Vector256.Min(Vector256.Max(q0 - delta, Vector256<short>.Zero), top);
        Vector256<short> avg = (p0 + q0 + one) >> 1;
        Vector256<short> np1 = Vector256.ConditionalSelect(ap, p1 + Vector256.Min(Vector256.Max((p2 + avg - (p1 << 1)) >> 1, -tc0), tc0), p1);
        Vector256<short> nq1 = Vector256.ConditionalSelect(aq, q1 + Vector256.Min(Vector256.Max((q2 + avg - (q1 << 1)) >> 1, -tc0), tc0), q1);

        Vector256<short> small = Vector256.LessThan(Vector256.Abs(p0 - q0), (vAlpha >> 2) + two);
        Vector256<short> strongP = ap & small, strongQ = aq & small;
        Vector256<short> sp0 = Vector256.ConditionalSelect(strongP, (p2 + (p1 << 1) + (p0 << 1) + (q0 << 1) + q1 + four) >> 3, ((p1 << 1) + p0 + q1 + two) >> 2);
        Vector256<short> sp1 = Vector256.ConditionalSelect(strongP, (p2 + p1 + p0 + q0 + two) >> 2, p1);
        Vector256<short> sp2 = Vector256.ConditionalSelect(strongP, ((p3 << 1) + (p2 * (short)3) + p1 + p0 + q0 + four) >> 3, p2);
        Vector256<short> sq0 = Vector256.ConditionalSelect(strongQ, (p1 + (p0 << 1) + (q0 << 1) + (q1 << 1) + q2 + four) >> 3, ((q1 << 1) + q0 + p1 + two) >> 2);
        Vector256<short> sq1 = Vector256.ConditionalSelect(strongQ, (p0 + q0 + q1 + q2 + two) >> 2, q1);
        Vector256<short> sq2 = Vector256.ConditionalSelect(strongQ, ((q3 << 1) + (q2 * (short)3) + q1 + q0 + p0 + four) >> 3, q2);

        Vector256<short> normal = filter & ~strongBs, strong = filter & strongBs;
        p0 = Vector256.ConditionalSelect(strong, sp0, Vector256.ConditionalSelect(normal, np0, p0));
        q0 = Vector256.ConditionalSelect(strong, sq0, Vector256.ConditionalSelect(normal, nq0, q0));
        p1 = Vector256.ConditionalSelect(strong, sp1, Vector256.ConditionalSelect(normal, np1, p1));
        q1 = Vector256.ConditionalSelect(strong, sq1, Vector256.ConditionalSelect(normal, nq1, q1));
        p2 = Vector256.ConditionalSelect(strong, sp2, p2);
        q2 = Vector256.ConditionalSelect(strong, sq2, q2);
    }

    /// <summary>The chroma filter for sixteen lanes, with alpha and beta per lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterChroma(Vector256<short> p1, ref Vector256<short> p0, ref Vector256<short> q0, Vector256<short> q1,
        Vector256<short> bs, Vector256<short> tc0, Vector256<short> vAlpha, Vector256<short> vBeta, Vector256<short> top)
    {
        Vector256<short> filter = Vector256.GreaterThan(bs, Vector256<short>.Zero)
            & Vector256.LessThan(Vector256.Abs(p0 - q0), vAlpha)
            & Vector256.LessThan(Vector256.Abs(p1 - p0), vBeta)
            & Vector256.LessThan(Vector256.Abs(q1 - q0), vBeta);
        if (filter == Vector256<short>.Zero)
        {
            return;
        }

        Vector256<short> two = Vector256.Create((short)2), four = Vector256.Create((short)4);
        Vector256<short> tc = tc0 + Vector256<short>.One;
        Vector256<short> delta = Vector256.Min(Vector256.Max((((q0 - p0) << 2) + (p1 - q1) + four) >> 3, -tc), tc);
        Vector256<short> np0 = Vector256.Min(Vector256.Max(p0 + delta, Vector256<short>.Zero), top);
        Vector256<short> nq0 = Vector256.Min(Vector256.Max(q0 - delta, Vector256<short>.Zero), top);
        Vector256<short> strong = Vector256.Equals(bs, four);
        np0 = Vector256.ConditionalSelect(strong, ((p1 << 1) + p0 + q1 + two) >> 2, np0);
        nq0 = Vector256.ConditionalSelect(strong, ((q1 << 1) + q0 + p1 + two) >> 2, nq0);
        p0 = Vector256.ConditionalSelect(filter, np0, p0);
        q0 = Vector256.ConditionalSelect(filter, nq0, q0);
    }

    /// <summary>
    /// Filters one edge of <paramref name="length"/> samples. The strength of sample k is bS[dir][edge][k / segment]: a luma edge has
    /// four segments of 4 samples; a chroma edge takes its luma edge's strengths over fewer samples.
    /// </summary>
    private void FilterEdge<T>(T[] plane, int at, int stride, int dir, int length, int qpAv, H264FilterParams p, int edge, bool chroma, int segment, int bitDepth)
        where T : unmanaged
    {
        int first = (dir * 16) + (edge * 4);
        if ((_bs[first] | _bs[first + 1] | _bs[first + 2] | _bs[first + 3]) == 0)
        {
            return;
        }

        if (UseVectors)
        {
            FilterEdgeVector(plane, at, stride, dir, length, qpAv, p, edge, chroma, segment, bitDepth);
            return;
        }

        int indexA = Math.Clamp(qpAv + p.OffsetA, 0, 51);
        int indexB = Math.Clamp(qpAv + p.OffsetB, 0, 51);
        int scale = 1 << (bitDepth - 8);
        int alpha = H264Tables.Alpha[indexA] * scale;
        int beta = H264Tables.Beta[indexB] * scale;
        if (alpha == 0 || beta == 0)
        {
            return;
        }

        int max = (1 << bitDepth) - 1;

        // Across the edge (step) and along it (along).
        int step = dir == 0 ? 1 : stride;
        int along = dir == 0 ? stride : 1;
        for (int k = 0; k < length; k++)
        {
            int bS = _bs[(dir * 16) + (edge * 4) + (k / segment)];
            if (bS == 0)
            {
                continue;
            }

            int q0At = at + (k * along);
            int p0 = H264Samples.Get(plane, q0At - step), q0 = H264Samples.Get(plane, q0At);
            int p1 = H264Samples.Get(plane, q0At - (2 * step)), q1 = H264Samples.Get(plane, q0At + step);
            if (Math.Abs(p0 - q0) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta)
            {
                continue;
            }

            if (chroma)
            {
                if (bS < 4)
                {
                    int tc = (H264Tables.Tc0[(indexA * 3) + bS - 1] * scale) + 1;
                    int delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                    H264Samples.Set(plane, q0At - step, Math.Clamp(p0 + delta, 0, max));
                    H264Samples.Set(plane, q0At, Math.Clamp(q0 - delta, 0, max));
                }
                else
                {
                    H264Samples.Set(plane, q0At - step, (((2 * p1) + p0 + q1 + 2) >> 2));
                    H264Samples.Set(plane, q0At, (((2 * q1) + q0 + p1 + 2) >> 2));
                }

                continue;
            }

            int p2 = H264Samples.Get(plane, q0At - (3 * step)), q2 = H264Samples.Get(plane, q0At + (2 * step));
            int ap = Math.Abs(p2 - p0), aq = Math.Abs(q2 - q0);
            if (bS < 4)
            {
                int tc0 = H264Tables.Tc0[(indexA * 3) + bS - 1] * scale;
                int tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
                int delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                H264Samples.Set(plane, q0At - step, Math.Clamp(p0 + delta, 0, max));
                H264Samples.Set(plane, q0At, Math.Clamp(q0 - delta, 0, max));
                if (ap < beta)
                {
                    H264Samples.Set(plane, q0At - (2 * step), (p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0)));
                }

                if (aq < beta)
                {
                    H264Samples.Set(plane, q0At + step, (q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0)));
                }

                continue;
            }

            bool strong = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
            if (ap < beta && strong)
            {
                int p3 = H264Samples.Get(plane, q0At - (4 * step));
                H264Samples.Set(plane, q0At - step, ((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3));
                H264Samples.Set(plane, q0At - (2 * step), ((p2 + p1 + p0 + q0 + 2) >> 2));
                H264Samples.Set(plane, q0At - (3 * step), (((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3));
            }
            else
            {
                H264Samples.Set(plane, q0At - step, (((2 * p1) + p0 + q1 + 2) >> 2));
            }

            if (aq < beta && strong)
            {
                int q3 = H264Samples.Get(plane, q0At + (3 * step));
                H264Samples.Set(plane, q0At, ((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3));
                H264Samples.Set(plane, q0At + step, ((p0 + q0 + q1 + q2 + 2) >> 2));
                H264Samples.Set(plane, q0At + (2 * step), (((2 * q3) + (3 * q2) + q1 + q0 + p0 + 4) >> 3));
            }
            else
            {
                H264Samples.Set(plane, q0At, (((2 * q1) + q0 + p1 + 2) >> 2));
            }
        }
    }

    /// <summary>Filters one edge as <see cref="FilterEdge"/> does, eight samples along it at a time.</summary>
    private void FilterEdgeVector<T>(T[] plane, int at, int stride, int dir, int length, int qpAv, H264FilterParams p, int edge, bool chroma, int segment, int bitDepth)
        where T : unmanaged
    {
        int indexA = Math.Clamp(qpAv + p.OffsetA, 0, 51);
        int indexB = Math.Clamp(qpAv + p.OffsetB, 0, 51);
        int scale = 1 << (bitDepth - 8);
        int alpha = H264Tables.Alpha[indexA] * scale;
        int beta = H264Tables.Beta[indexB] * scale;
        if (alpha == 0 || beta == 0)
        {
            return;
        }

        int reach = dir == 0 ? 4 : 4 * stride;
        if (at - reach < 0 || at + ((length - 1) * (dir == 0 ? stride : 1)) + reach + 8 > plane.Length)
        {
            throw H264Errors.Corrupt("A deblocking edge lies outside the picture.");
        }

        ref T origin = ref MemoryMarshal.GetArrayDataReference(plane);
        int first = (dir * 16) + (edge * 4);
        int tc1 = H264Tables.Tc0[indexA * 3] * scale, tc2 = H264Tables.Tc0[(indexA * 3) + 1] * scale, tc3 = H264Tables.Tc0[(indexA * 3) + 2] * scale;
        for (int k0 = 0; k0 < length; k0 += 8)
        {
            // Eight lanes cover two segments of four samples or four of two.
            Vector128<short> bs;
            if (segment == 4)
            {
                int s0 = _bs[first + (k0 >> 2)], s1 = _bs[first + (k0 >> 2) + 1];
                if ((s0 | s1) == 0)
                {
                    continue;
                }

                bs = Vector128.Create(Vector64.Create((short)s0), Vector64.Create((short)s1));
            }
            else
            {
                int s0 = _bs[first + (k0 >> 1)], s1 = _bs[first + (k0 >> 1) + 1], s2 = _bs[first + (k0 >> 1) + 2], s3 = _bs[first + (k0 >> 1) + 3];
                if ((s0 | s1 | s2 | s3) == 0)
                {
                    continue;
                }

                bs = Vector128.Create((short)s0, (short)s0, (short)s1, (short)s1, (short)s2, (short)s2, (short)s3, (short)s3);
            }

            Vector128<short> tc0 = (Vector128.Equals(bs, Vector128.Create((short)1)) & Vector128.Create((short)tc1))
                | (Vector128.Equals(bs, Vector128.Create((short)2)) & Vector128.Create((short)tc2))
                | (Vector128.Equals(bs, Vector128.Create((short)3)) & Vector128.Create((short)tc3));
            Vector128<short> p3, p2, p1, p0, q0, q1, q2, q3;
            if (dir == 1)
            {
                ref T q = ref Unsafe.Add(ref origin, at + k0);
                p3 = Row(ref q, -4 * stride);
                p2 = Row(ref q, -3 * stride);
                p1 = Row(ref q, -2 * stride);
                p0 = Row(ref q, -stride);
                q0 = Row(ref q, 0);
                q1 = Row(ref q, stride);
                q2 = Row(ref q, 2 * stride);
                q3 = Row(ref q, 3 * stride);
            }
            else
            {
                ref T r = ref Unsafe.Add(ref origin, at + (k0 * stride) - 4);
                p3 = Row(ref r, 0);
                p2 = Row(ref r, stride);
                p1 = Row(ref r, 2 * stride);
                p0 = Row(ref r, 3 * stride);
                q0 = Row(ref r, 4 * stride);
                q1 = Row(ref r, 5 * stride);
                q2 = Row(ref r, 6 * stride);
                q3 = Row(ref r, 7 * stride);
                Transpose(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3);
            }

            if (chroma)
            {
                FilterChroma(ref p1, ref p0, ref q0, ref q1, bs, tc0, alpha, beta, (1 << bitDepth) - 1);
            }
            else
            {
                FilterLuma(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, bs, tc0, alpha, beta, (1 << bitDepth) - 1);
            }

            if (dir == 1)
            {
                ref T q = ref Unsafe.Add(ref origin, at + k0);
                if (!chroma)
                {
                    Store(p2, ref q, -3 * stride);
                    Store(q2, ref q, 2 * stride);
                }

                Store(p1, ref q, -2 * stride);
                Store(p0, ref q, -stride);
                Store(q0, ref q, 0);
                Store(q1, ref q, stride);
            }
            else
            {
                Transpose(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3);
                ref T r = ref Unsafe.Add(ref origin, at + (k0 * stride) - 4);
                Store(p3, ref r, 0);
                Store(p2, ref r, stride);
                Store(p1, ref r, 2 * stride);
                Store(p0, ref r, 3 * stride);
                Store(q0, ref r, 4 * stride);
                Store(q1, ref r, 5 * stride);
                Store(q2, ref r, 6 * stride);
                Store(q3, ref r, 7 * stride);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Row<T>(ref T at, int offset)
        where T : unmanaged => H264Samples.Load8(ref at, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store<T>(Vector128<short> v, ref T at, int offset)
        where T : unmanaged => H264Samples.Store8(ref at, offset, v);

    /// <summary>The luma filter of 8.7.2.3 and 8.7.2.4 for eight lanes: bS 1 to 3 by tc, bS 4 strong, others unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterLuma(Vector128<short> p3, ref Vector128<short> p2, ref Vector128<short> p1, ref Vector128<short> p0,
        ref Vector128<short> q0, ref Vector128<short> q1, ref Vector128<short> q2, Vector128<short> q3, Vector128<short> bs,
        Vector128<short> tc0, int alpha, int beta, int max)
    {
        Vector128<short> vAlpha = Vector128.Create((short)alpha), vBeta = Vector128.Create((short)beta);
        Vector128<short> filter = Vector128.GreaterThan(bs, Vector128<short>.Zero)
            & Vector128.LessThan(Vector128.Abs(p0 - q0), vAlpha)
            & Vector128.LessThan(Vector128.Abs(p1 - p0), vBeta)
            & Vector128.LessThan(Vector128.Abs(q1 - q0), vBeta);
        if (filter == Vector128<short>.Zero)
        {
            return;
        }

        Vector128<short> one = Vector128<short>.One, two = Vector128.Create((short)2), four = Vector128.Create((short)4);
        Vector128<short> top = Vector128.Create((short)max);
        Vector128<short> ap = Vector128.LessThan(Vector128.Abs(p2 - p0), vBeta);
        Vector128<short> aq = Vector128.LessThan(Vector128.Abs(q2 - q0), vBeta);
        Vector128<short> strongBs = Vector128.Equals(bs, four);

        // bS below 4: tc grows by one for each side whose second sample is close.
        Vector128<short> tc = tc0 - ap - aq;
        Vector128<short> delta = Clamp((((q0 - p0) << 2) + (p1 - q1) + four) >> 3, -tc, tc);
        Vector128<short> np0 = Clamp(p0 + delta, Vector128<short>.Zero, top);
        Vector128<short> nq0 = Clamp(q0 - delta, Vector128<short>.Zero, top);
        Vector128<short> avg = (p0 + q0 + one) >> 1;
        Vector128<short> np1 = Vector128.ConditionalSelect(ap, p1 + Clamp((p2 + avg - (p1 << 1)) >> 1, -tc0, tc0), p1);
        Vector128<short> nq1 = Vector128.ConditionalSelect(aq, q1 + Clamp((q2 + avg - (q1 << 1)) >> 1, -tc0, tc0), q1);

        // bS 4: the strong filter where the edge is smooth enough, else the 3-tap one on the edge samples.
        Vector128<short> small = Vector128.LessThan(Vector128.Abs(p0 - q0), Vector128.Create((short)((alpha >> 2) + 2)));
        Vector128<short> strongP = ap & small, strongQ = aq & small;
        Vector128<short> sp0 = Vector128.ConditionalSelect(strongP, (p2 + (p1 << 1) + (p0 << 1) + (q0 << 1) + q1 + four) >> 3, ((p1 << 1) + p0 + q1 + two) >> 2);
        Vector128<short> sp1 = Vector128.ConditionalSelect(strongP, (p2 + p1 + p0 + q0 + two) >> 2, p1);
        Vector128<short> sp2 = Vector128.ConditionalSelect(strongP, ((p3 << 1) + (p2 * (short)3) + p1 + p0 + q0 + four) >> 3, p2);
        Vector128<short> sq0 = Vector128.ConditionalSelect(strongQ, (p1 + (p0 << 1) + (q0 << 1) + (q1 << 1) + q2 + four) >> 3, ((q1 << 1) + q0 + p1 + two) >> 2);
        Vector128<short> sq1 = Vector128.ConditionalSelect(strongQ, (p0 + q0 + q1 + q2 + two) >> 2, q1);
        Vector128<short> sq2 = Vector128.ConditionalSelect(strongQ, ((q3 << 1) + (q2 * (short)3) + q1 + q0 + p0 + four) >> 3, q2);

        Vector128<short> normal = filter & ~strongBs, strong = filter & strongBs;
        p0 = Vector128.ConditionalSelect(strong, sp0, Vector128.ConditionalSelect(normal, np0, p0));
        q0 = Vector128.ConditionalSelect(strong, sq0, Vector128.ConditionalSelect(normal, nq0, q0));
        p1 = Vector128.ConditionalSelect(strong, sp1, Vector128.ConditionalSelect(normal, np1, p1));
        q1 = Vector128.ConditionalSelect(strong, sq1, Vector128.ConditionalSelect(normal, nq1, q1));
        p2 = Vector128.ConditionalSelect(strong, sp2, p2);
        q2 = Vector128.ConditionalSelect(strong, sq2, q2);
    }

    /// <summary>The chroma filter for eight lanes: bS 1 to 3 by tc0 + 1, bS 4 the 3-tap filter on the edge samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterChroma(ref Vector128<short> p1, ref Vector128<short> p0, ref Vector128<short> q0, ref Vector128<short> q1,
        Vector128<short> bs, Vector128<short> tc0, int alpha, int beta, int max)
    {
        Vector128<short> vAlpha = Vector128.Create((short)alpha), vBeta = Vector128.Create((short)beta);
        Vector128<short> filter = Vector128.GreaterThan(bs, Vector128<short>.Zero)
            & Vector128.LessThan(Vector128.Abs(p0 - q0), vAlpha)
            & Vector128.LessThan(Vector128.Abs(p1 - p0), vBeta)
            & Vector128.LessThan(Vector128.Abs(q1 - q0), vBeta);
        if (filter == Vector128<short>.Zero)
        {
            return;
        }

        Vector128<short> one = Vector128<short>.One, two = Vector128.Create((short)2), four = Vector128.Create((short)4);
        Vector128<short> tc = tc0 + one;
        Vector128<short> delta = Clamp((((q0 - p0) << 2) + (p1 - q1) + four) >> 3, -tc, tc);
        Vector128<short> top = Vector128.Create((short)max);
        Vector128<short> np0 = Clamp(p0 + delta, Vector128<short>.Zero, top);
        Vector128<short> nq0 = Clamp(q0 - delta, Vector128<short>.Zero, top);
        Vector128<short> strong = Vector128.Equals(bs, four);
        np0 = Vector128.ConditionalSelect(strong, ((p1 << 1) + p0 + q1 + two) >> 2, np0);
        nq0 = Vector128.ConditionalSelect(strong, ((q1 << 1) + q0 + p1 + two) >> 2, nq0);
        p0 = Vector128.ConditionalSelect(filter, np0, p0);
        q0 = Vector128.ConditionalSelect(filter, nq0, q0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clamp(Vector128<short> v, Vector128<short> low, Vector128<short> high) => Vector128.Min(Vector128.Max(v, low), high);

    /// <summary>Transposes eight rows of eight 16-bit samples in place, with the unpack (zip) operations of the hardware.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector128<short> a0, ref Vector128<short> a1, ref Vector128<short> a2, ref Vector128<short> a3,
        ref Vector128<short> a4, ref Vector128<short> a5, ref Vector128<short> a6, ref Vector128<short> a7)
    {
        Vector128<short> b0 = Lo(a0, a1), b1 = Hi(a0, a1), b2 = Lo(a2, a3), b3 = Hi(a2, a3);
        Vector128<short> b4 = Lo(a4, a5), b5 = Hi(a4, a5), b6 = Lo(a6, a7), b7 = Hi(a6, a7);
        Vector128<int> c0 = Lo(b0.AsInt32(), b2.AsInt32()), c1 = Hi(b0.AsInt32(), b2.AsInt32());
        Vector128<int> c2 = Lo(b1.AsInt32(), b3.AsInt32()), c3 = Hi(b1.AsInt32(), b3.AsInt32());
        Vector128<int> c4 = Lo(b4.AsInt32(), b6.AsInt32()), c5 = Hi(b4.AsInt32(), b6.AsInt32());
        Vector128<int> c6 = Lo(b5.AsInt32(), b7.AsInt32()), c7 = Hi(b5.AsInt32(), b7.AsInt32());
        a0 = Lo(c0.AsInt64(), c4.AsInt64()).AsInt16();
        a1 = Hi(c0.AsInt64(), c4.AsInt64()).AsInt16();
        a2 = Lo(c1.AsInt64(), c5.AsInt64()).AsInt16();
        a3 = Hi(c1.AsInt64(), c5.AsInt64()).AsInt16();
        a4 = Lo(c2.AsInt64(), c6.AsInt64()).AsInt16();
        a5 = Hi(c2.AsInt64(), c6.AsInt64()).AsInt16();
        a6 = Lo(c3.AsInt64(), c7.AsInt64()).AsInt16();
        a7 = Hi(c3.AsInt64(), c7.AsInt64()).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Lo(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Hi(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Lo(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Hi(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<long> Lo(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<long> Hi(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <summary>Derives bS (8.7.2.1) for every 4-sample segment of the macroblock's luma edges, internal ones included.</summary>
    private void ComputeStrengths(int mbX, int mbY, int addr, bool left, bool top)
    {
        bool[] mbIntra = _pic.MbIntra;
        if (mbIntra[addr])
        {
            // An intra macroblock: 4 on its own edges, 3 inside.
            _bs.AsSpan().Fill(3);
            _bs.AsSpan(0, 4).Fill(4);
            _bs.AsSpan(16, 4).Fill(4);
            return;
        }

        if (!UseVectors)
        {
            ComputeStrengthsScalar(mbX, mbY, addr, left, top);
            return;
        }

        int w4 = _mb.W4;
        int b0 = (mbY * 4 * w4) + (mbX * 4);
        ref int bs = ref MemoryMarshal.GetArrayDataReference(_bs);

        // Vertical edges: row y of the macroblock's blocks holds segment y of edges 0 to 3; the row is transposed into the edges.
        Vector128<int> r0 = Strengths(b0, mbX > 0 ? 1 : 0), r1 = Strengths(b0 + w4, mbX > 0 ? 1 : 0);
        Vector128<int> r2 = Strengths(b0 + (2 * w4), mbX > 0 ? 1 : 0), r3 = Strengths(b0 + (3 * w4), mbX > 0 ? 1 : 0);
        Vector128<long> t0 = Lo(r0, r1).AsInt64(), t1 = Lo(r2, r3).AsInt64(), t2 = Hi(r0, r1).AsInt64(), t3 = Hi(r2, r3).AsInt64();
        Lo(t0, t1).AsInt32().StoreUnsafe(ref bs, 0);
        Hi(t0, t1).AsInt32().StoreUnsafe(ref bs, 4);
        Lo(t2, t3).AsInt32().StoreUnsafe(ref bs, 8);
        Hi(t2, t3).AsInt32().StoreUnsafe(ref bs, 12);

        // Horizontal edges: row e of blocks against the row above it is edge e.
        if (top)
        {
            (mbIntra[addr - _widthMbs] ? Vector128.Create(4) : Strengths(b0, w4)).StoreUnsafe(ref bs, 16);
        }

        Strengths(b0 + w4, w4).StoreUnsafe(ref bs, 20);
        Strengths(b0 + (2 * w4), w4).StoreUnsafe(ref bs, 24);
        Strengths(b0 + (3 * w4), w4).StoreUnsafe(ref bs, 28);
        if (left && mbIntra[addr - 1])
        {
            _bs[0] = _bs[1] = _bs[2] = _bs[3] = 4;
        }
    }

    /// <summary>
    /// The bS of four blocks q, q + 1, q + 2 and q + 3 against the blocks <paramref name="back"/> before each: 2 where either has
    /// coefficients, 0 where both have the same motion, else <see cref="MotionDiffers"/>. A back of 0 stands for 1 at the left
    /// picture edge, where the first block has no neighbour; its result is not used.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector128<int> Strengths(int q, int back)
    {
        ref byte nz = ref MemoryMarshal.GetArrayDataReference(_mb.Nonzero);
        int[][] refs = _pic.RefPicId, mvs = _pic.Mv;
        ref int ref0 = ref MemoryMarshal.GetArrayDataReference(refs[0]), ref1 = ref MemoryMarshal.GetArrayDataReference(refs[1]);
        ref int mv0 = ref MemoryMarshal.GetArrayDataReference(mvs[0]), mv1 = ref MemoryMarshal.GetArrayDataReference(mvs[1]);
        if ((uint)(q + 3) >= (uint)refs[0].Length || (uint)(q + 3) >= (uint)_mb.Nonzero.Length)
        {
            throw H264Errors.Corrupt("A deblocking edge lies outside the picture.");
        }

        Vector128<int> nq = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref nz, q))).AsByte()).AsInt16());
        Vector128<int> rq0 = Vector128.LoadUnsafe(ref ref0, (nuint)q), rq1 = Vector128.LoadUnsafe(ref ref1, (nuint)q);
        Vector128<int> mq0 = Vector128.LoadUnsafe(ref mv0, (nuint)q), mq1 = Vector128.LoadUnsafe(ref mv1, (nuint)q);
        Vector128<int> np, rp0, rp1, mp0, mp1;
        if (back == 0)
        {
            np = Vector128.Shuffle(nq, Vector128.Create(0, 0, 1, 2));
            rp0 = Vector128.Shuffle(rq0, Vector128.Create(0, 0, 1, 2));
            rp1 = Vector128.Shuffle(rq1, Vector128.Create(0, 0, 1, 2));
            mp0 = Vector128.Shuffle(mq0, Vector128.Create(0, 0, 1, 2));
            mp1 = Vector128.Shuffle(mq1, Vector128.Create(0, 0, 1, 2));
            back = 1;
        }
        else
        {
            int p = q - back;
            np = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref nz, p))).AsByte()).AsInt16());
            rp0 = Vector128.LoadUnsafe(ref ref0, (nuint)p);
            rp1 = Vector128.LoadUnsafe(ref ref1, (nuint)p);
            mp0 = Vector128.LoadUnsafe(ref mv0, (nuint)p);
            mp1 = Vector128.LoadUnsafe(ref mv1, (nuint)p);
        }

        Vector128<int> coded = ~Vector128.Equals(nq | np, Vector128<int>.Zero);
        Vector128<int> same = Vector128.Equals(rq0, rp0) & Vector128.Equals(rq1, rp1)
            & (Vector128.LessThan(rp0, Vector128<int>.Zero) | Vector128.Equals(mq0, mp0))
            & (Vector128.LessThan(rp1, Vector128<int>.Zero) | Vector128.Equals(mq1, mp1));
        Vector128<int> result = coded & Vector128.Create(2);
        uint pending = (~(coded | same)).ExtractMostSignificantBits();
        while (pending != 0)
        {
            int lane = BitOperations.TrailingZeroCount(pending);
            pending &= pending - 1;
            if (MotionDiffers(q + lane - back, q + lane))
            {
                result = result.WithElement(lane, 1);
            }
        }

        return result;
    }

    /// <summary>The scalar form of <see cref="ComputeStrengths"/>, the reference the vector form matches.</summary>
    private void ComputeStrengthsScalar(int mbX, int mbY, int addr, bool left, bool top)
    {
        int w4 = _mb.W4;
        int[] bsOut = _bs;
        bool[] mbIntra = _pic.MbIntra;
        bool intraQ = mbIntra[addr];
        byte[] nonzero = _mb.Nonzero;
        int[] ref0 = _pic.RefPicId[0], ref1 = _pic.RefPicId[1], mv0 = _pic.Mv[0], mv1 = _pic.Mv[1];
        int b0 = (mbY * 4 * w4) + (mbX * 4);
        for (int dir = 0; dir < 2; dir++)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                int o = (dir * 16) + (edge * 4);
                bool mbEdge = edge == 0;
                if (mbEdge && !(dir == 0 ? left : top))
                {
                    continue;
                }

                if (intraQ || mbIntra[!mbEdge ? addr : dir == 0 ? addr - 1 : addr - _widthMbs])
                {
                    int v = mbEdge ? 4 : 3;
                    bsOut[o] = bsOut[o + 1] = bsOut[o + 2] = bsOut[o + 3] = v;
                    continue;
                }

                // Block q of segment k, and the step back across the edge to its neighbour p.
                int q = dir == 0 ? b0 + edge : b0 + (edge * w4), along = dir == 0 ? w4 : 1, back = dir == 0 ? 1 : w4;
                for (int k = 0; k < 4; k++, q += along)
                {
                    int pb = q - back;
                    int bs;
                    if ((nonzero[q] | nonzero[pb]) != 0)
                    {
                        bs = 2;
                    }
                    else
                    {
                        // The same references and vectors, the common case, give bS 0 at once.
                        int rp0 = ref0[pb], rp1 = ref1[pb], rq0 = ref0[q], rq1 = ref1[q];
                        bool same = rp0 == rq0 && rp1 == rq1 && (rp0 < 0 || mv0[pb] == mv0[q]) && (rp1 < 0 || mv1[pb] == mv1[q]);
                        bs = !same && MotionDiffers(pb, q) ? 1 : 0;
                    }

                    bsOut[o + k] = bs;
                }
            }
        }
    }

    /// <summary>Whether the two blocks use different reference pictures, a different number of vectors or vectors 4 or more apart.</summary>
    private bool MotionDiffers(int p, int q)
    {
        int p0 = _pic.RefPicId[0][p], p1 = _pic.RefPicId[1][p];
        int q0 = _pic.RefPicId[0][q], q1 = _pic.RefPicId[1][q];
        int countP = (p0 >= 0 ? 1 : 0) + (p1 >= 0 ? 1 : 0), countQ = (q0 >= 0 ? 1 : 0) + (q1 >= 0 ? 1 : 0);
        if (countP != countQ)
        {
            return true;
        }

        if (countP == 1)
        {
            int refP = p0 >= 0 ? p0 : p1, refQ = q0 >= 0 ? q0 : q1;
            if (refP != refQ)
            {
                return true;
            }

            return Far(_pic.Mv[p0 >= 0 ? 0 : 1][p], _pic.Mv[q0 >= 0 ? 0 : 1][q]);
        }

        if (!((p0 == q0 && p1 == q1) || (p0 == q1 && p1 == q0)))
        {
            return true;
        }

        int mvP0 = _pic.Mv[0][p], mvP1 = _pic.Mv[1][p], mvQ0 = _pic.Mv[0][q], mvQ1 = _pic.Mv[1][q];
        if (p0 != p1)
        {
            // Two different pictures: compare the vectors for the same picture.
            return p0 == q0 ? Far(mvP0, mvQ0) || Far(mvP1, mvQ1) : Far(mvP0, mvQ1) || Far(mvP1, mvQ0);
        }

        // Both vectors name the same picture: either pairing may match.
        return (Far(mvP0, mvQ0) || Far(mvP1, mvQ1)) && (Far(mvP0, mvQ1) || Far(mvP1, mvQ0));
    }

    private static bool Far(int a, int b) =>
        Math.Abs(H264SliceDecoder.MvX(a) - H264SliceDecoder.MvX(b)) >= 4 || Math.Abs(H264SliceDecoder.MvY(a) - H264SliceDecoder.MvY(b)) >= 4;
}
