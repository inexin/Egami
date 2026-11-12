using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Intra prediction for the encoder (8.3.1.2, 8.3.2.2, 8.3.3, 8.3.4), from neighbouring samples gathered once per block:
/// <c>top</c> holds p[x, −1] for x = −1 (the corner) to 2n − 1 at index x + 1, <c>left</c> p[−1, y] for y = −1 to n − 1 at
/// index y + 1. Missing top-right samples are the last top sample repeated, as the standard substitutes them.
/// </summary>
internal static class H264IntraPredict
{
    /// <summary>The Intra4x4 and Intra8x8 modes' needs: bit 0 top, bit 1 left, bit 2 the corner.</summary>
    internal static ReadOnlySpan<byte> NxNNeeds => [1, 2, 0, 1, 7, 7, 7, 1, 2];

    /// <summary>Whether an Intra4x4 or Intra8x8 mode can be used with the neighbours there are.</summary>
    internal static bool NxNAllowed(int mode, bool top, bool left, bool corner)
    {
        int need = NxNNeeds[mode];
        return ((need & 1) == 0 || top) && ((need & 2) == 0 || left) && ((need & 4) == 0 || corner);
    }

    /// <summary>Whether an Intra16x16 mode (0 vertical, 1 horizontal, 2 DC, 3 plane) can be used.</summary>
    internal static bool Allowed16x16(int mode, bool top, bool left, bool corner) => mode switch
    {
        0 => top,
        1 => left,
        2 => true,
        _ => top && left && corner,
    };

    /// <summary>Whether a chroma mode (0 DC, 1 horizontal, 2 vertical, 3 plane) can be used.</summary>
    internal static bool AllowedChroma(int mode, bool top, bool left, bool corner) => mode switch
    {
        0 => true,
        1 => left,
        2 => top,
        _ => top && left && corner,
    };

    /// <summary>
    /// Gathers a block's neighbours from a plane: n samples above and n more above right (repeated from the last when
    /// <paramref name="topRight"/> is false), n to the left, and the corner.
    /// </summary>
    internal static void Gather(byte[] plane, int at, int stride, int n, bool top, bool topRight, bool left, bool corner, Span<int> t, Span<int> l)
    {
        if (top)
        {
            int above = at - stride;
            for (int x = 0; x < n; x++)
            {
                t[x + 1] = plane[above + x];
            }

            for (int x = n; x < 2 * n; x++)
            {
                t[x + 1] = topRight ? plane[above + x] : t[n];
            }
        }

        if (left)
        {
            for (int y = 0; y < n; y++)
            {
                l[y + 1] = plane[at + (y * stride) - 1];
            }
        }

        int c = corner ? plane[at - stride - 1] : 0;
        t[0] = l[0] = c;
    }

    /// <summary>Filters the neighbours of an 8x8 block in place (8.3.2.2.1).</summary>
    internal static void Filter8x8(Span<int> t, Span<int> l, bool top, bool left, bool corner)
    {
        Span<int> ft = stackalloc int[17];
        Span<int> fl = stackalloc int[9];
        if (top)
        {
            ft[1] = corner ? (t[0] + (2 * t[1]) + t[2] + 2) >> 2 : ((3 * t[1]) + t[2] + 2) >> 2;
            for (int x = 1; x < 15; x++)
            {
                ft[x + 1] = (t[x] + (2 * t[x + 1]) + t[x + 2] + 2) >> 2;
            }

            ft[16] = (t[15] + (3 * t[16]) + 2) >> 2;
        }

        if (corner)
        {
            ft[0] = !top && left ? ((3 * t[0]) + l[1] + 2) >> 2
                : top && !left ? ((3 * t[0]) + t[1] + 2) >> 2
                : top && left ? (t[1] + (2 * t[0]) + l[1] + 2) >> 2
                : t[0];
        }

        if (left)
        {
            fl[1] = corner ? (l[0] + (2 * l[1]) + l[2] + 2) >> 2 : ((3 * l[1]) + l[2] + 2) >> 2;
            for (int y = 1; y < 7; y++)
            {
                fl[y + 1] = (l[y] + (2 * l[y + 1]) + l[y + 2] + 2) >> 2;
            }

            fl[8] = (l[7] + (3 * l[8]) + 2) >> 2;
        }

        if (top)
        {
            ft[1..17].CopyTo(t[1..17]);
        }

        if (left)
        {
            fl[1..9].CopyTo(l[1..9]);
        }

        if (corner)
        {
            t[0] = l[0] = ft[0];
        }
    }

