using System.Runtime.Intrinsics.X86;

using Egami.Formats.Jpeg;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Step 2.4.10's exactness harness for the encoder's vector kernels: the AVX2 forward DCT with quantization, RGB to YCbCr, luma
/// and chroma downsampling each give exactly the scalar result, on random blocks and rows from fixed seeds and on worst cases
/// (all 0, all 255, checkerboards, stripes, single spikes), and the conversion on every RGB triple. Faster kernels must keep these
/// passing. The Huffman coder that adds two symbols per buffer check gives the bytes of the one that tests after every symbol.
/// </summary>
public class JpegEncodeExactnessTests
{
    public static TheoryData<int> Qualities() => [1, 5, 25, 50, 75, 90, 95, 100];

    public static TheoryData<string> BlockKinds() => ["sparse", "dense", "extreme", "long-runs", "ones", "dc-only"];

    [Theory]
    [MemberData(nameof(BlockKinds))]
    public void HuffmanCodingGivesTheBytesOfTheSymbolAtATimeCoder(string kind)
    {
        // Enough blocks that the coded data crosses the bit writer's 64 KB buffer several times.
        const int Mcus = 6000;
        Random random = new(kind.GetHashCode(StringComparison.Ordinal) & 0xFFFF);
        short[] blocks = new short[Mcus * 6 * 64];
        for (int b = 0; b < Mcus * 6; b++)
        {
            FillBlock(kind, random, blocks.AsSpan(b * 64, 64));
        }

        JpegHuffmanCodes[] dc = [JpegHuffmanCodes.DcLuma, JpegHuffmanCodes.DcChroma];
        JpegHuffmanCodes[] ac = [JpegHuffmanCodes.AcLuma, JpegHuffmanCodes.AcChroma];
        int[] blocksPerMcu = [4, 1, 1];
        int[] tables = [0, 1, 1];
        byte[] Code(Action<JpegBitWriter> code)
        {
            using MemoryStream stream = new();
            using (JpegBitWriter writer = new(stream))
            {
                code(writer);
                writer.Align();
                writer.Flush();
            }

            return stream.ToArray();
        }

        void PerBlock(JpegBitWriter writer, bool wide)
        {
            int[] predictors = new int[3];
            int b = 0;
            for (int m = 0; m < Mcus; m++)
            {
                for (int i = 0; i < 3; i++)
                {
                    for (int n = 0; n < blocksPerMcu[i]; n++, b++)
                    {
                        ReadOnlySpan<short> block = blocks.AsSpan(b * 64, 64);
                        if (wide)
                        {
                            JpegBlockCoder.EncodeWide(writer, block, ref predictors[i], dc[tables[i]], ac[tables[i]]);
                        }
                        else
                        {
                            JpegBlockCoder.Encode(writer, block, ref predictors[i], dc[tables[i]], ac[tables[i]]);
                        }
                    }
                }
            }
        }

        byte[] expected = Code(writer => PerBlock(writer, wide: true));
        Assert.Equal(expected, Code(writer => PerBlock(writer, wide: false)));
        Assert.Equal(expected, Code(writer => JpegBlockCoder.EncodeMcus(writer, blocks, Mcus, blocksPerMcu, tables, new int[3], dc, ac)));

        // In stretches, as restarts split a row, with symbols already buffered before each.
        Assert.Equal(expected, Code(writer =>
        {
            int[] predictors = new int[3];
            for (int m = 0; m < Mcus;)
            {
                int stretch = Math.Min(Mcus - m, 1 + random.Next(40));
                JpegBlockCoder.EncodeMcus(writer, blocks.AsSpan(m * 6 * 64, stretch * 6 * 64), stretch, blocksPerMcu, tables, predictors, dc, ac);
                m += stretch;
            }
        }));
    }

