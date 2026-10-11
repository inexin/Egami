using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations;
using Egami.Operations.Resampling;

namespace Egami;

/// <summary>
/// Resizes one plane of samples with the 1.2 resize filters and weights, in one channel: a horizontal pass into a ring of float
/// rows, then a vertical pass out of the ring, in bands of output rows across threads. The gray resize of the operations goes
/// through four-channel pixels; one channel is four times less work.
/// </summary>
internal static class YuvPlaneResize
{
    internal static void Resize<TSample>(YuvPlane<TSample> source, YuvPlane<TSample> target, ResampleFilter filter, int max, int maxParallelism)
        where TSample : unmanaged
    {
        using ResizeWeights horizontal = ResizeWeights.Build(filter, target.Width, 0, source.Width, 0, source.Width - 1);
        using ResizeWeights vertical = ResizeWeights.Build(filter, target.Height, 0, source.Height, 0, source.Height - 1);
        int taps = horizontal.Stride, padded = (taps + 7) & ~7;

        // Horizontal weights padded to whole vectors of eight; the source line is padded with zeros to match.
        float[] weights = ArrayPool<float>.Shared.Rent(target.Width * padded);
        try
        {
            for (int x = 0; x < target.Width; x++)
            {
                Span<float> row = weights.AsSpan(x * padded, padded);
                row.Clear();
                horizontal.Row(x).CopyTo(row);
            }

            RowRunner.Run(target.Height, (long)target.Width * 4, maxParallelism, (start, end) =>
                Band(source, target, horizontal, vertical, weights, padded, max, start, end), CancellationToken.None);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(weights);
        }
    }

    private static void Band<TSample>(
        YuvPlane<TSample> source, YuvPlane<TSample> target, ResizeWeights horizontal, ResizeWeights vertical, float[] weights, int padded, int max, int start, int end)
        where TSample : unmanaged
    {
        int outWidth = target.Width, ringRows = vertical.Stride;
        float[] ringBuffer = ArrayPool<float>.Shared.Rent(ringRows * outWidth);
        float[] lineBuffer = ArrayPool<float>.Shared.Rent(source.Width + padded);
        float[] outBuffer = ArrayPool<float>.Shared.Rent(outWidth);
        try
        {
            Span<float> line = lineBuffer.AsSpan(0, source.Width + padded);
            line[source.Width..].Clear();
            int next = vertical.First[start];
            for (int y = start; y < end; y++)
            {
                int first = vertical.First[y], count = vertical.Count[y];
                for (; next < first + count; next++)
                {
                    Load(source.Row(next), line);
                    Horizontal(line, horizontal.First, weights, padded, ringBuffer.AsSpan(next % ringRows * outWidth, outWidth));
                }

                Span<float> output = outBuffer.AsSpan(0, outWidth);
                Vertical(ringBuffer, outWidth, ringRows, first, vertical.Row(y), output);
                Store(output, target.Row(y), max);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ringBuffer);
            ArrayPool<float>.Shared.Return(lineBuffer);
            ArrayPool<float>.Shared.Return(outBuffer);
        }
    }

