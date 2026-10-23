using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Webp;

/// <summary>
/// The pixel work of the VP8 encoder: the forward transforms (the inverses of <see cref="Vp8Dsp"/>'s), quantization, and the
/// distortion measures mode choice weighs. Blocks are addressed in buffers of <see cref="Vp8Dsp.Stride"/> bytes a row.
/// </summary>
[SkipLocalsInit]
internal static class Vp8EncDsp
{
    private const int S = Vp8Dsp.Stride;

    /// <summary>The fixed-point precision of the quantizer's reciprocal.</summary>
    internal const int QFix = 17;

    /// <summary>The weights of the spectral distortion measure, highest for the lowest frequencies.</summary>
    internal static ReadOnlySpan<ushort> WeightY => [38, 32, 20, 9, 32, 28, 17, 7, 20, 17, 10, 4, 9, 7, 4, 2];

    /// <summary>
    /// The forward DCT of the 4 × 4 difference between <paramref name="src"/> at <paramref name="s"/> and the prediction
    /// <paramref name="pred"/> at <paramref name="p"/>, in raster order (RFC 6386's reference encoder transform).
    /// </summary>
    internal static void ForwardTransform(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Span<short> output)
    {
        if (Vp8TransformSimd.IsSupported)
        {
            Vp8TransformSimd.ForwardTransform(src, s, pred, p, output);
            return;
        }

        ForwardTransformScalar(src, s, pred, p, output);
    }

    /// <summary>The scalar path of <see cref="ForwardTransform"/>; the SIMD kernel must match it exactly.</summary>
    internal static void ForwardTransformScalar(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> pred, int p, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++, s += S, p += S)
        {
            int d0 = src[s] - pred[p];
            int d1 = src[s + 1] - pred[p + 1];
            int d2 = src[s + 2] - pred[p + 2];
            int d3 = src[s + 3] - pred[p + 3];
            int a0 = d0 + d3;
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;
            tmp[(i * 4) + 0] = (a0 + a1) * 8;
            tmp[(i * 4) + 1] = ((a2 * 2217) + (a3 * 5352) + 1812) >> 9;
            tmp[(i * 4) + 2] = (a0 - a1) * 8;
            tmp[(i * 4) + 3] = ((a3 * 2217) - (a2 * 5352) + 937) >> 9;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[12 + i];
            int a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i];
            int a3 = tmp[i] - tmp[12 + i];
            output[i] = (short)((a0 + a1 + 7) >> 4);
            output[4 + i] = (short)((((a2 * 2217) + (a3 * 5352) + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)(((a3 * 2217) - (a2 * 5352) + 51000) >> 16);
        }
    }