    /// <summary>Quantized coefficients within the 8-bit baseline range: AC to ±1023, DC so that differences reach ±2047.</summary>
    private static void FillBlock(string kind, Random random, Span<short> block)
    {
        block.Clear();
        switch (kind)
        {
            case "sparse":
                block[0] = (short)random.Next(-1024, 1024);
                for (int n = random.Next(8); n > 0; n--)
                {
                    block[random.Next(1, 64)] = (short)(random.Next(1, 40) * (random.Next(2) * 2 - 1));
                }

                break;
            case "dense":
                for (int i = 0; i < 64; i++)
                {
                    block[i] = (short)random.Next(-1023, 1024);
                }

                break;
            case "extreme":
                // DC alternating between the ends, so the differences are the largest; AC at the limits.
                block[0] = (short)(random.Next(2) == 0 ? -1024 : 1023);
                for (int i = 1; i < 64; i++)
                {
                    block[i] = (short)(random.Next(3) switch { 0 => 1023, 1 => -1023, _ => 0 });
                }

                break;
            case "long-runs":
                // Runs over 15 zeros, which need ZRL codes, ending at the last coefficient or before it.
                block[0] = (short)random.Next(-50, 50);
                block[JpegTables.ZigZag[random.Next(17, 64)]] = (short)random.Next(-5, 6);
                if (random.Next(2) == 0)
                {
                    block[JpegTables.ZigZag[63]] = 1;
                }

                break;
            case "ones":
                // Values whose bits are all ones, so the coded data is full of 0xFF bytes to stuff.
                block[0] = (short)(random.Next(2) == 0 ? 1023 : 511);
                for (int i = 1; i < 64; i++)
                {
                    block[i] = (short)(random.Next(4) == 0 ? 0 : (1 << random.Next(1, 11)) - 1);
                }

                break;
            default:
                block[0] = (short)random.Next(-1024, 1024);
                break;
        }
    }

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
                    // Every third block is only 0 and 255, the corners where the transform's values are largest.
                    int level = random.Next(256), spread = trial % 2 == 0 ? 256 : 1 + random.Next(32);
                    Fill(samples, stride, trial % 3 == 2
                        ? (_, _) => random.Next(2) * 255
                        : (_, _) => Math.Clamp(level + random.Next(spread) - (spread / 2), 0, 255));
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
        byte[] rgba = new byte[Width * 4];
        byte[][] fromRgba = [new byte[Width], new byte[Width], new byte[Width]];
        byte[] lumaFromRgba = new byte[Width];
        Random random = new(7);
        for (int red = 0; red < 256; red++)
        {
            for (int green = 0; green < 256; green++)
            {
                for (int blue = 0; blue < Width; blue++)
                {
                    rgb[3 * blue] = rgba[4 * blue] = (byte)red;
                    rgb[(3 * blue) + 1] = rgba[(4 * blue) + 1] = (byte)green;
                    rgb[(3 * blue) + 2] = rgba[(4 * blue) + 2] = (byte)blue;
                    rgba[(4 * blue) + 3] = (byte)random.Next(256);
                }

                JpegEncodeKernels.YCbCrScalar(rgb, scalar[0], scalar[1], scalar[2], 0);
                Assert.Equal(Width, JpegEncodeKernels.YCbCrAvx2(rgb, vector[0], vector[1], vector[2], lumaOnly: false));
                JpegEncodeKernels.Luma(rgb, luma);
                Assert.Equal(Width, JpegEncodeKernels.YCbCrFromRgbaAvx2(rgba, fromRgba[0], fromRgba[1], fromRgba[2], lumaOnly: false));
                JpegEncodeKernels.LumaFromRgba(rgba, lumaFromRgba);
                if (!scalar[0].AsSpan().SequenceEqual(vector[0]) || !scalar[1].AsSpan().SequenceEqual(vector[1])
                    || !scalar[2].AsSpan().SequenceEqual(vector[2]) || !scalar[0].AsSpan().SequenceEqual(luma))
                {
                    Assert.Fail($"The vector conversion differs from the scalar one at red {red}, green {green}.");
                }

                if (!scalar[0].AsSpan().SequenceEqual(fromRgba[0]) || !scalar[1].AsSpan().SequenceEqual(fromRgba[1])
                    || !scalar[2].AsSpan().SequenceEqual(fromRgba[2]) || !scalar[0].AsSpan().SequenceEqual(lumaFromRgba))
                {
                    Assert.Fail($"The conversion from RGBA differs from the scalar one at red {red}, green {green}.");
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

                // The same pixels as RGBA, with the alpha from the next byte of the pattern; the vector tail stops at the row end.
                byte[] rgba = new byte[width * 4];
                for (int x = 0; x < width; x++)
                {
                    rgb.AsSpan(3 * x, 3).CopyTo(rgba.AsSpan(4 * x));
                    rgba[(4 * x) + 3] = (byte)value(rgb.Length + x);
                }

                JpegEncodeKernels.YCbCrFromRgba(rgba, vector[0], vector[1], vector[2]);
                JpegEncodeKernels.LumaFromRgba(rgba, luma);
                for (int c = 0; c < 3; c++)
                {
                    Assert.True(scalar[c].AsSpan().SequenceEqual(vector[c]), $"{name}, width {width}, from RGBA: component {c} differs.");
                }

                Assert.True(scalar[0].AsSpan().SequenceEqual(luma), $"{name}, width {width}, from RGBA: luma alone differs.");
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

        // The two-block kernel, with the block on the left and its inverse on the right, then the other way round.
        int wide = Math.Max(stride, 16);
        byte[] pair = new byte[(7 * wide) + 16];
        byte[] inverse = new byte[(7 * stride) + 8];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                inverse[(y * stride) + x] = (byte)(255 - samples[(y * stride) + x]);
            }
        }

        short[] other = new short[64];
        JpegFdct.Forward(inverse, stride, divisors, other);
        foreach (bool swap in (bool[])[false, true])
        {
            for (int y = 0; y < 8; y++)
            {
                samples.AsSpan(y * stride, 8).CopyTo(pair.AsSpan((y * wide) + (swap ? 8 : 0)));
                inverse.AsSpan(y * stride, 8).CopyTo(pair.AsSpan((y * wide) + (swap ? 0 : 8)));
            }

            short[] left = new short[64], right = new short[64];
            JpegFdct.ForwardAvx2(pair, wide, reciprocals, left, right);
            Assert.True((swap ? right : left).AsSpan().SequenceEqual(scalar), $"{what}: the two-block FDCT differs from the scalar one.");
            Assert.True((swap ? left : right).AsSpan().SequenceEqual(other), $"{what}, inverted: the two-block FDCT differs from the scalar one.");
        }
    }

