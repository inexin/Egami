using System.Runtime.Intrinsics.X86;

using Egami.Formats.Jpeg;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Step 2.4.10's exactness harness for the encoder's vector kernels: the AVX2 forward DCT with quantization, RGB to YCbCr, luma
/// and chroma downsampling each give exactly the scalar result, on random blocks and rows from fixed seeds and on worst cases
/// (all 0, all 255, checkerboards, stripes, single spikes), and the conversion on every RGB triple. Faster kernels must keep these
/// passing.
/// </summary>
public class JpegEncodeExactnessTests
{
    public static TheoryData<int> Qualities() => [1, 5, 25, 50, 75, 90, 95, 100];

    [Theory]
    [MemberData(nameof(Qualities))]
    public void ForwardDctVectorEqualsScalarOnRandomAndWorstCaseBlocks(int quality)
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 is not available.");
        foreach (bool chroma in (bool[])[false, true])
        {
            int[] divisors = Divisors(chroma ? JpegTables.ChromaQuant : JpegTables.LumaQuant, quality);
            ushort[] reciprocals = JpegFdct.Reciprocals(divisors);
            foreach (int stride in (int[])[8, 13, 4000])
            {
                byte[] samples = new byte[(7 * stride) + 8];
                foreach ((string name, Func<int, int, int> sample) in WorstBlocks())
                {
                    Fill(samples, stride, sample);
                    Check(samples, stride, divisors, reciprocals, $"{name}, q{quality}, stride {stride}");
                }

                Random random = new(quality * 31 + stride);
                for (int trial = 0; trial < 2000; trial++)
                {
                    // Uniform noise, then noise in a narrow band around a random level, which keeps the high frequencies small.
                    int level = random.Next(256), spread = trial % 2 == 0 ? 256 : 1 + random.Next(32);
                    Fill(samples, stride, (_, _) => Math.Clamp(level + random.Next(spread) - (spread / 2), 0, 255));
                    Check(samples, stride, divisors, reciprocals, $"random trial {trial}, q{quality}, stride {stride}");
                }
            }
        }
    }

    [Fact]
    public void ColorConversionVectorEqualsScalarForEveryRgbTriple()
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 is not available.");

        // One row per red and green, blue running 0 to 255, so the 256 × 256 rows cover all 2^24 triples.
        const int Width = 256;
        byte[] rgb = new byte[Width * 3];
        byte[][] scalar = [new byte[Width], new byte[Width], new byte[Width]];
        byte[][] vector = [new byte[Width], new byte[Width], new byte[Width]];
        byte[] luma = new byte[Width];
        for (int red = 0; red < 256; red++)
        {
            for (int green = 0; green < 256; green++)
            {
                for (int blue = 0; blue < Width; blue++)
                {
                    rgb[3 * blue] = (byte)red;
                    rgb[(3 * blue) + 1] = (byte)green;
                    rgb[(3 * blue) + 2] = (byte)blue;
                }

                JpegEncodeKernels.YCbCrScalar(rgb, scalar[0], scalar[1], scalar[2], 0);
                Assert.Equal(Width, JpegEncodeKernels.YCbCrAvx2(rgb, vector[0], vector[1], vector[2], lumaOnly: false));
                JpegEncodeKernels.Luma(rgb, luma);
                if (!scalar[0].AsSpan().SequenceEqual(vector[0]) || !scalar[1].AsSpan().SequenceEqual(vector[1])
                    || !scalar[2].AsSpan().SequenceEqual(vector[2]) || !scalar[0].AsSpan().SequenceEqual(luma))
                {
                    Assert.Fail($"The vector conversion differs from the scalar one at red {red}, green {green}.");
                }
            }
        }
    }

    public static TheoryData<int> Widths() => [1, 15, 16, 17, 31, 33, 47, 48, 63, 100, 4000];

    [Theory]
    [MemberData(nameof(Widths))]
    public void ColorConversionAndLumaVectorEqualScalarOnRandomAndWorstCaseRows(int width)
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 is not available.");
        byte[] rgb = new byte[width * 3];
        byte[][] scalar = [new byte[width], new byte[width], new byte[width]];
        byte[][] vector = [new byte[width], new byte[width], new byte[width]];
        byte[] luma = new byte[width];
        Random random = new(width);
        foreach ((string name, Func<int, int> value) in WorstRows(random))
        {
            for (int trial = 0; trial < (name == "random" ? 200 : 1); trial++)
            {
                for (int i = 0; i < rgb.Length; i++)
                {
                    rgb[i] = (byte)value(i);
                }

                JpegEncodeKernels.YCbCrScalar(rgb, scalar[0], scalar[1], scalar[2], 0);
                JpegEncodeKernels.YCbCr(rgb, vector[0], vector[1], vector[2]);
                JpegEncodeKernels.Luma(rgb, luma);
                for (int c = 0; c < 3; c++)
                {
                    Assert.True(scalar[c].AsSpan().SequenceEqual(vector[c]), $"{name}, width {width}: component {c} differs.");
                }

                Assert.True(scalar[0].AsSpan().SequenceEqual(luma), $"{name}, width {width}: luma alone differs.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void DownsamplingVectorEqualsScalarOnRandomAndWorstCaseRows(int outputWidth)
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 is not available.");
        int width = 2 * outputWidth;
        byte[] row0 = new byte[width], row1 = new byte[width];
        byte[] expected = new byte[outputWidth], actual = new byte[outputWidth];
        Random random = new(outputWidth + 1);
        foreach ((string name, Func<int, int> value) in WorstRows(random))
        {
            for (int trial = 0; trial < (name == "random" ? 200 : 1); trial++)
            {
                for (int i = 0; i < width; i++)
                {
                    row0[i] = (byte)value(i);
                    row1[i] = (byte)value(i + width + 1);
                }

                foreach (bool twoRows in (bool[])[true, false])
                {
                    JpegEncodeKernels.DownsampleScalar(row0, row1, twoRows, expected, 0);
                    JpegEncodeKernels.Downsample(row0, row1, twoRows, actual);
                    Assert.True(expected.AsSpan().SequenceEqual(actual), $"{name}, {outputWidth} outputs, {(twoRows ? "2×2" : "2×1")}: the averages differ.");
                }
            }
        }
    }

    private static void Check(byte[] samples, int stride, int[] divisors, ushort[] reciprocals, string what)
    {
        short[] scalar = new short[64];
        short[] vector = new short[64];
        JpegFdct.Forward(samples, stride, divisors, scalar);
        JpegFdct.ForwardAvx2(samples, stride, reciprocals, vector);
        Assert.True(scalar.AsSpan().SequenceEqual(vector), $"{what}: the vector FDCT differs from the scalar one.");
    }

    private static void Fill(byte[] samples, int stride, Func<int, int, int> sample)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                samples[(y * stride) + x] = (byte)sample(x, y);
            }
        }
    }

    /// <summary>Blocks at the extremes of the transform's range: flat, checkerboards, stripes, spikes and steps.</summary>
    private static IEnumerable<(string, Func<int, int, int>)> WorstBlocks()
    {
        yield return ("all 0", (_, _) => 0);
        yield return ("all 255", (_, _) => 255);
        yield return ("all 128", (_, _) => 128);
        foreach (int size in (int[])[1, 2, 4])
        {
            yield return ($"checkerboard {size}", (x, y) => ((x / size) + (y / size)) % 2 == 0 ? 255 : 0);
            yield return ($"inverse checkerboard {size}", (x, y) => ((x / size) + (y / size)) % 2 == 0 ? 0 : 255);
            yield return ($"columns {size}", (x, _) => x / size % 2 == 0 ? 255 : 0);
            yield return ($"rows {size}", (_, y) => y / size % 2 == 0 ? 255 : 0);
        }

        yield return ("spike 255 on 0", (x, y) => x == 3 && y == 4 ? 255 : 0);
        yield return ("spike 0 on 255", (x, y) => x == 0 && y == 0 ? 0 : 255);
        yield return ("diagonal", (x, y) => x >= y ? 255 : 0);
        yield return ("ramp", (x, y) => ((x + y) * 255) / 14);
    }

    /// <summary>Byte sequences for rows: constant, alternating at byte and pixel steps, single channels, and random.</summary>
    private static IEnumerable<(string, Func<int, int>)> WorstRows(Random random)
    {
        yield return ("all 0", _ => 0);
        yield return ("all 255", _ => 255);
        yield return ("bytes 0, 255", i => i % 2 == 0 ? 0 : 255);
        yield return ("bytes 255, 0", i => i % 2 == 0 ? 255 : 0);
        yield return ("pixels black, white", i => i / 3 % 2 == 0 ? 0 : 255);
        yield return ("red only", i => i % 3 == 0 ? 255 : 0);
        yield return ("green only", i => i % 3 == 1 ? 255 : 0);
        yield return ("blue only", i => i % 3 == 2 ? 255 : 0);
        yield return ("blue and red", i => i % 3 == 1 ? 0 : 255);
        yield return ("random", _ => random.Next(256));
    }

    private static int[] Divisors(ReadOnlySpan<byte> example, int quality)
    {
        int factor = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        int[] divisors = new int[64];
        for (int i = 0; i < 64; i++)
        {
            divisors[i] = Math.Clamp(((example[i] * factor) + 50) / 100, 1, 255) * 8;
        }

        return divisors;
    }
}
