using System.Numerics;

using Egami.Operations;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Tests.Operations.Tone;

/// <summary>Phase 2.5.3: the filter pass, fusion, scopes, composition and the stage A effects.</summary>
public class ColorFilterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Action<RasterOps>[] Steps =
    [
        o => o.Level(0.05f, 0.9f, 1.2f),
        o => o.Gamma(1.4f),
        o => o.SigmoidalContrast(4, 0.45f),
        o => o.Tint(new Rgba8(255, 200, 150, 255), 0.4f),
        o => o.Solarize(0.8f),
    ];

    [Fact]
    public void FusedCurvesEqualTheStepsOneByOne()
    {
        foreach (Func<Raster> make in new Func<Raster>[] { () => Noise8(97, 41), () => Noise8(97, 41).As<Rgba16>() })
        {
            using Raster fused = make();
            using Raster stepped = make();
            fused.Apply(o =>
            {
                foreach (Action<RasterOps> step in Steps)
                {
                    step(o);
                }
            }, Ct);
            foreach (Action<RasterOps> step in Steps)
            {
                stepped.Apply(step, Ct);
            }

            using Raster<Rgba16> a = fused.As<Rgba16>();
            using Raster<Rgba16> b = stepped.As<Rgba16>();
            AssertEqual(a, b, fused.GetType().Name);
        }
    }

    [Fact]
    public void FusedChainsWithMatricesStayWithinOneLevel()
    {
        using Raster<Rgba8> source = Noise8(64, 48);
        static void Chain(RasterOps o) => o.Saturation(1.3f).Level(0.1f, 0.9f).HueRotate(30).Gamma(1.2f).Brightness(1.05f);
        using Raster<Rgba8> fused = source.Derive(Chain, Ct);
        using Raster<Rgba8> stepped = source.Derive(o => o.Saturation(1.3f), Ct);
        stepped.Apply(o => o.Level(0.1f, 0.9f), Ct);
        stepped.Apply(o => o.HueRotate(30), Ct);
        stepped.Apply(o => o.Gamma(1.2f), Ct);
        stepped.Apply(o => o.Brightness(1.05f), Ct);
        Assert.True(MaxDifference(fused, stepped) <= 1, $"{MaxDifference(fused, stepped)} levels");
    }

    [Fact]
    public void MatrixStepsAsFiltersGiveIdenticalPixels()
    {
        using Raster<Rgba8> source = Noise8(70, 30);
        using Raster<Rgba8> matrices = source.Derive(o => o.Brightness(1.1f).Saturation(1.4f).Contrast(0.9f), Ct);
        using Raster<Rgba8> filters = source.Derive(o => o.ApplyFilter(ColorFilter.Brightness(1.1f).Then(ColorFilter.Saturation(1.4f)).Then(ColorFilter.Contrast(0.9f))), Ct);
        AssertEqual(matrices, filters, "matrix filters");
    }

    [Fact]
    public void OnChannelsKeepsTheFilterInTheChosenChannels()
    {
        using Raster<Rgba8> source = Noise8(50, 20);
        using Raster<Rgba8> red = source.Derive(o => o.OnChannels(ColorChannels.Red, c => c.Level(0.2f, 0.8f).HueRotate(40)), Ct);
        using Raster<Rgba8> full = source.Derive(o => o.Level(0.2f, 0.8f).HueRotate(40), Ct);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba8 s = source.Row(y)[x], r = red.Row(y)[x], f = full.Row(y)[x];
                Assert.Equal((s.G, s.B, s.A), (r.G, r.B, r.A));
                Assert.True(Math.Abs(f.R - r.R) <= 1, $"({x}, {y}): {r.R}, whole chain {f.R}");
            }
        }
    }

    [Fact]
    public void WithinChangesOnlyTheRectangle()
    {
        using Raster<Rgba8> source = Noise8(40, 30);
        Rect area = new(5, 6, 20, 10);
        using Raster<Rgba8> scoped = source.Derive(o => o.Within(area, w => w.Posterize(3).Tint(Rgba8.Red)), Ct);
        using Raster<Rgba8> full = source.Derive(o => o.Posterize(3).Tint(Rgba8.Red), Ct);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba8 expected = area.Contains(new Point(x, y)) ? full.Row(y)[x] : source.Row(y)[x];
                Assert.Equal(expected, scoped.Row(y)[x]);
            }
        }
    }

    [Fact]
    public void LerpAndThenEqualRunningTheParts()
    {
        Vector4[] colors = [.. Enumerable.Range(0, 200).Select(i => new Vector4((i * 37 % 256) / 255f, (i * 91 % 256) / 255f, (i * 13 % 256) / 255f, 1))];
        ColorFilter a = ColorFilter.Sepia(), b = ColorFilter.Gamma(2).Then(ColorFilter.Invert());
        RgbaF32[] mixed = [.. colors.Select(c => new RgbaF32(c.X, c.Y, c.Z, c.W))];
        RgbaF32[] first = [.. mixed], second = [.. mixed], chained = [.. mixed];
        ColorFilter.Lerp(a, b, 0.3f).Apply<RgbaF32>(mixed);
        a.Apply<RgbaF32>(first);
        b.Apply<RgbaF32>(second);
        a.Then(b).Apply<RgbaF32>(chained);
        RgbaF32[] stepped = [.. colors.Select(c => new RgbaF32(c.X, c.Y, c.Z, c.W))];
        a.Apply<RgbaF32>(stepped);
        b.Apply<RgbaF32>(stepped);
        for (int i = 0; i < colors.Length; i++)
        {
            Assert.Equal(first[i].R + ((second[i].R - first[i].R) * 0.3f), mixed[i].R, 4);
            Assert.Equal(first[i].B + ((second[i].B - first[i].B) * 0.3f), mixed[i].B, 4);
            Assert.Equal(stepped[i].G, chained[i].G, 3);
        }
    }

    [Fact]
    public void ColorBlindnessMatchesThePapersMatrixInLinearLight()
    {
        // Machado, Oliveira and Fernandes (2009), deuteranopia at severity 1, and halfway between 0.6 and 0.7 for protanomaly.
        double[] deutan = [0.367322, 0.860646, -0.227968, 0.280085, 0.672501, 0.047413, -0.011820, 0.042940, 0.968881];
        double[] protan065 = [(0.385450 + 0.319627) / 2, (0.769005 + 0.849633) / 2, (-0.154455 - 0.169261) / 2, (0.100526 + 0.106241) / 2, (0.829802 + 0.815969) / 2, (0.069673 + 0.077790) / 2, (-0.007442 - 0.007025) / 2, (-0.022190 - 0.028051) / 2, (1.029632 + 1.035076) / 2];
        foreach ((ColorFilter filter, double[] m) in new[] { (ColorFilter.SimulateColorBlindness(ColorVisionDeficiency.Deuteranopia), deutan), (ColorFilter.SimulateColorBlindness(ColorVisionDeficiency.Protanopia, 0.65f), protan065) })
        {
            Rgba8[] pixels = [.. Enumerable.Range(0, 4096).Select(i => new Rgba8((byte)(i * 7), (byte)(i * 13 >> 2), (byte)(i * 29 >> 3), 255))];
            Rgba8[] source = [.. pixels];
            filter.Apply<Rgba8>(pixels);
            for (int i = 0; i < pixels.Length; i++)
            {
                double r = Srgb.ToLinear(source[i].R / 255.0), g = Srgb.ToLinear(source[i].G / 255.0), b = Srgb.ToLinear(source[i].B / 255.0);
                for (int c = 0; c < 3; c++)
                {
                    double linear = Math.Clamp((m[c * 3] * r) + (m[(c * 3) + 1] * g) + (m[(c * 3) + 2] * b), 0, 1);
                    int expected = (int)((Srgb.FromLinear(linear) * 255) + 0.5);
                    int actual = c == 0 ? pixels[i].R : c == 1 ? pixels[i].G : pixels[i].B;
                    Assert.True(Math.Abs(expected - actual) <= 1, $"{source[i]} channel {c}: {actual}, paper {expected}");
                }
            }
        }
    }

    [Fact]
    public void ThresholdModesPickTheTwoColors()
    {
        Rgba8 low = new(10, 20, 30, 255), high = new(200, 210, 220, 255);
        Rgba8[] pixels = [new(200, 200, 200, 128), new(30, 30, 30, 255), new(255, 0, 0, 255), new(120, 120, 120, 255)];
        Rgba8[] luminance = [.. pixels], saturation = [.. pixels], channels = [.. pixels];
        ColorFilter.Threshold(0.5f, ThresholdMode.Luminance, low, high).Apply<Rgba8>(luminance);
        ColorFilter.Threshold(0.5f, ThresholdMode.Saturation, low, high).Apply<Rgba8>(saturation);
        ColorFilter.Threshold(0.5f, ThresholdMode.Channels).Apply<Rgba8>(channels);
        Assert.Equal([high with { A = 128 }, low, low, low], luminance);
        Assert.Equal([low with { A = 128 }, low, high, low], saturation);
        Assert.Equal([new(255, 255, 255, 128), new(0, 0, 0, 255), new(255, 0, 0, 255), new(0, 0, 0, 255)], channels);
    }

    [Fact]
    public void EffectsKeepAlphaAndBlackAndWhiteWhereTheyShould()
    {
        Rgba8[] pixels = [new(0, 0, 0, 77), new(255, 255, 255, 99)];
        ColorFilter.Tint(new Rgba8(255, 0, 0, 255), 1).Apply<Rgba8>(pixels);
        Assert.Equal([new Rgba8(0, 0, 0, 77), new Rgba8(255, 255, 255, 99)], pixels);
        Rgba8[] duo = [new(0, 0, 0, 77), new(255, 255, 255, 99)];
        ColorFilter.Duotone(new Rgba8(27, 42, 73, 255), new Rgba8(242, 193, 78, 255)).Apply<Rgba8>(duo);
        Assert.Equal([new Rgba8(27, 42, 73, 77), new Rgba8(242, 193, 78, 99)], duo);
        Rgba8[] gray = [new(90, 140, 30, 255)];
        ColorFilter.HighContrast(grayscale: true).Apply<Rgba8>(gray);
        Assert.True(gray[0].R == gray[0].G && gray[0].G == gray[0].B);
        Rgba8[] inverted = [new(0, 0, 0, 255)];
        ColorFilter.HighContrast(invert: ContrastInvert.Lightness).Apply<Rgba8>(inverted);
        Assert.Equal(new Rgba8(255, 255, 255, 255), inverted[0]);
    }

    [Fact]
    public void DitheredPosterizeKeepsTheLevelsAndTheMean()
    {
        using Raster<Rgba8> ramp = Raster.Create<Rgba8>(256, 64);
        for (int y = 0; y < 64; y++)
        {
            Span<Rgba8> row = ramp.Row(y);
            for (int x = 0; x < 256; x++)
            {
                row[x] = new Rgba8((byte)x, (byte)x, (byte)x, 255);
            }
        }

        foreach (DitherMode mode in new[] { DitherMode.Ordered, DitherMode.FloydSteinberg })
        {
            using Raster<Rgba8> result = ramp.Derive(o => o.Posterize(3, mode), Ct);
            double sum = 0;
            for (int y = 0; y < 64; y++)
            {
                foreach (Rgba8 p in result.Row(y))
                {
                    Assert.Contains(p.R, new byte[] { 0, 128, 255 });
                    sum += p.R;
                }
            }

            Assert.InRange(sum / (256 * 64), 125, 130);
        }
    }

    [Fact]
    public void ArgumentsAreCheckedWhenTheStepIsAdded()
    {
        using Raster<Rgba8> raster = Noise8(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Level(0.5f, 0.5f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Gamma(0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Posterize(1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.SimulateColorBlindness(ColorVisionDeficiency.Tritanopia, 2), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.HighContrast(contrast: 1.5f), Ct));
        Assert.Throws<InvalidOperationException>(() => ColorFilter.Opacity(0.5f).ToLut3D());
    }

    internal static Raster<Rgba8> Noise8(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new((width * 31) + height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        }

        return raster;
    }

    internal static int MaxDifference(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        int worst = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgba8> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < ra.Length; x++)
            {
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(ra[x].R - rb[x].R), Math.Abs(ra[x].G - rb[x].G)), Math.Max(Math.Abs(ra[x].B - rb[x].B), Math.Abs(ra[x].A - rb[x].A))));
            }
        }

        return worst;
    }

    private static void AssertEqual<T>(Raster<T> expected, Raster<T> actual, string label)
        where T : unmanaged, IPixelType<T>
    {
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"{label}: row {y} differs");
        }
    }
}
