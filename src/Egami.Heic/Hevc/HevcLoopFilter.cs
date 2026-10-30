using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Heic.Hevc;

/// <summary>
/// The in-loop filters of a decoded picture (H.265 8.7): the deblocking filter (8.7.2), every vertical edge of the picture then
/// every horizontal one, and sample adaptive offset (8.7.3) on the deblocked samples.
/// </summary>
internal static class HevcLoopFilter
{
    private static readonly byte[] Beta =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24, 26, 28, 30, 32, 34,
        36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64,
    ];

    private static readonly byte[] Tc =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6,
        7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 22, 24,
    ];

    /// <summary>
    /// Deblocks, then applies SAO to, a decoded picture, on up to <paramref name="parallelism"/> threads by CTB rows. Vertical
    /// edges of different rows touch different samples, and so do horizontal edges 8 rows apart (each reads 4 and changes 3
    /// samples on either side), so the output does not depend on the number of threads.
    /// </summary>
    internal static void Apply(HevcPicture picture, int parallelism = 1, CancellationToken cancellationToken = default)
    {
        HevcSps sps = picture.Sps;
        int rows = sps.HeightInCtbs, rowHeight4 = 1 << (sps.CtbLog2Size - 2);
        foreach (bool vertical in (ReadOnlySpan<bool>)[true, false])
        {
            HevcThreads.For(rows, parallelism, row =>
                Deblock(picture, vertical, row * rowHeight4, Math.Min((row + 1) * rowHeight4, picture.Height4)), cancellationToken);
        }

        Sao(picture, parallelism, cancellationToken);
    }

    // ---------------------------------------------------------------- deblocking (8.7.2)

    private static void Deblock(HevcPicture picture, bool vertical, int y4Start, int y4End)
    {
        if (HevcInter.UseVectors && picture.Sps.BitDepthLuma <= 10)
        {
            DeblockPairs(picture, vertical, y4Start, y4End);
            return;
        }

        HevcSps sps = picture.Sps;
        HevcPps pps = picture.Pps;
        int w4 = picture.Width4;
        byte edgeFlag = vertical ? HevcPicture.EdgeLeft : HevcPicture.EdgeTop;

        // Luma edges on the 8x8 grid, in segments of four lines.
        for (int y4 = y4Start; y4 < y4End; y4++)
        {
            for (int x4 = 0; x4 < w4; x4++)
            {
                if ((vertical ? x4 : y4) % 2 != 0 || (vertical ? x4 : y4) == 0)
                {
                    continue;
                }

                int q = (y4 * w4) + x4;
                if ((picture.Flags[q] & edgeFlag) == 0)
                {
                    continue;
                }

                int p = vertical ? q - 1 : q - w4;
                if (!FilterEdge(picture, x4 << 2, y4 << 2, vertical, out HevcSliceHeader slice))
                {
                    continue;
                }

                int bs = BoundaryStrength(picture, p, q, (picture.Flags[q] & (vertical ? HevcPicture.TransformLeft : HevcPicture.TransformTop)) != 0);
                if (bs == 0)
                {
                    continue;
                }

                int qpL = (picture.QpY[q] + picture.QpY[p] + 1) >> 1;
                bool noP = NoFilter(picture, p), noQ = NoFilter(picture, q);
                LumaEdge(picture, x4 << 2, y4 << 2, vertical, qpL, bs, slice, noP, noQ);
                if (sps.ChromaArrayType != 0 && bs == 2)
                {
                    int sub = vertical ? sps.SubWidthC : sps.SubHeightC;
                    int lumaPosition = vertical ? x4 << 2 : y4 << 2;
                    if ((lumaPosition / sub) % 8 == 0)
                    {
                        for (int c = 1; c <= 2; c++)
                        {
                            int offset = c == 1 ? pps.CbQpOffset : pps.CrQpOffset;
                            ChromaEdge(picture, c, x4 << 2, y4 << 2, vertical, qpL + offset, slice, noP, noQ);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Deblocking with SSE2 at bit depths to 10: edge segments go in pairs, the two segments of four luma lines that make one
    /// edge of eight lines (two rows of a vertical edge, two columns of a horizontal one), whose decisions and filtering run on
    /// the eight lines at once, a vertical edge transposed. For 4:2:0 the chroma lines of the pair, four of Cb and four of Cr,
    /// filter together the same way.
    /// </summary>
    private static void DeblockPairs(HevcPicture picture, bool vertical, int y4Start, int y4End)
    {
        HevcSps sps = picture.Sps;
        HevcPps pps = picture.Pps;
        int w4 = picture.Width4;
        ushort[] plane = picture.Planes[0];
        int stride = picture.Widths[0];
        int bitDepth = sps.BitDepthLuma;
        bool chromaPairs = sps.ChromaArrayType == 1 && sps.BitDepthChroma <= 10;
        byte[] flags = picture.Flags;
        byte edge = vertical ? HevcPicture.EdgeLeft : HevcPicture.EdgeTop;
        int second = vertical ? w4 : 1;
        for (int y4 = vertical ? y4Start : Math.Max(y4Start, 2); y4 < y4End; y4 += 2)
        {
            for (int x4 = vertical ? 2 : 0; x4 < w4; x4 += 2)
            {
                bool pair = vertical ? y4 + 1 < y4End : x4 + 1 < w4;
                int q = (y4 * w4) + x4;
                if (((flags[q] | (pair ? flags[q + second] : 0)) & edge) == 0)
                {
                    continue;
                }

                int bs0 = Segment(picture, x4, y4, vertical, out int qp0, out HevcSliceHeader? slice0, out bool noP0, out bool noQ0);
                int bs1 = 0, qp1 = 0;
                HevcSliceHeader? slice1 = null;
                bool noP1 = false, noQ1 = false;
                if (pair)
                {
                    bs1 = Segment(picture, vertical ? x4 : x4 + 1, vertical ? y4 + 1 : y4, vertical, out qp1, out slice1, out noP1, out noQ1);
                }

                if (bs0 == 0 && bs1 == 0)
                {
                    continue;
                }

                int x = x4 << 2, y = y4 << 2;
                if (!pair)
                {
                    LumaEdge(picture, x, y, vertical, qp0, bs0, slice0!, noP0, noQ0);
                }
                else
                {
                    int beta0 = 0, tc0 = 0, beta1 = 0, tc1 = 0;
                    if (bs0 != 0)
                    {
                        beta0 = Beta[Math.Clamp(qp0 + (slice0!.BetaOffsetDiv2 << 1), 0, 51)] << (bitDepth - 8);
                        tc0 = Tc[Math.Clamp(qp0 + (2 * (bs0 - 1)) + (slice0.TcOffsetDiv2 << 1), 0, 53)] << (bitDepth - 8);
                    }

                    if (bs1 != 0)
                    {
                        beta1 = Beta[Math.Clamp(qp1 + (slice1!.BetaOffsetDiv2 << 1), 0, 51)] << (bitDepth - 8);
                        tc1 = Tc[Math.Clamp(qp1 + (2 * (bs1 - 1)) + (slice1.TcOffsetDiv2 << 1), 0, 53)] << (bitDepth - 8);
                    }

                    // With tC 0 the normal filter changes nothing and the strong one is never chosen; with beta 0 neither runs.
                    if ((tc0 != 0 && beta0 != 0) || (tc1 != 0 && beta1 != 0))
                    {
                        LumaPair(plane, stride, (y * stride) + x, vertical, beta0, beta1, tc0, tc1, Keep(noP0, noQ0), Keep(noP1, noQ1), (1 << bitDepth) - 1);
                    }
                }

                if (sps.ChromaArrayType == 0 || (bs0 != 2 && bs1 != 2))
                {
                    continue;
                }

                int sub = vertical ? sps.SubWidthC : sps.SubHeightC;
                if (((vertical ? x : y) / sub) % 8 != 0)
                {
                    continue;
                }

                if (pair && chromaPairs)
                {
                    ChromaPair(picture, x >> 1, y >> 1, vertical, bs0 == 2 ? ChromaTc(sps, pps.CbQpOffset, qp0, slice0!) : 0,
                        bs1 == 2 ? ChromaTc(sps, pps.CbQpOffset, qp1, slice1!) : 0, bs0 == 2 ? ChromaTc(sps, pps.CrQpOffset, qp0, slice0!) : 0,
                        bs1 == 2 ? ChromaTc(sps, pps.CrQpOffset, qp1, slice1!) : 0, Keep(noP0, noQ0), Keep(noP1, noQ1));
                    continue;
                }

                for (int k = 0; k < 2; k++)
                {
                    if ((k == 0 ? bs0 : bs1) != 2)
                    {
                        continue;
                    }

                    int xk = vertical || k == 0 ? x : x + 4, yk = !vertical || k == 0 ? y : y + 4;
                    int qp = k == 0 ? qp0 : qp1;
                    HevcSliceHeader slice = (k == 0 ? slice0 : slice1)!;
                    bool noP = k == 0 ? noP0 : noP1, noQ = k == 0 ? noQ0 : noQ1;
                    ChromaEdge(picture, 1, xk, yk, vertical, qp + pps.CbQpOffset, slice, noP, noQ);
                    ChromaEdge(picture, 2, xk, yk, vertical, qp + pps.CrQpOffset, slice, noP, noQ);
                }
            }
        }
    }

    /// <summary>
    /// The bS of the luma edge segment left of or above 4x4 block (x4, y4), 0 when it is not filtered, with QpL (8-286) and
    /// whether each side keeps its samples.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Segment(HevcPicture picture, int x4, int y4, bool vertical, out int qpL, out HevcSliceHeader? slice, out bool noP, out bool noQ)
    {
        qpL = 0;
        slice = null;
        noP = noQ = false;
        int q = (y4 * picture.Width4) + x4;
        byte flags = picture.Flags[q];
        if ((flags & (vertical ? HevcPicture.EdgeLeft : HevcPicture.EdgeTop)) == 0 || !FilterEdge(picture, x4 << 2, y4 << 2, vertical, out HevcSliceHeader s))
        {
            return 0;
        }

        int p = vertical ? q - 1 : q - picture.Width4;
        int bs = BoundaryStrength(picture, p, q, (flags & (vertical ? HevcPicture.TransformLeft : HevcPicture.TransformTop)) != 0);
        if (bs != 0)
        {
            slice = s;
            qpL = (picture.QpY[q] + picture.QpY[p] + 1) >> 1;
            noP = NoFilter(picture, p);
            noQ = NoFilter(picture, q);
        }

        return bs;
    }

    /// <summary>tC of a chroma edge (8.7.2.5.5) with bS 2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChromaTc(HevcSps sps, int qpOffset, int qpL, HevcSliceHeader slice)
    {
        int qpi = qpL + qpOffset;
        int qpc = sps.ChromaArrayType == 1 ? HevcSliceDecoder.ChromaQp(qpi) : Math.Min(qpi, 51);
        return Tc[Math.Clamp(qpc + 2 + (slice.TcOffsetDiv2 << 1), 0, 53)] << (sps.BitDepthChroma - 8);
    }

    /// <summary>A segment's pcm/bypass flags as a pair of lane masks: bit 0 keeps the p side, bit 1 the q side.</summary>
    private static int Keep(bool noP, bool noQ) => (noP ? 1 : 0) | (noQ ? 2 : 0);

    /// <summary>Lanes 0 to 3 all <paramref name="first"/> and lanes 4 to 7 all <paramref name="second"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Halves(int first, int second) =>
        Vector128.Create((ushort)first * 0x0001000100010001L, (ushort)second * 0x0001000100010001L).AsInt16();

    /// <summary>A lane mask of the first segment's four lanes and the second's.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Halves(bool first, bool second) => Vector128.Create(first ? -1L : 0, second ? -1L : 0).AsInt16();

    /// <summary>Each half of the vector set to the sum of its lanes 0 and 3: dpq0 + dpq3 of each segment (8-301 and on).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Ends(Vector128<short> v)
    {
        Vector128<byte> first = Vector128.Create((byte)0, 1, 0, 1, 0, 1, 0, 1, 8, 9, 8, 9, 8, 9, 8, 9);
        Vector128<byte> last = Vector128.Create((byte)6, 7, 6, 7, 6, 7, 6, 7, 14, 15, 14, 15, 14, 15, 14, 15);
        return Ssse3.Shuffle(v.AsByte(), first).AsInt16() + Ssse3.Shuffle(v.AsByte(), last).AsInt16();
    }

    /// <summary>
    /// Decides on and filters a luma edge of eight lines (8.7.2.5.3, 8.7.2.5.6, 8.7.2.5.7), two segments with their own beta,
    /// tC and pcm/bypass flags: the lines are vectors of p3 to q3, rows above and below a horizontal edge, columns of a
    /// transposed 8x8 block across a vertical one. A segment's decisions read its lines 0 and 3.
    /// </summary>
    private static void LumaPair(ushort[] plane, int stride, int at, bool vertical, int beta0, int beta1, int tc0, int tc1, int keep0, int keep1, int max)
    {
        ref short s = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(plane));
        Vector128<short> p3, p2, p1, p0, q0, q1, q2, q3;
        nuint row = (nuint)stride;
        if (vertical)
        {
            ref short r = ref Unsafe.Add(ref s, at - 4);
            Transpose(Vector128.LoadUnsafe(ref r), Vector128.LoadUnsafe(ref r, row), Vector128.LoadUnsafe(ref r, 2 * row), Vector128.LoadUnsafe(ref r, 3 * row),
                Vector128.LoadUnsafe(ref r, 4 * row), Vector128.LoadUnsafe(ref r, 5 * row), Vector128.LoadUnsafe(ref r, 6 * row), Vector128.LoadUnsafe(ref r, 7 * row),
                out p3, out p2, out p1, out p0, out q0, out q1, out q2, out q3);
        }
        else
        {
            ref short r = ref Unsafe.Add(ref s, at - (4 * stride));
            p3 = Vector128.LoadUnsafe(ref r);
            p2 = Vector128.LoadUnsafe(ref r, row);
            p1 = Vector128.LoadUnsafe(ref r, 2 * row);
            p0 = Vector128.LoadUnsafe(ref r, 3 * row);
            q0 = Vector128.LoadUnsafe(ref r, 4 * row);
            q1 = Vector128.LoadUnsafe(ref r, 5 * row);
            q2 = Vector128.LoadUnsafe(ref r, 6 * row);
            q3 = Vector128.LoadUnsafe(ref r, 7 * row);
        }

        Vector128<short> beta = Halves(beta0, beta1), tc = Halves(tc0, tc1);
        Vector128<short> dp = Vector128.Abs(p2 - p1 - p1 + p0), dq = Vector128.Abs(q2 - q1 - q1 + q0), dpq = dp + dq;
        Vector128<short> filter = Vector128.LessThan(Ends(dpq), beta);
        if (filter == Vector128<short>.Zero)
        {
            return;
        }

        // dSam of each line (8.7.2.5.6), and dE 2 where lines 0 and 3 both have it.
        Vector128<short> sam = Vector128.LessThan(dpq + dpq, beta >> 2)
            & Vector128.LessThan(Vector128.Abs(p3 - p0) + Vector128.Abs(q0 - q3), beta >> 3)
            & Vector128.LessThan(Vector128.Abs(p0 - q0), ((tc << 2) + tc + Vector128.Create((short)1)) >> 1);
        Vector128<short> strong = filter & Vector128.Equals(Ends(sam), Vector128.Create((short)-2));
        Vector128<short> side = (beta + (beta >> 1)) >> 3;
        Vector128<short> keepP = Halves((keep0 & 1) != 0, (keep1 & 1) != 0), keepQ = Halves((keep0 & 2) != 0, (keep1 & 2) != 0);
        Vector128<short> zero = Vector128<short>.Zero, vmax = Vector128.Create((short)max);
        Vector128<short> np0 = p0, np1 = p1, np2 = p2, nq0 = q0, nq1 = q1, nq2 = q2;
        if (strong != zero)
        {
            Vector128<short> tc2 = tc + tc, four = Vector128.Create((short)4), two = Vector128.Create((short)2);
            Vector128<short> sum = p1 + p0 + q0, qsum = p0 + q0 + q1;
            Vector128<short> mp = Vector128.AndNot(strong, keepP), mq = Vector128.AndNot(strong, keepQ);
            np0 = Vector128.ConditionalSelect(mp, Clip((p2 + sum + sum + q1 + four) >> 3, p0 - tc2, p0 + tc2), np0);
            np1 = Vector128.ConditionalSelect(mp, Clip((p2 + sum + two) >> 2, p1 - tc2, p1 + tc2), np1);
            np2 = Vector128.ConditionalSelect(mp, Clip((p3 + p3 + p2 + p2 + p2 + sum + four) >> 3, p2 - tc2, p2 + tc2), np2);
            nq0 = Vector128.ConditionalSelect(mq, Clip((q2 + qsum + qsum + p1 + four) >> 3, q0 - tc2, q0 + tc2), nq0);
            nq1 = Vector128.ConditionalSelect(mq, Clip((q2 + qsum + two) >> 2, q1 - tc2, q1 + tc2), nq1);
            nq2 = Vector128.ConditionalSelect(mq, Clip((q3 + q3 + q2 + q2 + q2 + qsum + four) >> 3, q2 - tc2, q2 + tc2), nq2);
        }

        Vector128<short> normal = Vector128.AndNot(filter, strong);
        if (normal != zero)
        {
            // delta = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4, used where |delta| < 10 tC.
            Vector128<short> d0 = q0 - p0, d1 = q1 - p1;
            Vector128<short> delta = ((d0 + (d0 << 3)) - (d1 + d1 + d1) + Vector128.Create((short)8)) >> 4;
            normal &= Vector128.LessThan(Vector128.Abs(delta), (tc << 3) + tc + tc);
            delta = Clip(delta, -tc, tc);
            Vector128<short> half = tc >> 1, one = Vector128.Create((short)1);
            Vector128<short> deltaP = Clip((((p2 + p0 + one) >> 1) - p1 + delta) >> 1, -half, half);
            Vector128<short> deltaQ = Clip((((q2 + q0 + one) >> 1) - q1 - delta) >> 1, -half, half);
            Vector128<short> mp = Vector128.AndNot(normal, keepP), mq = Vector128.AndNot(normal, keepQ);
            np0 = Vector128.ConditionalSelect(mp, Clip(p0 + delta, zero, vmax), np0);
            nq0 = Vector128.ConditionalSelect(mq, Clip(q0 - delta, zero, vmax), nq0);
            np1 = Vector128.ConditionalSelect(mp & Vector128.LessThan(Ends(dp), side), Clip(p1 + deltaP, zero, vmax), np1);
            nq1 = Vector128.ConditionalSelect(mq & Vector128.LessThan(Ends(dq), side), Clip(q1 + deltaQ, zero, vmax), nq1);
        }

        if (vertical)
        {
            Transpose(p3, np2, np1, np0, nq0, nq1, nq2, q3, out Vector128<short> r0, out Vector128<short> r1, out Vector128<short> r2, out Vector128<short> r3,
                out Vector128<short> r4, out Vector128<short> r5, out Vector128<short> r6, out Vector128<short> r7);
            ref short r = ref Unsafe.Add(ref s, at - 4);
            r0.StoreUnsafe(ref r);
            r1.StoreUnsafe(ref r, row);
            r2.StoreUnsafe(ref r, 2 * row);
            r3.StoreUnsafe(ref r, 3 * row);
            r4.StoreUnsafe(ref r, 4 * row);
            r5.StoreUnsafe(ref r, 5 * row);
            r6.StoreUnsafe(ref r, 6 * row);
            r7.StoreUnsafe(ref r, 7 * row);
        }
        else
        {
            ref short r = ref Unsafe.Add(ref s, at - (3 * stride));
            np2.StoreUnsafe(ref r);
            np1.StoreUnsafe(ref r, row);
            np0.StoreUnsafe(ref r, 2 * row);
            nq0.StoreUnsafe(ref r, 3 * row);
            nq1.StoreUnsafe(ref r, 4 * row);
            nq2.StoreUnsafe(ref r, 5 * row);
        }
    }

    /// <summary>
    /// Filters the 4:2:0 chroma lines of a luma edge pair (8.7.2.5.5) at chroma sample (xc, yc): lanes 0 to 3 are four lines of
    /// Cb, lanes 4 to 7 the same four lines of Cr, each pair of lines with its segment's tC (0 leaves them as they are).
    /// </summary>
    private static void ChromaPair(HevcPicture picture, int xc, int yc, bool vertical, int tcCb0, int tcCb1, int tcCr0, int tcCr1, int keep0, int keep1)
    {
        int stride = picture.Widths[1];
        ref short cb = ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(picture.Planes[1]), (yc * stride) + xc));
        ref short cr = ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(picture.Planes[2]), (yc * stride) + xc));
        Vector128<short> p1, p0, q0, q1;
        if (vertical)
        {
            Transpose(Row(ref cb, -2, 0, stride), Row(ref cb, -2, 1, stride), Row(ref cb, -2, 2, stride), Row(ref cb, -2, 3, stride),
                Row(ref cr, -2, 0, stride), Row(ref cr, -2, 1, stride), Row(ref cr, -2, 2, stride), Row(ref cr, -2, 3, stride),
                out p1, out p0, out q0, out q1, out _, out _, out _, out _);
        }
        else
        {
            p1 = Lines(ref cb, ref cr, -2 * stride);
            p0 = Lines(ref cb, ref cr, -stride);
            q0 = Lines(ref cb, ref cr, 0);
            q1 = Lines(ref cb, ref cr, stride);
        }

        Vector128<short> tc = Vector128.Create(Twice(tcCb0), Twice(tcCb1), Twice(tcCr0), Twice(tcCr1)).AsInt16();
        Vector128<short> keepP = Vector128.Create(Lanes((keep0 & 1) != 0), Lanes((keep1 & 1) != 0), Lanes((keep0 & 1) != 0), Lanes((keep1 & 1) != 0)).AsInt16();
        Vector128<short> keepQ = Vector128.Create(Lanes((keep0 & 2) != 0), Lanes((keep1 & 2) != 0), Lanes((keep0 & 2) != 0), Lanes((keep1 & 2) != 0)).AsInt16();
        Vector128<short> vmax = Vector128.Create((short)((1 << picture.Sps.BitDepthChroma) - 1));
        Vector128<short> delta = Clip(((((q0 - p0) << 2) + p1 - q1) + Vector128.Create((short)4)) >> 3, -tc, tc);
        p0 = Vector128.ConditionalSelect(keepP, p0, Clip(p0 + delta, Vector128<short>.Zero, vmax));
        q0 = Vector128.ConditionalSelect(keepQ, q0, Clip(q0 - delta, Vector128<short>.Zero, vmax));
        if (vertical)
        {
            Transpose(p1, p0, q0, q1, default, default, default, default, out Vector128<short> r0, out Vector128<short> r1, out Vector128<short> r2,
                out Vector128<short> r3, out Vector128<short> r4, out Vector128<short> r5, out Vector128<short> r6, out Vector128<short> r7);
            StoreRow(ref cb, -2, 0, stride, r0);
            StoreRow(ref cb, -2, 1, stride, r1);
            StoreRow(ref cb, -2, 2, stride, r2);
            StoreRow(ref cb, -2, 3, stride, r3);
            StoreRow(ref cr, -2, 0, stride, r4);
            StoreRow(ref cr, -2, 1, stride, r5);
            StoreRow(ref cr, -2, 2, stride, r6);
            StoreRow(ref cr, -2, 3, stride, r7);
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref cb, -stride)), p0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref cr, -stride)), p0.AsUInt64().GetElement(1));
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref cb), q0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref cr), q0.AsUInt64().GetElement(1));
        }
    }

    private static int Twice(int value) => (value & 0xFFFF) | (value << 16);

    private static int Lanes(bool set) => set ? -1 : 0;

    /// <summary>Four samples of one row, from column offset <paramref name="dx"/>, in the low half of a vector.</summary>
    private static Vector128<short> Row(ref short origin, int dx, int y, int stride) =>
        Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref origin, (y * stride) + dx)))).AsInt16();

    private static void StoreRow(ref short origin, int dx, int y, int stride, Vector128<short> v) =>
        Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref origin, (y * stride) + dx)), v.AsUInt64().ToScalar());

    /// <summary>Four samples of Cb and the same four of Cr at an offset.</summary>
    private static Vector128<short> Lines(ref short cb, ref short cr, int offset) =>
        Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref cb, offset))),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref cr, offset)))).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clip(Vector128<short> v, Vector128<short> low, Vector128<short> high) => Vector128.Min(Vector128.Max(v, low), high);

    /// <summary>Transposes an 8x8 block of shorts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(Vector128<short> r0, Vector128<short> r1, Vector128<short> r2, Vector128<short> r3, Vector128<short> r4, Vector128<short> r5,
        Vector128<short> r6, Vector128<short> r7, out Vector128<short> c0, out Vector128<short> c1, out Vector128<short> c2, out Vector128<short> c3,
        out Vector128<short> c4, out Vector128<short> c5, out Vector128<short> c6, out Vector128<short> c7)
    {
        Vector128<int> a0 = Sse2.UnpackLow(r0, r1).AsInt32(), a1 = Sse2.UnpackHigh(r0, r1).AsInt32();
        Vector128<int> a2 = Sse2.UnpackLow(r2, r3).AsInt32(), a3 = Sse2.UnpackHigh(r2, r3).AsInt32();
        Vector128<int> a4 = Sse2.UnpackLow(r4, r5).AsInt32(), a5 = Sse2.UnpackHigh(r4, r5).AsInt32();
        Vector128<int> a6 = Sse2.UnpackLow(r6, r7).AsInt32(), a7 = Sse2.UnpackHigh(r6, r7).AsInt32();
        Vector128<long> b0 = Sse2.UnpackLow(a0, a2).AsInt64(), b1 = Sse2.UnpackHigh(a0, a2).AsInt64();
        Vector128<long> b2 = Sse2.UnpackLow(a1, a3).AsInt64(), b3 = Sse2.UnpackHigh(a1, a3).AsInt64();
        Vector128<long> b4 = Sse2.UnpackLow(a4, a6).AsInt64(), b5 = Sse2.UnpackHigh(a4, a6).AsInt64();
        Vector128<long> b6 = Sse2.UnpackLow(a5, a7).AsInt64(), b7 = Sse2.UnpackHigh(a5, a7).AsInt64();
        c0 = Sse2.UnpackLow(b0, b4).AsInt16();
        c1 = Sse2.UnpackHigh(b0, b4).AsInt16();
        c2 = Sse2.UnpackLow(b1, b5).AsInt16();
        c3 = Sse2.UnpackHigh(b1, b5).AsInt16();
        c4 = Sse2.UnpackLow(b2, b6).AsInt16();
        c5 = Sse2.UnpackHigh(b2, b6).AsInt16();
        c6 = Sse2.UnpackLow(b3, b7).AsInt16();
        c7 = Sse2.UnpackHigh(b3, b7).AsInt16();
    }

    /// <summary>
    /// Gets filterEdgeFlag for the edge left of or above (x, y) (8.7.2.3): not at the picture boundary, not across a tile
    /// boundary or a slice boundary that does not allow it, and not when the slice of q0 turns the filter off.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FilterEdge(HevcPicture picture, int x, int y, bool vertical, out HevcSliceHeader slice)
    {
        HevcSps sps = picture.Sps;
        int widthCtbs = sps.WidthInCtbs;
        int ctbQ = ((y >> sps.CtbLog2Size) * widthCtbs) + (x >> sps.CtbLog2Size);
        slice = picture.CtbSliceHeader[ctbQ]!;
        if (slice is null || slice.DeblockingDisabled)
        {
            return false;
        }

        // Only an edge on a CTB boundary can be a slice or tile boundary.
        if (((vertical ? x : y) & ((1 << sps.CtbLog2Size) - 1)) == 0)
        {
            int xP = vertical ? x - 1 : x, yP = vertical ? y : y - 1;
            int ctbP = ((yP >> sps.CtbLog2Size) * widthCtbs) + (xP >> sps.CtbLog2Size);
            HevcLayout layout = picture.Layout;
            if (!picture.Pps.LoopFilterAcrossTiles && layout.TileId[layout.RsToTs[ctbP]] != layout.TileId[layout.RsToTs[ctbQ]])
            {
                return false;
            }

            if (picture.CtbSlice[ctbP] != picture.CtbSlice[ctbQ] && !slice.LoopFilterAcrossSlices)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// bS of an edge segment (8.7.2.4) between 4x4 blocks p and q: 2 with an intra side, 1 at a transform block edge with luma
    /// coefficients on a side, 1 when the sides predict from other pictures or with vectors a sample or more apart, else 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BoundaryStrength(HevcPicture picture, int p, int q, bool transformEdge)
    {
        int fp = picture.PredFlags[p], fq = picture.PredFlags[q];
        if (fp == 0 || fq == 0)
        {
            return 2;
        }

        if (transformEdge && ((picture.Flags[p] | picture.Flags[q]) & HevcPicture.Coded) != 0)
        {
            return 1;
        }

        int countP = (fp & 1) + (fp >> 1), countQ = (fq & 1) + (fq >> 1);
        if (countP != countQ)
        {
            return 1;
        }

        int[] id0 = picture.RefId[0], id1 = picture.RefId[1], mv0 = picture.Mv[0], mv1 = picture.Mv[1];
        if (countP == 1)
        {
            int refP = fp == 1 ? id0[p] : id1[p], refQ = fq == 1 ? id0[q] : id1[q];
            int mvP = fp == 1 ? mv0[p] : mv1[p], mvQ = fq == 1 ? mv0[q] : mv1[q];
            return refP != refQ || Far(mvP, mvQ) ? 1 : 0;
        }

        int p0 = id0[p], p1 = id1[p], q0 = id0[q], q1 = id1[q];
        if (!((p0 == q0 && p1 == q1) || (p0 == q1 && p1 == q0)))
        {
            return 1;
        }

        if (p0 != p1)
        {
            // Two different pictures: compare the vectors of the same picture.
            return p0 == q0 ? (Far(mv0[p], mv0[q]) || Far(mv1[p], mv1[q]) ? 1 : 0) : (Far(mv0[p], mv1[q]) || Far(mv1[p], mv0[q]) ? 1 : 0);
        }

        // Both vectors of each side name one picture: bS 1 only when neither pairing matches.
        return (Far(mv0[p], mv0[q]) || Far(mv1[p], mv1[q])) && (Far(mv0[p], mv1[q]) || Far(mv1[p], mv0[q])) ? 1 : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Far(int a, int b) => Math.Abs((short)a - (short)b) >= 4 || Math.Abs((a >> 16) - (b >> 16)) >= 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NoFilter(HevcPicture picture, int block)
    {
        byte flags = picture.Flags[block];
        return (flags & HevcPicture.Bypass) != 0 || ((flags & HevcPicture.Pcm) != 0 && picture.Sps.PcmLoopFilterDisabled);
    }

    private static void LumaEdge(HevcPicture picture, int x, int y, bool vertical, int qpL, int bs, HevcSliceHeader slice, bool noP, bool noQ)
    {
        HevcSps sps = picture.Sps;
        int bitDepth = sps.BitDepthLuma;
        int beta = Beta[Math.Clamp(qpL + (slice.BetaOffsetDiv2 << 1), 0, 51)] << (bitDepth - 8);
        int tc = Tc[Math.Clamp(qpL + (2 * (bs - 1)) + (slice.TcOffsetDiv2 << 1), 0, 53)] << (bitDepth - 8);
        if (tc == 0 && beta == 0)
        {
            return;
        }

        ushort[] plane = picture.Planes[0];
        int stride = picture.Widths[0];

        // Along the edge (step) and across it (offset).
        int step = vertical ? stride : 1, offset = vertical ? 1 : stride;
        int at = (y * stride) + x;
        int max = (1 << bitDepth) - 1;
        int line0 = at, line3 = at + (3 * step);
        int dp0 = Math.Abs(plane[line0 - (3 * offset)] - (2 * plane[line0 - (2 * offset)]) + plane[line0 - offset]);
        int dp3 = Math.Abs(plane[line3 - (3 * offset)] - (2 * plane[line3 - (2 * offset)]) + plane[line3 - offset]);
        int dq0 = Math.Abs(plane[line0 + (2 * offset)] - (2 * plane[line0 + offset]) + plane[line0]);
        int dq3 = Math.Abs(plane[line3 + (2 * offset)] - (2 * plane[line3 + offset]) + plane[line3]);
        int dpq0 = dp0 + dq0, dpq3 = dp3 + dq3, dp = dp0 + dp3, dq = dq0 + dq3;
        int d = dpq0 + dpq3;
        if (d >= beta)
        {
            return;
        }

        bool strong = Strong(plane, line0, offset, 2 * dpq0, beta, tc) && Strong(plane, line3, offset, 2 * dpq3, beta, tc);
        bool dEp = dp < ((beta + (beta >> 1)) >> 3), dEq = dq < ((beta + (beta >> 1)) >> 3);
        for (int k = 0; k < 4; k++)
        {
            int i = at + (k * step);
            int p0 = plane[i - offset], p1 = plane[i - (2 * offset)], p2 = plane[i - (3 * offset)], p3 = plane[i - (4 * offset)];
            int q0 = plane[i], q1 = plane[i + offset], q2 = plane[i + (2 * offset)], q3 = plane[i + (3 * offset)];
            if (strong)
            {
                int tc2 = 2 * tc;
                if (!noP)
                {
                    plane[i - offset] = (ushort)Math.Clamp((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3, p0 - tc2, p0 + tc2);
                    plane[i - (2 * offset)] = (ushort)Math.Clamp((p2 + p1 + p0 + q0 + 2) >> 2, p1 - tc2, p1 + tc2);
                    plane[i - (3 * offset)] = (ushort)Math.Clamp(((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3, p2 - tc2, p2 + tc2);
                }

                if (!noQ)
                {
                    plane[i] = (ushort)Math.Clamp((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3, q0 - tc2, q0 + tc2);
                    plane[i + offset] = (ushort)Math.Clamp((p0 + q0 + q1 + q2 + 2) >> 2, q1 - tc2, q1 + tc2);
                    plane[i + (2 * offset)] = (ushort)Math.Clamp((p0 + q0 + q1 + (3 * q2) + (2 * q3) + 4) >> 3, q2 - tc2, q2 + tc2);
                }
            }
            else
            {
                int delta = ((9 * (q0 - p0)) - (3 * (q1 - p1)) + 8) >> 4;
                if (Math.Abs(delta) >= tc * 10)
                {
                    continue;
                }

                delta = Math.Clamp(delta, -tc, tc);
                if (!noP)
                {
                    plane[i - offset] = (ushort)Math.Clamp(p0 + delta, 0, max);
                    if (dEp)
                    {
                        int deltaP = Math.Clamp(((((p2 + p0 + 1) >> 1) - p1) + delta) >> 1, -(tc >> 1), tc >> 1);
                        plane[i - (2 * offset)] = (ushort)Math.Clamp(p1 + deltaP, 0, max);
                    }
                }

                if (!noQ)
                {
                    plane[i] = (ushort)Math.Clamp(q0 - delta, 0, max);
                    if (dEq)
                    {
                        int deltaQ = Math.Clamp(((((q2 + q0 + 1) >> 1) - q1) - delta) >> 1, -(tc >> 1), tc >> 1);
                        plane[i + offset] = (ushort)Math.Clamp(q1 + deltaQ, 0, max);
                    }
                }
            }
        }
    }

    private static bool Strong(ushort[] plane, int i, int offset, int dpq, int beta, int tc)
    {
        int p0 = plane[i - offset], p3 = plane[i - (4 * offset)], q0 = plane[i], q3 = plane[i + (3 * offset)];
        return dpq < (beta >> 2) && Math.Abs(p3 - p0) + Math.Abs(q0 - q3) < (beta >> 3) && Math.Abs(p0 - q0) < ((5 * tc) + 1) >> 1;
    }

    private static void ChromaEdge(HevcPicture picture, int c, int x, int y, bool vertical, int qpi, HevcSliceHeader slice, bool noP, bool noQ)
    {
        HevcSps sps = picture.Sps;
        int bitDepth = sps.BitDepthChroma;
        int qpc = sps.ChromaArrayType == 1 ? HevcSliceDecoder.ChromaQp(qpi) : Math.Min(qpi, 51);
        int tc = Tc[Math.Clamp(qpc + 2 + (slice.TcOffsetDiv2 << 1), 0, 53)] << (bitDepth - 8);
        if (tc == 0)
        {
            return;
        }

        ushort[] plane = picture.Planes[c];
        int stride = picture.Widths[c];
        int xc = x / sps.SubWidthC, yc = y / sps.SubHeightC;
        int lines = 4 / (vertical ? sps.SubHeightC : sps.SubWidthC);
        int step = vertical ? stride : 1, offset = vertical ? 1 : stride;
        int at = (yc * stride) + xc;
        int max = (1 << bitDepth) - 1;
        for (int k = 0; k < lines; k++)
        {
            int i = at + (k * step);
            int p0 = plane[i - offset], p1 = plane[i - (2 * offset)], q0 = plane[i], q1 = plane[i + offset];
            int delta = Math.Clamp(((((q0 - p0) << 2) + p1 - q1) + 4) >> 3, -tc, tc);
            if (!noP)
            {
                plane[i - offset] = (ushort)Math.Clamp(p0 + delta, 0, max);
            }

            if (!noQ)
            {
                plane[i] = (ushort)Math.Clamp(q0 - delta, 0, max);
            }
        }
    }

    // ---------------------------------------------------------------- SAO (8.7.3)

    private static readonly int[][] EdgeOffsets = [[-1, 0, 1, 0], [0, -1, 0, 1], [-1, -1, 1, 1], [1, -1, -1, 1]];

    private static void Sao(HevcPicture picture, int parallelism, CancellationToken cancellationToken)
    {
        HevcSps sps = picture.Sps;
        if (!sps.SaoEnabled)
        {
            return;
        }

        int ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
        bool any = false;
        for (int i = 0; i < ctbs && !any; i++)
        {
            HevcSliceHeader? slice = picture.CtbSliceHeader[i];
            any = slice is not null && (slice.SaoLuma || slice.SaoChroma);
        }

        if (!any)
        {
            return;
        }

        int components = picture.Planes.Length;
        for (int c = 0; c < components; c++)
        {
            // Each CTB reads the deblocked samples and writes only its own.
            ushort[] plane = picture.Planes[c];
            ushort[] deblocked = ArrayPool<ushort>.Shared.Rent(plane.Length);
            plane.AsSpan().CopyTo(deblocked);
            int component = c;
            try
            {
                HevcThreads.For(sps.HeightInCtbs, parallelism, row =>
                {
                    for (int ctb = row * sps.WidthInCtbs; ctb < (row + 1) * sps.WidthInCtbs; ctb++)
                    {
                        HevcSliceHeader? slice = picture.CtbSliceHeader[ctb];
                        if (slice is null || (component == 0 ? !slice.SaoLuma : !slice.SaoChroma))
                        {
                            continue;
                        }

                        HevcSaoParameters sao = picture.Sao[ctb];
                        if (sao.Type(component) != 0)
                        {
                            SaoCtb(picture, component, ctb, sao, deblocked);
                        }
                    }
                }, cancellationToken);
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(deblocked);
            }
        }
    }

    private static void SaoCtb(HevcPicture picture, int c, int ctb, HevcSaoParameters sao, ushort[] deblocked)
    {
        HevcSps sps = picture.Sps;
        int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
        int ctbSize = 1 << sps.CtbLog2Size;
        int width = picture.Widths[c], height = picture.Heights[c];
        int x0 = ((ctb % sps.WidthInCtbs) * ctbSize) / sx, y0 = ((ctb / sps.WidthInCtbs) * ctbSize) / sy;
        int w = Math.Min(ctbSize / sx, width - x0), h = Math.Min(ctbSize / sy, height - y0);
        int bitDepth = c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
        int max = (1 << bitDepth) - 1;
        ushort[] plane = picture.Planes[c];
        HevcSaoParameters.OffsetSet offsets = sao.Offsets(c);
        bool checkFlags = sps.PcmLoopFilterDisabled || picture.Pps.TransquantBypassEnabled;
        if (HevcInter.UseVectors && w >= 8)
        {
            if (sao.Type(c) == 1)
            {
                BandVectors(plane, deblocked, width, x0, y0, w, h, sao.Class(c), bitDepth, offsets);
            }
            else
            {
                EdgeVectors(picture, plane, deblocked, width, height, x0, y0, w, h, sao.Class(c), sx, sy, max, offsets, ctb);
            }

            if (checkFlags)
            {
                // PCM samples with the loop filter off and transquant bypass samples keep their deblocked values.
                for (int y = y0; y < y0 + h; y++)
                {
                    for (int x = x0; x < x0 + w; x++)
                    {
                        if (Skip(picture, x * sx, y * sy))
                        {
                            plane[(y * width) + x] = deblocked[(y * width) + x];
                        }
                    }
                }
            }

            return;
        }

        if (sao.Type(c) == 1)
        {
            Span<int> table = stackalloc int[32];
            table.Clear();
            int position = sao.Class(c);
            for (int k = 0; k < 4; k++)
            {
                table[(k + position) & 31] = k + 1;
            }

            int shift = bitDepth - 5;
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    if (checkFlags && Skip(picture, x * sx, y * sy))
                    {
                        continue;
                    }

                    int i = (y * width) + x;
                    int band = table[deblocked[i] >> shift];
                    if (band != 0)
                    {
                        plane[i] = (ushort)Math.Clamp(deblocked[i] + offsets[band - 1], 0, max);
                    }
                }
            }

            return;
        }

        int[] direction = EdgeOffsets[sao.Class(c)];
        HevcLayout layout = picture.Layout;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int ax = x + direction[0], ay = y + direction[1], bx = x + direction[2], by = y + direction[3];
                if (ax < 0 || ay < 0 || bx < 0 || by < 0 || ax >= width || bx >= width || ay >= height || by >= height)
                {
                    continue;
                }

                if (checkFlags && Skip(picture, x * sx, y * sy))
                {
                    continue;
                }

                if (!NeighbourAllowed(picture, layout, x * sx, y * sy, ax * sx, ay * sy) || !NeighbourAllowed(picture, layout, x * sx, y * sy, bx * sx, by * sy))
                {
                    continue;
                }

                int i = (y * width) + x;
                int value = deblocked[i];
                int edge = 2 + Math.Sign(value - deblocked[(ay * width) + ax]) + Math.Sign(value - deblocked[(by * width) + bx]);
                int index = edge switch { 0 => 1, 1 => 2, 2 => 0, _ => edge };
                if (index != 0)
                {
                    plane[i] = (ushort)Math.Clamp(value + offsets[index - 1], 0, max);
                }
            }
        }
    }

    /// <summary>
    /// Band offset (8.7.3.2) with AVX2 for a CTB at least 8 samples wide: the band k = (sample &gt;&gt; (bitDepth - 5)) -
    /// sao_band_position, mod 32, picks offset k when k is below 4. A row whose width is not a multiple of the vector ends with a
    /// vector that overlaps the one before, which only writes the same values again, as every vector reads the deblocked copy.
    /// </summary>
    private static void BandVectors(ushort[] plane, ushort[] deblocked, int width, int x0, int y0, int w, int h, int position, int bitDepth,
        HevcSaoParameters.OffsetSet offsets)
    {
        Vector256<short> table = Vector256.Create(Vector128.Create((short)offsets[0], (short)offsets[1], (short)offsets[2], (short)offsets[3], 0, 0, 0, 0));
        Vector256<short> pos = Vector256.Create((short)position), mask31 = Vector256.Create((short)31), four = Vector256.Create((short)4);
        Vector256<short> max = Vector256.Create((short)((1 << bitDepth) - 1)), pick = Vector256.Create((short)0x0202), high = Vector256.Create((short)0x0100);
        int shift = bitDepth - 5;
        ref short src = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(deblocked));
        ref short dst = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(plane));
        for (int y = y0; y < y0 + h; y++)
        {
            int row = (y * width) + x0;
            if (w >= 16)
            {
                for (int x = 0; x < w; x += 16)
                {
                    int at = row + Math.Min(x, w - 16);
                    Vector256<short> v = Vector256.LoadUnsafe(ref src, (nuint)at);
                    Vector256<short> k = ((v >> shift) - pos) & mask31;
                    Vector256<short> offset = Avx2.Shuffle(table.AsByte(), ((k * pick) + high).AsByte()).AsInt16() & Vector256.LessThan(k, four);
                    Vector256.Min(Vector256.Max(v + offset, Vector256<short>.Zero), max).StoreUnsafe(ref dst, (nuint)at);
                }
            }
            else
            {
                for (int x = 0; x < w; x += 8)
                {
                    int at = row + Math.Min(x, w - 8);
                    Vector128<short> v = Vector128.LoadUnsafe(ref src, (nuint)at);
                    Vector128<short> k = ((v >> shift) - pos.GetLower()) & mask31.GetLower();
                    Vector128<short> offset = Ssse3.Shuffle(table.GetLower().AsByte(), ((k * pick.GetLower()) + high.GetLower()).AsByte()).AsInt16()
                        & Vector128.LessThan(k, four.GetLower());
                    Vector128.Min(Vector128.Max(v + offset, Vector128<short>.Zero), max.GetLower()).StoreUnsafe(ref dst, (nuint)at);
                }
            }
        }
    }

    /// <summary>
    /// Edge offset (8.7.3.2) with AVX2 for a CTB at least 8 samples wide. Samples whose neighbours lie outside the picture are
    /// left out; at the edges of the CTB, samples whose neighbour CTB may not be used get their deblocked values back afterwards.
    /// </summary>
    private static void EdgeVectors(HevcPicture picture, ushort[] plane, ushort[] deblocked, int width, int height, int x0, int y0, int w, int h,
        int edgeClass, int sx, int sy, int max, HevcSaoParameters.OffsetSet offsets, int ctb)
    {
        int[] direction = EdgeOffsets[edgeClass];
        int xs = x0, xe = x0 + w, ys = y0, ye = y0 + h;
        if (edgeClass != 1)
        {
            xs = Math.Max(xs, 1);
            xe = Math.Min(xe, width - 1);
        }

        if (edgeClass != 0)
        {
            ys = Math.Max(ys, 1);
            ye = Math.Min(ye, height - 1);
        }

        int n = xe - xs;
        int offA = (direction[1] * width) + direction[0], offB = (direction[3] * width) + direction[2];
        if (n >= 8)
        {
            // edgeIdx 2 + Sign(v - a) + Sign(v - b) is 0 to 4 and picks offset 1, 2, none, 3 or 4: a table of shorts for a byte
            // shuffle, whose index pair for edgeIdx e is 2e, 2e + 1. Sign(v - a) is (a > v) - (v > a) with comparisons of -1 or 0.
            Vector256<short> table = Vector256.Create(Vector128.Create((short)offsets[0], (short)offsets[1], 0, (short)offsets[2], (short)offsets[3], 0, 0, 0));
            Vector256<short> vmax = Vector256.Create((short)max), pick = Vector256.Create((short)0x0202), two = Vector256.Create((short)0x0504);
            ref short src = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(deblocked));
            ref short dst = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(plane));
            for (int y = ys; y < ye; y++)
            {
                int row = (y * width) + xs;
                if (n >= 16)
                {
                    for (int x = 0; x < n; x += 16)
                    {
                        int at = row + Math.Min(x, n - 16);
                        Vector256<short> v = Vector256.LoadUnsafe(ref src, (nuint)at);
                        Vector256<short> a = Vector256.LoadUnsafe(ref src, (nuint)(at + offA)), b = Vector256.LoadUnsafe(ref src, (nuint)(at + offB));
                        Vector256<short> e = Vector256.GreaterThan(a, v) - Vector256.GreaterThan(v, a) + Vector256.GreaterThan(b, v) - Vector256.GreaterThan(v, b);
                        Vector256<short> offset = Avx2.Shuffle(table.AsByte(), ((e * pick) + two).AsByte()).AsInt16();
                        Vector256.Min(Vector256.Max(v + offset, Vector256<short>.Zero), vmax).StoreUnsafe(ref dst, (nuint)at);
                    }
                }
                else
                {
                    for (int x = 0; x < n; x += 8)
                    {
                        int at = row + Math.Min(x, n - 8);
                        Vector128<short> v = Vector128.LoadUnsafe(ref src, (nuint)at);
                        Vector128<short> a = Vector128.LoadUnsafe(ref src, (nuint)(at + offA)), b = Vector128.LoadUnsafe(ref src, (nuint)(at + offB));
                        Vector128<short> e = Vector128.GreaterThan(a, v) - Vector128.GreaterThan(v, a) + Vector128.GreaterThan(b, v) - Vector128.GreaterThan(v, b);
                        Vector128<short> offset = Ssse3.Shuffle(table.GetLower().AsByte(), ((e * pick.GetLower()) + two.GetLower()).AsByte()).AsInt16();
                        Vector128.Min(Vector128.Max(v + offset, Vector128<short>.Zero), vmax.GetLower()).StoreUnsafe(ref dst, (nuint)at);
                    }
                }
            }
        }
        else
        {
            for (int y = ys; y < ye; y++)
            {
                for (int x = xs; x < xe; x++)
                {
                    int i = (y * width) + x;
                    int value = deblocked[i];
                    int edge = 2 + Math.Sign(value - deblocked[i + offA]) + Math.Sign(value - deblocked[i + offB]);
                    int index = edge switch { 0 => 1, 1 => 2, 2 => 0, _ => edge };
                    if (index != 0)
                    {
                        plane[i] = (ushort)Math.Clamp(value + offsets[index - 1], 0, max);
                    }
                }
            }
        }

        if (NeighbourCtbsAllowed(picture, ctb))
        {
            return;
        }

        // Only the first and last rows and columns have neighbours in other CTBs.
        HevcLayout layout = picture.Layout;
        for (int y = ys; y < ye; y++)
        {
            bool edgeRow = y == y0 || y == y0 + h - 1;
            for (int x = xs; x < xe; x++)
            {
                if (!edgeRow && x != xs && x != xe - 1)
                {
                    continue;
                }

                int ax = x + direction[0], ay = y + direction[1], bx = x + direction[2], by = y + direction[3];
                if (!NeighbourAllowed(picture, layout, x * sx, y * sy, ax * sx, ay * sy) || !NeighbourAllowed(picture, layout, x * sx, y * sy, bx * sx, by * sy))
                {
                    plane[(y * width) + x] = deblocked[(y * width) + x];
                }
            }
        }
    }

    /// <summary>
    /// Gets whether SAO may use every neighbouring CTB of a CTB: <see cref="NeighbourAllowed"/> at CTB granularity, where of two
    /// CTBs the later in z-scan order is the later in tile scan.
    /// </summary>
    private static bool NeighbourCtbsAllowed(HevcPicture picture, int ctb)
    {
        HevcSps sps = picture.Sps;
        HevcLayout layout = picture.Layout;
        int wc = sps.WidthInCtbs, xc = ctb % wc, yc = ctb / wc;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int xn = xc + dx, yn = yc + dy;
                if ((dx == 0 && dy == 0) || xn < 0 || yn < 0 || xn >= wc || yn >= sps.HeightInCtbs)
                {
                    continue;
                }

                int n = (yn * wc) + xn;
                if (picture.CtbSlice[ctb] != picture.CtbSlice[n])
                {
                    HevcSliceHeader? later = layout.RsToTs[n] < layout.RsToTs[ctb] ? picture.CtbSliceHeader[ctb] : picture.CtbSliceHeader[n];
                    if (later is null || !later.LoopFilterAcrossSlices)
                    {
                        return false;
                    }
                }

                if (!picture.Pps.LoopFilterAcrossTiles && layout.TileId[layout.RsToTs[ctb]] != layout.TileId[layout.RsToTs[n]])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Gets whether SAO leaves a sample unchanged: PCM with the loop filter off, or transquant bypass.</summary>
    private static bool Skip(HevcPicture picture, int xLuma, int yLuma)
    {
        byte flags = picture.Flags[((yLuma >> 2) * picture.Width4) + (xLuma >> 2)];
        return (flags & HevcPicture.Bypass) != 0 || ((flags & HevcPicture.Pcm) != 0 && picture.Sps.PcmLoopFilterDisabled);
    }

    /// <summary>
    /// Gets whether an edge offset neighbour may be used (8.7.3.2): not across a slice boundary the later slice forbids, nor
    /// across a tile boundary when the PPS forbids it.
    /// </summary>
    private static bool NeighbourAllowed(HevcPicture picture, HevcLayout layout, int xC, int yC, int xN, int yN)
    {
        HevcSps sps = picture.Sps;
        int ctbC = ((yC >> sps.CtbLog2Size) * sps.WidthInCtbs) + (xC >> sps.CtbLog2Size);
        int ctbN = ((yN >> sps.CtbLog2Size) * sps.WidthInCtbs) + (xN >> sps.CtbLog2Size);
        if (ctbC == ctbN)
        {
            return true;
        }

        if (picture.CtbSlice[ctbC] != picture.CtbSlice[ctbN])
        {
            int zC = picture.ZScan[((yC >> 2) * picture.Width4) + (xC >> 2)], zN = picture.ZScan[((yN >> 2) * picture.Width4) + (xN >> 2)];
            HevcSliceHeader later = zN < zC ? picture.CtbSliceHeader[ctbC]! : picture.CtbSliceHeader[ctbN]!;
            if (later is null || !later.LoopFilterAcrossSlices)
            {
                return false;
            }
        }

        return picture.Pps.LoopFilterAcrossTiles || layout.TileId[layout.RsToTs[ctbC]] == layout.TileId[layout.RsToTs[ctbN]];
    }
}
