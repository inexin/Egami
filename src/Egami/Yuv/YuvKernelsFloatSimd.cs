using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami;

/// <summary>
/// SIMD kernels of the float path, four pixels per vector in 32-bit lanes. Each converts as many leading pixels as it can and
/// returns the count. They do the scalar path's float operations in the same order, so the results are identical.
/// </summary>
internal static partial class YuvKernelsFloat
{
    private static readonly Vector128<byte> Drop4To3 = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> Expand3To4 = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);

    private static int ToRgbSimd<TSample, TOut>(ReadOnlySpan<TSample> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr, ReadOnlySpan<TSample> alpha, int max, Span<TOut> target, YuvConvert.Order order, in ToRgbCoefficients k)
        where TSample : unmanaged
        where TOut : unmanaged
    {
        if (!YuvSimd.IsSupported)
        {
            return 0;
        }

        int width = y.Length;
        bool words = typeof(TOut) == typeof(ushort), four = order.Size == 4, swap = order.R == 2;

        // Three-byte pixels are stored 16 bytes at a time for 12: keep two pixels of slack. (16-bit targets are always RGBA.)
        int end = four ? width - 8 : width - 10;
        ref int rcb = ref MemoryMarshal.GetReference(cb), rcr = ref MemoryMarshal.GetReference(cr);
        ref byte rd = ref Unsafe.As<TOut, byte>(ref MemoryMarshal.GetReference(target));
        Vector128<float> yOff = Vector128.Create(k.YOff), yScale = Vector128.Create(k.YScale), cOff = Vector128.Create(k.COff), cScale = Vector128.Create(k.CScale);
        Vector128<float> kr = Vector128.Create(k.Kr), kgb = Vector128.Create(k.Kgb), kgr = Vector128.Create(k.Kgr), kb = Vector128.Create(k.Kb);
        Vector128<float> half = Vector128.Create(0.5f), outMax = Vector128.Create((float)k.OutMax);
        Vector128<int> maxSample = Vector128.Create(max);
        bool identity = k.Identity, wide = Vector256.IsHardwareAccelerated;
        Vector256<float> yOff8 = Vector256.Create(k.YOff), yScale8 = Vector256.Create(k.YScale), cOff8 = Vector256.Create(k.COff), cScale8 = Vector256.Create(k.CScale);
        Vector256<float> kr8 = Vector256.Create(k.Kr), kgb8 = Vector256.Create(k.Kgb), kgr8 = Vector256.Create(k.Kgr), kb8 = Vector256.Create(k.Kb);
        Vector256<float> half8 = Vector256.Create(0.5f), outMax8 = Vector256.Create((float)k.OutMax);
        Vector256<int> maxSample8 = Vector256.Create(max);
        int x = 0;
        for (; x <= end; x += 8)
        {
            Vector128<int> r0, g0, b0, r1, g1, b1;
            if (wide)
            {
                (Vector256<int> r, Vector256<int> g, Vector256<int> b) = Pixels8(Vector256.Create(Samples(y, x), Samples(y, x + 4)), Vector256.LoadUnsafe(ref rcb, (nuint)x), Vector256.LoadUnsafe(ref rcr, (nuint)x));
                (r0, g0, b0, r1, g1, b1) = (r.GetLower(), g.GetLower(), b.GetLower(), r.GetUpper(), g.GetUpper(), b.GetUpper());
            }
            else
            {
                (r0, g0, b0) = Pixels4(Samples(y, x), Vector128.LoadUnsafe(ref rcb, (nuint)x), Vector128.LoadUnsafe(ref rcr, (nuint)x));
                (r1, g1, b1) = Pixels4(Samples(y, x + 4), Vector128.LoadUnsafe(ref rcb, (nuint)(x + 4)), Vector128.LoadUnsafe(ref rcr, (nuint)(x + 4)));
            }

            if (swap)
            {
                (r0, b0, r1, b1) = (b0, r0, b1, r1);
            }

            if (words)
            {
                Vector128<short> r = Vector128.Narrow(r0.AsUInt32(), r1.AsUInt32()).AsInt16(), g = Vector128.Narrow(g0.AsUInt32(), g1.AsUInt32()).AsInt16();
                Vector128<short> b = Vector128.Narrow(b0.AsUInt32(), b1.AsUInt32()).AsInt16(), a = Vector128.Create((ushort)k.OutMax).AsInt16();
                Vector128<int> rgLow = YuvSimd.ZipLow(r, g).AsInt32(), rgHigh = YuvSimd.ZipHigh(r, g).AsInt32();
                Vector128<int> baLow = YuvSimd.ZipLow(b, a).AsInt32(), baHigh = YuvSimd.ZipHigh(b, a).AsInt32();
                ref int ri = ref Unsafe.As<byte, int>(ref rd);
                YuvSimd.ZipLow(rgLow, baLow).StoreUnsafe(ref ri, (nuint)(2 * x));
                YuvSimd.ZipHigh(rgLow, baLow).StoreUnsafe(ref ri, (nuint)((2 * x) + 4));
                YuvSimd.ZipLow(rgHigh, baHigh).StoreUnsafe(ref ri, (nuint)((2 * x) + 8));
                YuvSimd.ZipHigh(rgHigh, baHigh).StoreUnsafe(ref ri, (nuint)((2 * x) + 12));
            }
            else
            {
                Vector128<byte> r = Bytes(r0, r1), g = Bytes(g0, g1), b = Bytes(b0, b1);
                Vector128<short> rg = YuvSimd.ZipLow(r, g).AsInt16(), ba = YuvSimd.ZipLow(b, Vector128<byte>.AllBitsSet).AsInt16();
                Vector128<byte> p0 = YuvSimd.ZipLow(rg, ba).AsByte(), p1 = YuvSimd.ZipHigh(rg, ba).AsByte();
                if (four)
                {
                    p0.StoreUnsafe(ref rd, (nuint)(4 * x));
                    p1.StoreUnsafe(ref rd, (nuint)((4 * x) + 16));
                }
                else
                {
                    YuvSimd.Shuffle(p0, Drop4To3).StoreUnsafe(ref rd, (nuint)(3 * x));
                    YuvSimd.Shuffle(p1, Drop4To3).StoreUnsafe(ref rd, (nuint)((3 * x) + 12));
                }
            }
        }

        // Alpha needs exact integer rounding to the output depth; it is rare, so it is filled in afterwards as the scalar path does.
        if (order.A >= 0 && !alpha.IsEmpty)
        {
            for (int i = 0; i < x; i++)
            {
                int value = (int)(((2L * YuvConvert.Sample(alpha, i, max) * k.OutMax) + max) / (2L * max));
                if (words)
                {
                    Unsafe.As<byte, ushort>(ref Unsafe.Add(ref rd, (2 * order.Size * i) + (2 * order.A))) = (ushort)value;
                }
                else
                {
                    Unsafe.Add(ref rd, (order.Size * i) + order.A) = (byte)value;
                }
            }
        }

        return x;

        (Vector128<int>, Vector128<int>, Vector128<int>) Pixels4(Vector128<int> samples, Vector128<int> cb16, Vector128<int> cr16)
        {
            Vector128<float> luma = (Vector128.ConvertToSingle(Vector128.Min(samples, maxSample)) - yOff) * yScale;
            Vector128<float> u = (Vector128.ConvertToSingle(cb16) - cOff) * cScale;
            Vector128<float> v = (Vector128.ConvertToSingle(cr16) - cOff) * cScale;
            Vector128<float> r, g, b;
            if (identity)
            {
                (r, g, b) = (v, luma, u);
            }
            else
            {
                r = luma + (v * kr);
                g = luma - (u * kgb) - (v * kgr);
                b = luma + (u * kb);
            }

            return (Round(r), Round(g), Round(b));
        }

        (Vector256<int>, Vector256<int>, Vector256<int>) Pixels8(Vector256<int> samples, Vector256<int> cb16, Vector256<int> cr16)
        {
            Vector256<float> luma = (Vector256.ConvertToSingle(Vector256.Min(samples, maxSample8)) - yOff8) * yScale8;
            Vector256<float> u = (Vector256.ConvertToSingle(cb16) - cOff8) * cScale8;
            Vector256<float> v = (Vector256.ConvertToSingle(cr16) - cOff8) * cScale8;
            Vector256<float> r, g, b;
            if (identity)
            {
                (r, g, b) = (v, luma, u);
            }
            else
            {
                r = luma + (v * kr8);
                g = luma - (u * kgb8) - (v * kgr8);
                b = luma + (u * kb8);
            }

            return (Round8(r), Round8(g), Round8(b));
        }

        Vector256<int> Round8(Vector256<float> value) =>
            Vector256.ConvertToInt32(Vector256.Clamp(Vector256.Floor(value + half8), Vector256<float>.Zero, outMax8));

        Vector128<int> Round(Vector128<float> value) =>
            Vector128.ConvertToInt32(Vector128.Clamp(Vector128.Floor(value + half), Vector128<float>.Zero, outMax));
    }

    private static int FromRgbSimd<TIn, TSample>(ReadOnlySpan<TIn> rgb, YuvConvert.Order order, Span<TSample> luma, Span<float> cb, Span<float> cr, int width, int max, in FromRgbCoefficients k)
        where TIn : unmanaged
        where TSample : unmanaged
    {
        if (!YuvSimd.IsSupported)
        {
            return 0;
        }

        bool words = typeof(TIn) == typeof(ushort), four = order.Size == 4, doLuma = !luma.IsEmpty, doChroma = !cb.IsEmpty;
        ref byte rs = ref Unsafe.As<TIn, byte>(ref MemoryMarshal.GetReference(rgb));
        ref float rcb = ref MemoryMarshal.GetReference(cb), rcr = ref MemoryMarshal.GetReference(cr);
        Vector128<float> yr = Vector128.Create(k.YR), yg = Vector128.Create(k.YG), yb = Vector128.Create(k.YB), yOff = Vector128.Create(k.YOff);
        Vector128<float> br = Vector128.Create(k.BR), bg = Vector128.Create(k.BG), bb = Vector128.Create(k.BB);
        Vector128<float> rr = Vector128.Create(k.RR), rg = Vector128.Create(k.RG), rb = Vector128.Create(k.RB);
        Vector128<float> half = Vector128.Create(0.5f), maxSample = Vector128.Create((float)max);
        Vector128<int> mask = Vector128.Create(0xFF);

        // Three-byte pixels are loaded 16 bytes at a time for 12: keep two pixels of slack. (16-bit sources are always RGBA.)
        int end = four ? width - 4 : width - 6;
        int x = 0;
        for (; x <= end; x += 4)
        {
            Vector128<int> ri, gi, bi;
            if (words)
            {
                // Four RGBA16 pixels, one per 32-bit vector, transposed to channels.
                (Vector128<uint> p0, Vector128<uint> p1) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.As<byte, ushort>(ref rs), (nuint)(4 * x)));
                (Vector128<uint> p2, Vector128<uint> p3) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.As<byte, ushort>(ref rs), (nuint)((4 * x) + 8)));
                Vector128<long> t0 = YuvSimd.ZipLow(p0.AsInt32(), p1.AsInt32()).AsInt64(), t1 = YuvSimd.ZipHigh(p0.AsInt32(), p1.AsInt32()).AsInt64();
                Vector128<long> t2 = YuvSimd.ZipLow(p2.AsInt32(), p3.AsInt32()).AsInt64(), t3 = YuvSimd.ZipHigh(p2.AsInt32(), p3.AsInt32()).AsInt64();
                Vector128<int> c0 = YuvSimd.ZipLow(t0, t2).AsInt32(), c1 = YuvSimd.ZipHigh(t0, t2).AsInt32(), c2 = YuvSimd.ZipLow(t1, t3).AsInt32();
                (ri, gi, bi) = order.R == 0 ? (c0, c1, c2) : (c2, c1, c0);
            }
            else
            {
                Vector128<int> p = four
                    ? Vector128.LoadUnsafe(ref rs, (nuint)(4 * x)).AsInt32()
                    : YuvSimd.Shuffle(Vector128.LoadUnsafe(ref rs, (nuint)(3 * x)), Expand3To4).AsInt32();
                ri = (p >>> (8 * order.R)) & mask;
                gi = (p >>> (8 * order.G)) & mask;
                bi = (p >>> (8 * order.B)) & mask;
            }

            Vector128<float> r = Vector128.ConvertToSingle(ri), g = Vector128.ConvertToSingle(gi), b = Vector128.ConvertToSingle(bi);
            if (doLuma)
            {
                Vector128<int> l = Vector128.ConvertToInt32(Vector128.Clamp(Vector128.Floor((r * yr) + (g * yg) + (b * yb) + yOff + half), Vector128<float>.Zero, maxSample));
                StoreSamples(luma, x, l);
            }

            if (doChroma)
            {
                ((r * br) + (g * bg) + (b * bb)).StoreUnsafe(ref rcb, (nuint)x);
                ((r * rr) + (g * rg) + (b * rb)).StoreUnsafe(ref rcr, (nuint)x);
            }
        }

        return x;
    }

    /// <summary>Four 32-bit samples from a row of bytes or 16-bit words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Samples<TSample>(ReadOnlySpan<TSample> row, int at)
        where TSample : unmanaged
    {
        ref byte start = ref Unsafe.As<TSample, byte>(ref Unsafe.Add(ref MemoryMarshal.GetReference(row), at));
        if (typeof(TSample) == typeof(byte))
        {
            Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref start)).AsByte();
            return Vector128.WidenLower(Vector128.WidenLower(bytes)).AsInt32();
        }

        return Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref start)).AsUInt16()).AsInt32();
    }

    /// <summary>Stores four 32-bit values, already in range, as samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreSamples<TSample>(Span<TSample> row, int at, Vector128<int> values)
        where TSample : unmanaged
    {
        ref byte start = ref Unsafe.As<TSample, byte>(ref Unsafe.Add(ref MemoryMarshal.GetReference(row), at));
        Vector128<ushort> words = Vector128.Narrow(values.AsUInt32(), values.AsUInt32());
        if (typeof(TSample) == typeof(byte))
        {
            Unsafe.WriteUnaligned(ref start, Vector128.Narrow(words, words).AsUInt32().ToScalar());
        }
        else
        {
            Unsafe.WriteUnaligned(ref start, words.AsUInt64().ToScalar());
        }
    }

    /// <summary>Narrows eight 32-bit values (0..255) to the first eight bytes of a vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Bytes(Vector128<int> low, Vector128<int> high)
    {
        Vector128<ushort> words = Vector128.Narrow(low.AsUInt32(), high.AsUInt32());
        return Vector128.Narrow(words, words);
    }
}
