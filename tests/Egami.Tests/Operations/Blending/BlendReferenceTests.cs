using Egami.Operations;
using Egami.Operations.Compose;

namespace Egami.Tests.Operations.Blending;

/// <summary>The W3C scalar reference against values computed by hand from the specification's formulas.</summary>
public class BlendReferenceTests
{
    // Straight colors Cs = (0.8, 0.4, 0.2) and Cb = (0.5, 0.25, 1.0).
    private static readonly (double, double, double, double) OpaqueSource = (0.8, 0.4, 0.2, 1);
    private static readonly (double, double, double, double) OpaqueDestination = (0.5, 0.25, 1.0, 1);

    // The same colors premultiplied by αs = 0.4 and αb = 0.75.
    private static readonly (double, double, double, double) HalfSource = (0.32, 0.16, 0.08, 0.4);
    private static readonly (double, double, double, double) HalfDestination = (0.375, 0.1875, 0.75, 0.75);

    [Fact]
    public void ThereAre31ModesWithSourceOverFirst()
    {
        Assert.Equal(31, Enum.GetValues<BlendMode>().Length);
        Assert.Equal(0, (int)BlendMode.SourceOver);
        Assert.Equal(30, (int)BlendMode.Luminosity);
        Assert.Equal(Enumerable.Range(0, 31), Enum.GetValues<BlendMode>().Select(m => (int)m));
    }

    /// <summary>Opaque layers: the result is B(Cb, Cs) itself.</summary>
    [Theory]
    [InlineData(BlendMode.Multiply, 0.4, 0.1, 0.2)]
    [InlineData(BlendMode.Screen, 0.9, 0.55, 1.0)]
    [InlineData(BlendMode.Overlay, 0.8, 0.2, 1.0)]
    [InlineData(BlendMode.Darken, 0.5, 0.25, 0.2)]
    [InlineData(BlendMode.Lighten, 0.8, 0.4, 1.0)]
    [InlineData(BlendMode.ColorDodge, 1.0, 0.25 / 0.6, 1.0)]
    [InlineData(BlendMode.ColorBurn, 0.375, 0.0, 1.0)]
    [InlineData(BlendMode.HardLight, 0.8, 0.2, 0.4)]
    [InlineData(BlendMode.SoftLight, 0.6242640687119285, 0.2125, 1.0)]
    [InlineData(BlendMode.Difference, 0.3, 0.15, 0.8)]
    [InlineData(BlendMode.Exclusion, 0.5, 0.45, 0.8)]
    [InlineData(BlendMode.Add, 1.0, 0.65, 1.0)]
    [InlineData(BlendMode.Subtract, 0.0, 0.0, 0.8)]
    [InlineData(BlendMode.Hue, 0.785, 0.285, 0.035)]
    [InlineData(BlendMode.Saturation, 0.4815, 0.2815, 0.8815)]
    [InlineData(BlendMode.Color, 0.7095, 0.3095, 0.1095)]
    [InlineData(BlendMode.Luminosity, 0.5763713080168776, 0.3645569620253165, 1.0)]
    [InlineData(BlendMode.SourceOver, 0.8, 0.4, 0.2)]
    [InlineData(BlendMode.DestinationOver, 0.5, 0.25, 1.0)]
    public void OpaqueBlendsMatchTheFormulas(BlendMode mode, double r, double g, double b)
    {
        AssertClose((r, g, b, 1), BlendReference.Blend(mode, OpaqueSource, OpaqueDestination));
    }