    /// <summary>The forward Walsh-Hadamard transform of the DCs of 16 blocks (coefficient 0 of each 16 in <paramref name="blocks"/>).</summary>
    internal static void ForwardWalshHadamard(ReadOnlySpan<short> blocks, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int row = i * 64;
            int a0 = blocks[row] + blocks[row + 32];
            int a1 = blocks[row + 16] + blocks[row + 48];
            int a2 = blocks[row + 16] - blocks[row + 48];
            int a3 = blocks[row] - blocks[row + 32];
            tmp[(i * 4) + 0] = a0 + a1;
            tmp[(i * 4) + 1] = a3 + a2;
            tmp[(i * 4) + 2] = a3 - a2;
            tmp[(i * 4) + 3] = a0 - a1;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[8 + i];
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[i] - tmp[8 + i];
            output[i] = (short)((a0 + a1) >> 1);
            output[4 + i] = (short)((a3 + a2) >> 1);
            output[8 + i] = (short)((a3 - a2) >> 1);
            output[12 + i] = (short)((a0 - a1) >> 1);
        }
    }

    /// <summary>
    /// Quantizes a block in place: <paramref name="coefficients"/> (raster order) become the dequantized values, and
    /// <paramref name="levels"/> receives the levels in zigzag order from <paramref name="first"/>; returns whether any is non-zero.
    /// </summary>
    internal static bool Quantize(Span<short> coefficients, Span<short> levels, Vp8Matrix m, int first = 0)
    {
        if (Vp8TransformSimd.IsSupported && (first == 0 || coefficients[0] == 0))
        {
            return Vp8TransformSimd.Quantize(coefficients, levels, m);
        }

        return QuantizeScalar(coefficients, levels, m, first);
    }

    /// <summary>The scalar path of <see cref="Quantize"/>.</summary>
    internal static bool QuantizeScalar(Span<short> coefficients, Span<short> levels, Vp8Matrix m, int first = 0)
    {
        ReadOnlySpan<byte> zigzag = Vp8Decoder.Zigzag;
        bool any = false;
        if (first > 0)
        {
            levels[0] = 0;
        }

        for (int n = first; n < 16; n++)
        {
            int j = zigzag[n];
            int c = coefficients[j];
            int sign = c >> 31;
            uint coeff = (uint)((c ^ sign) - sign + m.Sharpen[j]);
            int i = j == 0 ? 0 : 1;
            if (coeff > m.ZThresh[i])
            {
                int level = (int)(((coeff * m.IQ[i]) + m.Bias[i]) >> QFix);
                level = Math.Min(level, Vp8Costs.MaxLevel);
                level = (level ^ sign) - sign;
                coefficients[j] = (short)(level * m.Q[i]);
                levels[n] = (short)level;
                any |= level != 0;
            }
            else
            {
                coefficients[j] = 0;
                levels[n] = 0;
            }
        }

        return any;
    }

    /// <summary>The sum of squared differences over a <paramref name="w"/> × <paramref name="h"/> area at offset <paramref name="at"/> of both buffers.</summary>
    internal static int Sse(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int at, int w, int h)
    {
        if (Vp8TransformSimd.IsSupported && (w & 7) == 0)
        {
            return Vp8TransformSimd.Sse(a, b, at, w, h);
        }

        int sum = 0;
        for (int y = 0; y < h; y++, at += S)
        {
            ReadOnlySpan<byte> ra = a.Slice(at, w), rb = b.Slice(at, w);
            for (int x = 0; x < w; x++)
            {
                int d = ra[x] - rb[x];
                sum += d * d;
            }
        }

        return sum;
    }

    /// <summary>
    /// The sum of absolute differences of a <paramref name="w"/> × <paramref name="h"/> block of the work-stride buffer
    /// <paramref name="src"/> at <paramref name="s"/> and a plane of <paramref name="stride"/> bytes a row at <paramref name="p"/>:
    /// the motion search's measure. Two rows at a time with 256-bit vectors, one with 128-bit ones.
    /// </summary>
    internal static int Sad(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> plane, int p, int stride, int w, int h)
    {
        if ((w == 16 || w == 8) && (h & 1) == 0 && Sse2.IsSupported)
        {
            _ = src.Slice(s, ((h - 1) * S) + w);
            _ = plane.Slice(p, ((h - 1) * stride) + w);
            ref byte a = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
            ref byte b = ref Unsafe.Add(ref MemoryMarshal.GetReference(plane), p);
            if (w == 16 && Avx2.IsSupported)
            {
                Vector256<ulong> sum = Vector256<ulong>.Zero;
                for (int y = 0; y < h; y += 2)
                {
                    Vector256<byte> va = Vector256.Create(Vector128.LoadUnsafe(ref a), Vector128.LoadUnsafe(ref Unsafe.Add(ref a, S)));
                    Vector256<byte> vb = Vector256.Create(Vector128.LoadUnsafe(ref b), Vector128.LoadUnsafe(ref Unsafe.Add(ref b, stride)));
                    sum += Avx2.SumAbsoluteDifferences(va, vb).AsUInt64();
                    a = ref Unsafe.Add(ref a, 2 * S);
                    b = ref Unsafe.Add(ref b, 2 * stride);
                }

                return (int)Vector256.Sum(sum);
            }

            Vector128<ulong> total = Vector128<ulong>.Zero;
            for (int y = 0; y < h; y += 2)
            {
                Vector128<byte> va, vb;
                if (w == 16)
                {
                    total += Sse2.SumAbsoluteDifferences(Vector128.LoadUnsafe(ref a), Vector128.LoadUnsafe(ref b)).AsUInt64();
                    va = Vector128.LoadUnsafe(ref Unsafe.Add(ref a, S));
                    vb = Vector128.LoadUnsafe(ref Unsafe.Add(ref b, stride));
                }
                else
                {
                    va = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref a), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, S))).AsByte();
                    vb = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref b), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, stride))).AsByte();
                }

                total += Sse2.SumAbsoluteDifferences(va, vb).AsUInt64();
                a = ref Unsafe.Add(ref a, 2 * S);
                b = ref Unsafe.Add(ref b, 2 * stride);
            }

            return (int)Vector128.Sum(total);
        }

        if (w == 16 && Vector128.IsHardwareAccelerated)
        {
            // Byte differences summed in 16-bit lanes: 16 rows of two lanes' worth stay under 8160.
            _ = src.Slice(s, ((h - 1) * S) + 16);
            _ = plane.Slice(p, ((h - 1) * stride) + 16);
            ref byte a = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s);
            ref byte b = ref Unsafe.Add(ref MemoryMarshal.GetReference(plane), p);
            Vector128<ushort> sum = Vector128<ushort>.Zero;
            for (int y = 0; y < h; y++)
            {
                Vector128<byte> va = Vector128.LoadUnsafe(ref a), vb = Vector128.LoadUnsafe(ref b);
                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(Vector128.Max(va, vb) - Vector128.Min(va, vb));
                sum += lo + hi;
                a = ref Unsafe.Add(ref a, S);
                b = ref Unsafe.Add(ref b, stride);
            }

            return (int)(Vector128.Sum(Vector128.WidenLower(sum)) + Vector128.Sum(Vector128.WidenUpper(sum)));
        }

        int t = 0;
        for (int y = 0; y < h; y++, s += S, p += stride)
        {
            for (int x = 0; x < w; x++)
            {
                t += Math.Abs(src[s + x] - plane[p + x]);
            }
        }

        return t;
    }

    /// <summary>The sum of squared differences of two rows of samples of the same length.</summary>
    internal static long SquaredError(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Debug.Assert(a.Length == b.Length);
        int n = a.Length, x = 0;
        long sum = 0;
        if (Vector128.IsHardwareAccelerated && n >= 16)
        {
            // Each lane takes four squares a step, so 32-bit lanes hold any row a frame can have.
            ref byte ra = ref MemoryMarshal.GetReference(a);
            ref byte rb = ref MemoryMarshal.GetReference(b);
            Vector128<uint> acc = Vector128<uint>.Zero;
            for (; x <= n - 16; x += 16)
            {
                Vector128<byte> va = Vector128.LoadUnsafe(ref ra, (nuint)x), vb = Vector128.LoadUnsafe(ref rb, (nuint)x);
                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(Vector128.Max(va, vb) - Vector128.Min(va, vb));
                lo *= lo;
                hi *= hi;
                acc += Vector128.WidenLower(lo) + Vector128.WidenUpper(lo) + Vector128.WidenLower(hi) + Vector128.WidenUpper(hi);
            }

            sum = Vector128.Sum(acc);
        }

        for (; x < n; x++)
        {
            int d = a[x] - b[x];
            sum += d * d;
        }

        return sum;
    }

    /// <summary>The sum of squared differences between two 4 × 4 blocks at their own offsets.</summary>
    internal static int Sse4x4(ReadOnlySpan<byte> a, int ao, ReadOnlySpan<byte> b, int bo)
    {
        if (Vp8TransformSimd.IsSupported)
        {
            return Vp8TransformSimd.Sse4x4(a, ao, b, bo);
        }

        int sum = 0;
        for (int y = 0; y < 4; y++, ao += S, bo += S)
        {
            for (int x = 0; x < 4; x++)
            {
                int d = a[ao + x] - b[bo + x];
                sum += d * d;
            }
        }

        return sum;
    }

    /// <summary>
    /// The spectral distortion of a 4 × 4 block: the difference of the weighted Hadamard magnitudes of the source and the
    /// reconstruction, which penalizes lost texture that the squared error alone would accept.
    /// </summary>
    internal static int Disto4x4(ReadOnlySpan<byte> a, int ao, ReadOnlySpan<byte> b, int bo)
    {
        int sum1 = HadamardWeighted(a, ao);
        int sum2 = HadamardWeighted(b, bo);
        return Math.Abs(sum2 - sum1) >> 5;
    }

    /// <summary>The spectral distortion of a 16 × 16 block, the sum over its sixteen 4 × 4 blocks.</summary>
    internal static int Disto16x16(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int at)
    {
        Span<int> source = stackalloc int[16];
        HadamardWeighted16x16(a, at, source);
        return Disto16x16(source, b, at);
    }

    /// <summary>The spectral distortion of a 16 × 16 block against a source whose sixteen weighted Hadamard magnitudes are known.</summary>
    internal static int Disto16x16(ReadOnlySpan<int> source, ReadOnlySpan<byte> b, int at)
    {
        int d = 0;
        for (int n = 0; n < 16; n++)
        {
            d += Math.Abs(HadamardWeighted(b, at + ((n >> 2) * 4 * S) + ((n & 3) * 4)) - source[n]) >> 5;
        }

        return d;
    }

    /// <summary>The weighted Hadamard magnitudes of the sixteen 4 × 4 blocks of the 16 × 16 block at <paramref name="at"/>, in raster order.</summary>
    internal static void HadamardWeighted16x16(ReadOnlySpan<byte> a, int at, Span<int> output)
    {
        for (int n = 0; n < 16; n++)
        {
            output[n] = HadamardWeighted(a, at + ((n >> 2) * 4 * S) + ((n & 3) * 4));
        }
    }

    /// <summary>The weighted Hadamard magnitude of the 4 × 4 block at <paramref name="at"/>, which the spectral distortion compares.</summary>
    internal static int HadamardWeighted(ReadOnlySpan<byte> input, int at) =>
        Vp8TransformSimd.IsSupported ? Vp8TransformSimd.HadamardWeighted(input, at) : HadamardWeightedScalar(input, at);

    /// <summary>The scalar path of the weighted Hadamard magnitude.</summary>
    internal static int HadamardWeightedScalar(ReadOnlySpan<byte> input, int at)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++, at += S)
        {
            int a0 = input[at] + input[at + 2];
            int a1 = input[at + 1] + input[at + 3];
            int a2 = input[at + 1] - input[at + 3];
            int a3 = input[at] - input[at + 2];
            tmp[(i * 4) + 0] = a0 + a1;
            tmp[(i * 4) + 1] = a3 + a2;
            tmp[(i * 4) + 2] = a3 - a2;
            tmp[(i * 4) + 3] = a0 - a1;
        }

        ReadOnlySpan<ushort> w = WeightY;
        int sum = 0;
        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[8 + i];
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[i] - tmp[8 + i];
            sum += w[i] * Math.Abs(a0 + a1);
            sum += w[4 + i] * Math.Abs(a3 + a2);
            sum += w[8 + i] * Math.Abs(a3 - a2);
            sum += w[12 + i] * Math.Abs(a0 - a1);
        }

        return sum;
    }

    /// <summary>
    /// The susceptibility of blocks to quantization: the forward DCTs of the residual are binned by magnitude, and alpha is the
    /// highest occupied bin over the fullest bin's count, scaled to 0 (flat) to 510 (busy).
    /// </summary>
    internal static int Alpha(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, ReadOnlySpan<int> offsets)
    {
        Span<int> distribution = stackalloc int[32];
        distribution.Clear();
        if (Vp8PairSimd.IsSupported && (offsets.Length & 1) == 0)
        {
            // The blocks come in pairs side by side.
            Span<byte> bins = stackalloc byte[32];
            for (int i = 0; i < offsets.Length; i += 2)
            {
                Debug.Assert(offsets[i + 1] == offsets[i] + 4, "The blocks of a pair are side by side.");
                Vp8PairSimd.TransformBins(src, offsets[i], pred, offsets[i], bins);
                foreach (byte bin in bins)
                {
                    distribution[bin]++;
                }
            }
        }
        else if (Vp8TransformSimd.IsSupported)
        {
            Span<byte> bins = stackalloc byte[16];
            foreach (int at in offsets)
            {
                Vp8TransformSimd.TransformBins(src, at, pred, at, bins);
                foreach (byte bin in bins)
                {
                    distribution[bin]++;
                }
            }
        }
        else
        {
            Span<short> output = stackalloc short[16];
            foreach (int at in offsets)
            {
                ForwardTransform(src, at, pred, at, output);
                for (int k = 0; k < 16; k++)
                {
                    distribution[Math.Min(Math.Abs(output[k]) >> 3, 31)]++;
                }
            }
        }

        int max = 0, last = 1;
        for (int k = 0; k < 32; k++)
        {
            if (distribution[k] > 0)
            {
                max = Math.Max(max, distribution[k]);
                last = k;
            }
        }

        return max > 1 ? 2 * 255 * last / max : 0;
    }

    /// <summary>Returns whether <paramref name="count"/> blocks of zigzag levels have at most <paramref name="threshold"/> non-zero AC levels.</summary>
    internal static bool IsFlat(ReadOnlySpan<short> levels, int count, int threshold)
    {
        int score = 0;
        for (int b = 0; b < count; b++)
        {
            ReadOnlySpan<short> block = levels.Slice(b * 16, 16);
            for (int i = 1; i < 16; i++)
            {
                if (block[i] != 0 && ++score > threshold)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Returns whether all 16 × 16 source pixels at offset 0 have one value.</summary>
    internal static bool IsFlatSource16(ReadOnlySpan<byte> src)
    {
        byte v = src[0];
        for (int y = 0; y < 16; y++)
        {
            if (src.Slice(y * S, 16).ContainsAnyExcept(v))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The ten 4 × 4 predictions of the block at <paramref name="at"/> in <paramref name="ctx"/> (the row above, four above-right,
    /// the column to the left and the corner in place), as <see cref="Vp8Dsp.PredictSubblock"/> makes them, written side by side
    /// to <paramref name="dst"/>: mode m at column (m &amp; 7) × 4 of block row m >> 3. The neighbours are read once.
    /// </summary>
    internal static void PredictSubblocks(ReadOnlySpan<byte> ctx, int at, Span<byte> dst)
    {
        _ = ctx.Slice(at - S - 1, (3 * S) + 9);
        _ = dst.Slice(0, (7 * S) + 8);
        ref byte p = ref MemoryMarshal.GetReference(ctx);
        int t = at - S;
        int x0 = Unsafe.Add(ref p, t - 1);
        int a0 = Unsafe.Add(ref p, t), a1 = Unsafe.Add(ref p, t + 1), a2 = Unsafe.Add(ref p, t + 2), a3 = Unsafe.Add(ref p, t + 3);
        int a4 = Unsafe.Add(ref p, t + 4), a5 = Unsafe.Add(ref p, t + 5), a6 = Unsafe.Add(ref p, t + 6), a7 = Unsafe.Add(ref p, t + 7);
        int l0 = Unsafe.Add(ref p, at - 1), l1 = Unsafe.Add(ref p, at + S - 1), l2 = Unsafe.Add(ref p, at + (2 * S) - 1), l3 = Unsafe.Add(ref p, at + (3 * S) - 1);
        ref byte d = ref MemoryMarshal.GetReference(dst);

        uint dc = (uint)((a0 + a1 + a2 + a3 + l0 + l1 + l2 + l3 + 4) >> 3) * 0x01010101U;
        Rows(ref d, Vp8Dsp.BDc, dc, dc, dc, dc);
        Rows(ref d, Vp8Dsp.BTm, TmRow(l0), TmRow(l1), TmRow(l2), TmRow(l3));
        uint ve = Row(Avg3(x0, a0, a1), Avg3(a0, a1, a2), Avg3(a1, a2, a3), Avg3(a2, a3, a4));
        Rows(ref d, Vp8Dsp.BVe, ve, ve, ve, ve);
        Rows(ref d, Vp8Dsp.BHe, Avg3(x0, l0, l1) * 0x01010101U, Avg3(l0, l1, l2) * 0x01010101U, Avg3(l1, l2, l3) * 0x01010101U, Avg3(l2, l3, l3) * 0x01010101U);

        // Down-left: pixel (x, y) is e[x + y].
        uint e0 = Avg3(a0, a1, a2), e1 = Avg3(a1, a2, a3), e2 = Avg3(a2, a3, a4), e3 = Avg3(a3, a4, a5), e4 = Avg3(a4, a5, a6), e5 = Avg3(a5, a6, a7), e6 = Avg3(a6, a7, a7);
        Rows(ref d, Vp8Dsp.BLd, Row(e0, e1, e2, e3), Row(e1, e2, e3, e4), Row(e2, e3, e4, e5), Row(e3, e4, e5, e6));

        // Down-right: pixel (x, y) is f[3 + x − y].
        uint f0 = Avg3(l3, l2, l1), f1 = Avg3(l2, l1, l0), f2 = Avg3(l1, l0, x0), f3 = Avg3(l0, x0, a0), f4 = Avg3(x0, a0, a1), f5 = Avg3(a0, a1, a2), f6 = Avg3(a1, a2, a3);
        Rows(ref d, Vp8Dsp.BRd, Row(f3, f4, f5, f6), Row(f2, f3, f4, f5), Row(f1, f2, f3, f4), Row(f0, f1, f2, f3));

        uint h0 = Avg2(x0, a0), h1 = Avg2(a0, a1), h2 = Avg2(a1, a2), h3 = Avg2(a2, a3);
        Rows(ref d, Vp8Dsp.BVr, Row(h0, h1, h2, h3), Row(f3, f4, f5, f6), Row(f2, h0, h1, h2), Row(f1, f3, f4, f5));
        uint h4 = Avg2(a3, a4);
        Rows(ref d, Vp8Dsp.BVl, Row(h1, h2, h3, h4), Row(e0, e1, e2, e3), Row(h2, h3, h4, e4), Row(e1, e2, e3, e5));
        uint g0 = Avg2(l0, x0), g1 = Avg2(l1, l0), g2 = Avg2(l2, l1), g3 = Avg2(l3, l2);
        Rows(ref d, Vp8Dsp.BHd, Row(g0, f3, f4, f5), Row(g1, f2, g0, f3), Row(g2, f1, g1, f2), Row(g3, f0, g2, f1));
        uint u0 = Avg2(l0, l1), u1 = Avg3(l0, l1, l2), u2 = Avg2(l1, l2), u3 = Avg3(l1, l2, l3), u4 = Avg2(l2, l3), u5 = Avg3(l2, l3, l3);
        Rows(ref d, Vp8Dsp.BHu, Row(u0, u1, u2, u3), Row(u2, u3, u4, u5), Row(u4, u5, (uint)l3, (uint)l3), (uint)l3 * 0x01010101U);

        uint TmRow(int left)
        {
            int delta = left - x0;
            return Row(Clip(a0 + delta), Clip(a1 + delta), Clip(a2 + delta), Clip(a3 + delta));
        }

        static uint Clip(int v) => (uint)Math.Clamp(v, 0, 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Avg2(int a, int b) => (uint)((a + b + 1) >> 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Avg3(int a, int b, int c) => (uint)((a + (2 * b) + c + 2) >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Row(uint b0, uint b1, uint b2, uint b3) => b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rows(ref byte dst, int mode, uint r0, uint r1, uint r2, uint r3)
    {
        ref byte at = ref Unsafe.Add(ref dst, ((mode & 7) * 4) + ((mode >> 3) * 4 * S));
        Unsafe.WriteUnaligned(ref at, r0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, S), r1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 2 * S), r2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 3 * S), r3);
    }

    /// <summary>Copies a <paramref name="w"/> × <paramref name="h"/> area between buffers of the work stride.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Copy(ReadOnlySpan<byte> from, int fo, Span<byte> to, int to0, int w, int h)
    {
        if (w == 4 && h == 4)
        {
            // One check for each buffer, then four 4-byte moves.
            _ = from.Slice(fo, (3 * S) + 4);
            _ = to.Slice(to0, (3 * S) + 4);
            ref byte f = ref Unsafe.Add(ref MemoryMarshal.GetReference(from), fo);
            ref byte t = ref Unsafe.Add(ref MemoryMarshal.GetReference(to), to0);
            for (int y = 0; y < 4; y++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref t, y * S), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref f, y * S)));
            }

            return;
        }

        for (int y = 0; y < h; y++)
        {
            from.Slice(fo + (y * S), w).CopyTo(to.Slice(to0 + (y * S), w));
        }
    }
}

/// <summary>
/// One quantizer of a segment (luma, the Y2 block, or chroma): the DC and AC steps, their fixed-point reciprocals, the rounding
/// bias, the zero threshold under which a coefficient quantizes to 0, and the sharpening added to luma AC.
/// </summary>
internal sealed class Vp8Matrix
{
    // The rounding of inter residuals: they are mostly noise, so far more of their small coefficients go to 0 than an intra's.
    private const byte InterBias = 56;

    private static ReadOnlySpan<byte> FrequencySharpening => [0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90];

    internal int[] Q { get; } = new int[2];

    internal uint[] IQ { get; } = new uint[2];

    internal uint[] Bias { get; } = new uint[2];

    internal uint[] ZThresh { get; } = new uint[2];

    internal int[] Sharpen { get; } = new int[16];

    /// <summary>
    /// Sets the steps; <paramref name="kind"/> is 0 for luma, 1 for Y2, 2 for chroma. An inter residual rounds down more and is
    /// not sharpened. Returns the average step.
    /// </summary>
    internal int Set(int dc, int ac, int kind, bool inter = false)
    {
        ReadOnlySpan<byte> bias = inter ? [InterBias, InterBias] : kind switch { 0 => [96, 110], 1 => [96, 108], _ => [110, 115] };
        Q[0] = dc;
        Q[1] = ac;
        for (int i = 0; i < 2; i++)
        {
            IQ[i] = (uint)((1 << Vp8EncDsp.QFix) / Q[i]);
            Bias[i] = (uint)(bias[i] << (Vp8EncDsp.QFix - 8));
            ZThresh[i] = ((1u << Vp8EncDsp.QFix) - 1 - Bias[i]) / IQ[i];
        }

        for (int i = 0; i < 16; i++)
        {
            Sharpen[i] = kind == 0 && !inter ? (FrequencySharpening[i] * Q[i == 0 ? 0 : 1]) >> 11 : 0;
        }

        return ((dc + (15 * ac)) + 8) >> 4;
    }
}