    /// <summary>
    /// Bounds every 16-bit value the AVX2 forward DCT keeps, for any 8-bit block. Pass 1 takes eight samples level-shifted to
    /// −128..127; pass 2 takes one pass-1 output from each of eight rows, which vary independently. Every sum and difference
    /// adds distinct inputs, so interval arithmetic gives the exact bounds.
    /// </summary>
    [Fact]
    public void ForwardDctSixteenBitValuesFitForEightBitSamples()
    {
        (long Low, long High)[] pass1 = DctPassBounds([.. Enumerable.Repeat((-128L, 127L), 8)], first: true);
        for (int k = 0; k < 8; k++)
        {
            DctPassBounds([.. Enumerable.Repeat(pass1[k], 8)], first: false);
        }
    }

    /// <summary>One pass of the 16-bit kernel on input bounds: asserts each kept value fits and returns the output bounds.</summary>
    private static (long Low, long High)[] DctPassBounds((long Low, long High)[] d, bool first)
    {
        static (long, long) Add((long Low, long High) a, (long Low, long High) b) => (a.Low + b.Low, a.High + b.High);
        static (long, long) Sub((long Low, long High) a, (long Low, long High) b) => (a.Low - b.High, a.High - b.Low);
        static (long Low, long High) Fit((long Low, long High) v, string name)
        {
            Assert.True(v.Low >= short.MinValue && v.High <= short.MaxValue, $"{name} spans {v.Low}..{v.High}, beyond 16 bits.");
            return v;
        }

        // The kernel's multiply-add factors per input (the scalar products multiplied out) and the descale.
        int shift = first ? 11 : 15;
        (long, long) Rotate(string name, (long Low, long High)[] inputs, int[] factors)
        {
            long low = 0, high = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                Fit((factors[i], factors[i]), $"{name} factor {i}");
                low += Math.Min(factors[i] * inputs[i].Low, factors[i] * inputs[i].High);
                high += Math.Max(factors[i] * inputs[i].Low, factors[i] * inputs[i].High);
            }

            long round = 1L << (shift - 1);
            Assert.True(low + round >= int.MinValue && high + round <= int.MaxValue, $"{name} overflows 32 bits.");
            return Fit(((low + round) >> shift, (high + round) >> shift), name);
        }

        (long, long) tmp0 = Fit(Add(d[0], d[7]), "tmp0");
        (long, long) tmp7 = Fit(Sub(d[0], d[7]), "tmp7");
        (long, long) tmp1 = Fit(Add(d[1], d[6]), "tmp1");
        (long, long) tmp6 = Fit(Sub(d[1], d[6]), "tmp6");
        (long, long) tmp2 = Fit(Add(d[2], d[5]), "tmp2");
        (long, long) tmp5 = Fit(Sub(d[2], d[5]), "tmp5");
        (long, long) tmp3 = Fit(Add(d[3], d[4]), "tmp3");
        (long, long) tmp4 = Fit(Sub(d[3], d[4]), "tmp4");
        (long, long) tmp10 = Fit(Add(tmp0, tmp3), "tmp10");
        (long, long) tmp13 = Fit(Sub(tmp0, tmp3), "tmp13");
        (long, long) tmp11 = Fit(Add(tmp1, tmp2), "tmp11");
        (long, long) tmp12 = Fit(Sub(tmp1, tmp2), "tmp12");
        (long, long) even0 = Fit(Add(tmp10, tmp11), "even0");
        (long, long) even4 = Fit(Sub(tmp10, tmp11), "even4");
        (long Low, long High)[] output = new (long, long)[8];
        foreach ((int k, (long Low, long High) even) in (ReadOnlySpan<(int, (long, long))>)[(0, even0), (4, even4)])
        {
            // Pass 1 scales the even outputs up by 2 bits; pass 2 rounds them and shifts the 2 bits out.
            (long Low, long High) kept = Fit(first ? (even.Low << 2, even.High << 2) : Add(even, (2, 2)), $"even {k}");
            output[k] = first ? kept : (kept.Low >> 2, kept.High >> 2);
        }

        output[2] = Rotate("o2", [tmp13, tmp12], [10703, 4433]);
        output[6] = Rotate("o6", [tmp13, tmp12], [4433, -10704]);
        (long, long)[] odd = [tmp4, tmp5, tmp6, tmp7];
        output[7] = Rotate("o7", odd, [-11363, 9633, -6436, 2260]);
        output[5] = Rotate("o5", odd, [9633, 2261, -11362, 6437]);
        output[3] = Rotate("o3", odd, [-6436, -11362, -2259, 9633]);
        output[1] = Rotate("o1", odd, [2260, 6437, 9633, 11363]);
        return output;
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
