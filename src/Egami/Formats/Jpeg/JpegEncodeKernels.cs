using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The encoder's per-pixel kernels: RGB to YCbCr (or luma) in libjpeg's fixed point, and chroma averaging with its alternating
/// rounding bias. Each has a scalar form and an AVX2 form that gives exactly the same bytes.
/// </summary>
internal static class JpegEncodeKernels
{
    private const int ScaleBits = 16;
    private const int Half = 1 << (ScaleBits - 1);
    private const int CbCrOffset = 128 << ScaleBits;

    // libjpeg's fixed-point RGB to YCbCr coefficients (JFIF, full range BT.601).
    private static readonly int RY = Fix(0.29900);
    private static readonly int GY = Fix(0.58700);
    private static readonly int BY = Fix(0.11400);
    private static readonly int RCb = -Fix(0.16874);
    private static readonly int GCb = -Fix(0.33126);
    private static readonly int BCbRCr = Fix(0.5);
    private static readonly int GCr = -Fix(0.41869);
    private static readonly int BCr = -Fix(0.08131);

    /// <summary>Converts packed RGB to luma and chroma rows, as wide as <paramref name="y"/>.</summary>
    internal static void YCbCr(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr)
    {
        int done = Avx2.IsSupported ? YCbCrAvx2(rgb, y, cb, cr, lumaOnly: false) : 0;
        YCbCrScalar(rgb, 3, y, cb, cr, done);
    }

    /// <summary>Converts packed RGBA to luma and chroma rows, as wide as <paramref name="y"/>, ignoring the alpha.</summary>
    internal static void YCbCrFromRgba(ReadOnlySpan<byte> rgba, Span<byte> y, Span<byte> cb, Span<byte> cr)
    {
        int done = Avx2.IsSupported ? YCbCrFromRgbaAvx2(rgba, y, cb, cr, lumaOnly: false) : 0;
        YCbCrScalar(rgba, 4, y, cb, cr, done);
    }

    /// <summary>Converts packed RGB to luma alone, as <c>cjpeg -grayscale</c> does.</summary>
    internal static void Luma(ReadOnlySpan<byte> rgb, Span<byte> y)
    {
        int done = Avx2.IsSupported ? YCbCrAvx2(rgb, y, default, default, lumaOnly: true) : 0;
        LumaScalar(rgb, 3, y, done);
    }

    /// <summary>Converts packed RGBA to luma alone, ignoring the alpha.</summary>
    internal static void LumaFromRgba(ReadOnlySpan<byte> rgba, Span<byte> y)
    {
        int done = Avx2.IsSupported ? YCbCrFromRgbaAvx2(rgba, y, default, default, lumaOnly: true) : 0;
        LumaScalar(rgba, 4, y, done);
    }

    /// <summary>The scalar conversion from pixel <paramref name="start"/> on.</summary>
    internal static void YCbCrScalar(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int start)
    {
        YCbCrScalar(rgb, 3, y, cb, cr, start);
    }

    private static void YCbCrScalar(ReadOnlySpan<byte> pixels, int size, Span<byte> y, Span<byte> cb, Span<byte> cr, int start)
    {
        for (int x = start; x < y.Length; x++)
        {
            int red = pixels[size * x], green = pixels[(size * x) + 1], blue = pixels[(size * x) + 2];
            y[x] = (byte)(((RY * red) + (GY * green) + (BY * blue) + Half) >> ScaleBits);
            cb[x] = (byte)(((RCb * red) + (GCb * green) + (BCbRCr * blue) + CbCrOffset + Half - 1) >> ScaleBits);
            cr[x] = (byte)(((BCbRCr * red) + (GCr * green) + (BCr * blue) + CbCrOffset + Half - 1) >> ScaleBits);
        }
    }

