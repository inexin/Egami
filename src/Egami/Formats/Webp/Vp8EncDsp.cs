using System.Runtime.CompilerServices;

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
        int d = 0;
        for (int y = 0; y < 16; y += 4)
        {
            for (int x = 0; x < 16; x += 4)
            {
                int o = at + (y * S) + x;
                d += Disto4x4(a, o, b, o);
            }
        }

        return d;
    }

    private static int HadamardWeighted(ReadOnlySpan<byte> input, int at) =>
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
        Span<short> output = stackalloc short[16];
        foreach (int at in offsets)
        {
            ForwardTransform(src, at, pred, at, output);
            for (int k = 0; k < 16; k++)
            {
                distribution[Math.Min(Math.Abs(output[k]) >> 3, 31)]++;
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

    /// <summary>Copies a <paramref name="w"/> × <paramref name="h"/> area between buffers of the work stride.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Copy(ReadOnlySpan<byte> from, int fo, Span<byte> to, int to0, int w, int h)
    {
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
    private static ReadOnlySpan<byte> FrequencySharpening => [0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90];

    internal int[] Q { get; } = new int[2];

    internal uint[] IQ { get; } = new uint[2];

    internal uint[] Bias { get; } = new uint[2];

    internal uint[] ZThresh { get; } = new uint[2];

    internal int[] Sharpen { get; } = new int[16];

    /// <summary>Sets the steps; <paramref name="kind"/> is 0 for luma, 1 for Y2, 2 for chroma. Returns the average step.</summary>
    internal int Set(int dc, int ac, int kind)
    {
        ReadOnlySpan<byte> bias = kind switch { 0 => [96, 110], 1 => [96, 108], _ => [110, 115] };
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
            Sharpen[i] = kind == 0 ? (FrequencySharpening[i] * Q[i == 0 ? 0 : 1]) >> 11 : 0;
        }

        return ((dc + (15 * ac)) + 8) >> 4;
    }
}
