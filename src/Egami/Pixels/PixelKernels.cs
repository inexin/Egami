using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Egami.Pixels;

/// <summary>
/// Hand-written SIMD kernels for the common pixel conversions. Every kernel produces exactly the bytes the scalar path does.
/// A kernel converts as many leading pixels as it can and returns the count; the caller converts the rest with the scalar path.
/// </summary>
internal static class PixelKernels
{
    /// <summary>The widest vector size, in bits, that the hardware accelerates, or 0 when SIMD is unavailable or turned off.</summary>
    internal static readonly int HardwareBits =
        Vector512.IsHardwareAccelerated ? 512 :
        Vector256.IsHardwareAccelerated ? 256 :
        Vector128.IsHardwareAccelerated ? 128 : 0;

    // Shuffle masks. An index of 0x80 produces a zero byte on every platform.
    private static readonly Vector128<byte> SwapRb4 = Mask4(2, 1, 0, 3);
    private static readonly Vector128<byte> RotateRight4 = Mask4(3, 0, 1, 2);
    private static readonly Vector128<byte> RotateLeft4 = Mask4(1, 2, 3, 0);
    private static readonly Vector128<byte> Reverse4 = Mask4(3, 2, 1, 0);
    private static readonly Vector128<byte> SwapMiddle4 = Mask4(0, 3, 2, 1);
    private static readonly Vector128<byte> SwapRb3 = Vector128.Create((byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 14, 13, 12, 15);
    private static readonly Vector128<byte> Expand3To4 = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
    private static readonly Vector128<byte> Expand3To4Swap = Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);
    private static readonly Vector128<byte> Drop4To3 = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> Drop4To3Swap = Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> OpaqueAlpha = Vector128.Create(0xFF000000u).AsByte();
    private static readonly Vector128<byte>[] GrayTo4 = BuildGrayMasks(4);
    private static readonly Vector128<byte>[] GrayTo3 = BuildGrayMasks(3);
    private static readonly Vector128<byte>[] GrayAlphaTo4 =
    [
        Vector128.Create((byte)0, 0, 0, 1, 2, 2, 2, 3, 4, 4, 4, 5, 6, 6, 6, 7),
        Vector128.Create((byte)8, 8, 8, 9, 10, 10, 10, 11, 12, 12, 12, 13, 14, 14, 14, 15),
    ];

    /// <summary>
    /// Converts the leading pixels of <paramref name="source"/> with a SIMD kernel, if one exists for the pair,
    /// using vectors no wider than <paramref name="vectorBits"/>.
    /// </summary>
    /// <returns>The number of pixels converted. The caller converts the rest.</returns>
    internal static int Convert<TFrom, TTo>(ReadOnlySpan<TFrom> source, Span<TTo> destination, int vectorBits)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        if (vectorBits < 128)
        {
            return 0;
        }

        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(source);
        Span<byte> dst = MemoryMarshal.AsBytes(destination[..source.Length]);

        // Four-byte RGB layouts.
        if ((typeof(TFrom) == typeof(Rgba8) && typeof(TTo) == typeof(Bgra8)) || (typeof(TFrom) == typeof(Bgra8) && typeof(TTo) == typeof(Rgba8)))
        {
            return SwapRb4Bytes(src, dst, vectorBits) / 4;
        }

        // Half and single floats, four channels each.
        if (typeof(TFrom) == typeof(RgbaF16) && typeof(TTo) == typeof(RgbaF32))
        {
            return HalfKernels.ToSingle(MemoryMarshal.Cast<byte, ushort>(src), MemoryMarshal.Cast<byte, uint>(dst), vectorBits) / 4;
        }

        if (typeof(TFrom) == typeof(RgbaF32) && typeof(TTo) == typeof(RgbaF16))
        {
            return HalfKernels.ToHalf(MemoryMarshal.Cast<byte, uint>(src), MemoryMarshal.Cast<byte, ushort>(dst), vectorBits) / 4;
        }

        // 5-6-5 and the 8-bit four-channel layouts.
        if (typeof(TFrom) == typeof(Bgr565) && (typeof(TTo) == typeof(Rgba8) || typeof(TTo) == typeof(Bgra8)))
        {
            return PackedKernels.Widen565(MemoryMarshal.Cast<byte, ushort>(src), MemoryMarshal.Cast<byte, uint>(dst), typeof(TTo) == typeof(Bgra8), vectorBits);
        }

