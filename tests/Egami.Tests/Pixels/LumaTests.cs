using Egami.Pixels;

namespace Egami.Tests.Pixels;

public class LumaTests
{
    [Fact]
    public void CoarseTableNeedsAtMostOneStep()
    {
        Assert.True(Luma.MaxThresholdsPerStep() <= 1);
    }

    [Fact]
    public void GrayColorsKeepTheirValue()
    {
        for (int v = 0; v < 256; v++)
        {
            Assert.Equal(v, Luma.Of8((byte)v, (byte)v, (byte)v));
        }
    }

    [Fact]
    public void MatchesTheExactFormulaAwayFromRoundingBoundaries()
    {
        // Every 8-bit color in steps of 3 per channel (about 620,000 colors), against the exact formula in doubles.
        int checkedColors = 0;
        for (int r = 0; r < 256; r += 3)
        {
            for (int g = 0; g < 256; g += 3)
            {
                for (int b = 0; b < 256; b += 3)
                {
                    double linear = (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));
                    double encoded = (linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055) * 255;

                    // Within float precision of a .5 boundary either neighbor is acceptable.
                    if (Math.Abs(encoded - Math.Floor(encoded) - 0.5) < 1e-4)
                    {
                        continue;
                    }

                    Assert.Equal((int)Math.Floor(encoded + 0.5), Luma.Of8((byte)r, (byte)g, (byte)b));
                    checkedColors++;
                }
            }
        }

        Assert.True(checkedColors > 600_000);

        static double Linear(int v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
    }
}
