using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami.Operations.Resampling;

/// <summary>
/// The inner loops of resize, on working rows of four floats per pixel (premultiplied RGBA). Each kernel has a scalar reference that
/// defines its result; the vector paths add in the same order, so they match it bit for bit.
/// </summary>
internal static class ResizeKernels
{
    /// <summary>
    /// The horizontal pass. Each output pixel sums its even taps and its odd taps separately, then adds the two, which lets a
    /// 256-bit vector hold one tap pair. <paramref name="pairs"/> holds, per output pixel, <paramref name="pairStride"/> groups of
    /// eight floats: the even tap's weight four times, then the odd tap's four times (zero past the last tap).
    /// <paramref name="source"/> must hold one zeroed pixel past the last tap.
    /// </summary>
    internal static void Horizontal(ReadOnlySpan<float> source, ReadOnlySpan<int> first, ReadOnlySpan<int> pairCount, ReadOnlySpan<float> pairs, int pairStride, int xLow, Span<float> destination)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            ref float src = ref MemoryMarshal.GetReference(source);
            ref float w = ref MemoryMarshal.GetReference(pairs);
            ref float dst = ref MemoryMarshal.GetReference(destination);
            int width = destination.Length / 4;
            if (pairs.Length < width * pairStride * 8 || first.Length < width || pairCount.Length < width)
            {
                throw new ArgumentException("The weight tables are too short.");
            }

            for (int x = 0; x < width; x++)
            {
                int start = (first[x] - xLow) * 4;
                int n = pairCount[x];
                if ((uint)(start + (n * 8)) > (uint)source.Length)
                {
                    throw new ArgumentException("The source row is too short.");
                }

                nuint wi = (nuint)(x * pairStride * 8);
                Vector256<float> sum = Vector256<float>.Zero;
                for (int p = 0; p < n; p++)
                {
                    sum += Vector256.LoadUnsafe(ref src, (nuint)(start + (p * 8))) * Vector256.LoadUnsafe(ref w, wi + (nuint)(p * 8));
                }

                (sum.GetLower() + sum.GetUpper()).StoreUnsafe(ref dst, (nuint)(x * 4));
            }

