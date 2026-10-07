using Egami.Pixels;

namespace Egami.Tests.Pixels;

public class SrgbTests
{
    [Fact]
    public void EncodeTableRoundTripsEveryEightBitValue()
    {
        for (int v = 0; v < 256; v++)
        {
            Assert.Equal(v, Srgb.FromLinearTo8(Srgb.LinearFrom8[v]));
        }
    }

    [Fact]
    public void EncodeTableMatchesTheExactCurveForAllOutputs()
    {
        // Each 8-bit output v covers linear values between the decision points at encoded (v - 0.5) / 255 and (v + 0.5) / 255.
        // Sampling just inside both ends of every interval checks that the interpolated table picks v across its whole range.
        for (int v = 0; v < 256; v++)
        {
            double low = Srgb.ToLinear(Math.Max(0, v - 0.5) / 255.0);
            double high = Srgb.ToLinear(Math.Min(255, v + 0.5) / 255.0);
            double margin = (high - low) * 0.01;
            Assert.Equal(v, Srgb.FromLinearTo8((float)(low + margin)));
            Assert.Equal(v, Srgb.FromLinearTo8((float)(high - margin)));
        }
    }

    [Fact]
    public void EncodeClampsOutOfRangeInput()
    {
        Assert.Equal(0, Srgb.FromLinearTo8(-1f));
        Assert.Equal(0, Srgb.FromLinearTo8(float.NaN));
        Assert.Equal(255, Srgb.FromLinearTo8(2f));
        Assert.Equal(255, Srgb.FromLinearTo8(float.PositiveInfinity));
    }

    [Fact]
    public void ExactCurveMatchesKnownValues()
    {
        // IEC 61966-2-1: encoded 0.5 is linear 0.21404; the linear segment ends at linear 0.0031308.
        Assert.Equal(0.214041, Srgb.ToLinear(0.5), 6);
        Assert.Equal(0.5, Srgb.FromLinear(0.214041140), 6);
        Assert.Equal(0.0031308 * 12.92, Srgb.FromLinear(0.0031308), 12);
    }
}
