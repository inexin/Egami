using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Egami.Simd;

/// <summary>
/// The 128-bit operations the codec and operation kernels need beyond the portable <see cref="Vector128"/> API, each one
/// instruction or a short sequence on x86 (SSE2 to SSE4.1) and on Arm64 (AdvSimd): saturating packs, high and rounding
/// multiplies, pairwise multiply-add, sums of absolute differences, byte table lookup, interleaving and saturating arithmetic.
/// A kernel checks <see cref="IsSupported"/> once and then calls these freely; where it is false (other CPUs, or SIMD turned
/// off) the kernel's scalar path runs. Every method gives the same result on both architectures for every input, except where
/// its remarks say otherwise; the tests in Egami.Tests check each against a scalar form.
/// </summary>
internal static class Simd128
{
    /// <summary>
    /// Gets a value indicating whether every operation here is accelerated: SSE4.1 on x86 or AdvSimd on Arm64, with
    /// <see cref="Vector128"/> accelerated (so not with <c>DOTNET_EnableHWIntrinsic=0</c>).
    /// </summary>
    internal static bool IsSupported => (Sse41.IsSupported || AdvSimd.Arm64.IsSupported) && Vector128.IsHardwareAccelerated;

    /// <summary>Narrows two vectors of 32-bit lanes to 16 bits with signed saturation; <paramref name="lower"/> fills the first half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> PackSignedSaturate(Vector128<int> lower, Vector128<int> upper) =>
        Sse2.IsSupported ? Sse2.PackSignedSaturate(lower, upper)
            : AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(lower), upper);

    /// <summary>Narrows two vectors of 16-bit lanes to 8 bits with signed saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<sbyte> PackSignedSaturate(Vector128<short> lower, Vector128<short> upper) =>
        Sse2.IsSupported ? Sse2.PackSignedSaturate(lower, upper)
            : AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(lower), upper);

    /// <summary>Narrows two vectors of signed 16-bit lanes to bytes, clamping to 0..255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> PackUnsignedSaturate(Vector128<short> lower, Vector128<short> upper) =>
        Sse2.IsSupported ? Sse2.PackUnsignedSaturate(lower, upper)
            : AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(lower), upper);

    /// <summary>Narrows two vectors of signed 32-bit lanes to 16 bits, clamping to 0..65535.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ushort> PackUnsignedSaturate(Vector128<int> lower, Vector128<int> upper) =>
        Sse41.IsSupported ? Sse41.PackUnsignedSaturate(lower, upper)
            : AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(lower), upper);

    /// <summary>
    /// Narrows four vectors of signed 32-bit lanes to bytes, clamping to 0..255 (through 16 bits with signed saturation, which
    /// gives the same clamp).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> PackUnsignedSaturate(Vector128<int> a, Vector128<int> b, Vector128<int> c, Vector128<int> d) =>
        PackUnsignedSaturate(PackSignedSaturate(a, b), PackSignedSaturate(c, d));

    /// <summary>The high 16 bits of each signed 16 × 16-bit product, <c>(a × b) &gt;&gt; 16</c>: <c>pmulhw</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> MultiplyHigh(Vector128<short> a, Vector128<short> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyHigh(a, b);
        }

        Vector128<int> low = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
        Vector128<int> high = AdvSimd.MultiplyWideningUpper(a, b);
        return AdvSimd.Arm64.UnzipOdd(low.AsInt16(), high.AsInt16());
    }

    /// <summary>The high 16 bits of each unsigned 16 × 16-bit product: <c>pmulhuw</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ushort> MultiplyHigh(Vector128<ushort> a, Vector128<ushort> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyHigh(a, b);
        }

        Vector128<uint> low = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
        Vector128<uint> high = AdvSimd.MultiplyWideningUpper(a, b);
        return AdvSimd.Arm64.UnzipOdd(low.AsUInt16(), high.AsUInt16());
    }

    /// <summary>
    /// <c>(a × b + 2^14) &gt;&gt; 15</c> per signed 16-bit lane: <c>pmulhrsw</c> and <c>sqrdmulh</c>.
    /// </summary>
    /// <remarks>
    /// The one input where the two differ is −32768 × −32768: x86 gives −32768 and Arm64 saturates to 32767. Callers keep one
    /// operand above −32768 (a constant, as every kernel does).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> MultiplyHighRoundScale(Vector128<short> a, Vector128<short> b) =>
        Ssse3.IsSupported ? Ssse3.MultiplyHighRoundScale(a, b) : AdvSimd.MultiplyRoundedDoublingSaturateHigh(a, b);

    /// <summary>
    /// Multiplies signed 16-bit lanes and adds adjacent pairs of the 32-bit products: lane i is a[2i]b[2i] + a[2i+1]b[2i+1]
    /// (<c>pmaddwd</c>).
    /// </summary>
    /// <remarks>The one sum that overflows, with all four inputs −32768, wraps to −2^31 on both.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> MultiplyAddAdjacent(Vector128<short> a, Vector128<short> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyAddAdjacent(a, b);
        }

        Vector128<int> low = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
        Vector128<int> high = AdvSimd.MultiplyWideningUpper(a, b);
        return AdvSimd.Arm64.AddPairwise(low, high);
    }

    /// <summary>
    /// The sums of absolute differences of each half's eight bytes, in the low 16 bits of each 64-bit lane (the rest zero):
    /// <c>psadbw</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ulong> SumAbsoluteDifferences(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.SumAbsoluteDifferences(a, b).AsUInt64();
        }

        Vector128<ushort> pairs = AdvSimd.AddPairwiseWidening(AdvSimd.AbsoluteDifference(a, b));
        return AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(pairs));
    }

    /// <summary>Picks bytes of <paramref name="value"/> by <paramref name="indices"/>: <c>pshufb</c> and <c>tbl</c>.</summary>
    /// <remarks>
    /// Each index must be 0 to 15, which picks that byte, or 0x80 or more, which gives zero; the two architectures differ for
    /// 16 to 127.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> indices) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(value, indices) : AdvSimd.Arm64.VectorTableLookup(value, indices);

    /// <summary>Interleaves the low halves: a0 b0 a1 b1 …</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> ZipLow(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    /// <summary>Interleaves the high halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> ZipHigh(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <inheritdoc cref="ZipLow(Vector128{byte}, Vector128{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> ZipLow(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    /// <inheritdoc cref="ZipHigh(Vector128{byte}, Vector128{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> ZipHigh(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <inheritdoc cref="ZipLow(Vector128{byte}, Vector128{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ZipLow(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    /// <inheritdoc cref="ZipHigh(Vector128{byte}, Vector128{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ZipHigh(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <inheritdoc cref="ZipLow(Vector128{byte}, Vector128{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<long> ZipLow(Vector128<long> a, Vector128<long> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    /// <inheritdoc cref="ZipHigh(Vector128{byte}, Vector128{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<long> ZipHigh(Vector128<long> a, Vector128<long> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <summary>
    /// Transposes four rows of four 32-bit lanes in place: afterwards <paramref name="r0"/> holds lane 0 of each input, and so on.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3)
    {
        Vector128<long> t0 = ZipLow(r0, r1).AsInt64();
        Vector128<long> t1 = ZipHigh(r0, r1).AsInt64();
        Vector128<long> t2 = ZipLow(r2, r3).AsInt64();
        Vector128<long> t3 = ZipHigh(r2, r3).AsInt64();
        r0 = ZipLow(t0, t2).AsInt32();
        r1 = ZipHigh(t0, t2).AsInt32();
        r2 = ZipLow(t1, t3).AsInt32();
        r3 = ZipHigh(t1, t3).AsInt32();
    }

    /// <summary>Adds signed 16-bit lanes with saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> AddSaturate(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.AddSaturate(a, b) : AdvSimd.AddSaturate(a, b);

    /// <summary>Subtracts signed 16-bit lanes with saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> SubtractSaturate(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.SubtractSaturate(a, b) : AdvSimd.SubtractSaturate(a, b);

    /// <summary>Adds bytes with unsigned saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> AddSaturate(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.AddSaturate(a, b) : AdvSimd.AddSaturate(a, b);

    /// <summary>Subtracts bytes with unsigned saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> SubtractSaturate(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.SubtractSaturate(a, b) : AdvSimd.SubtractSaturate(a, b);

    /// <summary>Zero-extends the low four bytes to 32-bit lanes (<c>pmovzxbd</c>, two <c>uxtl</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> WidenToInt32(Vector128<byte> a) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int32(a) : Vector128.WidenLower(Vector128.WidenLower(a)).AsInt32();

    /// <summary>Sign-extends the low four 16-bit lanes to 32 bits (<c>pmovsxwd</c>, <c>sxtl</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> WidenToInt32(Vector128<short> a) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int32(a) : Vector128.WidenLower(a);

    /// <summary>Zero-extends the low eight bytes to 16-bit lanes (<c>pmovzxbw</c>, <c>uxtl</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> WidenToInt16(Vector128<byte> a) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int16(a) : Vector128.WidenLower(a).AsInt16();

    /// <summary>Adds signed bytes with saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<sbyte> AddSaturate(Vector128<sbyte> a, Vector128<sbyte> b) =>
        Sse2.IsSupported ? Sse2.AddSaturate(a, b) : AdvSimd.AddSaturate(a, b);

    /// <summary>Subtracts signed bytes with saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<sbyte> SubtractSaturate(Vector128<sbyte> a, Vector128<sbyte> b) =>
        Sse2.IsSupported ? Sse2.SubtractSaturate(a, b) : AdvSimd.SubtractSaturate(a, b);

    /// <summary>The rounded average of bytes, <c>(a + b + 1) &gt;&gt; 1</c>: <c>pavgb</c> and <c>urhadd</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> Average(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.Average(a, b) : AdvSimd.FusedAddRoundedHalving(a, b);

    /// <summary>The rounded average of unsigned 16-bit lanes: <c>pavgw</c> and <c>urhadd</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ushort> Average(Vector128<ushort> a, Vector128<ushort> b) =>
        Sse2.IsSupported ? Sse2.Average(a, b) : AdvSimd.FusedAddRoundedHalving(a, b);
}
