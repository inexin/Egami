using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami;

/// <summary>
/// The 128-bit operations the YUV kernels need beyond the portable vector API: rounding high multiply, saturating 16-bit
/// arithmetic, saturating narrowing and interleaving. x86 (SSSE3) and Arm64 have them all; elsewhere the kernels do nothing and
/// the scalar path runs.
/// </summary>
internal static class YuvSimd
{
    /// <summary>Gets a value indicating whether the operations are hardware accelerated and SIMD is not turned off.</summary>
    internal static bool IsSupported => (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && Vector128.IsHardwareAccelerated;

    /// <summary><c>(a × b + 2^14) &gt;&gt; 15</c>, per 16-bit lane: <c>pmulhrsw</c> and <c>sqrdmulh</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> MulHrs(Vector128<short> a, Vector128<short> b) =>
        Ssse3.IsSupported ? Ssse3.MultiplyHighRoundScale(a, b) : AdvSimd.MultiplyRoundedDoublingSaturateHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> AddSaturate(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.AddSaturate(a, b) : AdvSimd.AddSaturate(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> SubtractSaturate(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.SubtractSaturate(a, b) : AdvSimd.SubtractSaturate(a, b);

    /// <summary>Narrows two vectors of 16-bit lanes to bytes, clamping to 0..255; <paramref name="lower"/> fills the first half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> PackUnsigned(Vector128<short> lower, Vector128<short> upper) =>
        Sse2.IsSupported ? Sse2.PackUnsignedSaturate(lower, upper)
            : AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(lower), upper);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> ZipLow(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> ZipHigh(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> ZipLow(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> ZipHigh(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ZipLow(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ZipHigh(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<long> ZipLow(Vector128<long> a, Vector128<long> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<long> ZipHigh(Vector128<long> a, Vector128<long> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> indices) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(value, indices) : AdvSimd.Arm64.VectorTableLookup(value, indices);
}

/// <summary>SIMD kernels of the 8-bit fixed-point path. Each converts as many leading pixels as it can and returns the count.</summary>
internal static partial class YuvKernels8
{
    /// <summary>Keeps bytes 0-2 of each pixel: four RGBA pixels to twelve RGB bytes and four zeros.</summary>
    private static readonly Vector128<byte> Drop4To3 = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);

    /// <summary>Spreads twelve RGB bytes to four 32-bit lanes with zero high bytes.</summary>
    private static readonly Vector128<byte> Expand3To4 = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);

    /// <summary>
    /// Weights two rows of chroma samples (summing to 4) into <paramref name="t"/>, at 4 times the sample scale. With
    /// <paramref name="w1"/> 0, <paramref name="b"/> is ignored.
    /// </summary>
    internal static void UpsampleVertical(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int w0, int w1, Span<short> t)
    {
        int i = 0;
        if (YuvSimd.IsSupported)
        {
            Vector128<short> v0 = Vector128.Create((short)w0), v1 = Vector128.Create((short)w1);
            ref byte ra = ref MemoryMarshal.GetReference(a), rb = ref MemoryMarshal.GetReference(b);
            ref short rt = ref MemoryMarshal.GetReference(t);
            for (; i <= t.Length - 16; i += 16)
            {
                (Vector128<ushort> alo, Vector128<ushort> ahi) = Vector128.Widen(Vector128.LoadUnsafe(ref ra, (nuint)i));
                (Vector128<ushort> blo, Vector128<ushort> bhi) = Vector128.Widen(Vector128.LoadUnsafe(ref rb, (nuint)i));
                ((alo.AsInt16() * v0) + (blo.AsInt16() * v1)).StoreUnsafe(ref rt, (nuint)i);
                ((ahi.AsInt16() * v0) + (bhi.AsInt16() * v1)).StoreUnsafe(ref rt, (nuint)(i + 8));
            }
        }

        for (; i < t.Length; i++)
        {
            t[i] = (short)((w0 * a[i]) + (w1 * b[i]));
        }
    }

    /// <summary>
    /// Upsamples one row of vertically weighted chroma (4 times the sample scale) to <paramref name="target"/> (16 times, luma
    /// width), by the siting's bilinear weights or the nearest sample: the rule of <see cref="YuvConvert.Upsample.Horizontal"/>.
    /// </summary>
    internal static void UpsampleHorizontal(ReadOnlySpan<short> t, Span<short> target, ChromaFormat format, ChromaSiting siting, ChromaUpsampling mode)
    {
        int width = target.Length;
        ref short rt = ref MemoryMarshal.GetReference(t);
        ref short ro = ref MemoryMarshal.GetReference(target);
        if (format == ChromaFormat.Yuv444)
        {
            int x = 0;
            if (YuvSimd.IsSupported)
            {
                for (; x <= width - 8; x += 8)
                {
                    (Vector128.LoadUnsafe(ref rt, (nuint)x) << 2).StoreUnsafe(ref ro, (nuint)x);
                }
            }

            for (; x < width; x++)
            {
                target[x] = (short)(t[x] << 2);
            }

            return;
        }

        int last = t.Length - 1;
        int kind = mode == ChromaUpsampling.Nearest ? 0 : siting == ChromaSiting.Center ? 1 : 2;

        // Chroma samples 1 .. n - 2 have both neighbors, so their two outputs are vectorized; the first and last, and any
        // remainder, go through the scalar rule.
        int i = 1;
        if (YuvSimd.IsSupported)
        {
            Vector128<short> three = Vector128.Create((short)3);
            for (; i + 8 <= last; i += 8)
            {
                Vector128<short> mid = Vector128.LoadUnsafe(ref rt, (nuint)i);
                Vector128<short> even, odd;
                if (kind == 1)
                {
                    Vector128<short> mid3 = mid * three;
                    even = mid3 + Vector128.LoadUnsafe(ref rt, (nuint)(i - 1));
                    odd = mid3 + Vector128.LoadUnsafe(ref rt, (nuint)(i + 1));
                }
                else if (kind == 2)
                {
                    even = mid << 2;
                    odd = (mid + Vector128.LoadUnsafe(ref rt, (nuint)(i + 1))) << 1;
                }
                else
                {
                    even = odd = mid << 2;
                }

                YuvSimd.ZipLow(even, odd).StoreUnsafe(ref ro, (nuint)(2 * i));
                YuvSimd.ZipHigh(even, odd).StoreUnsafe(ref ro, (nuint)((2 * i) + 8));
            }
        }

        Scalar(t, target, 0, Math.Min(1, t.Length), kind, last);
        Scalar(t, target, i, t.Length, kind, last);

        static void Scalar(ReadOnlySpan<short> t, Span<short> target, int from, int to, int kind, int last)
        {
            for (int i = from; i < to; i++)
            {
                int x = 2 * i;
                int even, odd;
                if (kind == 1)
                {
                    even = (3 * t[i]) + t[Math.Max(i - 1, 0)];
                    odd = (3 * t[i]) + t[Math.Min(i + 1, last)];
                }
                else if (kind == 2)
                {
                    even = 4 * t[i];
                    odd = (2 * t[i]) + (2 * t[Math.Min(i + 1, last)]);
                }
                else
                {
                    even = odd = 4 * t[i];
                }

                target[x] = (short)even;
                if (x + 1 < target.Length)
                {
                    target[x + 1] = (short)odd;
                }
            }
        }
    }

    private static int ToRgbSimd(ReadOnlySpan<byte> y, ReadOnlySpan<short> cb, ReadOnlySpan<short> cr, ReadOnlySpan<byte> alpha, Span<byte> target, YuvConvert.Order order, in ToRgbCoefficients k)
    {
        if (!YuvSimd.IsSupported || k.Identity)
        {
            return 0;
        }

        int width = y.Length;
        bool four = order.Size == 4, swap = order.R == 2;

        // Three-byte pixels are stored 16 bytes at a time, 4 past the end of the last pixel written: keep two pixels of slack.
        int end = four ? width - 16 : width - 18;
        ref byte ry = ref MemoryMarshal.GetReference(y);
        ref short rcb = ref MemoryMarshal.GetReference(cb), rcr = ref MemoryMarshal.GetReference(cr);
        ref byte ra = ref MemoryMarshal.GetReference(alpha);
        ref byte rd = ref MemoryMarshal.GetReference(target);
        bool hasAlpha = !alpha.IsEmpty;
        Vector128<short> ky = Vector128.Create(k.Ky), kr = Vector128.Create(k.Kr), kgb = Vector128.Create(k.Kgb), kgr = Vector128.Create(k.Kgr), kb = Vector128.Create(k.Kb);
        Vector128<short> sixteen = Vector128.Create((short)16), round = Vector128.Create((short)32), center = Vector128.Create((short)2048);
        Vector128<byte> opaque = Vector128<byte>.AllBitsSet;
        bool full = k.LumaFull;
        int x = 0;
        for (; x <= end; x += 16)
        {
            (Vector128<ushort> ylo, Vector128<ushort> yhi) = Vector128.Widen(Vector128.LoadUnsafe(ref ry, (nuint)x));
            (Vector128<short> r0, Vector128<short> g0, Vector128<short> b0) = Pixels8(ylo.AsInt16(), Vector128.LoadUnsafe(ref rcb, (nuint)x), Vector128.LoadUnsafe(ref rcr, (nuint)x));
            (Vector128<short> r1, Vector128<short> g1, Vector128<short> b1) = Pixels8(yhi.AsInt16(), Vector128.LoadUnsafe(ref rcb, (nuint)(x + 8)), Vector128.LoadUnsafe(ref rcr, (nuint)(x + 8)));
            Vector128<byte> r = YuvSimd.PackUnsigned(Vector128.ShiftRightArithmetic(r0, 6), Vector128.ShiftRightArithmetic(r1, 6));
            Vector128<byte> g = YuvSimd.PackUnsigned(Vector128.ShiftRightArithmetic(g0, 6), Vector128.ShiftRightArithmetic(g1, 6));
            Vector128<byte> b = YuvSimd.PackUnsigned(Vector128.ShiftRightArithmetic(b0, 6), Vector128.ShiftRightArithmetic(b1, 6));
            if (swap)
            {
                (r, b) = (b, r);
            }

            Vector128<byte> a = hasAlpha ? Vector128.LoadUnsafe(ref ra, (nuint)x) : opaque;
            Vector128<short> rgLow = YuvSimd.ZipLow(r, g).AsInt16(), rgHigh = YuvSimd.ZipHigh(r, g).AsInt16();
            Vector128<short> baLow = YuvSimd.ZipLow(b, a).AsInt16(), baHigh = YuvSimd.ZipHigh(b, a).AsInt16();
            Vector128<byte> p0 = YuvSimd.ZipLow(rgLow, baLow).AsByte(), p1 = YuvSimd.ZipHigh(rgLow, baLow).AsByte();
            Vector128<byte> p2 = YuvSimd.ZipLow(rgHigh, baHigh).AsByte(), p3 = YuvSimd.ZipHigh(rgHigh, baHigh).AsByte();
            if (four)
            {
                p0.StoreUnsafe(ref rd, (nuint)(4 * x));
                p1.StoreUnsafe(ref rd, (nuint)((4 * x) + 16));
                p2.StoreUnsafe(ref rd, (nuint)((4 * x) + 32));
                p3.StoreUnsafe(ref rd, (nuint)((4 * x) + 48));
            }
            else
            {
                YuvSimd.Shuffle(p0, Drop4To3).StoreUnsafe(ref rd, (nuint)(3 * x));
                YuvSimd.Shuffle(p1, Drop4To3).StoreUnsafe(ref rd, (nuint)((3 * x) + 12));
                YuvSimd.Shuffle(p2, Drop4To3).StoreUnsafe(ref rd, (nuint)((3 * x) + 24));
                YuvSimd.Shuffle(p3, Drop4To3).StoreUnsafe(ref rd, (nuint)((3 * x) + 36));
            }
        }

        return x;

        // The arithmetic of ToRgbScalar on eight pixels, in 1/64 of a level.
        (Vector128<short>, Vector128<short>, Vector128<short>) Pixels8(Vector128<short> luma, Vector128<short> u16, Vector128<short> v16)
        {
            Vector128<short> l = (full ? luma << 6 : YuvSimd.MulHrs((luma - sixteen) << 7, ky)) + round;
            Vector128<short> u = (u16 - center) << 4, v = (v16 - center) << 4;
            Vector128<short> red = YuvSimd.AddSaturate(l, YuvSimd.MulHrs(v, kr));
            Vector128<short> green = YuvSimd.SubtractSaturate(YuvSimd.SubtractSaturate(l, YuvSimd.MulHrs(u, kgb)), YuvSimd.MulHrs(v, kgr));
            Vector128<short> blue = YuvSimd.AddSaturate(l, YuvSimd.MulHrs(u, kb));
            return (red, green, blue);
        }
    }

    private static int FromRgbSimd(ReadOnlySpan<byte> rgb, YuvConvert.Order order, Span<byte> luma, Span<int> cb, Span<int> cr, int width, in FromRgbCoefficients k)
    {
        if (!YuvSimd.IsSupported)
        {
            return 0;
        }

        bool four = order.Size == 4, doLuma = !luma.IsEmpty, doChroma = !cb.IsEmpty;
        int rShift = 8 * order.R, gShift = 8 * order.G, bShift = 8 * order.B;
        ref byte rs = ref MemoryMarshal.GetReference(rgb);
        ref byte rl = ref MemoryMarshal.GetReference(luma);
        ref int rcb = ref MemoryMarshal.GetReference(cb), rcr = ref MemoryMarshal.GetReference(cr);
        Vector128<int> mask = Vector128.Create(0xFF);
        Vector128<int> yr = Vector128.Create(k.YR), yg = Vector128.Create(k.YG), yb = Vector128.Create(k.YB), bias = Vector128.Create(k.YBias);
        Vector128<int> br = Vector128.Create(k.BR), bg = Vector128.Create(k.BG), bb = Vector128.Create(k.BB);
        Vector128<int> rr = Vector128.Create(k.RR), rg = Vector128.Create(k.RG), rb = Vector128.Create(k.RB);

        int x = 0;
        if (four && Vector256.IsHardwareAccelerated)
        {
            // Eight four-byte pixels per 256-bit vector: the same arithmetic in wider lanes.
            Vector256<int> mask8 = Vector256.Create(0xFF), top = Vector256.Create(255);
            Vector256<int> yr8 = Vector256.Create(k.YR), yg8 = Vector256.Create(k.YG), yb8 = Vector256.Create(k.YB), bias8 = Vector256.Create(k.YBias);
            Vector256<int> br8 = Vector256.Create(k.BR), bg8 = Vector256.Create(k.BG), bb8 = Vector256.Create(k.BB);
            Vector256<int> rr8 = Vector256.Create(k.RR), rg8 = Vector256.Create(k.RG), rb8 = Vector256.Create(k.RB);
            for (; x <= width - 8; x += 8)
            {
                Vector256<int> p = Vector256.LoadUnsafe(ref rs, (nuint)(4 * x)).AsInt32();
                Vector256<int> r = (p >>> rShift) & mask8, g = (p >>> gShift) & mask8, b = (p >>> bShift) & mask8;
                if (doLuma)
                {
                    Vector256<int> l = Vector256.Clamp(Vector256.ShiftRightArithmetic((r * yr8) + (g * yg8) + (b * yb8) + bias8, 15), Vector256<int>.Zero, top);
                    Vector128<ushort> words = Vector256.Narrow(l.AsUInt32(), l.AsUInt32()).GetLower();
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref rl, x), Vector128.Narrow(words, words).AsUInt64().ToScalar());
                }

                if (doChroma)
                {
                    ((r * br8) + (g * bg8) + (b * bb8)).StoreUnsafe(ref rcb, (nuint)x);
                    ((r * rr8) + (g * rg8) + (b * rb8)).StoreUnsafe(ref rcr, (nuint)x);
                }
            }
        }

        // Three-byte pixels are loaded 16 bytes at a time for 12: keep two pixels of slack.
        int end = four ? width - 8 : width - 10;
        for (; x <= end; x += 8)
        {
            Vector128<int> p0, p1;
            if (four)
            {
                p0 = Vector128.LoadUnsafe(ref rs, (nuint)(4 * x)).AsInt32();
                p1 = Vector128.LoadUnsafe(ref rs, (nuint)((4 * x) + 16)).AsInt32();
            }
            else
            {
                p0 = YuvSimd.Shuffle(Vector128.LoadUnsafe(ref rs, (nuint)(3 * x)), Expand3To4).AsInt32();
                p1 = YuvSimd.Shuffle(Vector128.LoadUnsafe(ref rs, (nuint)((3 * x) + 12)), Expand3To4).AsInt32();
            }

            Vector128<int> r0 = (p0 >>> rShift) & mask, g0 = (p0 >>> gShift) & mask, b0 = (p0 >>> bShift) & mask;
            Vector128<int> r1 = (p1 >>> rShift) & mask, g1 = (p1 >>> gShift) & mask, b1 = (p1 >>> bShift) & mask;
            if (doLuma)
            {
                Vector128<int> l0 = Vector128.ShiftRightArithmetic((r0 * yr) + (g0 * yg) + (b0 * yb) + bias, 15);
                Vector128<int> l1 = Vector128.ShiftRightArithmetic((r1 * yr) + (g1 * yg) + (b1 * yb) + bias, 15);
                Vector128<ushort> words = Vector128.Narrow(Vector128.Clamp(l0, Vector128<int>.Zero, mask), Vector128.Clamp(l1, Vector128<int>.Zero, mask)).AsUInt16();
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref rl, x), Vector128.Narrow(words, words).AsUInt64().ToScalar());
            }

            if (doChroma)
            {
                ((r0 * br) + (g0 * bg) + (b0 * bb)).StoreUnsafe(ref rcb, (nuint)x);
                ((r1 * br) + (g1 * bg) + (b1 * bb)).StoreUnsafe(ref rcb, (nuint)(x + 4));
                ((r0 * rr) + (g0 * rg) + (b0 * rb)).StoreUnsafe(ref rcr, (nuint)x);
                ((r1 * rr) + (g1 * rg) + (b1 * rb)).StoreUnsafe(ref rcr, (nuint)(x + 4));
            }
        }

        return x;
    }
}
