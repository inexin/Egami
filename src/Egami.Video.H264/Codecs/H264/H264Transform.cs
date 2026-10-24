using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.H264;

/// <summary>The inverse 4x4 and 8x8 transforms (8.5.12.2, 8.5.13.2), added to the prediction in a plane.</summary>
internal static class H264Transform
{
    /// <summary>Transforms scaled coefficients d (raster order, row by row) and adds (r + 32) &gt;&gt; 6 to the 4x4 block at <paramref name="at"/>.</summary>
    internal static void Add4x4<T>(Span<int> d, T[] plane, int at, int stride, int max)
        where T : unmanaged
    {
        if (Vector128.IsHardwareAccelerated && Sse2.IsSupported && H264Inter.UseVectors)
        {
            if (d.Length < 16 || at < 0 || at + (3 * stride) + 8 > plane.Length)
            {
                throw H264Errors.Corrupt("A residual block lies outside the picture.");
            }

            // DC only when coefficients 1 to 15 are zero: three vector tests (the first lane of the first vector is the DC).
            ref int c = ref MemoryMarshal.GetReference(d);
            Vector128<int> ac = Vector128.LoadUnsafe(ref c).WithElement(0, 0) | Vector128.LoadUnsafe(ref c, 4) | Vector128.LoadUnsafe(ref c, 8) | Vector128.LoadUnsafe(ref c, 12);
            if (ac == Vector128<int>.Zero)
            {
                ref T s = ref H264Samples.At(plane, at);
                Vector128<int> dcv = Vector128.Create((c + 32) >> 6), zero = Vector128<int>.Zero, top = Vector128.Create(max);
                for (int y = 0; y < 4; y++)
                {
                    Vector128<int> v = Vector128.Min(Vector128.Max(H264Samples.Load4Wide(ref s, y * stride) + dcv, zero), top);
                    H264Samples.Store4Wide(ref s, y * stride, v);
                }

                return;
            }

            Add4x4Vector(d, plane, at, stride, max);
            return;
        }

        bool dcOnly = true;
        for (int i = 1; i < 16 && dcOnly; i++)
        {
            dcOnly = d[i] == 0;
        }

        if (dcOnly)
        {
            int dc = (d[0] + 32) >> 6;
            for (int y = 0; y < 4; y++)
            {
                int row = at + (y * stride);
                for (int x = 0; x < 4; x++)
                {
                    H264Samples.Set(plane, row + x, Math.Clamp(H264Samples.Get(plane, row + x) + dc, 0, max));
                }
            }

            return;
        }

        Span<int> f = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int d0 = d[(i * 4) + 0], d1 = d[(i * 4) + 1], d2 = d[(i * 4) + 2], d3 = d[(i * 4) + 3];
            int e0 = d0 + d2, e1 = d0 - d2, e2 = (d1 >> 1) - d3, e3 = d1 + (d3 >> 1);
            f[(i * 4) + 0] = e0 + e3;
            f[(i * 4) + 1] = e1 + e2;
            f[(i * 4) + 2] = e1 - e2;
            f[(i * 4) + 3] = e0 - e3;
        }

