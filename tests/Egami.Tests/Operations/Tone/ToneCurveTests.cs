using Egami.Operations;
using Egami.Operations.Tone;
using Egami.Pixels;

namespace Egami.Tests.Operations.Tone;

/// <summary>Phase 2.5.3: the curve math against double-precision formulas, and ToneCurve's shapes.</summary>
public class ToneCurveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Steps() => [.. Formulas.Keys];

    /// <summary>Each tone step with the formula it must equal, written here from ImageMagick's documentation.</summary>
    private static readonly Dictionary<string, (ColorFilter Filter, Func<double, double> Formula)> Formulas = new()
    {
        ["level"] = (ColorFilter.Level(0.1f, 0.85f, 1.3f), v => Math.Pow(Math.Clamp((v - D(0.1f)) / (D(0.85f) - D(0.1f)), 0, 1), 1 / D(1.3f))),
        ["level-output"] = (ColorFilter.Level(outputBlack: 0.2f, outputWhite: 0.7f), v => D(0.2f) + ((D(0.7f) - D(0.2f)) * v)),
        ["level-inverted"] = (ColorFilter.Level(0.9f, 0.1f), v => Math.Clamp((v - D(0.9f)) / (D(0.1f) - D(0.9f)), 0, 1)),
        ["gamma"] = (ColorFilter.Gamma(2.2f), v => Math.Pow(v, 1 / D(2.2f))),
        ["sigmoidal"] = (ColorFilter.SigmoidalContrast(5, 0.4f), v => Sigmoid(v, 5, D(0.4f))),
        ["sigmoidal-decrease"] = (ColorFilter.SigmoidalContrast(5, 0.4f, increase: false), v => InverseSigmoid(v, 5, D(0.4f))),
        ["posterize"] = (ColorFilter.Posterize(5), v => Math.Floor((v * 4) + 0.5) / 4),
        ["solarize"] = (ColorFilter.Solarize(0.6f), v => v > D(0.6f) ? 1 - v : v),
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public void SixteenBitRampsAreTheFormulaRounded(string name)
    {
        (ColorFilter filter, Func<double, double> formula) = Formulas[name];
        using Raster<Rgba16> ramp = Raster.Create<Rgba16>(256, 256);
        for (int y = 0; y < 256; y++)
        {
            Span<Rgba16> row = ramp.Row(y);
            for (int x = 0; x < 256; x++)
            {
                ushort v = (ushort)((y * 256) + x);
                row[x] = new Rgba16(v, v, v, 65535);
            }
        }

        ramp.Apply(ops => ops.ApplyFilter(filter), Ct);
        for (int y = 0; y < 256; y++)
        {
            ReadOnlySpan<Rgba16> row = ramp.Row(y);
            for (int x = 0; x < 256; x++)
            {
                int v = (y * 256) + x;
                int expected = (int)((Math.Clamp(formula(v / 65535.0), 0, 1) * 65535) + 0.5);
                Assert.True(row[x].R == expected && row[x].R == row[x].B, $"{name}({v}): {row[x].R}, formula {expected}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void CurvesAreTheFormulaToOneMillionth(string name)
    {
        (ColorFilter filter, Func<double, double> formula) = Formulas[name];
        CurveStage curve = Assert.IsType<CurveStage>(Assert.Single(filter.Stages));
        for (int v = 0; v < 65536; v++)
        {
            double x = v / 65535.0;
            Assert.True(Math.Abs(curve.MapChannel(0, x) - Math.Clamp(formula(x), 0, 1)) <= 1e-6, $"{name}({x})");
        }
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void EightBitTablesAreExact(string name)
    {
        (ColorFilter filter, Func<double, double> formula) = Formulas[name];
        Rgba8[] pixels = new Rgba8[256];
        for (int v = 0; v < 256; v++)
        {
            pixels[v] = new Rgba8((byte)v, (byte)v, (byte)v, 200);
        }

        filter.Apply<Rgba8>(pixels);
        for (int v = 0; v < 256; v++)
        {
            Assert.Equal((int)((Math.Clamp(formula(v / 255.0), 0, 1) * 255) + 0.5), pixels[v].G);
            Assert.Equal(200, pixels[v].A);
        }
    }

    [Fact]
    public void MonotoneCurvesNeverOvershootOrDip()
    {
        Random random = new(253);
        for (int trial = 0; trial < 200; trial++)
        {
            int n = random.Next(2, 9);
            float[] xs = [.. Enumerable.Range(0, n).Select(_ => (float)random.NextDouble()).Distinct().Order()];
            float[] ys = [.. Enumerable.Range(0, xs.Length).Select(_ => (float)random.NextDouble()).Order()];
            (float, float)[] points = [.. xs.Zip(ys)];
            if (points.Length < 2)
            {
                continue;
            }

            ToneCurve curve = ToneCurve.FromPoints(points);
            float previous = -1;
            for (int i = 0; i <= 1000; i++)
            {
                float y = curve.Evaluate(i / 1000f);
                Assert.True(y >= previous - 1e-6f, $"trial {trial}: falls at {i / 1000f}");
                Assert.True(y >= ys[0] - 1e-6f && y <= ys[^1] + 1e-6f, $"trial {trial}: leaves the points' range at {i / 1000f}");
                previous = y;
            }

            foreach ((float x, float y) in points)
            {
                Assert.Equal(y, curve.Evaluate(x), 5);
            }
        }
    }

    [Fact]
    public void TablesAndFunctionsInterpolateStraight()
    {
        ToneCurve table = ToneCurve.FromTable([0, 1, 0.5f]);
        Assert.Equal(0.5f, table.Evaluate(0.25f), 6);
        Assert.Equal(0.75f, table.Evaluate(0.75f), 6);
        Assert.Equal(0f, table.Evaluate(-3));
        Assert.Equal(0.5f, table.Evaluate(7));

        ToneCurve squared = ToneCurve.FromFunction(x => x * x, 65536);
        Assert.Equal(0.25f, squared.Evaluate(0.5f), 6);
        Assert.Equal(0.0625f, squared.Then(squared).Evaluate(0.5f), 5);
        Assert.Same(squared, ToneCurve.Identity.Then(squared));
    }

    [Fact]
    public void BadPointsThrow()
    {
        Assert.Throws<ArgumentException>(() => ToneCurve.FromPoints([(0f, 0f)]));
        Assert.Throws<ArgumentException>(() => ToneCurve.FromPoints([(0.5f, 0f), (0.5f, 1f)]));
        Assert.Throws<ArgumentException>(() => ToneCurve.FromPoints([(0f, 0f), (1.2f, 1f)]));
        Assert.Throws<ArgumentException>(() => ToneCurve.FromTable([0.5f, 2f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToneCurve.FromFunction(x => x, 1));
    }

    /// <summary>A float parameter as the step receives it.</summary>
    private static double D(float value) => value;

    private static double Logistic(double a, double b, double x) => 1 / (1 + Math.Exp(a * (b - x)));

    private static double Sigmoid(double v, double a, double b) => (Logistic(a, b, v) - Logistic(a, b, 0)) / (Logistic(a, b, 1) - Logistic(a, b, 0));

    private static double InverseSigmoid(double v, double a, double b)
    {
        double s0 = Logistic(a, b, 0), s1 = Logistic(a, b, 1);
        double argument = Math.Clamp(((s1 - s0) * v) + s0, 1e-12, 1 - 1e-12);
        return b - (Math.Log((1 / argument) - 1) / a);
    }
}
