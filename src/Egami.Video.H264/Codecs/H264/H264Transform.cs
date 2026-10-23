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

        if (Vector128.IsHardwareAccelerated && Sse2.IsSupported && H264Inter.UseVectors)
        {
            Add4x4Vector(d, plane, at, stride, max);
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