    /// <summary>The size of the prediction lines <see cref="Edges"/> works out for <see cref="PredictNxN"/> and <see cref="SatdNxN"/>.</summary>
    internal const int EdgeSize = 200;

    // Where each Intra4x4 (first 36) and Intra8x8 mode's row starts in the lines, by (mode * n) + y.
    private static readonly byte[] RowsAt = MakeRowsAt();

    // RowsAt of the 4x4 modes in groups of four (mode 8 four times in the last), by (group * 16) + (y * 4) + the mode's place.
    private static readonly byte[] Group4 = MakeGroup4();

    // The lines' regions for blocks of n by n: F(k) for k from 1 − n to 2n − 1 and the last sample of diagonal down left at
    // 0 (F(k) at k + n − 1, so that sample stands where F(2n) would), A(k) for k from −n to 2n − 1 at 3n, then the top row, a
    // row of the DC, the rows of horizontal, the even and odd rows of vertical right, horizontal down and horizontal up.
    private static int AAt(int n) => 3 * n;

    private static int VAt(int n) => 6 * n;

    private static int DcAt(int n) => 7 * n;

    private static int HAt(int n) => 8 * n;

    private static int VrEvenAt(int n) => (8 * n) + (n * n);

    private static int VrOddAt(int n) => VrEvenAt(n) + (3 * n / 2) - 1;

    private static int HdAt(int n) => VrOddAt(n) + (3 * n / 2) - 1;

    private static int HuAt(int n) => HdAt(n) + (3 * n) - 2;

    // Where row y of an n by n block starts in the lines in each mode.
    private static int RowAt(int n, int mode, int y) => mode switch
    {
        0 => VAt(n),
        1 => HAt(n) + (y * n),
        2 => DcAt(n),

        // Diagonal down left: F(x + y + 2), the last sample in F(2n)'s place.
        3 => n + 1 + y,

        // Diagonal down right: F(x − y).
        4 => n - 1 - y,
        5 => ((y & 1) == 0 ? VrEvenAt(n) : VrOddAt(n)) + (n / 2) - 1 - (y >> 1),
        6 => HdAt(n) + (2 * (n - 1 - y)),

        // Vertical left: A(x + y / 2 + 1) on even rows, F(x + (y − 1) / 2 + 2) on odd ones.
        7 => (y & 1) == 0 ? AAt(n) + n + 1 + (y >> 1) : n + 1 + (y >> 1),
        _ => HuAt(n) + (2 * y),
    };

    private static byte[] MakeRowsAt()
    {
        byte[] at = new byte[(9 * 4) + (9 * 8)];
        for (int mode = 0; mode < 9; mode++)
        {
            for (int y = 0; y < 4; y++)
            {
                at[(mode * 4) + y] = (byte)RowAt(4, mode, y);
            }

            for (int y = 0; y < 8; y++)
            {
                at[36 + (mode * 8) + y] = (byte)RowAt(8, mode, y);
            }
        }

        return at;
    }

    private static byte[] MakeGroup4()
    {
        byte[] at = new byte[48];
        for (int g = 0; g < 3; g++)
        {
            for (int y = 0; y < 4; y++)
            {
                for (int j = 0; j < 4; j++)
                {
                    at[(g * 16) + (y * 4) + j] = (byte)RowAt(4, Math.Min((g * 4) + j, 8), y);
                }
            }
        }

        return at;
    }

