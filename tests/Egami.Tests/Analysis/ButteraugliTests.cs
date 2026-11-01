using System.Text.Json;

using Egami.Analysis;
using Egami.Pixels;

namespace Egami.Tests.Analysis;

/// <summary>
/// The Butteraugli distance of <see cref="CompareOptions.Butteraugli"/> against libjxl 0.12.0's butteraugli_main, from
/// tools/make-butteraugli-references.py: the distance and the 3-norm within 0.1 % (the plan's gate is 1 %).
/// </summary>
public class ButteraugliTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "analysis");

    private static readonly Lazy<JsonElement> Json = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference", "analysis", "butteraugli.json"))).RootElement);

    public static TheoryData<string> Pairs() => [.. Json.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(Pairs))]
    public void MatchesButteraugliMain(string pair)
    {
        string[] names = pair.Split('~');
        using Raster a = Raster.Load(Path.Combine(Images, names[0]));
        using Raster b = Raster.Load(Path.Combine(Images, names[1]));
        CompareResult result = Raster.Compare(a, b, new CompareOptions { Butteraugli = true });
        JsonElement expected = Json.Value.GetProperty(pair);
        Near(result.Butteraugli, expected.GetProperty("distance").GetDouble(), pair);
        Near(result.Butteraugli3Norm, expected.GetProperty("norm3").GetDouble(), pair + " 3-norm");
    }

    [Fact]
    public void IsNaNUnlessAsked()
    {
        using Raster<Rgb8> a = Raster.Create<Rgb8>(16, 16);
        CompareResult result = Raster.Compare(a, a);
        Assert.True(double.IsNaN(result.Butteraugli));
        Assert.True(double.IsNaN(result.Butteraugli3Norm));
    }

    [Fact]
    public void GrowsWithTheDifference()
    {
        using Raster a = Raster.Load(Path.Combine(Images, "duck.png"));
        using Raster small = a.Derive(ops => ops.Brightness(1.02f), TestContext.Current.CancellationToken);
        using Raster large = a.Derive(ops => ops.Brightness(1.2f), TestContext.Current.CancellationToken);
        CompareOptions options = new() { Butteraugli = true };
        double near = Raster.Compare(a, small, options).Butteraugli;
        double far = Raster.Compare(a, large, options).Butteraugli;
        Assert.InRange(near, 0.01, far);
    }

    [Fact]
    public void ComparesTheRegion()
    {
        using Raster a = Raster.Load(Path.Combine(Images, "duck.png"));
        using Raster b = Raster.Load(Path.Combine(Images, "duck-q75.png"));
        Rect region = new(8, 8, 40, 32);
        using Raster ca = a.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken);
        using Raster cb = b.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken);
        double whole = Raster.Compare(ca, cb, new CompareOptions { Butteraugli = true }).Butteraugli;
        double part = Raster.Compare(a, b, new CompareOptions { Butteraugli = true, Region = region }).Butteraugli;
        Assert.Equal(whole, part);
    }

    private static void Near(double actual, double expected, string what)
    {
        double tolerance = Math.Max(Math.Abs(expected) * 1e-3, 1e-6);
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{what}: {actual:R}, butteraugli_main {expected:R}");
    }
}
