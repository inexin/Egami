using Egami.Colors;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 5: compiled transforms against the reference pipeline, SIMD against scalar, and the cache.</summary>
public class CompiledTransformTests(ITestOutputHelper output)
{
    private static readonly RenderingIntent[] Intents = [RenderingIntent.Perceptual, RenderingIntent.RelativeColorimetric, RenderingIntent.AbsoluteColorimetric];

    public static TheoryData<string, string> Pairs() => new()
    {
        { "sRGB", "Display P3" },
        { "Display P3", "sRGB" },
        { "Rec. 2020", "ProPhoto RGB" },
        { "Adobe RGB (1998)", "Gray sRGB" },
        { "Gray sRGB", "Display P3" },
        { "Gray linear", "Gray sRGB" },
        { "compact/sRGB-v2-micro.icc", "sRGB" },
        { "synthetic/matrix-para-v4.icc", "Rec. 2020" },
        { "synthetic/rgb-lut16-v2.icc", "sRGB" },
        { "sRGB", "synthetic/rgb-mab-v4.icc" },
        { "synthetic/cmyk-lut16-v2.icc", "sRGB" },
        { "sRGB", "synthetic/cmyk-mab-v4.icc" },
        { "synthetic/cmyk-mab-v4.icc", "synthetic/cmyk-lut16-v2.icc" },
        { "synthetic/gray-lut16-v2.icc", "Display P3" },
        { "Rec. 2100 PQ", "Rec. 2020" },
        { "Rec. 2100 HLG", "Rec. 2100 PQ" },
    };

    /// <summary>
    /// Matrix forms match the pipeline within 1.5e-4 (5e-7 for curves given as formulas; ICC table curves differ by their 16-bit
    /// rounding), and in 8 bits within one level for fewer than 1% of values. Grids match on average within half a level; where
    /// a table has a kink, as a CMYK separation's black generation does, a grid point's neighbours can miss it by several levels,
    /// as with Little CMS's own 33 and 17 point grids.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void CompiledMatchesThePipeline(string fromName, string toName)
    {
        ColorProfile from = Profile(fromName), to = Profile(toName);
        foreach (RenderingIntent intent in Intents)
        {
            ColorPipeline pipeline = ColorPipeline.Create(from, to, intent, intent == RenderingIntent.Perceptual);
            CompiledTransform compiled = CompiledTransform.Compile(from, to, intent, intent == RenderingIntent.Perceptual);
            int ci = compiled.InputChannels, co = compiled.OutputChannels;
            Assert.Equal((pipeline.InputChannels, pipeline.OutputChannels), (ci, co));
            float[] inputs = Inputs(ci, out int count);
            float[] floats = new float[count * co];
            compiled.Apply(inputs, floats, count);
            byte[] bytesIn = [.. inputs.Select(v => (byte)Math.Round(v * 255))];
            byte[] bytes = new byte[count * co];
            compiled.Apply(bytesIn, new ByteLayout(ci, false), bytes, new ByteLayout(co, false), count);

            double worst = 0, sum = 0;
            int off = 0, worst8 = 0;
            double[] reference = new double[co];
            bool matrix = compiled is MatrixTransform;
            for (int i = 0; i < count; i++)
            {
                pipeline.Evaluate([.. inputs.AsSpan(i * ci, ci).ToArray().Select(v => (double)v)], reference);
                for (int c = 0; c < co; c++)
                {
                    // HDR targets are unbounded; compare relative to the value there.
                    double scale = Math.Max(1, Math.Abs(reference[c]));
                    double error = Math.Abs(floats[(i * co) + c] - reference[c]) / scale;
                    worst = Math.Max(worst, error);
                    sum += error;
                }

                pipeline.Evaluate([.. bytesIn.AsSpan(i * ci, ci).ToArray().Select(v => v / 255.0)], reference);
                for (int c = 0; c < co; c++)
                {
                    int expected = (int)Math.Round(Math.Clamp(reference[c], 0, 1) * 255);
                    int diff = Math.Abs(bytes[(i * co) + c] - expected);
                    worst8 = Math.Max(worst8, diff);
                    off += diff > 0 ? 1 : 0;
                }
            }

            output.WriteLine($"{fromName} -> {toName}, {intent}: {compiled.GetType().Name}, float worst {worst:0.#######} mean {sum / (count * co):0.#######}, 8-bit worst {worst8}, off in {off} of {count * co}");
            if (matrix)
            {
                Assert.True(worst <= 1.5e-4, $"{intent}: float worst {worst}");
                Assert.True(worst8 <= 1 && off <= count * co / 100, $"{intent}: 8-bit worst {worst8}, {off} of {count * co} off");
            }
            else
            {
                Assert.True(sum / (count * co) <= 0.5 / 255, $"{intent}: float mean {sum / (count * co)}");
            }
        }
    }

