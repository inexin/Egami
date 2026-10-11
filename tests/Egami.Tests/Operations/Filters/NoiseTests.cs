using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 7: AddNoise's statistics and determinism, and Despeckle's properties.</summary>
public class NoiseTests
{
    [Theory]
    [InlineData(NoiseKind.Gaussian, 1.0)]
    [InlineData(NoiseKind.Uniform, 1.0)]
    [InlineData(NoiseKind.Impulse, 1.0)]
    [InlineData(NoiseKind.Laplacian, 1.0)]
    [InlineData(NoiseKind.Multiplicative, 1.0)]
    [InlineData(NoiseKind.Poisson, 1.0)]
    [InlineData(NoiseKind.Gaussian, 0.3)]
    [InlineData(NoiseKind.Poisson, 2.5)]
    public void StatisticsMatchTheFormulas(NoiseKind kind, double amount)
    {
        // Gate: mean and variance within 1% of the formula over 10⁷ draws (the mean relative to the signal, 0.5).
        const double V = 0.5;
        const int Count = 10_000_000;
        NoiseOperation noise = new(kind, (float)amount, monochrome: false, seed: 9);
        double sum = 0, squares = 0;
        for (int i = 0; i < Count; i++)
        {
            double x = noise.Noise(V, NoiseOperation.Mix((ulong)i));
            sum += x;
            squares += x * x;
        }

        double mean = sum / Count, variance = (squares / Count) - (mean * mean);
        double a = amount;
        (double expectedMean, double expectedVariance) = kind switch
        {
            NoiseKind.Gaussian => (V, (a / 64 * a / 64 * V / 65536) + (5 * a / 64 * (5 * a / 64))),
            NoiseKind.Uniform => (V, a / 64 * (a / 64) / 12),
            NoiseKind.Impulse => ((V * (1 - (0.1 * a))) + (0.05 * a), (V * V * (1 - (0.1 * a))) + (0.05 * a) - Math.Pow((V * (1 - (0.1 * a))) + (0.05 * a), 2)),
            NoiseKind.Laplacian => (V, 2 * (5 * a / 128) * (5 * a / 128)),
            NoiseKind.Multiplicative => (V, V * V * (a / 4) * (a / 4)),
            _ => (V, V / (12.5 * a)),
        };

        Assert.InRange(mean, expectedMean - (0.01 * V), expectedMean + (0.01 * V));
        Assert.InRange(variance, expectedVariance * 0.99, expectedVariance * 1.01);
    }

    [Fact]
    public void SameSeedSamePixelsAtAnyThreadCountAndInScopes()
    {
        SameOnOneThread("egret", o => o.AddNoise(NoiseKind.Gaussian, 1, seed: 5).AddNoise(NoiseKind.Poisson, 0.5f, monochrome: true));
        SameInsideArea("duck", new Rect(30, 20, 100, 80), o => o.AddNoise(NoiseKind.Laplacian, 2, seed: 77));
        using Raster<Rgba8> source = Load("duck");
        using Raster<Rgba8> one = source.Derive(o => o.AddNoise(NoiseKind.Uniform, 4, seed: 1), Ct);
        using Raster<Rgba8> two = source.Derive(o => o.AddNoise(NoiseKind.Uniform, 4, seed: 2), Ct);
        Assert.True(MaxDifference(one, two) > 0);
    }

    [Fact]
    public void AlphaIsKeptAndMonochromeHasNoColor()
    {
        using Raster<Rgba8> gray = Raster.Create(64, 64, new Rgba8(128, 128, 128, 77));
        using Raster<Rgba8> result = gray.Derive(o => o.AddNoise(NoiseKind.Gaussian, 2, monochrome: true), Ct);
        bool changed = false;
        for (int y = 0; y < 64; y++)
        {
            foreach (Rgba8 p in result.Row(y))
            {
                Assert.Equal(77, p.A);
                Assert.True(p.R == p.G && p.G == p.B);
                changed |= p.R != 128;
            }
        }

        Assert.True(changed);
    }

    [Fact]
    public void DespeckleRemovesASpeckAndKeepsAnEdge()
    {
        using Raster<Rgba8> image = Raster.Create(30, 30, new Rgba8(60, 60, 60, 255));
        for (int y = 0; y < 30; y++)
        {
            image.Row(y)[20..].Fill(new Rgba8(200, 200, 200, 255));
        }

        image.Row(10)[8] = new Rgba8(70, 70, 70, 255);
        using Raster<Rgba8> result = image.Derive(o => o.Despeckle(), Ct);
        // The speck sinks most of the way back; flat areas away from the speck and the edge do not move.
        Assert.InRange(result.Row(10)[8].R, 60, 63);
        Assert.Equal(new Rgba8(60, 60, 60, 255), result.Row(20)[12]);
        Assert.Equal(new Rgba8(200, 200, 200, 255), result.Row(15)[25]);
    }

    [Fact]
    public void DespeckleIsTheSameOnOneThreadAndInScopes()
    {
        SameOnOneThread("egret", o => o.Despeckle());
        SameInsideArea("duck", new Rect(30, 20, 100, 80), o => o.Despeckle());
    }
}