    /// <summary>
    /// Works out once per n by n block (n 4 or 8) the lines every Intra4x4 and Intra8x8 mode copies its rows from (8.3.1.2,
    /// 8.3.2.2), so that each row of a prediction is n bytes from one place: along the edge e(k), which is p[k − 1, −1] for
    /// k ≥ 0 and p[−1, −k − 1] for k ≤ 0 (so e(0) is the corner), the 3-tap filter F(k) = (e(k − 1) + 2e(k) + e(k + 1) + 2)
    /// >> 2 and the 2-tap mean A(k) = (e(k) + e(k + 1) + 1) >> 1, laid out in the orders the directional modes read them, and
    /// the rows of vertical, horizontal and DC (from the neighbours there are). Samples a mode cannot use (a missing neighbour)
    /// go in too and are never read.
    /// </summary>
    [SkipLocalsInit]
    internal static void Edges(int n, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left, Span<byte> edges)
    {
        // The edge from e(−n) to e(2n), e(k) at index k + n, and room for the vectors to read past it.
        Span<short> e = stackalloc short[40];
        for (int k = 1; k <= n; k++)
        {
            e[n - k] = (short)l[k];
        }

        for (int k = 0; k <= 2 * n; k++)
        {
            e[n + k] = (short)t[k];
        }

        // F(k) at k + n − 1 and A(k) at 3n + k + n, eight at a time (each vector may write past its region into the next,
        // which is written after it).
        ref short at = ref MemoryMarshal.GetReference(e);
        ref byte dst = ref MemoryMarshal.GetReference(edges);
        int aAt = AAt(n);
        for (int i = 0; i < (3 * n) - 1; i += 8)
        {
            Vector128<short> e0 = Vector128.LoadUnsafe(ref at, (nuint)i), e1 = Vector128.LoadUnsafe(ref at, (nuint)(i + 1));
            Vector128<short> e2 = Vector128.LoadUnsafe(ref at, (nuint)(i + 2));
            Vector128<short> f = (e0 + (e1 << 1) + e2 + Vector128.Create((short)2)) >>> 2;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i), Vector128.Narrow(f, f).AsUInt64().ToScalar());
        }

        edges[(3 * n) - 1] = (byte)((t[(2 * n) - 1] + (3 * t[2 * n]) + 2) >> 2);
        for (int i = 0; i < 3 * n; i += 8)
        {
            Vector128<short> e0 = Vector128.LoadUnsafe(ref at, (nuint)i), e1 = Vector128.LoadUnsafe(ref at, (nuint)(i + 1));
            Vector128<short> a = (e0 + e1 + Vector128<short>.One) >>> 1;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, aAt + i), Vector128.Narrow(a, a).AsUInt64().ToScalar());
        }

        // The rows of vertical (t[1] to t[n]), DC and horizontal.
        Vector128<short> row = Vector128.LoadUnsafe(ref at, (nuint)(n + 1));
        ulong ones = 0x0101010101010101ul, mean = (ulong)Dc(n, t, l, top, left) * ones;
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, VAt(n)), Vector128.Narrow(row, row).AsUInt64().ToScalar());
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, DcAt(n)), mean);
        int h = HAt(n);
        for (int y = 0; y < n; y++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, h + (y * n)), (ulong)l[y + 1] * ones);
        }

        // Vertical right, horizontal down and the first part of horizontal up: samples of F and A in the orders they read them.
        ReadOnlySpan<byte> moves = n == 4 ? Moves4 : Moves8;
        for (int i = 0; i < moves.Length; i += 2)
        {
            Unsafe.Add(ref dst, moves[i]) = Unsafe.Add(ref dst, moves[i + 1]);
        }

        // The rest of horizontal up: (p[−1, n − 2] + 3p[−1, n − 1] + 2) >> 2, then p[−1, n − 1].
        int hu = HuAt(n) + (2 * n) - 3;
        edges[hu] = (byte)((l[n - 1] + (3 * l[n]) + 2) >> 2);
        edges.Slice(hu + 1, n).Fill((byte)l[n]);
    }

    // The (to, from) pairs of the lines' samples Edges copies from F and A, for 4x4 and 8x8 blocks.
    private static readonly byte[] Moves4 = MakeMoves(4);
    private static readonly byte[] Moves8 = MakeMoves(8);

    private static byte[] MakeMoves(int n)
    {
        List<byte> moves = [];
        void Move(int to, int from) => moves.AddRange([(byte)to, (byte)from]);

        // Vertical right, along d = x − y / 2 from 1 − n / 2: even rows F(2d + 1) left of the corner and A(d) from it, odd rows
        // F(2d) up to it and F(d) after (F(k) at f + k, A(k) at a + k).
        int half = (n / 2) - 1, even = VrEvenAt(n), odd = VrOddAt(n), f = n - 1, a = AAt(n) + n;
        for (int d = -half; d < n; d++)
        {
            Move(even + half + d, d < 0 ? f + (2 * d) + 1 : a + d);
            Move(odd + half + d, d <= 0 ? f + (2 * d) : f + d);
        }

        // Horizontal down: A(−k − 1) and F(−k) for k from n − 1 down to 0, then F(1) on; row y starts at k = y.
        int hd = HdAt(n);
        for (int k = 0; k < n; k++)
        {
            Move(hd + (2 * (n - 1 - k)), a - k - 1);
            Move(hd + (2 * (n - 1 - k)) + 1, f - k);
        }

        for (int i = 2 * n; i < (3 * n) - 2; i++)
        {
            Move(hd + i, f + i - (2 * n) + 1);
        }

        // Horizontal up, by zHU = x + 2y: A(−z / 2 − 2) for even z and F(−(z − 1) / 2 − 2) for odd z below 2n − 3.
        int hu = HuAt(n);
        for (int z = 0; z < (2 * n) - 3; z++)
        {
            Move(hu + z, (z & 1) == 0 ? a - (z >> 1) - 2 : f - (z >> 1) - 2);
        }

        return [.. moves];
    }

    /// <summary>
    /// Predicts an n by n block (n 4 or 8) in an Intra4x4 or Intra8x8 mode into <paramref name="dst"/> from the lines
    /// <see cref="Edges"/> worked out for it.
    /// </summary>
    internal static void PredictNxN(byte[] dst, int dstStride, int n, int mode, ReadOnlySpan<byte> edges)
    {
        ref byte e = ref MemoryMarshal.GetReference(edges);
        ref byte d = ref MemoryMarshal.GetArrayDataReference(dst);
        if (n == 4)
        {
            for (int y = 0; y < 4; y++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, y * dstStride), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref e, RowsAt[(mode * 4) + y])));
            }

            return;
        }

        for (int y = 0; y < 8; y++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, y * dstStride), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref e, RowsAt[36 + (mode * 8) + y])));
        }
    }

    /// <summary>
    /// The SATD (as <see cref="H264EncodeSimd.Satd"/> works it out) of each of the nine Intra4x4 or Intra8x8 modes' predictions
    /// from <paramref name="edges"/> against the n by n source block at <paramref name="at"/>, into <paramref name="satd"/>;
    /// <paramref name="scratch"/> holds a prediction where there is no AVX2.
    /// </summary>
    internal static void SatdNxN(int n, byte[] src, int at, int stride, byte[] edges, byte[] scratch, Span<int> satd)
    {
        if (!Avx2.IsSupported)
        {
            for (int mode = 0; mode < 9; mode++)
            {
                PredictNxN(scratch, n, n, mode, edges);
                satd[mode] = (int)H264EncodeSimd.Satd(src, at, stride, scratch, 0, n, n, n);
            }

            return;
        }

        ref byte s = ref MemoryMarshal.GetArrayDataReference(src);
        ref byte e = ref MemoryMarshal.GetArrayDataReference(edges);
        if (n == 4)
        {
            // Four modes at once, one 4x4 block of each: each source row four times against a row of each mode.
            Vector256<short> s0 = Source4(ref s, at), s1 = Source4(ref s, at + stride);
            Vector256<short> s2 = Source4(ref s, at + (2 * stride)), s3 = Source4(ref s, at + (3 * stride));
            ref byte g = ref MemoryMarshal.GetArrayDataReference(Group4);
            for (int i = 0; i < 3; i++)
            {
                ref byte rows = ref Unsafe.Add(ref g, i * 16);
                Vector256<int> sums = BlockSums(s0 - Rows4(ref e, ref rows), s1 - Rows4(ref e, ref Unsafe.Add(ref rows, 4)),
                    s2 - Rows4(ref e, ref Unsafe.Add(ref rows, 8)), s3 - Rows4(ref e, ref Unsafe.Add(ref rows, 12)));
                satd[i * 4] = sums.GetElement(0) >> 1;
                if (i < 2)
                {
                    satd[(i * 4) + 1] = sums.GetElement(1) >> 1;
                    satd[(i * 4) + 2] = sums.GetElement(4) >> 1;
                    satd[(i * 4) + 3] = sums.GetElement(5) >> 1;
                }
            }

            return;
        }

        // One mode at a time: rows 0-3 in the low lane, 4-7 in the high one, two 4x4 blocks side by side in each.
        int s4 = 4 * stride;
        Vector256<short> t0 = Source8(ref s, at, at + s4), t1 = Source8(ref s, at + stride, at + stride + s4);
        Vector256<short> t2 = Source8(ref s, at + (2 * stride), at + (2 * stride) + s4), t3 = Source8(ref s, at + (3 * stride), at + (3 * stride) + s4);
        ref byte r = ref MemoryMarshal.GetArrayDataReference(RowsAt);
        Span<Vector256<int>> sum = stackalloc Vector256<int>[9];
        for (int mode = 0; mode < 9; mode++)
        {
            ref byte rows = ref Unsafe.Add(ref r, 36 + (mode * 8));
            sum[mode] = Sums8(t0 - Rows8(ref e, ref rows, 0), t1 - Rows8(ref e, ref rows, 1), t2 - Rows8(ref e, ref rows, 2), t3 - Rows8(ref e, ref rows, 3));
        }

        for (int i = 0; i < 8; i += 4)
        {
            Vector256<int> q = Avx2.HorizontalAdd(Avx2.HorizontalAdd(sum[i], sum[i + 1]), Avx2.HorizontalAdd(sum[i + 2], sum[i + 3]));
            Vector128<int> both = (q.GetLower() + q.GetUpper()) >> 1;
            satd[i] = both.GetElement(0);
            satd[i + 1] = both.GetElement(1);
            satd[i + 2] = both.GetElement(2);
            satd[i + 3] = both.GetElement(3);
        }

        satd[8] = Vector256.Sum(sum[8]) >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Source4(ref byte s, int at) =>
        Avx2.ConvertToVector256Int16(Vector128.Create(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, at))).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Rows4(ref byte e, ref byte at) =>
        Avx2.ConvertToVector256Int16(Vector128.Create(
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref e, at)), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref e, Unsafe.Add(ref at, 1))),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref e, Unsafe.Add(ref at, 2))), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref e, Unsafe.Add(ref at, 3)))).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Source8(ref byte s, int at0, int at1) =>
        Avx2.ConvertToVector256Int16(Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, at0)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, at1))).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Rows8(ref byte e, ref byte rows, int y) =>
        Avx2.ConvertToVector256Int16(Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref e, Unsafe.Add(ref rows, y))), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref e, Unsafe.Add(ref rows, y + 4)))).AsByte());

    // The 4x4 Hadamard of the differences of four blocks side by side (two in each 128-bit lane, one row of each per vector),
    // down the columns, and each block's columns 0|1 and 2|3: the first block of each lane in a and b, the second in c and d.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector256<int> First, Vector256<int> Second) Hadamard(Vector256<short> r0, Vector256<short> r1, Vector256<short> r2, Vector256<short> r3)
    {
        Vector256<short> s01 = r0 + r1, d01 = r0 - r1, s23 = r2 + r3, d23 = r2 - r3;
        Vector256<short> v0 = s01 + s23, v1 = s01 - s23, v2 = d01 - d23, v3 = d01 + d23;
        Vector256<int> a = Avx2.UnpackLow(v0, v1).AsInt32(), b = Avx2.UnpackLow(v2, v3).AsInt32();
        Vector256<int> c = Avx2.UnpackHigh(v0, v1).AsInt32(), d = Avx2.UnpackHigh(v2, v3).AsInt32();
        return (Across(Avx2.UnpackLow(a, b).AsInt16(), Avx2.UnpackHigh(a, b).AsInt16()), Across(Avx2.UnpackLow(c, d).AsInt16(), Avx2.UnpackHigh(c, d).AsInt16()));
    }

    // Across the rows of one block per lane, given its columns 0|1 and 2|3: the absolute coefficients summed in pairs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Across(Vector256<short> c01, Vector256<short> c23)
    {
        Vector256<short> s = c01 + c23, t = c01 - c23;
        Vector256<short> u0 = Avx2.UnpackLow(s.AsInt64(), t.AsInt64()).AsInt16(), u1 = Avx2.UnpackHigh(s.AsInt64(), t.AsInt64()).AsInt16();
        Vector256<short> e = Vector256.Abs(u0 + u1), f = Vector256.Abs(u0 - u1);
        return Avx2.MultiplyAddAdjacent(e, Vector256<short>.One) + Avx2.MultiplyAddAdjacent(f, Vector256<short>.One);
    }

    // Each block's sum of absolute Hadamard coefficients: the first block of the low lane in element 0, its second in 1, the
    // high lane's in 4 and 5.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> BlockSums(Vector256<short> r0, Vector256<short> r1, Vector256<short> r2, Vector256<short> r3)
    {
        (Vector256<int> first, Vector256<int> second) = Hadamard(r0, r1, r2, r3);
        Vector256<int> h = Avx2.HorizontalAdd(first, second);
        return Avx2.HorizontalAdd(h, h);
    }

    // The absolute Hadamard coefficients of the four blocks, in eight partial sums.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Sums8(Vector256<short> r0, Vector256<short> r1, Vector256<short> r2, Vector256<short> r3)
    {
        (Vector256<int> first, Vector256<int> second) = Hadamard(r0, r1, r2, r3);
        return first + second;
    }

    private static int Dc(int n, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        int st = 0, sl = 0;
        for (int i = 1; i <= n; i++)
        {
            st += t[i];
            sl += l[i];
        }

        int log = n == 4 ? 2 : n == 8 ? 3 : 4;
        return top && left ? (st + sl + n) >> (log + 1)
            : left ? (sl + (n >> 1)) >> log
            : top ? (st + (n >> 1)) >> log
            : 128;
    }

    /// <summary>Predicts a 16x16 luma block in an Intra16x16 mode.</summary>
    internal static void Predict16x16(Span<byte> dst, int dstStride, int mode, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        if (mode == 3)
        {
            int h = 0, v = 0;
            for (int i = 0; i < 8; i++)
            {
                h += (i + 1) * (t[9 + i] - t[7 - i]);
                v += (i + 1) * (l[9 + i] - l[7 - i]);
            }

            Plane(dst, dstStride, 16, 16, 16 * (l[16] + t[16]), ((5 * h) + 32) >> 6, ((5 * v) + 32) >> 6, 7, 7);
            return;
        }

        if (mode == 0)
        {
            Span<byte> first = dst[..16];
            for (int x = 0; x < 16; x++)
            {
                first[x] = (byte)t[x + 1];
            }

            for (int y = 1; y < 16; y++)
            {
                first.CopyTo(dst.Slice(y * dstStride, 16));
            }

            return;
        }

        byte dc = (byte)(mode == 2 ? Dc(16, t, l, top, left) : 0);
        for (int y = 0; y < 16; y++)
        {
            dst.Slice(y * dstStride, 16).Fill(mode == 1 ? (byte)l[y + 1] : dc);
        }
    }

    /// <summary>Predicts an 8x8 chroma block of 4:2:0 in a chroma mode.</summary>
    internal static void PredictChroma(Span<byte> dst, int dstStride, int mode, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        if (mode == 3)
        {
            int h = 0, v = 0;
            for (int i = 0; i < 4; i++)
            {
                h += (i + 1) * (t[5 + i] - t[3 - i]);
                v += (i + 1) * (l[5 + i] - l[3 - i]);
            }

            Plane(dst, dstStride, 8, 8, 16 * (l[8] + t[8]), ((34 * h) + 32) >> 6, ((34 * v) + 32) >> 6, 3, 3);
            return;
        }

        if (mode == 1)
        {
            for (int y = 0; y < 8; y++)
            {
                dst.Slice(y * dstStride, 8).Fill((byte)l[y + 1]);
            }

            return;
        }

        ulong row;
        if (mode == 2)
        {
            row = 0;
            for (int x = 7; x >= 0; x--)
            {
                row = (row << 8) | (byte)t[x + 1];
            }

            for (int y = 0; y < 8; y++)
            {
                MemoryMarshal.Write(dst.Slice(y * dstStride, 8), row);
            }

            return;
        }

        // DC of each 4x4 quarter (8.3.4.1 to 8.3.4.3): the corner quarters from both sides, the others from the side they touch.
        for (int by = 0; by < 2; by++)
        {
            row = 0;
            for (int bx = 1; bx >= 0; bx--)
            {
                int st = t[(bx * 4) + 1] + t[(bx * 4) + 2] + t[(bx * 4) + 3] + t[(bx * 4) + 4];
                int sl = l[(by * 4) + 1] + l[(by * 4) + 2] + l[(by * 4) + 3] + l[(by * 4) + 4];
                bool both = (bx == 0) == (by == 0);
                int value = both ? (top && left ? (st + sl + 4) >> 3 : left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128)
                    : bx > 0 ? (top ? (st + 2) >> 2 : left ? (sl + 2) >> 2 : 128)
                    : (left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128);
                row = (row << 32) | ((uint)value * 0x01010101u);
            }

            for (int y = 0; y < 4; y++)
            {
                MemoryMarshal.Write(dst.Slice(((by * 4) + y) * dstStride, 8), row);
            }
        }
    }

    private static readonly Vector128<short> Ramp = Vector128.Create(0, 1, 2, 3, 4, 5, 6, 7);

    private static void Plane(Span<byte> dst, int dstStride, int width, int height, int a, int b, int c, int cx, int cy)
    {
        if (Sse2.IsSupported)
        {
            // In shorts: a + b(x − cx) + c(y − cy) + 16 stays within ±20000 for any neighbours (b and c at most 717 for 16x16
            // with |x − cx| up to 8, at most 1355 for chroma with up to 4), so each row adds c to the one before; the shift is
            // arithmetic and the pack clamps to 0 to 255 as the formula does.
            Vector128<short> left = Vector128.Create((short)(a + 16 - (c * cy))) + (Vector128.Create((short)b) * (Ramp - Vector128.Create((short)cx)));
            Vector128<short> right = left + Vector128.Create((short)(8 * b)), down = Vector128.Create((short)c);
            for (int y = 0; y < height; y++)
            {
                Span<byte> row = dst.Slice(y * dstStride, width);
                Vector128<byte> bytes = Sse2.PackUnsignedSaturate(Sse2.ShiftRightArithmetic(left, 5), Sse2.ShiftRightArithmetic(right, 5));
                if (width == 16)
                {
                    bytes.CopyTo(row);
                }
                else
                {
                    MemoryMarshal.Write(row, bytes.AsUInt64().ToScalar());
                }

                left += down;
                right += down;
            }

            return;
        }

        for (int y = 0; y < height; y++)
        {
            Span<byte> row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                row[x] = (byte)Math.Clamp((a + (b * (x - cx)) + (c * (y - cy)) + 16) >> 5, 0, 255);
            }
        }
    }
}