    /// <summary>At its nodes a grid is the pipeline; 8-bit input goes through the same interpolation as float input.</summary>
    [Theory]
    [InlineData("synthetic/cmyk-mab-v4.icc", "synthetic/cmyk-lut16-v2.icc", 17)]
    [InlineData("sRGB", "synthetic/cmyk-mab-v4.icc", 33)]
    [InlineData("synthetic/gray-lut16-v2.icc", "sRGB", 4096)]
    public void GridsHoldThePipelineAtTheirNodes(string fromName, string toName, int points)
    {
        ColorProfile from = Profile(fromName), to = Profile(toName);
        ColorPipeline pipeline = ColorPipeline.Create(from, to, RenderingIntent.Perceptual, false);
        GridTransform grid = Assert.IsType<GridTransform>(CompiledTransform.Compile(from, to, RenderingIntent.Perceptual, false));
        int ci = grid.InputChannels, co = grid.OutputChannels;
        Random random = new(points);
        float[] node = new float[ci], result = new float[co];
        double[] reference = new double[co];
        for (int i = 0; i < 500; i++)
        {
            for (int c = 0; c < ci; c++)
            {
                node[c] = random.Next(points) / (float)(points - 1);
            }

            grid.Apply(node, result, 1);
            pipeline.Evaluate([.. node.Select(v => (double)v)], reference);
            for (int c = 0; c < co; c++)
            {
                Assert.True(Math.Abs(result[c] - reference[c]) < 1e-5, $"node [{string.Join(", ", node)}] channel {c}: {result[c]} against {reference[c]}");
            }

            byte[] bytes = [.. Enumerable.Range(0, ci).Select(_ => (byte)random.Next(256))];
            byte[] out8 = new byte[co];
            grid.Apply(bytes, new ByteLayout(ci, false), out8, new ByteLayout(co, false), 1);
            grid.Apply([.. bytes.Select(b => b / 255f)], result, 1);
            Assert.Equal([.. result.Select(v => (byte)(Math.Clamp(v, 0, 1) * 255 + 0.5f))], out8);
        }
    }

    /// <summary>Long rows run the SIMD paths, single colors the scalar ones; they give the same bits.</summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void RowsAndSingleColorsAgree(string fromName, string toName)
    {
        CompiledTransform compiled = CompiledTransform.Compile(Profile(fromName), Profile(toName), RenderingIntent.Perceptual, false);
        int ci = compiled.InputChannels, co = compiled.OutputChannels;
        float[] inputs = Inputs(ci, out int count);
        float[] row = new float[count * co], one = new float[co];
        compiled.Apply(inputs, row, count);
        byte[] bytesIn = [.. inputs.Select(v => (byte)Math.Round(v * 255))];
        byte[] bytes = new byte[count * co], oneByte = new byte[co];
        compiled.Apply(bytesIn, new ByteLayout(ci, false), bytes, new ByteLayout(co, false), count);
        for (int i = 0; i < count; i++)
        {
            compiled.Apply(inputs.AsSpan(i * ci, ci), one, 1);
            Assert.Equal(row.AsSpan(i * co, co).ToArray(), one);
            compiled.Apply(bytesIn.AsSpan(i * ci, ci), new ByteLayout(ci, false), oneByte, new ByteLayout(co, false), 1);
            Assert.Equal(bytes.AsSpan(i * co, co).ToArray(), oneByte);
        }
    }