    private static void LumaScalar(ReadOnlySpan<byte> pixels, int size, Span<byte> y, int start)
    {
        for (int x = start; x < y.Length; x++)
        {
            y[x] = (byte)(((RY * pixels[size * x]) + (GY * pixels[(size * x) + 1]) + (BY * pixels[(size * x) + 2]) + Half) >> ScaleBits);
        }
    }

    /// <summary>
    /// Converts 16 pixels at a time: the RGB bytes are split into planes with byte shuffles and widened to 16 bits, then each output
    /// is two multiply-adds of interleaved pairs per 8 pixels, summed in 32-bit lanes. A factor of 0.5 or more does not fit 16 bits,
    /// so it is split across both pairs (G's luma factor and the chroma ones of 0.5), which keeps each sum exactly the scalar
    /// expression. Returns how many pixels it did.
    /// </summary>
    internal static int YCbCrAvx2(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, bool lumaOnly)
    {
        return YCbCrAvx2<Rgb>(rgb, y, cb, cr, lumaOnly);
    }

    /// <summary><see cref="YCbCrAvx2(ReadOnlySpan{byte}, Span{byte}, Span{byte}, Span{byte}, bool)"/> from packed RGBA.</summary>
    internal static int YCbCrFromRgbaAvx2(ReadOnlySpan<byte> rgba, Span<byte> y, Span<byte> cb, Span<byte> cr, bool lumaOnly)
    {
        return YCbCrAvx2<Rgba>(rgba, y, cb, cr, lumaOnly);
    }