            return;
        }

        HorizontalScalar(source, first, pairCount, pairs, pairStride, xLow, destination);
    }

    /// <summary>The fixed-point weight scale: weights are 1.14 integers, and each output position's weights sum to exactly this.</summary>
    internal const int FixedOne = 1 << 14;

    /// <summary>
    /// The horizontal pass for opaque 4-byte pixels, in fixed point: 16-bit weights times bytes into 32-bit sums, four taps per
    /// step. <paramref name="weights"/> holds, per output pixel, <paramref name="groupStride"/> groups of sixteen shorts: taps 0
    /// and 1 interleaved four times, then taps 2 and 3. <paramref name="source"/> must hold three zeroed pixels past the last tap.
    /// The sums are written as floats in 0..1, straight (which is premultiplied, since alpha is 1).
    /// </summary>
    internal static void HorizontalFixed8(ReadOnlySpan<byte> source, ReadOnlySpan<int> first, ReadOnlySpan<int> groupCount, ReadOnlySpan<short> weights, int groupStride, int xLow, Span<float> destination)
    {
        if (Avx2.IsSupported)
        {
            int width = destination.Length / 4;
            if (weights.Length < width * groupStride * 16 || first.Length < width || groupCount.Length < width)
            {
                throw new ArgumentException("The weight tables are too short.");
            }

            ref byte src = ref MemoryMarshal.GetReference(source);
            ref short w = ref MemoryMarshal.GetReference(weights);
            ref float dst = ref MemoryMarshal.GetReference(destination);
            Vector128<byte> interleave = Vector128.Create((byte)0, 4, 1, 5, 2, 6, 3, 7, 8, 12, 9, 13, 10, 14, 11, 15);
            Vector128<float> scale = Vector128.Create(1f / (255f * FixedOne));
            for (int x = 0; x < width; x++)
            {
                int start = (first[x] - xLow) * 4;
                int n = groupCount[x];
                if ((uint)(start + (n * 16)) > (uint)source.Length)
                {
                    throw new ArgumentException("The source row is too short.");
                }

                nuint wi = (nuint)(x * groupStride * 16);
                Vector256<int> sum = Vector256<int>.Zero;
                for (int g = 0; g < n; g++)
                {
                    Vector128<byte> pixels = Vector128.Shuffle(Vector128.LoadUnsafe(ref src, (nuint)(start + (g * 16))), interleave);
                    sum += Avx2.MultiplyAddAdjacent(Avx2.ConvertToVector256Int16(pixels), Vector256.LoadUnsafe(ref w, wi + (nuint)(g * 16)));
                }

                (Vector128.ConvertToSingle(sum.GetLower() + sum.GetUpper()) * scale).StoreUnsafe(ref dst, (nuint)(x * 4));
            }

            return;
        }

        HorizontalFixed8Scalar(source, first, groupCount, weights, groupStride, xLow, destination);
    }

    /// <summary>The reference for <see cref="HorizontalFixed8"/>. Integer sums are exact, so any order gives the same result.</summary>
    internal static void HorizontalFixed8Scalar(ReadOnlySpan<byte> source, ReadOnlySpan<int> first, ReadOnlySpan<int> groupCount, ReadOnlySpan<short> weights, int groupStride, int xLow, Span<float> destination)
    {
        const float Scale = 1f / (255f * FixedOne);
        for (int x = 0; x < destination.Length / 4; x++)
        {
            int start = (first[x] - xLow) * 4;
            ReadOnlySpan<short> w = weights.Slice(x * groupStride * 16, groupCount[x] * 16);
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int g = 0; g < groupCount[x]; g++)
                {
                    // Group g holds taps (4g, 4g+1) interleaved in its first eight shorts and (4g+2, 4g+3) in its last eight.
                    int s = start + (g * 16) + c;
                    sum += (source[s] * w[g * 16]) + (source[s + 4] * w[(g * 16) + 1])
                        + (source[s + 8] * w[(g * 16) + 8]) + (source[s + 12] * w[(g * 16) + 9]);
                }

                destination[(x * 4) + c] = sum * Scale;
            }
        }
    }

    /// <summary>The reference for <see cref="Horizontal"/>.</summary>
    internal static void HorizontalScalar(ReadOnlySpan<float> source, ReadOnlySpan<int> first, ReadOnlySpan<int> pairCount, ReadOnlySpan<float> pairs, int pairStride, int xLow, Span<float> destination)
    {
        Span<float> even = stackalloc float[4];
        Span<float> odd = stackalloc float[4];
        for (int x = 0; x < destination.Length / 4; x++)
        {
            int start = (first[x] - xLow) * 4;
            ReadOnlySpan<float> w = pairs.Slice(x * pairStride * 8, pairCount[x] * 8);
            even.Clear();
            odd.Clear();
            for (int p = 0; p < pairCount[x]; p++)
            {
                for (int c = 0; c < 4; c++)
                {
                    even[c] += source[start + (p * 8) + c] * w[(p * 8) + c];
                    odd[c] += source[start + (p * 8) + 4 + c] * w[(p * 8) + 4 + c];
                }
            }

            for (int c = 0; c < 4; c++)
            {
                destination[(x * 4) + c] = even[c] + odd[c];
            }
        }
    }

    /// <summary>
    /// The vertical pass: <c>destination = rows[0] * weights[0] + rows[1] * weights[1] + ...</c>, added left to right, element by
    /// element. <paramref name="ring"/> holds <paramref name="ringRows"/> rows of <paramref name="rowLength"/> floats; tap k reads ring
    /// row <c>(first + k) % ringRows</c>.
    /// </summary>
    internal static void Vertical(ReadOnlySpan<float> ring, int rowLength, int ringRows, int first, ReadOnlySpan<float> weights, Span<float> destination)
    {
        destination = destination[..rowLength];
        if (ring.Length < rowLength * ringRows || weights.Length == 0)
        {
            throw new ArgumentException("The ring is too short.");
        }

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref float r = ref MemoryMarshal.GetReference(ring);
            ref float d = ref MemoryMarshal.GetReference(destination);
            Span<nuint> rows = stackalloc nuint[weights.Length];
            for (int k = 0; k < rows.Length; k++)
            {
                rows[k] = (nuint)((first + k) % ringRows * rowLength);
            }

            for (; i <= rowLength - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> sum = Vector256.LoadUnsafe(ref r, rows[0] + (nuint)i) * weights[0];
                for (int k = 1; k < rows.Length; k++)
                {
                    sum += Vector256.LoadUnsafe(ref r, rows[k] + (nuint)i) * weights[k];
                }

                sum.StoreUnsafe(ref d, (nuint)i);
            }
        }

        VerticalScalar(ring, rowLength, ringRows, first, weights, destination, i);
    }

    /// <summary>The reference for <see cref="Vertical"/>, from element <paramref name="from"/> on.</summary>
    internal static void VerticalScalar(ReadOnlySpan<float> ring, int rowLength, int ringRows, int first, ReadOnlySpan<float> weights, Span<float> destination, int from = 0)
    {
        for (int i = from; i < rowLength; i++)
        {
            float sum = ring[(first % ringRows * rowLength) + i] * weights[0];
            for (int k = 1; k < weights.Length; k++)
            {
                sum += ring[((first + k) % ringRows * rowLength) + i] * weights[k];
            }

            destination[i] = sum;
        }
    }

    /// <summary>
    /// Loads 4-byte pixels with alpha last (Rgba8 or Bgra8) as floats in 0..1, premultiplied unless the row is opaque.
    /// </summary>
    /// <returns><see langword="true"/> if every pixel was opaque, so nothing was premultiplied.</returns>
    internal static bool Load8(ReadOnlySpan<byte> source, Span<float> destination)
    {
        bool opaque = IsOpaque8(source);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref byte src = ref MemoryMarshal.GetReference(source);
            ref float dst = ref MemoryMarshal.GetReference(destination);
            if (destination.Length < source.Length)
            {
                throw new ArgumentException("The destination is too short.");
            }

            Vector256<float> scale = Vector256.Create(1f / 255f);
            for (; i <= source.Length - 16; i += 16)
            {
                (Vector256<ushort> lo, _) = Vector256.Widen(Vector256.Create(Vector128.LoadUnsafe(ref src, (nuint)i), Vector128<byte>.Zero));
                (Vector256<uint> a, Vector256<uint> b) = Vector256.Widen(lo);
                Vector256<float> fa = Vector256.ConvertToSingle(a.AsInt32()) * scale;
                Vector256<float> fb = Vector256.ConvertToSingle(b.AsInt32()) * scale;
                if (!opaque)
                {
                    fa = Premultiply(fa);
                    fb = Premultiply(fb);
                }

                fa.StoreUnsafe(ref dst, (nuint)i);
                fb.StoreUnsafe(ref dst, (nuint)(i + 8));
            }
        }

        Load8Scalar(source[i..], destination[i..], opaque);
        return opaque;
    }

    /// <summary>The reference for <see cref="Load8"/>, with the opacity already known.</summary>
    internal static void Load8Scalar(ReadOnlySpan<byte> source, Span<float> destination, bool opaque)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            float a = source[i + 3] * (1f / 255f);
            float m = opaque ? 1f : a;
            for (int c = 0; c < 3; c++)
            {
                float v = source[i + c] * (1f / 255f);
                destination[i + c] = opaque ? v : v * m;
            }

            destination[i + 3] = a;
        }
    }

    /// <summary>Premultiplies a row of float pixels in place, unless every alpha is 1.</summary>
    /// <returns><see langword="true"/> if every pixel was opaque.</returns>
    internal static bool PremultiplyRow(Span<float> pixels)
    {
        bool opaque = true;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 1f)
            {
                opaque = false;
                break;
            }
        }

        if (opaque)
        {
            return true;
        }

        int j = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref float p = ref MemoryMarshal.GetReference(pixels);
            for (; j <= pixels.Length - 8; j += 8)
            {
                Premultiply(Vector256.LoadUnsafe(ref p, (nuint)j)).StoreUnsafe(ref p, (nuint)j);
            }
        }

        PremultiplyScalar(pixels, j);
        return false;
    }

    /// <summary>The reference for <see cref="PremultiplyRow"/> on a row that is not opaque, from element <paramref name="from"/> on.</summary>
    internal static void PremultiplyScalar(Span<float> pixels, int from = 0)
    {
        for (int j = from; j < pixels.Length; j += 4)
        {
            float a = pixels[j + 3];
            pixels[j] *= a;
            pixels[j + 1] *= a;
            pixels[j + 2] *= a;
        }
    }

    /// <summary>
    /// Stores working pixels as 4-byte pixels with alpha last, dividing out alpha and clamping the filter's overshoot.
    /// In an <paramref name="opaque"/> band alpha is written as 255 and nothing is divided.
    /// </summary>
    internal static void Store8(Span<float> source, Span<byte> destination, bool opaque)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            if (source.Length < destination.Length)
            {
                throw new ArgumentException("The source is too short.");
            }

            ref float src = ref MemoryMarshal.GetReference(source);
            ref byte dst = ref MemoryMarshal.GetReference(destination);
            for (; i <= destination.Length - 16; i += 16)
            {
                Vector256<int> a = Vector256.ConvertToInt32(Straighten(Vector256.LoadUnsafe(ref src, (nuint)i), opaque));
                Vector256<int> b = Vector256.ConvertToInt32(Straighten(Vector256.LoadUnsafe(ref src, (nuint)(i + 8)), opaque));
                Vector256<ushort> words = Vector256.Narrow(a.AsUInt32(), b.AsUInt32());
                Vector256.Narrow(words, words).GetLower().StoreUnsafe(ref dst, (nuint)i);
            }
        }

        Store8Scalar(source[i..], destination[i..], opaque);
    }

    /// <summary>The reference for <see cref="Store8"/>.</summary>
    internal static void Store8Scalar(ReadOnlySpan<float> source, Span<byte> destination, bool opaque)
    {
        for (int i = 0; i < destination.Length; i += 4)
        {
            float raw = source[i + 3];
            for (int c = 0; c < 3; c++)
            {
                float v = opaque ? source[i + c] : raw > 0f ? source[i + c] / raw : 0f;
                destination[i + c] = (byte)(int)((Clamp01(v) * 255f) + 0.5f);
            }

            destination[i + 3] = (byte)(int)(((opaque ? 1f : Clamp01(raw)) * 255f) + 0.5f);
        }
    }

    /// <summary>Divides out alpha in place, clamping to 0..1; alpha becomes 1 in an <paramref name="opaque"/> band.</summary>
    internal static void Unpremultiply(Span<float> pixels, bool opaque)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref float p = ref MemoryMarshal.GetReference(pixels);
            for (; i <= pixels.Length - 8; i += 8)
            {
                Straight01(Vector256.LoadUnsafe(ref p, (nuint)i), opaque).StoreUnsafe(ref p, (nuint)i);
            }
        }

        UnpremultiplyScalar(pixels, opaque, i);
    }

    /// <summary>The reference for <see cref="Unpremultiply"/>, from element <paramref name="from"/> on.</summary>
    internal static void UnpremultiplyScalar(Span<float> pixels, bool opaque, int from = 0)
    {
        for (int i = from; i < pixels.Length; i += 4)
        {
            float raw = pixels[i + 3];
            for (int c = 0; c < 3; c++)
            {
                pixels[i + c] = Clamp01(opaque ? pixels[i + c] : raw > 0f ? pixels[i + c] / raw : 0f);
            }

            pixels[i + 3] = opaque ? 1f : Clamp01(raw);
        }
    }

    /// <summary>Returns whether every alpha byte of a row of 4-byte pixels with alpha last is 255.</summary>
    internal static bool IsOpaque8(ReadOnlySpan<byte> source)
    {
        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(source);
        uint alpha = BitConverter.IsLittleEndian ? 0xFF000000u : 0xFFu;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> mask = Vector256.Create(alpha);
            for (; i <= pixels.Length - 8; i += 8)
            {
                if ((Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(pixels), (nuint)i) & mask) != mask)
                {
                    return false;
                }
            }
        }

        for (; i < pixels.Length; i++)
        {
            if ((pixels[i] & alpha) != alpha)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Multiplies the color lanes of two pixels by their alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Premultiply(Vector256<float> v)
    {
        Vector256<float> alpha = Color.ColorKernels.Broadcast(v, 3);
        return v * Vector256.ConditionalSelect(AlphaLanes, Vector256<float>.One, alpha);
    }

    /// <summary>
    /// Turns two premultiplied pixels into straight values scaled to 0..255 plus 0.5, ready to truncate to bytes, matching
    /// <see cref="Store8Scalar"/> lane by lane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Straighten(Vector256<float> v, bool opaque)
    {
        return (Straight01(v, opaque) * 255f) + Vector256.Create(0.5f);
    }

    /// <summary>Divides two premultiplied pixels by their alpha and clamps to 0..1; alpha becomes 1 when <paramref name="opaque"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Straight01(Vector256<float> v, bool opaque)
    {
        if (opaque)
        {
            return Vector256.ConditionalSelect(AlphaLanes, Vector256<float>.One, Clamp01(v));
        }

        // Color divides by the unclamped alpha, so ringing that pushes alpha past 1 does not brighten the color. Lanes with zero
        // or negative alpha divide to NaN or infinity, but the select drops them.
        Vector256<float> alpha = Color.ColorKernels.Broadcast(v, 3);
        Vector256<float> color = Vector256.ConditionalSelect(Vector256.GreaterThan(alpha, Vector256<float>.Zero), Clamp01(v / alpha), Vector256<float>.Zero);
        return Vector256.ConditionalSelect(AlphaLanes, Clamp01(alpha), color);
    }

    private static Vector256<float> AlphaLanes => Vector256.Create(0, 0, 0, -1, 0, 0, 0, -1).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Clamp01(Vector256<float> v)
    {
        // The native min and max equal the scalar clamp for finite values; NaN lanes are dropped by the callers' selects.
        return Vector256.MinNative(Vector256.MaxNative(v, Vector256<float>.Zero), Vector256<float>.One);
    }

    private static float Clamp01(float v)
    {
        return v > 0f ? (v < 1f ? v : 1f) : 0f;
    }
}
