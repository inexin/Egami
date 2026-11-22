using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Heic.Hevc;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// The motion search of one prediction unit in one reference picture: the start candidates (AMVP and merge vectors, zero and the
/// vectors the caller found before) by SAD, a hexagon of radius 2 to a whole-sample minimum and a square around it, then two
/// steps of half samples by SAD and one of quarter samples by SATD (as Egami's H.264 search does); every point costs its distortion plus lambda times the bits of its vector
/// difference from the better AMVP predictor. Costs are in 1/256 of a SAD or SATD unit.
/// </summary>
[SkipLocalsInit]
internal sealed class HevcMotionSearch
{
    private static readonly (int X, int Y)[] Hexagon = [(-2, 0), (-1, -2), (1, -2), (2, 0), (1, 2), (-1, 2)];
    private static readonly (int X, int Y)[] Square = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];
    private static readonly (int X, int Y)[] Diamond = [(0, -1), (-1, 0), (1, 0), (0, 1)];

    private readonly ushort[] _block = new ushort[64 * 64];
    private ushort[] _source = [];
    private int _sourceStride;
    private int _x;
    private int _y;
    private int _w;
    private int _h;
    private HevcReference _reference = null!;
    private int _mvp0;
    private int _mvp1;
    private int _lambda;

    /// <summary>Gets or sets the search range in whole samples around the best start.</summary>
    internal int Range { get; set; } = 57;

    /// <summary>Gets or sets whether the half and quarter steps try all eight neighbours rather than four.</summary>
    internal bool SquareSubpel { get; set; }

    /// <summary>Gets or sets the source luma as bytes (8-bit luma), for the SAD against references' byte planes, or null.</summary>
    internal byte[]? Source8 { get; set; }

    /// <summary>Gets or sets whether the quarter-sample step runs (else the search stops at half samples).</summary>
    internal bool QuarterSamples { get; set; } = true;

    /// <summary>The bits of one motion vector difference component (mvd_coding: greater-than flags, Exp-Golomb of order 1, sign).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ComponentBits(int v)
    {
        uint a = (uint)Math.Abs(v);
        return a == 0 ? 1 : a == 1 ? 3 : 5 + (2 * BitOperations.Log2(((a - 2) >> 1) + 1));
    }

    /// <summary>The bits of a packed vector difference.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VectorBits(int mvx, int mvy) => ComponentBits(mvx) + ComponentBits(mvy);

    /// <summary>
    /// Searches the unit (x, y, w, h) of the source luma in <paramref name="reference"/>; gives the best quarter-sample vector,
    /// its cost (SATD plus the vector's rate) and the AMVP predictor flag that codes it cheaper.
    /// </summary>
    /// <param name="source">The source luma and its stride.</param>
    /// <param name="sourceStride">The source's row length.</param>
    /// <param name="x">The unit's left column.</param>
    /// <param name="y">The unit's top row.</param>
    /// <param name="w">The unit's width.</param>
    /// <param name="h">The unit's height.</param>
    /// <param name="reference">The reference picture.</param>
    /// <param name="mvps">The two AMVP predictors.</param>
    /// <param name="starts">More start vectors (merge candidates of this reference, earlier results).</param>
    /// <param name="lambda">Lambda for SAD and SATD costs, in 1/256.</param>
    /// <param name="startsOnly">Whether the whole-sample search stops at the best start (the sub-sample steps still run).</param>
    internal (int Mv, long Cost, int MvpFlag) Search(ushort[] source, int sourceStride, int x, int y, int w, int h, HevcReference reference,
        (int Mvp0, int Mvp1) mvps, ReadOnlySpan<int> starts, int lambda, bool startsOnly = false)
    {
        (_source, _sourceStride, _x, _y, _w, _h, _reference) = (source, sourceStride, x, y, w, h, reference);
        (_mvp0, _mvp1, _lambda) = (mvps.Mvp0, mvps.Mvp1, lambda);
        HevcEncodeStats.Add(HevcEncodeStat.BlockSearches);

        // The start: the best of the candidates rounded to whole samples, each tried once.
        int bestX = 0, bestY = 0;
        long best = long.MaxValue;
        Span<int> tried = stackalloc int[starts.Length + 3];
        int count = 0;
        TryStart(Whole(mvps.Mvp0), tried, ref count, ref bestX, ref bestY, ref best);
        TryStart(Whole(mvps.Mvp1), tried, ref count, ref bestX, ref bestY, ref best);
        TryStart((0, 0), tried, ref count, ref bestX, ref bestY, ref best);
        foreach (int start in starts)
        {
            TryStart(Whole(start), tried, ref count, ref bestX, ref bestY, ref best);
        }

        // Hexagon steps while the centre is not the best, within the range of the start: all six points first, then the three
        // a step in the last direction has not tried.
        int startX = bestX, startY = bestY, direction = -1;
        for (int step = 0; step < (startsOnly ? 0 : Range); step++)
        {
            int cx = bestX, cy = bestY, moved = -1;
            for (int t = 0; t < (direction < 0 ? 6 : 3); t++)
            {
                int d = direction < 0 ? t : (direction + 5 + t) % 6;
                int px = cx + Hexagon[d].X, py = cy + Hexagon[d].Y;
                if (Math.Abs(px - startX) <= Range && Math.Abs(py - startY) <= Range && Try((px, py), ref bestX, ref bestY, ref best))
                {
                    moved = d;
                }
            }

            if (moved < 0)
            {
                break;
            }

            direction = moved;
        }

        int bx = bestX, by = bestY;
        foreach ((int dx, int dy) in startsOnly ? [] : Square)
        {
            Try((bx + dx, by + dy), ref bestX, ref bestY, ref best);
        }

        // Half samples by SAD straight from their planes (two steps), then one step of quarter samples by SATD.
        int mvx = bestX * 4, mvy = bestY * 4;
        ReadOnlySpan<(int X, int Y)> pattern = SquareSubpel ? Square : Diamond;
        for (int step = 0; step < 2; step++)
        {
            int cx = mvx, cy = mvy;
            foreach ((int dx, int dy) in pattern)
            {
                int px = cx + (dx * 2), py = cy + (dy * 2);
                if (Inside(px >> 2, py >> 2))
                {
                    long c = HalfCost(px, py);
                    if (c < best)
                    {
                        (best, mvx, mvy) = (c, px, py);
                    }
                }
            }

            if (mvx == cx && mvy == cy)
            {
                break;
            }
        }

        long cost = SubpelCost(mvx, mvy);
        if (QuarterSamples)
        {
            int cx = mvx, cy = mvy;
            foreach ((int dx, int dy) in pattern)
            {
                int px = cx + dx, py = cy + dy;
                if (Inside(px >> 2, py >> 2))
                {
                    long c = SubpelCost(px, py);
                    if (c < cost)
                    {
                        (cost, mvx, mvy) = (c, px, py);
                    }
                }
            }
        }

        int mv = HevcMotionPredictor.Pack(mvx, mvy);
        int flag = VectorBits(mvx - (short)_mvp1, mvy - (_mvp1 >> 16)) < VectorBits(mvx - (short)_mvp0, mvy - (_mvp0 >> 16)) ? 1 : 0;
        return (mv, cost, flag);
    }

    private static (int X, int Y) Whole(int mv) => (((short)mv + 2) >> 2, ((mv >> 16) + 2) >> 2);

    private bool Inside(int fx, int fy)
    {
        int margin = HevcReference.Pad - 4;
        return _x + fx >= -margin && _y + fy >= -margin && _x + fx + _w <= _reference.Width + margin && _y + fy + _h <= _reference.Height + margin;
    }

    private void TryStart((int X, int Y) p, Span<int> tried, ref int count, ref int bestX, ref int bestY, ref long best)
    {
        int packed = HevcMotionPredictor.Pack(p.X, p.Y);
        if (!tried[..count].Contains(packed))
        {
            tried[count++] = packed;
            Try(p, ref bestX, ref bestY, ref best);
        }
    }

    /// <summary>Costs a whole-sample vector; gives whether it is the best so far.</summary>
    private bool Try((int X, int Y) p, ref int bestX, ref int bestY, ref long best)
    {
        if (!Inside(p.X, p.Y))
        {
            return false;
        }

        HevcEncodeStats.Add(HevcEncodeStat.IntegerCosts);
        long sad = PlaneSad(0, _reference.At(_x + p.X, _y + p.Y));
        long cost = (sad << 8) + (_lambda * (long)MvBits(p.X * 4, p.Y * 4));
        if (cost < best)
        {
            (best, bestX, bestY) = (cost, p.X, p.Y);
            return true;
        }

        return false;
    }

    /// <summary>The SAD cost of a whole or half-sample vector, read from its plane in place.</summary>
    private long HalfCost(int mvx, int mvy)
    {
        HevcEncodeStats.Add(HevcEncodeStat.IntegerCosts);
        int hx = ((4 * _x) + mvx) >> 1, hy = ((4 * _y) + mvy) >> 1;
        long sad = PlaneSad((hx & 1) + (2 * (hy & 1)), _reference.At(hx >> 1, hy >> 1));
        return (sad << 8) + (_lambda * (long)MvBits(mvx, mvy));
    }

    /// <summary>The SAD of the unit against one of the reference's half-sample planes at an index: in bytes where both sides have them.</summary>
    private long PlaneSad(int plane, int at)
    {
        int atSource = (_y * _sourceStride) + _x;
        return Source8 is { } source8 && _reference.Planes8 is { } planes8 && (_w & 7) == 0
            ? Sad8(source8, atSource, _sourceStride, planes8[plane], at, _reference.Stride, _w, _h)
            : Sad(_source, atSource, _sourceStride, _reference.Planes[plane], at, _reference.Stride, _w, _h);
    }

    private long SubpelCost(int mvx, int mvy)
    {
        HevcEncodeStats.Add(HevcEncodeStat.SubpelCosts);
        _reference.Block(_x, _y, mvx, mvy, _w, _h, _block, 64);
        long satd = Satd(_source, (_y * _sourceStride) + _x, _sourceStride, _block, 0, 64, _w, _h, Source8 is not null);
        return (satd << 8) + (_lambda * (long)MvBits(mvx, mvy));
    }

    private int MvBits(int mvx, int mvy) =>
        Math.Min(VectorBits(mvx - (short)_mvp0, mvy - (_mvp0 >> 16)), VectorBits(mvx - (short)_mvp1, mvy - (_mvp1 >> 16)));

    /// <summary>The sum of absolute differences of two w by h blocks.</summary>
    /// <remarks>Rows of 16 or more widen into one 32-bit sum, summed once at the end; rows of 8 go two to a 256-bit step.</remarks>
    internal static long Sad(ushort[] a, int atA, int strideA, ushort[] b, int atB, int strideB, int w, int h)
    {
        ref ushort a0 = ref MemoryMarshal.GetArrayDataReference(a);
        ref ushort b0 = ref MemoryMarshal.GetArrayDataReference(b);
        if (Vector256.IsHardwareAccelerated && (w & 15) == 0)
        {
            Vector256<uint> total = Vector256<uint>.Zero;
            for (int r = 0; r < h; r++)
            {
                ref ushort pa = ref Unsafe.Add(ref a0, atA + (r * strideA));
                ref ushort pb = ref Unsafe.Add(ref b0, atB + (r * strideB));
                Vector256<ushort> acc = Vector256<ushort>.Zero;
                for (int k = 0; k < w; k += 16)
                {
                    Vector256<ushort> va = Vector256.LoadUnsafe(ref pa, (nuint)k), vb = Vector256.LoadUnsafe(ref pb, (nuint)k);
                    acc += Vector256.Max(va, vb) - Vector256.Min(va, vb);
                }

                (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(acc);
                total += lo + hi;
            }

            return Vector256.Sum(total);
        }

        if (Vector256.IsHardwareAccelerated && w == 8 && (h & 1) == 0)
        {
            Vector256<uint> total = Vector256<uint>.Zero;
            for (int r = 0; r < h; r += 2)
            {
                Vector256<ushort> va = Vector256.Create(Vector128.LoadUnsafe(ref a0, (nuint)(atA + (r * strideA))),
                    Vector128.LoadUnsafe(ref a0, (nuint)(atA + ((r + 1) * strideA))));
                Vector256<ushort> vb = Vector256.Create(Vector128.LoadUnsafe(ref b0, (nuint)(atB + (r * strideB))),
                    Vector128.LoadUnsafe(ref b0, (nuint)(atB + ((r + 1) * strideB))));
                (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(Vector256.Max(va, vb) - Vector256.Min(va, vb));
                total += lo + hi;
            }

            return Vector256.Sum(total);
        }

        long sum = 0;
        for (int r = 0; r < h; r++)
        {
            ref ushort pa = ref Unsafe.Add(ref a0, atA + (r * strideA));
            ref ushort pb = ref Unsafe.Add(ref b0, atB + (r * strideB));
            int k = 0;
            if (w >= 16)
            {
                Vector256<ushort> acc = Vector256<ushort>.Zero;
                for (; k + 16 <= w; k += 16)
                {
                    Vector256<ushort> va = Vector256.LoadUnsafe(ref pa, (nuint)k), vb = Vector256.LoadUnsafe(ref pb, (nuint)k);
                    acc += Vector256.Max(va, vb) - Vector256.Min(va, vb);
                }

                (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(acc);
                sum += Vector256.Sum(lo + hi);
            }

            if (k + 8 <= w)
            {
                Vector128<ushort> va = Vector128.LoadUnsafe(ref pa, (nuint)k), vb = Vector128.LoadUnsafe(ref pb, (nuint)k);
                (Vector128<uint> lo, Vector128<uint> hi) = Vector128.Widen(Vector128.Max(va, vb) - Vector128.Min(va, vb));
                sum += Vector128.Sum(lo + hi);
                k += 8;
            }

            for (; k < w; k++)
            {
                sum += Math.Abs(Unsafe.Add(ref pa, k) - Unsafe.Add(ref pb, k));
            }
        }

        return sum;
    }

    /// <summary>The sum of absolute differences of two w by h blocks of bytes, w a multiple of 8 (PSADBW where there is SSE2).</summary>
    internal static long Sad8(byte[] a, int atA, int strideA, byte[] b, int atB, int strideB, int w, int h)
    {
        ref byte a0 = ref MemoryMarshal.GetArrayDataReference(a);
        ref byte b0 = ref MemoryMarshal.GetArrayDataReference(b);
        if (Avx2.IsSupported && (w & 31) == 0)
        {
            Vector256<ulong> total = Vector256<ulong>.Zero;
            for (int r = 0; r < h; r++)
            {
                ref byte pa = ref Unsafe.Add(ref a0, atA + (r * strideA));
                ref byte pb = ref Unsafe.Add(ref b0, atB + (r * strideB));
                for (int k = 0; k < w; k += 32)
                {
                    total += Avx2.SumAbsoluteDifferences(Vector256.LoadUnsafe(ref pa, (nuint)k), Vector256.LoadUnsafe(ref pb, (nuint)k)).AsUInt64();
                }
            }

            return (long)Vector256.Sum(total);
        }

        if (Sse2.IsSupported)
        {
            Vector128<ulong> total = Vector128<ulong>.Zero;
            if (w == 8)
            {
                int r = 0;
                for (; r + 2 <= h; r += 2)
                {
                    Vector128<byte> va = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a0, atA + (r * strideA))),
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a0, atA + ((r + 1) * strideA)))).AsByte();
                    Vector128<byte> vb = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, atB + (r * strideB))),
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, atB + ((r + 1) * strideB)))).AsByte();
                    total += Sse2.SumAbsoluteDifferences(va, vb).AsUInt64();
                }

                if (r < h)
                {
                    Vector128<byte> va = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a0, atA + (r * strideA)))).AsByte();
                    Vector128<byte> vb = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, atB + (r * strideB)))).AsByte();
                    total += Sse2.SumAbsoluteDifferences(va, vb).AsUInt64();
                }

                return (long)Vector128.Sum(total);
            }

            for (int r = 0; r < h; r++)
            {
                ref byte pa = ref Unsafe.Add(ref a0, atA + (r * strideA));
                ref byte pb = ref Unsafe.Add(ref b0, atB + (r * strideB));
                int k = 0;
                for (; k + 16 <= w; k += 16)
                {
                    total += Sse2.SumAbsoluteDifferences(Vector128.LoadUnsafe(ref pa, (nuint)k), Vector128.LoadUnsafe(ref pb, (nuint)k)).AsUInt64();
                }

                if (k < w)
                {
                    Vector128<byte> va = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pa, k))).AsByte();
                    Vector128<byte> vb = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pb, k))).AsByte();
                    total += Sse2.SumAbsoluteDifferences(va, vb).AsUInt64();
                }
            }

            return (long)Vector128.Sum(total);
        }

        long sum = 0;
        for (int r = 0; r < h; r++)
        {
            for (int k = 0; k < w; k++)
            {
                sum += Math.Abs(Unsafe.Add(ref a0, atA + (r * strideA) + k) - Unsafe.Add(ref b0, atB + (r * strideB) + k));
            }
        }

        return sum;
    }

    /// <summary>The SATD of two w by h blocks: 8x8 Hadamards where both sides are multiples of 8, else 4x4 ones.</summary>
    /// <param name="a">The first block's plane.</param>
    /// <param name="atA">The first block's index.</param>
    /// <param name="strideA">The first plane's row length.</param>
    /// <param name="b">The second block's plane.</param>
    /// <param name="atB">The second block's index.</param>
    /// <param name="strideB">The second plane's row length.</param>
    /// <param name="w">The width.</param>
    /// <param name="h">The height.</param>
    /// <param name="narrow">Whether the samples have 8 bits, so two 8x8 transforms fit one vector of 16-bit lanes.</param>
    internal static long Satd(ushort[] a, int atA, int strideA, ushort[] b, int atB, int strideB, int w, int h, bool narrow = false)
    {
        int n = (w & 7) == 0 && (h & 7) == 0 ? 8 : 4;
        if (n == 8 && narrow && Avx2.IsSupported && ((w >> 3) * (h >> 3) & 1) == 0)
        {
            // Pairs of 8x8 blocks in raster order: side by side when the width allows, else one above the other.
            long total = 0;
            int blocksX = w >> 3, count = blocksX * (h >> 3);
            for (int i = 0; i < count; i += 2)
            {
                int x0 = (i % blocksX) << 3, y0 = (i / blocksX) << 3, x1 = ((i + 1) % blocksX) << 3, y1 = ((i + 1) / blocksX) << 3;
                total += Hadamard8Pair(a, atA + (y0 * strideA) + x0, atA + (y1 * strideA) + x1, strideA, b, atB + (y0 * strideB) + x0,
                    atB + (y1 * strideB) + x1, strideB);
            }

            return total;
        }

        long sum = 0;
        for (int y = 0; y < h; y += n)
        {
            for (int x = 0; x < w; x += n)
            {
                sum += HevcSatd.Compute(a, atA + (y * strideA) + x, strideA, b, atB + (y * strideB) + x, strideB, n);
            }
        }

        return sum;
    }

    /// <summary>
    /// The SATD of two 8x8 blocks of 8-bit samples, each rounded as <see cref="HevcSatd"/> rounds it, summed: block 0 in the low
    /// 128 bits of each row vector and block 1 in the high, in 16-bit lanes (a coefficient's magnitude is at most 255 · 64).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Hadamard8Pair(ushort[] a, int atA0, int atA1, int sa, ushort[] b, int atB0, int atB1, int sb)
    {
        ref ushort pa = ref MemoryMarshal.GetArrayDataReference(a);
        ref ushort pb = ref MemoryMarshal.GetArrayDataReference(b);
        Vector256<short> r0 = PairRow(ref pa, ref pb, atA0, atA1, atB0, atB1), r1 = PairRow(ref pa, ref pb, atA0 + sa, atA1 + sa, atB0 + sb, atB1 + sb);
        Vector256<short> r2 = PairRow(ref pa, ref pb, atA0 + (2 * sa), atA1 + (2 * sa), atB0 + (2 * sb), atB1 + (2 * sb));
        Vector256<short> r3 = PairRow(ref pa, ref pb, atA0 + (3 * sa), atA1 + (3 * sa), atB0 + (3 * sb), atB1 + (3 * sb));
        Vector256<short> r4 = PairRow(ref pa, ref pb, atA0 + (4 * sa), atA1 + (4 * sa), atB0 + (4 * sb), atB1 + (4 * sb));
        Vector256<short> r5 = PairRow(ref pa, ref pb, atA0 + (5 * sa), atA1 + (5 * sa), atB0 + (5 * sb), atB1 + (5 * sb));
        Vector256<short> r6 = PairRow(ref pa, ref pb, atA0 + (6 * sa), atA1 + (6 * sa), atB0 + (6 * sb), atB1 + (6 * sb));
        Vector256<short> r7 = PairRow(ref pa, ref pb, atA0 + (7 * sa), atA1 + (7 * sa), atB0 + (7 * sb), atB1 + (7 * sb));

        // Vertical butterflies: rows i and i ^ 1, i ^ 2, i ^ 4.
        (r0, r1) = (r0 + r1, r0 - r1);
        (r2, r3) = (r2 + r3, r2 - r3);
        (r4, r5) = (r4 + r5, r4 - r5);
        (r6, r7) = (r6 + r7, r6 - r7);
        (r0, r2) = (r0 + r2, r0 - r2);
        (r1, r3) = (r1 + r3, r1 - r3);
        (r4, r6) = (r4 + r6, r4 - r6);
        (r5, r7) = (r5 + r7, r5 - r7);
        (r0, r4) = (r0 + r4, r0 - r4);
        (r1, r5) = (r1 + r5, r1 - r5);
        (r2, r6) = (r2 + r6, r2 - r6);
        (r3, r7) = (r3 + r7, r3 - r7);

        Vector256<short> ones = Vector256<short>.One;
        Vector256<int> sum = Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r0)).AsInt16(), ones) + Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r1)).AsInt16(), ones)
            + Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r2)).AsInt16(), ones) + Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r3)).AsInt16(), ones)
            + Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r4)).AsInt16(), ones) + Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r5)).AsInt16(), ones)
            + Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r6)).AsInt16(), ones) + Avx2.MultiplyAddAdjacent(Avx2.Abs(Horizontal16(r7)).AsInt16(), ones);
        long s0 = Vector128.Sum(sum.GetLower()), s1 = Vector128.Sum(sum.GetUpper());
        return ((s0 + 2) >> 2) + ((s1 + 2) >> 2);
    }

    /// <summary>A row of two 8x8 blocks' differences: block 0's in the low half, block 1's in the high.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> PairRow(ref ushort pa, ref ushort pb, int a0, int a1, int b0, int b1) =>
        (Vector256.Create(Vector128.LoadUnsafe(ref pa, (nuint)a0), Vector128.LoadUnsafe(ref pa, (nuint)a1))
        - Vector256.Create(Vector128.LoadUnsafe(ref pb, (nuint)b0), Vector128.LoadUnsafe(ref pb, (nuint)b1))).AsInt16();

    private static readonly Vector256<byte> SwapPairs = Vector256.Create((byte)2, 3, 0, 1, 6, 7, 4, 5, 10, 11, 8, 9, 14, 15, 12, 13, 2, 3, 0, 1, 6, 7, 4, 5, 10, 11, 8, 9, 14, 15, 12, 13);
    private static readonly Vector256<byte> SwapQuads = Vector256.Create((byte)4, 5, 6, 7, 0, 1, 2, 3, 12, 13, 14, 15, 8, 9, 10, 11, 4, 5, 6, 7, 0, 1, 2, 3, 12, 13, 14, 15, 8, 9, 10, 11);

    /// <summary>The 8-point Walsh-Hadamard transform of each 128-bit half's eight 16-bit lanes, in some order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Horizontal16(Vector256<short> v)
    {
        // Lanes i and i ^ 1, i ^ 2, i ^ 4 of each half: sums where the bit is clear, differences where it is set.
        Vector256<short> b = Avx2.Shuffle(v.AsByte(), SwapPairs).AsInt16();
        v = Avx2.Blend(v + b, b - v, 0b1010_1010);
        b = Avx2.Shuffle(v.AsByte(), SwapQuads).AsInt16();
        v = Avx2.Blend(v + b, b - v, 0b1100_1100);
        b = Avx2.Shuffle(v.AsInt32(), 0b01_00_11_10).AsInt16();
        return Avx2.Blend(v + b, b - v, 0b1111_0000);
    }
}