        for (int j = 0; j < 4; j++)
        {
            int f0 = f[j], f1 = f[4 + j], f2 = f[8 + j], f3 = f[12 + j];
            int g0 = f0 + f2, g1 = f0 - f2, g2 = (f1 >> 1) - f3, g3 = f1 + (f3 >> 1);
            Add(plane, at + j, max, g0 + g3);
            Add(plane, at + stride + j, max, g1 + g2);
            Add(plane, at + (2 * stride) + j, max, g1 - g2);
            Add(plane, at + (3 * stride) + j, max, g0 - g3);
        }
    }

    /// <summary>
    /// <see cref="Add4x4"/> in 32-bit lanes: the rows are transposed so each butterfly of the row transform handles all four rows,
    /// then transposed back for the column transform, whose lanes are then the columns of the block.
    /// </summary>
    private static void Add4x4Vector<T>(Span<int> d, T[] plane, int at, int stride, int max)
        where T : unmanaged
    {
        if (d.Length < 16 || at < 0 || at + (3 * stride) + 8 > plane.Length)
        {
            throw H264Errors.Corrupt("A residual block lies outside the picture.");
        }

        ref int r = ref MemoryMarshal.GetReference(d);
        Vector128<int> c0 = Vector128.LoadUnsafe(ref r), c1 = Vector128.LoadUnsafe(ref r, 4), c2 = Vector128.LoadUnsafe(ref r, 8), c3 = Vector128.LoadUnsafe(ref r, 12);
        Transpose(ref c0, ref c1, ref c2, ref c3);
        Vector128<int> e0 = c0 + c2, e1 = c0 - c2, e2 = (c1 >> 1) - c3, e3 = c1 + (c3 >> 1);
        Vector128<int> f0 = e0 + e3, f1 = e1 + e2, f2 = e1 - e2, f3 = e0 - e3;
        Transpose(ref f0, ref f1, ref f2, ref f3);
        Vector128<int> g0 = f0 + f2, g1 = f0 - f2, g2 = (f1 >> 1) - f3, g3 = f1 + (f3 >> 1);
        ref T s = ref H264Samples.At(plane, at);
        Vector128<int> round = Vector128.Create(32), top = Vector128.Create(max);
        AddRow(ref s, 0, g0 + g3, round, top);
        AddRow(ref s, stride, g1 + g2, round, top);
        AddRow(ref s, 2 * stride, g1 - g2, round, top);
        AddRow(ref s, 3 * stride, g0 - g3, round, top);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRow<T>(ref T s, int offset, Vector128<int> residual, Vector128<int> round, Vector128<int> top)
        where T : unmanaged
    {
        Vector128<int> v = H264Samples.Load4Wide(ref s, offset) + ((residual + round) >> 6);
        H264Samples.Store4Wide(ref s, offset, Vector128.Min(Vector128.Max(v, Vector128<int>.Zero), top));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector128<int> a0, ref Vector128<int> a1, ref Vector128<int> a2, ref Vector128<int> a3)
    {
        Vector128<int> t0 = Sse2.UnpackLow(a0, a1), t1 = Sse2.UnpackHigh(a0, a1), t2 = Sse2.UnpackLow(a2, a3), t3 = Sse2.UnpackHigh(a2, a3);
        a0 = Sse2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32();
        a1 = Sse2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
        a2 = Sse2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32();
        a3 = Sse2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
    }

    /// <summary>Transforms an 8x8 block of scaled coefficients (raster order) and adds it to the plane.</summary>
    internal static void Add8x8<T>(Span<int> d, T[] plane, int at, int stride, int max)
        where T : unmanaged
    {
        if (Avx2.IsSupported && H264Inter.UseVectors)
        {
            Add8x8Vector(d, plane, at, stride, max);
            return;
        }

        Span<int> m = stackalloc int[64];
        for (int i = 0; i < 8; i++)
        {
            Transform8(d.Slice(i * 8, 8), m.Slice(i * 8, 8));
        }

        Span<int> column = stackalloc int[8];
        Span<int> result = stackalloc int[8];
        for (int j = 0; j < 8; j++)
        {
            for (int i = 0; i < 8; i++)
            {
                column[i] = m[(i * 8) + j];
            }

            Transform8(column, result);
            for (int i = 0; i < 8; i++)
            {
                Add(plane, at + (i * stride) + j, max, result[i]);
            }
        }
    }

    /// <summary>
    /// <see cref="Add8x8"/> in eight 256-bit rows of 32-bit lanes: transposed, each butterfly of the row transform handles all
    /// eight rows at once, then transposed back for the column transform, whose lanes are the block's columns.
    /// </summary>
    private static void Add8x8Vector<T>(Span<int> d, T[] plane, int at, int stride, int max)
        where T : unmanaged
    {
        if (d.Length < 64 || at < 0 || at + (7 * stride) + 8 > plane.Length)
        {
            throw H264Errors.Corrupt("A residual block lies outside the picture.");
        }

        ref int r = ref MemoryMarshal.GetReference(d);
        Vector256<int> a0 = Vector256.LoadUnsafe(ref r), a1 = Vector256.LoadUnsafe(ref r, 8), a2 = Vector256.LoadUnsafe(ref r, 16), a3 = Vector256.LoadUnsafe(ref r, 24);
        Vector256<int> a4 = Vector256.LoadUnsafe(ref r, 32), a5 = Vector256.LoadUnsafe(ref r, 40), a6 = Vector256.LoadUnsafe(ref r, 48), a7 = Vector256.LoadUnsafe(ref r, 56);
        Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
        Butterfly8(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
        Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
        Butterfly8(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
        ref T s = ref H264Samples.At(plane, at);
        Vector256<int> round = Vector256.Create(32), top = Vector256.Create(max);
        AddRow8(ref s, 0, a0, round, top);
        AddRow8(ref s, stride, a1, round, top);
        AddRow8(ref s, 2 * stride, a2, round, top);
        AddRow8(ref s, 3 * stride, a3, round, top);
        AddRow8(ref s, 4 * stride, a4, round, top);
        AddRow8(ref s, 5 * stride, a5, round, top);
        AddRow8(ref s, 6 * stride, a6, round, top);
        AddRow8(ref s, 7 * stride, a7, round, top);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRow8<T>(ref T s, int offset, Vector256<int> residual, Vector256<int> round, Vector256<int> top)
        where T : unmanaged
    {
        Vector256<int> v = Avx2.ConvertToVector256Int32(H264Samples.Load8(ref s, offset)) + ((residual + round) >> 6);
        v = Vector256.Min(Vector256.Max(v, Vector256<int>.Zero), top);
        H264Samples.Store8(ref s, offset, Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper()));
    }

    /// <summary>The 8-point transform of 8.5.13.2 on eight lanes at once: a0 to a7 are the inputs d0 to d7, then the outputs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly8(ref Vector256<int> a0, ref Vector256<int> a1, ref Vector256<int> a2, ref Vector256<int> a3,
        ref Vector256<int> a4, ref Vector256<int> a5, ref Vector256<int> a6, ref Vector256<int> a7)
    {
        Vector256<int> e0 = a0 + a4;
        Vector256<int> e1 = a5 - a3 - a7 - (a7 >> 1);
        Vector256<int> e2 = a0 - a4;
        Vector256<int> e3 = a1 + a7 - a3 - (a3 >> 1);
        Vector256<int> e4 = (a2 >> 1) - a6;
        Vector256<int> e5 = a7 - a1 + a5 + (a5 >> 1);
        Vector256<int> e6 = a2 + (a6 >> 1);
        Vector256<int> e7 = a3 + a5 + a1 + (a1 >> 1);
        Vector256<int> f0 = e0 + e6, f1 = e1 + (e7 >> 2), f2 = e2 + e4, f3 = e3 + (e5 >> 2);
        Vector256<int> f4 = e2 - e4, f5 = (e3 >> 2) - e5, f6 = e0 - e6, f7 = e7 - (e1 >> 2);
        a0 = f0 + f7;
        a1 = f2 + f5;
        a2 = f4 + f3;
        a3 = f6 + f1;
        a4 = f6 - f1;
        a5 = f4 - f3;
        a6 = f2 - f5;
        a7 = f0 - f7;
    }

    /// <summary>Transposes an 8 by 8 block of 32-bit values held as eight 256-bit rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3,
        ref Vector256<int> r4, ref Vector256<int> r5, ref Vector256<int> r6, ref Vector256<int> r7)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1), t1 = Avx2.UnpackHigh(r0, r1), t2 = Avx2.UnpackLow(r2, r3), t3 = Avx2.UnpackHigh(r2, r3);
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5), t5 = Avx2.UnpackHigh(r4, r5), t6 = Avx2.UnpackLow(r6, r7), t7 = Avx2.UnpackHigh(r6, r7);
        Vector256<int> u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32(), u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
        Vector256<int> u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32(), u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        Vector256<int> u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()).AsInt32(), u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64()).AsInt32();
        Vector256<int> u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()).AsInt32(), u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64()).AsInt32();
        r0 = Avx2.Permute2x128(u0, u4, 0x20);
        r1 = Avx2.Permute2x128(u1, u5, 0x20);
        r2 = Avx2.Permute2x128(u2, u6, 0x20);
        r3 = Avx2.Permute2x128(u3, u7, 0x20);
        r4 = Avx2.Permute2x128(u0, u4, 0x31);
        r5 = Avx2.Permute2x128(u1, u5, 0x31);
        r6 = Avx2.Permute2x128(u2, u6, 0x31);
        r7 = Avx2.Permute2x128(u3, u7, 0x31);
    }

    private static void Transform8(Span<int> d, Span<int> o)
    {
        int e0 = d[0] + d[4];
        int e1 = -d[3] + d[5] - d[7] - (d[7] >> 1);
        int e2 = d[0] - d[4];
        int e3 = d[1] + d[7] - d[3] - (d[3] >> 1);
        int e4 = (d[2] >> 1) - d[6];
        int e5 = -d[1] + d[7] + d[5] + (d[5] >> 1);
        int e6 = d[2] + (d[6] >> 1);
        int e7 = d[3] + d[5] + d[1] + (d[1] >> 1);
        int f0 = e0 + e6;
        int f1 = e1 + (e7 >> 2);
        int f2 = e2 + e4;
        int f3 = e3 + (e5 >> 2);
        int f4 = e2 - e4;
        int f5 = (e3 >> 2) - e5;
        int f6 = e0 - e6;
        int f7 = e7 - (e1 >> 2);
        o[0] = f0 + f7;
        o[1] = f2 + f5;
        o[2] = f4 + f3;
        o[3] = f6 + f1;
        o[4] = f6 - f1;
        o[5] = f4 - f3;
        o[6] = f2 - f5;
        o[7] = f0 - f7;
    }

    private static void Add<T>(T[] plane, int at, int max, int h)
        where T : unmanaged => H264Samples.Set(plane, at, Math.Clamp(H264Samples.Get(plane, at) + ((h + 32) >> 6), 0, max));
}