    /// <summary>The conversion for one pixel layout; generic so that each layout compiles to its own loop.</summary>
    private static int YCbCrAvx2<TLayout>(ReadOnlySpan<byte> pixels, Span<byte> y, Span<byte> cb, Span<byte> cr, bool lumaOnly)
        where TLayout : struct, ILayout
    {
        int size = TLayout.Size;
        int width = y.Length;
        ref byte source = ref MemoryMarshal.GetReference(pixels);
        Vector128<byte> r0 = Vector128.Create((byte)0, 3, 6, 9, 12, 15, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128);
        Vector128<byte> r1 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 2, 5, 8, 11, 14, 128, 128, 128, 128, 128);
        Vector128<byte> r2 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 1, 4, 7, 10, 13);
        Vector128<byte> g0 = Vector128.Create((byte)1, 4, 7, 10, 13, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128);
        Vector128<byte> g1 = Vector128.Create((byte)128, 128, 128, 128, 128, 0, 3, 6, 9, 12, 15, 128, 128, 128, 128, 128);
        Vector128<byte> g2 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 2, 5, 8, 11, 14);
        Vector128<byte> b0 = Vector128.Create((byte)2, 5, 8, 11, 14, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128);
        Vector128<byte> b1 = Vector128.Create((byte)128, 128, 128, 128, 128, 1, 4, 7, 10, 13, 128, 128, 128, 128, 128, 128);
        Vector128<byte> b2 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 0, 3, 6, 9, 12, 15);

        // RGBA: within each 128-bit lane, the four pixels' R, then G, B and A bytes; then 32-bit groups in plane order.
        Vector256<byte> planar = Vector256.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);
        Vector256<int> order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);

        // Y = R·RY + G·(GY − 2^14) + B·BY + G·2^14; Cb = R·RCb + G·GCb + B·0.5 in two halves; Cr = R·0.5 in two halves + G·GCr + B·BCr.
        int halfOfHalf = BCbRCr / 2;
        Vector256<short> yRg = Pair(RY, GY - (1 << 14)), yBg = Pair(BY, 1 << 14);
        Vector256<short> cbRg = Pair(RCb, GCb), cbBb = Pair(halfOfHalf, BCbRCr - halfOfHalf);
        Vector256<short> crRg = Pair(halfOfHalf, GCr), crBr = Pair(BCr, BCbRCr - halfOfHalf);
        Vector256<int> yAdd = Vector256.Create(Half), cAdd = Vector256.Create(CbCrOffset + Half - 1);
        int x = 0;
        for (; x + 16 <= width && (size * x) + (16 * size) <= pixels.Length && (lumaOnly || (x + 16 <= cb.Length && x + 16 <= cr.Length)); x += 16)
        {
            Vector256<short> red, green, blue;
            if (TLayout.Size == 4)
            {
                // Pixels 0-7 and 8-15 as R | G | B | A groups of 8 bytes per 128-bit lane pair, then R and B, and G, side by side.
                Vector256<ulong> first = Avx2.PermuteVar8x32(Avx2.Shuffle(Vector256.LoadUnsafe(ref source, (nuint)(4 * x)), planar).AsInt32(), order).AsUInt64();
                Vector256<ulong> second = Avx2.PermuteVar8x32(Avx2.Shuffle(Vector256.LoadUnsafe(ref source, (nuint)((4 * x) + 32)), planar).AsInt32(), order).AsUInt64();
                Vector256<byte> redBlue = Avx2.UnpackLow(first, second).AsByte();
                red = Avx2.ConvertToVector256Int16(redBlue.GetLower());
                blue = Avx2.ConvertToVector256Int16(redBlue.GetUpper());
                green = Avx2.ConvertToVector256Int16(Avx2.UnpackHigh(first, second).AsByte().GetLower());
            }
            else
            {
                Vector128<byte> v0 = Vector128.LoadUnsafe(ref source, (nuint)(3 * x));
                Vector128<byte> v1 = Vector128.LoadUnsafe(ref source, (nuint)((3 * x) + 16));
                Vector128<byte> v2 = Vector128.LoadUnsafe(ref source, (nuint)((3 * x) + 32));
                red = Avx2.ConvertToVector256Int16(Ssse3.Shuffle(v0, r0) | Ssse3.Shuffle(v1, r1) | Ssse3.Shuffle(v2, r2));
                green = Avx2.ConvertToVector256Int16(Ssse3.Shuffle(v0, g0) | Ssse3.Shuffle(v1, g1) | Ssse3.Shuffle(v2, g2));
                blue = Avx2.ConvertToVector256Int16(Ssse3.Shuffle(v0, b0) | Ssse3.Shuffle(v1, b1) | Ssse3.Shuffle(v2, b2));
            }

            // Pixels 0-3 and 8-11 in the low unpacks, 4-7 and 12-15 in the high ones; the packs put them back in order.
            Vector256<short> rgLow = Avx2.UnpackLow(red, green), rgHigh = Avx2.UnpackHigh(red, green);
            Vector256<short> luma = Channel(rgLow, rgHigh, yRg, Avx2.UnpackLow(blue, green), Avx2.UnpackHigh(blue, green), yBg, yAdd);
            if (lumaOnly)
            {
                Avx2.Permute4x64(Avx2.PackUnsignedSaturate(luma, luma).AsUInt64(), 0b00_00_10_00).AsByte().GetLower().StoreUnsafe(ref MemoryMarshal.GetReference(y), (nuint)x);
                continue;
            }

            Vector256<short> blueCb = Channel(rgLow, rgHigh, cbRg, Avx2.UnpackLow(blue, blue), Avx2.UnpackHigh(blue, blue), cbBb, cAdd);
            Vector256<short> redCr = Channel(rgLow, rgHigh, crRg, Avx2.UnpackLow(blue, red), Avx2.UnpackHigh(blue, red), crBr, cAdd);
            Vector256<byte> lumaCb = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(luma, blueCb).AsUInt64(), 0b11_01_10_00).AsByte();
            lumaCb.GetLower().StoreUnsafe(ref MemoryMarshal.GetReference(y), (nuint)x);
            lumaCb.GetUpper().StoreUnsafe(ref MemoryMarshal.GetReference(cb), (nuint)x);
            Avx2.Permute4x64(Avx2.PackUnsignedSaturate(redCr, redCr).AsUInt64(), 0b00_00_10_00).AsByte().GetLower().StoreUnsafe(ref MemoryMarshal.GetReference(cr), (nuint)x);
        }

        return x;
    }

    /// <summary>The bytes per pixel of a packed layout the colour conversion reads.</summary>
    private interface ILayout
    {
        static abstract int Size { get; }
    }

    private readonly struct Rgb : ILayout
    {
        public static int Size => 3;
    }

    private readonly struct Rgba : ILayout
    {
        public static int Size => 4;
    }

    /// <summary>Averages full-size rows down: 2×2 (bias 1, 2, 1, 2, …) or 2×1 (bias 0, 1, …), into <paramref name="output"/>.</summary>
    internal static void Downsample(ReadOnlySpan<byte> row0, ReadOnlySpan<byte> row1, bool twoRows, Span<byte> output)
    {
        int done = Avx2.IsSupported ? DownsampleAvx2(row0, row1, twoRows, output) : 0;
        DownsampleScalar(row0, row1, twoRows, output, done);
    }

    /// <summary>The scalar averaging from output sample <paramref name="start"/> (even) on.</summary>
    internal static void DownsampleScalar(ReadOnlySpan<byte> row0, ReadOnlySpan<byte> row1, bool twoRows, Span<byte> output, int start)
    {
        if (twoRows)
        {
            for (int x = start, bias = 1; x < output.Length; x++, bias ^= 3)
            {
                output[x] = (byte)((row0[2 * x] + row0[(2 * x) + 1] + row1[2 * x] + row1[(2 * x) + 1] + bias) >> 2);
            }
        }
        else
        {
            for (int x = start, bias = 0; x < output.Length; x++, bias ^= 1)
            {
                output[x] = (byte)((row0[2 * x] + row0[(2 * x) + 1] + bias) >> 1);
            }
        }
    }

    /// <summary>Averages 16 outputs at a time from 32 input columns; returns how many it did.</summary>
    internal static int DownsampleAvx2(ReadOnlySpan<byte> row0, ReadOnlySpan<byte> row1, bool twoRows, Span<byte> output)
    {
        ref byte a = ref MemoryMarshal.GetReference(row0);
        ref byte b = ref MemoryMarshal.GetReference(row1);
        ref byte o = ref MemoryMarshal.GetReference(output);
        Vector256<sbyte> ones = Vector256.Create((sbyte)1);
        Vector256<short> bias = twoRows
            ? Vector256.Create((short)1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2)
            : Vector256.Create((short)0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1);
        int x = 0;
        for (; x + 16 <= output.Length && (2 * x) + 32 <= row0.Length && (!twoRows || (2 * x) + 32 <= row1.Length); x += 16)
        {
            // Sums of adjacent pairs, as 16-bit lanes.
            Vector256<short> sum = Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref a, (nuint)(2 * x)), ones);
            if (twoRows)
            {
                sum += Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref b, (nuint)(2 * x)), ones);
            }

            Vector256<short> average = Vector256.ShiftRightLogical(sum + bias, twoRows ? 2 : 1);
            Vector256<byte> packed = Avx2.PackUnsignedSaturate(average, average);
            Avx2.Permute4x64(packed.AsUInt64(), 0b00_00_10_00).GetLower().AsByte().StoreUnsafe(ref o, (nuint)x);
        }

        return x;
    }

    /// <summary>
    /// One output channel of 16 pixels from two sets of interleaved pairs: (pairs · factors summed + <paramref name="add"/>) &gt;&gt; 16,
    /// as 16-bit lanes in pixel order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Channel(
        Vector256<short> low1, Vector256<short> high1, Vector256<short> factors1,
        Vector256<short> low2, Vector256<short> high2, Vector256<short> factors2, Vector256<int> add)
    {
        Vector256<int> low = Avx2.MultiplyAddAdjacent(low1, factors1) + Avx2.MultiplyAddAdjacent(low2, factors2) + add;
        Vector256<int> high = Avx2.MultiplyAddAdjacent(high1, factors1) + Avx2.MultiplyAddAdjacent(high2, factors2) + add;
        return Avx2.PackSignedSaturate(low >> ScaleBits, high >> ScaleBits);
    }

    /// <summary>Two 16-bit factors for the first and second value of interleaved pairs.</summary>
    private static Vector256<short> Pair(int first, int second)
    {
        return Vector256.Create((int)(ushort)first | (second << 16)).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Vector128<byte> values, Span<byte> target, int x)
    {
        values.StoreUnsafe(ref MemoryMarshal.GetReference(target), (nuint)x);
    }

    /// <summary>Gets ceil-ish reciprocals: (n × Reciprocal[d]) &gt;&gt; 32 equals n / d for every n below 2^16 and d up to 255.</summary>
    private static readonly ulong[] Reciprocal = [.. Enumerable.Range(0, 256).Select(d => d == 0 ? 0UL : (1UL << 32) / (ulong)d + 1)];

    /// <summary>
    /// Converts packed RGB to packed inks exactly as <see cref="Pixels.Cmyk8.FromRgba8"/> does (K = 255 − max, each ink rounded
    /// (max − channel) × 255 / max), with the division done by a reciprocal multiply.
    /// </summary>
    internal static void InksFromRgb(ReadOnlySpan<byte> rgb, Span<byte> cmyk)
    {
        int width = cmyk.Length / 4;
        _ = rgb[(3 * width) - 1];
        ref byte source = ref MemoryMarshal.GetReference(rgb);
        ref byte target = ref MemoryMarshal.GetReference(cmyk);
        ref ulong reciprocal = ref MemoryMarshal.GetArrayDataReference(Reciprocal);
        int done = Avx2.IsSupported ? InksFromRgbAvx2(rgb, cmyk) : 0;
        for (int x = done; x < width; x++)
        {
            int r = Unsafe.Add(ref source, 3 * x), g = Unsafe.Add(ref source, (3 * x) + 1), b = Unsafe.Add(ref source, (3 * x) + 2);
            int max = Math.Max(r, Math.Max(g, b));

            // Black (max 0) has reciprocal 0, which makes every ink 0, as FromRgba8 gives it.
            ulong m = Unsafe.Add(ref reciprocal, max);
            int half = max >> 1;
            uint ink = (uint)((((ulong)(((max - r) * 255) + half)) * m) >> 32)
                | (uint)((((ulong)(((max - g) * 255) + half)) * m) >> 32) << 8
                | (uint)((((ulong)(((max - b) * 255) + half)) * m) >> 32) << 16
                | (uint)(255 - max) << 24;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 4 * x), BitConverter.IsLittleEndian ? ink : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(ink));
        }
    }

    /// <summary>
    /// <see cref="InksFromRgb"/> sixteen pixels at a time: the bytes split into planes as
    /// <see cref="YCbCrAvx2(ReadOnlySpan{byte}, Span{byte}, Span{byte}, Span{byte}, bool)"/> splits them, and each ink as floor((n + 0.5) / max) in single precision, which is exact for these n and max (the fraction stays at least
    /// 0.5 / max from a whole number, far above the rounding error). Returns how many pixels it did.
    /// </summary>
    internal static int InksFromRgbAvx2(ReadOnlySpan<byte> rgb, Span<byte> cmyk)
    {
        int width = cmyk.Length / 4;
        ref byte source = ref MemoryMarshal.GetReference(rgb);
        ref byte target = ref MemoryMarshal.GetReference(cmyk);
        Vector128<byte> r0 = Vector128.Create((byte)0, 3, 6, 9, 12, 15, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128);
        Vector128<byte> r1 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 2, 5, 8, 11, 14, 128, 128, 128, 128, 128);
        Vector128<byte> r2 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 1, 4, 7, 10, 13);
        Vector128<byte> g0 = Vector128.Create((byte)1, 4, 7, 10, 13, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128);
        Vector128<byte> g1 = Vector128.Create((byte)128, 128, 128, 128, 128, 0, 3, 6, 9, 12, 15, 128, 128, 128, 128, 128);
        Vector128<byte> g2 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 2, 5, 8, 11, 14);
        Vector128<byte> b0 = Vector128.Create((byte)2, 5, 8, 11, 14, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128);
        Vector128<byte> b1 = Vector128.Create((byte)128, 128, 128, 128, 128, 1, 4, 7, 10, 13, 128, 128, 128, 128, 128, 128);
        Vector128<byte> b2 = Vector128.Create((byte)128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 0, 3, 6, 9, 12, 15);
        int x = 0;
        for (; x + 16 <= width && (3 * x) + 48 <= rgb.Length; x += 16)
        {
            Vector128<byte> v0 = Vector128.LoadUnsafe(ref source, (nuint)(3 * x));
            Vector128<byte> v1 = Vector128.LoadUnsafe(ref source, (nuint)((3 * x) + 16));
            Vector128<byte> v2 = Vector128.LoadUnsafe(ref source, (nuint)((3 * x) + 32));
            Vector128<byte> red = Ssse3.Shuffle(v0, r0) | Ssse3.Shuffle(v1, r1) | Ssse3.Shuffle(v2, r2);
            Vector128<byte> green = Ssse3.Shuffle(v0, g0) | Ssse3.Shuffle(v1, g1) | Ssse3.Shuffle(v2, g2);
            Vector128<byte> blue = Ssse3.Shuffle(v0, b0) | Ssse3.Shuffle(v1, b1) | Ssse3.Shuffle(v2, b2);
            Vector128<byte> max = Vector128.Max(red, Vector128.Max(green, blue));
            Inks8(red, green, blue, max).StoreUnsafe(ref target, (nuint)(4 * x));
            Inks8(Sse2.ShiftRightLogical128BitLane(red, 8), Sse2.ShiftRightLogical128BitLane(green, 8), Sse2.ShiftRightLogical128BitLane(blue, 8), Sse2.ShiftRightLogical128BitLane(max, 8))
                .StoreUnsafe(ref target, (nuint)((4 * x) + 32));
        }

        return x;
    }

    /// <summary>The packed inks of the low eight pixels of the planes: C, M, Y, K bytes per pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Inks8(Vector128<byte> red, Vector128<byte> green, Vector128<byte> blue, Vector128<byte> maxBytes)
    {
        Vector256<int> max = Avx2.ConvertToVector256Int32(maxBytes);
        Vector256<int> half = max >>> 1;
        Vector256<float> divisor = Vector256.ConvertToSingle(max);
        Vector256<int> black = Vector256.Equals(max, Vector256<int>.Zero);
        Vector256<int> c = Ink(Avx2.ConvertToVector256Int32(red), max, half, divisor, black);
        Vector256<int> m = Ink(Avx2.ConvertToVector256Int32(green), max, half, divisor, black);
        Vector256<int> y = Ink(Avx2.ConvertToVector256Int32(blue), max, half, divisor, black);
        Vector256<int> k = Vector256.Create(255) - max;
        return (c | (m << 8) | (y << 16) | (k << 24)).AsByte();

        static Vector256<int> Ink(Vector256<int> channel, Vector256<int> max, Vector256<int> half, Vector256<float> divisor, Vector256<int> black)
        {
            Vector256<float> n = Vector256.ConvertToSingle(((max - channel) * Vector256.Create(255)) + half) + Vector256.Create(0.5f);
            return Vector256.AndNot(Vector256.ConvertToInt32(Vector256.Floor(n / divisor)), black);
        }
    }

    /// <summary>
    /// Splits packed inks into four planes, each value inverted as Adobe stores CMYK, as wide as <paramref name="c"/>; with AVX2,
    /// eight pixels at a time by byte shuffles.
    /// </summary>
    internal static void SplitInverted(ReadOnlySpan<byte> cmyk, Span<byte> c, Span<byte> m, Span<byte> y, Span<byte> k)
    {
        int width = c.Length;
        int x = 0;
        if (Avx2.IsSupported)
        {
            ref byte source = ref MemoryMarshal.GetReference(cmyk);
            // Within each 128-bit lane, gather byte 0 of every pixel, then byte 1, 2 and 3: four 4-byte groups per lane.
            Vector256<byte> gather = Vector256.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);
            Vector256<int> order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);
            for (; x + 8 <= width && (4 * x) + 32 <= cmyk.Length; x += 8)
            {
                Vector256<byte> pixels = ~Vector256.LoadUnsafe(ref source, (nuint)(4 * x));
                Vector256<ulong> planes = Avx2.PermuteVar8x32(Avx2.Shuffle(pixels, gather).AsInt32(), order).AsUInt64();
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(c), x), planes.GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(m), x), planes.GetElement(1));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(y), x), planes.GetElement(2));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(k), x), planes.GetElement(3));
            }
        }

        for (; x < width; x++)
        {
            c[x] = (byte)~cmyk[4 * x];
            m[x] = (byte)~cmyk[(4 * x) + 1];
            y[x] = (byte)~cmyk[(4 * x) + 2];
            k[x] = (byte)~cmyk[(4 * x) + 3];
        }
    }

    /// <summary>
    /// Converts packed 12-bit RGB to luma and chroma rows, as wide as <paramref name="y"/>, with libjpeg's constants centered on
    /// 2048 as its 12-bit build does; <paramref name="cb"/> and <paramref name="cr"/> are empty for luma alone.
    /// </summary>
    internal static void YCbCr12(ReadOnlySpan<ushort> rgb, Span<ushort> y, Span<ushort> cb, Span<ushort> cr)
    {
        const int Offset = 2048 << ScaleBits;
        bool luma = cb.IsEmpty;
        for (int x = 0; x < y.Length; x++)
        {
            int red = rgb[3 * x], green = rgb[(3 * x) + 1], blue = rgb[(3 * x) + 2];
            y[x] = (ushort)(((RY * red) + (GY * green) + (BY * blue) + Half) >> ScaleBits);
            if (!luma)
            {
                cb[x] = (ushort)(((RCb * red) + (GCb * green) + (BCbRCr * blue) + Offset + Half - 1) >> ScaleBits);
                cr[x] = (ushort)(((BCbRCr * red) + (GCr * green) + (BCr * blue) + Offset + Half - 1) >> ScaleBits);
            }
        }
    }

    /// <summary><see cref="DownsampleScalar"/> for 12-bit samples.</summary>
    internal static void Downsample12(ReadOnlySpan<ushort> row0, ReadOnlySpan<ushort> row1, bool twoRows, Span<ushort> output)
    {
        if (twoRows)
        {
            for (int x = 0, bias = 1; x < output.Length; x++, bias ^= 3)
            {
                output[x] = (ushort)((row0[2 * x] + row0[(2 * x) + 1] + row1[2 * x] + row1[(2 * x) + 1] + bias) >> 2);
            }
        }
        else
        {
            for (int x = 0, bias = 0; x < output.Length; x++, bias ^= 1)
            {
                output[x] = (ushort)((row0[2 * x] + row0[(2 * x) + 1] + bias) >> 1);
            }
        }
    }

    /// <summary>Narrows 16-bit samples to 12 bits in place, rounding: 65535 becomes 4095.</summary>
    internal static void To12(Span<ushort> samples)
    {
        foreach (ref ushort s in samples)
        {
            s = (ushort)(((s * 4095) + 32767) / 65535);
        }
    }

    private static int Fix(double value) => (int)((value * (1 << ScaleBits)) + 0.5);
}
