using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>Each resize kernel's vector path matches its scalar reference bit for bit.</summary>
public class ResizeKernelTests
{
    private static readonly (int Source, int Size)[] Ratios = [(37, 11), (100, 23), (9, 40), (64, 64), (5, 1), (1, 7), (300, 299)];

    [Fact]
    public void HorizontalMatchesScalar()
    {
        Random random = new(1);
        foreach (ResampleFilter filter in Enum.GetValues<ResampleFilter>())
        {
            foreach ((int source, int size) in Ratios)
            {
                using ResizeWeights weights = ResizeWeights.Build(filter, size, 0, source, 0, source - 1);
                float[] row = RandomFloats(random, (source + 1) * 4);
                row.AsSpan(source * 4).Clear();
                float[] fast = new float[size * 4];
                float[] scalar = new float[size * 4];
                ResizeKernels.Horizontal(row, weights.First, weights.PairCount, weights.Pairs, weights.PairStride, 0, fast);
                ResizeKernels.HorizontalScalar(row, weights.First, weights.PairCount, weights.Pairs, weights.PairStride, 0, scalar);
                AssertBits(scalar, fast, $"{filter} {source}→{size}");
            }
        }
    }

    [Fact]
    public void HorizontalFixedMatchesScalarAndFloat()
    {
        Random random = new(2);
        foreach (ResampleFilter filter in Enum.GetValues<ResampleFilter>())
        {
            foreach ((int source, int size) in Ratios)
            {
                using ResizeWeights weights = ResizeWeights.Build(filter, size, 0, source, 0, source - 1);
                for (int i = 0; i < size; i++)
                {
                    int sum = 0;
                    ReadOnlySpan<short> group = weights.Fixed.AsSpan(i * weights.GroupStride * 16, weights.GroupCount[i] * 16);
                    for (int g = 0; g < group.Length; g += 16)
                    {
                        sum += group[g] + group[g + 1] + group[g + 8] + group[g + 9];
                    }

                    Assert.Equal(ResizeKernels.FixedOne, sum);
                }

                byte[] row = new byte[(source + 3) * 4];
                random.NextBytes(row.AsSpan(0, source * 4));
                float[] fast = new float[size * 4];
                float[] scalar = new float[size * 4];
                ResizeKernels.HorizontalFixed8(row, weights.First, weights.GroupCount, weights.Fixed, weights.GroupStride, 0, fast);
                ResizeKernels.HorizontalFixed8Scalar(row, weights.First, weights.GroupCount, weights.Fixed, weights.GroupStride, 0, scalar);
                AssertBits(scalar, fast, $"{filter} {source}→{size}");

                // Fixed point stays within half a level of the float pass on the same bytes.
                float[] floats = new float[(source + 1) * 4];
                for (int i = 0; i < source * 4; i++)
                {
                    floats[i] = row[i] / 255f;
                }

                float[] reference = new float[size * 4];
                ResizeKernels.HorizontalScalar(floats, weights.First, weights.PairCount, weights.Pairs, weights.PairStride, 0, reference);
                for (int i = 0; i < reference.Length; i++)
                {
                    Assert.True(Math.Abs(reference[i] - fast[i]) * 255 < 0.5, $"{filter} {source}→{size} [{i}]: {reference[i] * 255} vs {fast[i] * 255}");
                }
            }
        }
    }

    [Fact]
    public void VerticalMatchesScalar()
    {
        Random random = new(3);
        foreach (int rowLength in new[] { 4, 12, 40, 1000, 4004 })
        {
            foreach (int taps in new[] { 1, 2, 5, 13 })
            {
                int ringRows = taps + 2;
                float[] ring = RandomFloats(random, ringRows * rowLength);
                float[] weights = RandomFloats(random, taps);
                float[] fast = new float[rowLength];
                float[] scalar = new float[rowLength];
                ResizeKernels.Vertical(ring, rowLength, ringRows, 5, weights, fast);
                ResizeKernels.VerticalScalar(ring, rowLength, ringRows, 5, weights, scalar);
                AssertBits(scalar, fast, $"{rowLength} × {taps}");
            }
        }
    }

