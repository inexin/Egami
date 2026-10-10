using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>The fast 16-bit luma must equal the four-power reference for every input, as Gray16 has computed it since 1.0.</summary>
[Collection(ExhaustiveGroup.Name)]
public class Luma16Tests
{
    [Fact]
    public void EveryEightBitColorMatchesTheReference()
    {
        int failures = 0;
        string? first = null;
        Parallel.For(0, 256, new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken }, r =>
        {
            for (int g = 0; g < 256; g++)
            {
                for (int b = 0; b < 256; b++)
                {
                    ushort fast = Luma16.Of8((byte)r, (byte)g, (byte)b);
                    ushort reference = Reference(r / 255f, g / 255f, b / 255f);
                    if (fast != reference && Interlocked.Increment(ref failures) == 1)
                    {
                        first = $"({r}, {g}, {b}): {fast}, reference {reference}";
                    }
                }
            }
        });
        Assert.True(failures == 0, $"{failures} colors differ; first {first}");
    }

    [Fact]
    public void FloatColorsMatchTheReference()
    {
        int failures = 0;
        string? first = null;
        Parallel.For(0, 64, new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken }, block =>
        {
            Random random = new(block);
            for (int i = 0; i < 100_000; i++)
            {
                // Uniform values, and values that land on the encoding thresholds' neighbourhood through 16-bit grays.
                float r = (float)random.NextDouble(), g = (float)random.NextDouble(), b = (float)random.NextDouble();
                if ((i & 3) == 0)
                {
                    float level = random.Next(65536) / 65535f;
                    (r, g, b) = (level, Nudge(level, random), Nudge(level, random));
                }

                ushort fast = Luma16.OfScaled(r, g, b);
                ushort reference = Reference(r, g, b);
                if (fast != reference && Interlocked.Increment(ref failures) == 1)
                {
                    first = $"({r:R}, {g:R}, {b:R}): {fast}, reference {reference}";
                }
            }
        });
        Assert.True(failures == 0, $"{failures} colors differ; first {first}");

        static float Nudge(float v, Random random) => Math.Clamp(v + ((float)(random.NextDouble() - 0.5) * 1e-5f), 0f, 1f);
    }

    [Fact]
    public void GraysAndEdgesKeepTheirValues()
    {
        Assert.Equal(0, Luma16.OfScaled(0f, 0f, 0f));
        Assert.Equal(65535, Luma16.OfScaled(1f, 1f, 1f));
        Assert.Equal(Reference(1f, 0f, 0f), Luma16.OfScaled(1f, 0f, 0f));
        Assert.Equal(Reference(0f, 0f, 1e-30f), Luma16.OfScaled(0f, 0f, 1e-30f));
        Assert.Equal(32665, Luma16.Of8(255, 0, 0));
    }

    private static ushort Reference(float r, float g, float b)
    {
        if (r == g && g == b)
        {
            return (ushort)((r * 65535f) + 0.5f);
        }

        double linear = (0.2126 * ToLinear(r)) + (0.7152 * ToLinear(g)) + (0.0722 * ToLinear(b));
        double encoded = Math.Clamp(linear, 0.0, 1.0) is double l && l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(Math.Clamp(linear, 0.0, 1.0), 1.0 / 2.4)) - 0.055;
        float f = (float)encoded;
        return (ushort)(((f > 0f ? MathF.Min(f, 1f) : 0f) * 65535f) + 0.5f);

        static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