    private static void Load<TSample>(ReadOnlySpan<TSample> row, Span<float> line)
        where TSample : unmanaged
    {
        int x = 0;
        ref float rl = ref MemoryMarshal.GetReference(line);
        if (typeof(TSample) == typeof(byte))
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<TSample, byte>(row);
            ref byte rb = ref MemoryMarshal.GetReference(bytes);
            for (; x <= bytes.Length - 16; x += 16)
            {
                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(Vector128.LoadUnsafe(ref rb, (nuint)x));
                (Vector128<uint> a, Vector128<uint> b) = Vector128.Widen(lo);
                (Vector128<uint> c, Vector128<uint> d) = Vector128.Widen(hi);
                Vector128.ConvertToSingle(a.AsInt32()).StoreUnsafe(ref rl, (nuint)x);
                Vector128.ConvertToSingle(b.AsInt32()).StoreUnsafe(ref rl, (nuint)(x + 4));
                Vector128.ConvertToSingle(c.AsInt32()).StoreUnsafe(ref rl, (nuint)(x + 8));
                Vector128.ConvertToSingle(d.AsInt32()).StoreUnsafe(ref rl, (nuint)(x + 12));
            }

            for (; x < bytes.Length; x++)
            {
                line[x] = bytes[x];
            }
        }
        else
        {
            ReadOnlySpan<ushort> words = MemoryMarshal.Cast<TSample, ushort>(row);
            ref ushort rw = ref MemoryMarshal.GetReference(words);
            for (; x <= words.Length - 8; x += 8)
            {
                (Vector128<uint> a, Vector128<uint> b) = Vector128.Widen(Vector128.LoadUnsafe(ref rw, (nuint)x));
                Vector128.ConvertToSingle(a.AsInt32()).StoreUnsafe(ref rl, (nuint)x);
                Vector128.ConvertToSingle(b.AsInt32()).StoreUnsafe(ref rl, (nuint)(x + 4));
            }

            for (; x < words.Length; x++)
            {
                line[x] = words[x];
            }
        }
    }

    /// <summary>
    /// Each output sample: the dot product of its taps with the source line, eight taps at a time. With AVX, the products of eight
    /// outputs are summed across together, with a tree of horizontal adds instead of eight separate sums.
    /// </summary>
    private static void Horizontal(ReadOnlySpan<float> line, int[] first, float[] weights, int padded, Span<float> output)
    {
        ref float rl = ref MemoryMarshal.GetReference(line);
        ref float rw = ref MemoryMarshal.GetArrayDataReference(weights);
        ref float ro = ref MemoryMarshal.GetReference(output);
        int x = 0;
        if (System.Runtime.Intrinsics.X86.Avx.IsSupported)
        {
            Span<Vector256<float>> sums = stackalloc Vector256<float>[8];
            for (; x <= output.Length - 8; x += 8)
            {
                for (int j = 0; j < 8; j++)
                {
                    int at = first[x + j];
                    Vector256<float> sum = Vector256<float>.Zero;
                    for (int k = 0; k < padded; k += 8)
                    {
                        sum += Vector256.LoadUnsafe(ref rl, (nuint)(at + k)) * Vector256.LoadUnsafe(ref rw, (nuint)(((x + j) * padded) + k));
                    }

                    sums[j] = sum;
                }

                Reduce8(sums).StoreUnsafe(ref ro, (nuint)x);
            }
        }

        for (; x < output.Length; x++)
        {
            int at = first[x];
            Vector256<float> sum = Vector256<float>.Zero;
            for (int k = 0; k < padded; k += 8)
            {
                sum += Vector256.LoadUnsafe(ref rl, (nuint)(at + k)) * Vector256.LoadUnsafe(ref rw, (nuint)((x * padded) + k));
            }

            output[x] = Vector256.Sum(sum);
        }
    }

    /// <summary>The eight sums across eight vectors, in one vector: a tree of horizontal adds, then the two 128-bit halves added.</summary>
    private static Vector256<float> Reduce8(ReadOnlySpan<Vector256<float>> v)
    {
        Vector256<float> a = System.Runtime.Intrinsics.X86.Avx.HorizontalAdd(v[0], v[1]);
        Vector256<float> b = System.Runtime.Intrinsics.X86.Avx.HorizontalAdd(v[2], v[3]);
        Vector256<float> c = System.Runtime.Intrinsics.X86.Avx.HorizontalAdd(v[4], v[5]);
        Vector256<float> d = System.Runtime.Intrinsics.X86.Avx.HorizontalAdd(v[6], v[7]);
        Vector256<float> ab = System.Runtime.Intrinsics.X86.Avx.HorizontalAdd(a, b);
        Vector256<float> cd = System.Runtime.Intrinsics.X86.Avx.HorizontalAdd(c, d);

        // ab holds, per 128-bit half, partial sums of outputs 0-3; cd of 4-7. Lower halves of both, then upper halves, added.
        Vector256<float> low = System.Runtime.Intrinsics.X86.Avx.Permute2x128(ab, cd, 0x20);
        Vector256<float> high = System.Runtime.Intrinsics.X86.Avx.Permute2x128(ab, cd, 0x31);
        return low + high;
    }

    /// <summary>Each output row: the weighted sum of its taps' horizontally resized rows in the ring.</summary>
    private static void Vertical(float[] ring, int width, int ringRows, int first, ReadOnlySpan<float> taps, Span<float> output)
    {
        output.Clear();
        for (int k = 0; k < taps.Length; k++)
        {
            ReadOnlySpan<float> row = ring.AsSpan((first + k) % ringRows * width, width);
            Vector256<float> w = Vector256.Create(taps[k]);
            int x = 0;
            ref float ro = ref MemoryMarshal.GetReference(output);
            ref float rr = ref MemoryMarshal.GetReference(row);
            for (; x <= width - 8; x += 8)
            {
                (Vector256.LoadUnsafe(ref ro, (nuint)x) + (Vector256.LoadUnsafe(ref rr, (nuint)x) * w)).StoreUnsafe(ref ro, (nuint)x);
            }

            for (; x < width; x++)
            {
                output[x] += row[x] * taps[k];
            }
        }
    }

    /// <summary>Rounds half up and clamps to the bit depth.</summary>
    private static void Store<TSample>(ReadOnlySpan<float> values, Span<TSample> target, int max)
        where TSample : unmanaged
    {
        int x = 0;
        ref float rv = ref MemoryMarshal.GetReference(values);
        ref byte rt = ref Unsafe.As<TSample, byte>(ref MemoryMarshal.GetReference(target));
        Vector128<float> half = Vector128.Create(0.5f), top = Vector128.Create((float)max);
        for (; x <= values.Length - 8; x += 8)
        {
            Vector128<int> a = Vector128.ConvertToInt32(Vector128.Clamp(Vector128.Floor(Vector128.LoadUnsafe(ref rv, (nuint)x) + half), Vector128<float>.Zero, top));
            Vector128<int> b = Vector128.ConvertToInt32(Vector128.Clamp(Vector128.Floor(Vector128.LoadUnsafe(ref rv, (nuint)(x + 4)) + half), Vector128<float>.Zero, top));
            Vector128<ushort> words = Vector128.Narrow(a.AsUInt32(), b.AsUInt32());
            if (typeof(TSample) == typeof(byte))
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref rt, x), Vector128.Narrow(words, words).AsUInt64().ToScalar());
            }
            else
            {
                words.AsByte().StoreUnsafe(ref rt, (nuint)(2 * x));
            }
        }

        for (; x < values.Length; x++)
        {
            YuvConvert.SetSample(target, x, YuvKernelsFloat.Round(values[x], max));
        }
    }
}