        if (typeof(TTo) == typeof(Bgr565) && (typeof(TFrom) == typeof(Rgba8) || typeof(TFrom) == typeof(Bgra8)))
        {
            return PackedKernels.Narrow565(MemoryMarshal.Cast<byte, uint>(src), MemoryMarshal.Cast<byte, ushort>(dst), typeof(TFrom) == typeof(Bgra8), vectorBits);
        }

        // Alpha-first layouts: one byte shuffle per pixel.
        Vector128<byte> mask4 = Mask4For<TFrom, TTo>();
        if (mask4 != default)
        {
            return Shuffle4Bytes(src, dst, mask4, vectorBits) / 4;
        }

        if ((typeof(TFrom) == typeof(Rgb8) && typeof(TTo) == typeof(Bgr8)) || (typeof(TFrom) == typeof(Bgr8) && typeof(TTo) == typeof(Rgb8)))
        {
            return Shuffle(src, dst, 15, 15, [SwapRb3], default) / 3;
        }

        if ((typeof(TFrom) == typeof(Rgb8) && typeof(TTo) == typeof(Rgba8)) || (typeof(TFrom) == typeof(Bgr8) && typeof(TTo) == typeof(Bgra8)))
        {
            return Expand3To4Bytes(src, dst, Expand3To4, vectorBits) / 3;
        }

        if ((typeof(TFrom) == typeof(Rgb8) && typeof(TTo) == typeof(Bgra8)) || (typeof(TFrom) == typeof(Bgr8) && typeof(TTo) == typeof(Rgba8)))
        {
            return Expand3To4Bytes(src, dst, Expand3To4Swap, vectorBits) / 3;
        }

        if ((typeof(TFrom) == typeof(Rgba8) && typeof(TTo) == typeof(Rgb8)) || (typeof(TFrom) == typeof(Bgra8) && typeof(TTo) == typeof(Bgr8)))
        {
            return Shuffle(src, dst, 16, 12, [Drop4To3], default) / 4;
        }

        if ((typeof(TFrom) == typeof(Rgba8) && typeof(TTo) == typeof(Bgr8)) || (typeof(TFrom) == typeof(Bgra8) && typeof(TTo) == typeof(Rgb8)))
        {
            return Shuffle(src, dst, 16, 12, [Drop4To3Swap], default) / 4;
        }

        // Gray to RGB expansion.
        if (typeof(TFrom) == typeof(Gray8) && (typeof(TTo) == typeof(Rgba8) || typeof(TTo) == typeof(Bgra8)))
        {
            return Shuffle(src, dst, 16, 64, GrayTo4, OpaqueAlpha);
        }

        if (typeof(TFrom) == typeof(Gray8) && (typeof(TTo) == typeof(Rgb8) || typeof(TTo) == typeof(Bgr8)))
        {
            return Shuffle(src, dst, 16, 48, GrayTo3, default);
        }

        if (typeof(TFrom) == typeof(GrayAlpha8) && (typeof(TTo) == typeof(Rgba8) || typeof(TTo) == typeof(Bgra8)))
        {
            return Shuffle(src, dst, 16, 32, GrayAlphaTo4, default) / 2;
        }

        // RGB to gray: Rec. 709 luma per pixel, in a tight loop over the bytes.
        if (typeof(TTo) == typeof(Gray8) && (typeof(TFrom) == typeof(Rgba8) || typeof(TFrom) == typeof(Bgra8) || typeof(TFrom) == typeof(Rgb8) || typeof(TFrom) == typeof(Bgr8)))
        {
            return ToGray8(src, dst, TFrom.Layout);
        }

        // 8 and 16 bits.
        if ((typeof(TFrom) == typeof(Rgba8) && typeof(TTo) == typeof(Rgba16)) || (typeof(TFrom) == typeof(Gray8) && typeof(TTo) == typeof(Gray16))
            || (typeof(TFrom) == typeof(Rgb8) && typeof(TTo) == typeof(Rgb16)) || (typeof(TFrom) == typeof(GrayAlpha8) && typeof(TTo) == typeof(GrayAlpha16)))
        {
            return Widen(src, MemoryMarshal.Cast<byte, ushort>(dst), vectorBits) / TFrom.Layout.ChannelCount;
        }

        if ((typeof(TFrom) == typeof(Rgba16) && typeof(TTo) == typeof(Rgba8)) || (typeof(TFrom) == typeof(Gray16) && typeof(TTo) == typeof(Gray8))
            || (typeof(TFrom) == typeof(Rgb16) && typeof(TTo) == typeof(Rgb8)) || (typeof(TFrom) == typeof(GrayAlpha16) && typeof(TTo) == typeof(GrayAlpha8)))
        {
            return Narrow(MemoryMarshal.Cast<byte, ushort>(src), dst, vectorBits) / TFrom.Layout.ChannelCount;
        }