    [Fact]
    public void LayoutsPlaceChannels()
    {
        CompiledTransform compiled = CompiledTransform.Compile(ColorProfile.DisplayP3, ColorProfile.Srgb, RenderingIntent.Perceptual, false);
        byte[] rgb = [200, 100, 50, 10, 220, 130];
        byte[] expected = new byte[6];
        compiled.Apply(rgb, new ByteLayout(3, false), expected, new ByteLayout(3, false), 2);

        // B, G, R, A in and R, G, B, A out: alpha is left alone.
        byte[] bgra = [50, 100, 200, 77, 130, 220, 10, 88];
        byte[] rgba = new byte[8];
        rgba[3] = 1;
        rgba[7] = 2;
        compiled.Apply(bgra, new ByteLayout(4, true), rgba, new ByteLayout(4, false), 2);
        Assert.Equal([expected[0], expected[1], expected[2], 1, expected[3], expected[4], expected[5], 2], rgba);

        CompiledTransform same = CompiledTransform.Compile(ColorProfile.FromIcc(IccReferences.Bytes("compact/sRGB-v4.icc")), ColorProfile.Srgb, RenderingIntent.Perceptual, true);
        Assert.IsType<IdentityTransform>(same);
        byte[] copy = new byte[6];
        same.Apply(bgra.AsSpan(0, 6), new ByteLayout(3, true), copy, new ByteLayout(3, false), 2);
        Assert.Equal([200, 100, 50, 220, 130, 77], copy);
    }

    [Fact]
    public void TransformsAreCachedPerContext()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = 1 };
        ColorProfile cmyk = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/cmyk-mab-v4.icc"));
        ColorProfile again = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/cmyk-mab-v4.icc"));
        CompiledTransform a = TransformCache.Get(context, cmyk, ColorProfile.Srgb, RenderingIntent.Perceptual, false);
        Assert.Same(a, TransformCache.Get(context, again, ColorProfile.Srgb, RenderingIntent.Perceptual, false));   // same bytes, same transform
        Assert.NotSame(a, TransformCache.Get(context, cmyk, ColorProfile.Srgb, RenderingIntent.RelativeColorimetric, false));
        Assert.Same(
            TransformCache.Get(context, ColorProfile.Create(ColorPrimaries.Rec709, TransferFunction.Gamma(2.2)), ColorProfile.Srgb, RenderingIntent.Perceptual, false),
            TransformCache.Get(context, ColorProfile.Create(ColorPrimaries.Rec709, TransferFunction.Gamma(2.2)), ColorProfile.Srgb, RenderingIntent.Perceptual, false));
        Assert.NotSame(a, TransformCache.Get(EgamiContext.Default with { MaxParallelism = 2 }, cmyk, ColorProfile.Srgb, RenderingIntent.Perceptual, false));
        for (int i = 0; i < TransformCache.Capacity + 5; i++)
        {
            TransformCache.Get(context, ColorProfile.Create(ColorPrimaries.Rec709, TransferFunction.Gamma(1.5 + (i / 100.0))), ColorProfile.Srgb, RenderingIntent.Perceptual, false);
        }

        Assert.Equal(TransformCache.Capacity, TransformCache.Count(context));
    }

    internal static ColorProfile Profile(string name) => name.EndsWith(".icc", StringComparison.Ordinal)
        ? ColorProfile.FromIcc(IccReferences.Bytes(name))
        : new[] { ColorProfile.Srgb, ColorProfile.LinearSrgb, ColorProfile.DisplayP3, ColorProfile.AdobeRgb, ColorProfile.ProPhotoRgb, ColorProfile.Rec2020, ColorProfile.Rec2100Pq, ColorProfile.Rec2100Hlg, ColorProfile.GraySrgb, ColorProfile.GrayLinear }
            .Single(p => p.Name == name);

    // A grid with the corners and pseudo-random points, 0..1.
    private static float[] Inputs(int channels, out int count)
    {
        Random random = new(channels);
        List<float> values = [];
        int steps = channels == 4 ? 5 : channels == 3 ? 9 : 256;
        int grid = (int)Math.Pow(steps, channels);
        for (int i = 0; i < grid; i++)
        {
            for (int c = channels - 1, rest = i; c >= 0; c--, rest /= steps)
            {
                values.Add(rest % steps / (float)(steps - 1));
            }
        }

        for (int i = 0; i < 1000 * channels; i++)
        {
            values.Add((float)random.NextDouble());
        }

        count = values.Count / channels;
        return [.. values];
    }
}