    /// <summary>Partly transparent layers: each Porter–Duff operator, Plus, Modulate and two blend modes.</summary>
    [Theory]
    [InlineData(BlendMode.Clear, 0, 0, 0, 0)]
    [InlineData(BlendMode.Source, 0.32, 0.16, 0.08, 0.4)]
    [InlineData(BlendMode.Destination, 0.375, 0.1875, 0.75, 0.75)]
    [InlineData(BlendMode.SourceOver, 0.545, 0.2725, 0.53, 0.85)]
    [InlineData(BlendMode.DestinationOver, 0.455, 0.2275, 0.77, 0.85)]
    [InlineData(BlendMode.SourceIn, 0.24, 0.12, 0.06, 0.3)]
    [InlineData(BlendMode.DestinationIn, 0.15, 0.075, 0.3, 0.3)]
    [InlineData(BlendMode.SourceOut, 0.08, 0.04, 0.02, 0.1)]
    [InlineData(BlendMode.DestinationOut, 0.225, 0.1125, 0.45, 0.45)]
    [InlineData(BlendMode.SourceAtop, 0.465, 0.2325, 0.51, 0.75)]
    [InlineData(BlendMode.DestinationAtop, 0.23, 0.115, 0.32, 0.4)]
    [InlineData(BlendMode.Xor, 0.305, 0.1525, 0.47, 0.55)]
    [InlineData(BlendMode.Plus, 0.695, 0.3475, 0.83, 1.0)]
    [InlineData(BlendMode.Modulate, 0.12, 0.03, 0.06, 0.3)]
    [InlineData(BlendMode.Multiply, 0.425, 0.1825, 0.53, 0.85)]

    // Hue: B = (0.785, 0.285, 0.035) weighted by αs·αb = 0.3, plus s(1 − αb) + d(1 − αs).
    [InlineData(BlendMode.Hue, 0.08 + 0.225 + 0.2355, 0.04 + 0.1125 + 0.0855, 0.02 + 0.45 + 0.0105, 0.85)]
    public void TransparentBlendsMatchTheFormulas(BlendMode mode, double r, double g, double b, double a)
    {
        AssertClose((r, g, b, a), BlendReference.Blend(mode, HalfSource, HalfDestination));
    }

    /// <summary>Opacity scales the source before the mode: half of Multiply over an opaque destination is a half mix with B.</summary>
    [Fact]
    public void OpacityScalesTheSourceFirst()
    {
        AssertClose((0.45, 0.175, 0.6, 1), BlendReference.Blend(BlendMode.Multiply, OpaqueSource, OpaqueDestination, 0.5));
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            var result = BlendReference.Blend(mode, HalfSource, HalfDestination, 0);
            if (mode is BlendMode.Clear or BlendMode.Source or BlendMode.SourceIn or BlendMode.DestinationIn or BlendMode.SourceOut
                or BlendMode.DestinationAtop or BlendMode.Modulate)
            {
                continue;
            }

            AssertClose(HalfDestination, result);
        }
    }

    /// <summary>The 8-bit reference rounds the exact result to nearest.</summary>
    [Fact]
    public void TheByteReferenceRoundsToNearest()
    {
        // Multiply on opaque 200 and 100: 200 · 100 / 255 = 78.43, so 78.
        Assert.Equal((78, 78, 78, 255), BlendReference.Blend8(BlendMode.Multiply, (200, 200, 200, 255), (100, 100, 100, 255)));

        // Screen: 200 + 100 − 78.43 = 221.57, so 222.
        Assert.Equal((222, 222, 222, 255), BlendReference.Blend8(BlendMode.Screen, (200, 200, 200, 255), (100, 100, 100, 255)));

        // Opacity 128 scales 200 to round(200 · 128 / 255) = 100 first.
        Assert.Equal((100, 100, 100, 100), BlendReference.Blend8(BlendMode.Source, (200, 200, 200, 200), (1, 2, 3, 4), 128));
    }

    private static void AssertClose((double R, double G, double B, double A) expected, (double R, double G, double B, double A) actual)
    {
        Assert.Equal(expected.R, actual.R, 1e-12);
        Assert.Equal(expected.G, actual.G, 1e-12);
        Assert.Equal(expected.B, actual.B, 1e-12);
        Assert.Equal(expected.A, actual.A, 1e-12);
    }
}
