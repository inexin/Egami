using Egami.Formats.Psd;
using Egami.Operations;

namespace Egami.Tests.Formats.Psd;

/// <summary>The flattener's own math: Photoshop's special-mode fill, the feather blur and Lab's non-separable modes.</summary>
public class PsdFlattenMathTests
{
    /// <summary>Vivid Light looks at the source first: 0 stays 0 and 1 stays 1 whatever the backdrop; Hard Mix of 1 over 0 is 0.</summary>
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0f, 0.3f, 0f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(1f, 0.7f, 1f)]
    public void VividLightAtPureSources(float cs, float cb, float expected) =>
        Assert.Equal(expected, PsdFlattener.SpecialBlend(BlendMode.VividLight, cb, cs, 1));

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(1f, 0.2f, 1f)]
    [InlineData(0.6f, 0.4f, 1f)]
    [InlineData(0.6f, 0.3f, 0f)]
    public void HardMixThresholds(float cs, float cb, float expected) =>
        Assert.Equal(expected, PsdFlattener.SpecialBlend(BlendMode.HardMix, cb, cs, 1));

    /// <summary>A fill of 0 leaves the backdrop in every special mode; a fill of 1 is the plain formula.</summary>
    [Theory]
    [InlineData(BlendMode.ColorBurn)]
    [InlineData(BlendMode.LinearBurn)]
    [InlineData(BlendMode.ColorDodge)]
    [InlineData(BlendMode.Add)]
    [InlineData(BlendMode.VividLight)]
    [InlineData(BlendMode.LinearLight)]
    [InlineData(BlendMode.HardMix)]
    [InlineData(BlendMode.Difference)]
    public void FillPullsTheSourceToNeutral(BlendMode mode)
    {
        for (float cb = 0.05f; cb < 1; cb += 0.1f)
        {
            for (float cs = 0.05f; cs < 1; cs += 0.15f)
            {
                Assert.Equal(cb, PsdFlattener.SpecialBlend(mode, cb, cs, 0), 1e-5f);
                float full = PsdFlattener.SpecialBlend(mode, cb, cs, 1);
                Assert.InRange(full, 0, 1);
            }
        }

        Assert.Equal(0.3f, PsdFlattener.SpecialBlend(BlendMode.LinearBurn, 0.9f, 0.4f, 1), 1e-5f);
        Assert.Equal(0.6f, PsdFlattener.SpecialBlend(BlendMode.LinearBurn, 0.9f, 0.4f, 0.5f), 1e-5f);
    }

    /// <summary>
    /// The blur keeps a flat plane flat and the sum of a spot, whose spread has the asked variance: exactly for a small σ, within
    /// the step of the three boxes' whole widths otherwise.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(9.5)]
    public void FeatherBlurIsGaussian(double sigma)
    {
        const int size = 101;
        float[] flat = new float[size * size];
        Array.Fill(flat, 0.25f);
        PsdFlattener.Blur(flat, size, size, sigma);
        Assert.All(flat, v => Assert.Equal(0.25f, v, 1e-5f));

        float[] spot = new float[size * size];
        spot[(50 * size) + 50] = 1;
        PsdFlattener.Blur(spot, size, size, sigma);
        double sum = 0, variance = 0;
        for (int x = 0; x < size; x++)
        {
            double column = 0;
            for (int y = 0; y < size; y++)
            {
                column += spot[(y * size) + x];
            }

            sum += column;
            variance += column * (x - 50) * (x - 50);
        }

        Assert.Equal(1, sum, 1e-4);
        Assert.Equal(sigma * sigma, variance, sigma < 2 ? 0.01 : sigma * sigma * 0.1);
    }

    /// <summary>A σ far wider than the plane gives every value the plane's mean, quickly and without overflow.</summary>
    [Theory]
    [InlineData(1000.0)]
    [InlineData(1e9)]
    [InlineData(1e300)]
    public void HugeSigmaGivesTheMean(double sigma)
    {
        float[] plane = [.. Enumerable.Range(0, 7 * 5).Select(i => (float)(i % 11) / 10)];
        float mean = plane.Average();
        PsdFlattener.Blur(plane, 7, 5, sigma);
        Assert.All(plane, v => Assert.Equal(mean, v, 1e-5f));

        float[] same = [0.5f, 0.25f];
        PsdFlattener.Blur(same, 2, 1, double.NaN);
        PsdFlattener.Blur(same, 2, 1, double.PositiveInfinity);
        Assert.Equal([0.5f, 0.25f], same);
    }

    /// <summary>Lab's non-separable modes work on lightness, chroma and hue of the encoded values.</summary>
    [Fact]
    public void LabModes()
    {
        const float center = 128f / 255;
        float[] backdrop = [0.4f, center + 0.2f, center];
        float[] source = [0.8f, center, center - 0.1f];

        Assert.Equal([0.8f, center + 0.2f, center], Blend(BlendMode.Luminosity));
        Assert.Equal([0.4f, center, center - 0.1f], Blend(BlendMode.Color));
        float[] hue = Blend(BlendMode.Hue);
        Assert.Equal(0.4f, hue[0]);
        Assert.Equal(center, hue[1], 1e-6f);
        Assert.Equal(center - 0.2f, hue[2], 1e-6f);
        float[] saturation = Blend(BlendMode.Saturation);
        Assert.Equal(center + 0.1f, saturation[1], 1e-6f);
        Assert.Equal(center, saturation[2], 1e-6f);
        Assert.Equal(backdrop, Blend(BlendMode.DarkerColor));
        Assert.Equal(source, Blend(BlendMode.LighterColor));

        float[] Blend(BlendMode mode)
        {
            float[] result = (float[])backdrop.Clone();
            PsdFlattener.LabBlend(mode, result, source, center);
            return result;
        }
    }
}
