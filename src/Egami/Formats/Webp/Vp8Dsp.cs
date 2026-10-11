using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Webp;

/// <summary>
/// The pixel work of VP8 (RFC 6386): intra prediction (sections 12.2 and 12.3), the inverse transforms (14.3 and 14.4) and the
/// loop filters (15). Blocks are addressed in a work buffer with <see cref="Stride"/> bytes a row, so the row above and the column
/// to the left of a block are its neighbours in memory.
/// </summary>
internal static class Vp8Dsp
{
    /// <summary>The row length of the reconstruction work buffers.</summary>
    internal const int Stride = 32;

    // --- Inverse transforms ------------------------------------------------------------------------------------------------

    /// <summary>The inverse Walsh-Hadamard transform: the 16 coefficients of the Y2 block become the DC of each luma block.</summary>
    internal static void InverseWalshHadamard(ReadOnlySpan<short> input, Span<short> coefficients)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[i] - input[12 + i];
            tmp[i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i * 4] + 3;
            int a0 = dc + tmp[(i * 4) + 3];
            int a1 = tmp[(i * 4) + 1] + tmp[(i * 4) + 2];
            int a2 = tmp[(i * 4) + 1] - tmp[(i * 4) + 2];
            int a3 = dc - tmp[(i * 4) + 3];
            coefficients[(i * 64) + 0] = (short)((a0 + a1) >> 3);
            coefficients[(i * 64) + 16] = (short)((a3 + a2) >> 3);
            coefficients[(i * 64) + 32] = (short)((a0 - a1) >> 3);
            coefficients[(i * 64) + 48] = (short)((a3 - a2) >> 3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul1(int a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul2(int a) => (a * 35468) >> 16;

    /// <summary>Adds the inverse DCT of a block's 16 coefficients to the 4 × 4 pixels at <paramref name="at"/>.</summary>
    internal static void InverseTransformAdd(ReadOnlySpan<short> input, Span<byte> buffer, int at)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = input[i] + input[8 + i];
            int b = input[i] - input[8 + i];
            int c = Mul2(input[4 + i]) - Mul1(input[12 + i]);
            int d = Mul1(input[4 + i]) + Mul2(input[12 + i]);
            tmp[(i * 4) + 0] = a + d;
            tmp[(i * 4) + 1] = b + c;
            tmp[(i * 4) + 2] = b - c;
            tmp[(i * 4) + 3] = a - d;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i] + 4;
            int a = dc + tmp[8 + i];
            int b = dc - tmp[8 + i];
            int c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
            int d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
            int row = at + (i * Stride);
            buffer[row] = Clip8(buffer[row] + ((a + d) >> 3));
            buffer[row + 1] = Clip8(buffer[row + 1] + ((b + c) >> 3));
            buffer[row + 2] = Clip8(buffer[row + 2] + ((b - c) >> 3));
            buffer[row + 3] = Clip8(buffer[row + 3] + ((a - d) >> 3));
        }
    }

    /// <summary>Adds the inverse DCT of a block whose only non-zero coefficient is its DC.</summary>
    internal static void InverseTransformDcAdd(short dc, Span<byte> buffer, int at)
    {
        int add = (dc + 4) >> 3;
        for (int y = 0; y < 4; y++)
        {
            int row = at + (y * Stride);
            for (int x = 0; x < 4; x++)
            {
                buffer[row + x] = Clip8(buffer[row + x] + add);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Clip8(int v) => (uint)v <= 255 ? (byte)v : v < 0 ? (byte)0 : (byte)255;

    // --- Intra prediction ---------------------------------------------------------------------------------------------------

    /// <summary>The 16 × 16 and 8 × 8 prediction modes, numbered as RFC 6386 numbers them, with DC split by which edges exist.</summary>
    internal enum BlockMode
    {
        Dc,
        Vertical,
        Horizontal,
        TrueMotion,
        DcNoTop,
        DcNoLeft,
        DcNoTopLeft,
    }

    /// <summary>Predicts a square block of <paramref name="size"/> pixels (16 or 8) at <paramref name="at"/>.</summary>
    internal static void PredictBlock(BlockMode mode, Span<byte> b, int at, int size)
    {
        int top = at - Stride;
        switch (mode)
        {
            case BlockMode.Vertical:
                for (int y = 0; y < size; y++)
                {
                    b.Slice(top, size).CopyTo(b.Slice(at + (y * Stride), size));
                }

                break;
            case BlockMode.Horizontal:
                for (int y = 0; y < size; y++)
                {
                    b.Slice(at + (y * Stride), size).Fill(b[at + (y * Stride) - 1]);
                }

                break;
            case BlockMode.TrueMotion:
                TrueMotion(b, at, size);
                break;
            default:
                int shift = size == 16 ? 4 : 3;
                int sum = 0;
                bool hasTop = mode is BlockMode.Dc or BlockMode.DcNoLeft;
                bool hasLeft = mode is BlockMode.Dc or BlockMode.DcNoTop;
                if (hasTop)
                {
                    for (int x = 0; x < size; x++)
                    {
                        sum += b[top + x];
                    }
                }

                if (hasLeft)
                {
                    for (int y = 0; y < size; y++)
                    {
                        sum += b[at + (y * Stride) - 1];
                    }
                }

                int dc = hasTop && hasLeft ? (sum + size) >> (shift + 1) : hasTop || hasLeft ? (sum + (size >> 1)) >> shift : 0x80;
                for (int y = 0; y < size; y++)
                {
                    b.Slice(at + (y * Stride), size).Fill((byte)dc);
                }

                break;
        }
    }

    private static void TrueMotion(Span<byte> b, int at, int size)
    {
        int top = at - Stride;
        int corner = b[top - 1];
        for (int y = 0; y < size; y++)
        {
            int row = at + (y * Stride);
            int left = b[row - 1] - corner;
            for (int x = 0; x < size; x++)
            {
                b[row + x] = Clip8(b[top + x] + left);
            }
        }
    }

    /// <summary>The ten 4 × 4 modes, numbered as RFC 6386 numbers them (the order of its mode tree and probability tables).</summary>
    internal const int BDc = 0, BTm = 1, BVe = 2, BHe = 3, BLd = 4, BRd = 5, BVr = 6, BVl = 7, BHd = 8, BHu = 9;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg3(int a, int b, int c) => (byte)((a + (2 * b) + c + 2) >> 2);

    /// <summary>Predicts the 4 × 4 block at <paramref name="at"/>: the row above (and four above-right) and the column to the left are in place.</summary>
    internal static void PredictSubblock(int mode, Span<byte> b, int at)
    {
        int t = at - Stride;
        int x0 = b[t - 1];
        int a0 = b[t], a1 = b[t + 1], a2 = b[t + 2], a3 = b[t + 3];
        int l0 = b[at - 1], l1 = b[at + Stride - 1], l2 = b[at + (2 * Stride) - 1], l3 = b[at + (3 * Stride) - 1];
        int r0 = at, r1 = at + Stride, r2 = at + (2 * Stride), r3 = at + (3 * Stride);
        switch (mode)
        {
            case BDc:
                {
                    byte dc = (byte)((a0 + a1 + a2 + a3 + l0 + l1 + l2 + l3 + 4) >> 3);
                    b.Slice(r0, 4).Fill(dc);
                    b.Slice(r1, 4).Fill(dc);
                    b.Slice(r2, 4).Fill(dc);
                    b.Slice(r3, 4).Fill(dc);
                    break;
                }

            case BTm:
                TrueMotion(b, at, 4);
                break;
            case BVe:
                {
                    int a4 = b[t + 4];
                    byte v0 = Avg3(x0, a0, a1), v1 = Avg3(a0, a1, a2), v2 = Avg3(a1, a2, a3), v3 = Avg3(a2, a3, a4);
                    foreach (int r in (ReadOnlySpan<int>)[r0, r1, r2, r3])
                    {
                        b[r] = v0;
                        b[r + 1] = v1;
                        b[r + 2] = v2;
                        b[r + 3] = v3;
                    }

                    break;
                }

            case BHe:
                b.Slice(r0, 4).Fill(Avg3(x0, l0, l1));
                b.Slice(r1, 4).Fill(Avg3(l0, l1, l2));
                b.Slice(r2, 4).Fill(Avg3(l1, l2, l3));
                b.Slice(r3, 4).Fill(Avg3(l2, l3, l3));
                break;
            case BLd:
                {
                    int a4 = b[t + 4], a5 = b[t + 5], a6 = b[t + 6], a7 = b[t + 7];
                    b[r0] = Avg3(a0, a1, a2);
                    b[r0 + 1] = b[r1] = Avg3(a1, a2, a3);
                    b[r0 + 2] = b[r1 + 1] = b[r2] = Avg3(a2, a3, a4);
                    b[r0 + 3] = b[r1 + 2] = b[r2 + 1] = b[r3] = Avg3(a3, a4, a5);
                    b[r1 + 3] = b[r2 + 2] = b[r3 + 1] = Avg3(a4, a5, a6);
                    b[r2 + 3] = b[r3 + 2] = Avg3(a5, a6, a7);
                    b[r3 + 3] = Avg3(a6, a7, a7);
                    break;
                }

            case BRd:
                b[r3] = Avg3(l3, l2, l1);
                b[r3 + 1] = b[r2] = Avg3(l2, l1, l0);
                b[r3 + 2] = b[r2 + 1] = b[r1] = Avg3(l1, l0, x0);
                b[r3 + 3] = b[r2 + 2] = b[r1 + 1] = b[r0] = Avg3(l0, x0, a0);
                b[r2 + 3] = b[r1 + 2] = b[r0 + 1] = Avg3(x0, a0, a1);
                b[r1 + 3] = b[r0 + 2] = Avg3(a0, a1, a2);
                b[r0 + 3] = Avg3(a1, a2, a3);
                break;
            case BVr:
                b[r3] = Avg3(l2, l1, l0);
                b[r2] = Avg3(l1, l0, x0);
                b[r3 + 1] = b[r1] = Avg3(l0, x0, a0);
                b[r2 + 1] = b[r0] = Avg2(x0, a0);
                b[r3 + 2] = b[r1 + 1] = Avg3(x0, a0, a1);
                b[r2 + 2] = b[r0 + 1] = Avg2(a0, a1);
                b[r3 + 3] = b[r1 + 2] = Avg3(a0, a1, a2);
                b[r2 + 3] = b[r0 + 2] = Avg2(a1, a2);
                b[r1 + 3] = Avg3(a1, a2, a3);
                b[r0 + 3] = Avg2(a2, a3);
                break;
            case BVl:
                {
                    int a4 = b[t + 4], a5 = b[t + 5], a6 = b[t + 6], a7 = b[t + 7];
                    b[r0] = Avg2(a0, a1);
                    b[r1] = Avg3(a0, a1, a2);
                    b[r2] = b[r0 + 1] = Avg2(a1, a2);
                    b[r1 + 1] = b[r3] = Avg3(a1, a2, a3);
                    b[r2 + 1] = b[r0 + 2] = Avg2(a2, a3);
                    b[r3 + 1] = b[r1 + 2] = Avg3(a2, a3, a4);
                    b[r2 + 2] = b[r0 + 3] = Avg2(a3, a4);
                    b[r3 + 2] = b[r1 + 3] = Avg3(a3, a4, a5);
                    b[r2 + 3] = Avg3(a4, a5, a6);
                    b[r3 + 3] = Avg3(a5, a6, a7);
                    break;
                }

            case BHd:
                b[r3] = Avg2(l3, l2);
                b[r3 + 1] = Avg3(l3, l2, l1);
                b[r2] = b[r3 + 2] = Avg2(l2, l1);
                b[r2 + 1] = b[r3 + 3] = Avg3(l2, l1, l0);
                b[r2 + 2] = b[r1] = Avg2(l1, l0);
                b[r2 + 3] = b[r1 + 1] = Avg3(l1, l0, x0);
                b[r1 + 2] = b[r0] = Avg2(l0, x0);
                b[r1 + 3] = b[r0 + 1] = Avg3(l0, x0, a0);
                b[r0 + 2] = Avg3(x0, a0, a1);
                b[r0 + 3] = Avg3(a0, a1, a2);
                break;
            default:
                b[r0] = Avg2(l0, l1);
                b[r0 + 1] = Avg3(l0, l1, l2);
                b[r0 + 2] = b[r1] = Avg2(l1, l2);
                b[r0 + 3] = b[r1 + 1] = Avg3(l1, l2, l3);
                b[r1 + 2] = b[r2] = Avg2(l2, l3);
                b[r1 + 3] = b[r2 + 1] = Avg3(l2, l3, l3);
                b[r2 + 2] = b[r2 + 3] = (byte)l3;
                b.Slice(r3, 4).Fill((byte)l3);
                break;
        }
    }

    // --- Loop filters -------------------------------------------------------------------------------------------------------

    // Lookup tables over the ranges the filters reach: |i| for i in [-255, 255], i clamped to [-128, 127] for i in [-1020, 1020],
    // to [-16, 15] for i in [-112, 112], and to [0, 255] for i in [-255, 510].
    private static readonly byte[] AbsTable = BuildTable(-255, 255, Math.Abs);
    private static readonly sbyte[] SignedClipTable = BuildSigned(-1020, 1020, -128, 127);
    private static readonly sbyte[] SignedClip16Table = BuildSigned(-112, 112, -16, 15);
    private static readonly byte[] ClipTable = BuildTable(-255, 510, v => Math.Clamp(v, 0, 255));

    private static byte[] BuildTable(int from, int to, Func<int, int> f)
    {
        byte[] table = new byte[to - from + 1];
        for (int i = from; i <= to; i++)
        {
            table[i - from] = (byte)f(i);
        }

        return table;
    }

    private static sbyte[] BuildSigned(int from, int to, int min, int max)
    {
        sbyte[] table = new sbyte[to - from + 1];
        for (int i = from; i <= to; i++)
        {
            table[i - from] = (sbyte)Math.Clamp(i, min, max);
        }

        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs(int v) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(AbsTable), v + 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignedClip(int v) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SignedClipTable), v + 1020);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignedClip16(int v) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SignedClip16Table), v + 112);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixel(int v) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ClipTable), v + 255);

    /// <summary>Filters across one edge pixel position, changing p0 and q0 (the simple filter and the high-variance case).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter2(ref byte q, nint step)
    {
        int p1 = Unsafe.Add(ref q, -2 * step), p0 = Unsafe.Add(ref q, -step), q0 = q, q1 = Unsafe.Add(ref q, step);
        int a = (3 * (q0 - p0)) + SignedClip(p1 - q1);
        int a1 = SignedClip16((a + 4) >> 3);
        int a2 = SignedClip16((a + 3) >> 3);
        Unsafe.Add(ref q, -step) = ClipPixel(p0 + a2);
        q = ClipPixel(q0 - a1);
    }

    /// <summary>The inner-edge filter: changes p1, p0, q0 and q1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter4(ref byte q, nint step)
    {
        int p1 = Unsafe.Add(ref q, -2 * step), p0 = Unsafe.Add(ref q, -step), q0 = q, q1 = Unsafe.Add(ref q, step);
        int a = 3 * (q0 - p0);
        int a1 = SignedClip16((a + 4) >> 3);
        int a2 = SignedClip16((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;
        Unsafe.Add(ref q, -2 * step) = ClipPixel(p1 + a3);
        Unsafe.Add(ref q, -step) = ClipPixel(p0 + a2);
        q = ClipPixel(q0 - a1);
        Unsafe.Add(ref q, step) = ClipPixel(q1 - a3);
    }

    /// <summary>The macroblock-edge filter: changes three pixels on each side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter6(ref byte q, nint step)
    {
        int p2 = Unsafe.Add(ref q, -3 * step), p1 = Unsafe.Add(ref q, -2 * step), p0 = Unsafe.Add(ref q, -step);
        int q0 = q, q1 = Unsafe.Add(ref q, step), q2 = Unsafe.Add(ref q, 2 * step);
        int a = SignedClip((3 * (q0 - p0)) + SignedClip(p1 - q1));
        int a1 = ((27 * a) + 63) >> 7;
        int a2 = ((18 * a) + 63) >> 7;
        int a3 = ((9 * a) + 63) >> 7;
        Unsafe.Add(ref q, -3 * step) = ClipPixel(p2 + a3);
        Unsafe.Add(ref q, -2 * step) = ClipPixel(p1 + a2);
        Unsafe.Add(ref q, -step) = ClipPixel(p0 + a1);
        q = ClipPixel(q0 - a1);
        Unsafe.Add(ref q, step) = ClipPixel(q1 - a2);
        Unsafe.Add(ref q, 2 * step) = ClipPixel(q2 - a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HighVariance(ref byte q, nint step, int threshold)
    {
        int p1 = Unsafe.Add(ref q, -2 * step), p0 = Unsafe.Add(ref q, -step), q0 = q, q1 = Unsafe.Add(ref q, step);
        return Abs(p1 - p0) > threshold || Abs(q1 - q0) > threshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter(ref byte q, nint step, int threshold2)
    {
        int p1 = Unsafe.Add(ref q, -2 * step), p0 = Unsafe.Add(ref q, -step), q0 = q, q1 = Unsafe.Add(ref q, step);
        return (4 * Abs(p0 - q0)) + Abs(p1 - q1) <= threshold2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter2(ref byte q, nint step, int threshold2, int interior)
    {
        int p3 = Unsafe.Add(ref q, -4 * step), p2 = Unsafe.Add(ref q, -3 * step), p1 = Unsafe.Add(ref q, -2 * step), p0 = Unsafe.Add(ref q, -step);
        int q0 = q, q1 = Unsafe.Add(ref q, step), q2 = Unsafe.Add(ref q, 2 * step), q3 = Unsafe.Add(ref q, 3 * step);
        if ((4 * Abs(p0 - q0)) + Abs(p1 - q1) > threshold2)
        {
            return false;
        }

        return Abs(p3 - p2) <= interior && Abs(p2 - p1) <= interior && Abs(p1 - p0) <= interior
            && Abs(q3 - q2) <= interior && Abs(q2 - q1) <= interior && Abs(q1 - q0) <= interior;
    }

    /// <summary>
    /// The simple filter along <paramref name="count"/> positions of an edge: <paramref name="step"/> crosses the edge (1 for a
    /// vertical edge, the stride for a horizontal one) and <paramref name="along"/> moves along it. The caller guarantees the
    /// pixels the filter reads lie inside <paramref name="p"/>.
    /// </summary>
    internal static void SimpleFilter(Span<byte> p, int at, int step, int along, int count, int threshold)
    {
        int threshold2 = (2 * threshold) + 1;
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at);
        for (int i = 0; i < count; i++, q = ref Unsafe.Add(ref q, along))
        {
            if (NeedsFilter(ref q, step, threshold2))
            {
                Filter2(ref q, step);
            }
        }
    }

    /// <summary>The normal filter on a macroblock edge: the high-variance case changes one pixel a side, else three.</summary>
    internal static void MacroblockEdgeFilter(Span<byte> p, int at, int step, int along, int count, int threshold, int interior, int hevThreshold)
    {
        int threshold2 = (2 * threshold) + 1;
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at);
        for (int i = 0; i < count; i++, q = ref Unsafe.Add(ref q, along))
        {
            if (NeedsFilter2(ref q, step, threshold2, interior))
            {
                if (HighVariance(ref q, step, hevThreshold))
                {
                    Filter2(ref q, step);
                }
                else
                {
                    Filter6(ref q, step);
                }
            }
        }
    }

    /// <summary>The normal filter on an inner (sub-block) edge: the high-variance case changes one pixel a side, else two.</summary>
    internal static void InnerEdgeFilter(Span<byte> p, int at, int step, int along, int count, int threshold, int interior, int hevThreshold)
    {
        int threshold2 = (2 * threshold) + 1;
        ref byte q = ref Unsafe.Add(ref MemoryMarshal.GetReference(p), at);
        for (int i = 0; i < count; i++, q = ref Unsafe.Add(ref q, along))
        {
            if (NeedsFilter2(ref q, step, threshold2, interior))
            {
                if (HighVariance(ref q, step, hevThreshold))
                {
                    Filter2(ref q, step);
                }
                else
                {
                    Filter4(ref q, step);
                }
            }
        }
    }
}