        return 0;
    }

    /// <summary>Converts 8-bit RGB pixels in any of the four layouts to gray. Not SIMD, but free of per-pixel calls and bounds checks.</summary>
    /// <returns>The number of pixels converted.</returns>
    private static int ToGray8(ReadOnlySpan<byte> source, Span<byte> destination, PixelLayoutInfo layout)
    {
        int size = layout.BytesPerPixel;
        bool bgr = layout.ChannelOrder is PixelChannelOrder.Bgra or PixelChannelOrder.Bgr;
        int red = bgr ? 2 : 0;
        int blue = bgr ? 0 : 2;
        int count = source.Length / size;
        Luma.Row(source, destination[..count], size, red, blue);
        return count;
    }

    /// <summary>Returns <see langword="true"/> if <see cref="Convert"/> has a kernel for the pair.</summary>
    internal static bool HasKernel<TFrom, TTo>()
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        Span<TTo> destination = stackalloc TTo[64];
        return Convert<TFrom, TTo>(stackalloc TFrom[64], destination, 128) > 0;
    }

    /// <summary>
    /// Loads 16 source bytes at a time and writes one shuffled 16-byte block per mask, each ORed with <paramref name="or"/>.
    /// Each step reads <paramref name="inStep"/> bytes and writes <paramref name="outStep"/>. A block may write past
    /// <paramref name="outStep"/>, but never past the destination; the next step or the scalar tail overwrites those bytes.
    /// </summary>
    /// <returns>The number of source bytes consumed.</returns>
    private static int Shuffle(ReadOnlySpan<byte> source, Span<byte> destination, int inStep, int outStep, ReadOnlySpan<Vector128<byte>> masks, Vector128<byte> or)
    {
        int written = masks.Length * 16;
        int s = 0;
        int d = 0;
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        while (s + 16 <= source.Length && d + written <= destination.Length)
        {
            Vector128<byte> v = Vector128.LoadUnsafe(ref src, (nuint)s);
            for (int k = 0; k < masks.Length; k++)
            {
                (ShuffleBytes(v, masks[k]) | or).StoreUnsafe(ref dst, (nuint)(d + (16 * k)));
            }

            s += inStep;
            d += outStep;
        }

        return s;
    }

    /// <summary>
    /// Expands three-byte pixels to four with opaque alpha, 16 pixels per step: with AVX2, a cross-lane permute puts 12 source bytes in
    /// each 128-bit lane so one byte shuffle handles 8 pixels; otherwise four unaligned 16-byte loads at 12-byte steps.
    /// </summary>
    /// <returns>The number of source bytes converted.</returns>
    private static int Expand3To4Bytes(ReadOnlySpan<byte> source, Span<byte> destination, Vector128<byte> mask, int vectorBits)
    {
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        int s = 0;
        int d = 0;
        if (vectorBits >= 256 && Avx2.IsSupported)
        {
            Vector256<int> spread = Vector256.Create(0, 1, 2, 2, 3, 4, 5, 5);
            Vector256<byte> mask256 = Vector256.Create(mask, mask);
            Vector256<byte> alpha = Vector256.Create(OpaqueAlpha, OpaqueAlpha);
            for (; s + 56 <= source.Length && d + 64 <= destination.Length; s += 48, d += 64)
            {
                Vector256<byte> a = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref src, (nuint)s).AsInt32(), spread).AsByte();
                Vector256<byte> b = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref src, (nuint)(s + 24)).AsInt32(), spread).AsByte();
                (Avx2.Shuffle(a, mask256) | alpha).StoreUnsafe(ref dst, (nuint)d);
                (Avx2.Shuffle(b, mask256) | alpha).StoreUnsafe(ref dst, (nuint)(d + 32));
            }
        }

        for (; s + 52 <= source.Length && d + 64 <= destination.Length; s += 48, d += 64)
        {
            (ShuffleBytes(Vector128.LoadUnsafe(ref src, (nuint)s), mask) | OpaqueAlpha).StoreUnsafe(ref dst, (nuint)d);
            (ShuffleBytes(Vector128.LoadUnsafe(ref src, (nuint)(s + 12)), mask) | OpaqueAlpha).StoreUnsafe(ref dst, (nuint)(d + 16));
            (ShuffleBytes(Vector128.LoadUnsafe(ref src, (nuint)(s + 24)), mask) | OpaqueAlpha).StoreUnsafe(ref dst, (nuint)(d + 32));
            (ShuffleBytes(Vector128.LoadUnsafe(ref src, (nuint)(s + 36)), mask) | OpaqueAlpha).StoreUnsafe(ref dst, (nuint)(d + 48));
        }

        return s + Shuffle(source[s..], destination[d..], 12, 16, [mask], OpaqueAlpha);
    }

    /// <summary>Swaps red and blue in four-byte pixels.</summary>
    /// <returns>The number of bytes converted.</returns>
    private static int SwapRb4Bytes(ReadOnlySpan<byte> source, Span<byte> destination, int vectorBits)
    {
        return Shuffle4Bytes(source, destination, SwapRb4, vectorBits);
    }

    /// <summary>Builds a shuffle that reorders the four bytes of each pixel: destination byte i takes source byte <c>order[i]</c>.</summary>
    private static Vector128<byte> Mask4(byte a, byte b, byte c, byte d)
    {
        return Vector128.Create(a, b, c, d, (byte)(a + 4), (byte)(b + 4), (byte)(c + 4), (byte)(d + 4), (byte)(a + 8), (byte)(b + 8), (byte)(c + 8), (byte)(d + 8), (byte)(a + 12), (byte)(b + 12), (byte)(c + 12), (byte)(d + 12));
    }

    /// <summary>Returns the byte shuffle between two 4-byte layouts that involve an alpha-first type, or default if there is none.</summary>
    private static Vector128<byte> Mask4For<TFrom, TTo>()
    {
        Type from = typeof(TFrom), to = typeof(TTo);
        if ((from == typeof(Rgba8) && to == typeof(Argb8)) || (from == typeof(Bgra8) && to == typeof(Abgr8)))
        {
            return RotateRight4;
        }

        if ((from == typeof(Argb8) && to == typeof(Rgba8)) || (from == typeof(Abgr8) && to == typeof(Bgra8)))
        {
            return RotateLeft4;
        }

        if ((from == typeof(Rgba8) && to == typeof(Abgr8)) || (from == typeof(Abgr8) && to == typeof(Rgba8))
            || (from == typeof(Bgra8) && to == typeof(Argb8)) || (from == typeof(Argb8) && to == typeof(Bgra8)))
        {
            return Reverse4;
        }

        if ((from == typeof(Argb8) && to == typeof(Abgr8)) || (from == typeof(Abgr8) && to == typeof(Argb8)))
        {
            return SwapMiddle4;
        }

        return default;
    }

    /// <summary>Reorders the four bytes of each pixel with <paramref name="mask128"/>.</summary>
    /// <returns>The number of bytes converted.</returns>
    private static int Shuffle4Bytes(ReadOnlySpan<byte> source, Span<byte> destination, Vector128<byte> mask128, int vectorBits)
    {
        int i = 0;
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 512 && Avx512BW.IsSupported)
        {
            Vector512<byte> mask = Vector512.Create(Vector256.Create(mask128, mask128), Vector256.Create(mask128, mask128));
            for (; i + 64 <= source.Length; i += 64)
            {
                Avx512BW.Shuffle(Vector512.LoadUnsafe(ref src, (nuint)i), mask).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 256 && Avx2.IsSupported)
        {
            Vector256<byte> mask = Vector256.Create(mask128, mask128);
            for (; i + 32 <= source.Length; i += 32)
            {
                Avx2.Shuffle(Vector256.LoadUnsafe(ref src, (nuint)i), mask).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i + 16 <= source.Length; i += 16)
        {
            ShuffleBytes(Vector128.LoadUnsafe(ref src, (nuint)i), mask128).StoreUnsafe(ref dst, (nuint)i);
        }

        return i;
    }

    /// <summary>Widens 8-bit channels to 16 bits by multiplying by 257.</summary>
    /// <returns>The number of channels converted.</returns>
    private static int Widen(ReadOnlySpan<byte> source, Span<ushort> destination, int vectorBits)
    {
        int i = 0;
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref ushort dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 512 && Vector512.IsHardwareAccelerated)
        {
            for (; i + 64 <= source.Length; i += 64)
            {
                (Vector512<ushort> lower, Vector512<ushort> upper) = Vector512.Widen(Vector512.LoadUnsafe(ref src, (nuint)i));
                (lower * 257).StoreUnsafe(ref dst, (nuint)i);
                (upper * 257).StoreUnsafe(ref dst, (nuint)(i + 32));
            }
        }

        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= source.Length; i += 32)
            {
                (Vector256<ushort> lower, Vector256<ushort> upper) = Vector256.Widen(Vector256.LoadUnsafe(ref src, (nuint)i));
                (lower * 257).StoreUnsafe(ref dst, (nuint)i);
                (upper * 257).StoreUnsafe(ref dst, (nuint)(i + 16));
            }
        }

        for (; i + 16 <= source.Length; i += 16)
        {
            (Vector128<ushort> lower, Vector128<ushort> upper) = Vector128.Widen(Vector128.LoadUnsafe(ref src, (nuint)i));
            (lower * 257).StoreUnsafe(ref dst, (nuint)i);
            (upper * 257).StoreUnsafe(ref dst, (nuint)(i + 8));
        }

        return i;
    }

    /// <summary>Narrows 16-bit channels to the nearest 8-bit value: (v * 255 + 32895) >> 16, computed in 32 bits.</summary>
    /// <returns>The number of channels converted.</returns>
    private static int Narrow(ReadOnlySpan<ushort> source, Span<byte> destination, int vectorBits)
    {
        int i = 0;
        ref ushort src = ref MemoryMarshal.GetReference(source);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 512 && Vector512.IsHardwareAccelerated)
        {
            for (; i + 64 <= source.Length; i += 64)
            {
                Vector512<ushort> a = Narrow32(Vector512.LoadUnsafe(ref src, (nuint)i));
                Vector512<ushort> b = Narrow32(Vector512.LoadUnsafe(ref src, (nuint)(i + 32)));
                Vector512.Narrow(a, b).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= source.Length; i += 32)
            {
                Vector256<ushort> a = Narrow32(Vector256.LoadUnsafe(ref src, (nuint)i));
                Vector256<ushort> b = Narrow32(Vector256.LoadUnsafe(ref src, (nuint)(i + 16)));
                Vector256.Narrow(a, b).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i + 16 <= source.Length; i += 16)
        {
            Vector128<ushort> a = Narrow32(Vector128.LoadUnsafe(ref src, (nuint)i));
            Vector128<ushort> b = Narrow32(Vector128.LoadUnsafe(ref src, (nuint)(i + 8)));
            Vector128.Narrow(a, b).StoreUnsafe(ref dst, (nuint)i);
        }

        return i;
    }

    // Each returns the narrowed values, still one per 16-bit lane and all at most 255.
    private static Vector512<ushort> Narrow32(Vector512<ushort> v)
    {
        (Vector512<uint> lower, Vector512<uint> upper) = Vector512.Widen(v);
        return Vector512.Narrow(((lower * 255) + Vector512.Create(32895u)) >> 16, ((upper * 255) + Vector512.Create(32895u)) >> 16);
    }

    private static Vector256<ushort> Narrow32(Vector256<ushort> v)
    {
        (Vector256<uint> lower, Vector256<uint> upper) = Vector256.Widen(v);
        return Vector256.Narrow(((lower * 255) + Vector256.Create(32895u)) >> 16, ((upper * 255) + Vector256.Create(32895u)) >> 16);
    }

    private static Vector128<ushort> Narrow32(Vector128<ushort> v)
    {
        (Vector128<uint> lower, Vector128<uint> upper) = Vector128.Widen(v);
        return Vector128.Narrow(((lower * 255) + Vector128.Create(32895u)) >> 16, ((upper * 255) + Vector128.Create(32895u)) >> 16);
    }

    /// <summary>Shuffles bytes by index; an index of 0x80 or more gives zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ShuffleBytes(Vector128<byte> value, Vector128<byte> indices)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(value, indices);
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.VectorTableLookup(value, indices);
        }

        return Vector128.Shuffle(value, indices);
    }

    /// <summary>Builds the masks that copy each gray byte into the first three channels of consecutive pixels.</summary>
    private static Vector128<byte>[] BuildGrayMasks(int bytesPerPixel)
    {
        Vector128<byte>[] masks = new Vector128<byte>[bytesPerPixel];
        Span<byte> indices = stackalloc byte[16];
        for (int k = 0; k < masks.Length; k++)
        {
            for (int j = 0; j < 16; j++)
            {
                int outByte = (16 * k) + j;
                indices[j] = outByte % bytesPerPixel == 3 ? (byte)0x80 : (byte)(outByte / bytesPerPixel);
            }

            masks[k] = Vector128.Create<byte>(indices);
        }

        return masks;
    }
}
