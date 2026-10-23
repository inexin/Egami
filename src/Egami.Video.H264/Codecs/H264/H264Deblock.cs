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
                FilterEdge(_pic.Y, at, stride, dir, 16, qpAv, p, edge, false, 4, _bitDepthY);
            }
        }

        if (_chroma == 0)
        {
            return;
        }

        int cStride = _pic.ChromaStride;
        int cw = 8, ch = _chroma == 1 ? 8 : 16;
        int cOrigin = _pic.ChromaOrigin + (mbY * ch * cStride) + (mbX * cw);
        for (int c = 0; c < 2; c++)
        {
            ushort[] plane = c == 0 ? _pic.Cb : _pic.Cr;
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
    /// Filters one edge of <paramref name="length"/> samples. The strength of sample k is bS[dir][edge][k / segment]: a luma edge has
    /// four segments of 4 samples; a chroma edge takes its luma edge's strengths over fewer samples.
    /// </summary>
    private void FilterEdge(ushort[] plane, int at, int stride, int dir, int length, int qpAv, H264FilterParams p, int edge, bool chroma, int segment, int bitDepth)
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
            int p0 = plane[q0At - step], q0 = plane[q0At];
            int p1 = plane[q0At - (2 * step)], q1 = plane[q0At + step];
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
                    plane[q0At - step] = (ushort)Math.Clamp(p0 + delta, 0, max);
                    plane[q0At] = (ushort)Math.Clamp(q0 - delta, 0, max);
                }
                else
                {
                    plane[q0At - step] = (ushort)(((2 * p1) + p0 + q1 + 2) >> 2);
                    plane[q0At] = (ushort)(((2 * q1) + q0 + p1 + 2) >> 2);
                }

                continue;
            }

            int p2 = plane[q0At - (3 * step)], q2 = plane[q0At + (2 * step)];
            int ap = Math.Abs(p2 - p0), aq = Math.Abs(q2 - q0);
            if (bS < 4)
            {
                int tc0 = H264Tables.Tc0[(indexA * 3) + bS - 1] * scale;
                int tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
                int delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                plane[q0At - step] = (ushort)Math.Clamp(p0 + delta, 0, max);
                plane[q0At] = (ushort)Math.Clamp(q0 - delta, 0, max);
                if (ap < beta)
                {
                    plane[q0At - (2 * step)] = (ushort)(p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
                }

                if (aq < beta)
                {
                    plane[q0At + step] = (ushort)(q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
                }

                continue;
            }

            bool strong = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
            if (ap < beta && strong)
            {
                int p3 = plane[q0At - (4 * step)];
                plane[q0At - step] = (ushort)((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3);
                plane[q0At - (2 * step)] = (ushort)((p2 + p1 + p0 + q0 + 2) >> 2);
                plane[q0At - (3 * step)] = (ushort)(((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3);
            }
            else
            {
                plane[q0At - step] = (ushort)(((2 * p1) + p0 + q1 + 2) >> 2);
            }

            if (aq < beta && strong)
            {
                int q3 = plane[q0At + (3 * step)];
                plane[q0At] = (ushort)((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3);
                plane[q0At + step] = (ushort)((p0 + q0 + q1 + q2 + 2) >> 2);
                plane[q0At + (2 * step)] = (ushort)(((2 * q3) + (3 * q2) + q1 + q0 + p0 + 4) >> 3);
            }
            else
            {
                plane[q0At] = (ushort)(((2 * q1) + q0 + p1 + 2) >> 2);
            }
        }
    }

    /// <summary>Filters one edge as <see cref="FilterEdge"/> does, eight samples along it at a time.</summary>
    private void FilterEdgeVector(ushort[] plane, int at, int stride, int dir, int length, int qpAv, H264FilterParams p, int edge, bool chroma, int segment, int bitDepth)
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

        ref ushort origin = ref MemoryMarshal.GetArrayDataReference(plane);
        Span<short> bsLanes = stackalloc short[8];
        Span<short> tcLanes = stackalloc short[8];
        int first = (dir * 16) + (edge * 4);
        for (int k0 = 0; k0 < length; k0 += 8)
        {
            int any = 0;
            for (int l = 0; l < 8; l++)
            {
                int bS = _bs[first + ((k0 + l) / segment)];
                any |= bS;
                bsLanes[l] = (short)bS;
                tcLanes[l] = (short)(bS is > 0 and < 4 ? H264Tables.Tc0[(indexA * 3) + bS - 1] * scale : 0);
            }

            if (any == 0)
            {
                continue;
            }

            Vector128<short> bs = Vector128.Create((ReadOnlySpan<short>)bsLanes), tc0 = Vector128.Create((ReadOnlySpan<short>)tcLanes);
            Vector128<short> p3, p2, p1, p0, q0, q1, q2, q3;
            if (dir == 1)
            {
                ref ushort q = ref Unsafe.Add(ref origin, at + k0);
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
                ref ushort r = ref Unsafe.Add(ref origin, at + (k0 * stride) - 4);
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
                ref ushort q = ref Unsafe.Add(ref origin, at + k0);
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
                ref ushort r = ref Unsafe.Add(ref origin, at + (k0 * stride) - 4);
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

    private static Vector128<short> Row(ref ushort at, int offset) => Vector128.LoadUnsafe(ref Unsafe.Add(ref at, offset)).AsInt16();

    private static void Store(Vector128<short> v, ref ushort at, int offset) => v.AsUInt16().StoreUnsafe(ref Unsafe.Add(ref at, offset));

    /// <summary>The luma filter of 8.7.2.3 and 8.7.2.4 for eight lanes: bS 1 to 3 by tc, bS 4 strong, others unchanged.</summary>
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

    private static Vector128<short> Clamp(Vector128<short> v, Vector128<short> low, Vector128<short> high) => Vector128.Min(Vector128.Max(v, low), high);

    /// <summary>Transposes eight rows of eight 16-bit samples in place, with the unpack (zip) operations of the hardware.</summary>
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

    private static Vector128<short> Lo(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    private static Vector128<short> Hi(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    private static Vector128<int> Lo(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    private static Vector128<int> Hi(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    private static Vector128<long> Lo(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    private static Vector128<long> Hi(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <summary>Derives bS (8.7.2.1) for every 4-sample segment of the macroblock's luma edges, internal ones included.</summary>
    private void ComputeStrengths(int mbX, int mbY, int addr, bool left, bool top)
    {
        int w4 = _mb.W4;
        bool intraQ = _pic.MbIntra[addr];
        for (int dir = 0; dir < 2; dir++)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                bool mbEdge = edge == 0;
                if (mbEdge && !(dir == 0 ? left : top))
                {
                    continue;
                }

                int addrP = !mbEdge ? addr : dir == 0 ? addr - 1 : addr - _widthMbs;
                bool intra = intraQ || _pic.MbIntra[addrP];
                for (int k = 0; k < 4; k++)
                {
                    int bs;
                    if (intra)
                    {
                        bs = mbEdge ? 4 : 3;
                    }
                    else
                    {
                        int qx = (mbX * 4) + (dir == 0 ? edge : k), qy = (mbY * 4) + (dir == 0 ? k : edge);
                        int q = (qy * w4) + qx;
                        int pb = dir == 0 ? q - 1 : q - w4;
                        bs = (_mb.Nonzero[q] | _mb.Nonzero[pb]) != 0 ? 2 : SameMotion(pb, q) ? 0 : MotionDiffers(pb, q) ? 1 : 0;
                    }

                    _bs[(dir * 16) + (edge * 4) + k] = bs;
                }
            }
        }
    }

    /// <summary>Whether two blocks have exactly the same references and vectors, the common case, which gives bS 0 at once.</summary>
    private bool SameMotion(int p, int q)
    {
        H264Picture pic = _pic;
        return pic.RefPicId[0][p] == pic.RefPicId[0][q] && pic.RefPicId[1][p] == pic.RefPicId[1][q]
            && (pic.RefPicId[0][p] < 0 || pic.Mv[0][p] == pic.Mv[0][q]) && (pic.RefPicId[1][p] < 0 || pic.Mv[1][p] == pic.Mv[1][q]);
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