    [Fact]
    public void LoadAndStoreMatchScalar()
    {
        Random random = new(4);
        foreach (int pixels in new[] { 1, 3, 4, 5, 64, 333 })
        {
            byte[] bytes = new byte[pixels * 4];
            random.NextBytes(bytes);
            foreach (bool opaque in new[] { false, true })
            {
                if (opaque)
                {
                    for (int i = 3; i < bytes.Length; i += 4)
                    {
                        bytes[i] = 255;
                    }
                }

                float[] fast = new float[bytes.Length];
                float[] scalar = new float[bytes.Length];
                Assert.Equal(opaque, ResizeKernels.Load8(bytes, fast));
                ResizeKernels.Load8Scalar(bytes, scalar, opaque);
                AssertBits(scalar, fast, $"load {pixels} {opaque}");
            }

            // Overshoot, zero alpha and values just under 1 are where a vector path could round differently.
            float[] work = RandomFloats(random, pixels * 4, -0.2f, 1.2f);
            work[0] = 1e-30f;
            work[3] = 0f;
            if (pixels > 1)
            {
                work[7] = 1e-7f;
            }

            foreach (bool opaque in new[] { false, true })
            {
                byte[] fastBytes = new byte[pixels * 4];
                byte[] scalarBytes = new byte[pixels * 4];
                ResizeKernels.Store8([.. work], fastBytes, opaque);
                ResizeKernels.Store8Scalar(work, scalarBytes, opaque);
                Assert.Equal(scalarBytes, fastBytes);

                float[] fast = [.. work];
                float[] scalar = [.. work];
                ResizeKernels.Unpremultiply(fast, opaque);
                ResizeKernels.UnpremultiplyScalar(scalar, opaque);
                AssertBits(scalar, fast, $"unpremultiply {pixels} {opaque}");
            }

            float[] premultiplied = RandomFloats(random, pixels * 4);
            float[] reference = [.. premultiplied];
            Assert.False(ResizeKernels.PremultiplyRow(premultiplied));
            ResizeKernels.PremultiplyScalar(reference);
            AssertBits(reference, premultiplied, $"premultiply {pixels}");
        }
    }

    [Fact]
    public void FixedAndFloatPathsAgreeOnAPhotoLikeImage()
    {
        // Rgba8 rows that are opaque take the fixed-point pass; RgbaF32 always takes the float pass.
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(203, 151);
        Random random = new(5);
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgba8((byte)(x + random.Next(30)), (byte)(y + random.Next(30)), (byte)random.Next(256), 255);
            }
        }

        using Raster<RgbaF32> floats = raster.As<RgbaF32>();
        foreach (ResampleFilter filter in Enum.GetValues<ResampleFilter>())
        {
            using Raster<Rgba8> a = raster.Derive(ops => ops.Resize(77, 190, filter), TestContext.Current.CancellationToken);
            using Raster<RgbaF32> b = floats.Derive(ops => ops.Resize(77, 190, filter), TestContext.Current.CancellationToken);
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    Rgba8 p = a.GetPixel(x, y);
                    Rgba8 q = b.GetPixel(x, y).ToRgba8();
                    Assert.True(Math.Abs(p.R - q.R) <= 1 && Math.Abs(p.G - q.G) <= 1 && Math.Abs(p.B - q.B) <= 1 && p.A == q.A, $"{filter} ({x}, {y}): {p} vs {q}");
                }
            }
        }
    }

    private static float[] RandomFloats(Random random, int count, float low = 0f, float high = 1f)
    {
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = low + ((high - low) * random.NextSingle());
        }

        return values;
    }

    private static void AssertBits(float[] expected, float[] actual, string label)
    {
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())), label);
    }
}
